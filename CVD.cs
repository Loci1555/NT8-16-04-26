#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
using SharpDX;
using SharpDX.Direct2D1;
#endregion

namespace NinjaTrader.NinjaScript.Indicators.Loci
{
    [TypeConverter("NinjaTrader.NinjaScript.Indicators.Loci.CVD_PropertiesConverter")]
    public class CVD : Indicator
    {
        #region Private Fields

        // ── CVD Series (for rendering) ──
        private Series<double> cvdOpen;
        private Series<double> cvdHigh;
        private Series<double> cvdLow;
        private Series<double> cvdClose;
        private Series<double> cvdSmoothed;

        // ── Hosted OFCD ──
        private OrderFlowCumulativeDelta ofcd;

        // ── Own CVD accumulator (Candles / Line) ──
        private double myCVD;
        private double myBarOpen;

        // ── FilteredLine ──
        private long   filteredCVD;
        private long   filteredBarOpen;
        private Series<double> filteredClose;
        private System.Windows.Media.Brush frozenFilteredBrush;

        // ── Tick aggregator (FilteredLine — CME 1-lot fills → bucket) ──
        private double bucketPrice;
        private long   bucketDirection;
        private long   bucketVolume;
        private int    lastChartBarIdx;

        // ── Tick classification ──
        private long   lastTickDirection;
        private double lastTickPrice;
        private double cachedAsk;
        private double cachedBid;

        // ── Session tracking ──
        private SessionIterator sessionIter;
        private DateTime curSessionEnd;
        private Series<bool> isSessionStart;

        // ── VisibleRange offset cache (set in OnRender, read in OnBarUpdate) ──
        private double cachedVisOffset;

        // ── Frozen brushes ──
        private System.Windows.Media.Brush frozenUpBrush;
        private System.Windows.Media.Brush frozenDnBrush;
        private System.Windows.Media.Brush frozenLineBrush;
        private System.Windows.Media.Brush frozenLabelBrush;

        #endregion

        #region OnStateChange

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description              = "Cumulative Volume Delta — Candles, Line, Filtered Line";
                Name                     = "CVD";
                IsOverlay                = false;
                DrawOnPricePanel         = false;
                Calculate                = Calculate.OnEachTick;
                IsAutoScale              = true;
                IsSuspendedWhileInactive = false;
                BarsRequiredToPlot       = 0;
                DisplayInDataBox         = true;
                PaintPriceMarkers        = true;
                ScaleJustification       = ScaleJustification.Right;
                ArePlotsConfigurable     = false;
                MaximumBarsLookBack      = MaximumBarsLookBack.Infinite;

                // ── 1. Налаштування ──
                DisplayMode              = CVDDisplayMode.Candles;
                ResetMode                = CVDResetMode.Session;
                SmoothPeriod             = 1;
                SmoothType               = CVDSmoothType.EMA;

                // ── 2. Filtered Line ──
                FilterMinVolume          = 10;
                FilterMaxVolume          = 0;
                FilteredLineColor        = System.Windows.Media.Brushes.DodgerBlue;
                FilteredLineWidth        = 2;

                // ── 3. Кольори ──
                BarWidthPercent          = 40;
                UpCandleColor            = new System.Windows.Media.SolidColorBrush(
                                               System.Windows.Media.Color.FromRgb(0x00, 0x40, 0x71));
                DnCandleColor            = new System.Windows.Media.SolidColorBrush(
                                               System.Windows.Media.Color.FromRgb(0x66, 0x00, 0x3D));
                LineColor                = System.Windows.Media.Brushes.DodgerBlue;
                LineWidth                = 2;

                // ── 4. Мітка ──
                LabelColor               = System.Windows.Media.Brushes.SlateGray;
                ShowLeaderLine           = false;
            }
            else if (State == State.Configure)
            {
                // 1-tick data series required for hosted OFCD (and tick aggregation)
                AddDataSeries(Data.BarsPeriodType.Tick, 1);

                // Plots: all start transparent, then set price-marker color per mode
                AddPlot(System.Windows.Media.Brushes.Transparent, "CVDClose");   // Values[0]
                AddPlot(System.Windows.Media.Brushes.Transparent, "ScaleHigh");  // Values[1]
                AddPlot(System.Windows.Media.Brushes.Transparent, "ScaleLow");   // Values[2]

                switch (DisplayMode)
                {
                    case CVDDisplayMode.FilteredLine:
                        Plots[0].Brush = FilteredLineColor;
                        break;
                    default:
                        Plots[0].Brush = LabelColor;
                        break;
                }

                // Freeze brushes
                frozenUpBrush       = UpCandleColor.Clone();     frozenUpBrush.Freeze();
                frozenDnBrush       = DnCandleColor.Clone();     frozenDnBrush.Freeze();
                frozenLineBrush     = LineColor.Clone();          frozenLineBrush.Freeze();
                frozenFilteredBrush = FilteredLineColor.Clone();  frozenFilteredBrush.Freeze();
                frozenLabelBrush    = LabelColor.Clone();         frozenLabelBrush.Freeze();
            }
            else if (State == State.DataLoaded)
            {
                cvdOpen      = new Series<double>(this, MaximumBarsLookBack.Infinite);
                cvdHigh      = new Series<double>(this, MaximumBarsLookBack.Infinite);
                cvdLow       = new Series<double>(this, MaximumBarsLookBack.Infinite);
                cvdClose     = new Series<double>(this, MaximumBarsLookBack.Infinite);
                cvdSmoothed  = new Series<double>(this, MaximumBarsLookBack.Infinite);
                filteredClose = new Series<double>(this, MaximumBarsLookBack.Infinite);

                isSessionStart = new Series<bool>(this, MaximumBarsLookBack.Infinite);

                ofcd = OrderFlowCumulativeDelta(CumulativeDeltaType.BidAsk,
                           CumulativeDeltaPeriod.Session, 0);

                sessionIter       = new SessionIterator(Bars);
                curSessionEnd     = DateTime.MinValue;

                myCVD             = 0;
                myBarOpen         = 0;
                filteredCVD       = 0;
                filteredBarOpen   = 0;
                lastTickDirection = 1;
                lastChartBarIdx   = -1;
            }
            else if (State == State.Terminated)
            {
            }
        }

        #endregion

        #region OnMarketData / Tick Helpers

        protected override void OnMarketData(MarketDataEventArgs e)
        {
            if (e.MarketDataType == MarketDataType.Ask)
                cachedAsk = e.Price;
            else if (e.MarketDataType == MarketDataType.Bid)
                cachedBid = e.Price;
        }

        /// <summary>
        /// Classify a tick as buyer (+1) or seller (-1).
        /// Bid/Ask → tick rule fallback when no quotes available.
        /// </summary>
        private long ClassifyTick(double tickPrice)
        {
            double ask = GetCurrentAsk(0);
            double bid = GetCurrentBid(0);
            if (ask <= 0) ask = cachedAsk;
            if (bid <= 0) bid = cachedBid;

            long direction;
            if (ask > 0 && tickPrice >= ask)
                direction = 1;
            else if (bid > 0 && tickPrice <= bid)
                direction = -1;
            else if (ask <= 0 && bid <= 0)
            {
                // Tick rule fallback (historical without Tick Replay)
                if (tickPrice > lastTickPrice)
                    direction = 1;
                else if (tickPrice < lastTickPrice)
                    direction = -1;
                else
                    direction = lastTickDirection;
            }
            else
                direction = lastTickDirection;

            lastTickDirection = direction;
            lastTickPrice     = tickPrice;
            return direction;
        }

        /// <summary>
        /// Finalize accumulated bucket: apply volume filter, add to filteredCVD.
        /// </summary>
        private void FinalizeBucket()
        {
            long vol = bucketVolume;
            bucketVolume = 0;

            bool passMin = vol >= FilterMinVolume;
            bool passMax = (FilterMaxVolume <= 0) || (vol <= FilterMaxVolume);
            if (passMin && passMax)
                filteredCVD += bucketDirection * vol;
        }

        #endregion

        #region OnBarUpdate

        protected override void OnBarUpdate()
        {
            // ══════════════════════════════════════════
            //  BIP 1: OFCD tick series + FilteredLine aggregation
            // ══════════════════════════════════════════
            if (BarsInProgress == 1)
            {
                ofcd.Update(ofcd.BarsArray[1].Count - 1, 1);

                // ── Tick aggregation for FilteredLine ──
                if (DisplayMode == CVDDisplayMode.FilteredLine
                    && CurrentBars[0] >= 0 && CurrentBars[1] >= 0)
                {
                    double tickPrice = Closes[1][0];
                    long   tickVol   = (long)Volumes[1][0];

                    // ── New chart bar? ──
                    int chartIdx = CurrentBars[0];
                    if (chartIdx != lastChartBarIdx)
                    {
                        if (bucketVolume > 0)
                            FinalizeBucket();

                        // Session reset on tick series
                        if (ResetMode == CVDResetMode.Session
                            && BarsArray[1].IsFirstBarOfSession)
                            filteredCVD = 0;

                        lastChartBarIdx = chartIdx;
                        filteredBarOpen = filteredCVD;
                    }

                    // ── Classify + aggregate ──
                    long direction = ClassifyTick(tickPrice);

                    if (tickPrice == bucketPrice && direction == bucketDirection)
                    {
                        bucketVolume += tickVol;
                    }
                    else
                    {
                        if (bucketVolume > 0)
                            FinalizeBucket();

                        bucketPrice     = tickPrice;
                        bucketDirection = direction;
                        bucketVolume    = tickVol;
                    }
                }

                return;
            }

            // ══════════════════════════════════════════
            //  BIP 0: chart bars
            // ══════════════════════════════════════════
            if (BarsInProgress != 0) return;
            if (CurrentBars[0] < 1)  return;

            ofcd.Update(ofcd.BarsArray[0].Count - 1, 0);

            // ── Session detection via SessionIterator ──
            bool isNewSession = false;
            if (ResetMode == CVDResetMode.Session && IsFirstTickOfBar)
            {
                if (Time[0] > curSessionEnd)
                {
                    sessionIter.GetNextSession(Time[0], false);
                    curSessionEnd = sessionIter.ActualSessionEnd;
                    isNewSession  = true;
                }
            }
            if (IsFirstTickOfBar)
                isSessionStart[0] = isNewSession;

            // ── FilteredLine mode ──
            if (DisplayMode == CVDDisplayMode.FilteredLine)
            {
                if (isNewSession)
                {
                    filteredCVD     = 0;
                    filteredBarOpen = 0;
                }

                double rawVal = filteredCVD;
                filteredClose[0] = rawVal;

                // Smoothing
                double fVal = rawVal;
                if (SmoothPeriod > 1 && CurrentBars[0] >= SmoothPeriod)
                {
                    if (SmoothType == CVDSmoothType.EMA)
                    {
                        double k    = 2.0 / (SmoothPeriod + 1);
                        double prev = isNewSession ? fVal
                                    : cvdSmoothed.GetValueAt(CurrentBars[0] - 1);
                        fVal = fVal * k + prev * (1 - k);
                    }
                    else // SMA
                    {
                        double sum = 0;
                        for (int j = 0; j < SmoothPeriod; j++)
                            sum += filteredClose.GetValueAt(CurrentBars[0] - j);
                        fVal = sum / SmoothPeriod;
                    }
                }
                cvdSmoothed[0] = fVal;

                Values[0][0] = Math.Round(
                    (ResetMode == CVDResetMode.VisibleRange) ? fVal - cachedVisOffset : fVal);

                // Populate OHLC series for leader line
                cvdOpen[0]  = filteredBarOpen;
                cvdHigh[0]  = rawVal;
                cvdLow[0]   = rawVal;
                cvdClose[0] = fVal;
                return;
            }

            // ── Candles / Line — own CVD accumulator ──
            if (isNewSession)
            {
                myCVD     = 0;
                myBarOpen = 0;
            }
            if (IsFirstTickOfBar)
                myBarOpen = myCVD;

            // Bar delta from OFCD + intra-bar wicks
            double barDelta = ofcd.DeltaClose[0] - ofcd.DeltaOpen[0];
            double dC = myBarOpen + barDelta;
            double dO = myBarOpen;
            double dH = myBarOpen + (ofcd.DeltaHigh[0] - ofcd.DeltaOpen[0]);
            double dL = myBarOpen + (ofcd.DeltaLow[0]  - ofcd.DeltaOpen[0]);
            myCVD = dC;

            cvdOpen[0]  = dO;
            cvdHigh[0]  = dH;
            cvdLow[0]   = dL;
            cvdClose[0] = dC;

            if (DisplayMode == CVDDisplayMode.Candles)
            {
                // Values for data box + scale helpers for autoscale
                double visC = (ResetMode == CVDResetMode.VisibleRange)
                            ? dC - cachedVisOffset : dC;
                double visH = (ResetMode == CVDResetMode.VisibleRange)
                            ? dH - cachedVisOffset : dH;
                double visL = (ResetMode == CVDResetMode.VisibleRange)
                            ? dL - cachedVisOffset : dL;

                Values[0][0] = Math.Round(visC);
                Values[1][0] = visH;
                Values[2][0] = visL;
            }
            else // Line
            {
                // Smoothing
                if (SmoothPeriod > 1 && CurrentBars[0] >= SmoothPeriod)
                {
                    if (SmoothType == CVDSmoothType.EMA)
                    {
                        double k    = 2.0 / (SmoothPeriod + 1);
                        double prev = isNewSession ? dC
                                    : cvdSmoothed.GetValueAt(CurrentBars[0] - 1);
                        cvdSmoothed[0] = dC * k + prev * (1 - k);
                    }
                    else // SMA
                    {
                        double sum = 0;
                        for (int j = 0; j < SmoothPeriod; j++)
                            sum += cvdClose.GetValueAt(CurrentBars[0] - j);
                        cvdSmoothed[0] = sum / SmoothPeriod;
                    }
                }
                else
                    cvdSmoothed[0] = dC;

                double src = (SmoothPeriod > 1) ? cvdSmoothed[0] : dC;
                Values[0][0] = Math.Round(
                    (ResetMode == CVDResetMode.VisibleRange) ? src - cachedVisOffset : src);
            }
        }

        #endregion

        #region OnRender

        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            if (RenderTarget == null || ChartBars == null)
                return;

            RenderTarget.AntialiasMode = SharpDX.Direct2D1.AntialiasMode.Aliased;

            int fromIdx = ChartBars.FromIndex;
            int toIdx   = ChartBars.ToIndex;
            if (fromIdx < 0 || toIdx < 0 || toIdx < fromIdx)
                return;

            // ── Compute VisibleRange offset ──
            double visOffset = 0;
            if (ResetMode == CVDResetMode.VisibleRange)
            {
                if (DisplayMode == CVDDisplayMode.FilteredLine)
                {
                    var src = (SmoothPeriod > 1) ? cvdSmoothed : filteredClose;
                    visOffset = src.GetValueAt(fromIdx);
                }
                else
                    visOffset = cvdOpen.GetValueAt(fromIdx);
            }

            // ── Dispatch ──
            switch (DisplayMode)
            {
                case CVDDisplayMode.Candles:
                    RenderCandles(chartControl, chartScale, fromIdx, toIdx, visOffset);
                    break;
                case CVDDisplayMode.Line:
                    RenderLine(chartControl, chartScale, fromIdx, toIdx, visOffset);
                    break;
                case CVDDisplayMode.FilteredLine:
                    RenderFilteredLine(chartControl, chartScale, fromIdx, toIdx, visOffset);
                    break;
            }

            RenderLeaderLine(chartControl, chartScale, toIdx, visOffset);

            // Cache offset for OnBarUpdate → Values[] (PaintPriceMarkers)
            cachedVisOffset = (ResetMode == CVDResetMode.VisibleRange) ? visOffset : 0;
        }

        private void RenderCandles(ChartControl cc, ChartScale cs,
            int fromIdx, int toIdx, double offset)
        {
            float halfBarW = (float)(cc.BarWidth * BarWidthPercent / 100.0);
            if (halfBarW < 1) halfBarW = 1;

            using (var upBrush = frozenUpBrush.ToDxBrush(RenderTarget))
            using (var dnBrush = frozenDnBrush.ToDxBrush(RenderTarget))
            {
                for (int i = fromIdx; i <= toIdx; i++)
                {
                    double o = cvdOpen.GetValueAt(i)  - offset;
                    double h = cvdHigh.GetValueAt(i)  - offset;
                    double l = cvdLow.GetValueAt(i)   - offset;
                    double c = cvdClose.GetValueAt(i) - offset;

                    float x  = cc.GetXByBarIndex(ChartBars, i);
                    float yO = cs.GetYByValue(o);
                    float yH = cs.GetYByValue(h);
                    float yL = cs.GetYByValue(l);
                    float yC = cs.GetYByValue(c);

                    bool isUp = c >= o;
                    var  brush = isUp ? upBrush : dnBrush;

                    float bodyTop    = Math.Min(yO, yC);
                    float bodyBottom = Math.Max(yO, yC);
                    float bodyHeight = Math.Max(bodyBottom - bodyTop, 1);

                    // Wick
                    RenderTarget.DrawLine(
                        new Vector2(x, yH), new Vector2(x, yL),
                        brush, 1);

                    // Body
                    var bodyRect = new SharpDX.RectangleF(
                        x - halfBarW, bodyTop, halfBarW * 2, bodyHeight);
                    RenderTarget.FillRectangle(bodyRect, brush);
                    RenderTarget.DrawRectangle(bodyRect, brush, 1);
                }
            }
        }

        private void RenderLine(ChartControl cc, ChartScale cs,
            int fromIdx, int toIdx, double offset)
        {
            if (toIdx - fromIdx < 1) return;

            var source = (SmoothPeriod > 1) ? cvdSmoothed : cvdClose;

            using (var lineBrush = frozenLineBrush.ToDxBrush(RenderTarget))
            {
                float prevX = cc.GetXByBarIndex(ChartBars, fromIdx);
                float prevY = cs.GetYByValue(source.GetValueAt(fromIdx) - offset);

                for (int i = fromIdx + 1; i <= toIdx; i++)
                {
                    float x = cc.GetXByBarIndex(ChartBars, i);
                    float y = cs.GetYByValue(source.GetValueAt(i) - offset);

                    // Session break — don't connect across sessions
                    if (ResetMode == CVDResetMode.Session
                        && isSessionStart.GetValueAt(i))
                    {
                        prevX = x; prevY = y;
                        continue;
                    }

                    RenderTarget.DrawLine(
                        new Vector2(prevX, prevY), new Vector2(x, y),
                        lineBrush, LineWidth);

                    prevX = x;
                    prevY = y;
                }
            }
        }

        private void RenderFilteredLine(ChartControl cc, ChartScale cs,
            int fromIdx, int toIdx, double offset)
        {
            if (toIdx - fromIdx < 1) return;

            var source = (SmoothPeriod > 1) ? cvdSmoothed : filteredClose;

            using (var brush = frozenFilteredBrush.ToDxBrush(RenderTarget))
            {
                float prevX = cc.GetXByBarIndex(ChartBars, fromIdx);
                float prevY = cs.GetYByValue(source.GetValueAt(fromIdx) - offset);

                for (int i = fromIdx + 1; i <= toIdx; i++)
                {
                    float x = cc.GetXByBarIndex(ChartBars, i);
                    float y = cs.GetYByValue(source.GetValueAt(i) - offset);

                    // Session break
                    if (ResetMode == CVDResetMode.Session
                        && isSessionStart.GetValueAt(i))
                    {
                        prevX = x; prevY = y;
                        continue;
                    }

                    RenderTarget.DrawLine(
                        new Vector2(prevX, prevY), new Vector2(x, y),
                        brush, FilteredLineWidth);

                    prevX = x;
                    prevY = y;
                }
            }
        }

        private void RenderLeaderLine(ChartControl cc, ChartScale cs,
            int lastIdx, double offset)
        {
            if (!ShowLeaderLine) return;

            // Pick source that matches the rendered line
            double lastVal;
            switch (DisplayMode)
            {
                case CVDDisplayMode.FilteredLine:
                    var fSrc = (SmoothPeriod > 1) ? cvdSmoothed : filteredClose;
                    lastVal = fSrc.GetValueAt(lastIdx) - offset;
                    break;
                case CVDDisplayMode.Line:
                    var lSrc = (SmoothPeriod > 1) ? cvdSmoothed : cvdClose;
                    lastVal = lSrc.GetValueAt(lastIdx) - offset;
                    break;
                default: // Candles
                    lastVal = cvdClose.GetValueAt(lastIdx) - offset;
                    break;
            }

            float y        = cs.GetYByValue(lastVal);
            float lastBarX = cc.GetXByBarIndex(ChartBars, lastIdx);
            float panelW   = (float)ChartPanel.W;

            using (var lineBrush = frozenLabelBrush.ToDxBrush(RenderTarget))
            using (var leaderStyle = new StrokeStyle(RenderTarget.Factory,
                new StrokeStyleProperties
                { DashStyle = SharpDX.Direct2D1.DashStyle.Dash }))
            {
                RenderTarget.DrawLine(
                    new Vector2(lastBarX, y), new Vector2(panelW, y),
                    lineBrush, 1, leaderStyle);
            }
        }

        #endregion

        #region Properties

        // ═══════════════════════════════════════════
        //  1. Налаштування
        // ═══════════════════════════════════════════

        [RefreshProperties(RefreshProperties.All)]
        [Display(Name = "Режим відображення", GroupName = "1. Налаштування", Order = 1)]
        public CVDDisplayMode DisplayMode { get; set; }

        [Display(Name = "Режим скидання", GroupName = "1. Налаштування", Order = 2)]
        public CVDResetMode ResetMode { get; set; }

        [Range(1, 100)]
        [Display(Name = "Період згладжування", GroupName = "1. Налаштування", Order = 3)]
        public int SmoothPeriod { get; set; }

        [Display(Name = "Тип згладжування", GroupName = "1. Налаштування", Order = 4)]
        public CVDSmoothType SmoothType { get; set; }

        // ═══════════════════════════════════════════
        //  2. Filtered Line
        // ═══════════════════════════════════════════

        [Range(1, int.MaxValue)]
        [Display(Name = "Мін обсяг фільтра", GroupName = "2. Filtered Line", Order = 1)]
        public int FilterMinVolume { get; set; }

        [Range(0, int.MaxValue)]
        [Display(Name = "Макс обсяг фільтра (0 = без ліміту)", GroupName = "2. Filtered Line", Order = 2)]
        public int FilterMaxVolume { get; set; }

        [XmlIgnore]
        [Display(Name = "Колір фільтрованої лінії", GroupName = "2. Filtered Line", Order = 3)]
        public System.Windows.Media.Brush FilteredLineColor { get; set; }
        [Browsable(false)] public string FilteredLineColor_Serializable
        { get { return Serialize.BrushToString(FilteredLineColor); } set { FilteredLineColor = Serialize.StringToBrush(value); } }

        [Range(1, 10)]
        [Display(Name = "Ширина фільтрованої лінії", GroupName = "2. Filtered Line", Order = 4)]
        public int FilteredLineWidth { get; set; }

        // ═══════════════════════════════════════════
        //  3. Кольори
        // ═══════════════════════════════════════════

        [Range(10, 100)]
        [Display(Name = "Ширина свічки (%)", GroupName = "3. Кольори", Order = 0)]
        public int BarWidthPercent { get; set; }

        [XmlIgnore]
        [Display(Name = "Колір бичачої свічки", GroupName = "3. Кольори", Order = 1)]
        public System.Windows.Media.Brush UpCandleColor { get; set; }
        [Browsable(false)] public string UpCandleColor_Serializable
        { get { return Serialize.BrushToString(UpCandleColor); } set { UpCandleColor = Serialize.StringToBrush(value); } }

        [XmlIgnore]
        [Display(Name = "Колір ведмежої свічки", GroupName = "3. Кольори", Order = 2)]
        public System.Windows.Media.Brush DnCandleColor { get; set; }
        [Browsable(false)] public string DnCandleColor_Serializable
        { get { return Serialize.BrushToString(DnCandleColor); } set { DnCandleColor = Serialize.StringToBrush(value); } }

        [XmlIgnore]
        [Display(Name = "Колір лінії", GroupName = "3. Кольори", Order = 3)]
        public System.Windows.Media.Brush LineColor { get; set; }
        [Browsable(false)] public string LineColor_Serializable
        { get { return Serialize.BrushToString(LineColor); } set { LineColor = Serialize.StringToBrush(value); } }

        [Range(1, 10)]
        [Display(Name = "Ширина лінії", GroupName = "3. Кольори", Order = 4)]
        public int LineWidth { get; set; }

        // ═══════════════════════════════════════════
        //  4. Мітка
        // ═══════════════════════════════════════════

        [XmlIgnore]
        [Display(Name = "Колір мітки", GroupName = "4. Мітка", Order = 1)]
        public System.Windows.Media.Brush LabelColor { get; set; }
        [Browsable(false)] public string LabelColor_Serializable
        { get { return Serialize.BrushToString(LabelColor); } set { LabelColor = Serialize.StringToBrush(value); } }

        [Display(Name = "Показати лінію-лідер", GroupName = "4. Мітка", Order = 2)]
        public bool ShowLeaderLine { get; set; }

        #endregion
    }

    #region TypeConverter

    public class CVD_PropertiesConverter : IndicatorBaseConverter
    {
        public override bool GetPropertiesSupported(ITypeDescriptorContext context)
        { return true; }

        public override PropertyDescriptorCollection GetProperties(
            ITypeDescriptorContext context, object component, Attribute[] attrs)
        {
            CVD cvd = component as CVD;
            PropertyDescriptorCollection props =
                base.GetPropertiesSupported(context)
                    ? base.GetProperties(context, component, attrs)
                    : TypeDescriptor.GetProperties(component, attrs);

            if (cvd == null || props == null)
                return props;

            // ── Property groups ──
            string[] candleProps = {
                "BarWidthPercent",
                "UpCandleColor", "UpCandleColor_Serializable",
                "DnCandleColor", "DnCandleColor_Serializable"
            };
            string[] lineProps = {
                "LineColor", "LineColor_Serializable", "LineWidth"
            };
            string[] filteredProps = {
                "FilterMinVolume", "FilterMaxVolume",
                "FilteredLineColor", "FilteredLineColor_Serializable",
                "FilteredLineWidth"
            };
            string[] smoothProps = {
                "SmoothPeriod", "SmoothType"
            };

            switch (cvd.DisplayMode)
            {
                case CVDDisplayMode.Candles:
                    RemoveAll(props, lineProps);
                    RemoveAll(props, filteredProps);
                    RemoveAll(props, smoothProps);
                    break;

                case CVDDisplayMode.Line:
                    RemoveAll(props, candleProps);
                    RemoveAll(props, filteredProps);
                    break;

                case CVDDisplayMode.FilteredLine:
                    RemoveAll(props, candleProps);
                    RemoveAll(props, lineProps);
                    break;
            }

            return props;
        }

        private void RemoveAll(PropertyDescriptorCollection props, string[] names)
        {
            foreach (var name in names)
            {
                PropertyDescriptor pd = props[name];
                if (pd != null) props.Remove(pd);
            }
        }
    }

    #endregion

    #region Enums

    public enum CVDDisplayMode { Candles, Line, FilteredLine }
    public enum CVDResetMode   { Session, VisibleRange }
    public enum CVDSmoothType  { EMA, SMA }

    #endregion
}
