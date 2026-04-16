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
	public class ClusterStats : Indicator
	{
		private NinjaTrader.NinjaScript.Indicators.OrderFlowCumulativeDelta cumulativeDelta;
		private Series<double> absDeltaSeries;
		private SolidColorBrush[] palette;

		// Separator
		private SharpDX.Direct2D1.Brush dxSepBrush;

		#region OnStateChange

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description			= "Кольорова смужка дельти (Cluster Statistics)";
				Name				= "Cluster Stats";
				IsOverlay			= false;
				IsSuspendedWhileInactive	= true;
				DisplayInDataBox		= false;
				DrawOnPricePanel		= false;
				Calculate			= Calculate.OnEachTick;
				IsAutoScale			= false;
				PaintPriceMarkers		= false;

				Lookback			= 10;
				GradientPower		= 1.0;

				StrongBuyColor  = MkBrush(0x00, 0x50, 0x8E);
				StrongSellColor = MkBrush(0x71, 0x00, 0x43);
				NeutralColor    = MkBrush(0x00, 0x00, 0x00);

				SeparatorColor = MkBrush(0x00, 0x00, 0x00);

				AddPlot(Brushes.Transparent, "Hidden");
			}
			else if (State == State.Configure)
			{
				AddDataSeries(BarsPeriodType.Tick, 1);
			}
			else if (State == State.DataLoaded)
			{
				cumulativeDelta = OrderFlowCumulativeDelta(
					CumulativeDeltaType.BidAsk,
					CumulativeDeltaPeriod.Session,
					0);
				absDeltaSeries = new Series<double>(this);
				BuildPalette();
			}
			else if (State == State.Terminated)
			{
				if (dxSepBrush != null) { dxSepBrush.Dispose(); dxSepBrush = null; }
			}
		}

		#endregion

		#region OnBarUpdate

		protected override void OnBarUpdate()
		{
			if (BarsInProgress == 1) return;
			if (CurrentBar < 1) return;

			double barDelta = cumulativeDelta.DeltaClose[0] - cumulativeDelta.DeltaOpen[0];
			absDeltaSeries[0] = Math.Abs(barDelta);

			Value[0] = 0;

			double maxAbs = 1;
			int count = Math.Min(Lookback, CurrentBar + 1);
			for (int i = 0; i < count; i++)
			{
				double d = absDeltaSeries[i];
				if (d > maxAbs) maxAbs = d;
			}

			double ratio = barDelta / maxAbs;
			ratio = Math.Max(-1.0, Math.Min(1.0, ratio));

			BackBrushes[0] = GetPaletteBrush(ratio);
		}

		#endregion

		#region OnRender — сепаратори

		public override void OnRenderTargetChanged()
		{
			if (dxSepBrush != null) { dxSepBrush.Dispose(); dxSepBrush = null; }
		}

		protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
		{
			base.OnRender(chartControl, chartScale);

			if (RenderTarget == null || ChartBars == null) return;

			if (dxSepBrush == null || dxSepBrush.IsDisposed)
				dxSepBrush = SeparatorColor.ToDxBrush(RenderTarget);

			int firstBar = ChartBars.FromIndex;
			int lastBar  = ChartBars.ToIndex;
			float panelH = (float)ChartPanel.H;

			for (int i = firstBar; i < lastBar; i++)
			{
				float x1 = chartControl.GetXByBarIndex(ChartBars, i);
				float x2 = chartControl.GetXByBarIndex(ChartBars, i + 1);
				float mid = (x1 + x2) * 0.5f;

				RenderTarget.DrawLine(
					new Vector2(mid, 0),
					new Vector2(mid, panelH),
					dxSepBrush, 2f);
			}
		}

		#endregion

		#region Палітра

		private void BuildPalette()
		{
			var buyC  = GetColor(StrongBuyColor);
			var sellC = GetColor(StrongSellColor);
			var neutC = GetColor(NeutralColor);

			palette = new SolidColorBrush[41];
			for (int i = 0; i <= 40; i++)
			{
				double t = (i - 20) / 20.0;
				System.Windows.Media.Color c;

				if (t >= 0)
					c = LerpColor(neutC, buyC, Math.Pow(t, GradientPower));
				else
					c = LerpColor(neutC, sellC, Math.Pow(-t, GradientPower));

				var b = new SolidColorBrush(c);
				b.Freeze();
				palette[i] = b;
			}
		}

		private SolidColorBrush GetPaletteBrush(double ratio)
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

		private static System.Windows.Media.Color GetColor(Brush brush)
		{
			return brush is SolidColorBrush scb
				? scb.Color : System.Windows.Media.Colors.Gray;
		}

		private static SolidColorBrush MkBrush(byte r, byte g, byte b)
		{
			var brush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
			brush.Freeze();
			return brush;
		}

		#endregion

		#region Параметри

		[Range(5, 500)]
		[Display(Name = "Lookback",
			Description = "Період для нормалізації градієнту",
			GroupName = "1. Параметри", Order = 0)]
		public int Lookback { get; set; }

		[Range(0.3, 3.0)]
		[Display(Name = "Крива градієнту",
			Description = "1.0 = лінійний, <1 = швидше насичується, >1 = повільніше",
			GroupName = "1. Параметри", Order = 1)]
		public double GradientPower { get; set; }

		[XmlIgnore]
		[Display(Name = "Сильна купівля", GroupName = "2. Кольори", Order = 0)]
		public Brush StrongBuyColor { get; set; }
		[Browsable(false)]
		public string StrongBuyColorSerialize
		{
			get { return Serialize.BrushToString(StrongBuyColor); }
			set { StrongBuyColor = Serialize.StringToBrush(value); }
		}

		[XmlIgnore]
		[Display(Name = "Сильний продаж", GroupName = "2. Кольори", Order = 1)]
		public Brush StrongSellColor { get; set; }
		[Browsable(false)]
		public string StrongSellColorSerialize
		{
			get { return Serialize.BrushToString(StrongSellColor); }
			set { StrongSellColor = Serialize.StringToBrush(value); }
		}

		[XmlIgnore]
		[Display(Name = "Нейтральний", GroupName = "2. Кольори", Order = 2)]
		public Brush NeutralColor { get; set; }
		[Browsable(false)]
		public string NeutralColorSerialize
		{
			get { return Serialize.BrushToString(NeutralColor); }
			set { NeutralColor = Serialize.StringToBrush(value); }
		}

		[XmlIgnore]
		[Display(Name = "Сепаратор", GroupName = "2. Кольори", Order = 3)]
		public Brush SeparatorColor { get; set; }
		[Browsable(false)]
		public string SeparatorColorSerialize
		{
			get { return Serialize.BrushToString(SeparatorColor); }
			set { SeparatorColor = Serialize.StringToBrush(value); }
		}

		#endregion
	}
}
