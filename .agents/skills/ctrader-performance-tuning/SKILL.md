---
name: ctrader-performance-tuning
description: "Use when cTrader cBots or indicators are slow in the backtester or optimiser, lag the chart on live ticks, or burn CPU. Covers profiling the cTrader algo child process (Visual Studio attach, dotnet-counters, dotnet-trace), Stopwatch micro-timing inside handlers, eliminating LINQ from OnTick hot paths, Positions/PendingOrders snapshot caching, indicator value caching versus repeated Last(i) reads, event-driven versus OnTick polling design, eliminating per-tick Print and chart-object churn, avoiding Sleep and blocking on ExecuteAsync, and chart-object lifecycle cleanup. Trigger on slow backtests, long optimisation runs, CPU spikes, chart freeze, or 'speed up this cBot'."
metadata:
  author: opencode
  version: "1.0.0"
---

# cTrader Performance Tuning

## Core Principle

**Measure before you optimize.** cTrader runs algos in **separate child processes** since v4.2, which means you can attach a real .NET profiler instead of hand-rolling timing. Take a baseline, fix the top three hotspots, re-measure, and keep only real wins. Without a baseline you are guessing.

## Step 0 — Profiling the Algo Process

Because each cBot/indicator is its own process, the usual .NET tooling works against it directly.

```powershell
# find the algo child process
Get-Process cAlgo* ,*cTrader* | Select-Object Id, ProcessName, CPU, WS

# live counters (CPU, allocations, GC, threadpool)
dotnet-counters monitor --process-id <PID>

# allocation and CPU sampling trace
dotnet-trace collect --process-id <PID> --profile cpu-sampling
dotnet-trace report <trace.nettrace> > report.html
```

Or attach Visual Studio to the child process while the cBot is running (Debug → Attach to Process). This is a real advantage over MT4, where `GetTickCount()` was the only option.

**Optimiser runs are the best profiling workload** — a genetic sweep executes your code thousands of times, so any hot spot is magnified and obvious in a CPU-sampling trace.

For quick iteration without a profiler, time the handler itself:

```csharp
private readonly Stopwatch _sw = new Stopwatch();

protected override void OnTick()
{
    _sw.Restart();
    // ... work ...
    _sw.Stop();
    _lastTickMs = _sw.Elapsed.TotalMilliseconds;
}
```

Print the running average, not per-tick values. One long GC pause or one order rejection will otherwise dominate the number you are staring at.

## Step 1 — The Biggest Win: Get LINQ Out of `OnTick`

This is the cTrader equivalent of MQL4's "no `ArrayResize` in a loop" rule, and it is the most common avoidable cost.

```csharp
// BAD - allocates an enumerator and iterates the whole collection on EVERY tick
protected override void OnTick()
{
    foreach (var pos in Positions.Where(p => p.SymbolName == SymbolName && p.Label == Label))
        ApplyTrailing(pos);
}
```

`Positions` is a live collection. `.Where(...)` allocates a new iterator per call, and enumerating it touches every position on the account — including other algos' and the user's. On a busy account with several cBots running, this is pure waste repeated thousands of times a day.

```csharp
// GOOD - scan once per tick into a reusable buffer, or use the indexed lookup
private Position[] _mine = Array.Empty<Position>();

protected override void OnTick()
{
    _mine = Positions.FindAll(Label, SymbolName);   // single indexed lookup, no LINQ
    for (var i = 0; i < _mine.Length; i++)
        ApplyTrailing(_mine[i]);
}
```

`Positions.FindAll(label, symbolName)` / `Positions.Find(label, symbolName, tradeType)` are purpose-built lookups. Reach for `.Where()`/`.Select()`/`.Any()` only in cold paths — `OnStart`, `OnStop`, per-bar signal logic — never in a per-tick management loop.

## Step 2 — Move Work Off `OnTick`

`OnTick` fires on every bid/ask change: hundreds of times per second on liquid FX, thousands on gold during London. Anything that only needs to happen once per bar should not run there.

| Need | Wrong | Right |
| --- | --- | --- |
| Signal detection | `OnTick` + `Last(1)/Last(2)` comparison | `OnBarClosed()` |
| Crossover detection | recompute each tick | compute once per bar, cache the bool |
| ATR / indicator reads | `_atr.Result.Last(1)` every tick | read once per bar into a field |
| Custom indicator math | recompute loop every tick | compute once per bar, reuse in both the trading and UI paths |
| Time / session guards | `Server.Time` string parsing per tick | compare cached hour ints, or check once per bar |

The classic offender: a hand-rolled HMA/WMA that recomputes nested weighted sums several times per bar. Compute it once, store the value, and let the trailing-stop and HUD paths read the cached field.

## Step 3 — Throttle Management Operations

A per-tick trailing stop that only modifies when the target actually moved still re-evaluates every tick. Two cheap guards:

```csharp
private double _lastSlPrice;

// inside the trailing update
if (Math.Abs(targetSl - _lastSlPrice) < Symbol.TickSize) continue;   // nothing to do
var r = ModifyPosition(position, targetSl, position.TakeProfit, ProtectionType.Absolute);
if (r.IsSuccessful) _lastSlPrice = targetSl;
```

Also compare against the position's **current** stop before calling — a modify that changes nothing is a wasted server round-trip and can be rate-limited. See `ctrader-risk-management`.

## Step 4 — Logging Discipline

`Print()` in `OnTick` is a performance and readability disaster: string formatting, a log-panel update, and I/O on every single tick. It also floods the Log tab so badly that you can no longer read the errors that matter.

- Never `Print` per tick. Gate it behind a `[Parameter("Verbose", DefaultValue = false)]` flag.
- Log **events**: order opened/failed, position closed, circuit breaker tripped, session change. These are rare and are what you will actually read.
- Build one string and print once rather than several `Print` calls — each call is a separate log entry.
- If you must log per-tick for debugging, log to a file with buffering and flush every N ticks, never to the Log panel.

## Step 5 — Chart Objects and UI

- `Chart.DrawText` / `DrawIcon` / `DrawRectangle` are tracked by name but **persist with the chart**. Adding them in a per-tick refresh loop creates objects faster than cTrader cleans them up. Create once in `OnStart`, **update in place** (pass the same name), and remove every one in `OnStop`.
- Guard all `OnStop` cleanup with a `try/catch` — one throw mid-cleanup leaves the rest leaked.
- WPF controls (`Canvas`, `Grid`, `TextBlock` on `ChartArea`) are heavier than `Chart.Draw*`. Use them for real panels, not for a single status line.
- HUD refresh belongs on a `Timer` (e.g. 500 ms), not on every tick.
- Custom indicators: `OnBarClosed` beats per-tick recalculation for anything that only depends on closed bars.

## Step 6 — Blocking and Concurrency

- `Thread.Sleep` inside a handler blocks that handler. Use `Sleep(ms)` only as an intentional deliberate delay, and never in `OnTick`.
- `ExecuteAsync(...).Wait()` or `.Result` **blocks the algo's thread and can deadlock** — the completion callback needs that thread to run. Use `ExecuteAsync` with a continuation, or just use the synchronous `Execute*` (which cTrader already queues safely).
- `Task.Run` for a polling loop defeats the event-driven model and leaks a thread per instance. If you need a background job, cancel it in `OnStop`.
- `Timer.Start`/`Timer.Stop` — always `Stop()` in `OnStop`, or the callback keeps firing against a torn-down instance.

## Step 7 — Memory

- Reuse buffers: `Array.Empty<T>()` for the initial field, then index-assign rather than reallocating per tick.
- Group related state into a small class or struct instead of a growing pile of fields — it also makes the snapshot/restore pattern cleaner.
- Preallocate lists with a capacity hint when the max size is known and small.
- Watch the `GC` counters from Step 0. A high allocation rate in `OnTick` almost always traces back to LINQ or string interpolation in a loop.

## Verification Protocol

1. Profile against a real backtest or optimiser run. Record the top three hotspots and the total run time.
2. Fix those three only. Resist the urge to refactor cold code.
3. Re-measure the same workload. Keep changes that measurably help; revert the rest.
4. Re-run the backtest and **compare the equity curve to the pre-optimisation curve** — performance work must not change trading behaviour. Any difference means you altered semantics, not just speed.
5. Run on demo for a day and confirm no new lag and no behaviour change.

## Reference

- `ctrader-cbot-basics` (lifecycle events — pick the cheapest one that works)
- `ctrader-code-review` (spots the LINQ-in-`OnTick` and per-tick-`Print` anti-patterns)
- `ctrader-order-execution` (throttling `ModifyPosition` round-trips)
- Companion MQL4 skill: `mql4-performance-tuning` (same principles, `GetTickCount()` era).
