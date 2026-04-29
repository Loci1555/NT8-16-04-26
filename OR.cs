#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.NinjaScript;
using SharpDX;
using SharpDX.Direct2D1;
#endregion

namespace NinjaTrader.NinjaScript.Indicators.Loci
{
    public class OR : Indicator
    {
        #region Private Classes

        private class ORData
        {
            public DateTime Start;
            public DateTime End;
            public double   High = double.MinValue;
            public double   Low  = double.MaxValue;
            public bool     Complete;
        }

        private class CloseData
        {
            public double Price;
            public int    StartBar;
            public int    EndBar;    // -1 = правий край
        }

        #endregion

        #region Private Fields

        private Dictionary<DateTime, ORData> orDays;
        private List<CloseData>              closeList;
        private DateTime                     prevBarTime;

        #endregion

        #region OnStateChange

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description      = "Opening Range + RTH Close";
                Name             = "OR";
                Calculate        = Calculate.OnEachTick;
                IsOverlay        = true;
                DisplayInDataBox = false;
                DrawOnPricePanel = true;
                DrawHorizontalGridLines = false;
                DrawVerticalGridLines   = false;
                PaintPriceMarkers       = false;
                ScaleJustification      = ScaleJustification.Right;
                IsAutoScale             = false;

                // ── 1. Opening Range ──
                OR_StartTime   = DateTime.Parse("16:30");
                OR_Duration    = 5;
                OR_Color       = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x87, 0x87, 0x35)); OR_Color.Freeze();
                OR_Opacity     = 0;
                OR_BorderWidth = 1;

                // ── 2. RTH Close ──
                EnableClose   = true;
                CloseTime     = DateTime.Parse("23:00");
                CloseStroke   = new Stroke(
                    new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x6F, 0x7D)),
                    DashStyleHelper.Dash, 1);
            }
            else if (State == State.DataLoaded)
            {
                orDays      = new Dictionary<DateTime, ORData>();
                closeList   = new List<CloseData>();
                prevBarTime = DateTime.MinValue;
            }
        }

        #endregion

        #region OnBarUpdate

        protected override void OnBarUpdate()
        {
            DateTime barTime = Time[0];
            DateTime date    = barTime.Date;

            // ══════════════════════════════════════════════════════════
            //  Opening Range (без змін)
            // ══════════════════════════════════════════════════════════

            DateTime orStart = new DateTime(date.Year, date.Month, date.Day,
                OR_StartTime.Hour, OR_StartTime.Minute, 0);
            DateTime orEnd = orStart.AddMinutes(OR_Duration);

            if (!orDays.ContainsKey(date))
            {
                orDays[date] = new ORData
                {
                    Start = orStart,
                    End   = orEnd
                };
            }

            ORData or = orDays[date];

            if (barTime >= orStart && barTime < orEnd)
            {
                if (High[0] > or.High) or.High = High[0];
                if (Low[0]  < or.Low)  or.Low  = Low[0];
            }
            else if (barTime >= orEnd && !or.Complete && or.High > double.MinValue)
            {
                or.Complete = true;
            }

            // ══════════════════════════════════════════════════════════
            //  RTH Close
            //  Детекція: попередній бар до CloseTime, поточний >= CloseTime
            //  На volume bars час нерівномірний, але перетин ловиться завжди.
            // ══════════════════════════════════════════════════════════

            if (!EnableClose || !IsFirstTickOfBar || CurrentBar < 1)
            {
                prevBarTime = barTime;
                return;
            }

            // CloseTime сьогодні
            DateTime closeTarget = new DateTime(date.Year, date.Month, date.Day,
                CloseTime.Hour, CloseTime.Minute, 0);

            // Також перевіряємо вчорашній closeTarget (якщо сесія через midnight)
            DateTime closeTargetYesterday = closeTarget.AddDays(-1);

            bool crossed = false;

            // Перетин сьогоднішнього closeTarget
            if (prevBarTime < closeTarget && barTime >= closeTarget)
                crossed = true;

            // Перетин вчорашнього closeTarget (для нічних сесій)
            if (!crossed && prevBarTime < closeTargetYesterday && barTime >= closeTargetYesterday)
                crossed = true;

            if (crossed)
            {
                // Фіксуємо close — ціна останнього бара перед перетином
                double price = Close[1];

                if (closeList.Count > 0)
                    closeList[closeList.Count - 1].EndBar = CurrentBar;

                closeList.Add(new CloseData
                {
                    Price    = price,
                    StartBar = CurrentBar,
                    EndBar   = -1
                });
            }

            prevBarTime = barTime;
        }

        #endregion

        #region OnRender

        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            base.OnRender(chartControl, chartScale);
            ZOrder = -11000;

            if (RenderTarget == null) return;

            RenderOR(chartControl, chartScale);

            if (EnableClose)
                RenderClose(chartControl, chartScale);
        }

        private void RenderOR(ChartControl chartControl, ChartScale chartScale)
        {
            if (orDays == null || orDays.Count == 0) return;

            byte alpha = (byte)(OR_Opacity * 255 / 100);
            var c = ((System.Windows.Media.SolidColorBrush)OR_Color).Color;
            var fillColor = new SharpDX.Color4(
                c.R / 255f, c.G / 255f, c.B / 255f,
                alpha / 255f);
            var borderColor = new SharpDX.Color4(
                c.R / 255f, c.G / 255f, c.B / 255f, 1f);

            using (var fillBrush   = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, fillColor))
            using (var borderBrush = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, borderColor))
            {
                foreach (var kvp in orDays)
                {
                    ORData or = kvp.Value;

                    if (or.High <= double.MinValue || or.Low >= double.MaxValue)
                        continue;

                    int bar1, bar2;
                    try
                    {
                        bar1 = Bars.GetBar(or.Start);
                        bar2 = Bars.GetBar(or.End);
                    }
                    catch { continue; }

                    if (bar1 < 0 || bar2 < 0) continue;
                    if (bar1 > ChartBars.ToIndex || bar2 < ChartBars.FromIndex) continue;

                    float x1 = ChartControl.GetXByBarIndex(ChartBars, bar1);
                    float x2 = ChartControl.GetXByBarIndex(ChartBars, bar2);
                    float y1 = chartScale.GetYByValue(or.High);
                    float y2 = chartScale.GetYByValue(or.Low);

                    float width  = Math.Abs(x2 - x1);
                    float height = Math.Abs(y2 - y1);
                    float left   = Math.Min(x1, x2);
                    float top    = Math.Min(y1, y2);

                    if (width < 1 || height < 1) continue;

                    var rect = new RectangleF(left, top, width, height);
                    RenderTarget.FillRectangle(rect, fillBrush);
                    RenderTarget.DrawRectangle(rect, borderBrush, OR_BorderWidth);
                }
            }
        }

        private void RenderClose(ChartControl chartControl, ChartScale chartScale)
        {
            if (closeList == null || closeList.Count == 0) return;

            ChartPanel panel = chartControl.ChartPanels[chartScale.PanelIndex];
            float rightEdge = (float)(panel.X + panel.W);

            for (int i = 0; i < closeList.Count; i++)
            {
                CloseData cd = closeList[i];
                if (cd.Price == 0) continue;

                int endBar = (cd.EndBar > 0) ? cd.EndBar : CurrentBar;

                if (cd.StartBar > ChartBars.ToIndex || endBar < ChartBars.FromIndex)
                    continue;

                float y  = chartScale.GetYByValue(cd.Price);
                float x1 = ChartControl.GetXByBarIndex(ChartBars,
                    Math.Max(cd.StartBar, ChartBars.FromIndex));

                float x2;
                if (cd.EndBar > 0 && cd.EndBar <= ChartBars.ToIndex)
                    x2 = ChartControl.GetXByBarIndex(ChartBars, cd.EndBar);
                else
                    x2 = rightEdge;

                if (x2 <= x1) continue;

                RenderTarget.DrawLine(
                    new Vector2(x1, y), new Vector2(x2, y),
                    CloseStroke.BrushDX, CloseStroke.Width, CloseStroke.StrokeStyle);
            }
        }

        #endregion

        #region OnRenderTargetChanged

        public override void OnRenderTargetChanged()
        {
            if (RenderTarget != null)
                CloseStroke.RenderTarget = RenderTarget;
        }

        #endregion

        #region Properties

        // ═══════════════════════════════════════════════════════════════
        //  1. Opening Range
        // ═══════════════════════════════════════════════════════════════

        [Display(Name = "Час початку", Order = 1, GroupName = "1. Opening Range")]
        [PropertyEditor("NinjaTrader.Gui.Tools.TimeEditorKey")]
        public DateTime OR_StartTime { get; set; }

        [Range(1, 60)]
        [Display(Name = "Тривалість (хв)", Order = 2, GroupName = "1. Opening Range")]
        public int OR_Duration { get; set; }

        [XmlIgnore]
        [Display(Name = "Колір", Order = 3, GroupName = "1. Opening Range")]
        public System.Windows.Media.Brush OR_Color { get; set; }

        [Browsable(false)]
        public string OR_ColorSerialize
        {
            get { return Serialize.BrushToString(OR_Color); }
            set { OR_Color = Serialize.StringToBrush(value); }
        }

        [Range(0, 100)]
        [Display(Name = "Прозорість заливки (%)", Order = 4, GroupName = "1. Opening Range")]
        public int OR_Opacity { get; set; }

        [Range(1, 5)]
        [Display(Name = "Товщина рамки", Order = 5, GroupName = "1. Opening Range")]
        public int OR_BorderWidth { get; set; }

        // ═══════════════════════════════════════════════════════════════
        //  2. RTH Close
        // ═══════════════════════════════════════════════════════════════

        [Display(Name = "Показати RTH Close", Order = 1, GroupName = "2. RTH Close")]
        public bool EnableClose { get; set; }

        [Display(Name = "Час Close", Order = 2, GroupName = "2. RTH Close")]
        [PropertyEditor("NinjaTrader.Gui.Tools.TimeEditorKey")]
        public DateTime CloseTime { get; set; }

        [Display(Name = "Лінія", Order = 3, GroupName = "2. RTH Close")]
        public Stroke CloseStroke { get; set; }

        #endregion
    }
}
