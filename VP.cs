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

        private Dictionary<int, VPPriceBinMap> bin_price_map;
        private Dictionary<int, long> current_bar_by_price;
        private Dictionary<int, long> combined_by_price;
        private Dictionary<int, long> sum_by_price;
        private Dictionary<int, long>[] saved_by_price;

        private SessionIterator sessIter;
        private DateTime bip1_session_start;
        private DateTime bip1_session_end;
        private DateTime profile_start_time;
        private DateTime profile_end_time;

        private double profile_high;
        private double profile_low;

        private System.Windows.Media.Brush volume_color;
        private System.Windows.Media.Brush vpoc_color;
        private System.Windows.Media.Brush bg_color;

        private int saved_period_lookback;

        private object sync_lock = new object();

        #endregion

        #region OnStateChange

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description                 = "Volume Profile — Volume з POC/VA";
                Name                        = "VP";
                IsOverlay                   = true;
                Calculate                   = Calculate.OnPriceChange;
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
                Num_Days                    = 5;

                // ── 2. Profile Configuration ──
                Ticks_Per_Level             = 1;
                Smooth_Passes               = 0;
                VP_Placement                = VPHorizontalPosition.Right;
                VP_Alignment                = VPHorizontalPosition.Left;
                VP_Width                    = 200;
                VP_Offset                   = 0;

                // ── 3. Volume Profile ──
                Show_VPOC                   = true;
                Show_VA                     = false;
                VA_Percent                  = 70;
                VA_Dimming                  = 80;
                Volume_Color                = MkBrush(105, 105, 105);
                VPOC_Color                  = MkBrush(32, 178, 170);
                Volume_Opacity              = 30;
                VPOC_Opacity                = 40;

                // ── 4. Background ──
                Show_Background             = false;
                Background_Color            = MkBrush(20, 22, 28);
                Background_Opacity          = 50;
            }
            else if (State == State.Configure)
            {
                bin_price_map        = new Dictionary<int, VPPriceBinMap>();
                current_bar_by_price = new Dictionary<int, long>();
                combined_by_price    = new Dictionary<int, long>();
                sum_by_price         = new Dictionary<int, long>();
                profile_high         = double.MinValue;
                profile_low          = double.MaxValue;

                bool resetEOD = BarsArray[0].IsResetOnNewTradingDay;
                AddVolumetric("", BarsPeriodType.Minute, 1,
                    VolumetricDeltaType.BidAsk, 1, resetEOD);

                volume_color = CloneBrushOpacity(Volume_Color, Volume_Opacity);
                vpoc_color   = CloneBrushOpacity(VPOC_Color, VPOC_Opacity);
                bg_color     = CloneBrushOpacity(Background_Color, Background_Opacity);

                saved_period_lookback = 0;
                if (VP_Type == VPDurationType.Days)
                    saved_period_lookback = Num_Days;

                if (saved_period_lookback > 1)
                {
                    int n = saved_period_lookback - 1;
                    saved_by_price = new Dictionary<int, long>[n];
                    for (int i = 0; i < n; i++)
                        saved_by_price[i] = new Dictionary<int, long>();
                }
            }
            else if (State == State.DataLoaded)
            {
                sessIter = new SessionIterator(BarsArray[1]);
                profile_start_time = DateTime.MinValue;
                profile_end_time   = DateTime.MinValue;
            }
        }

        #endregion

        #region OnBarUpdate

        protected override void OnBarUpdate()
        {
            if (BarsArray[0] == null || BarsArray[1] == null
                || CurrentBars[0] == -1 || CurrentBars[1] == -1)
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
                    current_bar_by_price = new Dictionary<int, long>();
                    combined_by_price    = new Dictionary<int, long>();
                }
                else if (VP_Type == VPDurationType.Days)
                {
                    profile_start_time = bip1_session_start;
                    profile_end_time   = bip1_session_end;
                    ShiftAndInsert(ref saved_by_price, combined_by_price);
                    current_bar_by_price = new Dictionary<int, long>();
                    combined_by_price    = new Dictionary<int, long>();
                }
                else if (VP_Type == VPDurationType.CurrentWeek)
                {
                    DateTime tradingDay = sessIter.GetTradingDay(barTime);
                    if (tradingDay.DayOfWeek == DayOfWeek.Monday
                        || (CurrentBar > 0 && Time[0].DayOfWeek < Time[1].DayOfWeek))
                    {
                        profile_start_time = bip1_session_start;
                        current_bar_by_price = new Dictionary<int, long>();
                        combined_by_price    = new Dictionary<int, long>();
                    }
                    else if (profile_start_time == DateTime.MinValue)
                    {
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
                        profile_start_time = bip1_session_start;
                        current_bar_by_price = new Dictionary<int, long>();
                        combined_by_price    = new Dictionary<int, long>();
                    }
                    else if (profile_start_time == DateTime.MinValue)
                    {
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
                current_bar_by_price = new Dictionary<int, long>();
                combined_by_price    = new Dictionary<int, long>();
            }

            // ── Skip if outside profile window ──
            if (!(profile_start_time < Time[0]) || !(Time[0] <= profile_end_time))
                return;

            // ══════════════════════════════════════════════════════════
            //  AGGREGATE TICK DATA INTO PRICE BINS
            // ══════════════════════════════════════════════════════════

            // ── Save previous bar data on new bar (realtime) ──
            if (State == State.Realtime && IsFirstTickOfBar)
            {
                AddByPriceInPlace(combined_by_price, current_bar_by_price);
            }

            // ── Extract per-price data from Volumetric bar ──
            var barsType = BarsArray[1].BarsType as
                NinjaTrader.NinjaScript.BarsTypes.VolumetricBarsType;
            if (barsType == null)
                return;

            var vol = barsType.Volumes[CurrentBars[1]];

            double barLow  = Lows[1][0];
            double barHigh = Highs[1][0];

            if (barLow == 0 || barHigh == 0 || barLow > barHigh)
                return;

            bool directAccum = State == State.Historical
                && CurrentBar < BarsArray[1].Count - 1;

            Dictionary<int, long> target;
            if (directAccum)
            {
                target = combined_by_price;
            }
            else
            {
                current_bar_by_price = new Dictionary<int, long>();
                target = current_bar_by_price;
            }

            double tickSz = Instrument.MasterInstrument.TickSize;
            int tickLow  = (int)Math.Round(barLow  / tickSz);
            int tickHigh = (int)Math.Round(barHigh / tickSz);

            for (int tick = tickLow; tick <= tickHigh; tick++)
            {
                double price = tick * tickSz;
                long askVol, bidVol;
                try
                {
                    askVol = vol.GetAskVolumeForPrice(price);
                    bidVol = vol.GetBidVolumeForPrice(price);
                }
                catch { continue; }

                long totalVol = askVol + bidVol;
                if (totalVol == 0) continue;

                var bin = new VPPriceBinMap(price, Ticks_Per_Level, Instrument);

                if (target.ContainsKey(bin.bn))
                    target[bin.bn] += totalVol;
                else
                    target[bin.bn] = totalVol;

                if (!bin_price_map.ContainsKey(bin.bn))
                    bin_price_map[bin.bn] = bin;
            }

            // ── Historical bar: accumulate immediately ──
            if ((State == State.Historical && CurrentBar < BarsArray[1].Count - 1)
                || Calculate == Calculate.OnBarClose)
            {
                if (!directAccum)
                {
                    AddByPriceInPlace(combined_by_price, current_bar_by_price);
                    current_bar_by_price = new Dictionary<int, long>();
                }
            }

            // ── Build final sum ──
            if (State == State.Realtime || CurrentBar >= BarsArray[1].Count - 2)
            {
                lock (sync_lock)
                {
                    sum_by_price = AddByPrice(combined_by_price, current_bar_by_price);

                    if (VP_Type == VPDurationType.Days && saved_by_price != null)
                    {
                        for (int k = 0; k < saved_by_price.Length; k++)
                            sum_by_price = AddByPrice(saved_by_price[k], sum_by_price);
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

            Dictionary<int, long> renderData;

            if (VP_Type == VPDurationType.Days)
            {
                lock (sync_lock) { renderData = sum_by_price; }
            }
            else
            {
                if (sum_by_price == null || sum_by_price.Count == 0) return;
                lock (sync_lock) { renderData = sum_by_price; }
            }

            if (renderData == null || renderData.Count == 0)
                return;

            // ── Compute VA/POC ──
            double vah = 0, val2 = 0, poc = 0;
            if (Show_VPOC || Show_VA)
            {
                var sortedVol = new SortedDictionary<double, long>();
                foreach (int key in renderData.Keys)
                {
                    if (!bin_price_map.ContainsKey(key)) continue;
                    double h = bin_price_map[key].h;
                    if (!sortedVol.ContainsKey(h))
                        sortedVol[h] = renderData[key];
                    else
                        sortedVol[h] += renderData[key];
                }
                CalcVolumeProfile(sortedVol, ref vah, ref val2, ref poc);
            }

            // ── Згладжування для візуалу ──
            var volDisplayData = SmoothVolumeData(renderData);

            // ── Layout ──
            int totalWidth = VP_Width;
            int x_origin = (VP_Placement == VPHorizontalPosition.Right)
                ? ChartPanel.X + ChartPanel.W - totalWidth - VP_Offset
                : ChartPanel.X + VP_Offset;

            bool alignRight = VP_Alignment == VPHorizontalPosition.Right;

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
            RenderVolumeLadder(volDisplayData, chartScale, x_origin, totalWidth,
                alignRight, vah, val2, poc);
        }

        #endregion

        #region Rendering Helpers

        private void RenderVolumeLadder(
            Dictionary<int, long> data,
            ChartScale chartScale,
            float x_origin, int width, bool alignRight,
            double vah, double val_price, double poc_price)
        {
            if (width < 1) return;

            float dimFactor = (100 - VA_Dimming) / 100f;

            using (var dxFill = volume_color.ToDxBrush(RenderTarget))
            using (var dxPoc  = vpoc_color.ToDxBrush(RenderTarget))
            {
                List<int> keys = data.Keys.ToList();
                keys.Sort();

                // ── First pass: max value ──
                long maxVal = 0;
                int minBin = int.MaxValue, maxBin = int.MinValue;

                foreach (int key in keys)
                {
                    if (!data.ContainsKey(key) || !bin_price_map.ContainsKey(key)) continue;
                    maxVal = Math.Max(maxVal, data[key]);
                    maxBin = Math.Max(maxBin, key);
                    minBin = Math.Min(minBin, key);
                }

                if (maxVal == 0) return;

                float prevY = -1;

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

                    long barValue = data[i];

                    int barWidth = (maxVal > 0)
                        ? (int)Math.Round((double)barValue / maxVal * width)
                        : 0;

                    float yTop = chartScale.GetYByValue(
                        n - 0.5 * BarsArray[0].Instrument.MasterInstrument.TickSize);
                    float barHeight = Math.Abs(
                        chartScale.GetYByValue(l) - chartScale.GetYByValue(n));
                    barHeight = (prevY == -1) ? barHeight : (prevY - yTop);
                    prevY = yTop;

                    float xPos = x_origin;
                    if (alignRight)
                        xPos += (width - barWidth);

                    var rect = new SharpDX.RectangleF(xPos, yTop, barWidth, barHeight);

                    // ── Fill: POC, VA, or dimmed ──
                    if (Show_VPOC && h == poc_price)
                    {
                        RenderTarget.FillRectangle(rect, dxPoc);
                    }
                    else if (Show_VA)
                    {
                        bool inVA = h >= val_price && h <= vah;
                        if (inVA)
                        {
                            RenderTarget.FillRectangle(rect, dxFill);
                        }
                        else
                        {
                            // Dimmed: зменшуємо opacity
                            dxFill.Opacity = dimFactor;
                            RenderTarget.FillRectangle(rect, dxFill);
                            dxFill.Opacity = 1f;
                        }
                    }
                    else
                    {
                        RenderTarget.FillRectangle(rect, dxFill);
                    }
                }

                // ── POC line ──
                if (Show_VPOC && poc_price > 0)
                {
                    float pocY = chartScale.GetYByValue(poc_price);
                    RenderTarget.DrawLine(
                        new SharpDX.Vector2(x_origin, pocY),
                        new SharpDX.Vector2(x_origin + width, pocY),
                        dxPoc, 2f);
                }
            }
        }

        #endregion

        #region Algorithm Helpers

        private void CalcVolumeProfile(SortedDictionary<double, long> vp,
            ref double vah, ref double val_price, ref double poc)
        {
            if (vp.Count == 0) return;

            double minP = Instrument.MasterInstrument.RoundToTickSize(vp.Keys.First());
            double maxP = Instrument.MasterInstrument.RoundToTickSize(vp.Keys.Last());
            var filled = new SortedDictionary<double, long>();
            for (double p = minP; p <= maxP;
                 p = Instrument.MasterInstrument.RoundToTickSize(p + TickSize))
            {
                double key = Instrument.MasterInstrument.RoundToTickSize(p);
                filled[key] = vp.ContainsKey(key) ? vp[key] : 0;
            }

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

            var grouped = new SortedDictionary<double, long>();
            grouped[prices[pocIdx]] = vols[pocIdx];

            long accum = 0; int cnt = 0; double lastKey = 0;
            for (int i = pocIdx + 1; i < filled.Count; i++)
            {
                lastKey = prices[i];
                accum += vols[i];
                cnt++;
                if (cnt == Ticks_Per_Level) { grouped[lastKey] = accum; accum = 0; cnt = 0; }
            }
            if (cnt > 0) grouped[lastKey] = accum;

            accum = 0; cnt = 0;
            for (int i = pocIdx - 1; i >= 0; i--)
            {
                lastKey = prices[i];
                accum += vols[i];
                cnt++;
                if (cnt == Ticks_Per_Level) { grouped[lastKey] = accum; accum = 0; cnt = 0; }
            }
            if (cnt > 0) grouped[lastKey] = accum;

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

        private Dictionary<int, long> AddByPrice(
            Dictionary<int, long> a,
            Dictionary<int, long> b)
        {
            var result = new Dictionary<int, long>();
            foreach (var kv in a)
                result[kv.Key] = kv.Value;
            foreach (var kv in b)
            {
                if (result.ContainsKey(kv.Key))
                    result[kv.Key] += kv.Value;
                else
                    result[kv.Key] = kv.Value;
            }
            return result;
        }

        private void AddByPriceInPlace(
            Dictionary<int, long> target,
            Dictionary<int, long> source)
        {
            foreach (var kv in source)
            {
                if (target.ContainsKey(kv.Key))
                    target[kv.Key] += kv.Value;
                else
                    target[kv.Key] = kv.Value;
            }
        }

        private void ShiftAndInsert(
            ref Dictionary<int, long>[] saved,
            Dictionary<int, long> current)
        {
            if (saved == null) return;
            int n = saved.Length;
            if (n > 1)
            {
                for (int i = n - 2; i >= 0; i--)
                    saved[i + 1] = new Dictionary<int, long>(saved[i]);
            }
            saved[0] = new Dictionary<int, long>(current);
        }

        private DateTime GetRTHStartTime(DateTime sessionStart, DateTime sessionEnd)
        {
            DateTime etTime;
            if (Session_Type == VPSessionType.Custom)
                etTime = RTH_StartTime;
            else
                etTime = DateTime.Parse("09:30:00", CultureInfo.InvariantCulture);

            DateTime baseDate = (etTime.TimeOfDay >= sessionStart.TimeOfDay)
                ? sessionStart : sessionEnd;
            DateTime candidate = new DateTime(baseDate.Year, baseDate.Month, baseDate.Day,
                etTime.Hour, etTime.Minute, 0);

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

        private Dictionary<int, long> SmoothVolumeData(Dictionary<int, long> data)
        {
            if (Smooth_Passes <= 0 || data.Count < 3) return data;

            var keys = data.Keys.ToList();
            keys.Sort();
            int count = keys.Count;

            float[] vols = new float[count];
            for (int i = 0; i < count; i++)
                vols[i] = data[keys[i]];

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

            float maxV = 0;
            for (int i = 0; i < count; i++)
                if (vols[i] > maxV) maxV = vols[i];
            float threshold = maxV * 0.005f;

            var result = new Dictionary<int, long>();
            for (int i = 0; i < count; i++)
            {
                if (vols[i] < threshold) continue;
                result[keys[i]] = (long)vols[i];
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

        [Range(2, int.MaxValue)]
        [Display(Name = "Кількість днів", GroupName = "1. Налаштування", Order = 4)]
        public int Num_Days { get; set; }

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

        [Display(Name = "Вирівнювання", GroupName = "2. Профіль", Order = 4)]
        public VPHorizontalPosition VP_Alignment { get; set; }

        [Range(25, 500)]
        [Display(Name = "Ширина (пікселі)", GroupName = "2. Профіль", Order = 5)]
        public int VP_Width { get; set; }

        [Range(0, 5000)]
        [Display(Name = "Відступ (пікселі)", GroupName = "2. Профіль", Order = 6)]
        public int VP_Offset { get; set; }

        // ═══════════════════════════════════════════════════════════════
        //  3. Volume Profile
        // ═══════════════════════════════════════════════════════════════

        [RefreshProperties(RefreshProperties.All)]
        [Display(Name = "Макс. об'єм (POC)", GroupName = "3. Volume профіль", Order = 1)]
        public bool Show_VPOC { get; set; }

        [RefreshProperties(RefreshProperties.All)]
        [Display(Name = "Показати VA", GroupName = "3. Volume профіль", Order = 2)]
        public bool Show_VA { get; set; }

        [Range(50, 90)]
        [Display(Name = "VA (%)", GroupName = "3. Volume профіль", Order = 3)]
        public int VA_Percent { get; set; }

        [Range(0, 100)]
        [Display(Name = "Затемнення VA (%)", GroupName = "3. Volume профіль", Order = 4)]
        public int VA_Dimming { get; set; }

        [XmlIgnore]
        [Display(Name = "Volume профіль", GroupName = "3. Volume профіль", Order = 11)]
        public System.Windows.Media.Brush Volume_Color { get; set; }
        [Browsable(false)] public string Volume_Color_Serializable
        { get { return Serialize.BrushToString(Volume_Color); } set { Volume_Color = Serialize.StringToBrush(value); } }

        [Range(0, 100)]
        [Display(Name = "Прозорість Volume (0-100)", GroupName = "3. Volume профіль", Order = 12)]
        public int Volume_Opacity { get; set; }

        [XmlIgnore]
        [Display(Name = "Макс. об'єм", GroupName = "3. Volume профіль", Order = 13)]
        public System.Windows.Media.Brush VPOC_Color { get; set; }
        [Browsable(false)] public string VPOC_Color_Serializable
        { get { return Serialize.BrushToString(VPOC_Color); } set { VPOC_Color = Serialize.StringToBrush(value); } }

        [Range(0, 100)]
        [Display(Name = "Прозорість макс. об'єму (0-100)", GroupName = "3. Volume профіль", Order = 14)]
        public int VPOC_Opacity { get; set; }

        // ═══════════════════════════════════════════════════════════════
        //  4. Background
        // ═══════════════════════════════════════════════════════════════

        [RefreshProperties(RefreshProperties.All)]
        [Display(Name = "Показати фон", GroupName = "4. Фон", Order = 1)]
        public bool Show_Background { get; set; }

        [XmlIgnore]
        [Display(Name = "Колір фону", GroupName = "4. Фон", Order = 2)]
        public System.Windows.Media.Brush Background_Color { get; set; }
        [Browsable(false)] public string Background_Color_Serializable
        { get { return Serialize.BrushToString(Background_Color); } set { Background_Color = Serialize.StringToBrush(value); } }

        [Range(0, 100)]
        [Display(Name = "Прозорість фону (0-100)", GroupName = "4. Фон", Order = 3)]
        public int Background_Opacity { get; set; }

        #endregion
    }

    // ═══ Helper classes ═══════════════════════════════════════════════

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

    // ═══ Enums ════════════════════════════════════════════════════════

    [TypeConverter(typeof(VPDurationType_Converter))]
    public enum VPDurationType
    {
        CurrentRTH, CurrentETH, CurrentWeek, CurrentMonth, Days
    }

    public class VPDurationType_Converter : TypeConverter
    {
        private static readonly Dictionary<VPDurationType, string> _map = new Dictionary<VPDurationType, string>
        {
            { VPDurationType.CurrentRTH,   "Поточна RTH сесія" },
            { VPDurationType.CurrentETH,   "Поточна ETH сесія" },
            { VPDurationType.CurrentWeek,  "Поточний тиждень" },
            { VPDurationType.CurrentMonth, "Поточний місяць" },
            { VPDurationType.Days,         "Дні" },
        };

        public override bool GetStandardValuesSupported(ITypeDescriptorContext c) => true;
        public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext c)
            => new StandardValuesCollection(_map.Keys.ToList());
        public override bool CanConvertFrom(ITypeDescriptorContext c, Type t)
            => t == typeof(string) || base.CanConvertFrom(c, t);
        public override bool CanConvertTo(ITypeDescriptorContext c, Type t)
            => t == typeof(string) || base.CanConvertTo(c, t);
        public override object ConvertTo(ITypeDescriptorContext c, CultureInfo ci, object v, Type t)
        {
            if (t == typeof(string) && v is VPDurationType e && _map.ContainsKey(e))
                return _map[e];
            return base.ConvertTo(c, ci, v, t);
        }
        public override object ConvertFrom(ITypeDescriptorContext c, CultureInfo ci, object v)
        {
            if (v is string s)
                foreach (var kv in _map)
                    if (kv.Value == s) return kv.Key;
            return base.ConvertFrom(c, ci, v);
        }
    }

    public enum VPHorizontalPosition { Left, Right }

    [TypeConverter(typeof(VPSessionType_Converter))]
    public enum VPSessionType { Indices, Custom }

    public class VPSessionType_Converter : TypeConverter
    {
        private static readonly Dictionary<VPSessionType, string> _map = new Dictionary<VPSessionType, string>
        {
            { VPSessionType.Indices, "Indices (9:30 ET)" },
            { VPSessionType.Custom,  "Custom" },
        };

        public override bool GetStandardValuesSupported(ITypeDescriptorContext c) => true;
        public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext c)
            => new StandardValuesCollection(_map.Keys.ToList());
        public override bool CanConvertFrom(ITypeDescriptorContext c, Type t)
            => t == typeof(string) || base.CanConvertFrom(c, t);
        public override bool CanConvertTo(ITypeDescriptorContext c, Type t)
            => t == typeof(string) || base.CanConvertTo(c, t);
        public override object ConvertTo(ITypeDescriptorContext c, CultureInfo ci, object v, Type t)
        {
            if (t == typeof(string) && v is VPSessionType e && _map.ContainsKey(e))
                return _map[e];
            return base.ConvertTo(c, ci, v, t);
        }
        public override object ConvertFrom(ITypeDescriptorContext c, CultureInfo ci, object v)
        {
            if (v is string s)
                foreach (var kv in _map)
                    if (kv.Value == s) return kv.Key;
            return base.ConvertFrom(c, ci, v);
        }
    }

    // ═══ PropertiesConverter ══════════════════════════════════════════
    public class VP_PropertiesConverter : IndicatorBaseConverter
    {
        public override bool GetPropertiesSupported(ITypeDescriptorContext context)
        { return true; }

        public override PropertyDescriptorCollection GetProperties(
            ITypeDescriptorContext context, object component, Attribute[] attrs)
        {
            VP vp = component as VP;

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

            VPDurationType vpType = vp.VP_Type;

            // ── 1. Settings conditionals ──
            PropertyDescriptor pdSessType = props["Session_Type"];
            PropertyDescriptor pdRTH_Start = props["RTH_StartTime"];
            PropertyDescriptor pdDays = props["Num_Days"];

            if (pdSessType  != null) props.Remove(pdSessType);
            if (pdRTH_Start != null) props.Remove(pdRTH_Start);
            if (pdDays      != null) props.Remove(pdDays);

            if (vpType == VPDurationType.CurrentRTH)
            {
                if (pdSessType != null) props.Add(pdSessType);
                if (vp.Session_Type == VPSessionType.Custom && pdRTH_Start != null)
                    props.Add(pdRTH_Start);
            }

            if (vpType == VPDurationType.Days && pdDays != null)
                props.Add(pdDays);

            // ── 3. VA conditionals ──
            PropertyDescriptor pdVA_Pct = props["VA_Percent"];
            PropertyDescriptor pdVA_Dim = props["VA_Dimming"];

            if (pdVA_Pct != null) props.Remove(pdVA_Pct);
            if (pdVA_Dim != null) props.Remove(pdVA_Dim);

            if (vp.Show_VA)
            {
                if (pdVA_Pct != null) props.Add(pdVA_Pct);
                if (pdVA_Dim != null) props.Add(pdVA_Dim);
            }

            // ── 4. Background conditionals ──
            PropertyDescriptor pdBgColor = props["Background_Color"];
            PropertyDescriptor pdBgSer   = props["Background_Color_Serializable"];
            PropertyDescriptor pdBgOp    = props["Background_Opacity"];

            if (pdBgColor != null) props.Remove(pdBgColor);
            if (pdBgSer   != null) props.Remove(pdBgSer);
            if (pdBgOp    != null) props.Remove(pdBgOp);

            if (vp.Show_Background)
            {
                if (pdBgColor != null) props.Add(pdBgColor);
                if (pdBgSer   != null) props.Add(pdBgSer);
                if (pdBgOp    != null) props.Add(pdBgOp);
            }

            // ── VPOC conditionals ──
            PropertyDescriptor pdVPOC_Color = props["VPOC_Color"];
            PropertyDescriptor pdVPOC_Ser   = props["VPOC_Color_Serializable"];
            PropertyDescriptor pdVPOC_Op    = props["VPOC_Opacity"];

            if (pdVPOC_Color != null) props.Remove(pdVPOC_Color);
            if (pdVPOC_Ser   != null) props.Remove(pdVPOC_Ser);
            if (pdVPOC_Op    != null) props.Remove(pdVPOC_Op);

            if (vp.Show_VPOC)
            {
                if (pdVPOC_Color != null) props.Add(pdVPOC_Color);
                if (pdVPOC_Ser   != null) props.Add(pdVPOC_Ser);
                if (pdVPOC_Op    != null) props.Add(pdVPOC_Op);
            }

            return props;
        }
    }
}
