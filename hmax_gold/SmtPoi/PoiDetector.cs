// Detects ICT / SMT areas of interest on a higher-timeframe series and returns
// them as an immutable PoiSnapshot.
//
// The whole design exists to keep this class off the per-tick path. It runs once
// per CLOSED bar of the source timeframe — once an hour on an H1 series under an
// M5 chart — and every buffer it needs is allocated once and reused. The only
// allocation per pass is the returned snapshot itself, which the trading path
// then reads as flat double arrays.
//
// Index convention, matching the rest of this robot: index 0 is the newest
// CLOSED bar, index k is k bars ago. The forming bar at the source series'
// Last(0) is never read, so nothing here can see a price that has not happened.
using System;
using System.Collections.Generic;

namespace cAlgo.Robots
{
    internal sealed class PoiDetector
    {
        // Below this there is not enough history for a lookback window, a swing
        // to be confirmed on either side, and a structure break to be found.
        private const int MinimumBars = 20;

        // Reused detection window. Index 0 is the newest closed bar.
        private DateTime[] _times = new DateTime[512];
        private double[] _open = new double[512];
        private double[] _high = new double[512];
        private double[] _low = new double[512];
        private double[] _close = new double[512];

        // Reused scratch, never escapes this instance.
        private readonly List<int> _swingHighs = new List<int>(128);
        private readonly List<int> _swingLows = new List<int>(128);
        private readonly List<Poi> _zones = new List<Poi>(256);

        private int _count;
        private double _tickSize;
        private TimeSpan _barStep;

        // Mean true range of the detection window, and the right edge an
        // untouched zone extends to. Both are properties of the pass, computed
        // once in Detect and read by every registration routine.
        private double _averageTrueRange;
        private TimeSpan _extend;

        private static readonly Comparison<Poi> NewestOriginFirst =
            (a, b) => b.OriginTime.CompareTo(a.OriginTime);

        public PoiSnapshot Detect(Bars source, PoiSettings settings, double tickSize)
        {
            _tickSize = tickSize > 0 ? tickSize : 0.01;

            // Last(0) of the source series is still forming, so the usable closed
            // history is one bar shorter than Count.
            int closed = source.Count - 1;
            if (closed < MinimumBars)
                return PoiSnapshot.Empty;

            int n = Math.Min(closed, Math.Max(MinimumBars, settings.LookbackBars));
            EnsureCapacity(n);
            _count = n;
            _zones.Clear();

            // The series itself is CHRONOLOGICAL — index 0 is the oldest bar and
            // Count - 1 the forming bar — so a shift counted back from the newest
            // bar becomes a chronological index through Count - 1 - shift. The
            // newest closed bar is therefore shift 1, chronological Count - 2, and
            // the loop below walks that shift forward while it walks backwards
            // through time. That is what makes buffer index 0 the newest closed
            // bar, matching the convention documented at the top of this file.
            const int firstClosedIndex = 1;

            TimeSpan shortestGap = TimeSpan.Zero;
            double trueRangeSum = 0;
            double previousClose = 0;
            bool havePreviousClose = false;

            // A series exposes its raw arrays in CHRONOLOGICAL order — index 0 is
            // the oldest bar. What counts back from the newest bar is a *shift*
            // (Last(0), "bars ago"), and reading an array with a shift instead of
            // a chronological index does not throw: it silently scans the oldest
            // part of the history. That is how a 20-bar window on a 2026 chart
            // ended up reporting zones dated 2013, every one of which was
            // off-chart. So shift c is chronological index Count - 1 - c, and the
            // invariant below proves the mapping rather than trusting it. Note
            // that this conversion is for reading series values only: chart-object
            // bar indices are already chronological and must NOT be shifted, which
            // is what misplaced zones in PoiRenderer.
            for (int i = 0; i < n; i++)
            {
                int p = source.Count - 1 - (firstClosedIndex + i);

                _times[i] = source.OpenTimes[p];
                _open[i] = source.OpenPrices[p];
                _high[i] = source.HighPrices[p];
                _low[i] = source.LowPrices[p];
                _close[i] = source.ClosePrices[p];

                if (i > 0)
                {
                    TimeSpan gap = _times[i - 1] - _times[i];
                    if (gap > TimeSpan.Zero && (shortestGap == TimeSpan.Zero || gap < shortestGap))
                        shortestGap = gap;
                }

                // The previous bar's close is carried forward rather than read
                // from _close[i + 1]: that slot has not been written yet at this
                // point in the loop, and reading it would measure every gap from
                // a close of zero and inflate the average range enormously.
                double range = _high[i] - _low[i];

                if (havePreviousClose)
                {
                    double crossing = Math.Abs(previousClose - _close[i]);
                    trueRangeSum += crossing > range ? crossing : range;
                }
                else
                {
                    // Oldest bar in the window: there is no earlier close inside
                    // the window to measure a gap from.
                    trueRangeSum += range;
                }

                previousClose = _close[i];
                havePreviousClose = true;
            }

            // If the array order were ever the other way round, the buffer would
            // run oldest-first and every "newer bar" walk in the routines below
            // would quietly run backwards through history. Refusing to produce
            // zones is the safe outcome: no POIs is a neutral input to the gate,
            // whereas zones stamped with 2013 dates are not.
            if (_times[0] <= _times[n - 1])
                return PoiSnapshot.Empty;

            // The nominal bar length is the shortest positive gap between
            // consecutive opens, which skips weekend and holiday gaps instead of
            // letting them inflate a zone's right edge by three days.
            _barStep = shortestGap > TimeSpan.Zero ? shortestGap : TimeSpan.FromHours(1);

            _averageTrueRange = trueRangeSum / n;
            _extend = TimeSpan.FromHours(Math.Max(1, settings.ExtendHours));

            DetectOrderBlocks(settings);
            DetectFairValueGaps(settings);
            DetectLiquidity(settings);

            // Newest origin first, which is the order the renderer draws in and
            // therefore the order the object budget is spent in.
            _zones.Sort(NewestOriginFirst);

            // settings.MaxObjects is deliberately NOT enforced here. _zones feeds
            // the confluence gate as well as the chart, so truncating it to a
            // drawing budget made the rectangle limit decide which zones the
            // robot was allowed to trade. The cap lives in PoiRenderer now.
            return BuildSnapshot(settings);
        }

        // ── Order blocks ─────────────────────────────────────────────────
        //
        // A bullish order block is the last down-close candle before an up move
        // that takes out the most recent swing high by a configurable margin. The
        // structural break is what separates an order block from any random red
        // candle — without it this degenerates into "mark every down bar", which
        // on gold is one zone per bar and useless.
        private void DetectOrderBlocks(PoiSettings settings)
        {
            if (!settings.ShowOrderBlocks)
                return;

            int strength = Math.Max(1, settings.SwingStrength);
            double minImpulse = settings.OrderBlockMinImpulseAtr * _averageTrueRange;
            double referenceHigh = double.NaN;
            double referenceLow = double.NaN;

            // Oldest to newest, so i decreases.
            for (int i = _count - 1; i >= 0; i--)
            {
                int origin = i + 1;
                if (origin < _count)
                {
                    if (_close[i] > _close[origin] &&
                        !double.IsNaN(referenceHigh) &&
                        _close[i] > referenceHigh &&
                        _close[i] - _high[origin] >= minImpulse)
                    {
                        AppendOrderBlock(origin, i, PoiSide.Buy, settings);
                    }
                    else if (_close[i] < _close[origin] &&
                             !double.IsNaN(referenceLow) &&
                             _close[i] < referenceLow &&
                             _low[origin] - _close[i] >= minImpulse)
                    {
                        AppendOrderBlock(origin, i, PoiSide.Sell, settings);
                    }
                }

                // Swings are resolved AFTER the block above, so the reference
                // level at bar i always comes from a strictly older bar and a
                // zone can never be validated by its own impulse.
                if (i - strength >= 0 && i + strength < _count)
                {
                    if (IsSwingHigh(i, strength))
                        referenceHigh = _high[i];

                    if (IsSwingLow(i, strength))
                        referenceLow = _low[i];
                }
            }
        }

        private void AppendOrderBlock(int origin, int formationEnd, PoiSide side,
                                      PoiSettings settings)
        {
            double top = _high[origin];
            double bottom = _low[origin];

            if (!settings.OrderBlockIncludesWicks)
            {
                double open = _open[origin];
                double close = _close[origin];
                top = Math.Max(open, close);
                bottom = Math.Min(open, close);
            }

            AppendZone(PoiKind.OrderBlock, side, origin, formationEnd, top, bottom, settings);
        }

        // ── Fair value gaps ──────────────────────────────────────────────
        //
        // Three consecutive bars: bullish when the newest bar's low clears the
        // oldest bar's high, which leaves an untouched band between them.
        private void DetectFairValueGaps(PoiSettings settings)
        {
            if (!settings.ShowFairValueGaps)
                return;

            double minimumGap = settings.FvgMinGapAtr * _averageTrueRange;

            for (int i = 1; i + 1 < _count; i++)
            {
                if (_low[i - 1] > _high[i + 1])
                {
                    if (_low[i - 1] - _high[i + 1] >= minimumGap)
                        AppendZone(PoiKind.FairValueGap, PoiSide.Buy, i + 1, i - 1,
                                   _low[i - 1], _high[i + 1], settings);
                }
                else if (_high[i - 1] < _low[i + 1])
                {
                    if (_low[i + 1] - _high[i - 1] >= minimumGap)
                        AppendZone(PoiKind.FairValueGap, PoiSide.Sell, i + 1, i - 1,
                                   _low[i + 1], _high[i - 1], settings);
                }
            }
        }

        // ── Equal highs / equal lows ─────────────────────────────────────
        //
        // Resting liquidity is what actually pulls price to a POI, so it is
        // drawn explicitly rather than left for the trader to eyeball. Two
        // consecutive confirmed swings within a tolerance form one level, and a
        // triple of near-identical highs collapses to a single zone.
        private void DetectLiquidity(PoiSettings settings)
        {
            if (!settings.ShowLiquidityPools)
                return;

            int strength = Math.Max(1, settings.SwingStrength);
            double tolerance = settings.LiquidityToleranceAtr * _averageTrueRange;

            CollectSwings(strength, true, _swingHighs);
            EmitLiquidity(_swingHighs, true, tolerance, settings);

            CollectSwings(strength, false, _swingLows);
            EmitLiquidity(_swingLows, false, tolerance, settings);
        }

        // Collects confirmed swing indices newest-first. A bar needs `strength`
        // bars on each side that did not exceed it, which is also the reason a
        // swing is never confirmed on the same bar it completes: the newer side
        // must already have closed.
        private void CollectSwings(int strength, bool high, List<int> into)
        {
            into.Clear();

            for (int i = 0; i < _count; i++)
            {
                if (i - strength < 0 || i + strength >= _count)
                    continue;

                double extreme = high ? _high[i] : _low[i];
                bool isSwing = true;

                for (int j = 1; j <= strength; j++)
                {
                    if (high ? extreme <= _high[i - j] : extreme >= _low[i - j])
                    {
                        isSwing = false;
                        break;
                    }

                    if (high ? extreme <= _high[i + j] : extreme >= _low[i + j])
                    {
                        isSwing = false;
                        break;
                    }
                }

                if (isSwing)
                    into.Add(i);
            }
        }

        private void EmitLiquidity(List<int> swings, bool high, double tolerance,
                                   PoiSettings settings)
        {
            for (int a = 0; a + 1 < swings.Count; a++)
            {
                int newer = swings[a];
                int older = swings[a + 1];

                double recent = high ? _high[newer] : _low[newer];
                double distant = high ? _high[older] : _low[older];

                if (Math.Abs(recent - distant) > tolerance)
                    continue;

                // Collapse clusters: if this swing is already within tolerance
                // of the one before it, the pair that contains it as the OLDER
                // member owns the level and this pair would only shade it.
                if (a > 0)
                {
                    double previous = high ? _high[swings[a - 1]] : _low[swings[a - 1]];
                    if (Math.Abs(recent - previous) <= tolerance)
                        continue;
                }

                double upper = Math.Max(recent, distant);
                double lower = Math.Min(recent, distant);
                double level = settings.LiquidityAtFrontLine ? upper : (upper + lower) / 2.0;

                DateTime end = _times[older] + _extend;
                bool swept = false;

                // Buffer index 0 is the newest closed bar, so the scan walks
                // 0, 1, 2 ... and stops before the newer of the two defining
                // bars. A sweep has to happen after the level exists, and
                // excluding the pair also keeps a midpoint-priced level from
                // marking itself swept, since the pair's own extremes straddle
                // their midpoint.
                for (int j = 0; j < newer; j++)
                {
                    bool taken = high ? _high[j] > level : _low[j] < level;
                    if (!taken)
                        continue;

                    end = _times[j] + _barStep;
                    swept = true;
                    break;
                }

                _zones.Add(new Poi
                {
                    Kind = high ? PoiKind.EqualHighs : PoiKind.EqualLows,
                    Side = high ? PoiSide.Sell : PoiSide.Buy,
                    OriginTime = _times[older],
                    EndTime = end,

                    // Padded by a tick so an exactly-equal pair still renders as
                    // a visible band instead of a zero-height rectangle.
                    Top = upper + _tickSize,
                    Bottom = lower - _tickSize,
                    Swept = swept
                });
            }
        }

        // ── Shared zone registration ─────────────────────────────────────
        //
        // Scans forward from the bar that completed the zone and classifies
        // what price did to it — over the zone's whole life, not at its first
        // contact alone, because a touch no longer retires a zone:
        //
        //   never reached          -> live. Extends its full ExtendHours.
        //   touched, never cut     -> mitigated. Kept and marked so the chart
        //                             shows where it was taken, and still
        //                             counted by the confluence gate: a touch
        //                             disqualifies nothing.
        //   traded through         -> swept. At the FIRST contact an order
        //                             block is reborn as a breaker instead (the
        //                             one conversion in the system); every
        //                             other case — an FVG pushed through, an OB
        //                             with breakers off, or any of them touched
        //                             first and cut through later — is marked
        //                             Swept and never gates again.
        //
        // Nothing is dropped: both the touched and the traded-through zone stay
        // on the chart. What differs is the gate — touched is tradable, swept
        // is not, which used to be enforced by retiring the zone on its first
        // contact (mitigation excluded it, violation removed it entirely, so a
        // zone touched at 50 and cut through at 90 kept gating).
        private void AppendZone(PoiKind kind, PoiSide side, int origin, int formationEnd,
                                double top, double bottom, PoiSettings settings)
        {
            if (top - bottom <= 0)
                return;

            int contact;
            bool contactViolated;
            int sweep;

            ScanContact(formationEnd - 1, side, top, bottom,
                        out contact, out contactViolated, out sweep);

            if (contact < 0)
            {
                _zones.Add(new Poi
                {
                    Kind = kind,
                    Side = side,
                    OriginTime = _times[origin],
                    EndTime = _times[origin] + _extend,
                    Top = top,
                    Bottom = bottom
                });

                return;
            }

            DateTime contactEnd = _times[contact] + _barStep;

            // The one conversion in the system: a violated order block comes
            // back with the opposite polarity. Whether breakers are DRAWN is
            // ShowBreakerBlocks; whether they COUNT for confluence is
            // BreakersCount, decided later in CountsTowardConfluence. Only the
            // first contact converts: an OB that was retested first and cut
            // through later stays the same failed OB, not a breaker.
            if (contactViolated && kind == PoiKind.OrderBlock && settings.ShowBreakerBlocks)
            {
                AppendBreaker(origin, side, top, bottom, contact, contactEnd);
                return;
            }

            // Both outcomes stay on the chart — the touch and the cut-through
            // are shown where they happened — but they part ways at the gate: a
            // zone price ever traded through does not gate, a zone that was
            // only touched still does. An earlier touch does not shield a later
            // cut-through: the sweep wins whenever it happened.
            _zones.Add(new Poi
            {
                Kind = kind,
                Side = side,
                OriginTime = _times[origin],
                EndTime = sweep >= 0 ? _times[sweep] + _barStep : contactEnd,
                Top = top,
                Bottom = bottom,
                Mitigated = sweep < 0,
                Swept = sweep >= 0
            });
        }

        // The breaker's own life. Born at the bar where the parent order block
        // was cut through, so that bar — and everything the OB scan already
        // measured — cannot count as the BREAKER being swept: the scan starts
        // at the bar before it and asks the same questions with flipped
        // polarity:
        //
        //   never reached          -> live from birth, EndTime = the birth bar.
        //   touched                -> mitigated breaker, still gates.
        //   band traded through    -> Swept. CountsForGate then refuses it: a
        //                             breaker whose own band price ran through
        //                             is spent, exactly like any swept OB/FVG,
        //                             and never gates again.
        private void AppendBreaker(int origin, PoiSide obSide, double top, double bottom,
                                   int birthBar, DateTime birthEnd)
        {
            PoiSide side = obSide == PoiSide.Buy ? PoiSide.Sell : PoiSide.Buy;

            int contact;
            int sweep;

            ScanContact(birthBar - 1, side, top, bottom, out contact, out _, out sweep);

            _zones.Add(new Poi
            {
                Kind = PoiKind.BreakerBlock,
                Side = side,
                OriginTime = _times[origin],
                EndTime = sweep >= 0 ? _times[sweep] + _barStep
                         : contact >= 0 ? _times[contact] + _barStep
                         : birthEnd,
                Top = top,
                Bottom = bottom,
                Mitigated = contact >= 0 && sweep < 0,
                Swept = sweep >= 0
            });
        }

        // Walks forward from `start` — the bar after the zone completed, or the
        // bar before a breaker's birth — looking for contact with the band:
        //
        //   firstContact   first bar price came back into it (-1 if never).
        //   firstViolated  whether that first bar traded THROUGH it, which is
        //                  what converts an order block into a breaker.
        //   sweepBar       earliest bar that traded through the whole band, an
        //                  earlier touch notwithstanding (-1 if never). The
        //                  walk runs from older bars to newer ones, so the
        //                  first violation found is the earliest in time.
        private void ScanContact(int start, PoiSide side, double top, double bottom,
                                 out int firstContact, out bool firstViolated, out int sweepBar)
        {
            firstContact = -1;
            firstViolated = false;
            sweepBar = -1;

            for (int j = start; j >= 0; j--)
            {
                bool reached;
                bool violated;

                if (side == PoiSide.Buy)
                {
                    reached = _low[j] <= top;
                    violated = reached && _low[j] < bottom;
                }
                else
                {
                    reached = _high[j] >= bottom;
                    violated = reached && _high[j] > top;
                }

                if (!reached)
                    continue;

                if (firstContact < 0)
                {
                    firstContact = j;
                    firstViolated = violated;
                }

                if (violated)
                {
                    sweepBar = j;
                    return;
                }
            }
        }

        // ── Snapshot assembly ────────────────────────────────────────────

        private PoiSnapshot BuildSnapshot(PoiSettings settings)
        {
            int buyCount = 0;
            int sellCount = 0;

            // Touched zones count — a first touch disqualifies nothing. Zones
            // price traded through do not: a swept OB/FVG/breaker is refused
            // outright, while a swept EQH/EQL stays in, because trading a sweep
            // at its own level is the setup the level exists for. The gate's
            // proximity test is what confines it to that band: away from the
            // level it contributes nothing.
            for (int i = 0; i < _zones.Count; i++)
            {
                Poi zone = _zones[i];
                if (!CountsForGate(settings, zone))
                    continue;

                if (zone.Side == PoiSide.Buy)
                    buyCount++;
                else
                    sellCount++;
            }

            double[] buyBottom = new double[buyCount];
            double[] buyTop = new double[buyCount];
            double[] sellBottom = new double[sellCount];
            double[] sellTop = new double[sellCount];

            int buy = 0;
            int sell = 0;

            for (int i = 0; i < _zones.Count; i++)
            {
                Poi zone = _zones[i];
                if (!CountsForGate(settings, zone))
                    continue;

                if (zone.Side == PoiSide.Buy)
                {
                    buyBottom[buy] = zone.Bottom;
                    buyTop[buy] = zone.Top;
                    buy++;
                }
                else
                {
                    sellBottom[sell] = zone.Bottom;
                    sellTop[sell] = zone.Top;
                    sell++;
                }
            }

            // Copied because _zones is reused on the next pass and the snapshot
            // outlives it. The Poi instances themselves are never mutated after
            // this point, so a shallow copy is enough.
            return new PoiSnapshot(new List<Poi>(_zones), buyCount + sellCount, _averageTrueRange,
                                   buyBottom, buyTop, sellBottom, sellTop,
                                   // Buffer index 0 is the newest closed bar and _count - 1 the oldest, so the
                                   // window bounds are passed in that order.
                                   _times[_count - 1], _times[0], _count);
        }

        /// May this zone gate an entry at all?
        ///
        /// The one structural refusal first — a swept OB/FVG/breaker is spent
        /// and never gates — then the kind switches. A swept EQH/EQL passes
        /// both and is left to the proximity test, which confines it to its own
        /// level. Mitigated (touched, never traded through) zones are never
        /// refused: a touch disqualifies nothing.
        private static bool CountsForGate(PoiSettings settings, Poi zone)
        {
            if (zone.Swept && zone.Kind != PoiKind.EqualHighs && zone.Kind != PoiKind.EqualLows)
                return false;

            return CountsTowardConfluence(settings, zone);
        }

        private static bool CountsTowardConfluence(PoiSettings settings, Poi zone)
        {
            switch (zone.Kind)
            {
                case PoiKind.OrderBlock:
                    return settings.OrderBlocksCount;
                case PoiKind.BreakerBlock:
                    return settings.BreakersCount;
                case PoiKind.FairValueGap:
                    return settings.FgvsCount;
                case PoiKind.EqualHighs:
                case PoiKind.EqualLows:
                    return settings.LiquidityCounts;
                default:
                    return false;
            }
        }

        // ── Swing primitives ─────────────────────────────────────────────

        private bool IsSwingHigh(int i, int strength)
        {
            double extreme = _high[i];

            for (int j = 1; j <= strength; j++)
            {
                if (extreme <= _high[i - j] || extreme <= _high[i + j])
                    return false;
            }

            return true;
        }

        private bool IsSwingLow(int i, int strength)
        {
            double extreme = _low[i];

            for (int j = 1; j <= strength; j++)
            {
                if (extreme >= _low[i - j] || extreme >= _low[i + j])
                    return false;
            }

            return true;
        }

        private void EnsureCapacity(int n)
        {
            if (_times.Length >= n)
                return;

            int size = _times.Length;
            while (size < n)
                size *= 2;

            _times = new DateTime[size];
            _open = new double[size];
            _high = new double[size];
            _low = new double[size];
            _close = new double[size];
        }
    }
}