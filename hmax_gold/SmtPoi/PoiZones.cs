// Value types shared by the SMT / ICT areas-of-interest detector and renderer.
//
// Everything in this file is display-or-filter plumbing. Nothing here reaches
// into the trading path, and no member of it is exposed to the optimiser.
using System;
using System.Collections.Generic;

namespace cAlgo.Robots
{
    // ── Area-of-interest families ────────────────────────────────────────
    internal enum PoiKind
    {
        OrderBlock,
        BreakerBlock,
        FairValueGap,
        EqualHighs,
        EqualLows
    }

    // Which entry direction a zone can support. Liquidity is classified by the
    // side price has to reach for the level to mean anything: equal lows are the
    // demand you buy into, equal highs are the supply you sell into. Everything
    // else follows the polarity of the block that formed it.
    internal enum PoiSide
    {
        Buy,
        Sell
    }

    /// One immutable rectangle on the chart.
    ///
    /// Every field is final once appended to a PoiSnapshot. That is what makes
    /// the renderer cheap: a zone's geometry cannot change after it is detected,
    /// so the object is drawn once and then left alone. Retiring a zone is done
    /// by removing its chart object, never by mutating it.
    internal sealed class Poi
    {
        public PoiKind Kind;
        public PoiSide Side;

        // Bar open time the zone is anchored to. Combined with Kind and Side it
        // is the zone's identity: the renderer derives the chart object name from
        // it, so a zone redetected on the next refresh replaces its own object
        // instead of accumulating duplicates.
        public DateTime OriginTime;

        // Fixed right edge. Deliberately never Server.Time — a zone whose edge
        // followed the clock would have to be redrawn on every tick, which is
        // the one thing this whole design exists to avoid.
        public DateTime EndTime;

        public double Top;
        public double Bottom;

        // Price has already returned into the zone. Still drawn (so the chart
        // shows where it was taken) but excluded from the confluence gate.
        public bool Mitigated;

        // A liquidity level price has already traded beyond.
        public bool Swept;

        public bool IsLive
        {
            get { return !Mitigated && !Swept; }
        }
    }

    /// One detection pass, complete and self-contained.
    ///
    /// The confluence gate reads this exact object rather than re-running
    /// detection, which is why enabling entries-on- confluence costs nothing at
    /// the point of use: by the time it is consulted, every zone price could
    /// react to is already a flat pair of doubles here.
    internal sealed class PoiSnapshot
    {
        public static readonly PoiSnapshot Empty = new PoiSnapshot(
            new List<Poi>(), 0, 0, Array.Empty<double>(), Array.Empty<double>(),
            Array.Empty<double>(), Array.Empty<double>());

        // Flat price bounds for the live zones only, split by side. Precomputed
        // here so the gate is two array walks with no allocation, no predicate
        // delegate and no LINQ — it runs inside OnTick's bar-close path.
        private readonly double[] _buyBottom;
        private readonly double[] _buyTop;
        private readonly double[] _sellBottom;
        private readonly double[] _sellTop;

        public List<Poi> Zones { get; }

        // How many zones are eligible to gate an entry. Zero means the gate has
        // nothing to say, which the controller treats as permissive rather than
        // as "block everything" — see PoiController.HasConfluence.
        public int ConfluenceZoneCount { get; }

        // Mean true range of the detection window, in price. The confluence
        // proximity is expressed as a multiple of this.
        public double AverageTrueRange { get; }

        // Oldest and newest bar actually inspected by the detector. Logged on
        // every pass because a detector that reads its series in the wrong
        // direction does not fail — it reports zones stamped with dates from
        // years earlier, and the only symptom is that nothing sensible is drawn.
        // How many bars the detector actually inspected. Distinct from the series'
        // own closed-bar count, which can be far larger — reporting the series
        // count instead made a 20-bar lookback look like it had scanned tens of
        // thousands of bars, which is exactly the kind of mismatch that hides a
        // mis-indexed window.
        public int WindowBars { get; }

        public DateTime WindowOldest { get; }
        public DateTime WindowNewest { get; }

        public PoiSnapshot(List<Poi> zones, int confluenceZoneCount, double averageTrueRange,
                           double[] buyBottom, double[] buyTop,
                           double[] sellBottom, double[] sellTop,
                           DateTime windowOldest = default(DateTime),
                           DateTime windowNewest = default(DateTime),
                           int windowBars = 0)
        {
            Zones = zones;
            ConfluenceZoneCount = confluenceZoneCount;
            AverageTrueRange = averageTrueRange;
            WindowOldest = windowOldest;
            WindowNewest = windowNewest;
            WindowBars = windowBars;
            _buyBottom = buyBottom;
            _buyTop = buyTop;
            _sellBottom = sellBottom;
            _sellTop = sellTop;
        }

        /// Is the price sitting in enough of the right kind of zone to take a
        /// trade? Proximity of zero means strictly inside the zone; anything
        /// larger widens every zone by that many ATR before testing.
        public bool HasConfluence(double price, bool isBuy, double proximity, int minimumZones)
        {
            if (minimumZones < 1)
                return true;

            double[] bottoms = isBuy ? _buyBottom : _sellBottom;
            double[] tops = isBuy ? _buyTop : _sellTop;

            int hits = 0;
            for (int i = 0; i < bottoms.Length; i++)
            {
                if (price < bottoms[i] - proximity)
                    continue;

                if (price > tops[i] + proximity)
                    continue;

                if (++hits >= minimumZones)
                    return true;
            }

            return false;
        }
    }

    /// Detection and gate settings, copied out of the robot's [Parameter]
    /// properties once in OnStart so the detector and renderer never touch the
    /// robot and stay independently testable.
    internal sealed class PoiSettings
    {
        // Detection
        public bool DrawZones = true;
        public bool ShowOrderBlocks = true;
        public bool ShowBreakerBlocks = true;
        public bool ShowFairValueGaps = true;
        public bool ShowLiquidityPools = true;

        // An order block is normally the body of the origin candle. Wicks are
        // opt-in because including them widens every zone by roughly the ATR of
        // the origin bar, which on gold turns tight blocks into wide bands.
        public bool OrderBlockIncludesWicks;

        public int SwingStrength = 2;
        public double OrderBlockMinImpulseAtr = 1.0;
        public double FvgMinGapAtr = 0.1;
        public double LiquidityToleranceAtr = 0.15;

        // Equal highs are drawn at the higher of the pair and equal lows at the
        // lower, i.e. the side price reaches first and takes the front line.
        public bool LiquidityAtFrontLine = true;

        public int LookbackBars = 200;
        public int ExtendHours = 24;
        public int MaxObjects = 120;

        // Confluence gate
        public bool OrderBlocksCount = true;
        public bool FgvsCount = true;
        public bool BreakersCount = true;
        public bool LiquidityCounts = true;
        public int MinConfluenceZones = 1;
        public double ProximityAtr = 0.5;
    }

    /// Presentation settings. Resolved once in OnStart so no string parsing or
    /// colour arithmetic happens in the refresh path.
    internal sealed class PoiStyle
    {
        public bool DrawLabels = true;
        public double LabelFontSize = 10;
        public bool SolidBorder = true;

        // cTrader's chart shapes take a single colour that serves as both the
        // border and, when IsFilled is set, the fill. There is no separate fill
        // opacity, so a zone is drawn as two shapes: a translucent filled
        // rectangle plus an unfilled one on top for the outline.
        public int ZoneAlpha = 70;
        public int BorderAlpha = 190;
        public int LabelAlpha = 220;

        public Color OrderBlockBuy;
        public Color OrderBlockSell;
        public Color BreakerBuy;
        public Color BreakerSell;
        public Color FairValueGapBuy;
        public Color FairValueGapSell;
        public Color EqualHighsColor;
        public Color EqualLowsColor;

        /// Parses one "#RRGGBB" / "RRGGBB" / named-colour parameter.
        ///
        /// Logging lives with the caller rather than here: this is a static
        /// helper on a plain object, and there is no static Print on the cTrader
        /// API. The caller reports a failure once, at start-up.
        public static bool TryParseColor(string text, out Color color)
        {
            color = Color.Transparent;

            if (string.IsNullOrWhiteSpace(text))
                return false;

            string trimmed = text.Trim();
            if (trimmed[0] == '#')
                trimmed = trimmed.Substring(1);

            try
            {
                color = trimmed.Length == 6
                    ? Color.FromHex(trimmed)
                    : Color.FromName(trimmed);
            }
            catch (Exception)
            {
                color = Color.Transparent;
                return false;
            }

            return true;
        }
    }
}