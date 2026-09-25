using System;
using System.Collections.Generic;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Indicators;
using cAlgo.API.Internals;

namespace cAlgo.Robots
{
    // Behavioural port of MT4EAv4.txt (HMA_Crossover_Smart_Run4_FIXED).
    // Trading paths only; the CYBER MATRIX HUD is intentionally not ported.
    //
    // Parity notes (EA line references):
    //  - OnTick is used rather than OnBarClosed because the EA advances
    //    lastBarTime ONLY on a successful send (EA:578,609). When a bar's signal
    //    is rejected the EA re-evaluates on every tick until a send succeeds.
    //    Replacing that with a once-per-bar event would change trade timing.
    //  - MagicNumber 84721 maps to the cTrader position Label.
    //  - Slippage has no market-order equivalent in cTrader; the symbol's
    //    server-side execution settings apply.
    //  - OrderSend errors 135/136/138 (price changed / off quotes / requote)
    //    have no direct ErrorCode counterparts in cTrader, so the EA's
    //    resend-on-transient-error step is replaced by a logged failure.
    //  - cTrader History stores one record per round trip, so a losing trade
    //    counts once toward the streak (MT4 counted its entry and exit orders
    //    separately). Today's P&L is unaffected.
    //
    // Performance notes (all result-preserving unless noted):
    //  - Because lastBarTime only advances on a successful send, the signal
    //    block re-runs on every tick after the first bar that does not trade.
    //    The bar-invariant part of it (HMA, SMA, ATR, crossover) is memoised
    //    against the bar open time, and both per-tick history walks are cached
    //    against History.Count, so the per-tick cost drops to a handful of
    //    native reads. Values are bit-identical to the uncached version.
    //  - SignalOncePerBar goes further and evaluates the signal block at most
    //    once per bar. This is the largest single win but is NOT parity
    //    preserving: an intra-bar spread spike, session boundary or streak
    //    change is no longer re-checked unless a position closes, and SL/TP
    //    are sized from the first qualifying tick of the bar rather than any
    //    qualifying tick. Leave it off to reproduce the EA.
    [Robot(TimeZone = TimeZones.EAfricaStandardTime, AccessRights = AccessRights.None)]
    public class hmax_gold : Robot
    {
        // ── Circuit Breaker Matrix ────────────────────────────────────────
        [Parameter("Use Daily Loss Limit", DefaultValue = true)]
        public bool UseDailyLossLimit { get; set; }

        [Parameter("Max Daily Loss Percent", DefaultValue = 0.8, MinValue = 0.01)]
        public double MaxDailyLossPercent { get; set; }

        [Parameter("Use Consecutive Loss Guard", DefaultValue = true)]
        public bool UseConsecLossGuard { get; set; }

        [Parameter("Max Consecutive Losses", DefaultValue = 3, MinValue = 1)]
        public int MaxConsecLosses { get; set; }

        [Parameter("Reduced Risk Multiplier", DefaultValue = 0.5, MinValue = 0.01, MaxValue = 1.0)]
        public double ReducedRiskMult { get; set; }

        [Parameter("Use Account Daily Close", DefaultValue = true)]
        public bool UseAccountDailyClose { get; set; }

        [Parameter("Account Daily Close Percent", DefaultValue = 4.0, MinValue = 0.01)]
        public double AccountDailyClosePercent { get; set; }

        // ── Strategy Core Configuration ──────────────────────────────────
        [Parameter("Label (EA magic 84721)", DefaultValue = "HMA Cross")]
        public string Label { get; set; }

        [Parameter("Fast HMA Period", DefaultValue = 20, MinValue = 2)]
        public int FastPeriod { get; set; }

        [Parameter("Slow HMA Period", DefaultValue = 30, MinValue = 3)]
        public int SlowPeriod { get; set; }

        // ── High Probability Filtering ───────────────────────────────────
        [Parameter("Use Macro Trend Filter", DefaultValue = true)]
        public bool UseSmaFilter { get; set; }

        [Parameter("SMA Filter Period", DefaultValue = 200, MinValue = 10)]
        public int SmaPeriod { get; set; }

        // ── Advanced Guardrails ──────────────────────────────────────────
        [Parameter("Use ADX Filter", DefaultValue = true)]
        public bool UseAdxFilter { get; set; }

        [Parameter("ADX Threshold", DefaultValue = 28, MinValue = 1)]
        public int AdxThreshold { get; set; }

        [Parameter("Use Trailing Stop", DefaultValue = true)]
        public bool UseTrailingStop { get; set; }

        [Parameter("Trail Activation (ATR mult)", DefaultValue = 3.0, MinValue = 0.1)]
        public double TrailActivation { get; set; }

        [Parameter("Trail Cushion (ATR mult)", DefaultValue = 1.5, MinValue = 0.1)]
        public double TrailCushion { get; set; }

        [Parameter("Re-entry Cooldown (bars)", DefaultValue = 3, MinValue = 0)]
        public int ReentryCooldownBars { get; set; }

        // Off by default, which reproduces the EA exactly: the EA re-evaluates
        // the whole signal chain on every tick until a send succeeds. Turning
        // this on evaluates the signal chain at most once per bar, which is the
        // single largest performance win available, at the cost of no longer
        // re-checking an intra-bar spread spike, session boundary or streak
        // change unless a position closes.
        [Parameter("Evaluate Signal Once Per Bar", DefaultValue = false)]
        public bool SignalOncePerBar { get; set; }

        // The optimiser restarts the robot once per pass and every order logged
        // is duplicated into the Log tab, so per-order logging is opt-in.
        [Parameter("Verbose Logging", DefaultValue = false)]
        public bool VerboseLogging { get; set; }

        // ── Live Server Protection Matrix ────────────────────────────────
        [Parameter("Friday Auto Close", DefaultValue = true)]
        public bool FridayAutoClose { get; set; }

        [Parameter("Friday Close Hour", DefaultValue = 22, MinValue = 0, MaxValue = 23)]
        public int FridayCloseHour { get; set; }

        [Parameter("Use Spread Filter", DefaultValue = true)]
        public bool UseSpreadFilter { get; set; }

        [Parameter("Max Spread Pips", DefaultValue = 20, MinValue = 1)]
        public int MaxSpreadPips { get; set; }

        [Parameter("Spread Pip Divisor (0=auto)", DefaultValue = 0, MinValue = 0)]
        public int SpreadPipDivisor { get; set; }

        // ── Session Filters (broker time) ────────────────────────────────
        [Parameter("Trade Sydney", DefaultValue = true)]
        public bool TradeSydney { get; set; }

        [Parameter("Trade London", DefaultValue = true)]
        public bool TradeLondon { get; set; }

        [Parameter("Trade New York", DefaultValue = true)]
        public bool TradeNY { get; set; }

        [Parameter("Trade Asia / Tokyo", DefaultValue = false)]
        public bool TradeAsia { get; set; }

        // ── Volatility Risk Matrix ───────────────────────────────────────
        [Parameter("Auto Money Management", DefaultValue = true)]
        public bool AutoMoneyManagement { get; set; }

        [Parameter("Risk % Per Trade", DefaultValue = 1.0, MinValue = 0.01)]
        public double RiskPercentPerTrade { get; set; }

        [Parameter("Fallback Volume (Lots)", DefaultValue = 0.10, MinValue = 0.01)]
        public double LotSize { get; set; }

        [Parameter("ATR Period", DefaultValue = 14, MinValue = 1)]
        public int AtrPeriod { get; set; }

        [Parameter("SL ATR Multiplier", DefaultValue = 2.0, MinValue = 0.1)]
        public double SlAtrMultiplier { get; set; }

        [Parameter("TP ATR Multiplier", DefaultValue = 5.0, MinValue = 0.1)]
        public double TpAtrMultiplier { get; set; }

        // cTrader exposes no MODE_STOPLEVEL. 0 = derive from the live spread.
        [Parameter("Broker Min Stop Distance (price, 0=spread)", DefaultValue = 0.0, MinValue = 0.0)]
        public double BrokerMinStopDistance { get; set; }

        // ── Indicators ───────────────────────────────────────────────────
        private SimpleMovingAverage _smaFilter;
        private AverageDirectionalMovementIndexRating _adx;
        private AverageTrueRange _atr;

        // ── State tracking globals (EA:110-154) ─────────────────────────
        private DateTime _lastBarTime;
        private double _dayStartBalance;
        private DateTime _lastDayTracked;
        private double _accountDayStartEquity;
        private DateTime _accountDayTracked;
        private bool _accountLossTripped;
        private int _consecLosses;
        private int _barsSinceLastLoss;

        // Per-bar signal snapshot, valid while _sigBarTime == the current bar.
        // See OnTick step 9-12 and EnsureSignalSnapshot.
        private DateTime _lastEvaluatedBarTime;
        private DateTime _sigBarTime = default;
        private bool _sigCached;
        private double _sigFastHmaCurrent;
        private double _sigSlowHmaCurrent;
        private double _sigFastHmaPrior;
        private double _sigSlowHmaPrior;
        private double _sigMacroSma;
        private double _sigClosePrice;
        private double _sigAtrValue;
        private double _sigStopLossDistance;
        private double _sigTakeProfitDistance;
        private bool _sigBullishCross;
        private bool _sigBearishCross;

        protected override void OnStart()
        {
            _smaFilter = Indicators.SimpleMovingAverage(Bars.ClosePrices, SmaPeriod);
            _adx = Indicators.AverageDirectionalMovementIndexRating(14);
            _atr = Indicators.AverageTrueRange(AtrPeriod, MovingAverageType.WilderSmoothing);

            // EA:161-165 — gLastDayTracked is seeded with the current BAR open
            // time, not midnight. The daily rollover therefore fires from the
            // following day onward; this is reproduced deliberately.
            _lastBarTime = Bars.Last(0).OpenTime;
            _dayStartBalance = Account.Balance;
            _lastDayTracked = Bars.Last(0).OpenTime;
            _consecLosses = 0;
            _barsSinceLastLoss = 999;

            RestoreStateFromHistory();
            RebuildAccountDailyBaseline();

            Positions.Closed += OnPositionClosedEvent;

            Print("HMA Crossover port of HMA_Crossover_Smart_Run4_FIXED started on ",
                  SymbolName, " ", TimeFrame, " label='", Label, "'");
        }

        protected override void OnTick()
        {
            // Invalidates the Server.Time memo for this tick.
            _tickId++;

            // 1. Dynamic trailing stop (every tick) — EA:440
            ApplyAtrTrailingStop();

            // 2. Friday protection — EA:443-448
            DateTime now = ServerTime();
            if (FridayAutoClose && now.DayOfWeek == DayOfWeek.Friday &&
                now.Hour >= FridayCloseHour)
            {
                CloseOpenPositions(TradeType.Buy);
                CloseOpenPositions(TradeType.Sell);
                return;
            }

            // 3. Account-wide daily loss — EA:451-461
            if (IsAccountDailyLossBreached())
            {
                CloseAllOpenPositions();
                return;
            }

            // 4. EA-scoped daily loss limit — EA:464-473
            if (IsDailyLossLimitBreached())
                return;

            // 5. Bar close filter — EA:476-485
            DateTime currentBarTime = Bars.Last(0).OpenTime;
            if (currentBarTime == _lastBarTime)
                return;

            // FIX-6 / FIX-10: rebuild streak + cooldown once per new bar.
            RebuildLossStateFromHistory(currentBarTime);

            // 6. Spread filter — EA:496-501
            if (UseSpreadFilter && GetSpreadPips() > MaxSpreadPips)
                return;

            // 7. Session guardrail — EA:504-508
            if ((TradeSydney || TradeLondon || TradeNY || TradeAsia) && !IsWithinAllowedSession())
                return;

            // 8. ADX filter — EA:511-516 (period 14 is hardcoded in the EA)
            if (UseAdxFilter && _adx.ADX.Last(1) < AdxThreshold)
                return;

            // 9-12. Signal snapshot — EA:519-548
            //
            // Everything from here to the crossover booleans is a pure function
            // of the two most recent CLOSED bars plus this instance's period
            // parameters, so it cannot change between two ticks of the same bar.
            // Memoising it against the bar open time turns roughly 690 native
            // Series reads per tick into 690 per bar, and the cached value is
            // bit-identical to the uncached one.
            //
            // The stop-distance CLAMP is deliberately NOT cached: with
            // BrokerMinStopDistance at its default of 0 it falls back to the
            // live Symbol.Spread, which does move within a bar.
            if (SignalOncePerBar)
            {
                if (currentBarTime == _lastEvaluatedBarTime)
                    return;
                _lastEvaluatedBarTime = currentBarTime;
            }

            EnsureSignalSnapshot(currentBarTime);

            if (_sigFastHmaCurrent == 0 || _sigSlowHmaCurrent == 0 ||
                _sigFastHmaPrior == 0 || _sigSlowHmaPrior == 0)
                return;

            if (_sigAtrValue <= 0)
                return;

            // FIX-11: respect the broker minimum stop distance — EA:540-544
            double minStopDist = GetStopLevelDistance();
            double stopLossDistance = _sigStopLossDistance;
            double takeProfitDistance = _sigTakeProfitDistance;
            if (stopLossDistance < minStopDist)
                stopLossDistance = minStopDist;
            if (takeProfitDistance < minStopDist)
                takeProfitDistance = minStopDist;

            // 12. Crossover evaluation — EA:547-548
            bool isBullishCross = _sigBullishCross;
            bool isBearishCross = _sigBearishCross;

            // 13. Re-entry cooldown — EA:551-552
            if (IsCooldownActive() && (isBullishCross || isBearishCross))
                return;

            // 14. Execution core — EA:555-615
            //
            // With SignalOncePerBar enabled, the three conditions that the EA
            // retries on a later tick of the SAME bar release the latch, so the
            // EA's "keep trying until a send succeeds" behaviour is preserved.
            // Conditions that are constant within a bar (no crossover, the SMA
            // filter failing, the cooldown) keep the latch, because retrying
            // them cannot change the outcome.
            if (isBullishCross)
            {
                if (!UseSmaFilter || _sigClosePrice > _sigMacroSma)
                {
                    // FIX-8: the opposite side is NOT force-closed.
                    if (CountOpenPositions(TradeType.Buy) == 0)
                    {
                        if (!IsTradePermitted())
                        {
                            ReleaseEvaluationLatch();
                            return;
                        }

                        if (SendMarketOrder(TradeType.Buy, stopLossDistance, takeProfitDistance,
                                adjustedRisk: RiskPercentPerTrade * GetAdjustedRiskMultiplier(),
                                comment: "HMA Cross Buy"))
                        {
                            _lastBarTime = currentBarTime;
                        }
                        else
                        {
                            ReleaseEvaluationLatch();
                        }
                    }
                    else
                    {
                        ReleaseEvaluationLatch();
                    }
                }
            }
            else if (isBearishCross)
            {
                if (!UseSmaFilter || _sigClosePrice < _sigMacroSma)
                {
                    if (CountOpenPositions(TradeType.Sell) == 0)
                    {
                        if (!IsTradePermitted())
                        {
                            ReleaseEvaluationLatch();
                            return;
                        }

                        if (SendMarketOrder(TradeType.Sell, stopLossDistance, takeProfitDistance,
                                adjustedRisk: RiskPercentPerTrade * GetAdjustedRiskMultiplier(),
                                comment: "HMA Cross Sell"))
                        {
                            _lastBarTime = currentBarTime;
                        }
                        else
                        {
                            ReleaseEvaluationLatch();
                        }
                    }
                    else
                    {
                        ReleaseEvaluationLatch();
                    }
                }
            }
        }

        // A position closing mid-bar can free the slot that blocked an entry, or
        // change the streak, so the per-bar latch is released and the next tick
        // re-evaluates — matching the EA, which has no bar-level latch at all.
        //
        // Robot.OnPositionClosed(Position) is marked obsolete in this API
        // version, so the Positions.Closed event is used instead.
        private void OnPositionClosedEvent(PositionClosedEventArgs args)
        {
            Position position = args.Position;
            if (position != null && position.SymbolName == SymbolName &&
                position.Label == Label)
                ReleaseEvaluationLatch();
        }

        protected override void OnStop()
        {
            Positions.Closed -= OnPositionClosedEvent;
        }

        private void ReleaseEvaluationLatch()
        {
            if (SignalOncePerBar)
                _lastEvaluatedBarTime = default;
        }

        // ── State restoration — EA:186-224 ────────────────────────────────
        private void RestoreStateFromHistory()
        {
            DateTime todayStart = MidnightToday();
            int tempConsec = 0;
            double todayPnL = 0;

            foreach (HistoricalTrade trade in MatchingHistory())
            {
                // EA:205 scopes only the P&L accumulation to today; the streak
                // count below runs across the whole history, so no early exit.
                if (trade.ClosingTime >= todayStart)
                    todayPnL += trade.NetProfit;

                // EA:209-216 — accumulate losses from the most recent trade
                // backwards, stopping at the first non-loss.
                if (trade.NetProfit < 0)
                    tempConsec++;
                else if (tempConsec > 0)
                    break;
            }

            _consecLosses = tempConsec;
            _dayStartBalance = Account.Balance - todayPnL;
        }

        // ── EA-scoped daily loss breaker — EA:253-275 ─────────────────────
        private bool IsDailyLossLimitBreached()
        {
            if (!UseDailyLossLimit)
                return false;

            DateTime todayStart = MidnightToday();
            if (todayStart > _lastDayTracked)
            {
                _dayStartBalance = Account.Balance;
                _lastDayTracked = todayStart;
                _consecLosses = 0;
            }

            double dayLoss = -GetTodayEaPnL();
            double maxLoss = _dayStartBalance * (MaxDailyLossPercent / 100.0);

            return dayLoss >= maxLoss;
        }

        // Today's net P&L for this EA only — EA:282-311
        //
        // This is the hottest path in the robot: the daily-loss breaker calls
        // it on every tick, and a naive version re-walks the whole closed-trade
        // history and the whole account's open positions on each of those ticks.
        //
        // Both halves are cached against keys that make the result provably
        // identical to the uncached scan:
        //  - The closed half can only change when History.Count changes, because
        //    history is append-only. Its ORDER is preserved (newest first) so
        //    the floating-point accumulation is bit-identical.
        //  - The open half cannot be cached — an open position's NetProfit moves
        //    every tick — but Positions.FindAll(Label, SymbolName) is a native
        //    indexed lookup that returns only this instance's positions, instead
        //    of walking every position on the account.
        private double _todayClosedPnL;
        private int _todayClosedPnLHistoryCount = -1;
        private DateTime _todayClosedPnLDay = default;

        private double GetTodayEaPnL()
        {
            DateTime todayStart = MidnightToday();
            double net = 0;

            int historyCount = History.Count;
            if (_todayClosedPnLHistoryCount != historyCount || _todayClosedPnLDay != todayStart)
            {
                double closed = 0;
                List<HistoricalTrade> trades = MatchingHistory();
                for (int i = 0; i < trades.Count; i++)
                {
                    HistoricalTrade trade = trades[i];
                    if (trade.ClosingTime >= todayStart)
                        closed += trade.NetProfit;
                }

                _todayClosedPnL = closed;
                _todayClosedPnLHistoryCount = historyCount;
                _todayClosedPnLDay = todayStart;
            }

            net += _todayClosedPnL;

            Position[] open = Positions.FindAll(Label, SymbolName);
            for (int i = 0; i < open.Length; i++)
            {
                Position position = open[i];
                if (position.EntryTime >= todayStart)
                    net += position.NetProfit;
            }

            return net;
        }

        // Account-wide daily baseline — EA:316-347
        private void RebuildAccountDailyBaseline()
        {
            DateTime todayStart = MidnightToday();
            double todayNet = 0;
            double todayFloating = 0;

            foreach (HistoricalTrade trade in AllHistory())
                if ((trade.TradeType == TradeType.Buy || trade.TradeType == TradeType.Sell) &&
                    trade.ClosingTime >= todayStart)
                    todayNet += trade.NetProfit;

            foreach (Position position in Positions)
                if ((position.TradeType == TradeType.Buy || position.TradeType == TradeType.Sell) &&
                    position.EntryTime >= todayStart)
                    todayFloating += position.NetProfit;

            _accountDayStartEquity = Account.Equity - todayNet - todayFloating;
            _accountDayTracked = todayStart;
            _accountLossTripped = false;
        }

        // Account-wide daily loss breaker — EA:352-379
        private bool IsAccountDailyLossBreached()
        {
            if (!UseAccountDailyClose)
                return false;

            DateTime todayStart = MidnightToday();
            if (todayStart > _accountDayTracked)
            {
                _accountDayStartEquity = Account.Equity;
                _accountDayTracked = todayStart;
                _accountLossTripped = false;
            }

            if (_accountLossTripped)
                return true;

            double dayLoss = _accountDayStartEquity - Account.Equity;
            double maxLoss = _accountDayStartEquity * (AccountDailyClosePercent / 100.0);

            if (dayLoss >= maxLoss)
            {
                _accountLossTripped = true;
                return true;
            }

            return false;
        }

        // Close every position and pending order on the account — EA:384-406.
        // Note: the EA ignores symbol and magic here, so this is deliberately
        // account-wide rather than limited to this instance's positions.
        private void CloseAllOpenPositions()
        {
            foreach (Position position in Positions.ToList())
            {
                TradeResult result = ClosePosition(position);
                if (!result.IsSuccessful)
                    Log("CloseAll ClosePosition failed: code=", result.Error,
                        " symbol=", position.SymbolName, " id=", position.Id);
            }

            foreach (PendingOrder order in PendingOrders.ToList())
            {
                TradeResult result = CancelPendingOrder(order);
                if (!result.IsSuccessful)
                    Log("CloseAll CancelOrder failed: code=", result.Error,
                        " symbol=", order.SymbolName, " id=", order.Id);
            }
        }

        // Progressive risk scaling after losing streaks — EA:411-424
        private double GetAdjustedRiskMultiplier()
        {
            if (!UseConsecLossGuard)
                return 1.0;

            if (_consecLosses >= MaxConsecLosses)
                return ReducedRiskMult;

            // MaxConsecLosses / 2 is integer division in the EA.
            if (_consecLosses > MaxConsecLosses / 2)
                return 1.0 - ((_consecLosses - MaxConsecLosses / 2) * 0.1);

            return 1.0;
        }

        private bool IsCooldownActive()
        {
            return _barsSinceLastLoss < ReentryCooldownBars;
        }

        // Rebuild streak and cooldown from history — EA:672-705
        //
        // Called from OnTick after the bar-close gate, so without a cache this
        // walked the entire closed-trade history on every tick. Two fixes:
        //  - The EA's `counting` flag, once cleared, can never flip back, and
        //    nothing after it mutates state, so the scan can stop at the first
        //    non-loss rather than iterating the remainder.
        //  - The result depends only on the closed history and the current bar,
        //    so it is memoised against (History.Count, bar open time).
        private int _lossStateHistoryCount = -1;
        private DateTime _lossStateBarTime = default;
        private bool _lossStateCached;

        private void RebuildLossStateFromHistory(DateTime barTime)
        {
            int historyCount = History.Count;
            if (_lossStateCached && _lossStateHistoryCount == historyCount &&
                _lossStateBarTime == barTime)
                return;

            int consec = 0;
            DateTime lastLossClose = default;

            List<HistoricalTrade> trades = MatchingHistory();
            for (int i = 0; i < trades.Count; i++)
            {
                HistoricalTrade trade = trades[i];
                if (trade.NetProfit < 0)
                {
                    consec++;
                    if (lastLossClose == default)
                        lastLossClose = trade.ClosingTime;
                }
                else
                {
                    break;
                }
            }

            _consecLosses = consec;
            _barsSinceLastLoss = lastLossClose != default ? BarsSince(lastLossClose) : 999;

            _lossStateHistoryCount = historyCount;
            _lossStateBarTime = barTime;
            _lossStateCached = true;
        }

        // Spread in pips — EA:710-716. cTrader reports Symbol.Spread as a price
        // difference, so it is converted to points via TickSize before applying
        // the EA's SpreadPipDivisor / digit-count auto rule.
        private double GetSpreadPips()
        {
            double spreadPoints = Symbol.Spread / Symbol.TickSize;
            int divisor = SpreadPipDivisor > 0
                ? SpreadPipDivisor
                : (Symbol.Digits == 5 || Symbol.Digits == 3 ? 10 : 1);

            return Math.Round(spreadPoints / divisor, 1, MidpointRounding.AwayFromZero);
        }

        // EA:721-724. cTrader has no MODE_STOPLEVEL, so the configured distance
        // is used, defaulting to the live spread when left at zero.
        private double GetStopLevelDistance()
        {
            return BrokerMinStopDistance > 0 ? BrokerMinStopDistance : Symbol.Spread;
        }

        // EA:729-736. IsTradeContextBusy has no cTrader counterpart because
        // order calls are serialised by the platform.
        private bool IsTradePermitted()
        {
            return Symbol.IsTradingEnabled;
        }

        // EA:741-770 — send and log failures.
        //
        // The EA resends on OrderSend errors 135/136/138 (price changed, off
        // quotes, requote), which are rejections where the order provably never
        // reached the broker. cTrader has no such ErrorCode, and the nearest
        // ambiguous case (Timeout) may have already executed server-side, so a
        // blind resend risks a duplicate. No retry is performed; the failure is
        // logged and _lastBarTime is left unchanged, so the EA's own retry
        // behaviour still applies on the following tick.
        private bool SendMarketOrder(TradeType tradeType, double stopLossDistance,
                                     double takeProfitDistance, double adjustedRisk,
                                     string comment)
        {
            double volume = CalculateDynamicVolume(stopLossDistance, adjustedRisk);
            double slPips = stopLossDistance / Symbol.PipSize;
            double tpPips = takeProfitDistance / Symbol.PipSize;

            // Logged so the resulting lot size can be cross-checked against the
            // EA's journal on the same bar.
            Log(comment, " lots=", Math.Round(volume / Symbol.LotSize, 4),
                " units=", volume, " risk%=", Math.Round(adjustedRisk, 3),
                " sl=", Math.Round(slPips, 1), "p tp=", Math.Round(tpPips, 1), "p");

            TradeResult result = ExecuteMarketOrder(tradeType, SymbolName, volume, Label,
                                                    slPips, tpPips, comment);
            if (result.IsSuccessful)
                return true;

            Log(comment, " ExecuteMarketOrder failed: code=", result.Error,
                " lots=", volume, " sl=", slPips, "p tp=", tpPips, "p");
            return false;
        }

        // Log sink for the per-order lines. These fire on every fill and on
        // every rejected trailing modify, which during an optimisation run is
        // thousands of lines per pass across every pass, and the Log panel
        // update is not free. Genuinely rare events — the lot-floor risk warning
        // and the start-up banner — still Print unconditionally.
        private void Log(params object[] args)
        {
            if (VerboseLogging)
                Print(args);
        }

        // Session filter in broker time — EA:777-824
        private bool IsWithinAllowedSession()
        {
            if (!TradeLondon && !TradeNY && !TradeAsia && !TradeSydney)
                return true;

            int currentHour = ServerTime().Hour;

            bool inSydney = currentHour >= 22 || currentHour < 7;
            bool inTokyo = currentHour >= 2 && currentHour < 11;
            bool inLondon = currentHour >= 9 && currentHour < 18;
            bool inNY = currentHour >= 15;

            if (TradeSydney && inSydney) return true;
            if (TradeAsia && inTokyo) return true;
            if (TradeLondon && inLondon) return true;
            if (TradeNY && inNY) return true;

            return false;
        }

        // ── Hull moving average — EA:829-882 ──────────────────────────────

        // The EA's step 9-12 reads only closed bars 1 and 2, so the whole block
        // is constant for the lifetime of a bar. Recomputing it per tick cost
        // roughly 690 native Series indexer reads; caching it against the bar
        // open time removes that without changing any value.
        private void EnsureSignalSnapshot(DateTime barTime)
        {
            if (_sigCached && _sigBarTime == barTime)
                return;

            // EA:519-525
            double fastHmaCurrent = CalculateHma(1, FastPeriod);
            double slowHmaCurrent = CalculateHma(1, SlowPeriod);
            double fastHmaPrior = CalculateHma(2, FastPeriod);
            double slowHmaPrior = CalculateHma(2, SlowPeriod);

            // EA:528-529
            double macroSma = _smaFilter.Result.Last(1);
            double closePrice = Bars.ClosePrices.Last(1);

            // EA:532-537
            double atrValue = _atr.Result.Last(1);

            // EA:547-548
            bool bullish = fastHmaPrior <= slowHmaPrior && fastHmaCurrent > slowHmaCurrent;
            bool bearish = fastHmaPrior >= slowHmaPrior && fastHmaCurrent < slowHmaCurrent;

            _sigFastHmaCurrent = fastHmaCurrent;
            _sigSlowHmaCurrent = slowHmaCurrent;
            _sigFastHmaPrior = fastHmaPrior;
            _sigSlowHmaPrior = slowHmaPrior;
            _sigMacroSma = macroSma;
            _sigClosePrice = closePrice;
            _sigAtrValue = atrValue;
            _sigStopLossDistance = atrValue * SlAtrMultiplier;
            _sigTakeProfitDistance = atrValue * TpAtrMultiplier;
            _sigBullishCross = bullish;
            _sigBearishCross = bearish;
            _sigBarTime = barTime;
            _sigCached = true;
        }

        private double CalculateHma(int index, int period)
        {
            if (period <= 1)
                return Bars.ClosePrices[Bars.Count - 1 - index];

            int halfPeriod = period / 2;
            int sqrtPeriod = (int)Math.Round(Math.Sqrt(period), MidpointRounding.AwayFromZero);

            if (index + period + sqrtPeriod >= Bars.Count)
                return 0.0;

            double weightSum = 0;
            double valueSum = 0;

            for (int i = 0; i < sqrtPeriod; i++)
            {
                double wmaHalf = CalculateWma(index + i, halfPeriod);
                double wmaFull = CalculateWma(index + i, period);
                double hmaInput = (2.0 * wmaHalf) - wmaFull;
                double weight = sqrtPeriod - i;

                valueSum += hmaInput * weight;
                weightSum += weight;
            }

            return weightSum > 0 ? valueSum / weightSum : 0.0;
        }

        private double CalculateWma(int index, int period)
        {
            int chronologicalIndex = Bars.Count - 1 - index;
            DataSeries closes = Bars.ClosePrices;

            // period + (period-1) + ... + 1. The original accumulated this in
            // the loop; the closed form is the same exact integer for any period
            // below 2^26, so the divisor — and therefore every WMA value — is
            // bit-identical while halving the work in the inner loop.
            double weightSum = period * (period + 1) / 2.0;
            double valueSum = 0;

            for (int i = 0; i < period; i++)
                valueSum += closes[chronologicalIndex - i] * (period - i);

            return weightSum > 0 ? valueSum / weightSum : 0.0;
        }

        // ATR trailing stop — EA:887-934
        private void ApplyAtrTrailingStop()
        {
            if (!UseTrailingStop)
                return;

            double atr = _atr.Result.Last(1);
            if (atr <= 0)
                return;

            double activationDistance = atr * TrailActivation;
            double trailingDistance = atr * TrailCushion;

            // Positions.FindAll(Label, SymbolName) is a native indexed lookup
            // returning only this instance's positions. The previous
            // Positions.Where(...).ToList() allocated a lazy iterator, a
            // List and a closure on every tick.
            Position[] open = Positions.FindAll(Label, SymbolName);
            if (open.Length == 0)
                return;

            // Read once per tick rather than twice per position. Same tick,
            // same values, so the result is unchanged.
            double bid = Symbol.Bid;
            double ask = Symbol.Ask;

            for (int i = 0; i < open.Length; i++)
            {
                Position position = open[i];

                if (position.TradeType == TradeType.Buy)
                {
                    if (bid - position.EntryPrice <= activationDistance)
                        continue;

                    double targetStop = Math.Round(bid - trailingDistance, Symbol.Digits,
                                                    MidpointRounding.AwayFromZero);
                    if (position.StopLoss != null && targetStop <= position.StopLoss.Value)
                        continue;

                    TradeResult result = ModifyPosition(position, targetStop, position.TakeProfit,
                                                         ProtectionType.Absolute);
                    if (!result.IsSuccessful)
                        Log("Trail ModifyPosition failed: code=", result.Error,
                            " id=", position.Id, " sl=", targetStop);
                }
                else if (position.TradeType == TradeType.Sell)
                {
                    if (position.EntryPrice - ask <= activationDistance)
                        continue;

                    double targetStop = Math.Round(ask + trailingDistance, Symbol.Digits,
                                                    MidpointRounding.AwayFromZero);
                    if (position.StopLoss != null && targetStop >= position.StopLoss.Value)
                        continue;

                    TradeResult result = ModifyPosition(position, targetStop, position.TakeProfit,
                                                         ProtectionType.Absolute);
                    if (!result.IsSuccessful)
                        Log("Trail ModifyPosition failed: code=", result.Error,
                            " id=", position.Id, " sl=", targetStop);
                }
            }
        }

        // Dynamic lot sizing — EA:939-965.
        //
        // MT4's MODE_TICKVALUE is a per-lot figure and cTrader does not
        // document whether Symbol.TickValue is per-unit or per-lot, so the
        // risk-to-volume conversion is delegated to cTrader's documented
        // VolumeForFixedRisk. MT4's rounding chain is then applied in LOTS,
        // because rounding in units would differ by up to one lot step.
        private double CalculateDynamicVolume(double stopLossPriceDelta, double riskPercent)
        {
            if (!AutoMoneyManagement)
                return LotsToUnits(LotSize);

            double riskAmount = Account.Balance * (riskPercent / 100.0);
            if (stopLossPriceDelta <= 0 || Symbol.PipSize <= 0)
                return LotsToUnits(LotSize);

            double stopLossPips = stopLossPriceDelta / Symbol.PipSize;
            double units = Symbol.VolumeForFixedRisk(riskAmount, stopLossPips);
            if (units <= 0)
                return LotsToUnits(LotSize);

            double lotSize = Symbol.LotSize;
            double lotStep = Symbol.VolumeInUnitsStep / lotSize;
            double minLot = Symbol.VolumeInUnitsMin / lotSize;
            double maxLot = Symbol.VolumeInUnitsMax / lotSize;

            if (lotStep <= 0)
                return Symbol.NormalizeVolumeInUnits(units, RoundingMode.Down);

            double lots = units / lotSize;
            lots = Math.Round(lots / lotStep, MidpointRounding.AwayFromZero) * lotStep;

            double rawLots = lots;
            if (lots < minLot)
                lots = minLot;
            if (lots > maxLot)
                lots = maxLot;

            // EA:964 — NormalizeDouble(calculatedLots, 2), which can move the
            // value off the lot step and below the broker minimum.
            lots = Math.Round(lots, 2, MidpointRounding.AwayFromZero);

            if (lots < minLot)
                Print("Lot floor: risk lot size ", Math.Round(rawLots, 4),
                      " raised to broker minimum ", lots,
                      " — actual risk exceeds the configured percentage");

            return Symbol.NormalizeVolumeInUnits(LotsToUnits(lots), RoundingMode.Down);
        }

        private double LotsToUnits(double lots)
        {
            return Symbol.NormalizeVolumeInUnits(Symbol.QuantityToVolumeInUnits(lots),
                                                 RoundingMode.Down);
        }

        private int CountOpenPositions(TradeType tradeType)
        {
            return Positions.FindAll(Label, SymbolName, tradeType).Length;
        }

        // EA:987-1009 — close and track each close (Friday path only).
        private void CloseOpenPositions(TradeType tradeType)
        {
            Position[] open = Positions.FindAll(Label, SymbolName);
            for (int i = 0; i < open.Length; i++)
            {
                Position position = open[i];
                if (position.TradeType != tradeType)
                    continue;

                TradeResult result = ClosePosition(position);
                if (result.IsSuccessful)
                    TrackOrderClose(position.Id);
                else
                    Log("OrderClose failed: code=", result.Error,
                        " id=", position.Id, " symbol=", position.SymbolName);
            }
        }

        // EA:629-652 — the EA re-selects the closed order from history to read
        // its net result; FindByPositionId is the cTrader equivalent.
        private void TrackOrderClose(int positionId)
        {
            HistoricalTrade trade = History.FindByPositionId(positionId).FirstOrDefault();
            if (trade == null)
                return;

            if (trade.SymbolName != SymbolName || trade.Label != Label)
                return;

            if (trade.NetProfit < 0)
            {
                _consecLosses++;
                _barsSinceLastLoss = 0;
            }
            else
            {
                _consecLosses = 0;
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────

        // Bars elapsed since a point in time, 0 = current bar — EA:657-665.
        // Equivalent to iBarShift(..., exact=false): the shift of the newest bar
        // whose open time is at or before the target.
        private int BarsSince(DateTime targetTime)
        {
            if (targetTime == default)
                return 999;

            int low = 0;
            int high = Bars.Count - 1;

            if (Bars.OpenTimes[low] > targetTime)
                return 999;

            while (low < high)
            {
                int mid = low + ((high - low + 1) / 2);
                if (Bars.OpenTimes[mid] <= targetTime)
                    low = mid;
                else
                    high = mid - 1;
            }

            return Bars.Count - 1 - low;
        }

        private DateTime MidnightToday()
        {
            DateTime now = ServerTime();
            if (now.Date != _midnight)
            {
                _midnight = now.Date;
            }
            return _midnight;
        }

        // Server.Time is a native read and .Date allocates. OnTick consults it
        // up to four times (Friday gate, both daily breakers, the session
        // guard), and the daily breakers are consulted on every tick, so the
        // read is memoised against a tick id. Rollovers invalidate naturally
        // because a new tick re-reads Server.Time.
        private long _tickId;
        private long _serverTimeTickId = -1;
        private DateTime _serverTime;
        private DateTime _midnight;

        private DateTime ServerTime()
        {
            if (_serverTimeTickId == _tickId)
                return _serverTime;

            _serverTimeTickId = _tickId;
            _serverTime = Server.Time;
            return _serverTime;
        }

        // Closed trades for this instance, newest first. Sorted explicitly
        // because the EA relies on newest-to-oldest traversal.
        //
        // Memoised against History.Count. History is append-only, so an
        // unchanged count guarantees an unchanged list, and it is rebuilt only
        // when a trade actually closes. The consumers that used to walk this on
        // every tick (GetTodayEaPnL, RebuildLossStateFromHistory) now cache
        // above this level as well.
        private List<HistoricalTrade> _matchingHistoryCache;
        private int _matchingHistoryCacheCount = -1;

        private List<HistoricalTrade> MatchingHistory()
        {
            if (_matchingHistoryCache != null && _matchingHistoryCacheCount == History.Count)
                return _matchingHistoryCache;

            HistoricalTrade[] buys = History.FindAll(Label, SymbolName, TradeType.Buy);
            HistoricalTrade[] sells = History.FindAll(Label, SymbolName, TradeType.Sell);

            List<HistoricalTrade> trades = new List<HistoricalTrade>(buys.Length + sells.Length);

            // History.FindAll returns each side newest-first, so the combined
            // newest-first order is a linear merge of two sorted runs. The
            // sortedness of the inputs is verified rather than assumed, because
            // a wrong order here would silently corrupt the loss streak.
            if (IsNewestFirst(buys) && IsNewestFirst(sells))
            {
                int b = 0;
                int s = 0;
                while (b < buys.Length && s < sells.Length)
                {
                    if (buys[b].ClosingTime >= sells[s].ClosingTime)
                        trades.Add(buys[b++]);
                    else
                        trades.Add(sells[s++]);
                }

                while (b < buys.Length)
                    trades.Add(buys[b++]);
                while (s < sells.Length)
                    trades.Add(sells[s++]);
            }
            else
            {
                trades.AddRange(buys);
                trades.AddRange(sells);
                trades.Sort(ByClosingTimeDescending);
            }

            _matchingHistoryCache = trades;
            _matchingHistoryCacheCount = History.Count;
            return trades;
        }

        private static readonly Comparison<HistoricalTrade> ByClosingTimeDescending =
            (a, b) => b.ClosingTime.CompareTo(a.ClosingTime);

        private static bool IsNewestFirst(HistoricalTrade[] trades)
        {
            for (int i = 1; i < trades.Length; i++)
            {
                if (trades[i].ClosingTime > trades[i - 1].ClosingTime)
                    return false;
            }
            return true;
        }

        // Every closed market trade on the account, for the account-wide
        // daily baseline, which the EA scopes to all symbols and magics.
        // History offers no unfiltered market-trade lookup, so the collection
        // is walked directly through its documented Count/indexer pair.
        private List<HistoricalTrade> AllHistory()
        {
            List<HistoricalTrade> trades = new List<HistoricalTrade>();

            for (int i = 0; i < History.Count; i++)
            {
                HistoricalTrade trade = History[i];
                if (trade.TradeType == TradeType.Buy || trade.TradeType == TradeType.Sell)
                    trades.Add(trade);
            }

            // The sort is retained even though the sole consumer only sums
            // NetProfit: it is called once from OnStart over an empty history
            // in a backtest, so the cost is irrelevant, whereas dropping it
            // would reorder a floating-point accumulation.
            trades.Sort(ByClosingTimeDescending);
            return trades;
        }
    }
}
