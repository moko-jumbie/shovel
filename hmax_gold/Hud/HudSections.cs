using cAlgo.API;

namespace cAlgo.Robots
{
    // The five panels, in the order they appear in the stack.
    //
    // Row-for-row parity with the MT4 EA's RenderDashboard (MT4EAv4.txt:1384-1491)
    // is deliberate — every readout the original dashboard showed is still here.
    // The additions (loss budget meters, the HMA spread bar, the session timeline,
    // the trade tape, the position list) are the "new visualisations" layer.

    internal sealed class BreakerSection
    {
        private Row _daily;
        private Row _account;
        private Row _streak;
        private Row _session;
        private LinearMeter _dailyMeter;
        private LinearMeter _accountMeter;
        private Cells _streakCells;
        private Pill _verdict;

        public void Build(HudSkin skin, HudCard card, int valueWidth, double rowHeight,
                          double meterHeight)
        {
            int track = card.ContentWidth;

            _daily = card.AddMetric("Daily Loss", valueWidth, HudTheme.TextDim, rowHeight);
            _dailyMeter = card.AddLinear(track, meterHeight);
            card.Skip(3);

            _account = card.AddMetric("Acct Guard", valueWidth, HudTheme.TextDim, rowHeight);
            _accountMeter = card.AddLinear(track, meterHeight);
            card.Skip(3);

            _streak = card.AddMetric("Loss Streak", valueWidth, HudTheme.TextDim, rowHeight);
            _streakCells = card.AddStrip(8, (track - 7 * 3) / 8.0, 9, 3);
            card.Skip(3);

            _session = card.AddMetric("Session / Spread", valueWidth, HudTheme.TextDim, rowHeight);

            card.Skip(3);
            _verdict = card.AddChip(track, Math.Round(rowHeight * 0.95, 1));
        }

        public void Update(HudSnapshot s)
        {
            // Daily loss — the EA showed remaining currency against the budget, and
            // turned the row red the moment the breaker tripped.
            if (s.DailyBreached)
            {
                _daily.Set("TRIGGERED", HudTheme.Red);
                _dailyMeter.Set(1, HudTheme.Red);
            }
            else
            {
                _daily.Set(HudSnapshot.Num(s.DailyRemaining, 2) + " / " +
                           HudSnapshot.Num(s.DailyMaxLoss, 2),
                    s.DailyPctUsed > 70 ? HudTheme.Amber : HudTheme.Green);
                _dailyMeter.Set(s.DailyMaxLoss > 0 ? s.DailyPctUsed / 100.0 : 0,
                    s.DailyPctUsed > 70 ? HudTheme.Amber : HudTheme.Cyan);
            }

            // Account guard — the EA distinguished "GUARD OFF" from a live buffer.
            if (!s.AcctEnabled)
            {
                _account.Set("GUARD OFF", HudTheme.TextFaint);
                _accountMeter.Set(0, HudTheme.Track);
            }
            else if (s.AcctBreached)
            {
                _account.Set("HALTED - ALL CLOSED", HudTheme.Red);
                _accountMeter.Set(1, HudTheme.Red);
            }
            else
            {
                double used = s.AcctMaxLoss > 0
                    ? 1 - (s.AcctRemaining / s.AcctMaxLoss)
                    : 0;
                bool tight = s.AcctMaxLoss > 0 && s.AcctRemaining < s.AcctMaxLoss * 0.30;
                _account.Set(HudSnapshot.Num(s.AcctRemaining, 2) + " buffer",
                    tight ? HudTheme.Amber : HudTheme.TextPrimary);
                _accountMeter.Set(used, tight ? HudTheme.Amber : HudTheme.Cyan);
            }

            // Loss streak — segmented so the approach to the limit is visible before
            // it is reached, which the EA's numeric row never conveyed.
            int cells = _streakCells.Capacity;
            int filled = s.MaxConsecLosses > 0 && s.MaxConsecLosses < cells
                ? s.MaxConsecLosses
                : cells;

            _streakCells.Clear();
            for (int i = 0; i < filled; i++)
            {
                bool isLimit = s.MaxConsecLosses > 0 && i == s.MaxConsecLosses - 1;
                Color color = isLimit ? HudTheme.Amber : HudTheme.Track;
                if (i < s.ConsecLosses)
                    color = s.StreakTriggered ? HudTheme.Red : HudTheme.Amber;
                _streakCells.SetCell(i, color);
            }

            if (s.StreakTriggered)
                _streak.Set("TRIGGERED  risk " + HudSnapshot.Num(s.RiskMultiplier * 100, 0)
                            + "%", HudTheme.Red);
            else if (s.ConsecLosses > 0)
                _streak.Set(s.ConsecLosses + "/" + s.MaxConsecLosses + "  risk " +
                            HudSnapshot.Num(s.RiskMultiplier * 100, 0) + "%",
                    HudTheme.Amber);
            else
                _streak.Set("0/" + s.MaxConsecLosses + "  risk 100%", HudTheme.Green);

            // Session / spread, with the re-entry cooldown appended when it is
            // actually holding an entry back — otherwise invisible on the chart.
            string cooldown = s.CooldownBars > 0 && s.ReentryCooldownBars > 0
                ? "  CD " + s.CooldownBars + "b"
                : "";

            if (s.SessionBlocked)
                _session.Set("SESSION BLOCKED" + cooldown, HudTheme.Red);
            else if (s.SpreadTriggered)
                _session.Set("SPREAD " + HudSnapshot.Num(s.SpreadPips, 1) + "/" +
                             HudSnapshot.Num(s.MaxSpreadPips, 0) + cooldown, HudTheme.Red);
            else
                _session.Set("CLEAR " + HudSnapshot.Num(s.SpreadPips, 1) + "/" +
                             HudSnapshot.Num(s.MaxSpreadPips, 0) + "p" + cooldown,
                    s.CooldownBars > 0 ? HudTheme.Amber : HudTheme.TextPrimary);

            if (s.BreakerActive)
                _verdict.Set("SAFEGUARDS ACTIVE", HudTheme.Red, HudTheme.Red);
            else
                _verdict.Set("SAFEGUARDS STABLE", HudTheme.Green, HudTheme.SectionFill);
        }
    }

    internal sealed class StrategySection
    {
        private Row _fast;
        private Row _slow;
        private Row _macro;
        private Row _atr;
        private Row _losses;
        private Pill _signal;
        private DivergingMeter _spread;

        public void Build(HudSkin skin, HudCard card, int valueWidth, double rowHeight,
                          double meterHeight)
        {
            int track = card.ContentWidth;

            _fast = card.AddMetric("Fast HMA", valueWidth, HudTheme.TextDim, rowHeight);
            _slow = card.AddMetric("Slow HMA", valueWidth, HudTheme.TextDim, rowHeight);

            _signal = card.AddChip(track, Math.Round(rowHeight * 0.95, 1));
            card.Skip(2);

            _spread = card.AddDiverging(track, meterHeight);
            card.Skip(3);

            _macro = card.AddMetric("Macro Trend", valueWidth, HudTheme.TextDim, rowHeight);
            _atr = card.AddMetric("ATR / SL-TP", valueWidth, HudTheme.TextDim, rowHeight);
            _losses = card.AddMetric("Consec Losses", valueWidth, HudTheme.TextDim, rowHeight);
        }

        public void Update(HudSnapshot s)
        {
            _fast.Set(HudSnapshot.Num(s.FastHma, s.Digits), HudTheme.TextPrimary);
            _slow.Set(HudSnapshot.Num(s.SlowHma, s.Digits), HudTheme.TextPrimary);

            Color signalColor;
            switch (s.SignalMode)
            {
                case 0:
                case 1:
                    signalColor = HudTheme.Green;
                    break;
                case 2:
                case 3:
                    signalColor = HudTheme.Red;
                    break;
                default:
                    signalColor = HudTheme.Amber;
                    break;
            }

            bool isCross = s.SignalMode == 0 || s.SignalMode == 2;
            _signal.Set(HudSnapshot.SignalText(s.SignalMode), signalColor,
                        isCross ? HudTheme.Track : HudTheme.SectionFill);

            // The spread bar answers the question the two HMA rows cannot: how far
            // apart they are right now, relative to the volatility that would be
            // used to stop out. Full width means one ATR of separation.
            _spread.Set(s.HmaSpreadRatio, 1.0, HudTheme.Green, HudTheme.Red);

            if (!s.MacroEnabled)
                _macro.Set("FILTER OFF", HudTheme.TextFaint);
            else
                _macro.Set((s.MacroAbove ? "ABOVE " : "BELOW ") + HudSnapshot.Num(s.MacroSma, s.Digits),
                    s.MacroAbove ? HudTheme.Green : HudTheme.Red);

            _atr.Set(HudSnapshot.Num(s.Atr, s.Digits) + "  " + HudSnapshot.Num(s.SlMultiplier, 1)
                     + "x/" + HudSnapshot.Num(s.TpMultiplier, 1) + "x", HudTheme.TextDim);

            _losses.Set(s.ConsecLosses + "  risk " + HudSnapshot.Num(s.RiskMultiplier * 100, 0)
                        + "%",
                s.ConsecLosses >= s.MaxConsecLosses ? HudTheme.Red : HudTheme.TextDim);
        }
    }

    internal sealed class SessionSection
    {
        private Row _filter;
        private Row _sessions;
        private Row _friday;
        private Row _broker;
        private Row _spread;
        private Row _filters;
        private Cells _timeline;

        public void Build(HudSkin skin, HudCard card, int valueWidth, double rowHeight)
        {
            int track = card.ContentWidth;
            int labelWidth = valueWidth + 44;

            _filter = card.AddMetric("Session Filter", labelWidth, HudTheme.TextDim, rowHeight);

            // 24 hour cells, one per broker-time hour. The EA drew this as an ASCII
            // +/-- bar; discrete cells can also show which session each hour belongs
            // to and whether it is enabled.
            _timeline = card.AddStrip(24, (track - 23 * 1.5) / 24.0, 10, 1.5);
            card.Skip(3);

            _sessions = card.AddMetric("Sessions", valueWidth, HudTheme.TextDim, rowHeight);
            _friday = card.AddMetric("Friday Guard", valueWidth, HudTheme.TextDim, rowHeight);
            _broker = card.AddMetric("Broker Session", valueWidth, HudTheme.TextDim, rowHeight);
            _spread = card.AddMetric("Spread", valueWidth, HudTheme.TextDim, rowHeight);
            _filters = card.AddMetric("Filters", valueWidth, HudTheme.TextDim, rowHeight);
        }

        public void Update(HudSnapshot s)
        {
            if (s.SessionEnabled)
                _filter.Set(s.TradingActive ? "ONLINE" : "OFFLINE",
                    s.TradingActive ? HudTheme.Cyan : HudTheme.Red);
            else
                _filter.Set("ALL HOURS", HudTheme.TextDim);

            _timeline.Clear();
            for (int hour = 0; hour < 24; hour++)
            {
                int mask = SessionMask(hour);
                bool anyEnabled = (mask & s.SessionMask) != 0;
                Color color;

                if (!s.SessionEnabled)
                    color = HudTheme.Track;
                else if (anyEnabled)
                    color = SessionColor(mask);
                else
                    color = HudTheme.Track;

                // The current hour is drawn at full height so the playhead is
                // findable at a glance; the rest sit on the baseline.
                if (hour == s.Hour)
                    _timeline.SetBar(hour, color, 10);
                else
                    _timeline.SetCell(hour, color);
            }

            _sessions.Set(
                "SYD " + OnOff(s.TradeSydney) + "  TOK " + OnOff(s.TradeTokyo) +
                "  LON " + OnOff(s.TradeLondon) + "  NY " + OnOff(s.TradeNewYork),
                s.SessionEnabled ? HudTheme.TextPrimary : HudTheme.TextFaint);

            _friday.Set(s.FridayGuard ? "ON  close " + s.FridayCloseHour + ":00 Fri" : "OFF",
                s.FridayGuard ? HudTheme.Cyan : HudTheme.TextFaint);

            _broker.Set(s.SessionLabel, s.TradingActive ? HudTheme.Amber : HudTheme.Red);

            // An enabled filter and a disabled one must never look alike, or the
            // row implies protection that is not switched on.
            if (!s.UseSpreadFilter)
                _spread.Set("FILTER OFF", HudTheme.TextFaint);
            else
                _spread.Set(HudSnapshot.Num(s.SpreadPips, 1) + " / " +
                            HudSnapshot.Num(s.MaxSpreadPips, 0) + " pips",
                    s.SpreadTriggered ? HudTheme.Red : HudTheme.TextPrimary);

            _filters.Set("SMA " + OnOff(s.UseSmaFilter) + "  ADX " + OnOff(s.UseAdxFilter) +
                         "  SPRD " + OnOff(s.UseSpreadFilter) + "  " +
                         HudSnapshot.Num(s.Adx, 0) + "  TRAIL " + OnOff(s.UseTrailingStop),
                HudTheme.TextDim);
        }

        private static string OnOff(bool value) => value ? "ON" : "OFF";

        /// <summary>Which of the four sessions the EA recognises contain this hour.
        /// Same windows as IsWithinAllowedSession and GetSessionLabel.</summary>
        private static int SessionMask(int hour)
        {
            int mask = 0;
            if (hour < 9) mask |= 1;                                  // Sydney
            if (hour >= 3 && hour < 12) mask |= 2;                     // Tokyo
            if (hour >= 10 && hour < 19) mask |= 8;                    // London
            if (hour >= 15) mask |= 4;                                 // New York
            return mask;
        }

        private static Color SessionColor(int mask)
        {
            // Overlaps take the wider session, matching the EA's precedence order.
            if ((mask & 8) != 0 && (mask & 4) != 0) return HudTheme.Violet;
            if ((mask & 2) != 0 && (mask & 8) != 0) return HudTheme.Cyan;
            if ((mask & 1) != 0 && (mask & 2) != 0) return HudTheme.Violet;
            if ((mask & 8) != 0) return HudTheme.Cyan;
            if ((mask & 4) != 0) return HudTheme.Green;
            if ((mask & 2) != 0) return HudTheme.Amber;
            return HudTheme.Violet;
        }
    }

    internal sealed class PositionsSection
    {
        private Row _active;
        private Row _floating;
        private Row _lots;
        private readonly Row[] _detail = new Row[HudSnapshot.PositionSlots];
        private readonly TextBlock[] _detailTag = new TextBlock[HudSnapshot.PositionSlots];
        private readonly bool[] _detailShown = new bool[HudSnapshot.PositionSlots];

        public void Build(HudSkin skin, HudCard card, int valueWidth, double rowHeight)
        {
            _active = card.AddMetric("Active Trades", valueWidth, HudTheme.TextDim, rowHeight);
            _floating = card.AddMetric("Floating P&L", valueWidth, HudTheme.TextDim, rowHeight);
            _lots = card.AddMetric("Exposure", valueWidth, HudTheme.TextDim, rowHeight);
            card.Skip(3);

            for (int i = 0; i < HudSnapshot.PositionSlots; i++)
            {
                Grid grid = HudWidgets.Columns(2);
                grid.Columns[0].SetWidthInStars(1);
                grid.Columns[1].SetWidthInPixels(86);
                grid.Height = rowHeight;

                _detailTag[i] = HudWidgets.Text(skin, "", HudTheme.TextDim, skin.Small,
                                                FontWeight.Bold);
                TextBlock value = HudWidgets.Text(skin, "", HudTheme.TextPrimary, skin.Small,
                                                  FontWeight.Bold, TextAlignment.Right);
                value.HorizontalAlignment = HorizontalAlignment.Right;

                grid.AddChild(_detailTag[i], 0, 0);
                grid.AddChild(value, 0, 1);

                _detail[i] = new Row(_detailTag[i], value);
                _detail[i].LabelBlock.IsVisible = false;
                _detail[i].ValueBlock.IsVisible = false;
                _detailShown[i] = false;

                card.Add(grid, rowHeight);
            }
        }

        public void Update(HudSnapshot s)
        {
            _active.Set(s.OpenCount + "   B " + s.Buys + "  S " + s.Sells,
                s.OpenCount > 0 ? HudTheme.Cyan : HudTheme.TextFaint);

            _floating.Set(HudSnapshot.Num(s.FloatingPnl, 2),
                s.FloatingPnl > 0 ? HudTheme.Green
                    : (s.FloatingPnl < 0 ? HudTheme.Red : HudTheme.TextDim));

            _lots.Set(HudSnapshot.Num(s.OpenLots, 2) + " lots",
                s.OpenCount > 0 ? HudTheme.TextPrimary : HudTheme.TextFaint);

            for (int i = 0; i < HudSnapshot.PositionSlots; i++)
            {
                bool show = i < s.PositionShown;

                // Visibility is toggled on change, but the text has to be rewritten
                // every frame: an open position's P&L and price move continuously,
                // and skipping it here would freeze the row at its entry values.
                if (show != _detailShown[i])
                {
                    _detailShown[i] = show;
                    _detail[i].LabelBlock.IsVisible = show;
                    _detail[i].ValueBlock.IsVisible = show;
                }

                if (!show)
                    continue;

                _detailTag[i].Text = (s.PositionIsBuy[i] ? "BUY " : "SELL ") +
                                     HudSnapshot.Num(s.PositionEntry[i], s.Digits);
                _detailTag[i].ForegroundColor = s.PositionIsBuy[i]
                    ? HudTheme.Green : HudTheme.Red;

                double pnl = s.PositionPnl[i];
                _detail[i].Set(HudSnapshot.Num(pnl, 2),
                    pnl > 0 ? HudTheme.Green : (pnl < 0 ? HudTheme.Red : HudTheme.TextDim));
            }
        }
    }

    internal sealed class PerformanceSection
    {
        private Row _trades;
        private Row _winRate;
        private Row _profitFactor;
        private Row _net;
        private Row _today;
        private Cells _rate;
        private Cells _tape;

        public void Build(HudSkin skin, HudCard card, int valueWidth, double rowHeight)
        {
            int track = card.ContentWidth;

            _trades = card.AddMetric("Total Trades", valueWidth, HudTheme.TextDim, rowHeight);
            _winRate = card.AddMetric("Win Rate", valueWidth, HudTheme.TextDim, rowHeight);
            _rate = card.AddStrip(10, (track - 9 * 3) / 10.0, 8, 3);
            card.Skip(3);

            _profitFactor = card.AddMetric("Profit Factor", valueWidth, HudTheme.TextDim, rowHeight);
            _net = card.AddMetric("Net P&L", valueWidth, HudTheme.TextDim, rowHeight);
            _today = card.AddMetric("Today", valueWidth, HudTheme.TextDim, rowHeight);

            card.Skip(4);
            _tape = card.AddStrip(HudSnapshot.TapeSlots,
                                  (track - (HudSnapshot.TapeSlots - 1) * 2) / (double)HudSnapshot.TapeSlots,
                                  26, 2);
        }

        public void Update(HudSnapshot s)
        {
            _trades.Set(s.TotalTrades + "   " + s.TotalWins + "W / " + s.TotalLosses + "L",
                HudTheme.TextPrimary);

            _winRate.Set(s.TotalTrades > 0 ? HudSnapshot.Num(s.WinRatePct, 1) + "%" : "--",
                s.TotalTrades == 0 ? HudTheme.TextFaint
                    : (s.WinRatePct >= 50 ? HudTheme.Green : HudTheme.Red));

            _rate.Clear();
            if (s.TotalTrades > 0)
            {
                int filled = (int)Math.Round(s.WinRatePct / 10.0);
                filled = filled < 0 ? 0 : (filled > 10 ? 10 : filled);
                for (int i = 0; i < 10; i++)
                    _rate.SetCell(i, i < filled
                        ? (s.WinRatePct >= 50 ? HudTheme.Green : HudTheme.Red)
                        : HudTheme.Track);
            }

            // A profit factor with no losing trades is genuinely undefined rather
            // than zero, so it is reported as such instead of reading 0.00 in
            // green. The gross legs are shown too, because 1.84 means something
            // very different over 40 trades than over 400.
            string factor = s.GrossLoss > 0
                ? HudSnapshot.Num(s.ProfitFactor, 2)
                : (s.GrossProfit > 0 ? "NO LOSSES" : "--");

            _profitFactor.Set(s.TotalTrades > 0
                    ? factor + "  " + HudSnapshot.Num(s.GrossProfit, 0) + "/" +
                      HudSnapshot.Num(s.GrossLoss, 0)
                    : "--",
                s.GrossLoss <= 0 ? HudTheme.Green
                    : (s.ProfitFactor >= 2 ? HudTheme.Green
                        : (s.ProfitFactor >= 1 ? HudTheme.Amber : HudTheme.Red)));

            _net.Set(HudSnapshot.Num(s.NetPnl, 2),
                s.NetPnl > 0 ? HudTheme.Green : (s.NetPnl < 0 ? HudTheme.Red : HudTheme.TextDim));

            _today.Set(s.TodayTrades + " trades  " + HudSnapshot.Num(s.TodayPnl, 2),
                s.TodayPnl > 0 ? HudTheme.Green : (s.TodayPnl < 0 ? HudTheme.Red : HudTheme.TextDim));

            // Trade tape: the last twenty round trips as signed bars. A run of red
            // bars here is the earliest visible warning that the streak guard is
            // about to engage.
            _tape.Clear();
            if (s.TapeCount == 0)
                return;

            double peak = 0;
            for (int i = 0; i < s.TapeCount; i++)
            {
                double magnitude = Math.Abs(s.Tape[i]);
                if (magnitude > peak)
                    peak = magnitude;
            }

            if (peak <= 0)
                return;

            // Newest trade on the right, so the tape reads left-to-right in time.
            int offset = HudSnapshot.TapeSlots - s.TapeCount;
            for (int i = 0; i < s.TapeCount; i++)
            {
                double value = s.Tape[i];
                double fraction = Math.Abs(value) / peak;
                double barHeight = 3 + fraction * 23;
                _tape.SetBar(offset + i,
                    value >= 0 ? HudTheme.Green : HudTheme.Red, barHeight);
            }
        }
    }
}
