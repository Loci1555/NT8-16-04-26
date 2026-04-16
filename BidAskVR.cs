using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Data;
using SharpDX;

namespace NinjaTrader.NinjaScript.Indicators.Loci
{
	public class BidAskVR : Indicator
	{
		private NinjaTrader.NinjaScript.Indicators.OrderFlowCumulativeDelta cumulativeDelta;
		private Series<double> rawVR;
		private double emaValue;
		private double prevEma;
		private double lastValue = double.NaN;
		private int    lastColorIdx;

		// SharpDX ресурси
		private SharpDX.Direct2D1.Brush dxLeaderBrush;

		#region OnStateChange

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description			= "Bid/Ask Volume Ratio — EMA згладжений, шкала -100..+100";
				Name				= "Bid Ask VR";
				IsOverlay			= false;
				IsSuspendedWhileInactive	= true;
				DisplayInDataBox		= true;
				DrawOnPricePanel		= false;
				Calculate			= Calculate.OnEachTick;

				Period				= 10;

				UpperColor = MkBrush(0x00, 0x50, 0x8E);   // синій (+ росте)
				UpColor    = MkBrush(0x00, 0x23, 0x3D);   // темний синій (+ падає)
				LowerColor = MkBrush(0x71, 0x00, 0x43);   // рожевий (- падає)
				LowColor   = MkBrush(0x3D, 0x00, 0x24);   // темний рожевий (- росте)

				ShowLeaderLine		= true;
				LeaderDashStyle		= DashStyleHelper.Dash;

				AddPlot(new Stroke(Brushes.DodgerBlue, 4), PlotStyle.Bar, "VR");
			}
			else if (State == State.Configure)
			{
				AddDataSeries(BarsPeriodType.Tick, 1);
				AddLine(new Stroke(new SolidColorBrush(
					System.Windows.Media.Color.FromArgb(0x50, 0x80, 0x80, 0x80)), 1), 0, "Zero");
			}
			else if (State == State.DataLoaded)
			{
				cumulativeDelta = OrderFlowCumulativeDelta(
					CumulativeDeltaType.BidAsk,
					CumulativeDeltaPeriod.Session,
					0);
				rawVR = new Series<double>(this);
				emaValue = 0;
				prevEma  = 0;
			}
			else if (State == State.Terminated)
			{
				DisposeResources();
			}
		}

		#endregion

		#region OnBarUpdate

		protected override void OnBarUpdate()
		{
			if (BarsInProgress == 1) return;
			if (CurrentBar < 1) return;

			// ── Raw VR: 100 × (Ask - Bid) / (Ask + Bid) ──
			double ask = cumulativeDelta.DeltaClose[0] - cumulativeDelta.DeltaOpen[0];
			double vol = Volume[0];

			// ask = barDelta (buy - sell), bid = vol - ask приблизно
			// Точніше: Ask vol = (vol + delta) / 2, Bid vol = (vol - delta) / 2
			double askVol = (vol + ask) / 2.0;
			double bidVol = (vol - ask) / 2.0;
			double total  = askVol + bidVol;

			rawVR[0] = total > 0 ? 100.0 * (askVol - bidVol) / total : 0;

			// Зберігаємо EMA завершеного бару перед розрахунком нового
			if (IsFirstTickOfBar)
				prevEma = emaValue;

			// ── EMA згладжування ──
			// Завжди рахуємо від prevEma (EMA завершеного бару),
			// щоб live тіки не "складались самі на себе"
			double k = CurrentBar < Period
				? 2.0 / (CurrentBar + 2)
				: 2.0 / (Period + 1);

			emaValue = k * rawVR[0] + (1.0 - k) * prevEma;

			Value[0] = emaValue;

			// ── 4-кольорова схема (momentum) ──
			Brush barBrush;
			if (emaValue > 0)
				barBrush = emaValue >= prevEma ? UpperColor : UpColor;
			else
				barBrush = emaValue <= prevEma ? LowerColor : LowColor;

			PlotBrushes[0][0] = barBrush;

			// Зберігаємо для leader line
			lastValue    = emaValue;
			lastColorIdx = emaValue > 0
				? (emaValue >= prevEma ? 0 : 1)
				: (emaValue <= prevEma ? 2 : 3);
		}

		#endregion

		#region Рендеринг — leader line

		public override void OnRenderTargetChanged()
		{
			DisposeResources();
		}

		protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
		{
			base.OnRender(chartControl, chartScale);

			if (RenderTarget == null || double.IsNaN(lastValue)) return;
			if (!ShowLeaderLine) return;

			float y = chartScale.GetYByValue(lastValue);
			float panelW = (float)ChartPanel.W;

			Brush wpfBrush;
			switch (lastColorIdx)
			{
				case 0:  wpfBrush = UpperColor; break;
				case 1:  wpfBrush = UpColor;    break;
				case 2:  wpfBrush = LowerColor; break;
				default: wpfBrush = LowColor;   break;
			}

			EnsureResources(chartControl, wpfBrush);

			var style = CreateStrokeStyle(LeaderDashStyle);
			if (style != null)
			{
				RenderTarget.DrawLine(
					new Vector2(0, y), new Vector2(panelW, y),
					dxLeaderBrush, 1f, style);
				style.Dispose();
			}
			else
			{
				RenderTarget.DrawLine(
					new Vector2(0, y), new Vector2(panelW, y),
					dxLeaderBrush, 1f);
			}
		}

		private SharpDX.Direct2D1.StrokeStyle CreateStrokeStyle(DashStyleHelper helper)
		{
			if (helper == DashStyleHelper.Solid)
				return null;

			if (helper == DashStyleHelper.Dot)
			{
				return new SharpDX.Direct2D1.StrokeStyle(
					NinjaTrader.Core.Globals.D2DFactory,
					new SharpDX.Direct2D1.StrokeStyleProperties
					{
						DashStyle = SharpDX.Direct2D1.DashStyle.Custom,
						DashCap   = SharpDX.Direct2D1.CapStyle.Round
					},
					new float[] { 0.5f, 3f });
			}

			SharpDX.Direct2D1.DashStyle ds;
			switch (helper)
			{
				case DashStyleHelper.Dash:       ds = SharpDX.Direct2D1.DashStyle.Dash; break;
				case DashStyleHelper.DashDot:    ds = SharpDX.Direct2D1.DashStyle.DashDot; break;
				case DashStyleHelper.DashDotDot: ds = SharpDX.Direct2D1.DashStyle.DashDotDot; break;
				default:                         ds = SharpDX.Direct2D1.DashStyle.Solid; break;
			}

			return new SharpDX.Direct2D1.StrokeStyle(
				NinjaTrader.Core.Globals.D2DFactory,
				new SharpDX.Direct2D1.StrokeStyleProperties { DashStyle = ds });
		}

		#endregion

		#region SharpDX ресурси

		private Brush lastWpfBrush;

		private void EnsureResources(ChartControl chartControl, Brush wpfBrush)
		{
			if (wpfBrush != lastWpfBrush)
			{
				if (dxLeaderBrush != null) { dxLeaderBrush.Dispose(); dxLeaderBrush = null; }
				lastWpfBrush = wpfBrush;
			}

			if (dxLeaderBrush == null || dxLeaderBrush.IsDisposed)
				dxLeaderBrush = wpfBrush.ToDxBrush(RenderTarget);
		}

		private void DisposeResources()
		{
			if (dxLeaderBrush != null) { dxLeaderBrush.Dispose(); dxLeaderBrush = null; }
			lastWpfBrush = null;
		}

		#endregion

		#region Helpers

		private static SolidColorBrush MkBrush(byte r, byte g, byte b)
		{
			var brush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
			brush.Freeze();
			return brush;
		}

		#endregion

		#region Параметри

		[Range(1, 500)]
		[Display(Name = "Період EMA", Order = 0, GroupName = "1. Параметри")]
		public int Period { get; set; }

		[XmlIgnore]
		[Display(Name = "Позитивний (росте)", Order = 1, GroupName = "2. Кольори")]
		public Brush UpperColor { get; set; }
		[Browsable(false)]
		public string UpperColorSerialize
		{
			get { return Serialize.BrushToString(UpperColor); }
			set { UpperColor = Serialize.StringToBrush(value); }
		}

		[XmlIgnore]
		[Display(Name = "Позитивний (падає)", Order = 2, GroupName = "2. Кольори")]
		public Brush UpColor { get; set; }
		[Browsable(false)]
		public string UpColorSerialize
		{
			get { return Serialize.BrushToString(UpColor); }
			set { UpColor = Serialize.StringToBrush(value); }
		}

		[XmlIgnore]
		[Display(Name = "Негативний (падає)", Order = 3, GroupName = "2. Кольори")]
		public Brush LowerColor { get; set; }
		[Browsable(false)]
		public string LowerColorSerialize
		{
			get { return Serialize.BrushToString(LowerColor); }
			set { LowerColor = Serialize.StringToBrush(value); }
		}

		[XmlIgnore]
		[Display(Name = "Негативний (росте)", Order = 4, GroupName = "2. Кольори")]
		public Brush LowColor { get; set; }
		[Browsable(false)]
		public string LowColorSerialize
		{
			get { return Serialize.BrushToString(LowColor); }
			set { LowColor = Serialize.StringToBrush(value); }
		}

		[Display(Name = "Показати лінію", Order = 0, GroupName = "3. Лінія значення")]
		public bool ShowLeaderLine { get; set; }

		[Display(Name = "Тип лінії", Order = 1, GroupName = "3. Лінія значення")]
		public DashStyleHelper LeaderDashStyle { get; set; }

		#endregion
	}
}
