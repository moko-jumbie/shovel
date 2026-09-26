using System;
using cAlgo.API;

namespace cAlgo.Robots
{
    // Per-instance look settings. The palette is static, but the font, scale and
    // opacity are user parameters, so they travel with the widget factory.
    //
    // Widgets are told their own font explicitly: this platform does not document
    // font inheritance down the control tree, so relying on it would silently
    // fall back to the chart's default face.
    internal sealed class HudSkin
    {
        public readonly string FontFamily;
        public readonly double FontSize;
        public readonly double Opacity;
        public readonly double RowHeight;

        public HudSkin(string fontFamily, double fontSize, double opacity)
        {
            FontFamily = fontFamily;
            FontSize = fontSize;
            Opacity = opacity;
            RowHeight = Math.Round(fontSize * 1.62, 1);
        }

        public double Small => Math.Round(FontSize * 0.86, 1);
        public double Tiny => Math.Round(FontSize * 0.76, 1);
        public double SectionTitle => Math.Round(FontSize * 0.8, 1);
    }

    // Reusable pieces of the HUD. Each factory keeps a handle on the controls it
    // created so a refresh mutates text and colour in place.
    //
    // Rebuilding the tree on every timer tick was rejected on purpose: it would
    // churn a few hundred control instances twice a second and force the chart to
    // relayout each time. In-place updates leave the tree structure alone.
    internal static class HudWidgets
    {
        public static Thickness Pad(double all) => new Thickness(all);

        public static Thickness Pad(double left, double top, double right, double bottom)
            => new Thickness(left, top, right, bottom);

        public static Grid Columns(int columns) => new Grid(1, columns);

        /// <summary>A text run with the panel font already applied.</summary>
        public static TextBlock Text(HudSkin skin, string text, Color color,
                                     double? size = null,
                                     FontWeight weight = FontWeight.Normal,
                                     TextAlignment align = TextAlignment.Left)
        {
            return new TextBlock
            {
                Text = text,
                ForegroundColor = color,
                FontFamily = skin.FontFamily,
                FontSize = size ?? skin.FontSize,
                FontWeight = weight,
                TextAlignment = align,
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                LineHeight = skin.RowHeight,
                LineStackingStrategy = LineStackingStrategy.MaxHeight
            };
        }

        /// <summary>A solid block: panel, pill, meter fill or cell.</summary>
        public static Border Block(Color fill, double corner = 0)
        {
            return new Border
            {
                BackgroundColor = fill,
                CornerRadius = new CornerRadius(corner),
                BorderColor = fill,
                BorderThickness = new Thickness(0),
                Padding = Pad(0)
            };
        }

        /// <summary>An outlined block, used for section cards and pills.</summary>
        public static Border Frame(Color fill, Color border, double corner,
                                   double borderThickness, double padding)
        {
            Border frame = Block(fill, corner);
            frame.BorderColor = border;
            frame.BorderThickness = new Thickness(borderThickness);
            frame.Padding = Pad(padding);
            return frame;
        }

        /// <summary>A fixed-size cell, the primitive behind every meter and timeline.</summary>
        public static Border Cell(Color fill, double width, double height, double corner = 1.5)
        {
            Border cell = Block(fill, corner);
            cell.Width = width;
            cell.Height = height;
            cell.HorizontalAlignment = HorizontalAlignment.Stretch;
            cell.VerticalAlignment = VerticalAlignment.Bottom;
            return cell;
        }

        /// <summary>A full-width hairline.</summary>
        public static Border Rule(Color color, double height = 1)
        {
            Border rule = Block(color);
            rule.Height = height;
            rule.HorizontalAlignment = HorizontalAlignment.Stretch;
            rule.VerticalAlignment = VerticalAlignment.Center;
            return rule;
        }
    }

    /// <summary>A section card: an outlined frame around a single-column grid that
    /// tracks the height it has consumed.
    ///
    /// The height accounting is the point. Rows are pinned in pixels rather than
    /// auto-sized, so a value that grows wider mid-session can never reflow the
    /// card or push a neighbouring section out of the panel.</summary>
    internal sealed class HudCard
    {
        private const int MaxRows = 40;

        private readonly HudSkin _skin;
        private readonly int _contentWidth;
        private readonly double _titleHeight;
        private int _cursor;
        private double _height;

        public Border Frame { get; }
        public Grid Grid { get; }
        public int ContentWidth => _contentWidth;

        public HudCard(Grid parent, int row, int width, double padding, double titleHeight,
                       HudSkin skin, string title, Color titleColor)
        {
            _skin = skin;
            _titleHeight = titleHeight;

            // A card with no section title is the panel header, and gets the
            // stronger fill so it reads as the banner rather than another section.
            Color fill = string.IsNullOrEmpty(title) ? HudTheme.HeaderFill : HudTheme.SectionFill;

            Frame = HudWidgets.Frame(fill, HudTheme.SectionBorder, 8, 1, padding);
            Frame.Width = width;
            Frame.HorizontalAlignment = HorizontalAlignment.Stretch;

            _contentWidth = width - (int)(padding * 2) - 2;

            Grid = new Grid(MaxRows, 1);
            Grid.Columns[0].SetWidthInPixels(_contentWidth);
            Grid.Width = _contentWidth;
            Grid.HorizontalAlignment = HorizontalAlignment.Stretch;

            Frame.Child = Grid;
            parent.AddChild(Frame, row, 0);

            _height = padding * 2;

            if (!string.IsNullOrEmpty(title))
            {
                Add(HudWidgets.Text(skin, title, titleColor, skin.SectionTitle, FontWeight.Bold),
                    titleHeight);
                Add(HudWidgets.Rule(HudTheme.Hairline), 1);
            }
        }

        /// <summary>The height this card has consumed, including its padding.</summary>
        public double Height => _height;

        public void Add(ControlBase child, double height)
        {
            if (_cursor >= MaxRows)
                return;

            Grid.Rows[_cursor].SetHeightInPixels(height);
            Grid.AddChild(child, _cursor, 0);
            _cursor++;
            _height += height;
        }

        public Row AddMetric(string label, int valueWidth, Color labelColor, double height)
        {
            Grid grid = HudWidgets.Columns(2);
            grid.Columns[0].SetWidthInStars(1);
            grid.Columns[1].SetWidthInPixels(valueWidth);
            grid.Height = height;
            grid.HorizontalAlignment = HorizontalAlignment.Stretch;

            TextBlock labelText = HudWidgets.Text(_skin, label, labelColor, _skin.Small);
            TextBlock valueText = HudWidgets.Text(_skin, "", HudTheme.TextPrimary, _skin.Small,
                                                  FontWeight.Bold, TextAlignment.Right);
            valueText.HorizontalAlignment = HorizontalAlignment.Right;

            grid.AddChild(labelText, 0, 0);
            grid.AddChild(valueText, 0, 1);

            Row row = new Row(labelText, valueText);
            Add(grid, height);
            return row;
        }

        /// <summary>A rail line. The rail has no section header on its first line, so
        /// the label is the only thing identifying the value beside it.</summary>
        public RailRow AddRailMetric(string label, int valueWidth, double height)
        {
            Grid grid = HudWidgets.Columns(2);
            grid.Columns[0].SetWidthInStars(1);
            grid.Columns[1].SetWidthInPixels(valueWidth);
            grid.Height = height;
            grid.HorizontalAlignment = HorizontalAlignment.Stretch;

            TextBlock labelText = HudWidgets.Text(_skin, label, HudTheme.TextDim, _skin.Tiny);
            TextBlock valueText = HudWidgets.Text(_skin, "", HudTheme.TextPrimary, _skin.Tiny,
                                                  FontWeight.Bold, TextAlignment.Right);
            valueText.HorizontalAlignment = HorizontalAlignment.Right;

            grid.AddChild(labelText, 0, 0);
            grid.AddChild(valueText, 0, 1);

            RailRow row = new RailRow(valueText);
            Add(grid, height);
            return row;
        }

        /// <summary>A full-width strip of cells. The win-rate meter, the loss streak,
        /// the session timeline and the trade tape are all this primitive.</summary>
        public Cells AddStrip(int columnCount, double cellWidth, double cellHeight, double gap)
        {
            Grid grid = HudWidgets.Columns(columnCount);
            grid.Height = cellHeight;
            grid.HorizontalAlignment = HorizontalAlignment.Stretch;
            grid.Columns[0].SetWidthInPixels(cellWidth);

            Border[] cells = new Border[columnCount];
            for (int i = 0; i < columnCount; i++)
            {
                grid.Columns[i].SetWidthInPixels(i == 0 ? cellWidth : cellWidth + gap);
                Border cell = HudWidgets.Cell(HudTheme.Track, cellWidth, cellHeight);
                cell.IsVisible = false;
                grid.AddChild(cell, 0, i);
                cells[i] = cell;
            }

            Cells strip = new Cells(cells);
            Add(grid, cellHeight);
            return strip;
        }

        public LinearMeter AddLinear(int trackWidth, double height)
        {
            Grid host = HudWidgets.Columns(1);
            host.Columns[0].SetWidthInPixels(trackWidth);
            host.Height = height;
            host.HorizontalAlignment = HorizontalAlignment.Stretch;

            Border track = HudWidgets.Block(HudTheme.Track, height / 2.0);
            track.Width = trackWidth;
            track.Height = height;
            track.HorizontalAlignment = HorizontalAlignment.Left;

            Border fill = HudWidgets.Block(HudTheme.Cyan, height / 2.0);
            fill.Width = 0;
            fill.Height = height;
            fill.HorizontalAlignment = HorizontalAlignment.Left;
            fill.VerticalAlignment = VerticalAlignment.Center;

            StackPanel stack = new StackPanel { Orientation = Orientation.Horizontal };
            stack.AddChild(fill);
            track.Child = stack;

            host.AddChild(track, 0, 0);
            Add(host, height);

            return new LinearMeter(fill, trackWidth, height);
        }

        /// <summary>A centre-origin bar: positive fills right, negative fills left.</summary>
        public DivergingMeter AddDiverging(int trackWidth, double height)
        {
            Grid host = HudWidgets.Columns(1);
            host.Columns[0].SetWidthInPixels(trackWidth);
            host.Height = height;

            Grid split = HudWidgets.Columns(2);
            split.Columns[0].SetWidthInStars(1);
            split.Columns[1].SetWidthInStars(1);
            split.Width = trackWidth;
            split.HorizontalAlignment = HorizontalAlignment.Left;

            // Negative side grows leftwards from the centre.
            StackPanel left = new StackPanel { Orientation = Orientation.Horizontal };
            left.HorizontalAlignment = HorizontalAlignment.Right;
            left.VerticalAlignment = VerticalAlignment.Stretch;
            Border leftFill = HudWidgets.Block(HudTheme.Red, height / 2.0);
            leftFill.Width = 0;
            leftFill.Height = height;
            leftFill.HorizontalAlignment = HorizontalAlignment.Right;
            leftFill.VerticalAlignment = VerticalAlignment.Center;
            left.AddChild(leftFill);
            split.AddChild(left, 0, 0);

            // Positive side grows rightwards from the centre.
            StackPanel right = new StackPanel { Orientation = Orientation.Horizontal };
            right.HorizontalAlignment = HorizontalAlignment.Left;
            right.VerticalAlignment = VerticalAlignment.Stretch;
            Border rightFill = HudWidgets.Block(HudTheme.Green, height / 2.0);
            rightFill.Width = 0;
            rightFill.Height = height;
            rightFill.HorizontalAlignment = HorizontalAlignment.Left;
            rightFill.VerticalAlignment = VerticalAlignment.Center;
            right.AddChild(rightFill);
            split.AddChild(right, 0, 1);

            host.AddChild(split, 0, 0);
            Add(host, height);

            return new DivergingMeter(leftFill, rightFill, trackWidth, height);
        }

        public Pill AddChip(int width, double height)
        {
            Border chip = HudWidgets.Frame(HudTheme.SectionFill, HudTheme.Hairline, height / 2.0,
                                           1, 0);
            chip.Width = width;
            chip.Height = height;
            chip.HorizontalAlignment = HorizontalAlignment.Left;
            chip.VerticalAlignment = VerticalAlignment.Center;

            TextBlock text = HudWidgets.Text(_skin, "", HudTheme.Cyan, _skin.Tiny,
                                             FontWeight.Bold, TextAlignment.Center);
            text.HorizontalAlignment = HorizontalAlignment.Center;
            chip.Child = text;

            Pill pill = new Pill(chip, text);
            Add(chip, height);
            return pill;
        }

        public void AddRule(Color color, double height = 1)
        {
            Add(HudWidgets.Rule(color), height);
        }

        /// <summary>Reserves vertical space without adding a control.</summary>
        public void Skip(double height)
        {
            if (_cursor >= MaxRows)
                return;

            Grid.Rows[_cursor].SetHeightInPixels(height);
            _cursor++;
            _height += height;
        }

        public void AddSpacer(double height) => Skip(height);
    }

    // ── Update handles ─────────────────────────────────────────────────────

    internal sealed class Row
    {
        private readonly TextBlock _label;
        private readonly TextBlock _value;

        public Row(TextBlock label, TextBlock value)
        {
            _label = label;
            _value = value;
        }

        public TextBlock LabelBlock => _label;
        public TextBlock ValueBlock => _value;

        public void Set(string value, Color color)
        {
            _value.Text = value;
            _value.ForegroundColor = color;
        }

        public void SetLabel(string text, Color color)
        {
            _label.Text = text;
            _label.ForegroundColor = color;
        }
    }

    /// <summary>A rail value cell. Only the value changes on a refresh, so the
    /// label is not retained.</summary>
    internal sealed class RailRow
    {
        private readonly TextBlock _value;

        public RailRow(TextBlock value)
        {
            _value = value;
        }

        public void Set(string text, Color color)
        {
            _value.Text = text;
            _value.ForegroundColor = color;
        }
    }

    /// <summary>A row of cells, each individually addressable by index.</summary>
    internal sealed class Cells
    {
        private readonly Border[] _cells;

        public Cells(Border[] cells)
        {
            _cells = cells;
        }

        public int Capacity => _cells.Length;

        public void SetCell(int index, Color color)
        {
            if (index < 0 || index >= _cells.Length)
                return;

            _cells[index].BackgroundColor = color;
            _cells[index].Height = 1;
            _cells[index].VerticalAlignment = VerticalAlignment.Stretch;
            _cells[index].IsVisible = true;
        }

        /// <summary>A bar whose height encodes magnitude, anchored to the baseline.
        /// This is what turns the trade tape into a chart rather than a strip of
        /// coloured squares.</summary>
        public void SetBar(int index, Color color, double height)
        {
            if (index < 0 || index >= _cells.Length)
                return;

            Border cell = _cells[index];
            cell.BackgroundColor = color;
            cell.Height = height < 1.5 ? 1.5 : Math.Round(height, 1);
            cell.VerticalAlignment = VerticalAlignment.Bottom;
            cell.IsVisible = true;
        }

        public void Clear()
        {
            for (int i = 0; i < _cells.Length; i++)
                _cells[i].IsVisible = false;
        }
    }

    internal sealed class LinearMeter
    {
        private readonly Border _fill;
        private readonly int _trackWidth;
        private readonly double _height;

        public LinearMeter(Border fill, int trackWidth, double height)
        {
            _fill = fill;
            _trackWidth = trackWidth;
            _height = height;
        }

        public void Set(double fraction, Color color)
        {
            double clamped = fraction < 0 ? 0 : (fraction > 1 ? 1 : fraction);
            _fill.Width = Math.Round(_trackWidth * clamped, 1);
            _fill.Height = _height;
            _fill.BackgroundColor = color;
        }
    }

    internal sealed class DivergingMeter
    {
        private readonly Border _left;
        private readonly Border _right;
        private readonly int _trackWidth;
        private readonly double _height;

        public DivergingMeter(Border left, Border right, int trackWidth, double height)
        {
            _left = left;
            _right = right;
            _trackWidth = trackWidth;
            _height = height;
        }

        /// <summary>Ratio is signed; scale is the magnitude that maps to full width.</summary>
        public void Set(double ratio, double scale, Color positive, Color negative)
        {
            double half = _trackWidth / 2.0;
            if (scale <= 0)
                scale = 1;

            double magnitude = ratio / scale;
            if (magnitude < -1) magnitude = -1;
            if (magnitude > 1) magnitude = 1;

            if (magnitude >= 0)
            {
                _right.Width = Math.Round(half * magnitude, 1);
                _right.BackgroundColor = positive;
                _left.Width = 0;
            }
            else
            {
                _left.Width = Math.Round(half * -magnitude, 1);
                _left.BackgroundColor = negative;
                _right.Width = 0;
            }

            _left.Height = _height;
            _right.Height = _height;
        }
    }

    internal sealed class Pill
    {
        private readonly Border _chip;
        private readonly TextBlock _text;

        public Pill(Border chip, TextBlock text)
        {
            _chip = chip;
            _text = text;
        }

        public void Set(string text, Color foreground, Color background)
        {
            _text.Text = text;
            _text.ForegroundColor = foreground;
            _chip.BackgroundColor = background;
        }
    }
}
