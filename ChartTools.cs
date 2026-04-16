#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using SharpDX;
using SharpDX.Direct2D1;
using SharpDX.DirectWrite;
#endregion

namespace NinjaTrader.NinjaScript.Indicators.Loci
{
    public class ChartTools : Indicator
    {
        #region Private Fields

        // ── Ergonomic ────────────────────────────────────────────────
        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        private const byte VK_CONTROL            = 0x11;
        private const uint KEYEVENTF_EXTENDEDKEY = 0x1;
        private const uint KEYEVENTF_KEYUP       = 0x2;

        // ── Ruler ────────────────────────────────────────────────────
        private bool                isDragging;
        private System.Windows.Point startPoint;
        private System.Windows.Point currentPoint;

        // ── Shared ───────────────────────────────────────────────────
        private ChartControl chartCtrl;

        // ── SharpDX: Ruler ───────────────────────────────────────────
        private SharpDX.Direct2D1.Brush rulerLineBrush;
        private SharpDX.Direct2D1.Brush rulerFillBrush;
        private SharpDX.Direct2D1.Brush rulerTextBrush;
        private SharpDX.Direct2D1.Brush rulerBgBrush;
        private TextFormat              rulerTextFormat;

        // ── PriceLine (Stroke-based like standard NT8) ──────────────

        #endregion

        #region OnStateChange

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description             = "Ергономіка + Лінійка + Приховання назв + Лінія ціни";
                Name                    = "Chart Tools";
                Calculate               = Calculate.OnPriceChange;
                IsOverlay               = true;
                DisplayInDataBox        = false;
                DrawOnPricePanel        = true;
                DrawHorizontalGridLines = false;
                DrawVerticalGridLines   = false;
                PaintPriceMarkers       = false;
                ScaleJustification      = ScaleJustification.Right;
                IsAutoScale             = false;
                IsSuspendedWhileInactive = false;

                // ── 1. Модулі ──
                EnableErgonomic    = true;
                EnableRuler        = true;
                EnableLabelRemover = true;
                EnablePriceLine    = true;

                // ── 2. Лінійка ──
                RulerLineColor   = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x90, 0xFF)); RulerLineColor.Freeze();
                RulerTextColor   = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF2, 0xF2, 0xF2)); RulerTextColor.Freeze();
                RulerLineWidth   = 1;
                RulerFontSize    = 13;
                RulerZoneOpacity = 15;

                // ── 3. Лінія ціни ──
                PL_Stroke = new Stroke(
                    new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x69, 0x00, 0x00)),
                    DashStyleHelper.Dot, 2);
                ShowTransparentPlotsInDataBox = false;
                ArePlotsConfigurable          = false;
            }
            else if (State == State.Configure)
            {
                // Невидимий Plot — потрібен щоб OnMarketData → Values тригерив OnRender
                AddPlot(Brushes.Transparent, "PL");
            }
            else if (State == State.DataLoaded)
            {
                isDragging = false;

                // LabelRemover — одноразово при завантаженні
                if (EnableLabelRemover && ChartControl != null)
                {
                    foreach (var ind in ChartControl.Indicators)
                        ind.Name = "";
                }
            }
            else if (State == State.Historical)
            {
                if (ChartControl != null)
                {
                    chartCtrl = ChartControl;
                    chartCtrl.Dispatcher.InvokeAsync((Action)Subscribe);
                }
            }
            else if (State == State.Realtime)
            {
                if (ChartControl != null && chartCtrl == null)
                {
                    chartCtrl = ChartControl;
                    chartCtrl.Dispatcher.InvokeAsync((Action)Subscribe);
                }
            }
            else if (State == State.Terminated)
            {
                if (chartCtrl != null)
                    chartCtrl.Dispatcher.InvokeAsync((Action)Unsubscribe);
                DisposeResources();
            }
        }

        #endregion

        #region Subscribe / Unsubscribe

        private void Subscribe()
        {
            // Ergonomic
            chartCtrl.PreviewMouseLeftButtonDown += OnLeftDown;
            chartCtrl.PreviewMouseLeftButtonUp   += OnLeftUp;
            chartCtrl.LostFocus                  += OnLostFocus;

            // Ruler
            chartCtrl.PreviewMouseDown += OnMiddleDown;
            chartCtrl.PreviewMouseMove += OnMiddleMove;
            chartCtrl.PreviewMouseUp   += OnMiddleUp;

            // Shared: MouseWheel (Ergonomic zoom + Ruler block)
            chartCtrl.PreviewMouseWheel += OnWheel;
        }

        private void Unsubscribe()
        {
            if (chartCtrl == null) return;
            chartCtrl.PreviewMouseLeftButtonDown -= OnLeftDown;
            chartCtrl.PreviewMouseLeftButtonUp   -= OnLeftUp;
            chartCtrl.LostFocus                  -= OnLostFocus;
            chartCtrl.PreviewMouseDown           -= OnMiddleDown;
            chartCtrl.PreviewMouseMove           -= OnMiddleMove;
            chartCtrl.PreviewMouseUp             -= OnMiddleUp;
            chartCtrl.PreviewMouseWheel          -= OnWheel;
        }

        #endregion

        #region Mouse Handlers — Ergonomic

        private void OnLeftDown(object sender, MouseButtonEventArgs e)
        {
            if (!EnableErgonomic) return;

            if (e.ClickCount == 2)
                keybd_event(VK_CONTROL, 0, KEYEVENTF_EXTENDEDKEY | KEYEVENTF_KEYUP, UIntPtr.Zero);
            else
                keybd_event(VK_CONTROL, 0, KEYEVENTF_EXTENDEDKEY, UIntPtr.Zero);
        }

        private void OnLeftUp(object sender, MouseButtonEventArgs e)
        {
            if (!EnableErgonomic) return;
            keybd_event(VK_CONTROL, 0, KEYEVENTF_EXTENDEDKEY | KEYEVENTF_KEYUP, UIntPtr.Zero);
        }

        private void OnLostFocus(object sender, RoutedEventArgs e)
        {
            if (!EnableErgonomic) return;
            keybd_event(VK_CONTROL, 0, KEYEVENTF_EXTENDEDKEY | KEYEVENTF_KEYUP, UIntPtr.Zero);
        }

        #endregion

        #region Mouse Handlers — Ruler

        private void OnMiddleDown(object sender, MouseButtonEventArgs e)
        {
            if (!EnableRuler) return;
            if (e.MiddleButton != MouseButtonState.Pressed) return;

            startPoint   = e.GetPosition(chartCtrl);
            currentPoint = startPoint;
            isDragging   = true;

            e.Handled = true;
            chartCtrl.InvalidateVisual();
        }

        private void OnMiddleMove(object sender, MouseEventArgs e)
        {
            if (!EnableRuler || !isDragging) return;
            if (e.MiddleButton != MouseButtonState.Pressed)
            {
                isDragging = false;
                chartCtrl.InvalidateVisual();
                return;
            }

            currentPoint = e.GetPosition(chartCtrl);
            e.Handled    = true;
            chartCtrl.InvalidateVisual();
        }

        private void OnMiddleUp(object sender, MouseButtonEventArgs e)
        {
            if (!EnableRuler) return;
            if (e.ChangedButton != MouseButton.Middle) return;

            isDragging = false;
            e.Handled  = true;
            chartCtrl.InvalidateVisual();
        }

        #endregion

        #region Mouse Handler — Wheel (shared)

        private void OnWheel(object sender, MouseWheelEventArgs e)
        {
            // Ruler drag → блокуємо скрол
            if (isDragging && EnableRuler)
            {
                e.Handled = true;
                return;
            }

            // Ergonomic zoom
            if (!EnableErgonomic) return;
            if (chartCtrl == null || ChartBars == null) return;

            if (e.Delta < 0)
            {
                chartCtrl.Properties.BarDistance = (float)(chartCtrl.Properties.BarDistance * 0.9);
                chartCtrl.BarWidth = chartCtrl.BarWidth * 0.9;
            }
            else if (e.Delta > 0)
            {
                chartCtrl.Properties.BarDistance = (float)(chartCtrl.Properties.BarDistance / 0.9);
                chartCtrl.BarWidth = chartCtrl.BarWidth / 0.9;
            }

            e.Handled = true;
            chartCtrl.InvalidateVisual();
            ForceRefresh();
        }

        #endregion

        #region OnBarUpdate

        protected override void OnBarUpdate() { }

        protected override void OnMarketData(MarketDataEventArgs e)
        {
            if (!EnablePriceLine || CurrentBar < 0) return;
            if (e.MarketDataType == MarketDataType.Last)
                Values[0][0] = e.Price;
        }

        #endregion

        #region OnRender

        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            if (RenderTarget == null) return;
            base.OnRender(chartControl, chartScale);
            ZOrder = 25000;

            EnsureResources();

            // ── Ruler ────────────────────────────────────────────────
            if (EnableRuler && isDragging)
                RenderRuler(chartScale);

            // ── PriceLine ────────────────────────────────────────────
            if (EnablePriceLine && ChartBars != null)
                RenderPriceLine(chartControl, chartScale);
        }

        private void RenderRuler(ChartScale chartScale)
        {
            float x1 = (float)startPoint.X;
            float y1 = (float)startPoint.Y;
            float x2 = (float)currentPoint.X;
            float y2 = (float)currentPoint.Y;

            float left   = Math.Min(x1, x2);
            float top    = Math.Min(y1, y2);
            float width  = Math.Abs(x2 - x1);
            float height = Math.Abs(y2 - y1);

            // Зона
            if (width > 1 && height > 1)
                RenderTarget.FillRectangle(new RectangleF(left, top, width, height), rulerFillBrush);

            // Стрілки X / Y
            float arrowSize = 6;
            float midX = (x1 + x2) / 2f;
            float midY = (y1 + y2) / 2f;

            // Горизонтальна
            RenderTarget.DrawLine(new Vector2(x1, midY), new Vector2(x2, midY), rulerLineBrush, RulerLineWidth);
            float xDir = x2 > x1 ? -1 : 1;
            RenderTarget.DrawLine(new Vector2(x2, midY),
                new Vector2(x2 + xDir * arrowSize, midY - arrowSize), rulerLineBrush, RulerLineWidth);
            RenderTarget.DrawLine(new Vector2(x2, midY),
                new Vector2(x2 + xDir * arrowSize, midY + arrowSize), rulerLineBrush, RulerLineWidth);

            // Вертикальна
            RenderTarget.DrawLine(new Vector2(midX, y1), new Vector2(midX, y2), rulerLineBrush, RulerLineWidth);
            float yDir = y2 > y1 ? -1 : 1;
            RenderTarget.DrawLine(new Vector2(midX, y2),
                new Vector2(midX - arrowSize, y2 + yDir * arrowSize), rulerLineBrush, RulerLineWidth);
            RenderTarget.DrawLine(new Vector2(midX, y2),
                new Vector2(midX + arrowSize, y2 + yDir * arrowSize), rulerLineBrush, RulerLineWidth);

            // Тіки
            double price1 = chartScale.GetValueByY((int)y1);
            double price2 = chartScale.GetValueByY((int)y2);
            int ticks = (int)Math.Round(Math.Abs(price1 - price2) / Instrument.MasterInstrument.TickSize);
            string label = "Tick: " + ticks;

            float labelX = x2 + 10;
            float labelY = y2 - 10;

            using (var layout = new TextLayout(Core.Globals.DirectWriteFactory, label, rulerTextFormat, 300, 40))
            {
                float tw = (float)layout.Metrics.Width  + 8;
                float th = (float)layout.Metrics.Height + 4;

                RenderTarget.FillRectangle(new RectangleF(labelX - 2, labelY - 2, tw, th), rulerBgBrush);
                RenderTarget.DrawText(label, rulerTextFormat, new RectangleF(labelX, labelY, tw, th), rulerTextBrush);
            }
        }

        private void RenderPriceLine(ChartControl chartControl, ChartScale chartScale)
        {
            if (Values[0].Count == 0 || !Values[0].IsValidDataPointAt(Values[0].Count - 1))
                return;

            double price = Values[0].GetValueAt(Values[0].Count - 1);
            ChartPanel panel = chartControl.ChartPanels[chartScale.PanelIndex];
            float y   = chartScale.GetYByValue(price);
            float x1  = chartControl.GetXByBarIndex(ChartBars, ChartBars.ToIndex);
            float x2  = (float)(panel.X + panel.W);

            if (x2 <= x1) return;

            RenderTarget.DrawLine(
                new Vector2(x1, y), new Vector2(x2, y),
                PL_Stroke.BrushDX, PL_Stroke.Width, PL_Stroke.StrokeStyle);
        }

        #endregion

        #region SharpDX Resources

        public override void OnRenderTargetChanged()
        {
            DisposeResources();

            // Stroke потребує RenderTarget для BrushDX
            if (RenderTarget != null)
                PL_Stroke.RenderTarget = RenderTarget;
        }

        private void EnsureResources()
        {
            if (rulerLineBrush != null && !rulerLineBrush.IsDisposed) return;

            // ── Ruler ────────────────────────────────────────────────
            var rlc = ((System.Windows.Media.SolidColorBrush)RulerLineColor).Color;
            var rtc = ((System.Windows.Media.SolidColorBrush)RulerTextColor).Color;

            var lc = new SharpDX.Color4(rlc.R / 255f, rlc.G / 255f, rlc.B / 255f, 1f);
            rulerLineBrush = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, lc);

            byte alpha = (byte)(RulerZoneOpacity * 255 / 100);
            rulerFillBrush = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget,
                new SharpDX.Color4(rlc.R / 255f, rlc.G / 255f, rlc.B / 255f, alpha / 255f));

            var tc = new SharpDX.Color4(rtc.R / 255f, rtc.G / 255f, rtc.B / 255f, 1f);
            rulerTextBrush = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, tc);

            rulerBgBrush = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget,
                new SharpDX.Color4(rlc.R / 255f, rlc.G / 255f, rlc.B / 255f, 0.85f));

            rulerTextFormat = new TextFormat(Core.Globals.DirectWriteFactory, "Segoe UI Light",
                SharpDX.DirectWrite.FontWeight.Light, SharpDX.DirectWrite.FontStyle.Normal, RulerFontSize)
            {
                TextAlignment      = SharpDX.DirectWrite.TextAlignment.Leading,
                ParagraphAlignment = SharpDX.DirectWrite.ParagraphAlignment.Center
            };
        }

        private void DisposeResources()
        {
            rulerLineBrush?.Dispose();   rulerLineBrush   = null;
            rulerFillBrush?.Dispose();   rulerFillBrush   = null;
            rulerTextBrush?.Dispose();   rulerTextBrush   = null;
            rulerBgBrush?.Dispose();     rulerBgBrush     = null;
            rulerTextFormat?.Dispose();  rulerTextFormat  = null;
        }

        #endregion

        #region Properties

        // ═══════════════════════════════════════════════════════════════
        //  1. Модулі
        // ═══════════════════════════════════════════════════════════════

        [Display(Name = "Ергономіка", GroupName = "1. Модулі", Order = 1)]
        public bool EnableErgonomic { get; set; }

        [Display(Name = "Лінійка", GroupName = "1. Модулі", Order = 2)]
        public bool EnableRuler { get; set; }

        [Display(Name = "Сховати назви", GroupName = "1. Модулі", Order = 3)]
        public bool EnableLabelRemover { get; set; }

        [Display(Name = "Лінія ціни", GroupName = "1. Модулі", Order = 4)]
        public bool EnablePriceLine { get; set; }

        // ═══════════════════════════════════════════════════════════════
        //  2. Лінійка
        // ═══════════════════════════════════════════════════════════════

        [XmlIgnore]
        [Display(Name = "Колір лінії", GroupName = "2. Лінійка", Order = 1)]
        public System.Windows.Media.Brush RulerLineColor { get; set; }

        [Browsable(false)]
        public string RulerLineColorSerialize
        {
            get { return Serialize.BrushToString(RulerLineColor); }
            set { RulerLineColor = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "Колір тексту", GroupName = "2. Лінійка", Order = 2)]
        public System.Windows.Media.Brush RulerTextColor { get; set; }

        [Browsable(false)]
        public string RulerTextColorSerialize
        {
            get { return Serialize.BrushToString(RulerTextColor); }
            set { RulerTextColor = Serialize.StringToBrush(value); }
        }

        [Range(1, 5)]
        [Display(Name = "Товщина лінії", GroupName = "2. Лінійка", Order = 3)]
        public int RulerLineWidth { get; set; }

        [Range(8, 24)]
        [Display(Name = "Розмір шрифту", GroupName = "2. Лінійка", Order = 4)]
        public int RulerFontSize { get; set; }

        [Range(0, 100)]
        [Display(Name = "Прозорість зони (%)", GroupName = "2. Лінійка", Order = 5)]
        public int RulerZoneOpacity { get; set; }

        // ═══════════════════════════════════════════════════════════════
        //  3. Лінія ціни
        // ═══════════════════════════════════════════════════════════════

        [Display(Name = "Лінія ціни", GroupName = "3. Лінія ціни", Order = 1)]
        public Stroke PL_Stroke { get; set; }

        #endregion
    }
}
