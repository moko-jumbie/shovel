using System;
using System.Collections.Generic;
using cAlgo.API;

namespace cAlgo.Robots
{
    // The bridge between the trading logic and the dashboard. Split out of the
    // main file so the 1150-line strategy stays readable, and declared partial so
    // it can read the private state the robot already maintains.
    //
    // Two rules govern everything here:
    //
    //  1. Nothing in this file may change what the robot trades. The breaker
    //     helpers the trading path uses (IsDailyLossLimitBreached,
    //     IsAccountDailyLossBreached) both roll the day over and latch
    //     _accountLossTripped, so calling them from a timer that fires between
    //     ticks could halt the robot before its own OnTick ever asked it to.
    //     The breach conditions are therefore recomputed here from the same
    //     state, read-only.
    //
    //  2. Nothing in this file may do work proportional to the account's
    //     lifetime. The EA's ScanEAPerformance walked the entire order history
    //     every second; here the walk is memoised against History.Count, so it
    //     only re-runs when a trade actually closes.
    public partial class hmax_gold
    {
        // Memoised performance scan. Rebuilt only when history grows or the day
        // rolls over, matching the caching idiom already used for MatchingHistory.
        private int _hudStatsHistoryCount = -1;
        private DateTime _hudStatsDay = default;
        private int _hudTotalTrades;
        private int _hudWins;
        private int _hudLosses;
        private double _hudGrossProfit;
        private double _hudGrossLoss;
        private double _hudNetPnl;
        private int _hudTodayTrades;
        private double _hudTodayPnl;

        /// <summary>Fills a snapshot with the current state of the world. Allocates
        /// nothing — the controller reuses one instance across refreshes.</summary>
        internal void FillHudSnapshot(HudSnapshot s)
        {
            DateTime now = ServerTime();
            DateTime todayStart = MidnightToday();
            int digits = Symbol.Digits;

            s.Symbol = SymbolName;
            s.TimeFrameLabel = TimeFrameLabel(TimeFrame);

            FillPerformance(s, todayStart);
            FillPositions(s);
            FillStrategy(s);
            FillSession(s, now);
            FillBreakers(s);
            FillAccount(s, now);

            s.Digits = digits;
            s.Clock = now.ToString("yyyy-MM-dd  HH:mm:ss") + "  BROKER";
            s.Level = ResolveLevel(s);
        }

        // ── Performance ────────────────────────────────────────────────────

        private void FillPerformance(HudSnapshot s, DateTime todayStart)
        {
            int historyCount = History.Count;
            if (_hudStatsHistoryCount != historyCount || _hudStatsDay != todayStart)
                RebuildHudStats(historyCount, todayStart);

            double winRate = _hudTotalTrades > 0
                ? _hudWins * 100.0 / _hudTotalTrades
                : 0;

            s.TotalTrades = _hudTotalTrades;
            s.TotalWins = _hudWins;
            s.TotalLosses = _hudLosses;
            s.GrossProfit = _hudGrossProfit;
            s.GrossLoss = _hudGrossLoss;
            s.NetPnl = _hudNetPnl;
            s.WinRatePct = winRate;
            s.ProfitFactor = _hudGrossLoss > 0 ? _hudGrossProfit / _hudGrossLoss : 0;
            s.TodayTrades = _hudTodayTrades;
            s.TodayPnl = _hudTodayPnl;

            // Trade tape, oldest first so the newest bar lands on the right.
            // MatchingHistory is newest-first, so the walk is reversed.
            List<HistoricalTrade> trades = MatchingHistory();
            s.TapeCount = trades.Count < HudSnapshot.TapeSlots
                ? trades.Count
                : HudSnapshot.TapeSlots;

            for (int i = 0; i < s.TapeCount; i++)
                s.Tape[i] = trades[s.TapeCount - 1 - i].NetProfit;
        }

        private void RebuildHudStats(int historyCount, DateTime todayStart)
        {
            int total = 0;
            int wins = 0;
            int losses = 0;
            double grossProfit = 0;
            double grossLoss = 0;
            double net = 0;
            int todayTrades = 0;
            double todayPnl = 0;

            // The EA accumulated in newest-first order, and the order of a
            // floating-point sum is not associative, so the traversal direction is
            // preserved rather than replaced with a forward walk.
            List<HistoricalTrade> trades = MatchingHistory();
            for (int i = 0; i < trades.Count; i++)
            {
                double pnl = trades[i].NetProfit;
                total++;
                net += pnl;

                if (pnl >= 0)
                {
                    wins++;
                    grossProfit += pnl;
                }
                else
                {
                    losses++;
                    grossLoss -= pnl;
                }

                if (trades[i].ClosingTime >= todayStart)
                {
                    todayTrades++;
                    todayPnl += pnl;
                }
            }

            _hudTotalTrades = total;
            _hudWins = wins;
            _hudLosses = losses;
            _hudGrossProfit = grossProfit;
            _hudGrossLoss = grossLoss;
            _hudNetPnl = net;
            _hudTodayTrades = todayTrades;
            _hudTodayPnl = todayPnl;
            _hudStatsHistoryCount = historyCount;
            _hudStatsDay = todayStart;
        }

        // ── Positions ──────────────────────────────────────────────────────

        private void FillPositions(HudSnapshot s)
        {
            // The snapshot instance is reused, so the slot counter has to be
            // cleared here. Without this it only ever grows, and a position that
            // closes leaves a ghost row showing its last entry price and P&L.
            s.PositionShown = 0;

            Position[] open = Positions.FindAll(Label, SymbolName);
            double floating = 0;
            double lots = 0;
            int buys = 0;

            for (int i = 0; i < open.Length; i++)
            {
                Position position = open[i];
                floating += position.NetProfit;
                lots += position.VolumeInUnits / Symbol.LotSize;
                if (position.TradeType == TradeType.Buy)
                    buys++;

                int slot = s.PositionShown;
                if (slot >= HudSnapshot.PositionSlots)
                    continue;

                s.PositionIsBuy[slot] = position.TradeType == TradeType.Buy;
                s.PositionEntry[slot] = position.EntryPrice;
                s.PositionPnl[slot] = position.NetProfit;
                s.PositionShown++;
            }

            s.OpenCount = open.Length;
            s.Buys = buys;
            s.Sells = open.Length - buys;
            s.FloatingPnl = floating;
            s.OpenLots = lots;
        }

        // ── Strategy ───────────────────────────────────────────────────────

        private void FillStrategy(HudSnapshot s)
        {
            // Reuse the trading path's memoised values when they belong to the
            // current bar. They are a pure function of closed bars 1 and 2, so
            // computing them again would produce identical numbers — reading the
            // cache instead just avoids duplicating the HMA work.
            bool haveSnapshot = _sigCached && _sigBarTime == Bars.Last(0).OpenTime;

            double fast = haveSnapshot ? _sigFastHmaCurrent : CalculateHma(1, FastPeriod);
            double slow = haveSnapshot ? _sigSlowHmaCurrent : CalculateHma(1, SlowPeriod);
            double fastPrior = haveSnapshot ? _sigFastHmaPrior : CalculateHma(2, FastPeriod);
            double slowPrior = haveSnapshot ? _sigSlowHmaPrior : CalculateHma(2, SlowPeriod);

            double atr = _atr.Result.Last(1);
            double close = Bars.ClosePrices.Last(1);

            s.FastHma = fast;
            s.SlowHma = slow;
            s.Atr = atr;
            s.ClosePrice = close;
            s.SlMultiplier = SlAtrMultiplier;
            s.TpMultiplier = TpAtrMultiplier;
            s.ConsecLosses = _consecLosses;
            s.MaxConsecLosses = MaxConsecLosses;
            s.RiskMultiplier = GetAdjustedRiskMultiplier();
            s.Adx = UseAdxFilter ? _adx.ADX.Last(1) : 0;

            // (fast - slow) / ATR: a scale-free measure of how far the cross is
            // from happening, which the two absolute HMA prices cannot express.
            s.HmaSpreadRatio = atr > 0 ? (fast - slow) / atr : 0;

            s.MacroEnabled = UseSmaFilter;
            s.MacroAbove = close > _smaFilter.Result.Last(1);
            s.MacroSma = _smaFilter.Result.Last(1);

            // Same precedence as UpdateStrategySnapshot (MT4EAv4.txt:1024-1043).
            if (fast > slow)
                s.SignalMode = fastPrior <= slowPrior ? 0 : 1;
            else if (fast < slow)
                s.SignalMode = fastPrior >= slowPrior ? 2 : 3;
            else
                s.SignalMode = 4;
        }

        // ── Session ────────────────────────────────────────────────────────

        private void FillSession(HudSnapshot s, DateTime now)
        {
            int hour = now.Hour;
            double spread = GetSpreadPips();

            s.Hour = hour;
            s.SpreadPips = spread;
            s.MaxSpreadPips = MaxSpreadPips;
            s.UseSpreadFilter = UseSpreadFilter;
            s.UseSmaFilter = UseSmaFilter;
            s.UseAdxFilter = UseAdxFilter;
            s.UseTrailingStop = UseTrailingStop;
            s.TradeSydney = TradeSydney;
            s.TradeTokyo = TradeAsia;
            s.TradeLondon = TradeLondon;
            s.TradeNewYork = TradeNY;
            s.FridayGuard = FridayAutoClose;
            s.FridayCloseHour = FridayCloseHour;

            int mask = 0;
            if (TradeSydney) mask |= 1;
            if (TradeAsia) mask |= 2;
            if (TradeNY) mask |= 4;
            if (TradeLondon) mask |= 8;
            s.SessionMask = mask;

            s.SessionEnabled = (TradeSydney || TradeAsia || TradeNY || TradeLondon);
            s.TradingActive = IsWithinAllowedSession();
            s.SessionLabel = BuildSessionLabel(hour);

            // Mirrors IsCooldownActive: the guard is active while bars since the
            // last loss are below the threshold. Clamped, because with a zero
            // threshold the raw difference goes negative.
            s.ReentryCooldownBars = ReentryCooldownBars;
            s.CooldownBars = ReentryCooldownBars > 0
                ? Math.Max(ReentryCooldownBars - _barsSinceLastLoss, 0)
                : 0;
        }

        /// <summary>Ported from GetSessionLabel (MT4EAv4.txt:1162). The windows
        /// match IsWithinAllowedSession so the label and the filter cannot disagree.</summary>
        private static string BuildSessionLabel(int hour)
        {
            bool inSydney = hour < 9;
            bool inTokyo = hour >= 3 && hour < 12;
            bool inLondon = hour >= 10 && hour < 19;
            bool inNY = hour >= 15;

            if (inLondon && inNY) return "NY / LONDON OVERLAP";
            if (inTokyo && inLondon) return "TOKYO / LONDON OVERLAP";
            if (inSydney && inTokyo) return "SYDNEY / TOKYO OVERLAP";
            if (inSydney) return "SYDNEY SESSION";
            if (inTokyo) return "TOKYO SESSION";
            if (inLondon) return "LONDON SESSION";
            if (inNY) return "NEW YORK SESSION";
            return "OFF-HOURS";
        }

        // ── Breakers ───────────────────────────────────────────────────────

        // Read-only restatements of IsDailyLossLimitBreached (:439) and
        // IsAccountDailyLossBreached (:535) with the rollover and the latch write
        // removed. The day rollover is deliberately absent: the trading path
        // performs it on its own tick, and duplicating it here would reset
        // _dayStartBalance a second time if the two ever interleaved.
        private void FillBreakers(HudSnapshot s)
        {
            double todayPnl = GetTodayEaPnL();

            double dailyMaxLoss = UseDailyLossLimit
                ? _dayStartBalance * (MaxDailyLossPercent / 100.0)
                : 0;
            double dayLoss = -todayPnl;

            s.DailyMaxLoss = dailyMaxLoss;
            s.DailyBreached = UseDailyLossLimit && dayLoss >= dailyMaxLoss;
            s.DailyPctUsed = dailyMaxLoss > 0 ? dayLoss / dailyMaxLoss * 100.0 : 0;
            s.DailyRemaining = Math.Max(dailyMaxLoss - dayLoss, 0);

            double acctMaxLoss = UseAccountDailyClose
                ? _accountDayStartEquity * (AccountDailyClosePercent / 100.0)
                : 0;
            double acctDayLoss = _accountDayStartEquity - Account.Equity;

            s.AcctEnabled = UseAccountDailyClose;
            s.AcctMaxLoss = acctMaxLoss;
            s.AcctRemaining = Math.Max(acctMaxLoss - acctDayLoss, 0);
            s.AcctBreached = UseAccountDailyClose &&
                             (_accountLossTripped || acctDayLoss >= acctMaxLoss);

            s.StreakTriggered = UseConsecLossGuard && _consecLosses >= MaxConsecLosses;
            s.SpreadTriggered = UseSpreadFilter && GetSpreadPips() > MaxSpreadPips;
            s.SessionBlocked = s.SessionEnabled && !IsWithinAllowedSession();

            s.BreakerActive = s.DailyBreached || s.AcctBreached || s.StreakTriggered ||
                              s.SpreadTriggered || s.SessionBlocked;
        }

        private static int ResolveLevel(HudSnapshot s)
        {
            if (s.AcctBreached)
                return 2;

            if (s.DailyBreached || s.StreakTriggered || s.SpreadTriggered || s.SessionBlocked)
                return 1;

            return 0;
        }

        // ── Account ────────────────────────────────────────────────────────

        private void FillAccount(HudSnapshot s, DateTime now)
        {
            s.Balance = Account.Balance;
            s.Equity = Account.Equity;

            // Prefer the account-wide baseline so the figure matches the account
            // guard, and fall back to the EA baseline if the account one has not
            // been seeded yet.
            double baseline = _accountDayStartEquity > 0 ? _accountDayStartEquity : _dayStartBalance;
            s.DayDrawdownPct = baseline > 0
                ? Math.Max((baseline - Account.Equity) / baseline * 100.0, 0)
                : 0;

            double effectiveRisk = RiskPercentPerTrade * GetAdjustedRiskMultiplier();
            s.RiskPerTradePct = effectiveRisk;
            s.NextLots = ProjectLots(effectiveRisk);
        }

        /// <summary>Volume the next entry would use at the current ATR stop, for
        /// display only.
        ///
        /// CalculateDynamicVolume is not reused because it can Print a lot-floor
        /// warning, and a dashboard that writes to the log twice a second is worse
        /// than one that does not.</summary>
        private double ProjectLots(double riskPercent)
        {
            if (!AutoMoneyManagement || riskPercent <= 0)
                return LotSize;

            double atr = _atr.Result.Last(1);
            if (atr <= 0 || Symbol.PipSize <= 0)
                return LotSize;

            double riskAmount = Account.Balance * (riskPercent / 100.0);
            double stopLossPips = atr * SlAtrMultiplier / Symbol.PipSize;
            double units = Symbol.VolumeForFixedRisk(riskAmount, stopLossPips);
            if (units <= 0)
                return LotSize;

            return units / Symbol.LotSize;
        }

        // TimeFrame is a class, not an enum, so it cannot be switched on.
        // It carries the platform's own short name, which removes the need for a
        // hand-maintained mapping that could drift from cTrader's own table.
        private static string TimeFrameLabel(TimeFrame timeFrame)
        {
            string shortName = timeFrame.ShortName;
            return string.IsNullOrEmpty(shortName) ? timeFrame.Name : shortName;
        }
    }
}
