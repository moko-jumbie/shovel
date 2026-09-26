---
name: mql4-performance-tuning
description: "Use when MQL4 MetaTrader 4 Expert Advisors, custom indicators, or scripts are slow in the Strategy Tester or cause chart lag on live ticks. Covers GetTickCount() manual profiling (MQL4 has no built-in profiler), loop and invariant optimization, caching indicator values instead of repeated iMA()/iCustom() calls, order-scan caching, array pre-allocation and memory management, file-write batching, and avoiding RefreshRates() misuse. Trigger on slow backtests, CPU spikes, freeze, or 'speed up this EA'."
metadata:
  author: opencode
  version: "1.0.0"
---

# MQL4 Performance Tuning

## Core Principle

**Measure before you optimize.** MQL4 has no built-in profiler, so wrap suspect sections with `GetTickCount()` timing, record the average over several ticks, and re-measure after each change. Without a baseline you are guessing.

## Manual Profiling (MQL4)

```mql4
int t0 = GetTickCount();
// ... suspect code block ...
int elapsed = GetTickCount() - t0;
PrintFormat("block took %d ms", elapsed);
```

Run the EA for several ticks, note the average. Apply an optimization, re-measure, keep what helps. Focus on the hottest functions first — typically `OnTick()` indicator reads, order scanning, and file I/O.

## Step 1 — Optimize Loops

- **Move invariant calculations out of loops.** Anything that doesn't depend on the loop variable should be computed once.
- **Cache `OrdersTotal()` / `ArraySize()`** evaluated in a loop condition into a local `int total` before the loop — re-evaluating every iteration is wasted work.
- **Don't call `Symbol()`, `Period()`, `Point` inside loops.** Capture them in locals at the top of `OnTick()`.
- Prefer integer math over `double` for counters and indices; avoid unnecessary type conversions.

## Step 2 — Minimize Indicator Recalculations

- **Never call `iMA()` / `iCustom()` repeatedly inside a loop.** Each call internally recalculates the indicator. Fetch once into an array, then read the array:

  ```mql4
  double ma[];
  ArraySetAsSeries(ma, true);
  for(int i = 0; i < 20; i++)
     ma[i] = iMA(Symbol(), Period(), FastPeriod, 0, MODE_EMA, PRICE_CLOSE, i); // one pass, cached
  ```

- **Custom indicators:** use `IndicatorCounted()` in `start()` (the `prev_calculated` pattern) to only recalculate new bars. This single change can cut CPU 90% on historical backtests.

  ```mql4
  int start()
    {
     int counted = IndicatorCounted();
     if(counted < 0) return(-1);
     int limit = Bars - counted - 1;
     for(int i = limit; i >= 0; i--) { /* new bars only */ }
     return(0);
    }
  ```

- **In `OnTick`, avoid recomputing the same heavy value twice** (e.g., HMA computed per-call with fresh buffer allocation each tick) — cache the result in a global and reuse it in both the trading path and the UI path.

## Step 3 — Efficient Order Scanning

- Scanning all open orders on every tick is a common bottleneck. Cache open tickets in an array **at the start of each tick**, iterate the array, then refresh. Do NOT cache across ticks — order state changes.
- Batch `RefreshRates()`: call it **once per tick** or only right before critical price checks, never inside tight loops — every call re-quotes and skips ticks.

## Step 4 — Memory & Resources

- **Pre-allocate arrays** at init with `ArrayResize()` to the maximum expected size; repeated `ArrayResize()` inside loops fragments memory and slows the EA over hours.
- **Release handles** in `OnDeinit()`: chart objects, indicator handles, files. Leaks cause gradual slowdowns and "too many handles" errors.
- **Don't over-allocate indicator buffers** (`SetIndexBuffer`) — allocate only what you need.
- **Group related globals into `struct`s** instead of a growing pile of global variables.

## Step 5 — File I/O

- **Batch writes:** if you log trade data, build a string buffer and flush to a file every N ticks, not every tick.

## Verification Protocol

1. Profile → note baseline of the top 3 hot functions.
2. Apply loop/array optimizations to those functions first.
3. Re-measure; keep only real wins.
4. Test in Strategy Tester with "Every tick" modeling and compare backtest speed before/after.
5. Run on demo 24h to verify no new lag or misbehavior.

## Reference

- Companion skills: `mql4-ea-basics` (`#property strict` type-checks that catch runtime slowdowns early), `mql4-code-review` (spot the `ArrayResize`-in-loop and repeated-indicator-call anti-patterns).