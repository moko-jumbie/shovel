// SMT / ICT areas of interest: parameters and wiring.
//
// Kept in its own partial so the trading core above stays readable and so the
// feature can be removed by deleting this file and four call sites. Nothing in
// here runs unless a POI parameter is turned on; with every switch off the
// OnTick cost is a single null check and the results are bit-identical to the
// robot before this feature existed.
using System;
using cAlgo.API.Internals;

namespace cAlgo.Robots
{
    public partial class hmax_gold
    {
        // ── Master switches ───────────────────────────────────────────────

        [Parameter("Draw SMT POIs", DefaultValue = false, Group = "SMT POI")]
        public bool DrawSmtPois { get; set; }

        /// Lets the same areas of interest gate entries. Off by default: turning
        /// this on changes which trades are taken, so every optimisation result
        /// recorded before it existed applies to the ungated robot only.
        [Parameter("Use POI Confluence Filter", DefaultValue = false, Group = "SMT POI")]
        public bool UsePoiConfluenceFilter { get; set; }

        // TimeFrame.Hour is a static property rather than a const, so it cannot be an
        // attribute argument. The default is applied by the initialiser and again in
        // InitializePoi: with no DefaultValue in the attribute the parameter loader
        // can leave this null, which previously disabled the whole feature.
        [Parameter("POI Timeframe", Group = "SMT POI")]
        public TimeFrame PoiTimeframe { get; set; } = TimeFrame.Hour;

        [Parameter("POI Lookback Bars", DefaultValue = 200, MinValue = 20, Group = "SMT POI")]
        public int PoiLookbackBars { get; set; }

        [Parameter("POI Extend (hours)", DefaultValue = 24, MinValue = 1, Group = "SMT POI")]
        public int PoiExtendHours { get; set; }

        [Parameter("Max POI Objects", DefaultValue = 120, MinValue = 3, Group = "SMT POI")]
        public int MaxPoiObjects { get; set; }

        /// Chart drawing is pointless in a headless run and is the single most
        /// expensive thing this feature does, so it is opt-in during backtests.
        [Parameter("Draw POIs In Backtest", DefaultValue = false, Group = "SMT POI")]
        public bool DrawPoiInBacktest { get; set; }

        [Parameter("Draw POI Labels", DefaultValue = true, Group = "SMT POI")]
        public bool DrawPoiLabels { get; set; }

        // ── Layers ────────────────────────────────────────────────────────

        [Parameter("Show Order Blocks", DefaultValue = true, Group = "SMT POI Layers")]
        public bool PoiShowOrderBlocks { get; set; }

        [Parameter("Show Breaker Blocks", DefaultValue = true, Group = "SMT POI Layers")]
        public bool PoiShowBreakerBlocks { get; set; }

        [Parameter("Show Fair Value Gaps", DefaultValue = true, Group = "SMT POI Layers")]
        public bool PoiShowFairValueGaps { get; set; }

        [Parameter("Show EQH / EQL", DefaultValue = true, Group = "SMT POI Layers")]
        public bool PoiShowLiquidity { get; set; }

        [Parameter("Order Block Includes Wicks", DefaultValue = false, Group = "SMT POI Layers")]
        public bool PoiOrderBlockIncludesWicks { get; set; }

        [Parameter("Swing Strength (bars)", DefaultValue = 2, MinValue = 1, Group = "SMT POI Layers")]
        public int PoiSwingStrength { get; set; }

        [Parameter("OB Impulse (ATR mult)", DefaultValue = 1.0, MinValue = 0.1, Group = "SMT POI Layers")]
        public double PoiOrderBlockMinImpulseAtr { get; set; }

        [Parameter("FVG Gap (ATR mult)", DefaultValue = 0.1, MinValue = 0.0, Group = "SMT POI Layers")]
        public double PoiFvgMinGapAtr { get; set; }

        [Parameter("EQH/EQL Tolerance (ATR mult)", DefaultValue = 0.15, MinValue = 0.0, Group = "SMT POI Layers")]
        public double PoiLiquidityToleranceAtr { get; set; }

        /// Equal highs are drawn at the top of the pair and equal lows at the
        /// bottom, i.e. the side price reaches first. Off draws the pair's
        /// midpoint instead, which is the more literal reading of "equal".
        [Parameter("Liquidity At Front Line", DefaultValue = true, Group = "SMT POI Layers")]
        public bool PoiLiquidityAtFrontLine { get; set; }

        // ── Confluence gate ───────────────────────────────────────────────

        [Parameter("OB Counts For Confluence", DefaultValue = true, Group = "SMT POI Gate")]
        public bool PoiObCounts { get; set; }

        [Parameter("FVG Counts For Confluence", DefaultValue = true, Group = "SMT POI Gate")]
        public bool PoiFvgCounts { get; set; }

        [Parameter("Breaker Counts For Confluence", DefaultValue = true, Group = "SMT POI Gate")]
        public bool PoiBreakerCounts { get; set; }

        [Parameter("Liquidity Counts For Confluence", DefaultValue = true, Group = "SMT POI Gate")]
        public bool PoiLiquidityCounts { get; set; }

        [Parameter("Min Confluence Zones", DefaultValue = 1, MinValue = 1, Group = "SMT POI Gate")]
        public int PoiMinConfluenceZones { get; set; }

        /// Widens every zone by this many average true ranges before testing.
        /// Zero means the price has to be strictly inside a zone.
        [Parameter("Confluence Proximity (ATR mult)", DefaultValue = 0.5, MinValue = 0.0, Group = "SMT POI Gate")]
        public double PoiConfluenceProximityAtr { get; set; }

        // ── Appearance ────────────────────────────────────────────────────

        [Parameter("OB Bullish Colour", DefaultValue = "#2E7D32", Group = "SMT POI Look")]
        public string PoiObBuyHex { get; set; }

        [Parameter("OB Bearish Colour", DefaultValue = "#C62828", Group = "SMT POI Look")]
        public string PoiObSellHex { get; set; }

        [Parameter("Breaker Bullish Colour", DefaultValue = "#00695C", Group = "SMT POI Look")]
        public string PoiBreakerBuyHex { get; set; }

        [Parameter("Breaker Bearish Colour", DefaultValue = "#AD1457", Group = "SMT POI Look")]
        public string PoiBreakerSellHex { get; set; }

        [Parameter("FVG Bullish Colour", DefaultValue = "#1565C0", Group = "SMT POI Look")]
        public string PoiFvgBuyHex { get; set; }

        [Parameter("FVG Bearish Colour", DefaultValue = "#EF6C00", Group = "SMT POI Look")]
        public string PoiFvgSellHex { get; set; }

        [Parameter("EQH Colour", DefaultValue = "#B71C1C", Group = "SMT POI Look")]
        public string PoiEqhHex { get; set; }

        [Parameter("EQL Colour", DefaultValue = "#1B5E20", Group = "SMT POI Look")]
        public string PoiEqlHex { get; set; }

        [Parameter("Zone Fill Alpha (0-255)", DefaultValue = 70, MinValue = 0, MaxValue = 255, Group = "SMT POI Look")]
        public int PoiZoneAlpha { get; set; }

        [Parameter("Zone Border Alpha (0-255)", DefaultValue = 190, MinValue = 0, MaxValue = 255, Group = "SMT POI Look")]
        public int PoiBorderAlpha { get; set; }

        [Parameter("Label Alpha (0-255)", DefaultValue = 220, MinValue = 0, MaxValue = 255, Group = "SMT POI Look")]
        public int PoiLabelAlpha { get; set; }

        [Parameter("Solid Zone Borders", DefaultValue = true, Group = "SMT POI Look")]
        public bool PoiSolidBorder { get; set; }

        [Parameter("Label Font Size", DefaultValue = 10, MinValue = 6, Group = "SMT POI Look")]
        public double PoiLabelFontSize { get; set; }

        // ── State ─────────────────────────────────────────────────────────

        private PoiController _poi;

        /// Called from OnStart.
        ///
        /// Every failure path here is non-fatal on purpose. A POI feature that
        /// can stop the robot from trading is worse than no POI feature.
        private void InitializePoi()
        {
            if (!DrawSmtPois && !UsePoiConfluenceFilter)
                return;

            // An unset reference-typed parameter arrives as null, and a loader that
            // materialises the zero value arrives as Minute. Both mean "use the
            // documented default". Treating either as "not a higher timeframe"
            // disabled the entire feature while every switch was turned on, and
            // reported it as an invalid configuration rather than as a default.
            if (PoiTimeframe == null || PoiTimeframe <= TimeFrame.Minute)
            {
                PoiTimeframe = TimeFrame.Hour;
                Print("SMT POI: 'POI Timeframe' was unset, defaulting to ", PoiTimeframe, ".");
            }

            if (PoiTimeframe <= TimeFrame)
            {
                Print("SMT POI disabled: '", PoiTimeframe, "' is not higher than the chart's ",
                      TimeFrame, ". Set 'POI Timeframe' to a higher timeframe.");
                return;
            }

            // Drawing during a backtest is opt-in and costs the most, so it is
            // the thing to keep off when running headless.
            bool drawing = DrawSmtPois && (!IsBacktesting || DrawPoiInBacktest);

            // Ticking 'Draw SMT POIs' and seeing an empty chart is the single most
            // likely way to conclude this feature is broken. Say so explicitly
            // instead of letting the suppression look like a detection failure.
            if (DrawSmtPois && !drawing)
                Print("SMT POI: chart drawing is suppressed during this backtest. ",
                      "Set 'Draw POIs In Backtest' to true to see the zones.");

            PoiSettings settings = new PoiSettings
            {
                DrawZones = drawing,
                ShowOrderBlocks = PoiShowOrderBlocks,
                ShowBreakerBlocks = PoiShowBreakerBlocks,
                ShowFairValueGaps = PoiShowFairValueGaps,
                ShowLiquidityPools = PoiShowLiquidity,
                OrderBlockIncludesWicks = PoiOrderBlockIncludesWicks,
                SwingStrength = PoiSwingStrength,
                OrderBlockMinImpulseAtr = PoiOrderBlockMinImpulseAtr,
                FvgMinGapAtr = PoiFvgMinGapAtr,
                LiquidityToleranceAtr = PoiLiquidityToleranceAtr,
                LiquidityAtFrontLine = PoiLiquidityAtFrontLine,
                LookbackBars = PoiLookbackBars,
                ExtendHours = PoiExtendHours,
                MaxObjects = MaxObjectsInZones(MaxPoiObjects),
                OrderBlocksCount = PoiObCounts,
                FgvsCount = PoiFvgCounts,
                BreakersCount = PoiBreakerCounts,
                LiquidityCounts = PoiLiquidityCounts,
                MinConfluenceZones = PoiMinConfluenceZones,
                ProximityAtr = PoiConfluenceProximityAtr
            };

            PoiStyle style = new PoiStyle
            {
                DrawLabels = DrawPoiLabels,
                LabelFontSize = PoiLabelFontSize,
                SolidBorder = PoiSolidBorder,
                ZoneAlpha = PoiZoneAlpha,
                BorderAlpha = PoiBorderAlpha,
                LabelAlpha = PoiLabelAlpha,
                OrderBlockBuy = ResolveColour(PoiObBuyHex, Color.FromHex("#2E7D32"), nameof(PoiObBuyHex)),
                OrderBlockSell = ResolveColour(PoiObSellHex, Color.FromHex("#C62828"), nameof(PoiObSellHex)),
                BreakerBuy = ResolveColour(PoiBreakerBuyHex, Color.FromHex("#00695C"), nameof(PoiBreakerBuyHex)),
                BreakerSell = ResolveColour(PoiBreakerSellHex, Color.FromHex("#AD1457"), nameof(PoiBreakerSellHex)),
                FairValueGapBuy = ResolveColour(PoiFvgBuyHex, Color.FromHex("#1565C0"), nameof(PoiFvgBuyHex)),
                FairValueGapSell = ResolveColour(PoiFvgSellHex, Color.FromHex("#EF6C00"), nameof(PoiFvgSellHex)),
                EqualHighsColor = ResolveColour(PoiEqhHex, Color.FromHex("#B71C1C"), nameof(PoiEqhHex)),
                EqualLowsColor = ResolveColour(PoiEqlHex, Color.FromHex("#1B5E20"), nameof(PoiEqlHex))
            };

            PoiController controller = new PoiController(LogPoi);
            controller.Configure(Chart, settings, style, Symbol.TickSize);
            controller.EnableGate(UsePoiConfluenceFilter);

            // Sweeps objects left by a previous session before anything new is
            // drawn, so a restart cannot show two generations of rectangles.
            controller.Clear();

            Bars source = GetPoiSourceSeries();
            if (source != null)
                controller.Attach(source);

            _poi = controller;

            if (drawing)
                Print("SMT POI drawing enabled: ", PoiTimeframe, " zones, lookback ",
                      PoiLookbackBars, " bars");
            else if (UsePoiConfluenceFilter)
                Print("SMT POI confluence gate enabled on ", PoiTimeframe,
                      " — entry results will differ from an ungated run");

            if (source == null)
                Print("SMT POI disabled: no ", PoiTimeframe, " data for ", SymbolName);
        }

        /// Called from OnStop.
        private void ShutdownPoi()
        {
            if (_poi == null)
                return;

            _poi.Clear();
            _poi = null;
        }

        /// Called from the top of OnTick. Costs one null check when the feature
        /// is off, and one DateTime read plus one comparison per tick when it is
        /// on; detection itself runs only when the source bar rolls over.
        private void RefreshPoi()
        {
            _poi?.Refresh();
        }

        /// Gate consulted by the crossover branches. Both arguments are cached
        /// per-bar values, so a rejection here cannot change later in the bar —
        /// which is why it does not release the evaluation latch.
        ///
        /// A null controller means the feature is off, which must allow the
        /// trade. Getting that backwards would silently stop the robot from
        /// trading with default parameters.
        private bool AllowsPoiEntry(double price, bool isBuy)
        {
            return _poi == null || _poi.HasConfluence(price, isBuy);
        }

        private Bars GetPoiSourceSeries()
        {
            try
            {
                return MarketData.GetBars(PoiTimeframe, SymbolName);
            }
            catch (Exception error)
            {
                Print("SMT POI: could not load ", PoiTimeframe, " data (", error.Message,
                      "). The feature is disabled for this session.");
                return null;
            }
        }

        /// One zone can be a filled rectangle, an outline and a label, so the
        /// object budget is spent in multiples of three and at least one zone.
        private static int MaxObjectsInZones(int objectBudget)
        {
            if (objectBudget < 3)
                return 1;

            return objectBudget / 3;
        }

        private Color ResolveColour(string text, Color fallback, string parameterName)
        {
            Color parsed;
            if (PoiStyle.TryParseColor(text, out parsed))
                return parsed;

            Print("SMT POI: could not read colour '", text, "' from '", parameterName,
                  "', using the built-in fallback.");
            return fallback;
        }

        private void LogPoi(string message)
        {
            Print(message);
        }
    }
}