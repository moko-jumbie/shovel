using System;
using cAlgo.API;

namespace cAlgo.Robots
{
    // Owns the HUD's lifetime: fits it to the chart, builds the controls once,
    // refreshes them from a snapshot, and removes them again on stop.
    //
    // The controller never reads trading state. The robot hands it a
    // HudSnapshot and it renders it, which is what makes it impossible for the
    // dashboard to perturb a trade.
    //
    // Sizing is resolved here rather than hard-coded in HudLayout. Five sections
    // laid out at the default font are roughly 740px tall, which is taller than
    // most chart panes, and cTrader does not clip an oversized control - it drops
    // it, so the panel would simply never appear. The font is therefore solved
    // against the height the chart actually has, and re-solved when the pane is
    // resized.
    internal sealed class HudController : IDisposable
    {
        // Cleared top and bottom so the panel never sits under the toolbar or the
        // time scale.
        private const double VerticalClearance = 20;
        private const double HorizontalClearance = 16;

        // Below this the readouts stop being legible, so the fit gives up and lets
        // the user shorten the panel with the section toggles instead.
        private const double MinFontSize = 7.0;

        // Re-fitting on every pixel of a splitter drag would rebuild the tree
        // several times a second for no visible benefit.
        private const double RefitThreshold = 40;

        private readonly Chart _chart;
        private readonly bool _showRail;
        private readonly string _fontFamily;
        private readonly double _baseFontSize;
        private readonly double _opacity;
        private readonly double _scale;
        private readonly int _leftMargin;
        private readonly int _railRightMargin;
        private readonly int _topInset;

        private HudLayout _layout;
        private Border _panel;
        private Border _rail;
        private HudParts _parts = HudParts.All;
        private double _appliedFontSize;
        private bool _built;
        private bool _pending;
        private double _fittedHeight;

        public HudController(Chart chart, string fontFamily, double fontSize, double opacity,
                             bool showRail, double scale, int leftMargin, int railRightMargin,
                             int topInset)
        {
            _chart = chart;
            _showRail = showRail;
            _fontFamily = fontFamily;
            _baseFontSize = fontSize;
            _opacity = opacity;
            _scale = scale;
            _leftMargin = leftMargin;
            _railRightMargin = railRightMargin;
            _topInset = topInset;
        }

        public bool IsBuilt => _built;

        /// <summary>The left stack root, exposed so the caller can dock it.</summary>
        public Border Panel => _panel;

        /// <summary>The right rail root, or null when the rail is disabled.</summary>
        public Border Rail => _rail;

        /// <summary>Attaches the control tree to the chart. Safe to call once per
        /// start; a second call is ignored rather than stacking duplicate panels.
        /// If the chart has not been laid out yet the attach is deferred to the
        /// first refresh rather than fitted against a size of zero.</summary>
        public void Build()
        {
            if (_built || _pending)
                return;

            if (!ChartSizeKnown())
            {
                _pending = true;
                return;
            }

            Attach();
            _built = true;
        }

        /// <summary>Renders one frame. The snapshot is refilled by the caller, so a
        /// refresh allocates nothing. Also re-fits the panel if the chart pane has
        /// been resized since the tree was built.</summary>
        public void Refresh(HudSnapshot snapshot)
        {
            if (_pending)
            {
                _pending = false;

                if (!ChartSizeKnown())
                {
                    _pending = true;
                    return;
                }

                Attach();
                _built = true;
            }

            if (!_built)
                return;

            if (Math.Abs(_chart.Height - _fittedHeight) >= RefitThreshold)
            {
                Rebuild();
                return;
            }

            _layout.Update(snapshot);
        }

        private bool ChartSizeKnown() => _chart.Width > 0 && _chart.Height > 0;

        private void Attach()
        {
            _fittedHeight = _chart.Height;

            double fontSize = ResolveFit(out HudParts parts);
            int panelWidth = SolvePanelWidth();

            _appliedFontSize = fontSize;
            _layout = new HudLayout(new HudSkin(_fontFamily, fontSize, _opacity),
                                    panelWidth, ValueWidthFor(panelWidth), parts);
            _parts = parts;

            _panel = _layout.BuildPanel();
            _chart.AddControl(_panel);

            if (_showRail)
            {
                _rail = _layout.BuildRail();
                _chart.AddControl(_rail);
            }

            Dock();
        }

        /// <summary>Sections that were left out to make the panel fit, for the
        /// caller to report. A silently missing section reads as a bug.</summary>
        public HudParts OmittedParts => HudParts.All & ~_parts;

        /// <summary>Font size the panel was actually built at, which can be below
        /// the requested size when the pane is too short even for the two
        /// mandatory sections.</summary>
        public double AppliedFontSize => _appliedFontSize;

        /// <summary>True when the type size had to be reduced to fit.</summary>
        public bool FontReduced => _appliedFontSize < _baseFontSize * _scale - 0.05;

        private void Rebuild()
        {
            Detach();
            Attach();
            _built = true;
        }

        private void Detach()
        {
            if (_panel != null)
                _chart.RemoveControl(_panel);
            if (_rail != null)
                _chart.RemoveControl(_rail);

            _panel = null;
            _rail = null;
            _layout = null;
            _built = false;
        }

        // The panel is docked rather than free-floating, so the margins are
        // applied as an offset from the alignment edge. The top inset is shared by
        // the panel and the rail so the two read as one unit.
        private void Dock()
        {
            SetMargin(_panel, new Thickness(_leftMargin, _topInset, 0, 0));
            SetMargin(_rail, new Thickness(0, _topInset, _railRightMargin, 0));
        }

        private static void SetMargin(ControlBase control, Thickness margin)
        {
            if (control != null)
                control.Margin = margin;
        }

        /// <summary>Resolves the fit. The type size the user asked for is
        /// authoritative: on a short pane the trade is made in sections, not in
        /// legibility. Type is only shrunk once every optional section has already
        /// been shed, and never below the floor the font-size parameter enforces.
        /// </summary>
        private double ResolveFit(out HudParts parts)
        {
            double requested = Math.Max(_baseFontSize * _scale, MinFontSize);

            // The top inset eats into the same budget as the clearance, so a panel
            // fitted to the full pane height would hang off the bottom of the chart.
            double available = _chart.Height - VerticalClearance - _topInset;

            if (available <= 0)
            {
                parts = HudParts.All;
                return requested;
            }

            // 1. Everything, at the requested size.
            if (MeasureHeight(requested, HudParts.All) <= available)
            {
                parts = HudParts.All;
                return requested;
            }

            // 2. Shed sections in reverse order of importance, still at the
            //    requested size. A larger font costs sections; it does not
            //    silently get overridden, which is what made the size and scale
            //    parameters look inert.
            HudParts current = HudParts.All;

            foreach (HudParts shed in ShedOrder)
            {
                current &= ~shed;

                if (MeasureHeight(requested, current) <= available)
                {
                    parts = current;
                    return requested;
                }
            }

            // 3. Nothing left to shed. Only now is the type size traded away, and
            //    only as far as the legibility floor.
            parts = current;
            return SolveFontSize(requested, available, current);
        }

        // Reverse order of trading importance. The right rail already carries
        // spread, balance and risk per trade, so Session & Protection loses less
        // than dropping live position P&L, and the historical performance block
        // is the first thing to go.
        private static readonly HudParts[] ShedOrder =
        {
            HudParts.Performance,
            HudParts.Session,
            HudParts.Positions
        };

        /// <summary>Chooses the largest font size whose panel still fits the pane.</summary>
        private double SolveFontSize(double requested, double available, HudParts parts)
        {
            if (MeasureHeight(requested, parts) <= available)
                return requested;

            // Panel height is linear in font size to within a pixel: every row is
            // fontSize * 1.62, and the only fixed contributions are the paddings,
            // the meters and the trade tape. Two probes recover the slope and the
            // intercept, which gives an exact answer instead of a guess that would
            // need a search loop.
            double probe = Math.Max(requested * 0.75, MinFontSize);
            double measuredRequested = MeasureHeight(requested, parts);
            double measuredProbe = MeasureHeight(probe, parts);

            double denominator = requested - probe;
            if (denominator <= 0)
                return requested;

            double slope = (measuredRequested - measuredProbe) / denominator;
            if (slope <= 0)
                return requested;

            double intercept = measuredRequested - slope * requested;
            double fitted = (available - intercept) / slope;

            if (fitted >= requested)
                return requested;

            return fitted < MinFontSize ? MinFontSize : fitted;
        }

        private double MeasureHeight(double fontSize, HudParts parts)
        {
            // A throwaway tree: the caller only wants the height, and building it
            // is far cheaper than modelling the row accounting on paper.
            HudLayout probe = new HudLayout(
                new HudSkin(_fontFamily, fontSize, _opacity),
                HudLayout.DefaultPanelWidth, ValueWidthFor(HudLayout.DefaultPanelWidth), parts);

            probe.BuildPanel();
            return probe.PanelHeight;
        }

        /// <summary>Widens the value column with the panel, keeping the same ratio
        /// the layout was proportioned for.</summary>
        private static int ValueWidthFor(int panelWidth)
        {
            return (int)Math.Round(panelWidth * 0.442, MidpointRounding.AwayFromZero);
        }

        private int SolvePanelWidth()
        {
            int reserved = _leftMargin + (int)HorizontalClearance;

            if (_showRail)
                reserved += HudLayout.RailWidth + _railRightMargin;

            int available = (int)(_chart.Width - reserved);
            int width = HudLayout.DefaultPanelWidth;

            return available < width ? Math.Max(available, 180) : width;
        }

        public void Dispose()
        {
            _pending = false;
            Detach();
        }
    }
}
