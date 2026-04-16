using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using SharpDX;

namespace NinjaTrader.NinjaScript.Indicators.Loci
{
	public class StackImb : Indicator
	{
		#region Types

		private enum LineDir { Bullish, Bearish }

		private struct ImbalanceLine
		{
			public double  Price;
			public int     StartBar;
			public int     EndBar;        // -1 = активна, >=0 = бар торкання/expiry
			public LineDir Direction;
		}

		#endregion

		#region Private Fields

		private List<ImbalanceLine> lines;
		private double tickSize;
		private DateTime cutoffTime;

		// SharpDX
		private SharpDX.Direct2D1.Brush dxBullBrush;
		private SharpDX.Direct2D1.Brush dxBearBrush;

		#endregion

		#region OnStateChange

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description				= "Stack Imb — ATAS 1:1 port";
				Name					= "Stack Imb";
				IsOverlay				= true;
				Calculate				= Calculate.OnBarClose;
				DrawOnPricePanel		= true;
				IsAutoScale				= false;
				IsSuspendedWhileInactive = true;
				BarsRequiredToPlot		= 0;
				DisplayInDataBox		= false;
				PaintPriceMarkers		= false;
				ArePlotsConfigurable	= false;
				MaximumBarsLookBack		= MaximumBarsLookBack.Infinite;
				ZOrder					= -10000;

				// ── Дефолти Loci ──
				ImbalanceRatio		= 300;
				ImbalanceRange		= 3;
				ImbalanceVolume		= 0;
				IgnoreZeroValues	= false;
				DaysLookBack		= 20;
				TicksPerLevel		= 2;

				// ── Відмальовка ──
				TillTouch			= false;
				DrawBarsLength		= 2;
				LineWidth			= 3;
				LineOpacity			= 60;

				// ── Кольори ──
				AskBidColor			= MkBrush(0x00, 0x30, 0x63);
				BidAskColor			= MkBrush(0x55, 0x00, 0x32);
			}
			else if (State == State.Configure)
			{
				AddVolumetric("", BarsPeriod.BarsPeriodType, BarsPeriod.Value,
					VolumetricDeltaType.BidAsk, 1);
			}
			else if (State == State.DataLoaded)
			{
				lines = new List<ImbalanceLine>(512);
				tickSize = Instrument.MasterInstrument.TickSize;
				cutoffTime = DateTime.MinValue;
			}
			else if (State == State.Terminated)
			{
				DisposeAllBrushes();
			}
		}

		#endregion

		#region OnBarUpdate

		protected override void OnBarUpdate()
		{
			if (BarsInProgress != 1) return;
			if (CurrentBars[0] < 0 || CurrentBars[1] < 0) return;

			// ── DaysLookBack ──
			if (cutoffTime == DateTime.MinValue && DaysLookBack > 0)
			{
				cutoffTime = BarsArray[0].GetTime(BarsArray[0].Count - 1)
					.Date.AddDays(-DaysLookBack);
			}

			if (DaysLookBack > 0 && Times[1][0] < cutoffTime)
				return;

			var vbt = BarsArray[1].BarsType as
				NinjaTrader.NinjaScript.BarsTypes.VolumetricBarsType;
			if (vbt == null) return;

			int primaryBar = CurrentBars[0];

			// ── Сканувати бар ──
			ScanBar(vbt, CurrentBars[1], primaryBar);

			// ── Touch / Expiry ──
			bool useTillTouch = TillTouch || DrawBarsLength == 0;

			if (useTillTouch)
			{
				double barHigh = Highs[0][0];
				double barLow  = Lows[0][0];

				for (int i = 0; i < lines.Count; i++)
				{
					var ln = lines[i];
					if (ln.EndBar >= 0) continue;
					if (ln.StartBar == primaryBar) continue;
					if (barHigh >= ln.Price && barLow <= ln.Price)
					{
						ln.EndBar = primaryBar;
						lines[i] = ln;
					}
				}
			}
			else
			{
				for (int i = 0; i < lines.Count; i++)
				{
					var ln = lines[i];
					if (ln.EndBar >= 0) continue;
					if (primaryBar - ln.StartBar >= DrawBarsLength)
					{
						ln.EndBar = ln.StartBar + DrawBarsLength;
						lines[i] = ln;
					}
				}
			}

			// ── Cleanup ──
			if (lines.Count > 5000)
				lines.RemoveAll(ln => ln.EndBar >= 0 && primaryBar - ln.EndBar > 50000);

			if (State == State.Realtime && !IsInHitTest)
				ForceRefresh();
		}

		#endregion

		#region Imbalance Scanning

		private void ScanBar(
			NinjaTrader.NinjaScript.BarsTypes.VolumetricBarsType vbt,
			int tickBar, int primaryBar)
		{
			var vol     = vbt.Volumes[tickBar];
			double low  = Lows[1][0];
			double high = Highs[1][0];

			if (high <= low) return;

			// ═══════════════════════════════════════════════
			// КРОК 1: Зібрати ВСІ рівні по base tickSize
			// ═══════════════════════════════════════════════
			int totalLevels = (int)Math.Round((high - low) / tickSize) + 1;
			if (totalLevels < 2) return;

			var rawPrices = new double[totalLevels];
			var rawBids   = new long[totalLevels];
			var rawAsks   = new long[totalLevels];

			for (int i = 0; i < totalLevels; i++)
			{
				double p = low + i * tickSize;
				rawPrices[i] = p;
				rawBids[i]   = vol.GetBidVolumeForPrice(p);
				rawAsks[i]   = vol.GetAskVolumeForPrice(p);
			}

			// ═══════════════════════════════════════════════
			// КРОК 2: Агрегувати по TicksPerLevel
			// ═══════════════════════════════════════════════
			double[] aggPrices;
			long[]   aggBids;
			long[]   aggAsks;
			int      aggCount;

			if (TicksPerLevel <= 1)
			{
				// Без агрегації
				aggPrices = rawPrices;
				aggBids   = rawBids;
				aggAsks   = rawAsks;
				aggCount  = totalLevels;
			}
			else
			{
				int tpl = TicksPerLevel;
				aggCount = (totalLevels + tpl - 1) / tpl;  // ceiling division

				aggPrices = new double[aggCount];
				aggBids   = new long[aggCount];
				aggAsks   = new long[aggCount];

				for (int g = 0; g < aggCount; g++)
				{
					int start = g * tpl;
					int end   = Math.Min(start + tpl, totalLevels);

					aggPrices[g] = rawPrices[start];  // ціна = нижній рівень групи
					long sumBid = 0, sumAsk = 0;

					for (int j = start; j < end; j++)
					{
						sumBid += rawBids[j];
						sumAsk += rawAsks[j];
					}

					aggBids[g] = sumBid;
					aggAsks[g] = sumAsk;
				}
			}

			// ═══════════════════════════════════════════════
			// КРОК 3: Фільтрувати порожні ПІСЛЯ агрегації
			// ═══════════════════════════════════════════════
			var prices = new List<double>(aggCount);
			var bids   = new List<long>(aggCount);
			var asks   = new List<long>(aggCount);

			for (int i = 0; i < aggCount; i++)
			{
				if (aggBids[i] == 0 && aggAsks[i] == 0)
					continue;

				prices.Add(aggPrices[i]);
				bids.Add(aggBids[i]);
				asks.Add(aggAsks[i]);
			}

			int count = prices.Count;
			if (count < 2) return;

			// ═══════════════════════════════════════════════
			// КРОК 4: Розрахунок імбалансів (ATAS 1:1)
			// ═══════════════════════════════════════════════
			double[] fp = prices.ToArray();
			long[]   fb = bids.ToArray();
			long[]   fa = asks.ToArray();

			ScanAskBid(fp, fb, fa, count, primaryBar);
			ScanBidAsk(fp, fb, fa, count, primaryBar);
		}

		// ── ATAS CalculateAskBid ──
		private void ScanAskBid(double[] prices, long[] bids, long[] asks,
			int count, int primaryBar)
		{
			var imb = new bool[count];

			for (int i = 0; i < count - 1; i++)
			{
				long askFilterValue = bids[i] * ImbalanceRatio / 100;

				if (IgnoreZeroValues && askFilterValue == 0)
					continue;

				if (asks[i + 1] > askFilterValue && asks[i + 1] > ImbalanceVolume)
					imb[i] = true;
			}

			int run = 0;
			for (int i = 0; i < count; i++)
			{
				if (imb[i])
				{
					run++;
				}
				else
				{
					if (run >= ImbalanceRange)
					{
						for (int k = i - run + 1; k <= i; k++)
							EmitLine(prices[k], primaryBar, LineDir.Bullish);
					}
					run = 0;
				}
			}
			if (run >= ImbalanceRange)
			{
				for (int k = count - 1 - run + 1; k <= count - 1; k++)
					EmitLine(prices[k], primaryBar, LineDir.Bullish);
			}
		}

		// ── ATAS CalculateBidAsk ──
		private void ScanBidAsk(double[] prices, long[] bids, long[] asks,
			int count, int primaryBar)
		{
			var imb = new bool[count];

			for (int i = 0; i < count - 1; i++)
			{
				long bidFilterValue = asks[i + 1] * ImbalanceRatio / 100;

				if (IgnoreZeroValues && bidFilterValue == 0)
					continue;

				if (bids[i] > bidFilterValue && bids[i] > ImbalanceVolume)
					imb[i] = true;
			}

			int run = 0;
			for (int i = 0; i < count; i++)
			{
				if (imb[i])
				{
					run++;
				}
				else
				{
					if (run >= ImbalanceRange)
					{
						for (int k = i - run + 1; k <= i; k++)
						{
							if (k - 1 >= 0)
								EmitLine(prices[k - 1], primaryBar, LineDir.Bearish);
						}
					}
					run = 0;
				}
			}
			if (run >= ImbalanceRange)
			{
				for (int k = count - 1 - run + 1; k <= count - 1; k++)
				{
					if (k - 1 >= 0)
						EmitLine(prices[k - 1], primaryBar, LineDir.Bearish);
				}
			}
		}

		private void EmitLine(double price, int primaryBar, LineDir dir)
		{
			lines.Add(new ImbalanceLine
			{
				Price     = price,
				StartBar  = primaryBar,
				EndBar    = -1,
				Direction = dir
			});
		}

		#endregion

		#region OnRender

		public override void OnRenderTargetChanged()
		{
			DisposeAllBrushes();
		}

		protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
		{
			if (ZOrder > -10000)
				ZOrder = -10000;

			base.OnRender(chartControl, chartScale);

			if (RenderTarget == null || lines == null || lines.Count == 0)
				return;
			if (ChartBars == null) return;

			EnsureBrushes();

			int fromBar = ChartBars.FromIndex;
			int toBar   = ChartBars.ToIndex;
			bool useTillTouch = TillTouch || DrawBarsLength == 0;

			for (int i = 0; i < lines.Count; i++)
			{
				var ln = lines[i];

				int endBar;
				if (ln.EndBar >= 0)
					endBar = ln.EndBar;
				else if (useTillTouch)
					endBar = toBar;
				else
					endBar = ln.StartBar + DrawBarsLength;

				endBar = Math.Min(endBar, toBar);

				if (ln.StartBar > toBar || endBar < fromBar)
					continue;

				float xLeft  = chartControl.GetXByBarIndex(
					ChartBars, Math.Max(ln.StartBar, fromBar));
				float xRight = chartControl.GetXByBarIndex(ChartBars, endBar);

				if (xRight <= xLeft) continue;

				float y = chartScale.GetYByValue(ln.Price);

				var brush = ln.Direction == LineDir.Bullish ? dxBullBrush : dxBearBrush;

				RenderTarget.DrawLine(
					new SharpDX.Vector2(xLeft, y),
					new SharpDX.Vector2(xRight, y),
					brush, LineWidth);
			}
		}

		#endregion

		#region SharpDX Brushes

		private void EnsureBrushes()
		{
			if (dxBullBrush != null && !dxBullBrush.IsDisposed)
				return;

			byte alpha = (byte)(LineOpacity * 255 / 100);
			dxBullBrush = CreateDxBrush(AskBidColor, alpha);
			dxBearBrush = CreateDxBrush(BidAskColor, alpha);
		}

		private SharpDX.Direct2D1.Brush CreateDxBrush(
			System.Windows.Media.Brush wpf, byte alpha)
		{
			var sc = ((System.Windows.Media.SolidColorBrush)wpf).Color;
			return new SharpDX.Direct2D1.SolidColorBrush(RenderTarget,
				new SharpDX.Color(sc.R, sc.G, sc.B, alpha));
		}

		private void DisposeAllBrushes()
		{
			if (dxBullBrush != null) { dxBullBrush.Dispose(); dxBullBrush = null; }
			if (dxBearBrush != null) { dxBearBrush.Dispose(); dxBearBrush = null; }
		}

		#endregion

		#region Helpers

		private static System.Windows.Media.SolidColorBrush MkBrush(byte r, byte g, byte b)
		{
			var brush = new System.Windows.Media.SolidColorBrush(
				System.Windows.Media.Color.FromRgb(r, g, b));
			brush.Freeze();
			return brush;
		}

		#endregion

		#region Параметри

		// ── 1. Настройки ──

		[Range(0, 100000)]
		[Display(Name = "Співвідношення дисбалансу", Description = "300 = 300%",
			Order = 0, GroupName = "1. Налаштування")]
		public int ImbalanceRatio { get; set; }

		[Range(0, 100000)]
		[Display(Name = "Діапазон дисбалансу", Description = "Мін послідовних рівнів",
			Order = 1, GroupName = "1. Налаштування")]
		public int ImbalanceRange { get; set; }

		[Range(0, 10000000)]
		[Display(Name = "Об'єм дисбалансу", Description = "ATAS дефолт = 30",
			Order = 2, GroupName = "1. Налаштування")]
		public int ImbalanceVolume { get; set; }

		[Display(Name = "Ігнорувати нульові",
			Order = 3, GroupName = "1. Налаштування")]
		public bool IgnoreZeroValues { get; set; }

		// ── 2. Отрисовка ──

		[Display(Name = "Лінія до дотику",
			Order = 0, GroupName = "2. Відмальовка")]
		public bool TillTouch { get; set; }

		[XmlIgnore]
		[Display(Name = "Колір переваги Ask/Bid", Order = 1, GroupName = "2. Відмальовка")]
		public Brush AskBidColor { get; set; }
		[Browsable(false)]
		public string AskBidColorSerialize
		{ get { return Serialize.BrushToString(AskBidColor); } set { AskBidColor = Serialize.StringToBrush(value); } }

		[XmlIgnore]
		[Display(Name = "Колір переваги Bid/Ask", Order = 2, GroupName = "2. Відмальовка")]
		public Brush BidAskColor { get; set; }
		[Browsable(false)]
		public string BidAskColorSerialize
		{ get { return Serialize.BrushToString(BidAskColor); } set { BidAskColor = Serialize.StringToBrush(value); } }

		[Range(1, 100)]
		[Display(Name = "Ширина лінії", Description = "В пікселях (ATAS = 10)",
			Order = 3, GroupName = "2. Відмальовка")]
		public int LineWidth { get; set; }

		[Range(0, 10000)]
		[Display(Name = "Малювати лінію X барів", Description = "0 = завжди до дотику",
			Order = 4, GroupName = "2. Відмальовка")]
		public int DrawBarsLength { get; set; }

		[Range(5, 100)]
		[Display(Name = "Прозорість ліній %", Description = "60 = напівпрозорі",
			Order = 5, GroupName = "2. Відмальовка")]
		public int LineOpacity { get; set; }

		// ── 3. Расчёт ──

		[Range(0, 1000)]
		[Display(Name = "Кількість днів", Description = "0 = всі",
			Order = 0, GroupName = "3. Розрахунок")]
		public int DaysLookBack { get; set; }

		[Range(1, 100)]
		[Display(Name = "Тіків на рівень", Description = "NQ=2, ES=1. Агрегація рівнів перед розрахунком",
			Order = 1, GroupName = "3. Розрахунок")]
		public int TicksPerLevel { get; set; }

		#endregion
	}
}

