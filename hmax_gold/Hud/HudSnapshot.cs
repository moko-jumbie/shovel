using System;

namespace cAlgo.Robots
{
    // One frame of HUD data, captured atomically by the robot and rendered by a
    // tree of controls that holds no reference to the trading logic.
    //
    // Deliberately a reusable mutable DTO rather than a fresh immutable record:
    // the timer refreshes several times a second, and allocating a large object
    // graph every time is pure garbage-collector pressure for no benefit. The
    // controller owns exactly one instance and refills it in place.
    //
    // The arrays are fixed-size and allocated once, for the same reason.
    internal sealed class HudSnapshot
    {
        public const int TapeSlots = 20;
        public const int PositionSlots = 3;

        // ── Header ───────────────────────────────────────────────────────
        public string Symbol = "";
        public string TimeFrameLabel = "";
        public string Clock = "";
        public int Level;

        // ── EA performance ───────────────────────────────────────────────
        public int TotalTrades;
        public int TotalWins;
        public int TotalLosses;
        public double GrossProfit;
        public double GrossLoss;
        public double NetPnl;
        public double WinRatePct;
        public double ProfitFactor;
        public int TodayTrades;
        public double TodayPnl;

        /// <summary>Signed net P&amp;L of the most recent closed trades, oldest first.</summary>
        public readonly double[] Tape = new double[TapeSlots];

        public int TapeCount;

        // ── Open positions ───────────────────────────────────────────────
        public int OpenCount;
        public int Buys;
        public int Sells;
        public double FloatingPnl;
        public double OpenLots;

        public readonly bool[] PositionIsBuy = new bool[PositionSlots];
        public readonly double[] PositionPnl = new double[PositionSlots];
        public readonly double[] PositionEntry = new double[PositionSlots];
        public int PositionShown;

        // ── Strategy engine ──────────────────────────────────────────────
        public double FastHma;
        public double SlowHma;
        public double Atr;
        public double SlMultiplier;
        public double TpMultiplier;
        public double ClosePrice;

        /// <summary>0 bull cross, 1 bull, 2 bear cross, 3 bear, 4 neutral.</summary>
        public int SignalMode;

        public bool MacroEnabled;
        public bool MacroAbove;
        public double MacroSma;

        /// <summary>(fast - slow) / ATR. Signed, so it reads left/right of centre.</summary>
        public double HmaSpreadRatio;

        public int ConsecLosses;
        public int MaxConsecLosses;
        public double RiskMultiplier;

        // ── Session and protection ───────────────────────────────────────
        public int Hour;
        public string SessionLabel = "";
        public bool SessionEnabled;
        public bool TradingActive;
        public bool TradeSydney;
        public bool TradeTokyo;
        public bool TradeLondon;
        public bool TradeNewYork;

        /// <summary>Bit mask of the sessions the robot is permitted to trade:
        /// 1 Sydney, 2 Tokyo, 4 New York, 8 London. Drives the session timeline.</summary>
        public int SessionMask;
        public bool FridayGuard;
        public int FridayCloseHour;
        public double SpreadPips;
        public double MaxSpreadPips;
        public bool UseSpreadFilter;
        public bool UseSmaFilter;
        public bool UseAdxFilter;
        public bool UseTrailingStop;
        public double Adx;

        // ── Circuit breaker matrix ───────────────────────────────────────
        public bool DailyBreached;
        public double DailyMaxLoss;
        public double DailyRemaining;
        public double DailyPctUsed;

        public bool AcctBreached;
        public double AcctMaxLoss;
        public double AcctRemaining;
        public bool AcctEnabled;

        public bool StreakTriggered;
        public int CooldownBars;
        public int ReentryCooldownBars;

        public bool SessionBlocked;
        public bool SpreadTriggered;
        public bool BreakerActive;

        // ── Account rail ─────────────────────────────────────────────────
        public double Balance;
        public double Equity;
        public double DayDrawdownPct;
        public double RiskPerTradePct;
        public double NextLots;
        public int Digits;

        public static string SignalText(int mode)
        {
            switch (mode)
            {
                case 0: return "BULLISH CROSS";
                case 1: return "BULLISH";
                case 2: return "BEARISH CROSS";
                case 3: return "BEARISH";
                default: return "NEUTRAL";
            }
        }

        /// <summary>Round helper so every numeric readout uses the same precision.</summary>
        public static string Num(double value, int decimals)
        {
            return Math.Round(value, decimals, MidpointRounding.AwayFromZero)
                .ToString("F" + decimals);
        }
    }
}
