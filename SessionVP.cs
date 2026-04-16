#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Linq;
using System.Xml.Serialization;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.NinjaScript;
using SharpDX;
using SharpDX.Direct2D1;
#endregion

namespace NinjaTrader.NinjaScript.Indicators.Loci
{
	public class SessionVP : Indicator
	{
		#region Types

		private class SessData
		{
			public Dictionary<int, long> vol = new Dictionary<int, long>();
			public int  firstBar0 = int.MaxValue;
			public int  lastBar0  = -1;
			public long maxVol;
			public int  pocBin;
			public int  vahBin;
			public int  valBin;
			public int  minBin = int.MaxValue;
			public int  maxBin = int.MinValue;
			public long totalVol;
			public bool isETH;
		}

		#endregion

		#region Fields

		private List<SessData>   sessions;
		private SessData         active;
		private SessData         activeETH;   // другий профіль для RTH|ETH режиму
		private SessionIterator  sessIter;
		private DateTime         currentRthStart;
		private DateTime         currentRthEnd;
		private DateTime         currentSessBegin;
		private DateTime         currentSessEnd;
		private bool             prevInRTH;
		private bool             initialized;
		private double           tickSz;
		private TimeZoneInfo     easternTZ;
		private object           syncLock = new object();

		#endregion

		#region OnStateChange

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description          = "Volume Profile на барах сесії";
				Name                 = "Session VP";
				IsOverlay            = true;
				Calculate            = Calculate.OnEachTick;
				DrawOnPricePanel     = true;
				IsAutoScale          = true;
				IsSuspendedWhileInactive = false;
				BarsRequiredToPlot   = 0;
				DisplayInDataBox     = false;
				PaintPriceMarkers    = false;
				MaximumBarsLookBack  = MaximumBarsLookBack.Infinite;
				ArePlotsConfigurable = false;
				ZOrder               = -12000;

				SessionMode          = SVPSessionMode.RTH;
				DisplayMode          = SVPDisplayMode.Standard;
				TicksPerLevel        = 1;
				SmoothPasses         = 4;
				ProfileWidth         = 60;
				ProfileOpacity       = 100;
				ShowPOC              = true;
				ShowVA               = false;
				VA_Percent           = 70;
				VA_Dimming           = 80;
				POC_Color            = System.Windows.Media.Brushes.DarkTurquoise;
				RTH_Color            = MkBrush(105, 105, 105);
				ETH_Color            = MkBrush(75, 75, 95);
			}
			else if (State == State.Configure)
			{
				sessions  = new List<SessData>();
				active    = null;
				activeETH = null;

				bool resetEOD = BarsArray[0].IsResetOnNewTradingDay;
				AddVolumetric("", BarsPeriodType.Minute, 1,
					VolumetricDeltaType.BidAsk, 1, resetEOD);
			}
			else if (State == State.DataLoaded)
			{
				sessIter     = new SessionIterator(BarsArray[1]);
				easternTZ    = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
				tickSz       = Instrument.MasterInstrument.TickSize;
				initialized  = false;
				prevInRTH    = false;
				currentRthStart  = DateTime.MinValue;
				currentRthEnd    = DateTime.MinValue;
				currentSessBegin = DateTime.MinValue;
				currentSessEnd   = DateTime.MinValue;
			}
		}

		#endregion

		#region Session helpers

		private DateTime CalcRthStart(DateTime sessionStart, DateTime sessionEnd)
		{
			return CalcETtoChart("09:30:00", sessionStart, sessionEnd);
		}

		private DateTime CalcRthEnd(DateTime sessionStart, DateTime sessionEnd)
		{
			return CalcETtoChart("16:00:00", sessionStart, sessionEnd);
		}

		private DateTime CalcETtoChart(string etTimeStr, DateTime sessionStart, DateTime sessionEnd)
		{
			DateTime etTime = DateTime.Parse(etTimeStr, CultureInfo.InvariantCulture);
			DateTime baseDate = (etTime.TimeOfDay >= sessionStart.TimeOfDay)
				? sessionStart : sessionEnd;
			DateTime candidate = new DateTime(baseDate.Year, baseDate.Month, baseDate.Day,
				etTime.Hour, etTime.Minute, 0);
			try
			{
				candidate = TimeZoneInfo.ConvertTime(candidate, easternTZ,
					NinjaTrader.Core.Globals.GeneralOptions.TimeZoneInfo);
			}
			catch { }
			return candidate;
		}

		private void StoreProfile(SessData profile)
		{
			if (profile != null && profile.vol.Count > 0)
			{
				FinalizeProfile(profile);
				sessions.Add(profile);
			}
		}

		private SessData NewProfile(bool isETH)
		{
			return new SessData
			{
				firstBar0 = CurrentBars[0],
				lastBar0  = CurrentBars[0],
				isETH     = isETH
			};
		}

		#endregion

		#region OnBarUpdate

		protected override void OnBarUpdate()
		{
			if (BarsInProgress == 0) return;
			if (BarsInProgress != 1 || !IsFirstTickOfBar) return;

			// ═══ Детекція границь сесії ═══
			if (BarsArray[1].IsFirstBarOfSession)
			{
				DateTime barTime = BarsArray[1].GetTime(CurrentBars[1]);
				sessIter.GetTradingDay(barTime);
				currentSessBegin = sessIter.ActualSessionBegin;
				currentSessEnd   = sessIter.ActualSessionEnd;
				currentRthStart  = CalcRthStart(currentSessBegin, currentSessEnd);
				currentRthEnd    = CalcRthEnd(currentSessBegin, currentSessEnd);
				initialized = true;
			}

			if (!initialized || currentRthStart == DateTime.MinValue) return;

			// ═══ Фаза бару ═══
			DateTime t = Times[1][0];
			bool inRTH = (t >= currentRthStart && t < currentRthEnd);

			// ═══ Логіка переходів ═══
			switch (SessionMode)
			{
				case SVPSessionMode.RTH:
					ProcessSingleMode(inRTH, skipWhen: !inRTH);
					break;

				case SVPSessionMode.ETH:
					ProcessETHOnlyMode(inRTH);
					break;

				case SVPSessionMode.RTH_ETH_Split:
					ProcessSplitMode(inRTH);
					break;

				case SVPSessionMode.RTH_ETH:
					ProcessSingleMode(inRTH, skipWhen: false);
					break;
			}

			prevInRTH = inRTH;
		}

		/// <summary>
		/// RTH та RTH+ETH — один активний профіль.
		/// </summary>
		private void ProcessSingleMode(bool inRTH, bool skipWhen)
		{
			if (skipWhen) return;

			// Новий профіль при вході в RTH
			if (inRTH && !prevInRTH)
			{
				lock (syncLock) { StoreProfile(active); active = NewProfile(false); }
			}

			if (active == null)
				lock (syncLock) { active = NewProfile(false); }

			AccumulateVolume(active);
		}

		/// <summary>
		/// ETH — тільки не-RTH бари, новий профіль при виході з RTH.
		/// </summary>
		private void ProcessETHOnlyMode(bool inRTH)
		{
			if (inRTH) return;

			if (!inRTH && prevInRTH)
			{
				lock (syncLock) { StoreProfile(active); active = NewProfile(true); }
			}

			if (active == null)
				lock (syncLock) { active = NewProfile(true); }

			AccumulateVolume(active);
		}

		/// <summary>
		/// RTH | ETH — два активні профілі одночасно.
		/// </summary>
		private void ProcessSplitMode(bool inRTH)
		{
			if (inRTH)
			{
				// Вхід в RTH → фіналізуємо ETH, починаємо новий RTH
				if (!prevInRTH)
				{
					lock (syncLock)
					{
						StoreProfile(activeETH); activeETH = null;
						StoreProfile(active);    active = NewProfile(false);
					}
				}

				if (active == null)
					lock (syncLock) { active = NewProfile(false); }

				AccumulateVolume(active);
			}
			else
			{
				// Вихід з RTH → фіналізуємо RTH, починаємо новий ETH
				if (prevInRTH)
				{
					lock (syncLock)
					{
						StoreProfile(active);    active = null;
						StoreProfile(activeETH); activeETH = NewProfile(true);
					}
				}

				if (activeETH == null)
					lock (syncLock) { activeETH = NewProfile(true); }

				AccumulateVolume(activeETH);
			}
		}

		private void AccumulateVolume(SessData target)
		{
			var barsType = BarsArray[1].BarsType as
				NinjaTrader.NinjaScript.BarsTypes.VolumetricBarsType;
			if (barsType == null) return;

			var volData = barsType.Volumes[CurrentBars[1]];
			double barLow  = Lows[1][0];
			double barHigh = Highs[1][0];
			if (barLow == 0 || barHigh == 0 || barLow > barHigh) return;

			int tpl = Math.Max(1, TicksPerLevel);

			for (double p = barLow; p <= barHigh;
				 p = Instrument.MasterInstrument.RoundToTickSize(p + tickSz))
			{
				double price = Instrument.MasterInstrument.RoundToTickSize(p);
				long askVol, bidVol;
				try
				{
					askVol = volData.GetAskVolumeForPrice(price);
					bidVol = volData.GetBidVolumeForPrice(price);
				}
				catch { continue; }

				long total = askVol + bidVol;
				if (total == 0) continue;

				int tickIdx = (int)Math.Round(price / tickSz);
				int half    = tpl / 2;
				int bin     = (tickIdx + half) / tpl;

				lock (syncLock)
				{
					if (target.vol.ContainsKey(bin))
						target.vol[bin] += total;
					else
						target.vol[bin] = total;

					target.totalVol += total;
					if (bin < target.minBin) target.minBin = bin;
					if (bin > target.maxBin) target.maxBin = bin;
				}
			}

			if (CurrentBars[0] > target.lastBar0)
				target.lastBar0 = CurrentBars[0];
			if (CurrentBars[0] < target.firstBar0)
				target.firstBar0 = CurrentBars[0];
		}

		#endregion

		#region OnRender

		protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
		{
			if (RenderTarget == null || ChartBars == null) return;
			if (ZOrder > -12000) ZOrder = -12000;
			base.OnRender(chartControl, chartScale);
			RenderTarget.AntialiasMode = AntialiasMode.PerPrimitive;

			int fromIdx = ChartBars.FromIndex;
			int toIdx   = ChartBars.ToIndex;

			List<SessData> toRender;
			lock (syncLock)
			{
				toRender = new List<SessData>(sessions);

				// Додаємо активні профілі (snapshot)
				if (active != null && active.vol.Count > 0)
					toRender.Add(SnapshotProfile(active));
				if (activeETH != null && activeETH.vol.Count > 0)
					toRender.Add(SnapshotProfile(activeETH));
			}

			float halfBar = (float)chartControl.BarWidth / 2f + 1f;
			int tpl = Math.Max(1, TicksPerLevel);

			foreach (var sess in toRender)
			{
				if (sess.vol.Count == 0 || sess.lastBar0 < fromIdx || sess.firstBar0 > toIdx)
					continue;

				// Ширину рахуємо від реальних границь сесії (не обрізаних)
				float xLeft  = chartControl.GetXByBarIndex(ChartBars, sess.firstBar0) - halfBar;
				float xRight = chartControl.GetXByBarIndex(ChartBars, sess.lastBar0) + halfBar;
				float sessWidth = xRight - xLeft;
				if (sessWidth < 4) continue;

				float maxBarW = sessWidth * ProfileWidth / 100f;
				if (maxBarW < 2) continue;

				RenderProfile(sess, chartScale, xLeft, maxBarW, tpl);
			}
		}

		private SessData SnapshotProfile(SessData src)
		{
			var snap = new SessData
			{
				firstBar0 = src.firstBar0,
				lastBar0  = src.lastBar0,
				minBin    = src.minBin,
				maxBin    = src.maxBin,
				totalVol  = src.totalVol,
				isETH     = src.isETH,
				vol       = new Dictionary<int, long>(src.vol)
			};
			FinalizeProfile(snap);
			return snap;
		}

		private void RenderProfile(SessData sess, ChartScale chartScale,
			float xLeft, float maxBarW, int tpl)
		{
			if (sess.maxVol == 0) return;

			// Вибір кольору: ETH або RTH
			var profileBrush = sess.isETH ? ETH_Color : RTH_Color;

			float opacity = ProfileOpacity / 100f;
			var mediaColor = ((System.Windows.Media.SolidColorBrush)profileBrush).Color;
			var pocColor   = ((System.Windows.Media.SolidColorBrush)POC_Color).Color;

			var dxProfile = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget,
				new SharpDX.Color4(mediaColor.R / 255f, mediaColor.G / 255f, mediaColor.B / 255f, opacity));
			var dxDimmed  = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget,
				new SharpDX.Color4(mediaColor.R / 255f, mediaColor.G / 255f, mediaColor.B / 255f,
				opacity * (100 - VA_Dimming) / 100f));
			var dxPOC     = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget,
				new SharpDX.Color4(pocColor.R / 255f, pocColor.G / 255f, pocColor.B / 255f, opacity));
			var dxContour = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget,
				new SharpDX.Color4(mediaColor.R / 255f, mediaColor.G / 255f, mediaColor.B / 255f, 1f));

			try
			{
				bool hasVA = ShowVA && sess.vahBin > 0;

				if (DisplayMode == SVPDisplayMode.Standard)
					RenderStandard(sess, chartScale, xLeft, maxBarW, tpl, hasVA,
						dxProfile, dxDimmed, dxPOC);
				else
					RenderContour(sess, chartScale, xLeft, maxBarW, tpl,
						dxContour, dxPOC);
			}
			finally
			{
				dxProfile.Dispose();
				dxDimmed.Dispose();
				dxPOC.Dispose();
				dxContour.Dispose();
			}
		}

		private void RenderStandard(SessData sess, ChartScale chartScale,
			float xLeft, float maxBarW, int tpl, bool hasVA,
			SharpDX.Direct2D1.SolidColorBrush dxProfile,
			SharpDX.Direct2D1.SolidColorBrush dxDimmed,
			SharpDX.Direct2D1.SolidColorBrush dxPOC)
		{
			int half = tpl / 2;
			var pts = BuildCurvePoints(sess, chartScale, xLeft, maxBarW, tpl, half);
			if (pts.Count < 2) return;

			float yBot = chartScale.GetYByValue((sess.minBin * tpl - half) * tickSz);
			float yTop = chartScale.GetYByValue((sess.maxBin * tpl - half + tpl) * tickSz);

			var geometry = new SharpDX.Direct2D1.PathGeometry(RenderTarget.Factory);
			var sink = geometry.Open();
			sink.BeginFigure(new SharpDX.Vector2(xLeft, yBot), FigureBegin.Filled);
			sink.AddLine(pts[0]);
			AddCatmullRomSpline(sink, pts);
			sink.AddLine(new SharpDX.Vector2(xLeft, yTop));
			sink.EndFigure(FigureEnd.Closed);
			sink.Close();

			RenderTarget.FillGeometry(geometry, dxProfile);

			if (ShowPOC && sess.vol.ContainsKey(sess.pocBin))
			{
				double pocMid = (sess.pocBin * tpl - half + tpl * 0.5) * tickSz;
				float pocY = chartScale.GetYByValue(pocMid);
				RenderTarget.DrawLine(
					new SharpDX.Vector2(xLeft, pocY),
					new SharpDX.Vector2(xLeft + maxBarW, pocY),
					dxPOC, 2f);
			}

			geometry.Dispose();
		}

		private void RenderContour(SessData sess, ChartScale chartScale,
			float xLeft, float maxBarW, int tpl,
			SharpDX.Direct2D1.SolidColorBrush dxContour,
			SharpDX.Direct2D1.SolidColorBrush dxPOC)
		{
			int half = tpl / 2;
			var pts = BuildCurvePoints(sess, chartScale, xLeft, maxBarW, tpl, half);
			if (pts.Count < 2) return;

			float yBot = chartScale.GetYByValue((sess.minBin * tpl - half) * tickSz);
			float yTop = chartScale.GetYByValue((sess.maxBin * tpl - half + tpl) * tickSz);

			var geometry = new SharpDX.Direct2D1.PathGeometry(RenderTarget.Factory);
			var sink = geometry.Open();
			sink.BeginFigure(new SharpDX.Vector2(xLeft, yBot), FigureBegin.Hollow);
			sink.AddLine(pts[0]);
			AddCatmullRomSpline(sink, pts);
			sink.AddLine(new SharpDX.Vector2(xLeft, yTop));
			sink.EndFigure(FigureEnd.Closed);
			sink.Close();

			RenderTarget.DrawGeometry(geometry, dxContour, 1.5f);

			if (ShowPOC && sess.vol.ContainsKey(sess.pocBin))
			{
				double pocMid = (sess.pocBin * tpl - half + tpl * 0.5) * tickSz;
				float pocY = chartScale.GetYByValue(pocMid);
				RenderTarget.DrawLine(
					new SharpDX.Vector2(xLeft, pocY),
					new SharpDX.Vector2(xLeft + maxBarW, pocY),
					dxPOC, 2f);
			}

			geometry.Dispose();
		}

		#endregion

		#region Helpers

		private void FinalizeProfile(SessData s)
		{
			if (s.vol.Count == 0) return;

			long max = 0;
			foreach (var kv in s.vol)
			{
				if (kv.Value > max)
				{
					max = kv.Value;
					s.pocBin = kv.Key;
				}
			}
			s.maxVol = max;

			if (!ShowVA || s.totalVol == 0) return;

			long target = (long)(s.totalVol * VA_Percent / 100.0);
			long accum  = s.vol.ContainsKey(s.pocBin) ? s.vol[s.pocBin] : 0;
			int  hi = s.pocBin, lo = s.pocBin;

			while (accum < target)
			{
				long upVol = 0, dnVol = 0;
				int  nextHi = hi + 1, nextLo = lo - 1;

				while (nextHi <= s.maxBin && !s.vol.ContainsKey(nextHi)) nextHi++;
				if (nextHi <= s.maxBin) upVol = s.vol[nextHi];

				while (nextLo >= s.minBin && !s.vol.ContainsKey(nextLo)) nextLo--;
				if (nextLo >= s.minBin) dnVol = s.vol[nextLo];

				if (upVol == 0 && dnVol == 0) break;

				if (upVol >= dnVol && nextHi <= s.maxBin)
				{ hi = nextHi; accum += upVol; }
				else if (nextLo >= s.minBin)
				{ lo = nextLo; accum += dnVol; }
				else break;
			}

			s.vahBin = hi;
			s.valBin = lo;
		}

		private static System.Windows.Media.SolidColorBrush MkBrush(byte r, byte g, byte b)
		{
			var brush = new System.Windows.Media.SolidColorBrush(
				System.Windows.Media.Color.FromRgb(r, g, b));
			brush.Freeze();
			return brush;
		}

		/// <summary>
		/// Будує масив точок для кривої з центрованими бінами.
		/// 2 проходи згладжування (1,2,1)/4 перед побудовою.
		/// </summary>
		private List<SharpDX.Vector2> BuildCurvePoints(SessData sess,
			ChartScale chartScale, float xLeft, float maxBarW, int tpl, int half)
		{
			int count = sess.maxBin - sess.minBin + 1;
			if (count < 2) return new List<SharpDX.Vector2>();

			// ── Сирі об'єми ──
			float[] raw = new float[count];
			for (int i = 0; i < count; i++)
			{
				long v;
				if (sess.vol.TryGetValue(sess.minBin + i, out v))
					raw[i] = v;
			}

			// ── Згладжування: N проходів, ядро (1,2,1)/4 ──
			float[] smooth = raw;
			int passes = Math.Max(0, SmoothPasses);
			for (int pass = 0; pass < passes; pass++)
			{
				float[] tmp = new float[count];
				for (int i = 0; i < count; i++)
				{
					float prev = (i > 0) ? smooth[i - 1] : smooth[i];
					float next = (i < count - 1) ? smooth[i + 1] : smooth[i];
					tmp[i] = (prev + 2f * smooth[i] + next) / 4f;
				}
				smooth = tmp;
			}

			// ── Максимум після згладжування ──
			float maxSmooth = 0;
			for (int i = 0; i < count; i++)
				if (smooth[i] > maxSmooth) maxSmooth = smooth[i];

			if (maxSmooth == 0) return new List<SharpDX.Vector2>();

			// ── Обрізаємо хвости: < 0.5% від макс → 0 ──
			float threshold = maxSmooth * 0.005f;
			for (int i = 0; i < count; i++)
				if (smooth[i] < threshold) smooth[i] = 0;

			if (maxSmooth == 0) return new List<SharpDX.Vector2>();

			// ── Точки з центрованими бінами ──
			var pts = new List<SharpDX.Vector2>(count);
			for (int i = 0; i < count; i++)
			{
				int bin = sess.minBin + i;
				double priceMid = (bin * tpl - half + tpl * 0.5) * tickSz;
				float yMid = chartScale.GetYByValue(priceMid);
				float barW = smooth[i] / maxSmooth * maxBarW;

				pts.Add(new SharpDX.Vector2(xLeft + barW, yMid));
			}

			return pts;
		}

		/// <summary>
		/// Catmull-Rom → cubic Bézier сплайн.
		/// </summary>
		private void AddCatmullRomSpline(GeometrySink sink, List<SharpDX.Vector2> pts)
		{
			for (int i = 0; i < pts.Count - 1; i++)
			{
				var p0 = (i > 0) ? pts[i - 1] : pts[i];
				var p1 = pts[i];
				var p2 = pts[i + 1];
				var p3 = (i < pts.Count - 2) ? pts[i + 2] : pts[i + 1];

				var cp1 = new SharpDX.Vector2(
					p1.X + (p2.X - p0.X) / 6f,
					p1.Y + (p2.Y - p0.Y) / 6f);
				var cp2 = new SharpDX.Vector2(
					p2.X - (p3.X - p1.X) / 6f,
					p2.Y - (p3.Y - p1.Y) / 6f);

				sink.AddBezier(new BezierSegment
				{
					Point1 = cp1,
					Point2 = cp2,
					Point3 = p2
				});
			}
		}

		#endregion

		#region Properties

		[Display(Name = "Режим сесії", GroupName = "1. Налаштування", Order = 0)]
		public SVPSessionMode SessionMode { get; set; }

		[Display(Name = "Відображення", GroupName = "1. Налаштування", Order = 1)]
		public SVPDisplayMode DisplayMode { get; set; }

		[Range(1, 20)]
		[Display(Name = "Стиснення (тіки)", GroupName = "2. Профіль", Order = 0)]
		public int TicksPerLevel { get; set; }

		[Range(0, 10)]
		[Display(Name = "Згладжування", GroupName = "2. Профіль", Order = 1)]
		public int SmoothPasses { get; set; }

		[Range(10, 100)]
		[Display(Name = "Ширина профілю (%)", GroupName = "2. Профіль", Order = 2)]
		public int ProfileWidth { get; set; }

		[Range(0, 100)]
		[Display(Name = "Прозорість профілю", GroupName = "2. Профіль", Order = 3)]
		public int ProfileOpacity { get; set; }

		[Display(Name = "Показати POC", GroupName = "3. POC / VA", Order = 0)]
		public bool ShowPOC { get; set; }

		[Display(Name = "Показати VA", GroupName = "3. POC / VA", Order = 1)]
		public bool ShowVA { get; set; }

		[Range(50, 90)]
		[Display(Name = "VA (%)", GroupName = "3. POC / VA", Order = 2)]
		public int VA_Percent { get; set; }

		[Range(0, 100)]
		[Display(Name = "Затемнення VA (%)", GroupName = "3. POC / VA", Order = 3)]
		public int VA_Dimming { get; set; }

		[XmlIgnore]
		[Display(Name = "Колір POC", GroupName = "4. Кольори", Order = 0)]
		public System.Windows.Media.Brush POC_Color { get; set; }

		[Browsable(false)]
		public string POC_Color_Serializable
		{
			get { return Serialize.BrushToString(POC_Color); }
			set { POC_Color = Serialize.StringToBrush(value); }
		}

		[XmlIgnore]
		[Display(Name = "Колір RTH", GroupName = "4. Кольори", Order = 1)]
		public System.Windows.Media.Brush RTH_Color { get; set; }

		[Browsable(false)]
		public string RTH_Color_Serializable
		{
			get { return Serialize.BrushToString(RTH_Color); }
			set { RTH_Color = Serialize.StringToBrush(value); }
		}

		[XmlIgnore]
		[Display(Name = "Колір ETH", GroupName = "4. Кольори", Order = 2)]
		public System.Windows.Media.Brush ETH_Color { get; set; }

		[Browsable(false)]
		public string ETH_Color_Serializable
		{
			get { return Serialize.BrushToString(ETH_Color); }
			set { ETH_Color = Serialize.StringToBrush(value); }
		}

		#endregion
	}

	[TypeConverter(typeof(SVPSessionMode_Converter))]
	public enum SVPSessionMode { RTH, ETH, RTH_ETH_Split, RTH_ETH }

	public class SVPSessionMode_Converter : TypeConverter
	{
		private static readonly Dictionary<SVPSessionMode, string> _map =
			new Dictionary<SVPSessionMode, string>
		{
			{ SVPSessionMode.RTH,           "RTH (09:30–16:00)" },
			{ SVPSessionMode.ETH,           "ETH" },
			{ SVPSessionMode.RTH_ETH_Split, "RTH | ETH (окремі)" },
			{ SVPSessionMode.RTH_ETH,       "RTH+ETH (09:30–09:30)" },
		};
		public override bool GetStandardValuesSupported(ITypeDescriptorContext c) => true;
		public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext c)
			=> new StandardValuesCollection(_map.Keys.ToList());
		public override bool CanConvertFrom(ITypeDescriptorContext c, Type t)
			=> t == typeof(string) || base.CanConvertFrom(c, t);
		public override bool CanConvertTo(ITypeDescriptorContext c, Type t)
			=> t == typeof(string) || base.CanConvertTo(c, t);
		public override object ConvertTo(ITypeDescriptorContext c, CultureInfo ci, object v, Type t)
		{
			if (t == typeof(string) && v is SVPSessionMode e && _map.ContainsKey(e))
				return _map[e];
			return base.ConvertTo(c, ci, v, t);
		}
		public override object ConvertFrom(ITypeDescriptorContext c, CultureInfo ci, object v)
		{
			if (v is string s)
				foreach (var kv in _map)
					if (kv.Value == s) return kv.Key;
			return base.ConvertFrom(c, ci, v);
		}
	}

	[TypeConverter(typeof(SVPDisplayMode_Converter))]
	public enum SVPDisplayMode { Standard, Contour }

	public class SVPDisplayMode_Converter : TypeConverter
	{
		private static readonly Dictionary<SVPDisplayMode, string> _map =
			new Dictionary<SVPDisplayMode, string>
		{
			{ SVPDisplayMode.Standard, "Standard" },
			{ SVPDisplayMode.Contour,  "Contour" },
		};
		public override bool GetStandardValuesSupported(ITypeDescriptorContext c) => true;
		public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext c)
			=> new StandardValuesCollection(_map.Keys.ToList());
		public override bool CanConvertFrom(ITypeDescriptorContext c, Type t)
			=> t == typeof(string) || base.CanConvertFrom(c, t);
		public override bool CanConvertTo(ITypeDescriptorContext c, Type t)
			=> t == typeof(string) || base.CanConvertTo(c, t);
		public override object ConvertTo(ITypeDescriptorContext c, CultureInfo ci, object v, Type t)
		{
			if (t == typeof(string) && v is SVPDisplayMode e && _map.ContainsKey(e))
				return _map[e];
			return base.ConvertTo(c, ci, v, t);
		}
		public override object ConvertFrom(ITypeDescriptorContext c, CultureInfo ci, object v)
		{
			if (v is string s)
				foreach (var kv in _map)
					if (kv.Value == s) return kv.Key;
			return base.ConvertFrom(c, ci, v);
		}
	}
}
