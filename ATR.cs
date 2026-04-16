#region Using declarations
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
#endregion

namespace NinjaTrader.NinjaScript.Indicators.Loci
{
	public class ATR : Indicator
	{
		private double atrValue;

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description					= "Multi-Timeframe ATR — значення ATR з обраного таймфрейму";
				Name						= "ATR";
				IsSuspendedWhileInactive	= true;
				IsOverlay					= false;
				Calculate					= Calculate.OnBarClose;

				Period						= 10;
				MtfType						= BarsPeriodType.Minute;
				MtfValue					= 1;
				ShowInTicks					= false;

				AddPlot(new Stroke(new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x3D, 0x00, 0x24)), 2), PlotStyle.Line, "ATR");
			}
			else if (State == State.Configure)
			{
				AddDataSeries(MtfType, MtfValue);
			}
		}

		protected override void OnBarUpdate()
		{
			// Розрахунок ATR тільки на вторинній серії
			if (BarsInProgress == 1)
			{
				double high0 = Highs[1][0];
				double low0  = Lows[1][0];

				if (CurrentBars[1] == 0)
				{
					atrValue = high0 - low0;
				}
				else
				{
					double close1    = Closes[1][1];
					double trueRange = Math.Max(Math.Abs(low0 - close1), Math.Max(high0 - low0, Math.Abs(high0 - close1)));
					int    count     = Math.Min(CurrentBars[1] + 1, Period);
					atrValue         = ((count - 1) * atrValue + trueRange) / count;
				}
			}

			// Малюємо на основному чарті
			if (BarsInProgress == 0 && CurrentBars[1] >= 0)
			{
				Value[0] = ShowInTicks ? atrValue / TickSize : atrValue;
			}
		}

		#region Properties
		[Range(1, int.MaxValue)]
		[Display(Name = "Період", GroupName = "Параметри", Order = 0)]
		public int Period { get; set; }

		[Display(Name = "Тип бару", GroupName = "Таймфрейм", Order = 1)]
		public BarsPeriodType MtfType { get; set; }

		[Range(1, int.MaxValue)]
		[Display(Name = "Значення", GroupName = "Таймфрейм", Order = 2)]
		public int MtfValue { get; set; }

		[Display(Name = "Показати в тіках", GroupName = "Параметри", Order = 3)]
		public bool ShowInTicks { get; set; }
		#endregion
	}
}
