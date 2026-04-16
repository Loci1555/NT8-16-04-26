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
using SharpDX.DirectWrite;
#endregion

namespace NinjaTrader.NinjaScript.Indicators.Loci
{
    [TypeConverter("NinjaTrader.NinjaScript.Indicators.Loci.VP_PropertiesConverter")]
    public class VP : Indicator
    {
        #region Private Fields

        // ── Data dictionaries ──────────────────────────────────────
        private Dictionary<int, VPInfoByPrice> bin_info_map;
        private Dictionary<int, VPPriceBinMap> bin_price_map;
        private Dictionary<int, VPInfoByPrice> current_bar_by_price;
        private Dictionary<int, VPInfoByPrice> combined_by_price;
        private Dictionary<int, VPInfoByPrice> sum_by_price;
        private Dictionary<int, VPInfoByPrice>[] saved_by_price;
        private Series<Dictionary<int, VPInfoByPrice>> by_price_series;

        // ── Session tracking ────────────────────────────────────────
        private SessionIterator sessIter;
        private DateTime bip1_session_start;
        private DateTime bip1_session_end;
        private DateTime profile_start_time;
        private DateTime profile_end_time;

        // ── Swing tracking (CurrentSwing mode) ─────────────────────
        private ZigZag zz;
        private int current_HL_barnum;
        private int prior_HL_barnum;
        private int last_changebar;
        private int swing_start_bar;
        private int swing_end_bar;
        private double prior_swing_high;
        private double prior_swing_low;
        private double prior_price;

        // ── Profile bounds ─────────────────────────────────────────
        private double profile_high;
        private double profile_low;

        // ── Frozen opacity brushes ─────────────────────────────────
        private System.Windows.Media.Brush up_delta_color;
        private System.Windows.Media.Brush dn_delta_color;
        private System.Windows.Media.Brush dpoc_color;
        private System.Windows.Media.Brush volume_color;
        private System.Windows.Media.Brush vpoc_color;
        private System.Windows.Media.Brush va_color;
        private System.Windows.Media.Brush bg_color;

        // ── Lookback state ─────────────────────────────────────────
        private int saved_period_lookback;
        private bool have_enough_lookback;
        private bool have_enough_elements;

        private object sync_lock = new object();

        // ── Visible-area cache (avoid rebuild on every OnRender) ─────
        private int cachedVisFromIdx = -1;
        private int cachedVisToIdx   = -1;
        private Dictionary<int, VPInfoByPrice> cachedVisData;
        private bool visibleDirty = true;   // set true in OnBarUpdate to mark stale

        // ── HighlightedRange overlay bar indices ─────────────────────
        private int hlBarStart = -1;
        private int hlBarEnd   = -1;

        #endregion

        #region OnStateChange

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description                 = "Volume Profile — Volume & Delta з POC/VA";
                Name                        = "VP";
                IsOverlay                   = true;
                Calculate                   = Calculate.OnEachTick;
                DrawOnPricePanel            = true;
                DrawHorizontalGridLines     = false;
                DrawVerticalGridLines       = false;
                IsAutoScale                 = false;
                IsSuspendedWhileInactive    = false;
                BarsRequiredToPlot          = 0;
                DisplayInDataBox            = false;
                PaintPriceMarkers           = false;
                ScaleJustification          = ScaleJustification.Right;
                MaximumBarsLookBack         = MaximumBarsLookBack.Infinite;
                ArePlotsConfigurable        = false;

                // ── 1. Settings ──
                VP_Type                     = VPDurationType.CurrentRTH;
                Session_Type                = VPSessionType.Indices;
                RTH_StartTime               = DateTime.Parse("08:30:00", CultureInfo.InvariantCulture);
                Rotation_Size               = 10.0;
                Show_Swing_Start            = false;
                Highlight_Swing             = false;
                Num_Minutes                 = 30;
                Num_Days                    = 5;
                Num_Weeks                   = 2;
                VP_StartTime                = DateTime.Now.Date;
                VP_EndTime                  = DateTime.Now;
                OverlayOnChart              = false;
                RegionHLX_Color             = System.Windows.Media.Brushes.DimGray;
                RegionHLX_Opacity           = 10;

                // ── 2. Profile Configuration ──
                Ticks_Per_Level             = 1;
                Smooth_Passes              = 0;
                VP_Placement                = VPHorizontalPosition.Right;
                VP_Alignment                = VPHorizontalPosition.Left;
                VP_Width                    = 200;
                VP_Offset                   = 0;
                Relative_Placement          = VPProfilePlacement.SideBySide_VD;

                // ── 3. Delta Profile ──
                Show_DeltaProfile           = true;
                Show_DPOC                   = true;
                Show_Delta_Values           = false;
                UpDelta_Color               = MkBrush(20, 98, 137);
                DnDelta_Color               = MkBrush(133, 31, 29);
                DPOC_Color                  = MkBrush(0, 128, 255);
                UpDelta_Opacity             = 75;
                DnDelta_Opacity             = 75;
                DPOC_Opacity                = 100;
                DeltaUp_Text_Color          = MkBrush(16, 132, 188);
                DeltaDn_Text_Color          = MkBrush(187, 32, 25);

                // ── 4. Volume Profile ──
                Show_VolProfile             = true;
                Show_VPOC                   = true;
                Show_Volume_Values          = false;
                Show_VA                     = false;
                VA_Percent                  = 70;
                Volume_Color                = MkBrush(105, 105, 105);
                VA_Color                    = MkBrush(54, 59, 71);
                VPOC_Color                  = MkBrush(32, 178, 170);
                Volume_Text_Color           = MkBrush(68, 68, 68);
                Volume_Opacity              = 30;
                VA_Opacity                  = 75;
                VPOC_Opacity                = 40;
                Show_VA_Levels              = false;
                Show_VA_Labels              = false;
                Extend_VA_Levels            = false;

                // ── Font ──
                Value_Text_Font             = new SimpleFont("Segoe UI Light", 20);
                VA_Label_Font               = new SimpleFont("Segoe UI Light", 10);

                // ── Background ──
                Show_Background             = false;
                Background_Color            = MkBrush(20, 22, 28);
                Background_Opacity          = 50;
            }
            else if (State == State.Configure)
            {
                // ── Initialize dictionaries ──
                bin_info_map        = new Dictionary<int, VPInfoByPrice>();
                bin_price_map       = new Dictionary<int, VPPriceBinMap>();
                current_bar_by_price = new Dictionary<int, VPInfoByPrice>();
                combined_by_price   = new Dictionary<int, VPInfoByPrice>();
                sum_by_price        = new Dictionary<int, VPInfoByPrice>();
                profile_high        = double.MinValue;
                profile_low         = double.MaxValue;

                // ── Add 1-second Volumetric series as BIP 1 ──
                // Using 1-second bars for efficient per-price aggregation.
                // Each 1-min volumetric bar aggregates ALL ticks in that minute
                // with full per-price bid/ask data. Profile is identical to 1-sec
                // but 60x fewer bars to process (~55K vs ~3.3M for 40 days).
                bool resetEOD = BarsArray[0].IsResetOnNewTradingDay;
                AddVolumetric("", BarsPeriodType.Minute, 1,
                    VolumetricDeltaType.BidAsk, 1, resetEOD);

                // ── Freeze opacity brushes ──
                up_delta_color = CloneBrushOpacity(UpDelta_Color, UpDelta_Opacity);
                dn_delta_color = CloneBrushOpacity(DnDelta_Color, DnDelta_Opacity);
                dpoc_color     = CloneBrushOpacity(DPOC_Color, DPOC_Opacity);
                volume_color   = CloneBrushOpacity(Volume_Color, Volume_Opacity);
                vpoc_color     = CloneBrushOpacity(VPOC_Color, VPOC_Opacity);
                va_color       = CloneBrushOpacity(VA_Color, VA_Opacity);
                bg_color       = CloneBrushOpacity(Background_Color, Background_Opacity);

                // ── Period lookback for Days/Weeks ──
                saved_period_lookback = 0;
                if (VP_Type == VPDurationType.Days)
                    saved_period_lookback = Num_Days;
                else if (VP_Type == VPDurationType.Weeks)
                    saved_period_lookback = Num_Weeks;

                if (saved_period_lookback > 1)
                {
                    int n = saved_period_lookback - 1;
                    saved_by_price = new Dictionary<int, VPInfoByPrice>[n];
                    for (int i = 0; i < n; i++)
                        saved_by_price[i] = new Dictionary<int, VPInfoByPrice>();
                }
            }
            else if (State == State.DataLoaded)
            {
                sessIter = new SessionIterator(BarsArray[1]);

                // ── Series for VisibleArea/Minutes/CurrentSwing modes ──
                if (VP_Type == VPDurationType.VisibleArea
                    || VP_Type == VPDurationType.Minutes
                    || VP_Type == VPDurationType.CurrentSwing
                    || VP_Type == VPDurationType.HighlightedRange)
                {
                    var proxy = SMA(Closes[1], 1);
                    by_price_series = new Series<Dictionary<int, VPInfoByPrice>>(proxy, MaximumBarsLookBack.Infinite);
                }

                if (VP_Type == VPDurationType.CurrentSwing)
                {
                    zz = ZigZag(Closes[1], DeviationType.Points, Rotation_Size, true);
                }

                // ── Init profile time boundaries ──
                profile_start_time = DateTime.MinValue;
                profile_end_time   = DateTime.MinValue;

                if (VP_Type == VPDurationType.EntireChart
                    || VP_Type == VPDurationType.HighlightedRange)
                {
                    profile_start_time = DateTime.MinValue;
                    profile_end_time   = DateTime.MaxValue;
                }
                else if (VP_Type == VPDurationType.StartTime)
                {
                    profile_start_time = VP_StartTime == DateTime.MinValue ? DateTime.Today : VP_StartTime;
                    profile_end_time   = DateTime.MaxValue;
                }
                else if (VP_Type == VPDurationType.TimeSpan)
                {
                    // Set initial values; OnBarUpdate reads VP_StartTime/VP_EndTime fresh each bar
                    profile_start_time = VP_StartTime;
                    profile_end_time   = VP_EndTime;
                }
                else if (VP_Type == VPDurationType.VisibleArea
                      || VP_Type == VPDurationType.Minutes
                      || VP_Type == VPDurationType.CurrentSwing)
                {
                    profile_start_time = DateTime.MinValue;
                    profile_end_time   = DateTime.MaxValue;
                }
                else if (VP_Type == VPDurationType.Days
                      || VP_Type == VPDurationType.Weeks)
                {
                    profile_start_time = DateTime.MinValue;
                    profile_end_time   = DateTime.MinValue;
                }
            }
        }

        #endregion

        #region OnBarUpdate

        protected override void OnBarUpdate()
        {
            if (BarsArray[0] == null || BarsArray[1] == null
                || CurrentBars[0] == -1 || CurrentBars[1] == -1)
                return;

            if (BarsInProgress == 0)
                return;

            if (BarsInProgress != 1)
                return;

            // ══════════════════════════════════════════════════════════
            //  SESSION BOUNDARY DETECTION
            // ══════════════════════════════════════════════════════════
            if (BarsArray[1].IsFirstBarOfSession && IsFirstTickOfBar)
            {
                DateTime barTime = BarsArray[1].GetTime(CurrentBar);
                sessIter.GetTradingDay(barTime);
                bip1_session_start = sessIter.ActualSessionBegin;
                bip1_session_end   = sessIter.ActualSessionEnd;

                if (VP_Type == VPDurationType.CurrentRTH)
                {
                    profile_start_time = GetRTHStartTime(bip1_session_start, bip1_session_end);
                    profile_end_time   = bip1_session_end;
                }
                else if (VP_Type == VPDurationType.CurrentETH)
                {
                    profile_start_time = bip1_session_start;
                    profile_end_time   = bip1_session_end;
                    current_bar_by_price = new Dictionary<int, VPInfoByPrice>();
                    combined_by_price    = new Dictionary<int, VPInfoByPrice>();
                }
                else if (VP_Type == VPDurationType.Days)
                {
                    profile_start_time = bip1_session_start;
                    profile_end_time   = bip1_session_end;
                    ShiftAndInsert(ref saved_by_price, combined_by_price);
                    current_bar_by_price = new Dictionary<int, VPInfoByPrice>();
                    combined_by_price    = new Dictionary<int, VPInfoByPrice>();
                }
                else if (VP_Type == VPDurationType.CurrentWeek || VP_Type == VPDurationType.Weeks)
                {
                    DateTime tradingDay = sessIter.GetTradingDay(barTime);
                    if (tradingDay.DayOfWeek == DayOfWeek.Monday
                        || (CurrentBar > 0 && Time[0].DayOfWeek < Time[1].DayOfWeek))
                    {
                        // New week boundary — reset
                        profile_start_time = bip1_session_start;
                        if (VP_Type == VPDurationType.Weeks)
                            ShiftAndInsert(ref saved_by_price, combined_by_price);
                        current_bar_by_price = new Dictionary<int, VPInfoByPrice>();
                        combined_by_price    = new Dictionary<int, VPInfoByPrice>();
                    }
                    else if (profile_start_time == DateTime.MinValue)
                    {
                        // First session on chart (mid-week) — start accumulating
                        profile_start_time = bip1_session_start;
                    }
                    if (profile_start_time != DateTime.MinValue)
                        profile_end_time = bip1_session_end;
                }
                else if (VP_Type == VPDurationType.CurrentMonth)
                {
                    DateTime tradingDay = sessIter.GetTradingDay(barTime);
                    if (tradingDay.Day == 1
                        || (CurrentBar > 0 && Time[0].Day < Time[1].Day))
                    {
                        // New month boundary — reset
                        profile_start_time = bip1_session_start;
                        current_bar_by_price = new Dictionary<int, VPInfoByPrice>();
                        combined_by_price    = new Dictionary<int, VPInfoByPrice>();
                    }
                    else if (profile_start_time == DateTime.MinValue)
                    {
                        // First session on chart (mid-month) — start accumulating
                        profile_start_time = bip1_session_start;
                    }
                    if (profile_start_time != DateTime.MinValue)
                        profile_end_time = bip1_session_end;
                }
            }

            // ── Reset for RTH boundary ──
            if (VP_Type == VPDurationType.CurrentRTH
                && (BarsArray[1].IsFirstBarOfSession && profile_start_time < Time[0]
                    || (CurrentBar > 0 && Time[1] <= profile_start_time)))
            {
                current_bar_by_price = new Dictionary<int, VPInfoByPrice>();
                combined_by_price    = new Dictionary<int, VPInfoByPrice>();
            }

            // ── Swing detection (CurrentSwing mode) ──
            if (VP_Type == VPDurationType.CurrentSwing && IsFirstTickOfBar && CurrentBar > 100)
            {
                ProcessSwing();
            }

            // ── Skip if outside profile window ──
            bool outsideTimeSpan = false;
            if (VP_Type == VPDurationType.TimeSpan)
            {
                // Always read fresh from properties (user may change on the fly)
                profile_start_time = VP_StartTime;
                profile_end_time   = VP_EndTime;

                // Full datetime comparison
                outsideTimeSpan = (Time[0] < VP_StartTime || Time[0] > VP_EndTime);
            }
            else if (!(profile_start_time < Time[0]) || !(Time[0] <= profile_end_time))
                return;

            // ══════════════════════════════════════════════════════════
            //  AGGREGATE TICK DATA INTO PRICE BINS
            // ══════════════════════════════════════════════════════════

            if (!outsideTimeSpan)
            {
                // ── Save previous bar data on new bar (realtime) ──
                if (State == State.Realtime && Calculate == Calculate.OnEachTick && IsFirstTickOfBar)
                {
                    visibleDirty = true;  // invalidate visible-area cache on new data

                    if ((VP_Type == VPDurationType.VisibleArea
                        || VP_Type == VPDurationType.Minutes
                        || VP_Type == VPDurationType.CurrentSwing
                        || VP_Type == VPDurationType.HighlightedRange) && CurrentBar > 0)
                    {
                        by_price_series[1] = current_bar_by_price;
                    }

                    if (VP_Type == VPDurationType.Minutes)
                    {
                        RebuildMinutesLookback();
                    }
                    else
                    {
                        AddByPriceInPlace(combined_by_price, current_bar_by_price);
                    }
                }

                // ── Extract per-price data from Volumetric bar ──
                var barsType = BarsArray[1].BarsType as
                    NinjaTrader.NinjaScript.BarsTypes.VolumetricBarsType;
                if (barsType == null)
                    return;

                var vol = barsType.Volumes[CurrentBars[1]];

                double barLow  = Lows[1][0];
                double barHigh = Highs[1][0];

                if (!(barLow == 0 || barHigh == 0 || barLow > barHigh))
                {
                    // Modes that need per-bar data stored in by_price_series
                    bool needsPerBarData = (VP_Type == VPDurationType.VisibleArea
                        || VP_Type == VPDurationType.Minutes
                        || VP_Type == VPDurationType.CurrentSwing
                        || VP_Type == VPDurationType.HighlightedRange);

                    // For accumulate-only modes during historical: write directly to
                    // combined_by_price, skipping intermediate dictionary creation.
                    // This avoids ~1.8M dictionary allocations for CurrentMonth.
                    bool directAccum = !needsPerBarData
                        && State == State.Historical
                        && CurrentBar < BarsArray[1].Count - 1;

                    Dictionary<int, VPInfoByPrice> target;
                    if (directAccum)
                    {
                        target = combined_by_price;
                    }
                    else
                    {
                        current_bar_by_price = new Dictionary<int, VPInfoByPrice>();
                        target = current_bar_by_price;
                    }

                    for (double p = barLow; p <= barHigh;
                         p = Instrument.MasterInstrument.RoundToTickSize(p + TickSize))
                    {
                        double price = Instrument.MasterInstrument.RoundToTickSize(p);
                        long askVol, bidVol;
                        try
                        {
                            askVol = vol.GetAskVolumeForPrice(price);
                            bidVol = vol.GetBidVolumeForPrice(price);
                        }
                        catch { continue; }

                        long delta    = askVol - bidVol;
                        long totalVol = askVol + bidVol;
                        if (totalVol == 0) continue;

                        var bin = new VPPriceBinMap(price, Ticks_Per_Level, Instrument);
                        VPInfoByPrice existing;
                        if (target.TryGetValue(bin.bn, out existing))
                            existing.Add(delta, askVol, bidVol, totalVol);
                        else
                            target[bin.bn] = new VPInfoByPrice(delta, askVol, bidVol, totalVol);

                        if (!bin_price_map.ContainsKey(bin.bn))
                            bin_price_map[bin.bn] = bin;
                    }

                    // ── Historical bar: accumulate immediately ──
                    if ((State == State.Historical && CurrentBar < BarsArray[1].Count - 1)
                        || Calculate == Calculate.OnBarClose)
                    {
                        if (needsPerBarData)
                        {
                            by_price_series[0] = current_bar_by_price;
                        }

                        if (VP_Type == VPDurationType.Minutes)
                        {
                            if (CurrentBar >= BarsArray[1].Count - 2)
                                RebuildMinutesLookback();
                        }
                        else if (!directAccum && !needsPerBarData)
                        {
                            // Realtime or OnBarClose: still need intermediate step
                            AddByPriceInPlace(combined_by_price, current_bar_by_price);
                            current_bar_by_price = new Dictionary<int, VPInfoByPrice>();
                        }
                    }
                }
            } // end !outsideTimeSpan

            // ── Build final sum (only when needed for display) ──
            // During historical, OnRender is not called, so skip expensive sum
            // until the last bar. In realtime, always keep sum_by_price fresh.
            if (State == State.Realtime || CurrentBar >= BarsArray[1].Count - 2)
            {
                lock (sync_lock)
                {
                    sum_by_price = AddByPrice(combined_by_price, current_bar_by_price);

                    if ((VP_Type == VPDurationType.Days || VP_Type == VPDurationType.Weeks)
                        && saved_by_price != null)
                    {
                        for (int k = 0; k < saved_by_price.Length; k++)
                            sum_by_price = AddByPrice(saved_by_price[k], sum_by_price);
                        have_enough_elements = saved_by_price[saved_by_price.Length - 1].Count > 0;
                    }
                }
            }
        }

        #endregion

        #region OnRender

        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            if (RenderTarget == null || BarsArray[0] == null || BarsArray[0].Instrument == null)
                return;

            base.OnRender(chartControl, chartScale);
            RenderTarget.AntialiasMode = SharpDX.Direct2D1.AntialiasMode.Aliased;
            ZOrder = 15000;

            // ── Select data source ──
            Dictionary<int, VPInfoByPrice> renderData;

            if (VP_Type == VPDurationType.VisibleArea)
            {
                int fromIdx = ChartBars.FromIndex;
                int toIdx   = ChartBars.ToIndex;

                // Only rebuild when visible range changed or new data arrived
                if (fromIdx != cachedVisFromIdx || toIdx != cachedVisToIdx || visibleDirty)
                {
                    int bar1 = BarsArray[1].GetBar(Times[0].GetValueAt(fromIdx));
                    int bar2 = BarsArray[1].GetBar(Times[0].GetValueAt(toIdx));
                    cachedVisData    = BuildFromBarRange(bar1, bar2);
                    cachedVisFromIdx = fromIdx;
                    cachedVisToIdx   = toIdx;
                    visibleDirty     = false;
                }
                renderData = cachedVisData;
            }
            else if (VP_Type == VPDurationType.CurrentSwing)
            {
                if (swing_start_bar <= 0 || swing_end_bar <= swing_start_bar)
                    return;
                renderData = BuildFromBarRange(swing_start_bar, swing_end_bar);
            }
            else if (VP_Type == VPDurationType.HighlightedRange)
            {
                // Find any RegionHighlight drawing on the chart panel.
                int hlStart = -1, hlEnd = -1;
                object foundHL = null;
                DateTime hlTimeStart = DateTime.MinValue;
                DateTime hlTimeEnd   = DateTime.MinValue;

                foreach (var obj in DrawObjects)
                {
                    if (obj == null) continue;
                    string typeName = obj.GetType().Name;
                    if (typeName.Contains("RegionHighlight"))
                    {
                        var anchors = obj.Anchors.ToList();
                        if (anchors.Count >= 2)
                        {
                            DateTime t0 = anchors[0].Time;
                            DateTime t1 = anchors[1].Time;
                            hlTimeStart = t0 < t1 ? t0 : t1;
                            hlTimeEnd   = t0 < t1 ? t1 : t0;
                            foundHL = obj;
                            break;
                        }
                    }
                }

                if (foundHL == null || hlTimeStart >= hlTimeEnd)
                    return;

                hlStart = BarsArray[1].GetBar(hlTimeStart);
                hlEnd   = BarsArray[1].GetBar(hlTimeEnd);
                if (hlStart < 0 || hlEnd < 0 || hlEnd <= hlStart)
                    return;

                renderData = BuildFromBarRange(hlStart, hlEnd);

                // Apply user color & opacity to the RegionHighlight drawing
                try
                {
                    dynamic hlDraw = foundHL;
                    hlDraw.AreaBrush   = RegionHLX_Color;
                    hlDraw.AreaOpacity = RegionHLX_Opacity;
                    hlDraw.OutlineStroke = new Stroke(RegionHLX_Color, DashStyleHelper.Dash, 1);
                }
                catch { }

                // Store BIP 0 bar indices for overlay positioning
                hlBarStart = Bars.GetBar(hlTimeStart);
                hlBarEnd   = Bars.GetBar(hlTimeEnd);
            }
            else if (VP_Type == VPDurationType.Minutes)
            {
                if (!have_enough_lookback) return;
                lock (sync_lock) { renderData = sum_by_price; }
            }
            else if (VP_Type == VPDurationType.Days || VP_Type == VPDurationType.Weeks)
            {
                // Show whatever data is available (don't require all periods filled)
                lock (sync_lock) { renderData = sum_by_price; }
            }
            else
            {
                if (sum_by_price == null || sum_by_price.Count == 0) return;
                lock (sync_lock) { renderData = sum_by_price; }
            }

            if (renderData == null || renderData.Count == 0)
                return;

            // ── Compute VA/POC for volume profile ──
            double vah = 0, val2 = 0, poc = 0;
            if (Show_VolProfile && (Show_VA || Show_VPOC))
            {
                var sortedVol = new SortedDictionary<double, long>();
                foreach (int key in renderData.Keys)
                {
                    if (!bin_price_map.ContainsKey(key)) continue;
                    double h = bin_price_map[key].h;
                    if (!sortedVol.ContainsKey(h))
                        sortedVol[h] = renderData[key].totalVolume;
                    else
                        sortedVol[h] += renderData[key].totalVolume;
                }
                CalcVolumeProfile(sortedVol, ref vah, ref val2, ref poc);
            }

            // ── Згладжування для Volume профілю (візуал, POC/VA з оригінальних) ──
            var volDisplayData = SmoothVolumeData(renderData);

            // ── Compute layout ──
            int totalWidth;
            int x_origin;

            if (VP_Type == VPDurationType.HighlightedRange && OverlayOnChart
                && hlBarStart >= 0 && hlBarEnd >= 0)
            {
                // Overlay mode: position profile directly over highlighted range
                x_origin   = chartControl.GetXByBarIndex(ChartBars, hlBarStart);
                int xEnd   = chartControl.GetXByBarIndex(ChartBars, hlBarEnd);
                totalWidth = Math.Max(xEnd - x_origin, 10);
            }
            else
            {
                totalWidth = VP_Width;
                x_origin = (VP_Placement == VPHorizontalPosition.Right)
                    ? ChartPanel.X + ChartPanel.W - totalWidth - VP_Offset
                    : ChartPanel.X + VP_Offset;
            }

            int deltaWidth = 0, volWidth = 0;
            float deltaX = 0, volX = 0;

            if (Show_DeltaProfile && Show_VolProfile)
            {
                bool isSideBySide = Relative_Placement == VPProfilePlacement.SideBySide_VD
                    || Relative_Placement == VPProfilePlacement.SideBySide_DV
                    || Relative_Placement == VPProfilePlacement.SideBySide_VD_Flipped
                    || Relative_Placement == VPProfilePlacement.SideBySide_DV_Flipped;

                if (isSideBySide)
                {
                    deltaWidth = totalWidth / 2 - 2;
                    volWidth   = deltaWidth;
                    float leftX  = x_origin;
                    float rightX = leftX + deltaWidth + 2;

                    if (Relative_Placement == VPProfilePlacement.SideBySide_VD
                        || Relative_Placement == VPProfilePlacement.SideBySide_VD_Flipped)
                    {
                        volX   = leftX;
                        deltaX = rightX;
                    }
                    else
                    {
                        deltaX = leftX;
                        volX   = rightX;
                    }
                }
                else // Overlay
                {
                    deltaWidth = totalWidth;
                    volWidth   = totalWidth;
                    deltaX     = x_origin;
                    volX       = x_origin;
                }
            }
            else if (Show_DeltaProfile)
            {
                deltaWidth = totalWidth;
                deltaX     = x_origin;
            }
            else if (Show_VolProfile)
            {
                volWidth = totalWidth;
                volX     = x_origin;
            }

            // ── Get alignment ──
            bool deltaAlignRight = GetAlignment(true);
            bool volAlignRight   = GetAlignment(false);

            // ── Background ──
            if (Show_Background)
            {
                using (var dxBg = bg_color.ToDxBrush(RenderTarget))
                {
                    RenderTarget.FillRectangle(
                        new SharpDX.RectangleF(x_origin, ChartPanel.Y,
                            totalWidth, ChartPanel.H), dxBg);
                }
            }

            // ── Render ──
            if (Show_DeltaProfile && Show_VolProfile)
            {
                if (Relative_Placement == VPProfilePlacement.Overlay_DV)
                {
                    RenderLadder(volDisplayData, chartScale, volX, volWidth, false, volAlignRight,
                        0, 0, 0, vah, val2, poc);
                    RenderLadder(renderData, chartScale, deltaX, deltaWidth, true, deltaAlignRight,
                        0, 0, 0, 0, 0, 0);
                }
                else if (Relative_Placement == VPProfilePlacement.Overlay_VD)
                {
                    RenderLadder(renderData, chartScale, deltaX, deltaWidth, true, deltaAlignRight,
                        0, 0, 0, 0, 0, 0);
                    RenderLadder(volDisplayData, chartScale, volX, volWidth, false, volAlignRight,
                        0, 0, 0, vah, val2, poc);
                }
                else
                {
                    RenderLadder(renderData, chartScale, deltaX, deltaWidth, true, deltaAlignRight,
                        0, 0, 0, 0, 0, 0);
                    RenderLadder(volDisplayData, chartScale, volX, volWidth, false, volAlignRight,
                        0, 0, 0, vah, val2, poc);
                }
            }
            else if (Show_DeltaProfile)
            {
                RenderLadder(renderData, chartScale, deltaX, deltaWidth, true, deltaAlignRight,
                    0, 0, 0, 0, 0, 0);
            }
            else if (Show_VolProfile)
            {
                RenderLadder(volDisplayData, chartScale, volX, volWidth, false, volAlignRight,
                    0, 0, 0, vah, val2, poc);
            }
        }

        #endregion

        #region Rendering Helpers

        private void RenderLadder(
            Dictionary<int, VPInfoByPrice> data,
            ChartScale chartScale,
            float x_origin, int width,
            bool isDelta, bool alignRight,
            double dpoc_val, double dpoc_vol_unused, double dpoc_poc_unused,
            double vah, double val_price, double poc_price)
        {
            if (width < 1) return;

            // ── Determine brushes ──
            System.Windows.Media.Brush fillBrush1, fillBrush2;
            System.Windows.Media.Brush labelBrush1, labelBrush2;
            System.Windows.Media.Brush pocBrush, vaBrush, vpBrush;
            bool showText, showPoc, showVA, showVALevels, showVALabels;

            if (isDelta)
            {
                fillBrush1  = up_delta_color;
                fillBrush2  = dn_delta_color;
                labelBrush1 = DeltaUp_Text_Color;
                labelBrush2 = DeltaDn_Text_Color;
                pocBrush    = dpoc_color;
                vaBrush     = Brushes.Transparent;
                vpBrush     = Brushes.Transparent;
                showText    = Show_Delta_Values;
                showPoc     = Show_DPOC;
                showVA      = false;
                showVALevels = false;
                showVALabels = false;
            }
            else
            {
                fillBrush1  = volume_color;
                fillBrush2  = Brushes.Transparent;
                labelBrush1 = Volume_Text_Color;
                labelBrush2 = Volume_Text_Color;
                pocBrush    = vpoc_color;
                vaBrush     = va_color;
                vpBrush     = volume_color;
                showText    = Show_Volume_Values;
                showPoc     = Show_VPOC;
                showVA      = Show_VA;
                showVALevels = Show_VA_Levels;
                showVALabels = Show_VA_Labels;
            }

            // ── Convert to DX brushes ──
            using (var dxFill1 = fillBrush1.ToDxBrush(RenderTarget))
            using (var dxFill2 = fillBrush2.ToDxBrush(RenderTarget))
            using (var dxLabel1 = labelBrush1.ToDxBrush(RenderTarget))
            using (var dxLabel2 = labelBrush2.ToDxBrush(RenderTarget))
            using (var dxPoc = pocBrush.ToDxBrush(RenderTarget))
            using (var dxVA = vaBrush.ToDxBrush(RenderTarget))
            using (var dxVP = vpBrush.ToDxBrush(RenderTarget))
            {
                List<int> keys = data.Keys.ToList();
                keys.Sort();

                // ── First pass: find max value and min pixel height ──
                long maxVal = 0;
                long maxDelta = 0;
                int minPixelH = int.MaxValue;
                int minBin = int.MaxValue, maxBin = int.MinValue;

                foreach (int key in keys)
                {
                    if (!data.ContainsKey(key) || !bin_price_map.ContainsKey(key)) continue;

                    if (isDelta)
                    {
                        long absDelta = Math.Abs(data[key].delta);
                        maxDelta = Math.Max(maxDelta, absDelta);
                        maxVal = maxDelta;
                    }
                    else
                    {
                        maxVal = Math.Max(maxVal, data[key].totalVolume);
                    }

                    double n = bin_price_map[key].n;
                    double l = bin_price_map[key].l;
                    int pixH = Math.Abs(chartScale.GetYByValue(l) - chartScale.GetYByValue(n));
                    minPixelH = Math.Min(minPixelH, pixH);
                    maxBin = Math.Max(maxBin, key);
                    minBin = Math.Min(minBin, key);
                }

                if (maxVal == 0) return;

                // ── Auto-size font ──
                SimpleFont labelFont = new SimpleFont(
                    Value_Text_Font.Family.ToString(), (int)Value_Text_Font.Size);
                labelFont.Bold = Value_Text_Font.Bold;
                labelFont.Italic = Value_Text_Font.Italic;

                if (showText && minPixelH > 2)
                {
                    // Shrink font to fit bar height 
                    var testFmt = labelFont.ToDirectWriteTextFormat();
                    var testLayout = new SharpDX.DirectWrite.TextLayout(
                        NinjaTrader.Core.Globals.DirectWriteFactory, "0", testFmt, 1000f, 1000f);
                    while (testLayout.Metrics.Height > minPixelH && labelFont.Size > 6)
                    {
                        labelFont.Size -= 1;
                        testFmt.Dispose();
                        testLayout.Dispose();
                        testFmt = labelFont.ToDirectWriteTextFormat();
                        testLayout = new SharpDX.DirectWrite.TextLayout(
                            NinjaTrader.Core.Globals.DirectWriteFactory, "0", testFmt, 1000f, 1000f);
                    }
                    testFmt.Dispose();
                    testLayout.Dispose();

                    if (labelFont.Size > Value_Text_Font.Size)
                        labelFont.Size = Value_Text_Font.Size;
                    if (labelFont.Size < 6)
                        labelFont.Size = 6;
                }

                using (var textFormat = labelFont.ToDirectWriteTextFormat())
                {
                    textFormat.TextAlignment = SharpDX.DirectWrite.TextAlignment.Leading;
                    textFormat.ParagraphAlignment = SharpDX.DirectWrite.ParagraphAlignment.Center;

                    float prevY = -1;
                    SharpDX.RectangleF pocRect = new SharpDX.RectangleF();
                    float pocStrokeW = 0;

                    // ── Second pass: render bars ──
                    for (int i = minBin; i <= maxBin; i++)
                    {
                        if (!data.ContainsKey(i) || !bin_price_map.ContainsKey(i))
                            continue;

                        double n = bin_price_map[i].n;
                        double h = bin_price_map[i].h;
                        double l = bin_price_map[i].l;

                        if (l > chartScale.MaxValue || n < chartScale.MinValue)
                            continue;

                        // ── Bar value and text ──
                        long barValue = 0;
                        string text = "";

                        if (isDelta)
                        {
                            long delta = data[i].delta;
                            barValue = Math.Abs(delta);
                            text = delta.ToString();
                        }
                        else
                        {
                            barValue = data[i].totalVolume;
                            text = barValue.ToString();
                        }

                        // ── Bar width proportional to value ──
                        int barWidth = (maxVal > 0)
                            ? (int)Math.Round((double)barValue / maxVal * width)
                            : 0;

                        // ── Y position ──
                        float yTop = chartScale.GetYByValue(
                            n - 0.5 * BarsArray[0].Instrument.MasterInstrument.TickSize);
                        float barHeight = Math.Abs(
                            chartScale.GetYByValue(l) - chartScale.GetYByValue(n));
                        barHeight = (prevY == -1) ? barHeight : (prevY - yTop);
                        prevY = yTop;

                        // ── X position with alignment ──
                        float xPos = x_origin;
                        if (alignRight)
                            xPos += (width - barWidth);

                        var rect = new SharpDX.RectangleF(xPos, yTop, barWidth, barHeight);

                        // ── Fill ──
                        if (isDelta)
                        {
                            long delta = data[i].delta;
                            RenderTarget.FillRectangle(rect,
                                delta >= 0 ? dxFill1 : dxFill2);

                            if (showPoc && Math.Abs(delta) == maxDelta)
                            {
                                float sw = barHeight < 20 ? 1f : 2f;
                                RenderTarget.DrawRectangle(rect, dxPoc, sw);
                                pocRect = rect;
                                pocStrokeW = sw;
                            }
                        }
                        else
                        {
                            // Volume profile
                            if (showPoc && h == poc_price)
                            {
                                RenderTarget.FillRectangle(rect, dxPoc);

                                if (showVA && showVALevels && Extend_VA_Levels)
                                    RenderVALine(x_origin, barWidth, h, chartScale, dxPoc, true);
                                if (showVA && showVALabels)
                                    RenderVAText(barHeight, h, chartScale, dxPoc, "POC", yTop);
                            }
                            else if (!showVA)
                            {
                                RenderTarget.FillRectangle(rect, dxFill1);
                            }
                            else
                            {
                                bool inVA = h >= val_price && h <= vah;
                                RenderTarget.FillRectangle(rect, inVA ? dxVA : dxVP);

                                if (showVALevels && h == vah)
                                    RenderVALine(x_origin, barWidth, vah, chartScale, dxVA, false);
                                if (showVALabels && h == vah)
                                    RenderVAText(barHeight, vah, chartScale, dxVA, "VAH", 0);
                                if (showVALevels && h == val_price)
                                    RenderVALine(x_origin, barWidth, val_price, chartScale, dxVA, false);
                                if (showVALabels && h == val_price)
                                    RenderVAText(barHeight, val_price, chartScale, dxVA, "VAL", 0);
                            }
                        }

                        // ── Text ──
                        if (showText && minPixelH >= 2 && width >= 10)
                        {
                            using (var layout = new SharpDX.DirectWrite.TextLayout(
                                NinjaTrader.Core.Globals.DirectWriteFactory,
                                text, textFormat, (float)width, barHeight))
                            {
                                long signedVal = isDelta ? data[i].delta : barValue;
                                var textBrush = signedVal >= 0 ? dxLabel1 : dxLabel2;

                                // Position text based on alignment 
                                float textX = x_origin + 1f;
                                if (alignRight)
                                {
                                    float tw = layout.Metrics.Width;
                                    textX = x_origin + width - tw - 1f;
                                }
                                RenderTarget.DrawTextLayout(
                                    new SharpDX.Vector2(textX, yTop),
                                    layout, textBrush);
                            }
                        }
                    }

                    // ── Redraw POC outline on top ──
                    if (pocStrokeW > 0 && isDelta)
                        RenderTarget.DrawRectangle(pocRect, dxPoc, pocStrokeW);

                    // ── POC line (volume profile) — окремо від циклу, не залежить від масштабу ──
                    if (!isDelta && showPoc && poc_price > 0)
                    {
                        float pocY = chartScale.GetYByValue(poc_price);
                        RenderTarget.DrawLine(
                            new SharpDX.Vector2(x_origin, pocY),
                            new SharpDX.Vector2(x_origin + width, pocY),
                            dxPoc, 2f);
                    }
                }
            }
        }

        private void RenderVALine(float fpX, int binWidth, double price,
            ChartScale chartScale, SharpDX.Direct2D1.Brush dxBrush, bool isPOC)
        {
            float y = chartScale.GetYByValue(price);
            float x1 = fpX + binWidth + 1;
            float x2 = Extend_VA_Levels
                ? ChartPanel.X + ChartPanel.W
                : fpX + VP_Width - 1;

            RenderTarget.DrawLine(
                new SharpDX.Vector2(x1, y),
                new SharpDX.Vector2(x2, y),
                dxBrush, 1f);
        }

        private void RenderVAText(float height, double price,
            ChartScale chartScale, SharpDX.Direct2D1.Brush dxBrush,
            string label, float overrideY)
        {
            float y = overrideY > 0 ? overrideY : chartScale.GetYByValue(price);
            float x = ChartPanel.X + ChartPanel.W - 50;

            using (var tf = new SharpDX.DirectWrite.TextFormat(
                NinjaTrader.Core.Globals.DirectWriteFactory, "Segoe UI Light", 10))
            using (var layout = new SharpDX.DirectWrite.TextLayout(
                NinjaTrader.Core.Globals.DirectWriteFactory,
                label, tf, 50, 15))
            {
                RenderTarget.DrawTextLayout(
                    new SharpDX.Vector2(x, y - 7), layout, dxBrush);
            }
        }

        #endregion

        #region Algorithm Helpers

        private void CalcVolumeProfile(SortedDictionary<double, long> vp,
            ref double vah, ref double val_price, ref double poc)
        {
            if (vp.Count == 0) return;

            // ── Fill gaps ──
            double minP = Instrument.MasterInstrument.RoundToTickSize(vp.Keys.First());
            double maxP = Instrument.MasterInstrument.RoundToTickSize(vp.Keys.Last());
            var filled = new SortedDictionary<double, long>();
            for (double p = minP; p <= maxP;
                 p = Instrument.MasterInstrument.RoundToTickSize(p + TickSize))
            {
                double key = Instrument.MasterInstrument.RoundToTickSize(p);
                filled[key] = vp.ContainsKey(key) ? vp[key] : 0;
            }

            // ── Find POC ──
            double[] prices = new double[filled.Count];
            long[]   vols   = new long[filled.Count];
            long maxVol = 0;
            long totalVol = 0;
            int pocIdx = 0, idx = 0;

            foreach (var kv in filled)
            {
                if (kv.Value >= maxVol)
                {
                    maxVol = kv.Value;
                    poc    = kv.Key;
                    pocIdx = idx;
                }
                totalVol += kv.Value;
                prices[idx] = kv.Key;
                vols[idx]   = kv.Value;
                idx++;
            }

            // ── Group by ticks_per_level from POC outward ──
            var grouped = new SortedDictionary<double, long>();
            grouped[prices[pocIdx]] = vols[pocIdx];

            // Above POC
            long accum = 0; int cnt = 0; double lastKey = 0;
            for (int i = pocIdx + 1; i < filled.Count; i++)
            {
                lastKey = prices[i];
                accum += vols[i];
                cnt++;
                if (cnt == Ticks_Per_Level) { grouped[lastKey] = accum; accum = 0; cnt = 0; }
            }
            if (cnt > 0) grouped[lastKey] = accum;

            // Below POC
            accum = 0; cnt = 0;
            for (int i = pocIdx - 1; i >= 0; i--)
            {
                lastKey = prices[i];
                accum += vols[i];
                cnt++;
                if (cnt == Ticks_Per_Level) { grouped[lastKey] = accum; accum = 0; cnt = 0; }
            }
            if (cnt > 0) grouped[lastKey] = accum;

            // ── Rebuild arrays ──
            double[] gPrices = new double[grouped.Count];
            long[]   gVols   = new long[grouped.Count];
            int gPocIdx = 0; idx = 0;
            foreach (var kv in grouped)
            {
                if (kv.Key == poc) gPocIdx = idx;
                gPrices[idx] = kv.Key;
                gVols[idx]   = kv.Value;
                idx++;
            }

            // ── Expand value area ──
            long vaTarget = (long)(totalVol * (double)VA_Percent / 100.0);
            long vaAccum  = maxVol;
            int  upper    = gPocIdx, lower = gPocIdx;
            int  gMax     = grouped.Count - 1;

            while (vaAccum < vaTarget)
            {
                bool hitTop = upper + 1 > gMax;
                bool hitBot = lower - 1 < 0;

                if (!hitTop) upper++;
                if (!hitBot) lower--;

                if (!hitTop && !hitBot)
                {
                    if (gVols[upper] == gVols[lower])
                    {
                        vaAccum += gVols[upper] + gVols[lower];
                    }
                    else if (gVols[upper] > gVols[lower])
                    {
                        vaAccum += gVols[upper];
                        lower++;
                    }
                    else
                    {
                        vaAccum += gVols[lower];
                        upper--;
                    }
                }
                else if (!hitTop)
                    vaAccum += gVols[upper];
                else if (!hitBot)
                    vaAccum += gVols[lower];
                else
                    break;
            }

            vah       = gPrices[upper];
            val_price = gPrices[lower];
        }

        private Dictionary<int, VPInfoByPrice> AddByPrice(
            Dictionary<int, VPInfoByPrice> a,
            Dictionary<int, VPInfoByPrice> b)
        {
            var result = new Dictionary<int, VPInfoByPrice>();
            foreach (int key in a.Keys)
                result[key] = new VPInfoByPrice(a[key]);
            foreach (int key in b.Keys)
            {
                if (result.ContainsKey(key))
                    result[key].Add(b[key]);
                else
                    result[key] = new VPInfoByPrice(b[key]);
            }
            return result;
        }

        /// <summary>
        /// Adds source entries directly into the target dictionary (in-place).
        /// Avoids creating a full copy every bar — only touches the few price
        /// levels present in 'source' (typically 5-20 for a 1-second bar).
        /// </summary>
        private void AddByPriceInPlace(
            Dictionary<int, VPInfoByPrice> target,
            Dictionary<int, VPInfoByPrice> source)
        {
            foreach (int key in source.Keys)
            {
                VPInfoByPrice srcVal = source[key];
                VPInfoByPrice existing;
                if (target.TryGetValue(key, out existing))
                    existing.Add(srcVal);
                else
                    target[key] = new VPInfoByPrice(srcVal);
            }
        }

        private void ShiftAndInsert(
            ref Dictionary<int, VPInfoByPrice>[] saved,
            Dictionary<int, VPInfoByPrice> current)
        {
            if (saved == null) return;
            int n = saved.Length;
            if (n > 1)
            {
                for (int i = n - 2; i >= 0; i--)
                {
                    saved[i + 1] = new Dictionary<int, VPInfoByPrice>();
                    foreach (int key in saved[i].Keys.ToList())
                        saved[i + 1][key] = saved[i][key];
                }
            }
            saved[0] = new Dictionary<int, VPInfoByPrice>();
            foreach (int key in current.Keys.ToList())
                saved[0][key] = current[key];
        }

        private Dictionary<int, VPInfoByPrice> BuildFromBarRange(int start, int end)
        {
            var result = new Dictionary<int, VPInfoByPrice>();
            for (int i = start; i <= end; i++)
            {
                Dictionary<int, VPInfoByPrice> src = null;
                if (by_price_series != null && by_price_series.IsValidDataPointAt(i))
                    src = by_price_series.GetValueAt(i);
                else if (i == BarsArray[1].Count - 1)
                    src = current_bar_by_price;

                if (src == null) continue;

                // Accumulate in-place — no intermediate dictionary copies
                foreach (int key in src.Keys)
                {
                    VPInfoByPrice srcVal = src[key];
                    VPInfoByPrice existing;
                    if (result.TryGetValue(key, out existing))
                        existing.Add(srcVal);
                    else
                        result[key] = new VPInfoByPrice(srcVal);
                }
            }
            return result;
        }

        private void RebuildMinutesLookback()
        {
            combined_by_price = new Dictionary<int, VPInfoByPrice>();
            have_enough_lookback = false;

            // BIP 1 uses 1-second bars. Look back Num_Minutes minutes by time.
            DateTime lookbackTime = Time[0].AddMinutes(-Num_Minutes);
            int startBar = BarsArray[1].GetBar(lookbackTime);
            if (startBar < 0 || startBar >= CurrentBar) return;

            for (int i = startBar; i < CurrentBar; i++)
            {
                if (by_price_series != null && by_price_series.IsValidDataPointAt(i))
                    combined_by_price = AddByPrice(combined_by_price, by_price_series.GetValueAt(i));
            }
            have_enough_lookback = combined_by_price.Count > 0;
        }

        private void ProcessSwing()
        {
            if (zz == null || CurrentBar <= 100) return;
            if (zz.ZigZagHigh[0] == 0.0 || zz.ZigZagLow[0] == 0.0) return;

            int highBar = zz.HighBar(0, 1, 1440);
            int lowBar  = zz.LowBar(0, 1, 1440);
            if (highBar == -1 || lowBar == -1) return;

            if (lowBar == 1)
            {
                if (last_changebar == 1)
                    current_HL_barnum = CurrentBar - 1;
                else if (last_changebar == 2)
                {
                    prior_HL_barnum = current_HL_barnum;
                    current_HL_barnum = CurrentBar - 1;
                }
                last_changebar = 1;
            }
            if (highBar == 1)
            {
                if (last_changebar == 2)
                    current_HL_barnum = CurrentBar - 1;
                else if (last_changebar == 1)
                {
                    prior_HL_barnum = current_HL_barnum;
                    current_HL_barnum = CurrentBar - 1;
                }
                last_changebar = 2;
            }
            swing_start_bar = prior_HL_barnum;
            swing_end_bar   = CurrentBar;
        }


        private DateTime GetRTHStartTime(DateTime sessionStart, DateTime sessionEnd)
        {
            DateTime etTime;
            if (Session_Type == VPSessionType.Custom)
                etTime = RTH_StartTime;
            else
                etTime = DateTime.Parse("09:30:00", CultureInfo.InvariantCulture);

            // Place the ET time-of-day onto the correct calendar date
            DateTime baseDate = (etTime.TimeOfDay >= sessionStart.TimeOfDay)
                ? sessionStart : sessionEnd;
            DateTime candidate = new DateTime(baseDate.Year, baseDate.Month, baseDate.Day,
                etTime.Hour, etTime.Minute, 0);

            // Convert from Eastern Time to chart timezone (unless Custom — already local)
            if (Session_Type != VPSessionType.Custom)
            {
                try
                {
                    TimeZoneInfo eastern = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
                    candidate = TimeZoneInfo.ConvertTime(candidate, eastern,
                        NinjaTrader.Core.Globals.GeneralOptions.TimeZoneInfo);
                }
                catch { }
            }

            return candidate;
        }

        private bool GetAlignment(bool isDelta)
        {
            if (!Show_DeltaProfile || !Show_VolProfile)
                return VP_Alignment == VPHorizontalPosition.Right;

            switch (Relative_Placement)
            {
                case VPProfilePlacement.SideBySide_DV_Flipped:
                    return isDelta
                        ? VP_Alignment == VPHorizontalPosition.Right
                        : VP_Alignment != VPHorizontalPosition.Right;
                case VPProfilePlacement.SideBySide_VD_Flipped:
                    return !isDelta
                        ? VP_Alignment == VPHorizontalPosition.Right
                        : VP_Alignment != VPHorizontalPosition.Right;
                default:
                    return VP_Alignment == VPHorizontalPosition.Right;
            }
        }

        /// <summary>
        /// Згладжує totalVolume для візуального рендерингу.
        /// POC/VA рахуються з оригінальних даних — тут тільки візуал.
        /// </summary>
        private Dictionary<int, VPInfoByPrice> SmoothVolumeData(
            Dictionary<int, VPInfoByPrice> data)
        {
            if (Smooth_Passes <= 0 || data.Count < 3) return data;

            var keys = data.Keys.ToList();
            keys.Sort();
            int count = keys.Count;

            // Збираємо totalVolume
            float[] vols = new float[count];
            for (int i = 0; i < count; i++)
                vols[i] = data[keys[i]].totalVolume;

            // N проходів ядра (1,2,1)/4
            for (int pass = 0; pass < Smooth_Passes; pass++)
            {
                float[] tmp = new float[count];
                for (int i = 0; i < count; i++)
                {
                    float prev = (i > 0) ? vols[i - 1] : vols[i];
                    float next = (i < count - 1) ? vols[i + 1] : vols[i];
                    tmp[i] = (prev + 2f * vols[i] + next) / 4f;
                }
                vols = tmp;
            }

            // Обрізка хвостів: < 0.5% від макс
            float maxV = 0;
            for (int i = 0; i < count; i++)
                if (vols[i] > maxV) maxV = vols[i];
            float threshold = maxV * 0.005f;

            // Копія зі згладженим totalVolume
            var result = new Dictionary<int, VPInfoByPrice>();
            for (int i = 0; i < count; i++)
            {
                if (vols[i] < threshold) continue;
                var orig = data[keys[i]];
                result[keys[i]] = new VPInfoByPrice(
                    orig.delta, orig.askVolume, orig.bidVolume, (long)vols[i]);
            }

            return result;
        }

        private static System.Windows.Media.SolidColorBrush MkBrush(byte r, byte g, byte b)
        {
            var brush = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }

        private static System.Windows.Media.Brush CloneBrushOpacity(
            System.Windows.Media.Brush source, int opacityPercent)
        {
            var b = source.Clone();
            b.Opacity = opacityPercent / 100.0;
            b.Freeze();
            return b;
        }

        #endregion

        #region Properties

        // ═══════════════════════════════════════════════════════════════
        //  1. Settings
        // ═══════════════════════════════════════════════════════════════

        [NinjaScriptProperty]
        [RefreshProperties(RefreshProperties.All)]
        [Display(Name = "Тип профілю", GroupName = "1. Налаштування", Order = 1)]
        public VPDurationType VP_Type { get; set; }

        [RefreshProperties(RefreshProperties.All)]
        [Display(Name = "Ринок (Pit Open)", GroupName = "1. Налаштування", Order = 2)]
        public VPSessionType Session_Type { get; set; }

        [Display(Name = "Початок RTH", GroupName = "1. Налаштування", Order = 3)]
        [PropertyEditor("NinjaTrader.Gui.Tools.TimeEditorKey")]
        public DateTime RTH_StartTime { get; set; }

        [Display(Name = "Початок", GroupName = "1. Налаштування", Order = 4)]
        [PropertyEditor("NinjaTrader.Gui.Tools.ChartAnchorTimeEditor")]
        public DateTime VP_StartTime { get; set; }

        [Display(Name = "Кінець", GroupName = "1. Налаштування", Order = 5)]
        [PropertyEditor("NinjaTrader.Gui.Tools.ChartAnchorTimeEditor")]
        public DateTime VP_EndTime { get; set; }

        [Range(0.0005, 1000.0)]
        [Display(Name = "Розмір ротації (пункти)", GroupName = "1. Налаштування", Order = 11)]
        public double Rotation_Size { get; set; }

        [RefreshProperties(RefreshProperties.All)]
        [Display(Name = "Показати початок свінга", GroupName = "1. Налаштування", Order = 12)]
        public bool Show_Swing_Start { get; set; }

        [RefreshProperties(RefreshProperties.All)]
        [Display(Name = "Підсвітити поточний свінг", GroupName = "1. Налаштування", Order = 16)]
        public bool Highlight_Swing { get; set; }

        [Range(1, int.MaxValue)]
        [Display(Name = "Кількість хвилин", GroupName = "1. Налаштування", Order = 31)]
        public int Num_Minutes { get; set; }

        [Range(2, int.MaxValue)]
        [Display(Name = "Кількість днів", GroupName = "1. Налаштування", Order = 32)]
        public int Num_Days { get; set; }

        [Range(2, int.MaxValue)]
        [Display(Name = "Кількість тижнів", GroupName = "1. Налаштування", Order = 33)]
        public int Num_Weeks { get; set; }

        [RefreshProperties(RefreshProperties.All)]
        [Display(Name = "Накласти на чарт", GroupName = "1. Налаштування", Order = 41)]
        public bool OverlayOnChart { get; set; }

        [XmlIgnore]
        [Display(Name = "Колір підсвітки", GroupName = "1. Налаштування", Order = 42)]
        public System.Windows.Media.Brush RegionHLX_Color { get; set; }
        [Browsable(false)] public string RegionHLX_Color_Serializable
        { get { return Serialize.BrushToString(RegionHLX_Color); } set { RegionHLX_Color = Serialize.StringToBrush(value); } }

        [Range(0, 100)]
        [Display(Name = "Прозорість підсвітки (0-100)", GroupName = "1. Налаштування", Order = 43)]
        public int RegionHLX_Opacity { get; set; }

        // ═══════════════════════════════════════════════════════════════
        //  2. Profile Configuration
        // ═══════════════════════════════════════════════════════════════

        [Range(1, int.MaxValue)]
        [Display(Name = "Стиснення (тіки)", GroupName = "2. Профіль", Order = 1)]
        public int Ticks_Per_Level { get; set; }

        [Range(0, 10)]
        [Display(Name = "Згладжування", GroupName = "2. Профіль", Order = 2)]
        public int Smooth_Passes { get; set; }

        [Display(Name = "Позиція", GroupName = "2. Профіль", Order = 3)]
        public VPHorizontalPosition VP_Placement { get; set; }

        [Display(Name = "Вирівнювання", GroupName = "2. Профіль", Order = 3)]
        public VPHorizontalPosition VP_Alignment { get; set; }

        [Range(25, 500)]
        [Display(Name = "Ширина (пікселі)", GroupName = "2. Профіль", Order = 4)]
        public int VP_Width { get; set; }

        [Range(0, 5000)]
        [Display(Name = "Відступ (пікселі)", GroupName = "2. Профіль", Order = 5)]
        public int VP_Offset { get; set; }

        [Display(Name = "Взаємне розташування", GroupName = "2. Профіль", Order = 6)]
        public VPProfilePlacement Relative_Placement { get; set; }


        [Display(Name = "Шрифт значень", GroupName = "2. Профіль", Order = 7)]
        public NinjaTrader.Gui.Tools.SimpleFont Value_Text_Font { get; set; }

        // ═══════════════════════════════════════════════════════════════
        //  5. Background
        // ═══════════════════════════════════════════════════════════════

        [RefreshProperties(RefreshProperties.All)]
        [Display(Name = "Показати фон", GroupName = "5. Фон", Order = 1)]
        public bool Show_Background { get; set; }

        [XmlIgnore]
        [Display(Name = "Колір фону", GroupName = "5. Фон", Order = 2)]
        public System.Windows.Media.Brush Background_Color { get; set; }
        [Browsable(false)] public string Background_Color_Serializable
        { get { return Serialize.BrushToString(Background_Color); } set { Background_Color = Serialize.StringToBrush(value); } }

        [Range(0, 100)]
        [Display(Name = "Прозорість фону (0-100)", GroupName = "5. Фон", Order = 3)]
        public int Background_Opacity { get; set; }

        // ═══════════════════════════════════════════════════════════════
        //  3. Delta Profile
        // ═══════════════════════════════════════════════════════════════

        [RefreshProperties(RefreshProperties.All)]
        [Display(Name = "Показати Delta профіль", GroupName = "3. Delta профіль", Order = 1)]
        public bool Show_DeltaProfile { get; set; }

        [RefreshProperties(RefreshProperties.All)]
        [Display(Name = "Макс. Delta (POC)", GroupName = "3. Delta профіль", Order = 2)]
        public bool Show_DPOC { get; set; }

        [RefreshProperties(RefreshProperties.All)]
        [Display(Name = "Показати значення", GroupName = "3. Delta профіль", Order = 3)]
        public bool Show_Delta_Values { get; set; }

        [XmlIgnore]
        [Display(Name = "Позитивна Delta", GroupName = "3. Delta профіль", Order = 11)]
        public System.Windows.Media.Brush UpDelta_Color { get; set; }
        [Browsable(false)] public string UpDelta_Color_Serializable
        { get { return Serialize.BrushToString(UpDelta_Color); } set { UpDelta_Color = Serialize.StringToBrush(value); } }

        [Range(0, 100)]
        [Display(Name = "Прозорість позит. Delta (0-100)", GroupName = "3. Delta профіль", Order = 12)]
        public int UpDelta_Opacity { get; set; }

        [XmlIgnore]
        [Display(Name = "Негативна Delta", GroupName = "3. Delta профіль", Order = 13)]
        public System.Windows.Media.Brush DnDelta_Color { get; set; }
        [Browsable(false)] public string DnDelta_Color_Serializable
        { get { return Serialize.BrushToString(DnDelta_Color); } set { DnDelta_Color = Serialize.StringToBrush(value); } }

        [Range(0, 100)]
        [Display(Name = "Прозорість негат. Delta (0-100)", GroupName = "3. Delta профіль", Order = 14)]
        public int DnDelta_Opacity { get; set; }

        [XmlIgnore]
        [Display(Name = "Макс. Delta", GroupName = "3. Delta профіль", Order = 15)]
        public System.Windows.Media.Brush DPOC_Color { get; set; }
        [Browsable(false)] public string DPOC_Color_Serializable
        { get { return Serialize.BrushToString(DPOC_Color); } set { DPOC_Color = Serialize.StringToBrush(value); } }

        [Range(0, 100)]
        [Display(Name = "Прозорість макс. Delta (0-100)", GroupName = "3. Delta профіль", Order = 16)]
        public int DPOC_Opacity { get; set; }

        [XmlIgnore]
        [Display(Name = "Колір тексту позит. Delta", GroupName = "3. Delta профіль", Order = 17)]
        public System.Windows.Media.Brush DeltaUp_Text_Color { get; set; }
        [Browsable(false)] public string DeltaUp_Text_Color_Serializable
        { get { return Serialize.BrushToString(DeltaUp_Text_Color); } set { DeltaUp_Text_Color = Serialize.StringToBrush(value); } }

        [XmlIgnore]
        [Display(Name = "Колір тексту негат. Delta", GroupName = "3. Delta профіль", Order = 18)]
        public System.Windows.Media.Brush DeltaDn_Text_Color { get; set; }
        [Browsable(false)] public string DeltaDn_Text_Color_Serializable
        { get { return Serialize.BrushToString(DeltaDn_Text_Color); } set { DeltaDn_Text_Color = Serialize.StringToBrush(value); } }

        // ═══════════════════════════════════════════════════════════════
        //  4. Volume Profile
        // ═══════════════════════════════════════════════════════════════

        [RefreshProperties(RefreshProperties.All)]
        [Display(Name = "Показати Volume профіль", GroupName = "4. Volume профіль", Order = 1)]
        public bool Show_VolProfile { get; set; }

        [RefreshProperties(RefreshProperties.All)]
        [Display(Name = "Макс. об'єм (POC)", GroupName = "4. Volume профіль", Order = 2)]
        public bool Show_VPOC { get; set; }

        [RefreshProperties(RefreshProperties.All)]
        [Display(Name = "Показати значення", GroupName = "4. Volume профіль", Order = 3)]
        public bool Show_Volume_Values { get; set; }

        [RefreshProperties(RefreshProperties.All)]
        [Display(Name = "Показати VA", GroupName = "4. Volume профіль", Order = 4)]
        public bool Show_VA { get; set; }

        [Range(1, 100)]
        [Display(Name = "VA (%)", GroupName = "4. Volume профіль", Order = 5)]
        public int VA_Percent { get; set; }

        [XmlIgnore]
        [Display(Name = "Volume профіль", GroupName = "4. Volume профіль", Order = 11)]
        public System.Windows.Media.Brush Volume_Color { get; set; }
        [Browsable(false)] public string Volume_Color_Serializable
        { get { return Serialize.BrushToString(Volume_Color); } set { Volume_Color = Serialize.StringToBrush(value); } }

        [Range(0, 100)]
        [Display(Name = "Прозорість Volume (0-100)", GroupName = "4. Volume профіль", Order = 12)]
        public int Volume_Opacity { get; set; }

        [XmlIgnore]
        [Display(Name = "VA", GroupName = "4. Volume профіль", Order = 13)]
        public System.Windows.Media.Brush VA_Color { get; set; }
        [Browsable(false)] public string VA_Color_Serializable
        { get { return Serialize.BrushToString(VA_Color); } set { VA_Color = Serialize.StringToBrush(value); } }

        [Range(0, 100)]
        [Display(Name = "Прозорість VA (0-100)", GroupName = "4. Volume профіль", Order = 14)]
        public int VA_Opacity { get; set; }

        [XmlIgnore]
        [Display(Name = "Макс. об'єм", GroupName = "4. Volume профіль", Order = 15)]
        public System.Windows.Media.Brush VPOC_Color { get; set; }
        [Browsable(false)] public string VPOC_Color_Serializable
        { get { return Serialize.BrushToString(VPOC_Color); } set { VPOC_Color = Serialize.StringToBrush(value); } }

        [Range(0, 100)]
        [Display(Name = "Прозорість макс. об'єму (0-100)", GroupName = "4. Volume профіль", Order = 16)]
        public int VPOC_Opacity { get; set; }

        [XmlIgnore]
        [Display(Name = "Колір тексту значень", GroupName = "4. Volume профіль", Order = 17)]
        public System.Windows.Media.Brush Volume_Text_Color { get; set; }
        [Browsable(false)] public string Volume_Text_Color_Serializable
        { get { return Serialize.BrushToString(Volume_Text_Color); } set { Volume_Text_Color = Serialize.StringToBrush(value); } }

        [Display(Name = "Лінії VA", GroupName = "4. Volume профіль", Order = 21)]
        public bool Show_VA_Levels { get; set; }

        [Display(Name = "Продовжити лінії VA", GroupName = "4. Volume профіль", Order = 22)]
        public bool Extend_VA_Levels { get; set; }

        [Display(Name = "Мітки VA", GroupName = "4. Volume профіль", Order = 23)]
        public bool Show_VA_Labels { get; set; }

        [Display(Name = "Шрифт міток VA", GroupName = "4. Volume профіль", Order = 24)]
        public SimpleFont VA_Label_Font { get; set; }

        #endregion
    }

    // ═══ Helper classes ═══════════════════════════════════════════════

    public class VPInfoByPrice
    {
        public long delta;
        public long askVolume;
        public long bidVolume;
        public long totalVolume;

        public VPInfoByPrice(long delta, long askVol, long bidVol, long totalVol)
        {
            this.delta       = delta;
            this.askVolume   = askVol;
            this.bidVolume   = bidVol;
            this.totalVolume = totalVol;
        }

        public VPInfoByPrice(VPInfoByPrice other)
        {
            delta       = other.delta;
            askVolume   = other.askVolume;
            bidVolume   = other.bidVolume;
            totalVolume = other.totalVolume;
        }

        public void Add(long d, long ask, long bid, long total)
        {
            delta       += d;
            askVolume   += ask;
            bidVolume   += bid;
            totalVolume += total;
        }

        public void Add(VPInfoByPrice other)
        {
            delta       += other.delta;
            askVolume   += other.askVolume;
            bidVolume   += other.bidVolume;
            totalVolume += other.totalVolume;
        }
    }

    public class VPPriceBinMap
    {
        public double n;  // next price above bin
        public double h;  // highest price in bin
        public double l;  // lowest price in bin
        public int    bn; // bin number

        public VPPriceBinMap(double price, int ticksPerBlock, NinjaTrader.Cbi.Instrument instrument)
        {
            double tickSize = instrument.MasterInstrument.TickSize;
            int tickIndex   = (int)instrument.MasterInstrument.RoundToTickSize(price / tickSize);
            int half        = ticksPerBlock / 2;
            int binNum      = (tickIndex + half) / ticksPerBlock;

            double binLow  = instrument.MasterInstrument.RoundToTickSize(
                (double)(binNum * ticksPerBlock - half) * tickSize);
            double binHigh = instrument.MasterInstrument.RoundToTickSize(
                binLow + (double)(ticksPerBlock - 1) * tickSize);
            double binNext = instrument.MasterInstrument.RoundToTickSize(
                binLow + (double)ticksPerBlock * tickSize);

            h  = binHigh;
            l  = binLow;
            n  = binNext;
            bn = binNum;
        }
    }

    // ═══ Enums with display-name TypeConverters ═══════════════════

    [TypeConverter(typeof(VPDurationType_Converter))]
    public enum VPDurationType
    {
        CurrentSwing, CurrentRTH, CurrentETH,
        CurrentWeek, CurrentMonth,
        StartTime, TimeSpan, Minutes, Days, Weeks,
        EntireChart, VisibleArea, HighlightedRange
    }

    public class VPDurationType_Converter : TypeConverter
    {
        private static readonly Dictionary<VPDurationType, string> _map = new Dictionary<VPDurationType, string>
        {
            { VPDurationType.CurrentSwing,    "Поточний свінг" },
            { VPDurationType.CurrentRTH,      "Поточна RTH сесія" },
            { VPDurationType.CurrentETH,      "Поточна ETH сесія" },
            { VPDurationType.CurrentWeek,     "Поточний тиждень" },
            { VPDurationType.CurrentMonth,    "Поточний місяць" },
            { VPDurationType.StartTime,       "З вказаного часу" },
            { VPDurationType.TimeSpan,        "Часовий діапазон" },
            { VPDurationType.Minutes,         "Хвилини" },
            { VPDurationType.Days,            "Дні" },
            { VPDurationType.Weeks,           "Тижні" },
            { VPDurationType.EntireChart,     "Весь чарт" },
            { VPDurationType.VisibleArea,     "Видима область" },
            { VPDurationType.HighlightedRange,"Виділений діапазон" },
        };

        public override bool GetStandardValuesSupported(ITypeDescriptorContext c)
        {
            return true;
        }

        public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext c)
        {
            return new StandardValuesCollection(_map.Keys.ToList());
        }

        public override bool CanConvertFrom(ITypeDescriptorContext c, Type t)
        {
            return t == typeof(string) || base.CanConvertFrom(c, t);
        }

        public override bool CanConvertTo(ITypeDescriptorContext c, Type t)
        {
            return t == typeof(string) || base.CanConvertTo(c, t);
        }

        public override object ConvertTo(ITypeDescriptorContext c, CultureInfo ci, object v, Type t)
        {
            if (t == typeof(string) && v is VPDurationType)
            {
                VPDurationType e = (VPDurationType)v;
                if (_map.ContainsKey(e))
                    return _map[e];
            }
            return base.ConvertTo(c, ci, v, t);
        }

        public override object ConvertFrom(ITypeDescriptorContext c, CultureInfo ci, object v)
        {
            if (v is string)
            {
                string s = (string)v;
                foreach (var kv in _map)
                    if (kv.Value == s) return kv.Key;
            }
            return base.ConvertFrom(c, ci, v);
        }
    }

    public enum VPHorizontalPosition { Left, Right }

    [TypeConverter(typeof(VPProfilePlacement_Converter))]
    public enum VPProfilePlacement
    {
        SideBySide_VD, SideBySide_DV,
        SideBySide_VD_Flipped, SideBySide_DV_Flipped,
        Overlay_DV, Overlay_VD
    }

    public class VPProfilePlacement_Converter : TypeConverter
    {
        private static readonly Dictionary<VPProfilePlacement, string> _map = new Dictionary<VPProfilePlacement, string>
        {
            { VPProfilePlacement.SideBySide_VD,        "Рядом: Volume → Delta" },
            { VPProfilePlacement.SideBySide_DV,        "Рядом: Delta → Volume" },
            { VPProfilePlacement.SideBySide_VD_Flipped,"Рядом: Volume → Delta (отзеркалено)" },
            { VPProfilePlacement.SideBySide_DV_Flipped,"Рядом: Delta → Volume (отзеркалено)" },
            { VPProfilePlacement.Overlay_DV,           "Наложение: Delta поверх Volume" },
            { VPProfilePlacement.Overlay_VD,           "Наложение: Volume поверх Delta" },
        };

        public override bool GetStandardValuesSupported(ITypeDescriptorContext c)
        {
            return true;
        }

        public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext c)
        {
            return new StandardValuesCollection(_map.Keys.ToList());
        }

        public override bool CanConvertFrom(ITypeDescriptorContext c, Type t)
        {
            return t == typeof(string) || base.CanConvertFrom(c, t);
        }

        public override bool CanConvertTo(ITypeDescriptorContext c, Type t)
        {
            return t == typeof(string) || base.CanConvertTo(c, t);
        }

        public override object ConvertTo(ITypeDescriptorContext c, CultureInfo ci, object v, Type t)
        {
            if (t == typeof(string) && v is VPProfilePlacement)
            {
                VPProfilePlacement e = (VPProfilePlacement)v;
                if (_map.ContainsKey(e))
                    return _map[e];
            }
            return base.ConvertTo(c, ci, v, t);
        }

        public override object ConvertFrom(ITypeDescriptorContext c, CultureInfo ci, object v)
        {
            if (v is string)
            {
                string s = (string)v;
                foreach (var kv in _map)
                    if (kv.Value == s) return kv.Key;
            }
            return base.ConvertFrom(c, ci, v);
        }
    }


    [TypeConverter(typeof(VPSessionType_Converter))]
    public enum VPSessionType
    {
        Indices, Custom
    }

    public class VPSessionType_Converter : TypeConverter
    {
        private static readonly Dictionary<VPSessionType, string> _map = new Dictionary<VPSessionType, string>
        {
            { VPSessionType.Indices, "Indices (9:30 ET)" },
            { VPSessionType.Custom,  "Custom" },
        };

        public override bool GetStandardValuesSupported(ITypeDescriptorContext c) { return true; }
        public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext c)
        { return new StandardValuesCollection(_map.Keys.ToList()); }
        public override bool CanConvertFrom(ITypeDescriptorContext c, Type t) { return t == typeof(string) || base.CanConvertFrom(c, t); }
        public override bool CanConvertTo(ITypeDescriptorContext c, Type t) { return t == typeof(string) || base.CanConvertTo(c, t); }

        public override object ConvertTo(ITypeDescriptorContext c, CultureInfo ci, object v, Type t)
        {
            if (t == typeof(string) && v is VPSessionType)
            {
                VPSessionType e = (VPSessionType)v;
                if (_map.ContainsKey(e)) return _map[e];
            }
            return base.ConvertTo(c, ci, v, t);
        }

        public override object ConvertFrom(ITypeDescriptorContext c, CultureInfo ci, object v)
        {
            if (v is string)
            {
                string s = (string)v;
                foreach (var kv in _map)
                    if (kv.Value == s) return kv.Key;
            }
            return base.ConvertFrom(c, ci, v);
        }
    }

    // ═══ TypeConverter for conditional property visibility ═══════════
    // Must extend IndicatorBaseConverter (NOT TypeConverter) so NinjaTrader
    // recognises this converter and lets it control the property grid.
    public class VP_PropertiesConverter : IndicatorBaseConverter
    {
        // ── CRITICAL: must return true or NinjaTrader will never call GetProperties ──
        public override bool GetPropertiesSupported(ITypeDescriptorContext context)
        { return true; }

        public override PropertyDescriptorCollection GetProperties(
            ITypeDescriptorContext context, object component, Attribute[] attrs)
        {
            VP vp = component as VP;

            // Get ALL properties from the NinjaTrader base converter first
            PropertyDescriptorCollection props =
                base.GetPropertiesSupported(context)
                    ? base.GetProperties(context, component, attrs)
                    : TypeDescriptor.GetProperties(component, attrs);

            if (vp == null || props == null)
                return props;

            // ── Remove NinjaTrader default "Misc" properties ──
            foreach (string name in new[] {
                "BarsPeriod", "InputPlot", "Plots", "SelectedValueSeries",
                "Calculate", "MaximumBarsLookBack", "IsAutoScale",
                "IsOverlay", "DisplayInDataBox", "ScaleJustification",
                "PaintPriceMarkers", "IsSuspendedWhileInactive" })
            {
                PropertyDescriptor pd = props[name];
                if (pd != null) props.Remove(pd);
            }

            // ════════════════════════════════════════════════════════
            //  Save references to ALL conditionally-visible properties,
            //  remove them, then add back only those that should appear.
            // ════════════════════════════════════════════════════════

            VPDurationType vpType = vp.VP_Type;

            // ── 1. Settings conditionals ──
            PropertyDescriptor pdSessType    = props["Session_Type"];
            PropertyDescriptor pdRTH_Start   = props["RTH_StartTime"];
            PropertyDescriptor pdVP_Start    = props["VP_StartTime"];
            PropertyDescriptor pdVP_End      = props["VP_EndTime"];
            PropertyDescriptor pdRotation    = props["Rotation_Size"];
            PropertyDescriptor pdSwingStart  = props["Show_Swing_Start"];
            PropertyDescriptor pdHighlight   = props["Highlight_Swing"];
            PropertyDescriptor pdMinutes     = props["Num_Minutes"];
            PropertyDescriptor pdDays        = props["Num_Days"];
            PropertyDescriptor pdWeeks       = props["Num_Weeks"];
            PropertyDescriptor pdOverlay     = props["OverlayOnChart"];
            PropertyDescriptor pdHLX_Color   = props["RegionHLX_Color"];
            PropertyDescriptor pdHLX_ColorS  = props["RegionHLX_Color_Serializable"];
            PropertyDescriptor pdHLX_Op      = props["RegionHLX_Opacity"];

            // Remove all conditional settings properties
            if (pdSessType   != null) props.Remove(pdSessType);
            if (pdRTH_Start  != null) props.Remove(pdRTH_Start);
            if (pdVP_Start   != null) props.Remove(pdVP_Start);
            if (pdVP_End     != null) props.Remove(pdVP_End);
            if (pdRotation   != null) props.Remove(pdRotation);
            if (pdSwingStart != null) props.Remove(pdSwingStart);
            if (pdHighlight  != null) props.Remove(pdHighlight);
            if (pdMinutes    != null) props.Remove(pdMinutes);
            if (pdDays       != null) props.Remove(pdDays);
            if (pdWeeks      != null) props.Remove(pdWeeks);
            if (pdOverlay    != null) props.Remove(pdOverlay);
            if (pdHLX_Color  != null) props.Remove(pdHLX_Color);
            if (pdHLX_ColorS != null) props.Remove(pdHLX_ColorS);
            if (pdHLX_Op     != null) props.Remove(pdHLX_Op);

            // Add back only the ones relevant to the selected VP_Type
            if (vpType == VPDurationType.CurrentRTH)
            {
                if (pdSessType  != null) props.Add(pdSessType);
                // Show RTH_StartTime only when Custom is selected
                if (vp.Session_Type == VPSessionType.Custom && pdRTH_Start != null)
                    props.Add(pdRTH_Start);
            }

            if (vpType == VPDurationType.CurrentSwing)
            {
                if (pdRotation   != null) props.Add(pdRotation);
                if (pdSwingStart != null) props.Add(pdSwingStart);
                if (pdHighlight  != null) props.Add(pdHighlight);
            }

            if (vpType == VPDurationType.HighlightedRange)
            {
                if (pdOverlay    != null) props.Add(pdOverlay);
                if (pdHLX_Color  != null) props.Add(pdHLX_Color);
                if (pdHLX_ColorS != null) props.Add(pdHLX_ColorS);
                if (pdHLX_Op     != null) props.Add(pdHLX_Op);

                // When overlay is on, hide Placement/Width/Offset/Alignment
                // (profile spans the highlighted region directly)
                if (vp.OverlayOnChart)
                {
                    PropertyDescriptor p;
                    p = props["VP_Placement"]; if (p != null) props.Remove(p);
                    p = props["VP_Alignment"]; if (p != null) props.Remove(p);
                    p = props["VP_Width"];     if (p != null) props.Remove(p);
                    p = props["VP_Offset"];    if (p != null) props.Remove(p);
                }
            }

            if ((vpType == VPDurationType.StartTime || vpType == VPDurationType.TimeSpan) && pdVP_Start != null)
                props.Add(pdVP_Start);
            if (vpType == VPDurationType.TimeSpan && pdVP_End != null)
                props.Add(pdVP_End);

            if (vpType == VPDurationType.Minutes && pdMinutes != null)
                props.Add(pdMinutes);
            if (vpType == VPDurationType.Days && pdDays != null)
                props.Add(pdDays);
            if (vpType == VPDurationType.Weeks && pdWeeks != null)
                props.Add(pdWeeks);

            // ── 5. Background conditionals ──
            PropertyDescriptor pdBgColor  = props["Background_Color"];
            PropertyDescriptor pdBgSer    = props["Background_Color_Serializable"];
            PropertyDescriptor pdBgOp     = props["Background_Opacity"];

            if (pdBgColor != null) props.Remove(pdBgColor);
            if (pdBgSer   != null) props.Remove(pdBgSer);
            if (pdBgOp    != null) props.Remove(pdBgOp);

            if (vp.Show_Background)
            {
                if (pdBgColor != null) props.Add(pdBgColor);
                if (pdBgSer   != null) props.Add(pdBgSer);
                if (pdBgOp    != null) props.Add(pdBgOp);
            }

            // ── 3. Delta Profile conditionals ──
            PropertyDescriptor pdDPOC_Color   = props["DPOC_Color"];
            PropertyDescriptor pdDPOC_Ser     = props["DPOC_Color_Serializable"];
            PropertyDescriptor pdDPOC_Op      = props["DPOC_Opacity"];
            PropertyDescriptor pdDUpText      = props["DeltaUp_Text_Color"];
            PropertyDescriptor pdDUpTextS     = props["DeltaUp_Text_Color_Serializable"];
            PropertyDescriptor pdDDnText      = props["DeltaDn_Text_Color"];
            PropertyDescriptor pdDDnTextS     = props["DeltaDn_Text_Color_Serializable"];

            if (pdDPOC_Color   != null) props.Remove(pdDPOC_Color);
            if (pdDPOC_Ser     != null) props.Remove(pdDPOC_Ser);
            if (pdDPOC_Op      != null) props.Remove(pdDPOC_Op);
            if (pdDUpText      != null) props.Remove(pdDUpText);
            if (pdDUpTextS     != null) props.Remove(pdDUpTextS);
            if (pdDDnText      != null) props.Remove(pdDDnText);
            if (pdDDnTextS     != null) props.Remove(pdDDnTextS);

            if (vp.Show_Delta_Values)
            {
                if (pdDUpText  != null) props.Add(pdDUpText);
                if (pdDUpTextS != null) props.Add(pdDUpTextS);
                if (pdDDnText  != null) props.Add(pdDDnText);
                if (pdDDnTextS != null) props.Add(pdDDnTextS);
            }

            if (vp.Show_DPOC)
            {
                if (pdDPOC_Color != null) props.Add(pdDPOC_Color);
                if (pdDPOC_Ser   != null) props.Add(pdDPOC_Ser);
                if (pdDPOC_Op    != null) props.Add(pdDPOC_Op);
            }

            // ── 4. Volume Profile conditionals ──
            PropertyDescriptor pdVA_Pct      = props["VA_Percent"];
            PropertyDescriptor pdVA_Color    = props["VA_Color"];
            PropertyDescriptor pdVA_Ser      = props["VA_Color_Serializable"];
            PropertyDescriptor pdVA_Op       = props["VA_Opacity"];
            PropertyDescriptor pdVPOC_Color  = props["VPOC_Color"];
            PropertyDescriptor pdVPOC_Ser    = props["VPOC_Color_Serializable"];
            PropertyDescriptor pdVPOC_Op     = props["VPOC_Opacity"];
            PropertyDescriptor pdVALevels    = props["Show_VA_Levels"];
            PropertyDescriptor pdExtVA       = props["Extend_VA_Levels"];
            PropertyDescriptor pdVALabels    = props["Show_VA_Labels"];
            PropertyDescriptor pdVAFont      = props["VA_Label_Font"];
            PropertyDescriptor pdVolText     = props["Volume_Text_Color"];
            PropertyDescriptor pdVolTextS    = props["Volume_Text_Color_Serializable"];

            if (pdVA_Pct     != null) props.Remove(pdVA_Pct);
            if (pdVA_Color   != null) props.Remove(pdVA_Color);
            if (pdVA_Ser     != null) props.Remove(pdVA_Ser);
            if (pdVA_Op      != null) props.Remove(pdVA_Op);
            if (pdVPOC_Color != null) props.Remove(pdVPOC_Color);
            if (pdVPOC_Ser   != null) props.Remove(pdVPOC_Ser);
            if (pdVPOC_Op    != null) props.Remove(pdVPOC_Op);
            if (pdVALevels   != null) props.Remove(pdVALevels);
            if (pdExtVA      != null) props.Remove(pdExtVA);
            if (pdVALabels   != null) props.Remove(pdVALabels);
            if (pdVAFont     != null) props.Remove(pdVAFont);
            if (pdVolText    != null) props.Remove(pdVolText);
            if (pdVolTextS   != null) props.Remove(pdVolTextS);

            if (vp.Show_VA)
            {
                if (pdVA_Pct   != null) props.Add(pdVA_Pct);
                if (pdVA_Color != null) props.Add(pdVA_Color);
                if (pdVA_Ser   != null) props.Add(pdVA_Ser);
                if (pdVA_Op    != null) props.Add(pdVA_Op);
                if (pdVALevels != null) props.Add(pdVALevels);
                if (pdExtVA    != null) props.Add(pdExtVA);
                if (pdVALabels != null) props.Add(pdVALabels);
                if (vp.Show_VA_Labels && pdVAFont != null)
                    props.Add(pdVAFont);
            }

            if (vp.Show_VPOC)
            {
                if (pdVPOC_Color != null) props.Add(pdVPOC_Color);
                if (pdVPOC_Ser   != null) props.Add(pdVPOC_Ser);
                if (pdVPOC_Op    != null) props.Add(pdVPOC_Op);
            }

            if (vp.Show_Volume_Values)
            {
                if (pdVolText  != null) props.Add(pdVolText);
                if (pdVolTextS != null) props.Add(pdVolTextS);
            }

            return props;
        }
    }
}
