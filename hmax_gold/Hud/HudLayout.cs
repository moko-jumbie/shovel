using System;
using cAlgo.API;

namespace cAlgo.Robots
{
    // Which sections the panel carries. The set is resolved against the chart
    // pane before anything is built, because a control taller than its container
    // is discarded by the platform rather than clipped, so the panel has to be
    // made to fit rather than left to overflow.
    [Flags]
    internal enum HudParts
    {
        None = 0,
        Breakers = 1,
        Strategy = 2,
        Session = 4,
        Positions = 8,
        Performance = 16,
        All = Breakers | Strategy | Session | Positions | Performance
    }

    // Builds and updates the HUD: a left stack carrying the five sections, and a
    // slim right rail carrying account and risk readouts.
    //
    // Every row is pinned in pixels by HudCard, so nothing reflows as values
    // change width. The root panels are built once and only their contents are
    // mutated afterwards.
    internal sealed class HudLayout
    {
        public const int DefaultPanelWidth = 344;
        public const int RailWidth = 172;

        private const int RailValueWidth = 92;
        private const double Pad = 7;
        private const double MeterHeight = 6;
        private const double Gap = 3;

        // Stroke width of the panel/rail border. The shell is built without padding,
        // so this is the only chrome the content has to fit inside.
        private const double ShellBorder = 1;

        private readonly HudSkin _skin;
        private readonly double _rowHeight;
        private readonly double _titleHeight;

        // Panel geometry is resolved by HudController against the chart it is
        // being attached to, not hard-coded: the natural size of five sections is
        // far taller than a typical chart pane, and a control larger than its
        // container is dropped by the platform rather than clipped.
        private readonly int _panelWidth;
        private readonly int _valueWidth;
        private readonly HudParts _parts;
        private double _panelHeight;

        /// <summary>Sections this instance actually built, for the caller to report.</summary>
        public HudParts Parts => _parts;

        // Left stack
        private Border _panel;
        private Grid _headerHost;
        private Grid _stackHost;
        private Border _panelAccent;
        private TextBlock _clock;
        private TextBlock _headline;
        private TextBlock _timeFrameText;
        private Border _statusLed;
        private BreakerSection _breakers;
        private StrategySection _strategy;
        private SessionSection _session;
        private PositionsSection _positions;
        private PerformanceSection _performance;

        // Right rail
        private Border _rail;
        private RailRow _railBalance;
        private RailRow _railEquity;
        private RailRow _railDrawdown;
        private RailRow _railRisk;
        private RailRow _railLots;
        private RailRow _railSpread;
        private LinearMeter _railBudget;
        private TextBlock _railBudgetLabel;
        private TextBlock _railCountLabel;

        public HudLayout(HudSkin skin, int panelWidth, int valueWidth, HudParts parts)
        {
            _skin = skin;
            _panelWidth = panelWidth;
            _valueWidth = valueWidth;
            _parts = parts;
            _rowHeight = skin.RowHeight;
            _titleHeight = Math.Round(skin.RowHeight * 1.2, 1);
        }

        /// <summary>Height the built panel occupies, measured from the rows that were
        /// actually added. The controller uses this to test the fit.</summary>
        public double PanelHeight => _panelHeight;

        // ── Build ─────────────────────────────────────────────────────────

        public Border BuildPanel()
        {
            // The panel border hugs the cards exactly. The 7px inset is card padding
            // and is applied once, here, by sizing the card column to _panelWidth
            // minus it. Making the root column the full _panelWidth instead left the
            // 330px of content hugging the left of a 344px cell, which read as the
            // cards being off-centre inside their own background.
            int inner = _panelWidth - (int)Pad * 2;

            // The accent bar is a sibling of the content rather than an overlay so
            // it can never sit on top of the header text.
            Grid shell = new Grid(2, 1);
            shell.Columns[0].SetWidthInPixels(inner);
            shell.Width = inner;
            shell.HorizontalAlignment = HorizontalAlignment.Stretch;

            Grid accent = new Grid(1, 1);
            accent.Columns[0].SetWidthInPixels(inner);
            accent.Width = inner;
            accent.Height = 2;
            accent.HorizontalAlignment = HorizontalAlignment.Stretch;
            _panelAccent = HudWidgets.Block(HudTheme.Cyan, 2);
            _panelAccent.HorizontalAlignment = HorizontalAlignment.Stretch;
            // Without an explicit height the accent sits in an auto-sized row with
            // no content to measure, so the state colour would never be visible.
            _panelAccent.Height = 2;
            accent.AddChild(_panelAccent, 0, 0);

            double headerHeight = BuildHeader(inner);
            double stackHeight = BuildStack(inner) + Pad;

            Grid content = new Grid(2, 1);
            content.Columns[0].SetWidthInPixels(inner);
            content.Width = inner;
            // Shell() sizes the border from the content, so the content has to
            // carry an explicit height: a grid with only fixed rows does not
            // report one and the panel would collapse to its padding.
            content.Height = headerHeight + stackHeight;
            content.HorizontalAlignment = HorizontalAlignment.Stretch;
            content.Rows[0].SetHeightInPixels(headerHeight);
            content.Rows[1].SetHeightInPixels(stackHeight);
            content.AddChild(_headerHost, 0, 0);
            content.AddChild(_stackHost, 1, 0);

            shell.Rows[0].SetHeightInPixels(2);
            shell.Rows[1].SetHeightInPixels(headerHeight + stackHeight);
            shell.AddChild(accent, 0, 0);
            shell.AddChild(content, 1, 0);

            // The root grid has to carry an explicit height. Shell() derives the
            // border from content.Height, and a grid whose rows are all pinned in
            // pixels does not report one of its own, so the height read back was 0
            // and the border collapsed to its own padding. BuildRail() sets the
            // same thing on its root for the same reason.
            shell.Height = 2 + headerHeight + stackHeight;

            _panelHeight = shell.Height + ShellBorder * 2;

            _panel = Shell(shell, HudTheme.Cyan, HorizontalAlignment.Left);
            return _panel;
        }

        public Border BuildRail()
        {
            int railInner = RailWidth - (int)Pad * 2;

            Grid stack = new Grid(2, 1);
            stack.Columns[0].SetWidthInPixels(railInner);
            stack.Width = railInner;
            stack.HorizontalAlignment = HorizontalAlignment.Left;

            HudCard account = new HudCard(stack, 0, railInner, Pad, _titleHeight, _skin,
                                          "ACCOUNT", HudTheme.Violet);
            _railBalance = account.AddRailMetric("Balance", RailValueWidth, _rowHeight);
            _railEquity = account.AddRailMetric("Equity", RailValueWidth, _rowHeight);
            _railDrawdown = account.AddRailMetric("Day DD", RailValueWidth, _rowHeight);
            account.AddRule(HudTheme.Hairline);
            account.Skip(Gap);
            _railBudgetLabel = HudWidgets.Text(_skin, "DAILY BUDGET 0%", HudTheme.TextDim,
                                               _skin.Tiny);
            account.Add(_railBudgetLabel, _titleHeight);
            _railBudget = account.AddLinear(account.ContentWidth, MeterHeight);

            stack.Rows[0].SetHeightInPixels(account.Height);

            HudCard risk = new HudCard(stack, 1, railInner, Pad, _titleHeight, _skin,
                                       "RISK", HudTheme.Cyan);
            _railRisk = risk.AddRailMetric("Risk/trade", RailValueWidth, _rowHeight);
            _railLots = risk.AddRailMetric("Next lots", RailValueWidth, _rowHeight);
            _railSpread = risk.AddRailMetric("Spread", RailValueWidth, _rowHeight);
            risk.AddRule(HudTheme.Hairline);
            risk.Skip(Gap);
            _railCountLabel = HudWidgets.Text(_skin, "", HudTheme.TextDim, _skin.Tiny);
            risk.Add(_railCountLabel, _titleHeight);

            stack.Rows[1].SetHeightInPixels(risk.Height + Pad);

            Grid root = new Grid(1, 1);
            root.Columns[0].SetWidthInPixels(railInner);
            root.Width = railInner;
            root.Height = account.Height + risk.Height + Pad;
            root.HorizontalAlignment = HorizontalAlignment.Left;
            root.Rows[0].SetHeightInPixels(account.Height + risk.Height + Pad);
            root.AddChild(stack, 0, 0);

            _rail = Shell(root, HudTheme.Hairline, HorizontalAlignment.Right);
            return _rail;
        }

        private Border Shell(Grid content, Color borderColor, HorizontalAlignment alignment)
        {
            Border shell = HudWidgets.Frame(HudTheme.PanelFill, borderColor, 12, ShellBorder, 0);

            // Frame() is built with zero padding, so the only chrome the content has
            // to clear is the border stroke itself. The old Pad * 2 here described
            // padding that was never applied, which left 14px of slack at the right
            // edge and made the content look pushed to one side.
            shell.Width = content.Width + ShellBorder * 2;
            shell.Height = content.Height + ShellBorder * 2;
            shell.Opacity = _skin.Opacity;
            shell.HorizontalAlignment = alignment;
            shell.VerticalAlignment = VerticalAlignment.Top;

            // Centre the content inside the border. This is separate from the
            // alignment above, which positions the whole HUD against the chart edge.
            content.HorizontalAlignment = HorizontalAlignment.Center;
            content.VerticalAlignment = VerticalAlignment.Top;

            // Without this the HUD swallows mouse input meant for the chart.
            shell.IsHitTestVisible = false;
            shell.Child = content;
            return shell;
        }

        private double BuildHeader(int width)
        {
            _headerHost = new Grid(1, 1);
            _headerHost.Columns[0].SetWidthInPixels(width);
            _headerHost.Width = width;
            _headerHost.HorizontalAlignment = HorizontalAlignment.Stretch;

            HudCard header = new HudCard(_headerHost, 0, width, Pad, _titleHeight, _skin, null,
                                         HudTheme.Cyan);

            Grid line = HudWidgets.Columns(3);
            line.Columns[0].SetWidthInPixels(12);
            line.Columns[1].SetWidthInStars(1);
            line.Columns[2].SetWidthInPixels(56);
            line.Height = _titleHeight;
            line.HorizontalAlignment = HorizontalAlignment.Stretch;

            _statusLed = HudWidgets.Block(HudTheme.Cyan, 4);
            _statusLed.Width = 8;
            _statusLed.Height = 8;
            _statusLed.HorizontalAlignment = HorizontalAlignment.Left;
            _statusLed.VerticalAlignment = VerticalAlignment.Center;

            _headline = HudWidgets.Text(_skin, "HMAX//GOLD", HudTheme.TextPrimary,
                                        _skin.FontSize + 0.6, FontWeight.Bold);

            _timeFrameText = HudWidgets.Text(_skin, "", HudTheme.Cyan, _skin.Small,
                                             FontWeight.Bold, TextAlignment.Right);
            _timeFrameText.HorizontalAlignment = HorizontalAlignment.Right;

            line.AddChild(_statusLed, 0, 0);
            line.AddChild(_headline, 0, 1);
            line.AddChild(_timeFrameText, 0, 2);
            header.Add(line, _titleHeight);

            _clock = HudWidgets.Text(_skin, "", HudTheme.TextDim, _skin.Tiny);
            header.Add(_clock, _titleHeight);

            _headerHost.Rows[0].SetHeightInPixels(header.Height);
            return header.Height;
        }

        private double BuildStack(int width)
        {
            _stackHost = new Grid(5, 1);
            _stackHost.Columns[0].SetWidthInPixels(width);
            _stackHost.Width = width;
            _stackHost.HorizontalAlignment = HorizontalAlignment.Stretch;

            // Sections are packed into consecutive rows so a dropped section does
            // not leave a gap, and each card is only built when its flag is set.
            int row = 0;
            double total = 0;

            if (Has(HudParts.Breakers))
            {
                _breakers = new BreakerSection();
                HudCard breakers = new HudCard(_stackHost, row, width, Pad, _titleHeight, _skin,
                                               "CIRCUIT BREAKER MATRIX", HudTheme.Amber);
                _breakers.Build(_skin, breakers, _valueWidth, _rowHeight, MeterHeight);
                _stackHost.Rows[row].SetHeightInPixels(breakers.Height);
                total += breakers.Height;
                row++;
            }

            if (Has(HudParts.Strategy))
            {
                _strategy = new StrategySection();
                HudCard strategy = new HudCard(_stackHost, row, width, Pad, _titleHeight, _skin,
                                               "STRATEGY ENGINE", HudTheme.Cyan);
                _strategy.Build(_skin, strategy, _valueWidth, _rowHeight, MeterHeight);
                _stackHost.Rows[row].SetHeightInPixels(strategy.Height);
                total += strategy.Height;
                row++;
            }

            if (Has(HudParts.Session))
            {
                _session = new SessionSection();
                HudCard session = new HudCard(_stackHost, row, width, Pad, _titleHeight, _skin,
                                              "SESSION & PROTECTION", HudTheme.Cyan);
                _session.Build(_skin, session, _valueWidth, _rowHeight);
                _stackHost.Rows[row].SetHeightInPixels(session.Height);
                total += session.Height;
                row++;
            }

            if (Has(HudParts.Positions))
            {
                _positions = new PositionsSection();
                HudCard positions = new HudCard(_stackHost, row, width, Pad, _titleHeight, _skin,
                                                "OPEN POSITIONS", HudTheme.Cyan);
                _positions.Build(_skin, positions, _valueWidth, _rowHeight);
                _stackHost.Rows[row].SetHeightInPixels(positions.Height);
                total += positions.Height;
                row++;
            }

            if (Has(HudParts.Performance))
            {
                _performance = new PerformanceSection();
                HudCard performance = new HudCard(_stackHost, row, width, Pad, _titleHeight, _skin,
                                                  "EA PERFORMANCE", HudTheme.Cyan);
                _performance.Build(_skin, performance, _valueWidth, _rowHeight);
                _stackHost.Rows[row].SetHeightInPixels(performance.Height);
                total += performance.Height;
                row++;
            }

            return total;
        }

        private bool Has(HudParts part) => (_parts & part) != 0;

        // ── Update ────────────────────────────────────────────────────────

        public void Update(HudSnapshot s)
        {
            Color level = HudTheme.Level(s.Level);
            _panelAccent.BackgroundColor = level;
            _statusLed.BackgroundColor = level;

            _headline.Text = "HMAX//GOLD  " + s.Symbol;
            _timeFrameText.Text = s.TimeFrameLabel;
            _clock.Text = s.Clock + "   " + HudSnapshot.Num(s.ClosePrice, s.Digits);

            if (_breakers != null)
                _breakers.Update(s);
            if (_strategy != null)
                _strategy.Update(s);
            if (_session != null)
                _session.Update(s);
            if (_positions != null)
                _positions.Update(s);
            if (_performance != null)
                _performance.Update(s);

            // BuildRail is skipped entirely when the rail is switched off, so
            // these handles stay null for the lifetime of the instance.
            if (_rail == null)
                return;

            _railBalance.Set(HudSnapshot.Num(s.Balance, 2), HudTheme.TextPrimary);
            _railEquity.Set(HudSnapshot.Num(s.Equity, 2),
                s.Equity >= s.Balance ? HudTheme.Green : HudTheme.Red);
            _railDrawdown.Set(HudSnapshot.Num(s.DayDrawdownPct, 2) + "%",
                s.DayDrawdownPct > 0.05 ? HudTheme.Amber : HudTheme.TextFaint);

            _railBudgetLabel.Text = "DAILY BUDGET " + HudSnapshot.Num(s.DailyPctUsed, 0) + "%";
            _railBudget.Set(s.DailyMaxLoss > 0 ? s.DailyPctUsed / 100.0 : 0,
                s.DailyBreached ? HudTheme.Red
                    : (s.DailyPctUsed > 70 ? HudTheme.Amber : HudTheme.Cyan));

            _railRisk.Set(HudSnapshot.Num(s.RiskPerTradePct, 2) + "%",
                s.RiskMultiplier < 1 ? HudTheme.Amber : HudTheme.TextPrimary);
            _railLots.Set(HudSnapshot.Num(s.NextLots, 2), HudTheme.TextPrimary);
            _railSpread.Set(HudSnapshot.Num(s.SpreadPips, 1) + "p",
                s.SpreadTriggered ? HudTheme.Red : HudTheme.TextPrimary);
            _railCountLabel.Text = s.TotalTrades + " TRADES   " + s.TodayTrades + " TODAY";
        }
    }
}
