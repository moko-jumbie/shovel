---
name: ctrader-chart-objects-display
description: "Use when building, reviewing, or debugging anything drawn or overlaid on a cTrader chart: chart controls (Panel, Grid, StackPanel, Canvas, DockPanel, ScrollViewer, Border, TextBlock), Chart.AddControl and RemoveControl, Chart.Draw* objects, the IsVisible/Opacity/IsHitTestVisible contract, and the layout rules that decide whether a control renders at all. Covers the panel-specific layout model (Left/Top only inside Canvas, Dock only inside DockPanel, Grid(rows, columns) and AddChild(child,row,column)), why a wrapper Border collapses a control whose root Grid has no explicit Height, control-tree sizing rules, backtest Visual Mode, and known cTrader Grid rendering bugs. Trigger on a control that does not appear, AddControl, DrawText/DrawRectangle/DrawLine/DrawIcon, a HUD or dashboard overlay, or 'my panel/label is invisible on the chart'."
metadata:
  author: opencode
  version: "1.0.0"
---

# cTrader Chart Objects & Display

## The one rule that matters most

**A cTrader control renders only if the platform can compute its size. If you
build a root control whose size is implied rather than assigned, it silently
does not appear.** There is no exception, no log entry, and no crash. The most
common way to hit this is wrapping a layout panel in a `Border` and letting the
`Border` derive its own `Width`/`Height` from the child.

```csharp
// BROKEN: the Border's Height is computed from root.Height, which is 0.
Border Shell(Panel content)
{
    var shell = new Border { Child = content };
    shell.Height = content.Height + 14;   // content.Height == 0 -> shell.Height == 14
    return shell;                        // 600px of content clipped into a 14px sliver
}
```

The tell: the same builder works for one control and silently fails for another.
Compare a working root against a broken one and look for the explicit `Height`.

### Checklist: "my control is invisible"

Run this top to bottom. Items 1-3 cause the majority of failures.

1. **Does the root control passed to `Chart.AddControl` have an explicit `Width`
   AND `Height`?** Assigned, not inferred. This is the number one cause.
2. **If the root is wrapped in a `Border`, does the Border read a size that is
   actually set?** `new Border { Child = grid }` does not measure `grid`.
3. **Is the `Grid` handed to `Border.Child` given its own `Height`?** A `Grid`
   whose rows are all `SetHeightInPixels` still reports `Height == 0` unless you
   also assign `grid.Height`. Assign both; they are independent.
4. **Is every row/column you rely on explicitly sized** via `SetHeightInPixels`,
   `SetWidthInPixels`, `SetHeightInStars`, or `SetWidthToAuto`? Unsized rows in a
   `Grid(5, 1)` that only ever receives three children leave two rows with no
   definition. Size every row you construct, including empty spacer rows.
5. **Is `IsVisible` left `true`?** It defaults to true, but cell/strip widgets
   legitimately toggle it, and a parent set false hides the whole subtree.
6. **Is `Opacity` actually > 0?** `0` is fully transparent, and it is a `double`
   — an unassigned or NaN value can render nothing.
7. **Is it bigger than the chart pane?** A control larger than its container may
   be dropped rather than clipped. Measure and fit explicitly.
8. **Backtest only:** is Visual Mode enabled? Controls do not render in a
   non-visual backtest, and panels added after the backtest finishes never appear.

## Layout model: rules are panel-specific

cTrader is not WPF. The base `ControlBase` properties do not all apply in all
containers, and reading them as if they do is the second most common failure.

| Property | Only works inside |
|---|---|
| `Left`, `Top`, `Right`, `Bottom` | `Canvas` |
| `Dock` (`Dock.Top`, `Dock.Left`, ...) | `DockPanel` |
| `HorizontalAlignment`, `VerticalAlignment` | `Grid`, `StackPanel`, `DockPanel` |
| `Width`, `Height`, `Margin`, `Padding` | anywhere |
| `IsVisible`, `Opacity`, `IsHitTestVisible` | anywhere |

`HorizontalAlignment.Left` on a direct child of `Chart` is what pins an overlay
to the left edge of the pane. Anchoring relies on it, so do not assume it works
if the root happens to be a `Canvas` — inside a `Canvas`, use `Left`/`Top`.

### The five layout panels

| Panel | Use it for | Key API |
|---|---|---|
| `Grid` | rows x columns of fixed or star sizing | `new Grid(rows, cols)`, `AddChild(child, row, col)`, `Rows[i]`, `Columns[i]` |
| `StackPanel` | one line, horizontal or vertical | `Orientation`, `AddChild(child)` |
| `WrapPanel` | flow children, break on overflow | `Orientation` |
| `DockPanel` | pin children to edges | `child.Dock = Dock.Left` |
| `Canvas` | absolute coordinates | `child.Left`, `child.Top` |
| `ScrollViewer` | clip + scroll oversized content | `Content` |

### Children do not stretch by default

**cTrader's default `HorizontalAlignment` is `Left`, not WPF's `Stretch`.** A child
placed in a `Grid` cell is therefore sized to its own content and pinned to the
cell's left edge, leaving the rest of the cell empty. Star-sized columns still
resolve against the cell, but a child that never stretches looks left-justified
inside its parent and the value column floats instead of sitting flush right.

Set `HorizontalAlignment = HorizontalAlignment.Stretch` explicitly on every row
container, or give it an exact `Width`:

```csharp
// WRONG: hugs the left of the cell, value column floats mid-card.
var row = new Grid(1, 2);
row.Columns[0].SetWidthInStars(1);
row.Columns[1].SetWidthInPixels(valueWidth);

// RIGHT
var row = new Grid(1, 2);
row.HorizontalAlignment = HorizontalAlignment.Stretch;   // or row.Width = contentWidth;
row.Columns[0].SetWidthInStars(1);
row.Columns[1].SetWidthInPixels(valueWidth);
```

Apply this to fixed-width widgets too — chips, pills and fixed cells that *should*
stay compact are the exception, so set them `Left` deliberately rather than
inheriting a blanket stretch.

### Apply padding exactly once

Double-applied inset is a common source of "the content is off-centre in its
frame". If cards carry their own `Padding`, the wrapping panel must be sized to
the padded content and **not** add the same inset again:

```csharp
// WRONG: Frame has Padding 0, so +14 describes padding that was never applied.
// The content ends up 14px narrower than the frame and hugs its left edge.
shell.Width = content.Width + 14;

// RIGHT: size from the border stroke, and let card padding do the insetting.
shell.Width = content.Width + borderThickness * 2;
```

The same rule applies vertically. If a root `Grid` column is wider than the
content `Grid` inside it, the content is off-centre even though both are
individually "correct" — make the root column and the content the same width.

### Grid argument order

`Grid` takes **rows first**, then columns, and `AddChild` takes **row first**.
Both are transposed from the intuition most people arrive with.

```csharp
var grid = new Grid(rowsCount: 10, columnsCount: 2);   // 10 rows, 2 columns
grid.Rows[i].SetHeightInPixels(18);
grid.Columns[j].SetWidthInStars(1);
grid.AddChild(textBlock, rowIndex: i, columnIndex: j);
```

`new Grid(2, 1)` is two rows of one column — a vertical stack. `new Grid(1, 2)`
is one row of two columns — a horizontal pair. There is a five-argument
`AddChild(child, row, column, rowSpan, columnSpan)` for spans.

## Sizing a control that must fit a chart

Because a control larger than its container can be dropped rather than clipped,
resolve size against the chart before building, not after.

```csharp
// Measure what the content wants, then shed optional sections, then shrink type.
// Never shrink below a legibility floor, and never shed a critical section.
int available = (int)Chart.Height - 20;
if (MeasureHeight(requestedFont, HudParts.All) <= available)
    return Build(requestedFont, HudParts.All);

var parts = HudParts.All;
foreach (var droppable in new[] { HudParts.ChartJunk, HudParts.Verbose })
{
    parts &= ~droppable;
    if (MeasureHeight(requestedFont, parts) <= available)
        return Build(requestedFont, parts);
}

return Build(SolveFontSize(requestedFont, available, parts), parts);
```

Prioritise in this order, and log what you actually resolved to:

1. All content at the user's requested size.
2. Shed optional sections, keeping the requested size.
3. Shrink the type, never below the floor.

Also treat the user's size parameters as authoritative. A fitter that solves for
the smallest size that fits *all* content first, clamps to a floor, and only then
sheds sections will make every size parameter look inert on a short pane.

Re-fit on `Chart.SizeChanged` when the layout is size-dependent, and tolerate
`Chart.Width`/`Chart.Height` reporting `0` at `OnStart` by deferring the initial
build until the first refresh that has real dimensions.

## Tree depth and churn

- **Do not rebuild the control tree on a timer.** Mutate `.Text`,
  `.ForegroundColor`, `.BackgroundColor`, `.Width`, `.IsVisible` in place. A
  refresh that recreates a few hundred controls twice a second forces a full
  relayout and will make the chart stutter.
- **Keep nesting shallow.** Three to four levels of `Border`/`Grid` wrappers is
  already enough to lose track of who owns the size. Every extra level is another
  place the height can silently become zero.
- **Build the tree once, hold handles to the leaves.** A small handle class per
  section (`Row`, `Cells`, `Pill`, `LinearMeter`) keeps `Update()` readable and
  keeps the layout code out of the refresh path.
- **Pin rows in pixels** when a value can change width mid-session, so nothing
  reflows and no neighbouring section shifts.

## Mouse input

An overlay that swallows clicks blocks panning, zooming, drawing and the trade
panel. Set `IsHitTestVisible = false` on a purely informational overlay, and only
set it `true` on the specific interactive control that needs clicks.

```csharp
shell.IsHitTestVisible = false;   // whole overlay transparent to the mouse
button.IsHitTestVisible = true;   // except this one control
```

## Drawings vs controls

`Chart.Draw*` and `Chart.AddControl` are separate layers and have known
interference issues in some cTrader Desktop builds. If drawings vanish when a
control is added (or vice versa), and `Print` confirms both calls succeeded, that
is a platform bug, not your code. Workarounds that have shipped behaviour
reliably:

- Add all drawings first, then controls, or the reverse, and keep the order fixed.
- Give every drawing a unique, stable name so redraws replace rather than stack.
- Remove and re-add controls after changing parameters rather than mutating
  layout parameters in place.
- In an `Indicator`, prefer `IndicatorArea ?? Chart` and add controls in
  `Initialize()`; a `NewPanel` indicator area has its own grid rendering bugs.

## Drawing objects

```csharp
Chart.DrawText("hud", "text", VerticalAlignment.Top, HorizontalAlignment.Left,
               Color.FromArgb(200, 0, 0, 0), 10);
Chart.DrawRectangle("box", x1, y1, x2, y2, Color.Cyan);
Chart.DrawLine("ln", time1, y1, time2, y2, Color.Amber);
Chart.RemoveAllObjects();   // by object type, or RemoveObject(name)
```

Always namespace object names (`"hud_"`, `"ea_"`) so several algos on one chart
cannot clobber each other's drawings. Serialise the name when drawing per-bar
objects. Clean up in `OnStop()`.

## Review checklist

- [ ] Root control passed to `AddControl` has explicit `Width` **and** `Height`.
- [ ] Any wrapper `Border` gets its size by assignment, not from the child.
- [ ] Every `Grid` handed to a parent has its own `Height` set.
- [ ] Every constructed row/column is explicitly sized, including spacers.
- [ ] `Grid(rows, cols)` and `AddChild(child, row, col)` argument order correct.
- [ ] `Left`/`Top` only inside `Canvas`; `Dock` only inside `DockPanel`.
- [ ] Content is measured against `Chart.Height` before being built.
- [ ] User size parameters honoured before automatic shrinking.
- [ ] `Opacity` in (0, 1]; no NaN.
- [ ] `IsHitTestVisible = false` on non-interactive overlays.
- [ ] Refresh mutates leaves, never rebuilds the tree.
- [ ] `RemoveControl` in `OnStop()`; drawings namespaced and cleaned up.
- [ ] Backtest runs use Visual Mode.
