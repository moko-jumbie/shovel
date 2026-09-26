---
name: mql4-code-review
description: "Use when reviewing or auditing existing MQL4 MetaTrader 4 Expert Advisors and indicators before deployment or as part of development. A checklist-driven reviewer: dead code paths, unchecked Order* return values, pips-vs-points confusion, stop-level compliance, trade-permission checks, session/time filtering correctness, chart-object cleanup, look-ahead bias, performance hotspots, and state restoration across restarts. Trigger on 'review this EA', 'is this EA safe', 'check my code', or pre-live audit."
metadata:
  author: opencode
  version: "1.0.0"
---

# MQL4 Code Review

## Core Principle

**Review for behaviors, not just syntax.** Most live EA failures come from incomplete handling of edge cases and silent failure paths, not from compile errors. Run this checklist top to bottom and report each finding with a file:line reference, severity, and concrete fix.

## Checklist

### 1. State & Lifecycle
- [ ] `OnInit` validates inputs and returns `INIT_FAILED`/`INIT_PARAMETERS_INCORRECT` on bad config.
- [ ] `OnDeinit` cleans up: `ObjectsDeleteAll(0, prefix)`, `EventKillTimer()`, released handles.
- [ ] State that must survive an input edit / restart (counters, day-start balance, streak, pending modifications) is restored — either rebuilt from order history or read from persistent storage. **Stuck-state recovery**: on init, scan for orders whose SL/TP differ from stored/pending values (a disconnect during `OrderModify` leaves positions stuck).

### 2. Order Execution
- [ ] **Every** `OrderSend/OrderModify/OrderClose` return value is checked.
- [ ] Every failure logs `GetLastError()` **with context** (the values attempted).
- [ ] Transient rejections retried with `RefreshRates()`; permanent ones fail loudly (see `mql4-order-execution`).
- [ ] `IsTradeAllowed()` / `IsTradeContextBusy()` checked before attempts (not just at init).
- [ ] All prices `NormalizeDouble(d, Digits)`; all lots snapped to `MODE_LOTSTEP` within `MINLOT..MAXLOT`.
- [ ] SL/TP distance validated against `MODE_STOPLEVEL` (error-130 prevention).
- [ ] Order scans iterate backwards and filter by `OrderSymbol()` **and** `OrderMagicNumber()`.

### 3. Units & Math
- [ ] **Pips vs points**: any comparison of `MarketInfo(Symbol(), MODE_SPREAD)` (returned in points) against a "pips" input is off by 10× on a 5/3-digit account. Convert: `pips = points / 10` (5/3-digit) or `pips = points` (4/2-digit).
- [ ] `Point` scaling (`.3 d/igits`) handled — `Multiplier` for 5-digit used where needed.
- [ ] Integer division pitfalls; `MathRound` vs casting for lot/percent rounding.

### 4. Logic & Dead Code
- [ ] **No dead code paths**: functions defined but never called (e.g., a `TrackOrderClose()` that is never invoked — the loss streak then never updates live). Grep for each helper and confirm a call site.
- [ ] Duplicate code blocks (copy-pasted comment headers, repeated session logic) — one source of truth per rule.
- [ ] Consecutive-loss / cooldown counters increment on real closes, not only at init from history.
- [ ] Conflicting signals on the same bar have an explicit rule (first-wins + log, or skip).
- [ ] Look-ahead bias: strategy decisions never use `Close[0]`/`iXXX(...,0)` when operating bar-close; use `index >= 1`.

### 5. Risk & Protection
- [ ] Position sizing uses risk% of equity (not fixed lots) or is an intentional choice.
- [ ] Circuit breakers present and wired: daily loss limit, consecutive-loss guard, cooldown, optional DD halt. Verify their triggers actually gate new entries (not just annotated on the HUD).
- [ ] Risk-reduction multiplier logic is monotonic and resets on a win/new day.

### 6. Session & Time Filtering
- [ ] Session definitions match the broker timezone (comment states UTC offset) and the toggles combine correctly (OR across enabled sessions, all-off = trade everything).
- [ ] Session logic in the trading path matches the UI path (a shared function, not duplicated logic that can drift).
- [ ] Friday/weekend close logic uses `DayOfWeek()` + `TimeHour()` correctly.

### 7. Performance
- [ ] No `ArrayResize()` inside per-tick loops (allocate once).
- [ ] Indicator values fetched into arrays rather than repeated `iMA()`/`iCustom()` calls in loops.
- [ ] `OrdersTotal()`/`ArraySize()` not evaluated in loop conditions.
- [ ] No `RefreshRates()` inside tight loops.
- [ ] No file writes per tick.

### 8. Inputs & Hygiene
- [ ] No magic literals in trading logic — everything tunable is an `input`.
- [ ] `#property strict` present.
- [ ] Duplicate/legacy commented-out code removed or clearly marked (FIX-style log at top preferred).

## Output Format

Report findings as:
```
[SEVERITY] file.mq4:line — finding — suggested fix
```
Severities: `BLOCKER` (live-misbehavior/risk), `MAJOR` (hidden failure/silent wrong path), `MINOR` (readability/hygiene), `INFO` (observation). End with a one-line pass/fail recommendation.

## Reference

- Skills that encode each area: `mql4-ea-basics`, `mql4-order-execution`, `mql4-risk-management`, `mql4-performance-tuning`, `mql4-backtesting-optimization`.