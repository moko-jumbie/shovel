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

            Print("HMA Crossover port of HMA_Crossover_Smart_Run4_FIXED started on ",
                  SymbolName, " ", TimeFrame, " label='", Label, "'");
        }

        protected override void OnTick()
        {
            // 1. Dynamic trailing stop (every tick) — EA:440
            ApplyAtrTrailingStop();

            // 2. Friday protection — EA:443-448
            if (FridayAutoClose && Server.Time.DayOfWeek == DayOfWeek.Friday &&
                Server.Time.Hour >= FridayCloseHour)
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
            RebuildLossStateFromHistory();

            // 6. Spread filter — EA:496-501
            if (UseSpreadFilter && GetSpreadPips() > MaxSpreadPips)
                return;

            // 7. Session guardrail — EA:504-508
            if ((TradeSydney || TradeLondon || TradeNY || TradeAsia) && !IsWithinAllowedSession())
                return;

            // 8. ADX filter — EA:511-516 (period 14 is hardcoded in the EA)
            if (UseAdxFilter && _adx.ADX.Last(1) < AdxThreshold)
                return;

            // 9. HMA values — EA:519-525
            double fastHmaCurrent = CalculateHma(1, FastPeriod);
            double slowHmaCurrent = CalculateHma(1, SlowPeriod);
            double fastHmaPrior = CalculateHma(2, FastPeriod);
            double slowHmaPrior = CalculateHma(2, SlowPeriod);

            if (fastHmaCurrent == 0 || slowHmaCurrent == 0 || fastHmaPrior == 0 || slowHmaPrior == 0)
                return;

            // 10. SMA macro filter — EA:528-529
            double macroSma = _smaFilter.Result.Last(1);
            double closePrice = Bars.ClosePrices.Last(1);

            // 11. ATR volatility — EA:532-537
            double atrValue = _atr.Result.Last(1);
            if (atrValue <= 0)
                return;

            double stopLossDistance = atrValue * SlAtrMultiplier;
            double takeProfitDistance = atrValue * TpAtrMultiplier;

            // FIX-11: respect the broker minimum stop distance — EA:540-544
            double minStopDist = GetStopLevelDistance();
            if (stopLossDistance < minStopDist)
                stopLossDistance = minStopDist;
            if (takeProfitDistance < minStopDist)
                takeProfitDistance = minStopDist;

            // 12. Crossover evaluation — EA:547-548
            bool isBullishCross = fastHmaPrior <= slowHmaPrior && fastHmaCurrent > slowHmaCurrent;
            bool isBearishCross = fastHmaPrior >= slowHmaPrior && fastHmaCurrent < slowHmaCurrent;

            // 13. Re-entry cooldown — EA:551-552
            if (IsCooldownActive() && (isBullishCross || isBearishCross))
                return;

            // 14. Execution core — EA:555-615
            if (isBullishCross)
            {
                if (!UseSmaFilter || closePrice > macroSma)
                {
                    // FIX-8: the opposite side is NOT force-closed.
                    if (CountOpenPositions(TradeType.Buy) == 0)
                    {
                        if (!IsTradePermitted())
                            return;

                        if (SendMarketOrder(TradeType.Buy, stopLossDistance, takeProfitDistance,
                                adjustedRisk: RiskPercentPerTrade * GetAdjustedRiskMultiplier(),
                                comment: "HMA Cross Buy"))
                        {
                            _lastBarTime = currentBarTime;
                        }
                    }
                }
            }
            else if (isBearishCross)
            {
                if (!UseSmaFilter || closePrice < macroSma)
                {
                    if (CountOpenPositions(TradeType.Sell) == 0)
                    {
                        if (!IsTradePermitted())
                            return;

                        if (SendMarketOrder(TradeType.Sell, stopLossDistance, takeProfitDistance,
                                adjustedRisk: RiskPercentPerTrade * GetAdjustedRiskMultiplier(),
                                comment: "HMA Cross Sell"))
                        {
                            _lastBarTime = currentBarTime;
                        }
                    }
                }
            }
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
        private double GetTodayEaPnL()
        {
            DateTime todayStart = MidnightToday();
            double todayNet = 0;

            foreach (HistoricalTrade trade in MatchingHistory())
                if (trade.ClosingTime >= todayStart)
                    todayNet += trade.NetProfit;

            foreach (Position position in Positions)
                if (position.SymbolName == SymbolName && position.Label == Label &&
                    (position.TradeType == TradeType.Buy || position.TradeType == TradeType.Sell) &&
                    position.EntryTime >= todayStart)
                    todayNet += position.NetProfit;

            return todayNet;
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
                    Print("CloseAll ClosePosition failed: code=", result.Error,
                          " symbol=", position.SymbolName, " id=", position.Id);
            }

            foreach (PendingOrder order in PendingOrders.ToList())
            {
                TradeResult result = CancelPendingOrder(order);
                if (!result.IsSuccessful)
                    Print("CloseAll CancelOrder failed: code=", result.Error,
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
        private void RebuildLossStateFromHistory()
        {
            int consec = 0;
            DateTime lastLossClose = default;
            bool counting = true;

            foreach (HistoricalTrade trade in MatchingHistory())
            {
                if (!counting)
                    continue;

                if (trade.NetProfit < 0)
                {
                    consec++;
                    if (lastLossClose == default)
                        lastLossClose = trade.ClosingTime;
                }
                else
                {
                    counting = false;
                }
            }

            _consecLosses = consec;
            _barsSinceLastLoss = lastLossClose != default ? BarsSince(lastLossClose) : 999;
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
            Print(comment, " lots=", Math.Round(volume / Symbol.LotSize, 4),
                  " units=", volume, " risk%=", Math.Round(adjustedRisk, 3),
                  " sl=", Math.Round(slPips, 1), "p tp=", Math.Round(tpPips, 1), "p");

            TradeResult result = ExecuteMarketOrder(tradeType, SymbolName, volume, Label,
                                                    slPips, tpPips, comment);
            if (result.IsSuccessful)
                return true;

            Print(comment, " ExecuteMarketOrder failed: code=", result.Error,
                  " lots=", volume, " sl=", slPips, "p tp=", tpPips, "p");
            return false;
        }

        // Session filter in broker time — EA:777-824
        private bool IsWithinAllowedSession()
        {
            if (!TradeLondon && !TradeNY && !TradeAsia && !TradeSydney)
                return true;

            int currentHour = Server.Time.Hour;

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
            double weightSum = 0;
            double valueSum = 0;

            for (int i = 0; i < period; i++)
            {
                double weight = period - i;
                valueSum += Bars.ClosePrices[chronologicalIndex - i] * weight;
                weightSum += weight;
            }

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

            foreach (Position position in Positions
                         .Where(p => p.SymbolName == SymbolName && p.Label == Label)
                         .ToList())
            {
                if (position.TradeType == TradeType.Buy)
                {
                    if (Symbol.Bid - position.EntryPrice <= activationDistance)
                        continue;

                    double targetStop = Math.Round(Symbol.Bid - trailingDistance, Symbol.Digits,
                                                    MidpointRounding.AwayFromZero);
                    if (position.StopLoss != null && targetStop <= position.StopLoss.Value)
                        continue;

                    TradeResult result = ModifyPosition(position, targetStop, position.TakeProfit,
                                                         ProtectionType.Absolute);
                    if (!result.IsSuccessful)
                        Print("Trail ModifyPosition failed: code=", result.Error,
                              " id=", position.Id, " sl=", targetStop);
                }
                else if (position.TradeType == TradeType.Sell)
                {
                    if (position.EntryPrice - Symbol.Ask <= activationDistance)
                        continue;

                    double targetStop = Math.Round(Symbol.Ask + trailingDistance, Symbol.Digits,
                                                    MidpointRounding.AwayFromZero);
                    if (position.StopLoss != null && targetStop >= position.StopLoss.Value)
                        continue;

                    TradeResult result = ModifyPosition(position, targetStop, position.TakeProfit,
                                                         ProtectionType.Absolute);
                    if (!result.IsSuccessful)
                        Print("Trail ModifyPosition failed: code=", result.Error,
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
            return Positions.Count(p => p.SymbolName == SymbolName && p.Label == Label &&
                                        p.TradeType == tradeType);
        }

        // EA:987-1009 — close and track each close (Friday path only).
        private void CloseOpenPositions(TradeType tradeType)
        {
            foreach (Position position in Positions
                         .Where(p => p.SymbolName == SymbolName && p.Label == Label &&
                                     p.TradeType == tradeType)
                         .ToList())
            {
                TradeResult result = ClosePosition(position);
                if (result.IsSuccessful)
                    TrackOrderClose(position.Id);
                else
                    Print("OrderClose failed: code=", result.Error,
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
            return Server.Time.Date;
        }

        // Closed trades for this instance, newest first. Sorted explicitly
        // because the EA relies on newest-to-oldest traversal.
        //
        // GetTodayEaPnL runs this on every tick (EA:282-311), so the result is
        // memoised against History.Count. History is append-only, so an
        // unchanged count guarantees an unchanged result, and the value stays
        // bit-identical to the uncached scan.
        private List<HistoricalTrade> _matchingHistoryCache;
        private int _matchingHistoryCacheCount = -1;

        private List<HistoricalTrade> MatchingHistory()
        {
            if (_matchingHistoryCache != null && _matchingHistoryCacheCount == History.Count)
                return _matchingHistoryCache;

            HistoricalTrade[] buys = History.FindAll(Label, SymbolName, TradeType.Buy);
            HistoricalTrade[] sells = History.FindAll(Label, SymbolName, TradeType.Sell);

            List<HistoricalTrade> trades = new List<HistoricalTrade>(buys);
            trades.AddRange(sells);
            trades.Sort((a, b) => b.ClosingTime.CompareTo(a.ClosingTime));

            _matchingHistoryCache = trades;
            _matchingHistoryCacheCount = History.Count;
            return trades;
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

            trades.Sort((a, b) => b.ClosingTime.CompareTo(a.ClosingTime));
            return trades;
        }
    }
}
