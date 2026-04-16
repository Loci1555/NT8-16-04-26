using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using SharpDX;
using SharpDX.DirectWrite;

namespace NinjaTrader.NinjaScript.Indicators.Loci
{
	public class GEXLevelsES : Indicator
	{
		private struct GexLevel
		{
			public double EsPrice;
			public int    SpxStrike;
			public int    Tier;
		}

		#region Поля

		private readonly List<GexLevel> levels = new List<GexLevel>();
		private bool needsRecalc = true;

		private SharpDX.Direct2D1.Brush		dxBrush100;
		private SharpDX.Direct2D1.Brush		dxBrush50;
		private SharpDX.Direct2D1.Brush		dxBrush25;
		private SharpDX.Direct2D1.Brush		dxLabelBrush;
		private SharpDX.Direct2D1.Brush		dxInfoBrush;
		private SharpDX.DirectWrite.TextFormat	labelFormat;
		private SharpDX.DirectWrite.TextFormat	infoFormat;

		private const int RangePoints = 200;

		#endregion

		#region Параметри — Конвертація

		[NinjaScriptProperty]
		[Display(Name = "ES Close", Order = 0, GroupName = "1. Конвертація")]
		public double EsClose { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "SPX Close", Order = 1, GroupName = "1. Конвертація")]
		public double SpxClose { get; set; }

		#endregion

		#region Параметри — SPX 100s

		[XmlIgnore]
		[Display(Name = "Колір", Order = 0, GroupName = "2. SPX 100s")]
		public Brush Color100 { get; set; }
		[Browsable(false)]
		public string Color100Serialize
		{
			get { return Serialize.BrushToString(Color100); }
			set { Color100 = Serialize.StringToBrush(value); }
		}

		[NinjaScriptProperty]
		[Display(Name = "Тип лінії", Order = 1, GroupName = "2. SPX 100s")]
		public DashStyleHelper DashStyle100 { get; set; }

		[NinjaScriptProperty]
		[Range(1, 5)]
		[Display(Name = "Товщина", Order = 2, GroupName = "2. SPX 100s")]
		public int Width100 { get; set; }

		#endregion

		#region Параметри — SPX 50s

		[XmlIgnore]
		[Display(Name = "Колір", Order = 0, GroupName = "3. SPX 50s")]
		public Brush Color50 { get; set; }
		[Browsable(false)]
		public string Color50Serialize
		{
			get { return Serialize.BrushToString(Color50); }
			set { Color50 = Serialize.StringToBrush(value); }
		}

		[NinjaScriptProperty]
		[Display(Name = "Тип лінії", Order = 1, GroupName = "3. SPX 50s")]
		public DashStyleHelper DashStyle50 { get; set; }

		[NinjaScriptProperty]
		[Range(1, 5)]
		[Display(Name = "Товщина", Order = 2, GroupName = "3. SPX 50s")]
		public int Width50 { get; set; }

		#endregion

		#region Параметри — SPX 25s

		[NinjaScriptProperty]
		[Display(Name = "Показати", Order = 0, GroupName = "4. SPX 25s")]
		public bool Show25 { get; set; }

		[XmlIgnore]
		[Display(Name = "Колір", Order = 1, GroupName = "4. SPX 25s")]
		public Brush Color25 { get; set; }
		[Browsable(false)]
		public string Color25Serialize
		{
			get { return Serialize.BrushToString(Color25); }
			set { Color25 = Serialize.StringToBrush(value); }
		}

		[NinjaScriptProperty]
		[Display(Name = "Тип лінії", Order = 2, GroupName = "4. SPX 25s")]
		public DashStyleHelper DashStyle25 { get; set; }

		[NinjaScriptProperty]
		[Range(1, 5)]
		[Display(Name = "Товщина", Order = 3, GroupName = "4. SPX 25s")]
		public int Width25 { get; set; }

		#endregion

		#region Параметри — Лейбли

		[XmlIgnore]
		[Display(Name = "Колір", Order = 0, GroupName = "5. Лейбли")]
		public Brush LabelColor { get; set; }
		[Browsable(false)]
		public string LabelColorSerialize
		{
			get { return Serialize.BrushToString(LabelColor); }
			set { LabelColor = Serialize.StringToBrush(value); }
		}

		[NinjaScriptProperty]
		[Range(6, 24)]
		[Display(Name = "Розмір шрифту", Order = 1, GroupName = "5. Лейбли")]
		public int LabelFontSize { get; set; }

		#endregion

		#region OnStateChange

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description			= "GEX рівні SPX на графіку ES";
				Name				= "GEX Levels ES";
				IsOverlay			= true;
				IsSuspendedWhileInactive	= true;
				DisplayInDataBox		= false;
				DrawOnPricePanel		= true;

				EsClose				= 6102.00;
				SpxClose			= 6090.00;

				// 100s
				Color100 = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0xFF, 0x3F, 0x3F, 0x3F));
				Color100.Freeze();
				DashStyle100			= DashStyleHelper.Solid;
				Width100			= 1;

				// 50s
				Color50 = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0xFF, 0x3F, 0x3F, 0x3F));
				Color50.Freeze();
				DashStyle50			= DashStyleHelper.Solid;
				Width50				= 1;

				// 25s
				Show25				= false;
				Color25 = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0xFF, 0x3F, 0x3F, 0x3F));
				Color25.Freeze();
				DashStyle25			= DashStyleHelper.Solid;
				Width25				= 1;

				// Лейбли
				LabelColor = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0xFF, 0x3F, 0x3F, 0x3F));
				LabelColor.Freeze();
				LabelFontSize			= 12;
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
			if (needsRecalc)
			{
				RecalculateLevels();
				needsRecalc = false;
			}
		}

		#endregion

		#region Рендеринг

		public override void OnRenderTargetChanged()
		{
			DisposeResources();
		}

		protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
		{
			base.OnRender(chartControl, chartScale);

			if (needsRecalc || levels.Count == 0)
			{
				RecalculateLevels();
				needsRecalc = false;
			}

			if (RenderTarget == null || levels.Count == 0) return;

			EnsureResources(chartControl);

			float panelW = (float)ChartPanel.W;
			float minY   = 0;
			float maxY   = (float)chartScale.Height;

			foreach (var level in levels)
			{
				if (level.Tier == 2 && !Show25) continue;

				float y = chartScale.GetYByValue(level.EsPrice);
				if (y < minY - 20 || y > maxY + 20) continue;

				SharpDX.Direct2D1.Brush brush;
				float width;
				DashStyleHelper dashHelper;

				switch (level.Tier)
				{
					case 0:  brush = dxBrush100; width = Width100; dashHelper = DashStyle100; break;
					case 1:  brush = dxBrush50;  width = Width50;  dashHelper = DashStyle50;  break;
					default: brush = dxBrush25;  width = Width25;  dashHelper = DashStyle25;  break;
				}

				if (brush == null) continue;

				// Лінія
				var style = CreateStrokeStyle(dashHelper, width);
				if (style != null)
				{
					RenderTarget.DrawLine(
						new Vector2(0, y), new Vector2(panelW, y),
						brush, width, style);
					style.Dispose();
				}
				else
				{
					RenderTarget.DrawLine(
						new Vector2(0, y), new Vector2(panelW, y),
						brush, width);
				}

				// Лейбл SPX страйка — справа, над лінією
				string label = level.SpxStrike.ToString();
				using (var layout = new TextLayout(NinjaTrader.Core.Globals.DirectWriteFactory,
					label, labelFormat, 100, 30))
				{
					float labelX = panelW - layout.Metrics.Width - 5;
					RenderTarget.DrawTextLayout(new Vector2(labelX, y - layout.Metrics.Height), layout, dxLabelBrush);
				}
			}

			DrawInfoBlock();
		}

		private void DrawInfoBlock()
		{
			double prem = EsClose - SpxClose;
			string info = $"GEX | Premium: {prem:F2}";

			using (var layout = new TextLayout(NinjaTrader.Core.Globals.DirectWriteFactory,
				info, infoFormat, 400, 20))
			{
				RenderTarget.DrawTextLayout(new Vector2(10, 10), layout, dxInfoBrush);
			}
		}

		private SharpDX.Direct2D1.StrokeStyle CreateStrokeStyle(DashStyleHelper helper, float width)
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
				case DashStyleHelper.Dash:	 ds = SharpDX.Direct2D1.DashStyle.Dash; break;
				case DashStyleHelper.DashDot:	 ds = SharpDX.Direct2D1.DashStyle.DashDot; break;
				case DashStyleHelper.DashDotDot: ds = SharpDX.Direct2D1.DashStyle.DashDotDot; break;
				default:			 ds = SharpDX.Direct2D1.DashStyle.Solid; break;
			}

			return new SharpDX.Direct2D1.StrokeStyle(
				NinjaTrader.Core.Globals.D2DFactory,
				new SharpDX.Direct2D1.StrokeStyleProperties { DashStyle = ds });
		}

		#endregion

		#region Розрахунок рівнів

		private void RecalculateLevels()
		{
			levels.Clear();
			if (EsClose <= 0 || SpxClose <= 0) return;

			int spxCenter = (int)(Math.Round(SpxClose / 100.0) * 100);
			double prem   = EsClose - SpxClose;

			for (int strike = spxCenter - RangePoints; strike <= spxCenter + RangePoints; strike += 25)
			{
				int tier;
				if (strike % 100 == 0)		tier = 0;
				else if (strike % 50 == 0)	tier = 1;
				else				tier = 2;

				levels.Add(new GexLevel
				{
					EsPrice   = strike + prem,
					SpxStrike = strike,
					Tier      = tier
				});
			}
		}

		#endregion

		#region SharpDX ресурси

		private void EnsureResources(ChartControl chartControl)
		{
			if (dxBrush100 == null || dxBrush100.IsDisposed)
				dxBrush100 = Color100.ToDxBrush(RenderTarget);

			if (dxBrush50 == null || dxBrush50.IsDisposed)
				dxBrush50 = Color50.ToDxBrush(RenderTarget);

			if (dxBrush25 == null || dxBrush25.IsDisposed)
				dxBrush25 = Color25.ToDxBrush(RenderTarget);

			if (dxLabelBrush == null || dxLabelBrush.IsDisposed)
				dxLabelBrush = LabelColor.ToDxBrush(RenderTarget);

			if (dxInfoBrush == null || dxInfoBrush.IsDisposed)
				dxInfoBrush = chartControl.Properties.ChartText.ToDxBrush(RenderTarget);

			if (labelFormat == null || labelFormat.IsDisposed)
				labelFormat = new TextFormat(NinjaTrader.Core.Globals.DirectWriteFactory, "Segoe UI Light", LabelFontSize);

			if (infoFormat == null || infoFormat.IsDisposed)
				infoFormat = new TextFormat(NinjaTrader.Core.Globals.DirectWriteFactory, "Segoe UI Light", 12f);
		}

		private void DisposeResources()
		{
			if (dxBrush100 != null)	  { dxBrush100.Dispose();   dxBrush100 = null; }
			if (dxBrush50 != null)	  { dxBrush50.Dispose();    dxBrush50 = null; }
			if (dxBrush25 != null)	  { dxBrush25.Dispose();    dxBrush25 = null; }
			if (dxLabelBrush != null)  { dxLabelBrush.Dispose();  dxLabelBrush = null; }
			if (dxInfoBrush != null)  { dxInfoBrush.Dispose();  dxInfoBrush = null; }
			if (labelFormat != null)  { labelFormat.Dispose();   labelFormat = null; }
			if (infoFormat != null)	  { infoFormat.Dispose();    infoFormat = null; }
		}

		#endregion
	}
}
