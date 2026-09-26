---
name: ctrader-code-review
description: "Use when reviewing or auditing an existing cTrader cBot or indicator before deployment, or as part of development. A checklist-driven reviewer: unchecked TradeResult returns, volume-in-units vs lots confusion, pips vs prices, ProtectionType misuse, OnBarClosed bar-index off-by-one (look-ahead bias), missing label filtering, RoundingMode.ToNearest risk creep, min-volume risk over-risk, circuit breakers present but not wired, hedging vs netting assumptions, over-declared AccessRights, leaked chart objects, cAlgo.API.Internals usage, LINQ and per-tick Print in OnTick, and state restoration across restarts. Trigger on 'review this cBot', 'is this safe to run live', 'check my code', or a pre-live audit."
metadata:
  author: opencode
  version: "1.0.0"
---

# cTrader Code Review

## Core Principle

**Review for behaviors, not just syntax.** Most live cBot failures come from incomplete edge-case handling and silent failure paths, not compile errors. Run this checklist top to bottom and report each finding with a `file:line` reference, severity, and concrete fix.

## Checklist

### 1. State & Lifecycle
- [ ] `OnStart` validates parameters and refuses to run on invalid config (periods, hours, risk values, `FastPeriod < SlowPeriod`, etc.) rather than failing later at trade time.
- [ ] Indicators are constructed in `OnStart`, never in a handler (constructing an indicator per tick recalculates it every tick).
- [ ] `OnStop` removes **every** chart object / UI control the cBot added, each in its own `try/catch` so one throw does not abort the rest of the cleanup. Timers are stopped.
- [ ] **Restart reconciliation:** positions opened before a restart still exist. On `OnStart`, walk the cBot's positions and restore any intended SL/TP or trailing state that is missing. A restart during a network blip otherwise leaves a position naked.
- [ ] State that must survive a restart (day-start balance, consecutive-loss count, bars-since-loss) is either rebuilt from history or reset deliberately — and a restart mid-day does not restore a stale day-start balance.
- [ ] `OnError(Error)` is handled and logs `error.Code`.

### 2. Bar Indexing & Look-Ahead (highest-severity category)
- [ ] The cBot uses **one** bar handler consistently, and its indexing matches: `OnBar()`/`OnTick` → closed bar is `Last(1)`; `OnBarClosed()` → closed bar is `Last(0)`.
- [ ] No signal decision reads `Last(0)` from `OnTick` or `OnBar` (forming-bar data = look-ahead bias; the backtest will lie).
- [ ] `Last(0)` in `OnBarClosed` and `Last(0)` in `OnTick` are not being conflated as the same bar anywhere in the file.
- [ ] Crossover/comparison logic uses the correct pair (e.g. `Last(0)` vs `Last(1)` in `OnBarClosed`, or `Last(1)` vs `Last(2)` in `OnTick`) — verify the "prior" index really is prior.
- [ ] No hand-rolled indicator with an off-by-one or reversed series order. Custom HMA/WMA-style code is the usual offender; check the series indexing and the weight direction.

### 3. Order Execution
- [ ] **Every** `Execute*` / `Place*` / `ModifyPosition` / `ClosePosition` return value is checked for `IsSuccessful`.
- [ ] Every failure logs `result.Error` **with context** — the values attempted (type, volume, sl, tp), not just the code.
- [ ] Transient errors (`Disconnected`, `Timeout`) are retried with a cap; permanent ones (`BadVolume`, `InvalidStopLossTakeProfit`, `NoMoney`, `InvalidRequest`) fail loudly and are not blindly retried.
- [ ] After a `Timeout` retry path, `Positions` is re-checked before resending — the fill may have landed server-side. Blind retry after timeout creates duplicate positions.
- [ ] No MT4-ism crept in: no `RefreshRates()`, no `IsTradeAllowed()`/`IsTradeContextBusy()`, no `GetLastError()`, no `MODE_STOPLEVEL`/`MODE_LOTSTEP` references. (See the translation table in `ctrader-cbot-basics`.)
- [ ] Iterating `Positions`/`PendingOrders` while closing/modifying — **the collection is snapshotted first** (`FindAll(...)` into a local), because the operation mutates it.

### 4. Units & Math
- [ ] **Volume is in units, not lots.** Every order call's volume argument is a unit figure. A `0.01` passed where `1000` is expected is a critical bug.
- [ ] Lots↔units conversion uses `Symbol.QuantityToVolumeInUnits` / `Symbol.VolumeInUnitsToQuantity` rather than multiplying/dividing by 100000 by hand.
- [ ] SL/TP arguments are **pips**, and any computed pip value goes through `Symbol.NormalizePips` before use (prevents "invalid decimal places" → `InvalidStopLossTakeProfit`).
- [ ] Spread comparisons convert correctly: `Symbol.NormalizePips(Symbol.ToPips(Symbol.Spread))`. A raw `Symbol.Spread` compared against a "pips" parameter is a unit error.
- [ ] Price-level math rounds to `Symbol.Digits` before being used as an absolute stop.
- [ ] `Position.StopLoss` / `TakeProfit` are treated as **nullable** (`is null` / `.Value`), not assumed present.

### 5. Position Identity & Account Mode
- [ ] **Every** position/pending-order scan filters by `Label` **and** `SymbolName`. An unfiltered scan manages other algos' and the user's trades.
- [ ] `OnPositionClosed` / `OnPositionOpened` handlers check the position's label before updating internal state.
- [ ] The cBot's correctness does not depend on a hedging-only assumption (multiple independent positions, per-position partial management) unless the account is confirmed hedging. On a netted account an opposite order reduces/closes/reverses the single position — "close all buys" and grid logic break.

### 6. Risk Management
- [ ] Position sizing uses the native risk APIs (`VolumeForFixedRisk` / `VolumeForProportionalRisk`) or, if hand-rolled, converts account currency correctly for non-account-base symbols.
- [ ] `NormalizeVolumeInUnits` uses `RoundingMode.Down` — **`ToNearest` can round up past the intended risk cap**, silently breaking the risk model across an optimisation sweep.
- [ ] **Min-volume risk creep is handled:** if the risk-derived volume falls below `Symbol.VolumeInUnitsMin`, the trade is skipped or logged — not silently floored to the minimum, which risks far more than the parameter promised.
- [ ] Risk is sized on `Account.Equity` (live) or `Account.Balance` (reproducible) — chosen deliberately and commented.
- [ ] Free margin is checked before committing (or `GetEstimatedMargin` consulted); a `NoMoney` rejection mid-signal wastes the setup.
- [ ] SL/TP derive from volatility (ATR) rather than a hardcoded pip count, and the ATR read is `> 0`-guarded.
- [ ] Stop placement respects the correct side: SL on the losing side, TP on the winning side, for both Buy and Sell.

### 7. Circuit Breakers — present AND wired
- [ ] Daily loss limit, consecutive-loss guard, loss cooldown, and (optionally) max-drawdown halt are **wired into the entry path**, not merely present as fields or shown on a panel. Trace from the guard's trigger to the actual `return` that blocks the order.
- [ ] The consecutive-loss counter increments on **live closes** — via `OnPositionClosed` — not only rebuilt from history in `OnStart`. A guard rebuilt at init and never incremented is dead code.
- [ ] Day-boundary reset compares `Server.Time.Date` against the stored date; it does not rely on a cBot restart happening at midnight.
- [ ] The risk-reduction multiplier is monotonic and resets on a win and on a new day.

### 8. Sessions, Time & Weekends
- [ ] Session definitions state their timezone, and the timezone matches what the inputs actually mean (`Server.Time` is broker/server time, not UTC — the classic off-by-hours bug when porting from an EA that used `TimeCurrent()` with a different broker offset).
- [ ] Overnight sessions (start > end) are handled correctly in the windowing logic, or the input range is constrained to avoid needing it.
- [ ] The session rule exists in **one** place and is shared by the trading path and any UI/panel path — duplicated logic drifts.
- [ ] Friday/weekend flatten uses `Server.Time.DayOfWeek` + hour correctly, and the code accounts for `BarClosed` being an alias of `BarOpened` (a Friday bar "closes" on Monday's first tick).
- [ ] Market-closed handling: `Symbol.MarketHours.IsOpened()` is checked (or `ErrorCode.MarketClosed` is handled) before assuming an order will fill.

### 9. Performance
- [ ] No LINQ (`.Where`/`.Select`/`.Any`) in `OnTick` — use `Positions.FindAll`/`Find` and a plain `for` loop. (See `ctrader-performance-tuning`.)
- [ ] No `Print` in `OnTick` or a per-tick management loop; logging is event-driven and/or behind a verbose flag.
- [ ] Indicators are built in `OnStart`, not per tick; per-bar indicator values are read once into a field and reused by both the trading and UI paths.
- [ ] Expensive per-bar math (custom HMA/WMA loops, nested weighted sums) is computed once per bar and cached, not recomputed on every access.
- [ ] `ModifyPosition` is skipped when the target stop has not actually moved (no-op round-trips / rate limits).
- [ ] No `Thread.Sleep` in a hot handler; no `Task.Run` polling loop; no `.Wait()`/`.Result` on `ExecuteAsync` (blocks the algo thread and can deadlock).

### 10. Project & Hygiene
- [ ] `AccessRights` declares the **minimum** required (no `.Web`/`.Full`/`.FileSystem` unless genuinely used; blocks cloud deployment otherwise).
- [ ] `config.json` matches the code's actual needs (`AccessRights`, `AddIndicators`).
- [ ] `TargetFramework` in the `.csproj` matches the installed cTrader build.
- [ ] No `using cAlgo.API.Internals;` unless a specific member has been verified as public — `Internals` is non-public surface and breaks on platform upgrade.
- [ ] **No dead code paths:** every helper has a call site. Grep each method and confirm it is invoked — an uncalled risk/close-tracking helper is the signature bug this codebase pattern produces.
- [ ] Every tunable value is a `[Parameter]`; no magic literals in trading logic. `Group` is set so the parameter panel is navigable.
- [ ] Enum-typed parameters exist where discrete choice is intended (moving-average type, rounding mode) so the optimiser can sweep them.
- [ ] Header comment documents the strategy and a fix log; commented-out legacy code is removed, not left inline.

## Output Format

Report findings as:
```
[SEVERITY] file.cs:line — finding — suggested fix
```
Severities: `BLOCKER` (live-misbehavior / real-money risk), `MAJOR` (hidden failure / silent wrong path), `MINOR` (readability / hygiene), `INFO` (observation). End with a one-line pass/fail recommendation.

## Reference

- Skills that encode each area: `ctrader-cbot-basics` (lifecycle, bar indexing, translation table), `ctrader-order-execution` (pips/`TradeResult`/`ErrorCode`/netting), `ctrader-risk-management` (sizing, breakers), `ctrader-performance-tuning` (LINQ, print, chart churn), `ctrader-backtesting-optimization` (validating the fixes before believing them).
- Companion MQL4 skill: `mql4-code-review` (the MT4 twin of this checklist).
