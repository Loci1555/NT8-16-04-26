using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Net.Http;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using SharpDX;
using SharpDX.DirectWrite;

namespace NinjaTrader.NinjaScript.Indicators.Loci
{
	public class GEX : Indicator
	{
		#region Enums

		public enum GexTicker  { NDX, SPX }
		public enum AggPeriod  { Full, Zero, One }
		public enum PanelPos   { Left, Right }

		#endregion

		#region Кеш (static, спільний між інстансами)

		private class StrikeData
		{
			public double Strike;
			public double GexVol;
			public double GexOI;
			public double[] Priors;
		}

		private class GexData
		{
			public DateTime  FetchTime;
			public long      Timestamp;
			public double    Spot;
			public double    ZeroGamma;
			public double    MajorPosOI;
			public double    MajorNegOI;
			public double    MajorPosVol;
			public double    MajorNegVol;
			public double    NetGexOI;
			public double    NetGexVol;
			public double    Premium;
			public List<StrikeData> Strikes = new List<StrikeData>();
		}

		private static readonly object                      cacheLock    = new object();
		private static readonly Dictionary<string, GexData> dataCache    = new Dictionary<string, GexData>();
		private static readonly HashSet<string>             fetchingKeys = new HashSet<string>();
		private static readonly HttpClient                  httpClient   = new HttpClient();

		static GEX()
		{
			httpClient.DefaultRequestHeaders.Add("User-Agent", "LociNT8/1.0");
			httpClient.DefaultRequestHeaders.Add("Accept", "application/json");
		}

		#endregion

		#region Поля

		private GexData  currentData;
		private double   currentPremium;
		private bool     profileVisible = true;
		private bool     isOffline;

		// Конвертовані рівні (futures ціни)
		private double lvlZeroGamma;
		private double lvlMajorPosOI;
		private double lvlMajorNegOI;
		private double lvlMajorPosVol;
		private double lvlMajorNegVol;

		// Strike grid
		private readonly List<GridLine> gridLines = new List<GridLine>();
		private struct GridLine
		{
			public double FuturesPrice;
			public int    IndexStrike;
			public int    Tier;
		}

		// DX ресурси
		private SharpDX.Direct2D1.Brush dxOIPos, dxOINeg, dxVolPos, dxVolNeg;
		private SharpDX.Direct2D1.Brush dxZeroGamma, dxMajorPosOI, dxMajorNegOI, dxMajorPosVol, dxMajorNegVol;
		private SharpDX.Direct2D1.Brush dxGrid100, dxGrid50, dxGrid25, dxGrid5;
		private SharpDX.Direct2D1.Brush dxLabel, dxInfo;
		private SharpDX.Direct2D1.Brush dxPrior1m, dxPrior5m, dxPrior10m, dxPrior15m, dxPrior30m;
		private TextFormat fmtLabel, fmtInfo;

		#endregion

		#region Параметри — 1. GexBot Setup

		[Display(Name = "API Key", Order = 0, GroupName = "1. GexBot Setup")]
		public string ApiKey { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Ticker", Order = 1, GroupName = "1. GexBot Setup")]
		public GexTicker Ticker { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Aggregation", Order = 2, GroupName = "1. GexBot Setup")]
		public AggPeriod Aggregation { get; set; }

		[Display(Name = "Refresh Interval (sec)", Order = 3, GroupName = "1. GexBot Setup")]
		[Range(5, 600)]
		public int RefreshSeconds { get; set; }

		[Display(Name = "Fetch Start (ET)", Order = 4, GroupName = "1. GexBot Setup")]
		[Range(0, 2359)]
		public int FetchStartET { get; set; }

		[Display(Name = "Fetch End (ET)", Order = 5, GroupName = "1. GexBot Setup")]
		[Range(0, 2359)]
		public int FetchEndET { get; set; }

		#endregion

		#region Параметри — 2. OI Profile

		[Display(Name = "Показувати OI профіль", Order = 0, GroupName = "2. OI Profile")]
		public bool ShowOIProfile { get; set; }

		[Display(Name = "Позиція OI", Order = 1, GroupName = "2. OI Profile")]
		public PanelPos OIPosition { get; set; }

		[Display(Name = "Відступ від краю OI (px)", Order = 2, GroupName = "2. OI Profile")]
		[Range(0, 500)]
		public int OICenterOffset { get; set; }

		[XmlIgnore]
		[Display(Name = "OI Positive", Order = 3, GroupName = "2. OI Profile")]
		public Brush OIPositiveColor { get; set; }
		[Browsable(false)] public string OIPositiveColorSerialize { get { return Serialize.BrushToString(OIPositiveColor); } set { OIPositiveColor = Serialize.StringToBrush(value); } }

		[XmlIgnore]
		[Display(Name = "OI Negative", Order = 4, GroupName = "2. OI Profile")]
		public Brush OINegativeColor { get; set; }
		[Browsable(false)] public string OINegativeColorSerialize { get { return Serialize.BrushToString(OINegativeColor); } set { OINegativeColor = Serialize.StringToBrush(value); } }

		#endregion

		#region Параметри — 3. VOL Profile

		[Display(Name = "Показувати VOL профіль", Order = 0, GroupName = "3. VOL Profile")]
		public bool ShowVolProfile { get; set; }

		[Display(Name = "Позиція VOL", Order = 1, GroupName = "3. VOL Profile")]
		public PanelPos VolPosition { get; set; }

		[Display(Name = "Відступ від краю VOL (px)", Order = 2, GroupName = "3. VOL Profile")]
		[Range(0, 500)]
		public int VolCenterOffset { get; set; }

		[XmlIgnore]
		[Display(Name = "VOL Positive", Order = 3, GroupName = "3. VOL Profile")]
		public Brush VolPositiveColor { get; set; }
		[Browsable(false)] public string VolPositiveColorSerialize { get { return Serialize.BrushToString(VolPositiveColor); } set { VolPositiveColor = Serialize.StringToBrush(value); } }

		[XmlIgnore]
		[Display(Name = "VOL Negative", Order = 4, GroupName = "3. VOL Profile")]
		public Brush VolNegativeColor { get; set; }
		[Browsable(false)] public string VolNegativeColorSerialize { get { return Serialize.BrushToString(VolNegativeColor); } set { VolNegativeColor = Serialize.StringToBrush(value); } }

		#endregion

		#region Параметри — 4. Profile Visual

		[Display(Name = "Profile Width (px)", Order = 0, GroupName = "4. Profile Visual")]
		[Range(50, 500)]
		public int ProfileWidth { get; set; }

		[Display(Name = "Bar Height (px)", Order = 1, GroupName = "4. Profile Visual")]
		[Range(1, 20)]
		public int BarHeight { get; set; }

		[Display(Name = "Min Bar Width (px)", Order = 2, GroupName = "4. Profile Visual")]
		[Range(1, 20)]
		public int MinBarWidth { get; set; }

		[Display(Name = "Profile Contrast", Order = 3, GroupName = "4. Profile Visual")]
		[Range(0.1, 1.0)]
		public double ProfileContrast { get; set; }

		#endregion

		#region Параметри — 5. GEX Levels

		// Zero Gamma
		[Display(Name = "Show Zero Gamma", Order = 0, GroupName = "5. GEX Levels")]
		public bool ShowZeroGamma { get; set; }

		[XmlIgnore]
		[Display(Name = "Zero Gamma Color", Order = 1, GroupName = "5. GEX Levels")]
		public Brush ZeroGammaColor { get; set; }
		[Browsable(false)] public string ZeroGammaColorSerialize { get { return Serialize.BrushToString(ZeroGammaColor); } set { ZeroGammaColor = Serialize.StringToBrush(value); } }

		[Display(Name = "Zero Gamma Dash", Order = 2, GroupName = "5. GEX Levels")]
		public DashStyleHelper ZeroGammaDash { get; set; }

		[Display(Name = "Zero Gamma Width", Order = 3, GroupName = "5. GEX Levels")]
		[Range(1, 5)]
		public int ZeroGammaWidth { get; set; }

		// Major + OI
		[Display(Name = "Show Major + OI", Order = 4, GroupName = "5. GEX Levels")]
		public bool ShowMajorPosOI { get; set; }

		[XmlIgnore]
		[Display(Name = "Major + OI Color", Order = 5, GroupName = "5. GEX Levels")]
		public Brush MajorPosOIColor { get; set; }
		[Browsable(false)] public string MajorPosOIColorSerialize { get { return Serialize.BrushToString(MajorPosOIColor); } set { MajorPosOIColor = Serialize.StringToBrush(value); } }

		[Display(Name = "Major + OI Dash", Order = 6, GroupName = "5. GEX Levels")]
		public DashStyleHelper MajorPosOIDash { get; set; }

		[Display(Name = "Major + OI Width", Order = 7, GroupName = "5. GEX Levels")]
		[Range(1, 5)]
		public int MajorPosOIWidth { get; set; }

		// Major - OI
		[Display(Name = "Show Major - OI", Order = 8, GroupName = "5. GEX Levels")]
		public bool ShowMajorNegOI { get; set; }

		[XmlIgnore]
		[Display(Name = "Major - OI Color", Order = 9, GroupName = "5. GEX Levels")]
		public Brush MajorNegOIColor { get; set; }
		[Browsable(false)] public string MajorNegOIColorSerialize { get { return Serialize.BrushToString(MajorNegOIColor); } set { MajorNegOIColor = Serialize.StringToBrush(value); } }

		[Display(Name = "Major - OI Dash", Order = 10, GroupName = "5. GEX Levels")]
		public DashStyleHelper MajorNegOIDash { get; set; }

		[Display(Name = "Major - OI Width", Order = 11, GroupName = "5. GEX Levels")]
		[Range(1, 5)]
		public int MajorNegOIWidth { get; set; }

		// Major + Vol
		[Display(Name = "Show Major + Vol", Order = 12, GroupName = "5. GEX Levels")]
		public bool ShowMajorPosVol { get; set; }

		[XmlIgnore]
		[Display(Name = "Major + Vol Color", Order = 13, GroupName = "5. GEX Levels")]
		public Brush MajorPosVolColor { get; set; }
		[Browsable(false)] public string MajorPosVolColorSerialize { get { return Serialize.BrushToString(MajorPosVolColor); } set { MajorPosVolColor = Serialize.StringToBrush(value); } }

		[Display(Name = "Major + Vol Dash", Order = 14, GroupName = "5. GEX Levels")]
		public DashStyleHelper MajorPosVolDash { get; set; }

		[Display(Name = "Major + Vol Width", Order = 15, GroupName = "5. GEX Levels")]
		[Range(1, 5)]
		public int MajorPosVolWidth { get; set; }

		// Major - Vol
		[Display(Name = "Show Major - Vol", Order = 16, GroupName = "5. GEX Levels")]
		public bool ShowMajorNegVol { get; set; }

		[XmlIgnore]
		[Display(Name = "Major - Vol Color", Order = 17, GroupName = "5. GEX Levels")]
		public Brush MajorNegVolColor { get; set; }
		[Browsable(false)] public string MajorNegVolColorSerialize { get { return Serialize.BrushToString(MajorNegVolColor); } set { MajorNegVolColor = Serialize.StringToBrush(value); } }

		[Display(Name = "Major - Vol Dash", Order = 18, GroupName = "5. GEX Levels")]
		public DashStyleHelper MajorNegVolDash { get; set; }

		[Display(Name = "Major - Vol Width", Order = 19, GroupName = "5. GEX Levels")]
		[Range(1, 5)]
		public int MajorNegVolWidth { get; set; }

		#endregion

		#region Параметри — 6. Strike Levels

		[Display(Name = "Range (pts)", Order = 0, GroupName = "6. Strike Levels")]
		[Range(50, 2000)]
		public int StrikeRange { get; set; }

		[Display(Name = "Show 100s", Order = 1, GroupName = "6. Strike Levels")]
		public bool Show100s { get; set; }

		[XmlIgnore]
		[Display(Name = "100s Color", Order = 2, GroupName = "6. Strike Levels")]
		public Brush Color100s { get; set; }
		[Browsable(false)] public string Color100sSerialize { get { return Serialize.BrushToString(Color100s); } set { Color100s = Serialize.StringToBrush(value); } }

		[Display(Name = "100s Dash", Order = 3, GroupName = "6. Strike Levels")]
		public DashStyleHelper Dash100s { get; set; }

		[Display(Name = "100s Width", Order = 4, GroupName = "6. Strike Levels")]
		[Range(1, 5)]
		public int Width100s { get; set; }

		[Display(Name = "Show 50s", Order = 5, GroupName = "6. Strike Levels")]
		public bool Show50s { get; set; }

		[XmlIgnore]
		[Display(Name = "50s Color", Order = 6, GroupName = "6. Strike Levels")]
		public Brush Color50s { get; set; }
		[Browsable(false)] public string Color50sSerialize { get { return Serialize.BrushToString(Color50s); } set { Color50s = Serialize.StringToBrush(value); } }

		[Display(Name = "50s Dash", Order = 7, GroupName = "6. Strike Levels")]
		public DashStyleHelper Dash50s { get; set; }

		[Display(Name = "50s Width", Order = 8, GroupName = "6. Strike Levels")]
		[Range(1, 5)]
		public int Width50s { get; set; }

		[Display(Name = "Show 25s", Order = 9, GroupName = "6. Strike Levels")]
		public bool Show25s { get; set; }

		[XmlIgnore]
		[Display(Name = "25s Color", Order = 10, GroupName = "6. Strike Levels")]
		public Brush Color25s { get; set; }
		[Browsable(false)] public string Color25sSerialize { get { return Serialize.BrushToString(Color25s); } set { Color25s = Serialize.StringToBrush(value); } }

		[Display(Name = "25s Dash", Order = 11, GroupName = "6. Strike Levels")]
		public DashStyleHelper Dash25s { get; set; }

		[Display(Name = "25s Width", Order = 12, GroupName = "6. Strike Levels")]
		[Range(1, 5)]
		public int Width25s { get; set; }

		[Display(Name = "Show 5s", Order = 13, GroupName = "6. Strike Levels")]
		public bool Show5s { get; set; }

		[XmlIgnore]
		[Display(Name = "5s Color", Order = 14, GroupName = "6. Strike Levels")]
		public Brush Color5s { get; set; }
		[Browsable(false)] public string Color5sSerialize { get { return Serialize.BrushToString(Color5s); } set { Color5s = Serialize.StringToBrush(value); } }

		[Display(Name = "5s Dash", Order = 15, GroupName = "6. Strike Levels")]
		public DashStyleHelper Dash5s { get; set; }

		[Display(Name = "5s Width", Order = 16, GroupName = "6. Strike Levels")]
		[Range(1, 5)]
		public int Width5s { get; set; }

		#endregion

		#region Параметри — 7. Labels

		[Display(Name = "Show Labels", Order = 0, GroupName = "7. Labels")]
		public bool ShowLabels { get; set; }

		[Display(Name = "Label Position", Order = 1, GroupName = "7. Labels")]
		public PanelPos LabelPosition { get; set; }

		[Display(Name = "Font Size", Order = 2, GroupName = "7. Labels")]
		[Range(6, 24)]
		public int LabelFontSize { get; set; }

		[XmlIgnore]
		[Display(Name = "Label Color", Order = 3, GroupName = "7. Labels")]
		public Brush LabelColor { get; set; }
		[Browsable(false)] public string LabelColorSerialize { get { return Serialize.BrushToString(LabelColor); } set { LabelColor = Serialize.StringToBrush(value); } }

		#endregion

		#region Параметри — 8. Info Block

		[Display(Name = "Show Info Block", Order = 0, GroupName = "8. Info Block")]
		public bool ShowInfoBlock { get; set; }

		[Display(Name = "Font Size", Order = 1, GroupName = "8. Info Block")]
		[Range(6, 24)]
		public int InfoFontSize { get; set; }

		#endregion

		#region Параметри — 9. Prior Dots

		[Display(Name = "Show Prior Dots", Order = 0, GroupName = "9. Prior Dots")]
		public bool ShowPriorDots { get; set; }

		[Display(Name = "Dot Size (px)", Order = 1, GroupName = "9. Prior Dots")]
		[Range(1, 10)]
		public int DotSize { get; set; }

		[XmlIgnore]
		[Display(Name = "1 Minute", Order = 2, GroupName = "9. Prior Dots")]
		public Brush PriorColor1m { get; set; }
		[Browsable(false)] public string PriorColor1mSerialize { get { return Serialize.BrushToString(PriorColor1m); } set { PriorColor1m = Serialize.StringToBrush(value); } }

		[XmlIgnore]
		[Display(Name = "5 Minutes", Order = 3, GroupName = "9. Prior Dots")]
		public Brush PriorColor5m { get; set; }
		[Browsable(false)] public string PriorColor5mSerialize { get { return Serialize.BrushToString(PriorColor5m); } set { PriorColor5m = Serialize.StringToBrush(value); } }

		[XmlIgnore]
		[Display(Name = "10 Minutes", Order = 4, GroupName = "9. Prior Dots")]
		public Brush PriorColor10m { get; set; }
		[Browsable(false)] public string PriorColor10mSerialize { get { return Serialize.BrushToString(PriorColor10m); } set { PriorColor10m = Serialize.StringToBrush(value); } }

		[XmlIgnore]
		[Display(Name = "15 Minutes", Order = 5, GroupName = "9. Prior Dots")]
		public Brush PriorColor15m { get; set; }
		[Browsable(false)] public string PriorColor15mSerialize { get { return Serialize.BrushToString(PriorColor15m); } set { PriorColor15m = Serialize.StringToBrush(value); } }

		[XmlIgnore]
		[Display(Name = "30 Minutes", Order = 6, GroupName = "9. Prior Dots")]
		public Brush PriorColor30m { get; set; }
		[Browsable(false)] public string PriorColor30mSerialize { get { return Serialize.BrushToString(PriorColor30m); } set { PriorColor30m = Serialize.StringToBrush(value); } }

		#endregion

		#region Параметри — 10. Hotkeys

		[Display(Name = "Toggle Profiles", Order = 0, GroupName = "10. Hotkeys")]
		public System.Windows.Input.Key ToggleProfileKey { get; set; }

		#endregion

		#region OnStateChange

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description		= "GEX — OI + VOL dual profile з GexBot API";
				Name			= "GEX";
				IsOverlay		= true;
				IsSuspendedWhileInactive = true;
				DisplayInDataBox	= false;
				DrawOnPricePanel	= true;
				Calculate		= Calculate.OnEachTick;

				// 1. GexBot Setup
				ApiKey			= "Insert Key Here";
				Ticker			= GexTicker.NDX;
				Aggregation		= AggPeriod.Full;
				RefreshSeconds		= 15;
				FetchStartET		= 930;
				FetchEndET		= 1600;

				// 2. OI Profile
				ShowOIProfile		= true;
				OIPosition		= PanelPos.Right;
				OICenterOffset		= 0;
				OIPositiveColor		= new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1F, 0x40, 0x69)); OIPositiveColor.Freeze();
				OINegativeColor		= new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x69, 0x3D, 0x1A)); OINegativeColor.Freeze();

				// 3. VOL Profile
				ShowVolProfile		= false;
				VolPosition		= PanelPos.Left;
				VolCenterOffset		= 0;
				VolPositiveColor	= new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x00, 0x38, 0x00)); VolPositiveColor.Freeze();
				VolNegativeColor	= new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x45, 0x02, 0x01)); VolNegativeColor.Freeze();

				// 4. Profile Visual
				ProfileWidth		= 150;
				BarHeight		= 4;
				MinBarWidth		= 4;
				ProfileContrast		= 1.0;

				// 5. GEX Levels
				ShowZeroGamma		= true;
				ZeroGammaColor		= new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x6B, 0x6B, 0x01)); ZeroGammaColor.Freeze();
				ZeroGammaDash		= DashStyleHelper.Dash;
				ZeroGammaWidth		= 1;

				ShowMajorPosOI		= true;
				MajorPosOIColor		= new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1F, 0x40, 0x69)); MajorPosOIColor.Freeze();
				MajorPosOIDash		= DashStyleHelper.Dash;
				MajorPosOIWidth		= 1;

				ShowMajorNegOI		= true;
				MajorNegOIColor		= new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x75, 0x42, 0x19)); MajorNegOIColor.Freeze();
				MajorNegOIDash		= DashStyleHelper.Dash;
				MajorNegOIWidth		= 1;

				ShowMajorPosVol		= true;
				MajorPosVolColor	= new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x00, 0x38, 0x00)); MajorPosVolColor.Freeze();
				MajorPosVolDash		= DashStyleHelper.Dash;
				MajorPosVolWidth	= 1;

				ShowMajorNegVol		= true;
				MajorNegVolColor	= new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x45, 0x02, 0x01)); MajorNegVolColor.Freeze();
				MajorNegVolDash		= DashStyleHelper.Dash;
				MajorNegVolWidth	= 1;

				// 6. Strike Levels
				StrikeRange		= 600;
				var slateGray		= new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x70, 0x80, 0x90)); slateGray.Freeze();
				Show100s = true;  Color100s = slateGray; Dash100s = DashStyleHelper.Dash; Width100s = 1;
				Show50s  = true;  Color50s  = slateGray; Dash50s  = DashStyleHelper.Dash; Width50s  = 1;
				Show25s  = false; Color25s  = slateGray; Dash25s  = DashStyleHelper.Dash; Width25s  = 1;
				Show5s   = false; Color5s   = slateGray; Dash5s   = DashStyleHelper.Dash; Width5s   = 1;

				// 7. Labels
				ShowLabels		= true;
				LabelPosition		= PanelPos.Right;
				LabelFontSize		= 12;
				LabelColor		= new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0x4C, 0x4C)); LabelColor.Freeze();

				// 8. Info Block
				ShowInfoBlock		= true;
				InfoFontSize		= 12;

				// 9. Prior Dots
				ShowPriorDots		= false;
				DotSize			= 4;
				PriorColor1m		= Brushes.Red;         PriorColor1m.Freeze();
				PriorColor5m		= new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x00, 0xFF, 0x00)); PriorColor5m.Freeze();
				PriorColor10m		= Brushes.Green;       PriorColor10m.Freeze();
				PriorColor15m		= new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x00, 0x64, 0x00)); PriorColor15m.Freeze();
				PriorColor30m		= Brushes.White;       PriorColor30m.Freeze();

				// 10. Hotkeys
				ToggleProfileKey	= System.Windows.Input.Key.Tab;
			}
			else if (State == State.DataLoaded)
			{
				if (ChartControl != null && ChartPanel != null)
					ChartPanel.KeyDown += OnChartKeyDown;
			}
			else if (State == State.Terminated)
			{
				if (ChartControl != null && ChartPanel != null)
					ChartPanel.KeyDown -= OnChartKeyDown;
				DisposeResources();
			}
		}

		#endregion

		#region OnBarUpdate

		protected override void OnBarUpdate()
		{
			if (CurrentBar < 1) return;
			if (ApiKey == "Insert Key Here" || string.IsNullOrEmpty(ApiKey)) return;

			if (CurrentBar < Count - 2 && State != State.Realtime) return;

			string cacheKey = Ticker.ToString() + "_" + Aggregation.ToString();
			bool needsFetch = false;

			lock (cacheLock)
			{
				if (dataCache.ContainsKey(cacheKey))
				{
					var cached = dataCache[cacheKey];
					currentData = cached;
					if ((DateTime.UtcNow - cached.FetchTime).TotalSeconds >= RefreshSeconds)
						needsFetch = true;
				}
				else
					needsFetch = true;

				bool noCache = !dataCache.ContainsKey(cacheKey);
				if (needsFetch && (noCache || IsWithinFetchWindow()) && !fetchingKeys.Contains(cacheKey))
				{
					fetchingKeys.Add(cacheKey);
					FetchAsync(cacheKey);
				}
			}

			if (currentData != null && currentData.Spot > 0)
			{
				currentPremium = currentData.Premium;
				RecalcConvertedLevels();
				RecalcGridLines();
			}
		}

		private bool IsWithinFetchWindow()
		{
			var et = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow,
				TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"));
			int now = et.Hour * 100 + et.Minute;
			return now >= FetchStartET && now <= FetchEndET;
		}

		#endregion

		#region API — HTTP запит і парсинг

		private bool NeedsChainEndpoint
		{
			get { return ShowOIProfile || ShowVolProfile || ShowPriorDots; }
		}

		private async void FetchAsync(string cacheKey)
		{
			try
			{
				string agg = Aggregation == AggPeriod.Full ? "full"
					   : Aggregation == AggPeriod.Zero ? "zero" : "one";
				string ticker = Ticker == GexTicker.NDX ? "NDX" : "SPX";

				string url;
				if (!NeedsChainEndpoint)
					url = string.Format("https://api.gexbot.com/{0}/classic/{1}/majors?key={2}", ticker, agg, ApiKey);
				else
					url = string.Format("https://api.gexbot.com/{0}/classic/{1}?key={2}", ticker, agg, ApiKey);

				string json = await httpClient.GetStringAsync(url);

				if (ChartControl != null && ChartControl.Dispatcher != null && State != State.Terminated)
				{
					ChartControl.Dispatcher.InvokeAsync(() =>
					{
						if (State == State.Terminated) { ReleaseFetchKey(cacheKey); return; }
						try
						{
							GexData data;
							if (!NeedsChainEndpoint)
								data = ParseMajors(json);
							else
								data = ParseChain(json);

							data.FetchTime = DateTime.UtcNow;
							isOffline = false;

							// Premium фіксується в момент fetch — всі чарти читають однакове значення
							double bid = GetCurrentBid();
							if (data.Spot > 0 && bid > 0)
								data.Premium = bid - data.Spot;
							else if (dataCache.ContainsKey(cacheKey))
								data.Premium = dataCache[cacheKey].Premium;

							lock (cacheLock)
							{
								dataCache[cacheKey] = data;
								fetchingKeys.Remove(cacheKey);
							}
							currentData = data;

							if (data.Spot > 0)
							{
								currentPremium = data.Premium;
								RecalcConvertedLevels();
								RecalcGridLines();
							}
							ForceRefresh();
						}
						catch (Exception ex)
						{
							Print("[GEX] Parse error: " + ex.Message);
							isOffline = true;
							ReleaseFetchKey(cacheKey);
						}
					});
				}
				else
					ReleaseFetchKey(cacheKey);
			}
			catch (Exception ex)
			{
				Print("[GEX] Fetch error: " + ex.Message);
				isOffline = true;
				ReleaseFetchKey(cacheKey);
			}
		}

		private static void ReleaseFetchKey(string key)
		{
			lock (cacheLock) { fetchingKeys.Remove(key); }
		}

		private GexData ParseChain(string json)
		{
			var data = new GexData
			{
				Timestamp    = JsonLong(json, "timestamp"),
				Spot         = JsonDouble(json, "spot"),
				ZeroGamma    = JsonDouble(json, "zero_gamma"),
				MajorPosOI   = JsonDouble(json, "major_pos_oi"),
				MajorNegOI   = JsonDouble(json, "major_neg_oi"),
				MajorPosVol  = JsonDouble(json, "major_pos_vol"),
				MajorNegVol  = JsonDouble(json, "major_neg_vol"),
				NetGexOI     = JsonDouble(json, "sum_gex_oi"),
				NetGexVol    = JsonDouble(json, "sum_gex_vol")
			};

			int strikesStart = json.IndexOf("\"strikes\"");
			if (strikesStart >= 0)
			{
				int arrStart = json.IndexOf('[', strikesStart);
				if (arrStart >= 0)
				{
					int depth = 0;
					int arrEnd = -1;
					for (int idx = arrStart; idx < json.Length; idx++)
					{
						if (json[idx] == '[') depth++;
						else if (json[idx] == ']') { depth--; if (depth == 0) { arrEnd = idx; break; } }
					}

					if (arrEnd > arrStart)
					{
						string strikesJson = json.Substring(arrStart, arrEnd - arrStart + 1);
						ParseStrikesArray(strikesJson, data.Strikes);
					}
				}
			}
			return data;
		}

		private void ParseStrikesArray(string arr, List<StrikeData> list)
		{
			int i = 0;
			while (i < arr.Length)
			{
				int start = arr.IndexOf('[', i);
				if (start < 0) break;

				if (start + 1 < arr.Length && arr[start + 1] == '[')
				{
					i = start + 1;
					continue;
				}

				int depth = 0;
				int end = start;
				for (int j = start; j < arr.Length; j++)
				{
					if (arr[j] == '[') depth++;
					else if (arr[j] == ']') { depth--; if (depth == 0) { end = j; break; } }
				}

				string inner = arr.Substring(start + 1, end - start - 1);
				var sd = ParseSingleStrike(inner);
				if (sd != null)
					list.Add(sd);

				i = end + 1;
			}
		}

		private StrikeData ParseSingleStrike(string inner)
		{
			double[] priors = null;
			int priorsStart = inner.IndexOf('[');
			string mainPart;

			if (priorsStart >= 0)
			{
				int priorsEnd = inner.IndexOf(']', priorsStart);
				if (priorsEnd > priorsStart)
				{
					string priorsStr = inner.Substring(priorsStart + 1, priorsEnd - priorsStart - 1);
					string[] pParts = priorsStr.Split(',');
					if (pParts.Length >= 5)
					{
						priors = new double[5];
						for (int k = 0; k < 5; k++)
							double.TryParse(pParts[k].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out priors[k]);
					}
				}
				mainPart = inner.Substring(0, priorsStart);
			}
			else
				mainPart = inner;

			string[] parts = mainPart.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
			if (parts.Length < 3) return null;

			var sd = new StrikeData();
			double.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out sd.Strike);
			double.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out sd.GexVol);
			double.TryParse(parts[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out sd.GexOI);
			sd.Priors = priors;
			return sd;
		}

		private GexData ParseMajors(string json)
		{
			return new GexData
			{
				Timestamp    = JsonLong(json, "timestamp"),
				Spot         = JsonDouble(json, "spot"),
				ZeroGamma    = JsonDouble(json, "zero_gamma"),
				MajorPosOI   = JsonDouble(json, "mpos_oi"),
				MajorNegOI   = JsonDouble(json, "mneg_oi"),
				MajorPosVol  = JsonDouble(json, "mpos_vol"),
				MajorNegVol  = JsonDouble(json, "mneg_vol"),
				NetGexOI     = JsonDouble(json, "net_gex_oi"),
				NetGexVol    = JsonDouble(json, "net_gex_vol")
			};
		}

		#region JSON helpers

		private static double JsonDouble(string json, string key)
		{
			string raw = JsonRawValue(json, key);
			if (raw == null) return 0;
			double val;
			double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out val);
			return val;
		}

		private static long JsonLong(string json, string key)
		{
			string raw = JsonRawValue(json, key);
			if (raw == null) return 0;
			long val;
			long.TryParse(raw, out val);
			return val;
		}

		private static string JsonRawValue(string json, string key)
		{
			string pattern = "\"" + key + "\"";
			int idx = json.IndexOf(pattern);
			if (idx < 0) return null;

			int colon = json.IndexOf(':', idx + pattern.Length);
			if (colon < 0) return null;

			int start = colon + 1;
			while (start < json.Length && json[start] == ' ') start++;
			if (start >= json.Length) return null;

			if (json[start] == '"')
			{
				int end = json.IndexOf('"', start + 1);
				return end > start ? json.Substring(start + 1, end - start - 1) : null;
			}

			int valEnd = start;
			while (valEnd < json.Length && json[valEnd] != ',' && json[valEnd] != '}' && json[valEnd] != ']')
				valEnd++;

			return json.Substring(start, valEnd - start).Trim();
		}

		#endregion

		#endregion

		#region Перерахунок рівнів

		private void RecalcConvertedLevels()
		{
			if (currentData == null) return;
			lvlZeroGamma    = currentData.ZeroGamma   + currentPremium;
			lvlMajorPosOI   = currentData.MajorPosOI  + currentPremium;
			lvlMajorNegOI   = currentData.MajorNegOI  + currentPremium;
			lvlMajorPosVol  = currentData.MajorPosVol + currentPremium;
			lvlMajorNegVol  = currentData.MajorNegVol + currentPremium;
		}

		private void RecalcGridLines()
		{
			gridLines.Clear();
			if (currentData == null || currentData.Spot <= 0) return;

			double spot = currentData.Spot;
			int[] steps = { 100, 50, 25, 5 };
			bool[] shows = { Show100s, Show50s, Show25s, Show5s };

			for (int tier = 0; tier < 4; tier++)
			{
				if (!shows[tier]) continue;
				int step = steps[tier];
				int start = (int)(Math.Floor((spot - StrikeRange) / step) * step);
				int end   = (int)(Math.Ceiling((spot + StrikeRange) / step) * step);

				for (int strike = start; strike <= end; strike += step)
				{
					if (tier == 1 && strike % 100 == 0) continue;
					if (tier == 2 && strike % 50 == 0) continue;
					if (tier == 3 && strike % 25 == 0) continue;

					gridLines.Add(new GridLine
					{
						FuturesPrice = strike + currentPremium,
						IndexStrike  = strike,
						Tier         = tier
					});
				}
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
			if (RenderTarget == null) return;
			ZOrder = 20000;

			EnsureResources(chartControl);

			float panelW = (float)ChartPanel.W;
			float panelH = (float)ChartPanel.H;

			// 1. OI профіль
			if (profileVisible && ShowOIProfile && currentData != null && currentData.Strikes.Count > 0)
				RenderProfile(chartScale, panelW, panelH, true, OIPosition, OICenterOffset, dxOIPos, dxOINeg);

			// 2. VOL профіль
			if (profileVisible && ShowVolProfile && currentData != null && currentData.Strikes.Count > 0)
				RenderProfile(chartScale, panelW, panelH, false, VolPosition, VolCenterOffset, dxVolPos, dxVolNeg);

			// 3. Strike levels
			RenderStrikeLevels(chartScale, panelW);

			// 4. GEX рівні
			if (currentData != null)
				RenderGexLevels(chartScale, panelW);

			// 5. Labels
			if (ShowLabels && gridLines.Count > 0)
				RenderLabels(chartScale, panelW);

			// 6. Info block
			if (ShowInfoBlock && currentData != null)
				RenderInfoBlock(panelW);

			// 7. Prior dots (завжди Vol)
			if (ShowPriorDots && currentData != null && currentData.Strikes.Count > 0)
				RenderPriorDots(chartScale, panelW);
		}

		private void RenderProfile(ChartScale chartScale, float panelW, float panelH,
			bool useOI, PanelPos position, int centerOffset,
			SharpDX.Direct2D1.Brush posBrush, SharpDX.Direct2D1.Brush negBrush)
		{
			// Незалежний maxGex для кожного профілю
			double maxGex = 0;
			foreach (var s in currentData.Strikes)
			{
				double val = useOI ? Math.Abs(s.GexOI) : Math.Abs(s.GexVol);
				if (val > maxGex) maxGex = val;
			}
			if (maxGex <= 0) return;

			foreach (var s in currentData.Strikes)
			{
				double gexVal = useOI ? s.GexOI : s.GexVol;
				if (gexVal == 0) continue;

				double futuresPrice = s.Strike + currentPremium;
				float y = chartScale.GetYByValue(futuresPrice);
				if (y < -BarHeight || y > panelH + BarHeight) continue;

				float barW = (float)(Math.Pow(Math.Abs(gexVal) / maxGex, ProfileContrast) * ProfileWidth);
				if (barW < MinBarWidth) barW = MinBarWidth;

				float barX;
				if (position == PanelPos.Left)
					barX = centerOffset;
				else
					barX = panelW - centerOffset - barW;

				var brush = gexVal >= 0 ? posBrush : negBrush;
				if (brush != null)
				{
					RenderTarget.FillRectangle(
						new SharpDX.RectangleF(barX, y - BarHeight / 2f, barW, BarHeight),
						brush);
				}
			}
		}

		private void RenderGexLevels(ChartScale chartScale, float panelW)
		{
			if (ShowZeroGamma)
				DrawGexLine(chartScale, panelW, lvlZeroGamma, dxZeroGamma, ZeroGammaDash, ZeroGammaWidth);
			if (ShowMajorPosOI)
				DrawGexLine(chartScale, panelW, lvlMajorPosOI, dxMajorPosOI, MajorPosOIDash, MajorPosOIWidth);
			if (ShowMajorNegOI)
				DrawGexLine(chartScale, panelW, lvlMajorNegOI, dxMajorNegOI, MajorNegOIDash, MajorNegOIWidth);
			if (ShowMajorPosVol)
				DrawGexLine(chartScale, panelW, lvlMajorPosVol, dxMajorPosVol, MajorPosVolDash, MajorPosVolWidth);
			if (ShowMajorNegVol)
				DrawGexLine(chartScale, panelW, lvlMajorNegVol, dxMajorNegVol, MajorNegVolDash, MajorNegVolWidth);
		}

		private void DrawGexLine(ChartScale chartScale, float panelW, double price, SharpDX.Direct2D1.Brush brush, DashStyleHelper dash, int width)
		{
			if (brush == null || price <= 0) return;
			float y = chartScale.GetYByValue(price);
			if (y < -20 || y > ChartPanel.H + 20) return;

			var style = CreateStrokeStyle(dash);
			if (style != null)
			{
				RenderTarget.DrawLine(new Vector2(0, y), new Vector2(panelW, y), brush, width, style);
				style.Dispose();
			}
			else
				RenderTarget.DrawLine(new Vector2(0, y), new Vector2(panelW, y), brush, width);
		}

		private void RenderStrikeLevels(ChartScale chartScale, float panelW)
		{
			foreach (var g in gridLines)
			{
				float y = chartScale.GetYByValue(g.FuturesPrice);
				if (y < -20 || y > ChartPanel.H + 20) continue;

				SharpDX.Direct2D1.Brush brush;
				DashStyleHelper dash;
				int width;

				switch (g.Tier)
				{
					case 0: brush = dxGrid100; dash = Dash100s; width = Width100s; break;
					case 1: brush = dxGrid50;  dash = Dash50s;  width = Width50s;  break;
					case 2: brush = dxGrid25;  dash = Dash25s;  width = Width25s;  break;
					default: brush = dxGrid5;  dash = Dash5s;   width = Width5s;   break;
				}

				if (brush == null) continue;

				var style = CreateStrokeStyle(dash);
				if (style != null)
				{
					RenderTarget.DrawLine(new Vector2(0, y), new Vector2(panelW, y), brush, width, style);
					style.Dispose();
				}
				else
					RenderTarget.DrawLine(new Vector2(0, y), new Vector2(panelW, y), brush, width);
			}
		}

		private void RenderLabels(ChartScale chartScale, float panelW)
		{
			if (dxLabel == null || fmtLabel == null) return;

			foreach (var g in gridLines)
			{
				float y = chartScale.GetYByValue(g.FuturesPrice);
				if (y < -20 || y > ChartPanel.H + 20) continue;

				string text = g.IndexStrike.ToString();
				using (var layout = new TextLayout(NinjaTrader.Core.Globals.DirectWriteFactory, text, fmtLabel, 100, 20))
				{
					float labelX = LabelPosition == PanelPos.Left
						? 5
						: panelW - layout.Metrics.Width - 5;
					RenderTarget.DrawTextLayout(new Vector2(labelX, y - layout.Metrics.Height), layout, dxLabel);
				}
			}
		}

		private void RenderInfoBlock(float panelW)
		{
			if (currentData == null || fmtInfo == null) return;

			string ticker = Ticker == GexTicker.NDX ? "NDX" : "SPX";
			string agg = Aggregation == AggPeriod.Full ? "90d (full)"
				   : Aggregation == AggPeriod.Zero ? "0DTE (zero)" : "Next (one)";

			var ts = DateTimeOffset.FromUnixTimeSeconds(currentData.Timestamp);
			var et = TimeZoneInfo.ConvertTime(ts,
				TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"));
			string time = et.ToString("HH:mm") + " ET";

			string offline = isOffline ? " | OFFLINE" : "";
			string info = string.Format("GEX | {0} | {1} | OI: {2:F1} | Vol: {3:F1} | {4}{5} | Prem: {6:+0.00;-0.00}",
				ticker, agg, currentData.NetGexOI, currentData.NetGexVol, time, offline, currentPremium);

			var brush = isOffline
				? new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, new Color4(1f, 0.3f, 0.3f, 1f))
				: dxInfo;

			using (var layout = new TextLayout(NinjaTrader.Core.Globals.DirectWriteFactory, info, fmtInfo, 800, 20))
			{
				RenderTarget.DrawTextLayout(new Vector2(10, 10), layout, brush);
			}

			if (isOffline && brush != dxInfo)
				brush.Dispose();
		}

		private void RenderPriorDots(ChartScale chartScale, float panelW)
		{
			if (currentData.Strikes == null) return;

			SharpDX.Direct2D1.Brush[] priorBrushes = { dxPrior1m, dxPrior5m, dxPrior10m, dxPrior15m, dxPrior30m };

			// Prior Dots завжди по Volume (як на сайті GexBot)
			double maxGex = 0;
			foreach (var s in currentData.Strikes)
			{
				double val = Math.Abs(s.GexVol);
				if (val > maxGex) maxGex = val;
			}
			if (maxGex <= 0) return;

			// Позиція dots прив'язана до VOL профілю
			PanelPos dotPos = VolPosition;
			int dotOffset = VolCenterOffset;

			foreach (var s in currentData.Strikes)
			{
				if (s.Priors == null) continue;
				double futuresPrice = s.Strike + currentPremium;
				float y = chartScale.GetYByValue(futuresPrice);
				if (y < -DotSize || y > ChartPanel.H + DotSize) continue;

				for (int i = 0; i < 5 && i < s.Priors.Length; i++)
				{
					if (priorBrushes[i] == null || s.Priors[i] == 0) continue;
					float dotW = (float)(Math.Pow(Math.Abs(s.Priors[i]) / maxGex, ProfileContrast) * ProfileWidth);

					float dotX;
					if (dotPos == PanelPos.Left)
						dotX = dotOffset + dotW;
					else
						dotX = panelW - dotOffset - dotW;

					var ellipse = new SharpDX.Direct2D1.Ellipse(new Vector2(dotX, y), DotSize / 2f, DotSize / 2f);
					RenderTarget.FillEllipse(ellipse, priorBrushes[i]);
				}
			}
		}

		#endregion

		#region Stroke Style

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

		private void EnsureResources(ChartControl chartControl)
		{
			// OI Profile
			if (dxOIPos  == null || dxOIPos.IsDisposed)  dxOIPos  = OIPositiveColor.ToDxBrush(RenderTarget);
			if (dxOINeg  == null || dxOINeg.IsDisposed)  dxOINeg  = OINegativeColor.ToDxBrush(RenderTarget);

			// VOL Profile
			if (dxVolPos == null || dxVolPos.IsDisposed) dxVolPos = VolPositiveColor.ToDxBrush(RenderTarget);
			if (dxVolNeg == null || dxVolNeg.IsDisposed) dxVolNeg = VolNegativeColor.ToDxBrush(RenderTarget);

			// GEX levels
			if (dxZeroGamma   == null || dxZeroGamma.IsDisposed)   dxZeroGamma   = ZeroGammaColor.ToDxBrush(RenderTarget);
			if (dxMajorPosOI  == null || dxMajorPosOI.IsDisposed)  dxMajorPosOI  = MajorPosOIColor.ToDxBrush(RenderTarget);
			if (dxMajorNegOI  == null || dxMajorNegOI.IsDisposed)  dxMajorNegOI  = MajorNegOIColor.ToDxBrush(RenderTarget);
			if (dxMajorPosVol == null || dxMajorPosVol.IsDisposed) dxMajorPosVol = MajorPosVolColor.ToDxBrush(RenderTarget);
			if (dxMajorNegVol == null || dxMajorNegVol.IsDisposed) dxMajorNegVol = MajorNegVolColor.ToDxBrush(RenderTarget);

			// Strike grid
			if (dxGrid100 == null || dxGrid100.IsDisposed) dxGrid100 = Color100s.ToDxBrush(RenderTarget);
			if (dxGrid50  == null || dxGrid50.IsDisposed)  dxGrid50  = Color50s.ToDxBrush(RenderTarget);
			if (dxGrid25  == null || dxGrid25.IsDisposed)  dxGrid25  = Color25s.ToDxBrush(RenderTarget);
			if (dxGrid5   == null || dxGrid5.IsDisposed)   dxGrid5   = Color5s.ToDxBrush(RenderTarget);

			// Labels / Info
			if (dxLabel == null || dxLabel.IsDisposed) dxLabel = LabelColor.ToDxBrush(RenderTarget);
			if (dxInfo  == null || dxInfo.IsDisposed)  dxInfo  = chartControl.Properties.ChartText.ToDxBrush(RenderTarget);

			// Prior dots
			if (dxPrior1m  == null || dxPrior1m.IsDisposed)  dxPrior1m  = PriorColor1m.ToDxBrush(RenderTarget);
			if (dxPrior5m  == null || dxPrior5m.IsDisposed)  dxPrior5m  = PriorColor5m.ToDxBrush(RenderTarget);
			if (dxPrior10m == null || dxPrior10m.IsDisposed) dxPrior10m = PriorColor10m.ToDxBrush(RenderTarget);
			if (dxPrior15m == null || dxPrior15m.IsDisposed) dxPrior15m = PriorColor15m.ToDxBrush(RenderTarget);
			if (dxPrior30m == null || dxPrior30m.IsDisposed) dxPrior30m = PriorColor30m.ToDxBrush(RenderTarget);

			// Text formats
			if (fmtLabel == null || fmtLabel.IsDisposed)
				fmtLabel = new TextFormat(NinjaTrader.Core.Globals.DirectWriteFactory, "Segoe UI Light", LabelFontSize);
			if (fmtInfo == null || fmtInfo.IsDisposed)
				fmtInfo = new TextFormat(NinjaTrader.Core.Globals.DirectWriteFactory, "Segoe UI Light", InfoFontSize);
		}

		private void DisposeResources()
		{
			SharpDX.Direct2D1.Brush[] brushes = {
				dxOIPos, dxOINeg, dxVolPos, dxVolNeg,
				dxZeroGamma, dxMajorPosOI, dxMajorNegOI, dxMajorPosVol, dxMajorNegVol,
				dxGrid100, dxGrid50, dxGrid25, dxGrid5,
				dxLabel, dxInfo,
				dxPrior1m, dxPrior5m, dxPrior10m, dxPrior15m, dxPrior30m
			};

			foreach (var b in brushes)
				if (b != null) b.Dispose();

			dxOIPos = dxOINeg = dxVolPos = dxVolNeg = null;
			dxZeroGamma = dxMajorPosOI = dxMajorNegOI = dxMajorPosVol = dxMajorNegVol = null;
			dxGrid100 = dxGrid50 = dxGrid25 = dxGrid5 = null;
			dxLabel = dxInfo = null;
			dxPrior1m = dxPrior5m = dxPrior10m = dxPrior15m = dxPrior30m = null;

			if (fmtLabel != null) { fmtLabel.Dispose(); fmtLabel = null; }
			if (fmtInfo  != null) { fmtInfo.Dispose();  fmtInfo  = null; }
		}

		#endregion

		#region Hotkey

		private void OnChartKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
		{
			if (e.Key == ToggleProfileKey && (ShowOIProfile || ShowVolProfile))
			{
				profileVisible = !profileVisible;
				ForceRefresh();
			}
		}

		#endregion
	}
}
