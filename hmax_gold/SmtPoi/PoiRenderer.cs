// Turns a detection snapshot into chart objects.
//
// Two lessons are baked into this file, both learned the hard way:
//
//  1. Zones are detected on a higher timeframe but drawn on the chart's own
//     timeframe, so every time is resolved to a *bar index on the chart's
//     series* before anything is drawn. Handing cTrader a higher-timeframe
//     timestamp and letting it look the position up itself silently produces
//     nothing when that timestamp is not a chart bar, and an object that is not
//     on the chart is indistinguishable from one that is off screen.
//  2. Orphan eviction works from the names this renderer drew, never from
//     enumerating the chart. Whether Chart.Objects is limited to the visible
//     range is not documented, and if it is then a viewport sweep would delete
//     every zone that has simply scrolled out of view.
using System;
using System.Collections.Generic;

namespace cAlgo.Robots
{
    /// Outcome of one render pass. Reported once so that "nothing appeared" is
    /// always answerable from the log.
    internal sealed class PoiRenderReport
    {
        public int Zones;
        public int Drawn;
        public int Clamped;
        public int Objects;

        public int ChartBars;
        public DateTime ChartFirstOpen;
        public DateTime ChartLastOpen;
        public double ChartTopY;
        public double ChartBottomY;

        public DateTime OldestZone;
        public DateTime NewestZone;
    }

    internal sealed class PoiRenderer
    {
        // Everything this renderer owns starts with this. Cleanup can then be
        // exact: sweep objects carrying this prefix and nothing else, which is
        // the difference between a safe restart and Chart.RemoveAllObjects()
        // wiping the trader's own drawings.
        internal const string Prefix = "SMTPOI_";

        // Z-order within the zones themselves: body under border under label.
        // Non-negative on purpose: cTrader documents ZIndex only as ordering
        // between chart objects, and a negative layer can composite behind the
        // chart background, which looks exactly like detecting nothing.
        private const int ZoneZIndex = 1;
        private const int BorderZIndex = 2;
        private const int LabelZIndex = 3;

        /// A level zone (EQH/EQL) has top == bottom, and a rectangle with no
        // height may render as nothing at all. Zones are given this fraction of
        /// the chart's visible price range so a level reads as a thin band at any
        // zoom level.
        private const double MinimumHeightDivisor = 300.0;

        private readonly Chart _chart;
        private readonly PoiStyle _style;
        private readonly double _tickSize;

        private readonly HashSet<string> _live = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _known = new HashSet<string>(StringComparer.Ordinal);

        public PoiRenderer(Chart chart, PoiStyle style)
            : this(chart, style, 0.01)
        {
        }

        public PoiRenderer(Chart chart, PoiStyle style, double tickSize)
        {
            _chart = chart;
            _style = style;
            _tickSize = tickSize > 0 ? tickSize : 0.01;
        }

        // ── Lifetime ──────────────────────────────────────────────────────

        /// Removes every object this renderer previously created, plus any
        /// prefixed object left on the chart by an earlier session.
        ///
        /// The chart sweep is needed because a restarted cBot has an empty
        /// _known, and the renderer cannot enumerate names it never drew. It is
        /// safe even if the chart reports a viewport-limited list: anything
        /// missed was off screen and gets removed on a later sweep.
        public void Clear()
        {
            List<string> names = new List<string>(_known);
            AddChartObjectsWithPrefix(names);

            for (int i = 0; i < names.Count; i++)
            {
                try
                {
                    _chart.RemoveObject(names[i]);
                }
                catch (Exception)
                {
                    // An object can be destroyed by the chart between the name
                    // snapshot and the removal. Losing a rectangle is not worth
                    // failing start-up or shutdown over.
                }
            }

            _live.Clear();
            _known.Clear();
        }

        // ── Render ────────────────────────────────────────────────────────

        public PoiRenderReport Render(PoiSnapshot snapshot)
        {
            _live.Clear();

            PoiRenderReport report = new PoiRenderReport();
            List<Poi> zones = snapshot.Zones;

            if (zones != null)
            {
                for (int i = 0; i < zones.Count; i++)
                {
                    report.Zones++;
                    TrackZoneTimes(report, zones[i]);

                    if (Draw(zones[i], report))
                        report.Drawn++;
                }
            }

            EvictOrphans();

            report.Objects = _live.Count;
            DescribeChart(report);

            return report;
        }

        private void DescribeChart(PoiRenderReport report)
        {
            Bars bars = _chart.Bars;

            if (bars != null && bars.Count > 0)
            {
                report.ChartBars = bars.Count;
                report.ChartFirstOpen = bars.OpenTimes[0];
                report.ChartLastOpen = bars.OpenTimes[bars.Count - 1];
            }

            report.ChartTopY = _chart.TopY;
            report.ChartBottomY = _chart.BottomY;
        }

        /// Removes objects this renderer drew previously that the current
        /// snapshot did not claim. Drives the object cap: when a zone falls out
        /// of the lookback window its rectangles leave with it.
        private void EvictOrphans()
        {
            if (_known.Count > 0)
            {
                List<string> stale = null;

                foreach (string name in _known)
                {
                    if (_live.Contains(name))
                        continue;

                    if (stale == null)
                        stale = new List<string>();

                    stale.Add(name);
                }

                if (stale != null)
                {
                    for (int i = 0; i < stale.Count; i++)
                    {
                        try
                        {
                            _chart.RemoveObject(stale[i]);
                        }
                        catch (Exception)
                        {
                            // Same reasoning as Clear().
                        }
                    }
                }
            }

            _known.Clear();

            foreach (string name in _live)
                _known.Add(name);
        }

        // ── Drawing ───────────────────────────────────────────────────────

        private void TrackZoneTimes(PoiRenderReport report, Poi zone)
        {
            if (report.OldestZone == default(DateTime) || zone.OriginTime < report.OldestZone)
                report.OldestZone = zone.OriginTime;

            if (zone.OriginTime > report.NewestZone)
                report.NewestZone = zone.OriginTime;
        }

        /// Index of the chart bar that contains, or most closely precedes, a time.
        ///
        /// OpenTimes is CHRONOLOGICAL — index 0 is the oldest bar the chart holds
        /// and Count - 1 the newest — and a chart bar index is the SAME ordering,
        /// not a shift counted back from the newest bar. cTrader states the
        /// equivalence itself: the bar-index and time overloads of DrawRiskReward
        /// are documented as `Chart.LastVisibleBarIndex - 10` and
        /// `Bars.OpenTimes[Chart.LastVisibleBarIndex - 10]`, and the drawing guide
        /// offers `Bars.OpenTimes[Chart.LastVisibleBarIndex]` as the time form of
        /// `Chart.LastVisibleBarIndex`. So the chronological index the search
        /// finds is handed straight to Chart.Draw*; converting it into a
        /// bars-back-from-the-newest shift (Count - 1 - index) mirrors every zone
        /// onto the opposite end of the chart, which is what drew zones near the
        /// chart's start instead of at their own time.
        ///
        /// This is a binary search rather than TimeSeries.GetIndexByTime because
        /// that method's behaviour for a time that is not an exact bar open is not
        /// documented, and an unverified answer here means zones silently vanish.
        /// Times older than the chart's history report false rather than clamping;
        /// the caller decides what to do about them.
        private static bool TryGetBarIndex(Bars bars, DateTime time, out int index)
        {
            index = -1;

            if (bars == null || bars.Count == 0)
                return false;

            int newest = bars.Count - 1;

            // Older than the oldest bar the chart holds.
            if (time < bars.OpenTimes[0])
                return false;

            // Newer than the newest bar, including the forming bar. Anchors to
            // the newest bar: a chart object cannot be placed on an index that
            // does not exist yet, and index 0 is the OLDEST bar, not this one.
            if (time >= bars.OpenTimes[newest])
            {
                index = newest;
                return true;
            }

            // Upper bound: the first bar that opens after `time`, so the bar
            // containing the time is the one before it.
            int lo = 0;
            int hi = newest;

            while (lo < hi)
            {
                int mid = lo + (hi - lo) / 2;

                if (bars.OpenTimes[mid] <= time)
                    lo = mid + 1;
                else
                    hi = mid;
            }

            index = lo - 1;
            return true;
        }

        private bool Draw(Poi zone, PoiRenderReport report)
        {
            string zoneName = ZoneName(zone);

            Bars bars = _chart.Bars;
            if (bars == null || bars.Count == 0)
                return false;

            // A zone whose right edge precedes its origin is a zero-width
            // rectangle and cannot be drawn at all.
            if (zone.EndTime <= zone.OriginTime)
                return false;

            int from;
            int to;

            // A zone that formed before the chart's history is not discarded, it is
            // drawn from the chart's oldest bar. Skipping it would hide exactly the
            // long-lived order blocks and unfilled gaps a trader most wants to
            // see, and it would do so silently. A zone that formed before the
            // chart starts but is still unmitigated is still live.
            bool clamped = false;

            if (!TryGetBarIndex(bars, zone.OriginTime, out from))
            {
                from = 0;
                clamped = true;
            }

            if (!TryGetBarIndex(bars, zone.EndTime, out to))
            {
                to = 0;
                clamped = true;
            }

            if (clamped)
                report.Clamped++;

            // A zone pinned entirely into the oldest bar would be a zero-width
            // rectangle anchored at a time the chart does not hold, which renders
            // as nothing. Give it one bar of width so a zone older than the
            // history is still visible at the left edge instead of silently gone.
            if (clamped && to <= from)
            {
                to = from + 1;

                if (to > bars.Count - 1)
                    to = bars.Count - 1;
            }

            double top = Math.Max(zone.Top, zone.Bottom);
            double bottom = Math.Min(zone.Top, zone.Bottom);
            double minimum = MinimumHeight();

            if (top - bottom < minimum)
            {
                // Centre the band on the level rather than hanging it all below,
                // so a level line stays at its actual price.
                double centre = (top + bottom) / 2.0;
                top = centre + minimum / 2.0;
                bottom = centre - minimum / 2.0;
            }

            Color baseColor = ResolveColor(zone);
            string borderName = BorderName(zoneName);

            // cTrader has one colour per shape and no separate fill opacity, so
            // a zone is two objects: a translucent body and an opaque outline
            // sitting on top of it. One rectangle could not do both jobs.
            //
            // Chart bar indices are chronological (0 = oldest bar), so a zone
            // that started in the past has from < to. Only from == to means the
            // zone sits inside a single chart bar, and that is the one case where
            // a bar-index anchor would collapse it to zero width, so it falls
            // back to the time overloads and lets cTrader place both edges inside
            // that bar.
            bool anchorByBar = to != from;

            ChartRectangle body = anchorByBar
                ? _chart.DrawRectangle(zoneName, from, top, to, bottom,
                                       Color.FromArgb(ClampAlpha(_style.ZoneAlpha), baseColor), 1)
                : _chart.DrawRectangle(zoneName, zone.OriginTime, top, zone.EndTime, bottom,
                                       Color.FromArgb(ClampAlpha(_style.ZoneAlpha), baseColor), 1);

            body.IsFilled = true;
            body.IsInteractive = false;
            body.IsLocked = true;
            body.ZIndex = ZoneZIndex;
            body.Comment = Describe(zone);

            _live.Add(zoneName);

            if (_style.SolidBorder)
            {
                ChartRectangle border = anchorByBar
                    ? _chart.DrawRectangle(borderName, from, top, to, bottom,
                                           Color.FromArgb(ClampAlpha(_style.BorderAlpha), baseColor), 1,
                                           LineStyle.Solid)
                    : _chart.DrawRectangle(borderName, zone.OriginTime, top, zone.EndTime, bottom,
                                           Color.FromArgb(ClampAlpha(_style.BorderAlpha), baseColor), 1,
                                           LineStyle.Solid);

                border.IsFilled = false;
                border.IsInteractive = false;
                border.IsLocked = true;
                border.ZIndex = BorderZIndex;

                _live.Add(borderName);
            }

            if (_style.DrawLabels)
                DrawLabel(zone, zoneName, from, top, bottom);

            return true;
        }

        /// Minimum zone height in price terms.
        ///
        /// Taken from the chart's visible range so that a level zone is visible
        /// at every zoom level instead of being a hairline that disappears as
        /// soon as the chart is zoomed out past the tick size.
        private double MinimumHeight()
        {
            double span = _chart.TopY - _chart.BottomY;

            if (span > 0)
                return span / MinimumHeightDivisor;

            return _tickSize * 4.0;
        }

        private void DrawLabel(Poi zone, string zoneName, int barIndex, double top, double bottom)
        {
            string labelName = LabelName(zoneName);
            Color color = Color.FromArgb(ClampAlpha(_style.LabelAlpha), ResolveColor(zone));
            double y = (top + bottom) / 2.0;

            ChartText label = _chart.DrawText(labelName, Abbreviate(zone), barIndex, y, color);

            label.FontSize = _style.LabelFontSize;
            label.VerticalAlignment = VerticalAlignment.Center;
            label.HorizontalAlignment = HorizontalAlignment.Left;
            label.IsInteractive = false;
            label.IsLocked = true;
            label.ZIndex = LabelZIndex;

            _live.Add(labelName);
        }

        // ── Naming and ownership ──────────────────────────────────────────

        internal static string ZoneName(Poi zone)
        {
            return ZoneName(zone.Kind, zone.Side, zone.OriginTime);
        }

        internal static string ZoneName(PoiKind kind, PoiSide side, DateTime origin)
        {
            return Prefix + KindCode(kind) + SideCode(side) + origin.ToString("yyyyMMddHHmmss");
        }

        private static string KindCode(PoiKind kind)
        {
            switch (kind)
            {
                case PoiKind.OrderBlock:
                    return "0B";
                case PoiKind.BreakerBlock:
                    return "1K";
                case PoiKind.FairValueGap:
                    return "2F";
                case PoiKind.EqualHighs:
                    return "3H";
                default:
                    return "4L";
            }
        }

        private static string SideCode(PoiSide side)
        {
            return side == PoiSide.Buy ? "B" : "S";
        }

        private static string BorderName(string zoneName)
        {
            return zoneName + "E";
        }

        private static string LabelName(string zoneName)
        {
            return zoneName + "L";
        }

        private static bool Owns(string name)
        {
            return name != null && name.StartsWith(Prefix, StringComparison.Ordinal);
        }

        private void AddChartObjectsWithPrefix(List<string> names)
        {
            try
            {
                var objects = _chart.Objects;
                if (objects == null)
                    return;

                for (int i = 0; i < objects.Count; i++)
                {
                    ChartObject chartObject = objects[i];

                    if (chartObject == null || !Owns(chartObject.Name))
                        continue;

                    if (!names.Contains(chartObject.Name))
                        names.Add(chartObject.Name);
                }
            }
            catch (Exception)
            {
                // Cleanup is best effort; see Clear().
            }
        }

        /// How many prefixed objects the chart currently reports. Diagnostics
        /// only, so a zone count that disagrees with an object count is visible
        /// in the log instead of being inferred from an apparently blank chart.
        internal int OwnedCount()
        {
            return OwnedNames().Count;
        }

        private List<string> OwnedNames()
        {
            var names = new List<string>();
            AddChartObjectsWithPrefix(names);
            return names;
        }

        // ── Appearance ────────────────────────────────────────────────────

        private Color ResolveColor(Poi zone)
        {
            switch (zone.Kind)
            {
                case PoiKind.OrderBlock:
                    return zone.Side == PoiSide.Buy ? _style.OrderBlockBuy : _style.OrderBlockSell;
                case PoiKind.BreakerBlock:
                    return zone.Side == PoiSide.Buy ? _style.BreakerBuy : _style.BreakerSell;
                case PoiKind.FairValueGap:
                    return zone.Side == PoiSide.Buy ? _style.FairValueGapBuy : _style.FairValueGapSell;
                case PoiKind.EqualHighs:
                    return _style.EqualHighsColor;
                default:
                    return _style.EqualLowsColor;
            }
        }

        private static string Describe(Poi zone)
        {
            string state = zone.Mitigated ? "mitigated" : zone.Swept ? "swept" : "live";
            return zone.Kind + " " + zone.Side + " " + state;
        }

        private static string Abbreviate(Poi zone)
        {
            switch (zone.Kind)
            {
                case PoiKind.OrderBlock:
                    return zone.Side == PoiSide.Buy ? "OB+" : "OB-";
                case PoiKind.BreakerBlock:
                    return zone.Side == PoiSide.Buy ? "BRK+" : "BRK-";
                case PoiKind.FairValueGap:
                    return zone.Side == PoiSide.Buy ? "FVG+" : "FVG-";
                case PoiKind.EqualHighs:
                    return "EQH";
                default:
                    return "EQL";
            }
        }

        private static int ClampAlpha(int alpha)
        {
            if (alpha < 0)
                return 0;

            return alpha > 255 ? 255 : alpha;
        }
    }
}