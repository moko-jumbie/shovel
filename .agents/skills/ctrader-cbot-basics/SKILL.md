---
name: ctrader-cbot-basics
description: "Use when writing, structuring, or reviewing cTrader cBots (C#) built on the cAlgo API. Covers the event-driven lifecycle (OnStart/OnTick/OnBar/OnBarClosed/OnStop/OnError/OnPosition*), the critical OnBar vs OnBarClosed bar-index mapping, label-instead-of-magic position identity, [Robot]/[Parameter] conventions, project anatomy (csproj, config.json, .algo, .cbotset), AccessRights, the .NET 6/8 child-process model, standard file section layout, and an MQL4-to-cTrader translation table. Trigger on any task that creates or edits a cBot .cs file."
metadata:
  author: opencode
  version: "1.0.0"
---

# cTrader cBot Basics

## Core Principles

**1. cTrader is event-driven — you do not write the loop.**
cTrader calls your handlers; you implement them. Never `while(true)`, never `Task.Run` with a polling loop, never `Thread.Sleep` in `OnTick`. cTrader runs each algo in a **separate child process** since v4.2, so a runaway loop burns a core and freezes the chart but will not take down the terminal. It still must be fixed.

**2. The lifecycle, and which one you actually need.**

| Handler | Fires | Use for |
| --- | --- | --- |
| `OnStart()` | once, on start/restart | Build indicators in `OnStart`, validate params, init state |
| `OnTick()` | every bid/ask change | Per-tick management: trailing stops, break-even, time guards |
| `OnBar()` | new bar **opened** | Bar-open logic; closed-bar data is at `Last(1)` |
| `OnBarClosed()` | new bar opened (alias) | Bar-close signals; closed bar is at `Last(0)` |
| `OnStop()` | once, on stop | Remove chart objects, stop timers, release UI |
| `OnError(Error)` | algo/runtime error | Log `error.Code`, decide whether to stop the instance |
| `OnPositionOpened/Closed/Modified` | position events | Maintain state that must react to fills and SL/TP hits |

`OnBarClosed` is an **alias of the bar-opened event** — neither fires without a tick. So the last bar of Friday "closes" on Monday's first tick. Weekend and holiday gaps are real, not a bug.

**3. The bar-index mapping is the #1 porting bug.**

| Context | `Last(0)` / `Bars.LastBar` | `Last(1)` | `Last(2)` |
| --- | --- | --- | --- |
| `OnTick()`, `OnBar()` | **forming** bar (incomplete) | last closed | one before that |
| `OnBarClosed()` | **just-closed** bar | one before that | two before that |

Inside `OnBarClosed` the newly opened bar is omitted from `Bars` entirely. Porting MT4 `iMA(..., 0)` / `Close[1]` code by substituting `Last(0)` blindly produces look-ahead bias on one side and a one-bar-late signal on the other. **Prefer `OnBarClosed()` for signals** and read `Last(0)`/`Last(1)`; that also makes the backtest match live execution.

**4. The label replaces the magic number.**
cTrader has no magic number. Position identity is `Label` + `SymbolName` (+ `TradeType`). Every scan must filter both, or the cBot will manage a human's trade or another instance's position on the same account:

```csharp
var mine = Positions.FindAll(Label, SymbolName, TradeType.Buy);
```

`Positions` and `PendingOrders` also see orders placed manually or by other algos — they are not scoped to you.

**5. Every tunable number belongs in a `[Parameter]`.**

```csharp
[Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None, AddIndicators = true)]
public class MyBot : Robot
{
    [Parameter("Label", DefaultValue = "MyBot", Group = "General")]
    public string Label { get; set; }

    [Parameter("Fast Period", DefaultValue = 9, MinValue = 2, MaxValue = 200, Step = 1, Group = "Signal")]
    public int FastPeriod { get; set; }

    [Parameter("Risk %", DefaultValue = 1.0, MinValue = 0.1, MaxValue = 5.0, Step = 0.1, Group = "Risk")]
    public double RiskPercent { get; set; }
}
```

- `Group` is not cosmetic. Without it the parameter panel becomes an unreadable wall past ~8 inputs, and reviewers cannot tell which knob does what.
- `TimeFrame` is auto-exposed to the optimiser — do not redeclare it.
- The optimiser can only vary `[Parameter]` properties. A hardcoded literal in trading logic is untunable by construction.
- Enum-typed parameters (`RoundingMode`, `MovingAverageType`, `TimeFrame`) are optimisable via discrete `Values`.

**6. `AccessRights` — declare the minimum.**
`AccessRights.None` (default for trading-only), `.FileSystem`, `.Web`, `.Full`. Requesting more than you need blocks cloud cBots and is a red flag in review. Only set `.Web` if you genuinely call an external endpoint.

**7. One function = one responsibility.**
Reading market data, computing a decision, and sending an order are three responsibilities. Reading your method names in order should describe what the cBot does: `IsWithinSession()`, `HasNewSignal()`, `ManageOpenPositions()`, `CalculateVolume()`.

**8. Comment the WHY, not the WHAT.**

## Project Anatomy

```
Sources/Robots/MyBot/
├── MyBot.sln
└── MyBot/
    ├── MyBot.csproj        # <PackageReference Include="cTrader.Automate" Version="*-*" />
    ├── MyBot.cs            # namespace cAlgo.Robots
    ├── config.json         # { "AccessRights": "None", "AddIndicators": true }
    ├── GlobalUsings.cs     # global using cAlgo.API; cAlgo.API.Collections; ...
    ├── obj/ bin/           # build output — never edit, never commit .algo by hand
```

- **`TargetFramework` must match your cTrader build.** cTrader 4.2+ is .NET 6.0; the cTrader CLI (5.10+) expects .NET 8. Do not guess — check the TFM the IDE/CLI generated, or the installed `cAlgo.API.dll` target. A mismatch fails at load, not at compile.
- `cTrader.Automate` uses a floating `Version="*-*"`, so builds follow the installed platform. Pin a version only if you need reproducible builds.
- **`.algo`** is the deployable package cTrader installs. **`.cbotset`** is a saved parameter set — this is how you ship "these settings work" alongside a cBot.
- **`using cAlgo.API.Internals;` is a red flag.** `Internals` is non-public surface: it can break on any platform upgrade. Use the documented `cAlgo.API` surface unless you have verified a specific member is public.

## Standard File Section Layout

```
// ─────────────────────────────────────────────
// Header / description / fix log
// ─────────────────────────────────────────────
[Robot(...)]
namespace cAlgo.Robots
{
    public class MyBot : Robot
    {
        // ─── Parameters ───
        // ─── Indicator instances (private fields) ───
        // ─── State ───

        // ─── Lifecycle ───
        protected override void OnStart() { }
        protected override void OnBarClosed() { }   // thin orchestrator
        protected override void OnTick() { }        // management only
        protected override void OnStop() { }

        // ─── Signal logic ───
        // ─── Filters / guards ───
        // ─── Risk ───
        // ─── Execution helpers ───
    }
}
```

`OnBarClosed()` orchestrates: check guards (each an early `return`), then delegate. Do not bury strategy logic inline.

## Anti-Patterns

| Anti-pattern | Symptom | Fix |
| --- | --- | --- |
| Reading `Last(0)` in `OnTick`/`OnBar` for a signal | Look-ahead bias; backtest curve-fit, live fills differ | Use `OnBarClosed()` or `Last(1)` |
| Mixing `OnBar()` and `OnBarClosed()` indexing | Off-by-one signals that still "look" plausible | Pick one handler, know its mapping (table above) |
| `Last(0)` in `OnBarClosed` then again in `OnTick` | Same bar read as two different bars | Centralise bar reads in one helper |
| Scanning `Positions` without the label | Modifies a human's or another bot's trade | Always filter `Label` + `SymbolName` |
| `ExecuteMarketOrder` result ignored | Silent no-trade; backtest shows fills that never happened | Check `result.IsSuccessful`, log `result.Error` |
| `while(true)` / `Thread.Sleep` / `Task.Run` poll in a handler | Core burn, chart freeze, pointless cBot death | Return from the handler; use the right event |
| Volume hardcoded in lots | Wrong size on every non-forex symbol | Volume is in **units**; use `Symbol.NormalizeVolumeInUnits` |
| Magic literals in trading logic | Not optimisable, unrecognisable later | Promote to `[Parameter]` or `const` |
| Undeclared `Group` on many parameters | Unusable parameter panel | Group by concern |
| `using cAlgo.API.Internals` | Breaks on platform upgrade | Stick to the public API |
| No `OnStop` cleanup | Chart objects persist across restarts | Remove every `Chart.Draw*` you added |
| Hand-rolled HMA/WMA recomputed per bar | See `ctrader-performance-tuning` | Compute once per bar, cache, reuse |

## MQL4 → cTrader Translation Table

| MQL4 / MT4 | cTrader |
| --- | --- |
| `OrderSend(..., lots, ...)` | `ExecuteMarketOrder(TradeType, symbol, volumeInUnits, label, slPips, tpPips)` |
| Order volume in **lots** | Volume in **units** of base currency (`Symbol.QuantityToVolumeInUnits(lots)` to convert) |
| `MagicNumber` | `Label` string (filter with `Positions.FindAll(Label, ...)`) |
| `OrderSelect(i, ...)` | Iterate `Positions` / `PendingOrders` collections |
| `MarketInfo(Sym, MODE_TICKVALUE/TICKSIZE)` | `Symbol.TickValue` / `Symbol.TickSize`, or better `Symbol.VolumeForFixedRisk(...)` |
| `MODE_LOTSTEP` / `MINLOT` / `MAXLOT` | `Symbol.VolumeInUnitsStep` / `Min` / `Max` via `NormalizeVolumeInUnits` |
| `MODE_SPREAD` (points) | `Symbol.Spread` → `Symbol.NormalizePips(Symbol.ToPips(Symbol.Spread))` |
| `MODE_STOPLEVEL` | No direct equivalent; cTrader validates server-side and returns `ErrorCode.InvalidStopLossTakeProfit` |
| `GetLastError()` | `TradeResult.Error` (`ErrorCode?`) and `OnError(Error)` |
| `IsTradeAllowed()` / `IsTradeContextBusy()` | No equivalent; cTrader queues and validates for you |
| `RefreshRates()` | Not needed — `Symbol.Bid`/`Ask` are already live |
| `Sleep(100)` | `Sleep(ms)` exists, but it blocks the handler; prefer `ExecuteAsync` or a deferred callback |
| `OnTick` with a `iTime(...,0) == lastBarTime` guard | `OnBarClosed()` — the guard becomes unnecessary |
| `Close[0]` / `iMA(...,0)` (forming bar) | `Last(0)` in `OnTick`/`OnBar`; `Last(0)` is closed in `OnBarClosed` |
| `ObjectsDeleteAll(0, prefix)` in `OnDeinit` | Remove objects you added in `OnStop()`; cTrader tracks `Chart.Draw*` by name |
| `Comment()` / chart-object HUD | `Chart.DrawText` / WPF `Canvas`+`Grid` via `ChartArea` |
| `input` variable | `[Parameter]` property |
| `sinput` (static, exclude from optimiser) | No direct equivalent — simply omit the optimiser checkbox for that parameter |
| MT4 Strategy Tester "Every tick" | Backtesting tab → Data: **Tick data** (vs 1-min bar, fixed spread) |

## Reference

- `ctrader-order-execution` (pips contracts, `TradeResult`, `ErrorCode`, netting vs hedging)
- `ctrader-risk-management` (native risk APIs, circuit breakers, position sizing)
- `ctrader-code-review` (the checklist that audits each area above)
- Companion MQL4 skills: `mql4-ea-basics` covers the same lifecycle in MT4 terms.
