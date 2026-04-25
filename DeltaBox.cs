using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.NinjaScript;
using SharpDX;
using SharpDX.Direct2D1;
using SharpDX.DirectWrite;

namespace NinjaTrader.NinjaScript.Indicators.Loci
{
	public class DeltaBox : Indicator
	{
		#region Private Fields

		// ── Gradient ──
		private Series<double> rawDelta;
		private System.Windows.Media.SolidColorBrush[] palette;

		// ── Volume Anomaly ──
		private Series<double> anomBuyVol;
		private Series<double> anomSellVol;
		private Series<int>    anomBuyTier;
		private Series<int>    anomSellTier;
		private Series<double> anomBuyPosY;
		private Series<double> anomSellPosY;
		private int anomLastBuyBar;
		private int anomLastSellBar;

		// ── Volume Anomaly SharpDX brushes ──
		private SharpDX.Direct2D1.SolidColorBrush dxAnomBuyBrush;
		private SharpDX.Direct2D1.SolidColorBrush dxAnomSellBrush;
		private SharpDX.Direct2D1.SolidColorBrush dxAnomInfoBrush;
		private SharpDX.Direct2D1.SolidColorBrush dxAnomPanelBuyBrush;
		private SharpDX.Direct2D1.SolidColorBrush dxAnomPanelSellBrush;

		private TextFormat fmtAnomPanel;
		private double lastBuyVol;
		private double lastSellVol;

		#endregion

		#region OnStateChange

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description					= "Delta Box — Heatmap + Exhaustion + Turnaround + Strength + Volume Anomaly";
				Name						= "Delta Box";
				IsOverlay					= true;
				Calculate					= Calculate.OnEachTick;
				DrawOnPricePanel			= true;
				IsAutoScale					= false;
				IsSuspendedWhileInactive	= false;
				BarsRequiredToPlot			= 0;
				DisplayInDataBox			= false;
				PaintPriceMarkers			= false;
				ArePlotsConfigurable		= false;
				MaximumBarsLookBack			= MaximumBarsLookBack.Infinite;

				// ── 1. Delta Heatmap ──
				ShowHeatmap			= true;
				Smoothing			= 10;
				GradientScale		= 1.0;
				DeadZone			= 0.15;
				StrongBuyColor	= MkBrush(0x52, 0xA5, 0xFF);
				StrongSellColor	= MkBrush(0xFF, 0x54, 0xAF);
				NeutralColor	= MkBrush(0x30, 0x30, 0x30);

				// ── 2. Exhaustion ──
				ShowExhaustion		= true;
				ExhCalcMode			= ExhaustionCalcMode.BidAndAsk;
				ExhLevelCount		= 5;
				ExhTicksPerLevel	= 2;
				ExhSignalOffset		= 10;
				ExhResistanceColor	= MkBrush(0x87, 0x87, 0x35);
				ExhSupportColor		= MkBrush(0x87, 0x87, 0x35);
				ExhShape			= LociMarkerShape.Dot;
				ExhMarkerSize		= 2;

				// ── 3. Turnaround ──
				ShowTurnaround		= true;
				TurnSignalOffset	= 10;
				TurnBearishColor	= MkBrush(0x6E, 0x0A, 0x29);
				TurnBullishColor	= MkBrush(0x0D, 0x47, 0x85);
				TurnShape			= LociMarkerShape.Triangle;
				TurnMarkerSize		= 3;

				// ── 4. Strength ──
				ShowStrength		= true;
				StrMinPercent		= 90;
				StrMaxPercent		= 98;
				StrSignalOffset		= 10;
				StrPositiveColor	= MkBrush(0x0D, 0x47, 0x85);
				StrNegativeColor	= MkBrush(0x6E, 0x0A, 0x29);
				StrShape			= LociMarkerShape.Dot;
				StrMarkerSize		= 2;

				// ── 5. Volume Anomaly ──
				ShowAnomaly			= true;
				AnomLookback		= 10;
				AnomStrength		= 1.8;
				AnomCooldown		= 0;
				AnomBuyColor		= MkBrush(0x3B, 0x81, 0xDB);
				AnomSellColor		= MkBrush(0xD9, 0x41, 0x76);
				AnomOpacity			= 50;
				AnomTier1Radius		= 7;
				AnomTier2Radius		= 10;
				AnomTier3Radius		= 14;
				AnomShowPanel		= true;
				AnomPanelOffsetY	= 28;
				AnomInfoFontColor	= MkBrush(0x80, 0x80, 0x80);
				AnomPanelBuyColor	= MkBrush(0x3B, 0x81, 0xDB);
				AnomPanelSellColor	= MkBrush(0xD9, 0x41, 0x76);
				AnomPanelFontSize	= 13;
			}
			else if (State == State.Configure)
			{
				AddVolumetric("", BarsPeriod.BarsPeriodType, BarsPeriod.Value,
					VolumetricDeltaType.BidAsk, 1);

				// Plots 0-1: Exhaustion
				AddPlot(new Stroke(CloneFreeze(ExhResistanceColor), DashStyleHelper.Solid, ExhMarkerSize),
					ToPlotStyle(ExhShape, false), "ExhRes");
				AddPlot(new Stroke(CloneFreeze(ExhSupportColor), DashStyleHelper.Solid, ExhMarkerSize),
					ToPlotStyle(ExhShape, true), "ExhSup");

				// Plots 2-3: Turnaround
				AddPlot(new Stroke(CloneFreeze(TurnBearishColor), DashStyleHelper.Solid, TurnMarkerSize),
					ToPlotStyle(TurnShape, false), "TurnBear");
				AddPlot(new Stroke(CloneFreeze(TurnBullishColor), DashStyleHelper.Solid, TurnMarkerSize),
					ToPlotStyle(TurnShape, true), "TurnBull");

				// Plots 4-5: Strength
				AddPlot(new Stroke(CloneFreeze(StrPositiveColor), DashStyleHelper.Solid, StrMarkerSize),
					ToPlotStyle(StrShape, true), "StrUp");
				AddPlot(new Stroke(CloneFreeze(StrNegativeColor), DashStyleHelper.Solid, StrMarkerSize),
					ToPlotStyle(StrShape, false), "StrDn");

				// Volume Anomaly uses SharpDX OnRender — no Plots
			}
			else if (State == State.DataLoaded)
			{
				var calcProxy = SMA(BarsArray[1], 1);
				rawDelta    = new Series<double>(calcProxy, MaximumBarsLookBack.Infinite);
				anomBuyVol  = new Series<double>(calcProxy, MaximumBarsLookBack.Infinite);
				anomSellVol = new Series<double>(calcProxy, MaximumBarsLookBack.Infinite);
				anomBuyTier  = new Series<int>(calcProxy, MaximumBarsLookBack.Infinite);
				anomSellTier = new Series<int>(calcProxy, MaximumBarsLookBack.Infinite);
				anomBuyPosY  = new Series<double>(calcProxy, MaximumBarsLookBack.Infinite);
				anomSellPosY = new Series<double>(calcProxy, MaximumBarsLookBack.Infinite);
				anomLastBuyBar  = -999;
				anomLastSellBar = -999;
				BuildPalette();
			}
			else if (State == State.Terminated)
			{
				DisposeAnomResources();
			}
		}

		#endregion

		#region OnBarUpdate

		protected override void OnBarUpdate()
		{
			if (BarsInProgress != 1)
				return;
			if (CurrentBars[0] < 0 || CurrentBars[1] < 1)
				return;

			var vbt = BarsArray[1].BarsType as
				NinjaTrader.NinjaScript.BarsTypes.VolumetricBarsType;
			if (vbt == null) return;

			var vol = vbt.Volumes[CurrentBar];

			// ══════════════════════════════════════════════════════
			//  Delta Heatmap (Gradient Bars)
			// ══════════════════════════════════════════════════════
			rawDelta[0] = vol.TotalBuyingVolume - vol.TotalSellingVolume;

			if (ShowHeatmap)
				ApplyGradient();

			// ══════════════════════════════════════════════════════
			//  Exhaustion
			// ══════════════════════════════════════════════════════
			Values[0][0] = double.NaN;
			Values[1][0] = double.NaN;

			if (ShowExhaustion)
				ProcessExhaustion(vbt);

			// ══════════════════════════════════════════════════════
			//  Delta Turnaround
			// ══════════════════════════════════════════════════════
			Values[2][0] = double.NaN;
			Values[3][0] = double.NaN;

			if (ShowTurnaround && CurrentBars[1] >= 2)
				ProcessTurnaround(vbt);

			// ══════════════════════════════════════════════════════
			//  Delta Strength
			// ══════════════════════════════════════════════════════
			Values[4][0] = double.NaN;
			Values[5][0] = double.NaN;

			if (ShowStrength)
				ProcessStrength(vbt);

			// ══════════════════════════════════════════════════════
			//  Volume Anomaly (data → Series, rendering via OnRender)
			// ══════════════════════════════════════════════════════
			anomBuyVol[0]   = vol.TotalBuyingVolume;
			anomSellVol[0]  = vol.TotalSellingVolume;
			anomBuyTier[0]  = 0;
			anomSellTier[0] = 0;
			anomBuyPosY[0]  = double.NaN;
			anomSellPosY[0] = double.NaN;

			lastBuyVol  = anomBuyVol[0];
			lastSellVol = anomSellVol[0];

			if (ShowAnomaly)
				ProcessAnomaly(vbt);
		}

		#endregion

		#region Gradient

		private void ApplyGradient()
		{
			double wma    = 0;
			double totalW = 0;
			int    start  = Math.Max(0, CurrentBar - Smoothing + 1);
			int    w      = 0;

			for (int i = start; i <= CurrentBar; i++)
			{
				w++;
				totalW += w;
				wma    += w * rawDelta.GetValueAt(i);
			}
			if (totalW > 0) wma /= totalW;

			double avgAbs = 0;
			int    cnt    = 0;
			for (int i = start; i <= CurrentBar; i++)
			{
				avgAbs += Math.Abs(rawDelta.GetValueAt(i));
				cnt++;
			}
			if (cnt > 0) avgAbs /= cnt;

			double denom = avgAbs * GradientScale;
			double ratio = denom < 0.0001 ? 0.0 : wma / denom;
			ratio = Math.Max(-1.0, Math.Min(1.0, ratio));
			if (Math.Abs(ratio) < DeadZone) ratio = 0;

			var brush = GetPaletteBrush(ratio);

			if (State == State.Realtime)
			{
				BarBrushes[0]           = brush;
				CandleOutlineBrushes[0] = brush;
			}
			else
			{
				for (int i = 0; i <= CurrentBars[0]; i++)
				{
					if (Times[0][i] <= Times[1][1]) break;
					if (Times[0][i] <= Times[1][0])
					{
						BarBrushes[i]           = brush;
						CandleOutlineBrushes[i] = brush;
					}
				}
			}
		}

		#endregion

		#region Exhaustion

		private void ProcessExhaustion(
			NinjaTrader.NinjaScript.BarsTypes.VolumetricBarsType vbt)
		{
			double high = Highs[1][0];
			double low  = Lows[1][0];
			double step = TickSize;

			bool checkTop = ExhCalcMode == ExhaustionCalcMode.Ask
				|| ExhCalcMode == ExhaustionCalcMode.BidAndAsk
				|| ExhCalcMode == ExhaustionCalcMode.Volume;

			if (checkTop && CheckExhFromHigh(vbt, CurrentBar, high, low, step))
				Values[0][0] = Highs[0][0] + ExhSignalOffset * TickSize;

			bool checkBottom = ExhCalcMode == ExhaustionCalcMode.Bid
				|| ExhCalcMode == ExhaustionCalcMode.BidAndAsk
				|| ExhCalcMode == ExhaustionCalcMode.Volume;

			if (checkBottom && CheckExhFromLow(vbt, CurrentBar, high, low, step))
				Values[1][0] = Lows[0][0] - ExhSignalOffset * TickSize;
		}

		private bool CheckExhFromHigh(
			NinjaTrader.NinjaScript.BarsTypes.VolumetricBarsType vbt,
			int barIdx, double high, double low, double step)
		{
			var    vol    = vbt.Volumes[barIdx];
			bool   useAsk = ExhCalcMode != ExhaustionCalcMode.Volume;
			int    tpl    = ExhTicksPerLevel;

			// Збираємо raw рівні від high вниз
			int rawCount = (int)Math.Round((high - low) / step) + 1;
			if (rawCount < tpl * ExhLevelCount) return false;

			// Агрегуємо по TPL і перевіряємо наростання
			long   prevValue = 0;
			int    count     = 0;

			for (int g = 0; g < rawCount / tpl; g++)
			{
				long aggValue = 0;
				for (int t = 0; t < tpl; t++)
				{
					double price = high - (g * tpl + t) * step;
					if (price < low) break;
					aggValue += useAsk
						? vol.GetAskVolumeForPrice(price)
						: vol.GetBidVolumeForPrice(price)
						  + vol.GetAskVolumeForPrice(price);
				}

				count++;
				if (count == 1)
				{
					prevValue = aggValue;
					if (count == ExhLevelCount) return true;
					continue;
				}
				if (aggValue > prevValue)
				{
					prevValue = aggValue;
					if (count == ExhLevelCount) return true;
				}
				else return false;
			}
			return false;
		}

		private bool CheckExhFromLow(
			NinjaTrader.NinjaScript.BarsTypes.VolumetricBarsType vbt,
			int barIdx, double high, double low, double step)
		{
			var    vol    = vbt.Volumes[barIdx];
			bool   useBid = ExhCalcMode != ExhaustionCalcMode.Volume;
			int    tpl    = ExhTicksPerLevel;

			// Збираємо raw рівні від low вгору
			int rawCount = (int)Math.Round((high - low) / step) + 1;
			if (rawCount < tpl * ExhLevelCount) return false;

			// Агрегуємо по TPL і перевіряємо наростання
			long   prevValue = 0;
			int    count     = 0;

			for (int g = 0; g < rawCount / tpl; g++)
			{
				long aggValue = 0;
				for (int t = 0; t < tpl; t++)
				{
					double price = low + (g * tpl + t) * step;
					if (price > high) break;
					aggValue += useBid
						? vol.GetBidVolumeForPrice(price)
						: vol.GetBidVolumeForPrice(price)
						  + vol.GetAskVolumeForPrice(price);
				}

				count++;
				if (count == 1)
				{
					prevValue = aggValue;
					if (count == ExhLevelCount) return true;
					continue;
				}
				if (aggValue > prevValue)
				{
					prevValue = aggValue;
					if (count == ExhLevelCount) return true;
				}
				else return false;
			}
			return false;
		}

		#endregion

		#region Delta Turnaround

		private void ProcessTurnaround(
			NinjaTrader.NinjaScript.BarsTypes.VolumetricBarsType vbt)
		{
			var vol = vbt.Volumes[CurrentBar];

			double curOpen  = Opens[1][0];
			double curClose = Closes[1][0];
			double curHigh  = Highs[1][0];
			double curLow   = Lows[1][0];
			double curDelta = vol.BarDelta;

			double prev1Open  = Opens[1][1];
			double prev1Close = Closes[1][1];
			double prev1High  = Highs[1][1];
			double prev1Low   = Lows[1][1];

			double prev2Open  = Opens[1][2];
			double prev2Close = Closes[1][2];

			double offset = TurnSignalOffset * TickSize;

			if (prev2Close > prev2Open
				&& prev1Close > prev1Open
				&& curClose < curOpen
				&& curHigh >= prev1High
				&& curDelta < 0)
			{
				Values[2][0] = Highs[0][0] + offset;
			}

			if (prev2Close < prev2Open
				&& prev1Close < prev1Open
				&& curClose > curOpen
				&& curLow <= prev1Low
				&& curDelta > 0)
			{
				Values[3][0] = Lows[0][0] - offset;
			}
		}

		#endregion

		#region Delta Strength

		private void ProcessStrength(
			NinjaTrader.NinjaScript.BarsTypes.VolumetricBarsType vbt)
		{
			var vol = vbt.Volumes[CurrentBar];

			double barDelta = vol.BarDelta;
			double maxDelta = vol.MaxSeenDelta;
			double minDelta = vol.MinSeenDelta;

			double offset = StrSignalOffset * TickSize;
			double minPct = StrMinPercent / 100.0;
			double maxPct = StrMaxPercent / 100.0;

			if (barDelta > 0 && maxDelta > 0)
			{
				double strength = barDelta / maxDelta;
				if (strength >= minPct && strength <= maxPct)
					Values[4][0] = Lows[0][0] - offset;
			}
			else if (barDelta < 0 && minDelta < 0)
			{
				double strength = barDelta / minDelta;
				if (strength >= minPct && strength <= maxPct)
					Values[5][0] = Highs[0][0] + offset;
			}
		}

		#endregion

		#region Volume Anomaly

		private void ProcessAnomaly(
			NinjaTrader.NinjaScript.BarsTypes.VolumetricBarsType vbt)
		{
			if (CurrentBar < AnomLookback + 1)
				return;

			double sumBuy = 0, sumSell = 0;
			for (int i = 1; i <= AnomLookback; i++)
			{
				sumBuy  += anomBuyVol[i];
				sumSell += anomSellVol[i];
			}
			double avgBuy  = sumBuy  / AnomLookback;
			double avgSell = sumSell / AnomLookback;

			double sqBuy = 0, sqSell = 0;
			for (int i = 1; i <= AnomLookback; i++)
			{
				double dB = anomBuyVol[i]  - avgBuy;
				double dS = anomSellVol[i] - avgSell;
				sqBuy  += dB * dB;
				sqSell += dS * dS;
			}
			double stdBuy  = Math.Sqrt(sqBuy  / AnomLookback);
			double stdSell = Math.Sqrt(sqSell / AnomLookback);

			double gap = AnomStrength * 0.5;
			double threshBuy1 = avgBuy  + stdBuy  * AnomStrength;
			double threshBuy2 = avgBuy  + stdBuy  * (AnomStrength + gap);
			double threshBuy3 = avgBuy  + stdBuy  * (AnomStrength + gap * 2.0);

			double threshSell1 = avgSell + stdSell * AnomStrength;
			double threshSell2 = avgSell + stdSell * (AnomStrength + gap);
			double threshSell3 = avgSell + stdSell * (AnomStrength + gap * 2.0);

			double bv = anomBuyVol[0];
			double sv = anomSellVol[0];

			int bTier = 0;
			if      (bv > threshBuy3) bTier = 3;
			else if (bv > threshBuy2) bTier = 2;
			else if (bv > threshBuy1) bTier = 1;

			int sTier = 0;
			if      (sv > threshSell3) sTier = 3;
			else if (sv > threshSell2) sTier = 2;
			else if (sv > threshSell1) sTier = 1;

			// Cooldown (higher tier breaks through)
			if (bTier > 0 && AnomCooldown > 0)
			{
				if (CurrentBar - anomLastBuyBar < AnomCooldown
					&& bTier <= anomBuyTier.GetValueAt(anomLastBuyBar))
					bTier = 0;
			}
			if (bTier > 0)
				anomLastBuyBar = CurrentBar;

			if (sTier > 0 && AnomCooldown > 0)
			{
				if (CurrentBar - anomLastSellBar < AnomCooldown
					&& sTier <= anomSellTier.GetValueAt(anomLastSellBar))
					sTier = 0;
			}
			if (sTier > 0)
				anomLastSellBar = CurrentBar;

			anomBuyTier[0]  = bTier;
			anomSellTier[0] = sTier;

			// ── Position: find actual price with peak buy/sell volume ──
			if (bTier > 0 || sTier > 0)
			{
				var    vol      = vbt.Volumes[CurrentBar];
				double high     = Highs[1][0];
				double low      = Lows[1][0];
				double step     = TickSize;

				long   maxAsk   = 0;
				double peakBuy  = (Closes[0][0] + Lows[0][0]) / 2.0;  // fallback
				long   maxBid   = 0;
				double peakSell = (Closes[0][0] + Highs[0][0]) / 2.0; // fallback

				for (double price = low; price <= high; price += step)
				{
					long askVol = vol.GetAskVolumeForPrice(price);
					long bidVol = vol.GetBidVolumeForPrice(price);

					if (askVol > maxAsk)
					{
						maxAsk  = askVol;
						peakBuy = price;
					}
					if (bidVol > maxBid)
					{
						maxBid   = bidVol;
						peakSell = price;
					}
				}

				if (bTier > 0) anomBuyPosY[0]  = peakBuy;
				if (sTier > 0) anomSellPosY[0] = peakSell;
			}
		}

		#endregion

		#region OnRender — Volume Anomaly circles + panel

		protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
		{
			base.OnRender(chartControl, chartScale);

			if (!ShowAnomaly || Bars == null || ChartBars == null)
				return;

			EnsureAnomResources();

			int fromIdx = ChartBars.FromIndex;
			int toIdx   = Math.Min(ChartBars.ToIndex, CurrentBar);

			for (int i = fromIdx; i <= toIdx; i++)
			{
				float x = chartControl.GetXByBarIndex(ChartBars, i);

				int bt = anomBuyTier.GetValueAt(i);
				if (bt > 0)
				{
					double price = anomBuyPosY.GetValueAt(i);
					if (!double.IsNaN(price))
					{
						float y = chartScale.GetYByValue(price);
						float r = AnomTierRadius(bt);
						RenderTarget.FillEllipse(
							new Ellipse(new Vector2(x, y), r, r),
							dxAnomBuyBrush);
					}
				}

				int st = anomSellTier.GetValueAt(i);
				if (st > 0)
				{
					double price = anomSellPosY.GetValueAt(i);
					if (!double.IsNaN(price))
					{
						float y = chartScale.GetYByValue(price);
						float r = AnomTierRadius(st);
						RenderTarget.FillEllipse(
							new Ellipse(new Vector2(x, y), r, r),
							dxAnomSellBrush);
					}
				}
			}

			if (AnomShowPanel)
				DrawAnomPanel();
		}

		public override void OnRenderTargetChanged()
		{
			DisposeAnomBrushes();
		}

		private void DrawAnomPanel()
		{
			if (fmtAnomPanel == null) return;

			float x = 10;
			float y = AnomPanelOffsetY;

			string seg1 = string.Format("ANOMALY {0:F1}\u03C3", AnomStrength);
			string seg2 = " | ";
			string seg3 = string.Format("BUY {0:F0}", lastBuyVol);
			string seg4 = " | ";
			string seg5 = string.Format("SELL {0:F0}", lastSellVol);

			x = DrawAnomPanelSegment(x, y, seg1, dxAnomInfoBrush);
			x = DrawAnomPanelSegment(x, y, seg2, dxAnomInfoBrush);
			x = DrawAnomPanelSegment(x, y, seg3, dxAnomPanelBuyBrush);
			x = DrawAnomPanelSegment(x, y, seg4, dxAnomInfoBrush);
			    DrawAnomPanelSegment(x, y + 1, seg5, dxAnomPanelSellBrush);
		}

		private float DrawAnomPanelSegment(float x, float y, string text,
			SharpDX.Direct2D1.SolidColorBrush brush)
		{
			x = (float)Math.Round(x);
			using (var layout = new TextLayout(
				NinjaTrader.Core.Globals.DirectWriteFactory, text, fmtAnomPanel, 400, 20))
			{
				RenderTarget.DrawTextLayout(new Vector2(x, y), layout, brush);
				return x + layout.Metrics.WidthIncludingTrailingWhitespace;
			}
		}

		private float AnomTierRadius(int tier)
		{
			switch (tier)
			{
				case 1:  return AnomTier1Radius;
				case 2:  return AnomTier2Radius;
				case 3:  return AnomTier3Radius;
				default: return 0;
			}
		}

		private void EnsureAnomResources()
		{
			if (dxAnomBuyBrush == null || dxAnomBuyBrush.IsDisposed)
			{
				byte alpha = (byte)(AnomOpacity * 255 / 100);
				dxAnomBuyBrush      = CreateAnomDxBrush(AnomBuyColor, alpha);
				dxAnomSellBrush     = CreateAnomDxBrush(AnomSellColor, alpha);
				dxAnomInfoBrush     = CreateAnomDxBrush(AnomInfoFontColor, 220);
				dxAnomPanelBuyBrush = CreateAnomDxBrush(AnomPanelBuyColor, 220);
				dxAnomPanelSellBrush = CreateAnomDxBrush(AnomPanelSellColor, 220);
			}

			if (fmtAnomPanel == null || fmtAnomPanel.IsDisposed)
				fmtAnomPanel = new TextFormat(
					NinjaTrader.Core.Globals.DirectWriteFactory,
					"Segoe UI Light", AnomPanelFontSize);
		}

		private SharpDX.Direct2D1.SolidColorBrush CreateAnomDxBrush(
			System.Windows.Media.Brush wpf, byte alpha)
		{
			var sc = ((System.Windows.Media.SolidColorBrush)wpf).Color;
			return new SharpDX.Direct2D1.SolidColorBrush(RenderTarget,
				new SharpDX.Color(sc.R, sc.G, sc.B, alpha));
		}

		private void DisposeAnomBrushes()
		{
			if (dxAnomBuyBrush       != null) { dxAnomBuyBrush.Dispose();       dxAnomBuyBrush       = null; }
			if (dxAnomSellBrush      != null) { dxAnomSellBrush.Dispose();      dxAnomSellBrush      = null; }
			if (dxAnomInfoBrush      != null) { dxAnomInfoBrush.Dispose();      dxAnomInfoBrush      = null; }
			if (dxAnomPanelBuyBrush  != null) { dxAnomPanelBuyBrush.Dispose();  dxAnomPanelBuyBrush  = null; }
			if (dxAnomPanelSellBrush != null) { dxAnomPanelSellBrush.Dispose(); dxAnomPanelSellBrush = null; }
		}

		private void DisposeAnomResources()
		{
			DisposeAnomBrushes();
			if (fmtAnomPanel != null) { fmtAnomPanel.Dispose(); fmtAnomPanel = null; }
		}

		#endregion

		#region Helpers

		private void BuildPalette()
		{
			var buyC  = GetColor(StrongBuyColor);
			var sellC = GetColor(StrongSellColor);
			var neutC = GetColor(NeutralColor);

			palette = new System.Windows.Media.SolidColorBrush[41];
			for (int i = 0; i <= 40; i++)
			{
				double t = (i - 20) / 20.0;
				System.Windows.Media.Color c = t >= 0
					? LerpColor(neutC, buyC, t)
					: LerpColor(neutC, sellC, -t);

				var b = new System.Windows.Media.SolidColorBrush(c);
				b.Freeze();
				palette[i] = b;
			}
		}

		private System.Windows.Media.SolidColorBrush GetPaletteBrush(double ratio)
		{
			int idx = (int)Math.Round(ratio * 20) + 20;
			return palette[Math.Max(0, Math.Min(40, idx))];
		}

		private static System.Windows.Media.Color LerpColor(
			System.Windows.Media.Color a, System.Windows.Media.Color b, double t)
		{
			if (t <= 0) return a;
			if (t >= 1) return b;
			return System.Windows.Media.Color.FromArgb(255,
				(byte)(a.R + (b.R - a.R) * t),
				(byte)(a.G + (b.G - a.G) * t),
				(byte)(a.B + (b.B - a.B) * t));
		}

		private static System.Windows.Media.Color GetColor(System.Windows.Media.Brush brush)
		{
			return brush is System.Windows.Media.SolidColorBrush scb
				? scb.Color : System.Windows.Media.Colors.Gray;
		}

		private static System.Windows.Media.SolidColorBrush MkBrush(byte r, byte g, byte b)
		{
			var brush = new System.Windows.Media.SolidColorBrush(
				System.Windows.Media.Color.FromRgb(r, g, b));
			brush.Freeze();
			return brush;
		}

		private static System.Windows.Media.Brush CloneFreeze(System.Windows.Media.Brush source)
		{
			var b = source.Clone();
			b.Freeze();
			return b;
		}

		internal static PlotStyle ToPlotStyle(LociMarkerShape shape, bool isSupport)
		{
			switch (shape)
			{
				case LociMarkerShape.Dot:          return PlotStyle.Dot;
				case LociMarkerShape.Square:       return PlotStyle.Square;
				case LociMarkerShape.Block:        return PlotStyle.Block;
				case LociMarkerShape.Hash:         return PlotStyle.Hash;
				case LociMarkerShape.Cross:        return PlotStyle.Cross;
				case LociMarkerShape.TriangleUp:   return PlotStyle.TriangleUp;
				case LociMarkerShape.TriangleDown: return PlotStyle.TriangleDown;
				case LociMarkerShape.Triangle:
					return isSupport ? PlotStyle.TriangleUp : PlotStyle.TriangleDown;
				default: return PlotStyle.Dot;
			}
		}

		#endregion

		#region Properties

		// ═══════════════════════════════════════════════════════════
		//  1. Delta Heatmap
		// ═══════════════════════════════════════════════════════════

		[Display(Name = "Увімкнути Delta Heatmap",
			GroupName = "1. Delta Heatmap", Order = 0)]
		public bool ShowHeatmap { get; set; }

		[Range(1, 500)]
		[Display(Name = "Згладжування (барів)",
			Description = "Період WMA для згладження delta",
			GroupName = "1. Delta Heatmap", Order = 1)]
		public int Smoothing { get; set; }

		[Range(0.1, 10.0)]
		[Display(Name = "Шкала градієнту",
			Description = "Множник знаменника (більше = блідіший)",
			GroupName = "1. Delta Heatmap", Order = 2)]
		public double GradientScale { get; set; }

		[Range(0.0, 0.99)]
		[Display(Name = "Мертва зона (0 = вимк.)",
			Description = "Ratio нижче цього = нейтральний колір",
			GroupName = "1. Delta Heatmap", Order = 3)]
		public double DeadZone { get; set; }

		[XmlIgnore]
		[Display(Name = "Сильна купівля",
			GroupName = "1. Delta Heatmap", Order = 4)]
		public System.Windows.Media.Brush StrongBuyColor { get; set; }
		[Browsable(false)]
		public string StrongBuyColorSerializable
		{ get { return Serialize.BrushToString(StrongBuyColor); } set { StrongBuyColor = Serialize.StringToBrush(value); } }

		[XmlIgnore]
		[Display(Name = "Сильний продаж",
			GroupName = "1. Delta Heatmap", Order = 5)]
		public System.Windows.Media.Brush StrongSellColor { get; set; }
		[Browsable(false)]
		public string StrongSellColorSerializable
		{ get { return Serialize.BrushToString(StrongSellColor); } set { StrongSellColor = Serialize.StringToBrush(value); } }

		[XmlIgnore]
		[Display(Name = "Нейтральний",
			GroupName = "1. Delta Heatmap", Order = 6)]
		public System.Windows.Media.Brush NeutralColor { get; set; }
		[Browsable(false)]
		public string NeutralColorSerializable
		{ get { return Serialize.BrushToString(NeutralColor); } set { NeutralColor = Serialize.StringToBrush(value); } }

		// ═══════════════════════════════════════════════════════════
		//  2. Exhaustion
		// ═══════════════════════════════════════════════════════════

		[Display(Name = "Увімкнути Exhaustion",
			GroupName = "2. Exhaustion", Order = 0)]
		public bool ShowExhaustion { get; set; }

		[Display(Name = "Режим розрахунку",
			Description = "Bid, Ask, Bid+Ask або Volume",
			GroupName = "2. Exhaustion", Order = 1)]
		public ExhaustionCalcMode ExhCalcMode { get; set; }

		[Range(2, 50)]
		[Display(Name = "Кількість рівнів",
			Description = "Скільки цінових рівнів перевіряти від краю бару",
			GroupName = "2. Exhaustion", Order = 2)]
		public int ExhLevelCount { get; set; }

		[Range(1, 10)]
		[Display(Name = "Тіків на рівень",
			Description = "Агрегація per-price даних (NQ=2, ES=1). Має збігатись з StackImb/VP",
			GroupName = "2. Exhaustion", Order = 3)]
		public int ExhTicksPerLevel { get; set; }

		[Range(1, 50)]
		[Display(Name = "Зміщення (тіки)",
			GroupName = "2. Exhaustion", Order = 4)]
		public int ExhSignalOffset { get; set; }

		[XmlIgnore]
		[Display(Name = "Опір (виснаження купівлі)",
			GroupName = "2. Exhaustion", Order = 5)]
		public System.Windows.Media.Brush ExhResistanceColor { get; set; }
		[Browsable(false)]
		public string ExhResistanceColorSerialize
		{ get { return Serialize.BrushToString(ExhResistanceColor); } set { ExhResistanceColor = Serialize.StringToBrush(value); } }

		[XmlIgnore]
		[Display(Name = "Підтримка (виснаження продажу)",
			GroupName = "2. Exhaustion", Order = 6)]
		public System.Windows.Media.Brush ExhSupportColor { get; set; }
		[Browsable(false)]
		public string ExhSupportColorSerialize
		{ get { return Serialize.BrushToString(ExhSupportColor); } set { ExhSupportColor = Serialize.StringToBrush(value); } }

		[Display(Name = "Форма маркера",
			GroupName = "2. Exhaustion", Order = 7)]
		public LociMarkerShape ExhShape { get; set; }

		[Range(1, 20)]
		[Display(Name = "Розмір маркера",
			GroupName = "2. Exhaustion", Order = 8)]
		public int ExhMarkerSize { get; set; }

		// ═══════════════════════════════════════════════════════════
		//  3. Delta Turnaround
		// ═══════════════════════════════════════════════════════════

		[Display(Name = "Увімкнути Delta Turnaround",
			GroupName = "3. Delta Turnaround", Order = 0)]
		public bool ShowTurnaround { get; set; }

		[Range(1, 50)]
		[Display(Name = "Зміщення (тіки)",
			GroupName = "3. Delta Turnaround", Order = 1)]
		public int TurnSignalOffset { get; set; }

		[XmlIgnore]
		[Display(Name = "Ведмежий розворот",
			GroupName = "3. Delta Turnaround", Order = 2)]
		public System.Windows.Media.Brush TurnBearishColor { get; set; }
		[Browsable(false)]
		public string TurnBearishColorSerialize
		{ get { return Serialize.BrushToString(TurnBearishColor); } set { TurnBearishColor = Serialize.StringToBrush(value); } }

		[XmlIgnore]
		[Display(Name = "Бичачий розворот",
			GroupName = "3. Delta Turnaround", Order = 3)]
		public System.Windows.Media.Brush TurnBullishColor { get; set; }
		[Browsable(false)]
		public string TurnBullishColorSerialize
		{ get { return Serialize.BrushToString(TurnBullishColor); } set { TurnBullishColor = Serialize.StringToBrush(value); } }

		[Display(Name = "Форма маркера",
			Description = "Triangle = авто напрям (▲ bullish / ▼ bearish)",
			GroupName = "3. Delta Turnaround", Order = 4)]
		public LociMarkerShape TurnShape { get; set; }

		[Range(1, 20)]
		[Display(Name = "Розмір маркера",
			GroupName = "3. Delta Turnaround", Order = 5)]
		public int TurnMarkerSize { get; set; }

		// ═══════════════════════════════════════════════════════════
		//  4. Delta Strength
		// ═══════════════════════════════════════════════════════════

		[Display(Name = "Увімкнути Delta Strength",
			GroupName = "4. Delta Strength", Order = 0)]
		public bool ShowStrength { get; set; }

		[Range(1, 100)]
		[Display(Name = "Мінімум %",
			Description = "Мінімальний поріг delta/maxDelta для маркування",
			GroupName = "4. Delta Strength", Order = 1)]
		public int StrMinPercent { get; set; }

		[Range(1, 100)]
		[Display(Name = "Максимум %",
			Description = "Максимальний поріг (вище = клімакс, ігнорується)",
			GroupName = "4. Delta Strength", Order = 2)]
		public int StrMaxPercent { get; set; }

		[Range(1, 50)]
		[Display(Name = "Зміщення (тіки)",
			GroupName = "4. Delta Strength", Order = 3)]
		public int StrSignalOffset { get; set; }

		[XmlIgnore]
		[Display(Name = "Позитивний",
			GroupName = "4. Delta Strength", Order = 4)]
		public System.Windows.Media.Brush StrPositiveColor { get; set; }
		[Browsable(false)]
		public string StrPositiveColorSerialize
		{ get { return Serialize.BrushToString(StrPositiveColor); } set { StrPositiveColor = Serialize.StringToBrush(value); } }

		[XmlIgnore]
		[Display(Name = "Негативний",
			GroupName = "4. Delta Strength", Order = 5)]
		public System.Windows.Media.Brush StrNegativeColor { get; set; }
		[Browsable(false)]
		public string StrNegativeColorSerialize
		{ get { return Serialize.BrushToString(StrNegativeColor); } set { StrNegativeColor = Serialize.StringToBrush(value); } }

		[Display(Name = "Форма маркера",
			GroupName = "4. Delta Strength", Order = 6)]
		public LociMarkerShape StrShape { get; set; }

		[Range(1, 20)]
		[Display(Name = "Розмір маркера",
			GroupName = "4. Delta Strength", Order = 7)]
		public int StrMarkerSize { get; set; }

		// ═══════════════════════════════════════════════════════════
		//  5. Volume Anomaly
		// ═══════════════════════════════════════════════════════════

		[Display(Name = "Увімкнути Volume Anomaly",
			GroupName = "5. Volume Anomaly", Order = 0)]
		public bool ShowAnomaly { get; set; }

		[Range(2, 200)]
		[Display(Name = "Lookback",
			Description = "Кількість барів для розрахунку норми",
			GroupName = "5. Volume Anomaly", Order = 1)]
		public int AnomLookback { get; set; }

		[Range(0.5, 10.0)]
		[Display(Name = "Сила (σ)",
			Description = "Tier1 = Nσ, Tier2 = 1.5Nσ, Tier3 = 2Nσ. Більше = менше сигналів",
			GroupName = "5. Volume Anomaly", Order = 2)]
		public double AnomStrength { get; set; }

		[Range(0, 50)]
		[Display(Name = "Cooldown (bars)",
			Description = "Мін барів між сигналами. 0 = без обмеження",
			GroupName = "5. Volume Anomaly", Order = 3)]
		public int AnomCooldown { get; set; }

		[XmlIgnore]
		[Display(Name = "Buy",
			GroupName = "5. Volume Anomaly", Order = 4)]
		public System.Windows.Media.Brush AnomBuyColor { get; set; }
		[Browsable(false)]
		public string AnomBuyColorSerialize
		{ get { return Serialize.BrushToString(AnomBuyColor); } set { AnomBuyColor = Serialize.StringToBrush(value); } }

		[XmlIgnore]
		[Display(Name = "Sell",
			GroupName = "5. Volume Anomaly", Order = 5)]
		public System.Windows.Media.Brush AnomSellColor { get; set; }
		[Browsable(false)]
		public string AnomSellColorSerialize
		{ get { return Serialize.BrushToString(AnomSellColor); } set { AnomSellColor = Serialize.StringToBrush(value); } }

		[Range(10, 100)]
		[Display(Name = "Opacity %",
			GroupName = "5. Volume Anomaly", Order = 6)]
		public int AnomOpacity { get; set; }

		[Range(2, 30)]
		[Display(Name = "Tier 1 (px)",
			Description = "Радіус бульбашки Tier 1",
			GroupName = "5. Volume Anomaly", Order = 7)]
		public int AnomTier1Radius { get; set; }

		[Range(2, 40)]
		[Display(Name = "Tier 2 (px)",
			Description = "Радіус бульбашки Tier 2",
			GroupName = "5. Volume Anomaly", Order = 8)]
		public int AnomTier2Radius { get; set; }

		[Range(4, 50)]
		[Display(Name = "Tier 3 (px)",
			Description = "Радіус бульбашки Tier 3",
			GroupName = "5. Volume Anomaly", Order = 9)]
		public int AnomTier3Radius { get; set; }

		[Display(Name = "Показувати панель",
			GroupName = "5. Volume Anomaly", Order = 10)]
		public bool AnomShowPanel { get; set; }

		[Range(0, 200)]
		[Display(Name = "Панель Y-зміщення (px)",
			Description = "Відступ зверху. 28 = під GEX панеллю",
			GroupName = "5. Volume Anomaly", Order = 11)]
		public int AnomPanelOffsetY { get; set; }

		[XmlIgnore]
		[Display(Name = "Колір панелі",
			GroupName = "5. Volume Anomaly", Order = 12)]
		public System.Windows.Media.Brush AnomInfoFontColor { get; set; }
		[Browsable(false)]
		public string AnomInfoFontColorSerialize
		{ get { return Serialize.BrushToString(AnomInfoFontColor); } set { AnomInfoFontColor = Serialize.StringToBrush(value); } }

		[XmlIgnore]
		[Display(Name = "Панель Buy",
			GroupName = "5. Volume Anomaly", Order = 13)]
		public System.Windows.Media.Brush AnomPanelBuyColor { get; set; }
		[Browsable(false)]
		public string AnomPanelBuyColorSerialize
		{ get { return Serialize.BrushToString(AnomPanelBuyColor); } set { AnomPanelBuyColor = Serialize.StringToBrush(value); } }

		[XmlIgnore]
		[Display(Name = "Панель Sell",
			GroupName = "5. Volume Anomaly", Order = 14)]
		public System.Windows.Media.Brush AnomPanelSellColor { get; set; }
		[Browsable(false)]
		public string AnomPanelSellColorSerialize
		{ get { return Serialize.BrushToString(AnomPanelSellColor); } set { AnomPanelSellColor = Serialize.StringToBrush(value); } }

		[Range(8, 18)]
		[Display(Name = "Шрифт панелі (pt)",
			GroupName = "5. Volume Anomaly", Order = 15)]
		public int AnomPanelFontSize { get; set; }

		#endregion
	}

	#region Enums

	public enum ExhaustionCalcMode { Bid, Ask, BidAndAsk, Volume }
	public enum LociMarkerShape   { Dot, Square, Block, Hash, Cross, TriangleUp, TriangleDown, Triangle }

	#endregion
}
