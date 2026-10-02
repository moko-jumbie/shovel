// Owns the lifetime of the POI subsystem: keeps a source series, decides when a
// detection pass is warranted, renders the result and answers confluence
// questions about it.
//
// This is the only class that knows how often detection runs. The rule is
// deliberately blunt: detection happens when the source series' forming bar
// rolls over, and not otherwise. On an H1 series under an M5 chart that is once
// an hour, so the cost of everything above this line is one DateTime read and one
// comparison per tick.
using System;
using System.Collections.Generic;

namespace cAlgo.Robots
{
    internal sealed class PoiController
    {
        private readonly PoiDetector _detector = new PoiDetector();
        private readonly Action<string> _log;

        private PoiRenderer _renderer;
        private PoiSettings _settings;
        private PoiStyle _style;
        private Bars _source;
        private PoiSnapshot _snapshot = PoiSnapshot.Empty;

        private DateTime _lastOpenTime;
        private double _tickSize = 0.01;

        // Detection runs when either consumer needs it. These are deliberately
        // separate flags: a backtest with drawing off still has to detect in
        // order to gate entries, and drawing the chart is not a reason to make
        // the gate work.
        private bool _drawing;
        private bool _gating;

        private bool _failed;
        private bool _warnedNoZones;
        private bool _warnedThinHistory;
        private bool _reportedFirstPass;
        private PoiRenderReport _lastReport;

        public PoiController(Action<string> log)
        {
            _log = log ?? (message => { });
        }

        /// True once a pass has produced a usable snapshot.
        public bool IsReady
        {
            get { return _snapshot != PoiSnapshot.Empty; }
        }

        public PoiSnapshot Snapshot
        {
            get { return _snapshot; }
        }

        /// Configures the subsystem and clears anything already on the chart.
        ///
        /// The renderer is only created when drawing is on, so a pure-gate
        /// configuration never touches the chart at all.
        public void Configure(Chart chart, PoiSettings settings, PoiStyle style, double tickSize)
        {
            _settings = settings;
            _style = style;
            _tickSize = tickSize > 0 ? tickSize : 0.01;

            // The renderer is always constructed, even when nothing will be drawn, so that
            // the prefix sweep works in every configuration. A session that was
            // started with drawing on must still clean up after itself when the
            // robot is restarted with drawing off.
            _renderer?.Clear();
            _renderer = new PoiRenderer(chart, style, tickSize);
            _drawing = settings.DrawZones;

            // A new configuration may change the snapshot's size, so force the
            // next Refresh to detect rather than trusting the old timestamp.
            _lastOpenTime = DateTime.MinValue;
        }

        /// Points the subsystem at a higher-timeframe series.
        public void Attach(Bars source)
        {
            _source = source;
            _lastOpenTime = DateTime.MinValue;
            _snapshot = PoiSnapshot.Empty;
            _failed = false;
            _warnedNoZones = false;
        }

        public void EnableGate(bool enabled)
        {
            _gating = enabled;
        }

        /// Called from OnTick. Returns immediately on every tick that does not
        /// open a new source bar.
        public void Refresh()
        {
            if (_settings == null || _failed)
                return;

            if (_source == null)
                return;

            if (_source.Count < 2)
            {
                // Previously this returned in silence on every tick forever, which
                // is indistinguishable from "the detector found nothing".
                if (!_warnedThinHistory)
                {
                    _warnedThinHistory = true;
                    _log("SMT POI: the source series has only " + _source.Count +
                         " bar(s); waiting for at least 2 before detecting.");
                }

                return;
            }

            if (!_drawing && !_gating)
                return;

            DateTime openTime = _source.OpenTimes.LastValue;
            if (openTime == _lastOpenTime)
                return;

            _lastOpenTime = openTime;

            try
            {
                _snapshot = _detector.Detect(_source, _settings, _tickSize);
            }
            catch (Exception error)
            {
                // A detector that cannot run must not take the robot with it.
                // Give up on POIs entirely rather than throwing the same
                // exception on every subsequent bar.
                _failed = true;
                _log("SMT POI detection failed and has been disabled: " + error.Message);
                return;
            }

            if (_drawing)
            {
                try
                {
                    PoiRenderReport report = _renderer?.Render(_snapshot);
                    if (report != null)
                        _lastReport = report;
                }
                catch (Exception error)
                {
                    _log("SMT POI drawing failed: " + error.Message);
                }
            }

            ReportFirstPass();
        }

        /// Describes the first detection pass in full.
        ///
        /// Every other explanation for "no zones appeared" (no layers enabled, a
        /// history shorter than the detector's minimum, thresholds that cannot be
        /// met, zones anchored outside the chart's bars) has to be visible
        /// somewhere, and a drawing-only session never reaches HasConfluence, so
        /// it would otherwise report nothing at all.
        private void ReportFirstPass()
        {
            if (_reportedFirstPass)
                return;

            _reportedFirstPass = true;

            List<string> enabled = new List<string>();
            if (_settings.ShowOrderBlocks) enabled.Add("OB");
            if (_settings.ShowBreakerBlocks) enabled.Add("breaker");
            if (_settings.ShowFairValueGaps) enabled.Add("FVG");
            if (_settings.ShowLiquidityPools) enabled.Add("EQH/EQL");

            string layers = enabled.Count == 0 ? "NONE" : string.Join("+", enabled.ToArray());

            _log("SMT POI: first pass over " + _snapshot.WindowBars + " of " + (_source.Count - 1) +
                 " closed bars (" + _snapshot.WindowOldest.ToString("yyyy-MM-dd HH:mm") +
                 " to " + _snapshot.WindowNewest.ToString("yyyy-MM-dd HH:mm") +
                 ") found " + _snapshot.Zones.Count + " zone(s) (layers " + layers +
                 ", ATR " + _snapshot.AverageTrueRange.ToString("0.####") +
                 ", " + _snapshot.ConfluenceZoneCount + " live/gateable)");

            if (!_drawing)
                return;

            PoiRenderReport report = _lastReport;
            if (report == null)
            {
                _log("SMT POI: no render pass has run yet.");
                return;
            }

            _log("SMT POI drawing: " + report.Drawn + " of " + report.Zones + " zone(s) drawn as " +
                 report.Objects + " object(s); " + report.Clamped + " clamped to the chart's range.");
            _log("SMT POI chart: " + report.ChartBars + " bars from " +
                 report.ChartFirstOpen.ToString("yyyy-MM-dd HH:mm") + " to " +
                 report.ChartLastOpen.ToString("yyyy-MM-dd HH:mm") +
                 ", visible price " + report.ChartBottomY.ToString("0.##") +
                 " to " + report.ChartTopY.ToString("0.##"));
            _log("SMT POI zones: oldest " + report.OldestZone.ToString("yyyy-MM-dd HH:mm") +
                 ", newest " + report.NewestZone.ToString("yyyy-MM-dd HH:mm"));
        }

        /// Is the price in enough of the right kind of zone to allow an entry?
        ///
        /// Returns true whenever the gate is off or nothing has been detected
        /// yet, which keeps the default configuration trading exactly as it did
        /// before this subsystem existed.
        public bool HasConfluence(double price, bool isBuy)
        {
            if (!_gating || _snapshot == PoiSnapshot.Empty)
                return true;

            // A pass that found nothing eligible is not a reason to stop
            // trading. Blocking everything here would present as a broken
            // robot rather than as a permissive filter, so report it once and
            // behave as though the gate were off.
            if (_snapshot.ConfluenceZoneCount == 0)
            {
                if (!_warnedNoZones)
                {
                    _warnedNoZones = true;
                    _log("SMT POI: no live zones found; the confluence gate is allowing all entries.");
                }

                return true;
            }

            double proximity = Math.Max(0, _settings.ProximityAtr) * _snapshot.AverageTrueRange;
            return _snapshot.HasConfluence(price, isBuy, proximity, _settings.MinConfluenceZones);
        }

        /// Removes every chart object this subsystem created.
        public void Clear()
        {
            try
            {
                _renderer?.Clear();
            }
            catch (Exception error)
            {
                _log("SMT POI cleanup failed: " + error.Message);
            }
        }
    }
}