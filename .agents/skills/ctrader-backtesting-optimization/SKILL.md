---
name: ctrader-backtesting-optimization
description: "Use when setting up cTrader Algo backtests or optimisations, interpreting results, or tuning cBot parameters. Covers the Backtesting tab (period slider, starting capital, commission per million units, Visual mode), 1-min bar fixed-spread vs tick-data historical-spread data modes, the Optimisation tab (parameter checkboxes, Grid vs Genetic algorithm, maximise/minimise criteria, multi-criteria fitness), custom GetFitness(GetFitnessArgs) with a minimum-trade guard, walk-forward and out-of-sample validation, overfitting avoidance, metric interpretation, cTrader CLI for CI, realism gaps, and the demo-before-live checklist. Trigger on Test, Optimise, 'is it overfitted', backtest results, or a curve-fit concern."
metadata:
  author: opencode
  version: "1.0.0"
---

# cTrader Backtesting & Optimisation

## Core Principle

**A backtest that looks good is the start, not the finish.** The goal is a strategy that survives unseen data. Anything that only fits the past is worthless — and with a good optimiser, curve-fitting is the default outcome, not the exception.

## Backtest Setup (Backtesting tab)

| Setting | Guidance |
| --- | --- |
| **Period** | Cover several regimes — trend up, trend down, range. Minimum several hundred trades for statistical significance. 10 winners is luck; 500+ is evidence. |
| **Starting capital** | Match the intended account. A cBot sized for $10k and tested on $1k produces different results. |
| **Commission** | Per million units traded. **Leave it non-zero.** Zero-commission backtests systematically overstate grid and high-frequency strategies. |
| **Data** | The single most consequential setting — see below. |
| **Visual mode** | Off for throughput, on to inspect specific behaviour. Invaluable for debugging why a trade fired. |

### Data mode — the one that decides whether results mean anything

| Mode | Spread source | Use for |
| --- | --- | --- |
| **1-min bar** | **Fixed constant** you set | Fast iteration during optimisation sweeps |
| **Tick data** | **Historical** spread per tick | Final verification — the honest number |

**A fixed spread makes a spread-sensitive strategy look far better than it is.** A cBot with a `MaxSpreadPips` filter or any edge thinner than the real spread will pass on 1-min bars and bleed live. The recommended workflow: sweep parameters cheaply on 1-min bar, then **re-verify every survivor on tick data** before believing it. Never report a 1-min bar result as the strategy's performance.

## Optimisation (Optimisation tab)

- **Parameter selection** — tick the checkbox next to each `[Parameter]` you deliberately want varied. Everything unchecked is held fixed. `TimeFrame` is available for all cBots automatically. Un-tickable parameters (Label, AccessRights-style config) should not be swept.
- **Algorithm:**
  - **Grid** — exhaustive, every combination. Accurate and resource-intensive. Use when the range is small and you need certainty.
  - **Genetic** — evolutionary; each pass is an individual, parameters are genes, criteria score is fitness. Far faster for wide ranges. Default choice.
- **Criteria** — choose what to maximise (net profit, profit factor) and what to minimise (max drawdown, duration). Multiple criteria are combined: **product of values to maximise ÷ product of values to minimise**, then ranked.
- **Autoselect the best pass** — applies the winning parameter set to the instance. **Do not skip the validation step below regardless.**

## Custom Fitness Functions

Override `GetFitness` when the built-in criteria optimise the wrong thing. Most common real need: **reward profit per unit of drawdown taken.**

```csharp
protected override double GetFitness(GetFitnessArgs args)
{
    // Guard against statistical bias on low sample sizes
    if (args.TotalTrades < 30)
        return double.MinValue;

    double netProfit = args.Profit;
    double maxDrawdownPercent = args.MaxDrawdownPercent;

    if (maxDrawdownPercent <= 0) return double.MinValue;

    return netProfit / maxDrawdownPercent;
}
```

**The `double.MinValue` guard is not optional.** Without it, the optimiser will happily select a parameter set that made 3 trades and won all 3 — a 2000% return on a sample size that proves nothing. Low-sample passes must be made unattractive, not merely unlikely to be chosen.

`GetFitnessArgs` exposes the pass statistics (total/winning/losing trades, net profit, drawdown, duration, and the parameter values). Use it to reject degenerate passes: too few trades, zero losses on a large sample (suspicious), or a maximum-drawdown of 0 (means it never risked anything).

## Validation Protocol

1. **Optimise on an in-sample window only.** Record the date range.
2. **Walk-forward:** optimise on window 1, test on the immediately following window 2, roll forward, repeat. Consistency across windows matters more than any single peak.
3. **Reserve an out-of-sample period** the optimiser never sees. Run it **once**, at the end. If you tune in response to its result, it is no longer out-of-sample.
4. **Re-verify on tick data**, not 1-min bar.
5. **Check neighbourhood stability.** The best pass should be surrounded by passes with similar results. A sharp isolated spike in the optimisation surface is curve-fitting, not an edge.

## Metrics That Matter

| Metric | Reading |
| --- | --- |
| **Profit factor** | `Gross profit / Gross loss`. Above ~1.5 is robust; below ~1.3 is marginal. |
| **Max drawdown (absolute + %)** | Must fit the actual risk tolerance and, for prop accounts, the firm's max-DD rule. |
| **Win rate** | Meaningless alone. A 35%-win, 3R strategy can be excellent. |
| **Reward:risk** | Average win vs average loss. Verify it matches the intended SL/TP spec. |
| **Total trades** | Low counts mean unreliable statistics. Below ~100, treat every metric as noise. |
| **Equity curve shape** | Smooth rising beats spiky. Check the Equity tab for long flat stretches and sudden cliffs. |
| **Longest losing streak** | Model the max consecutive losses and confirm the circuit breakers survive it. |
| **Exposure / average duration** | A strategy holding for hours has different "open trade" risk than one holding minutes. |

**Always read the Log tab.** Rejected orders, `InvalidStopLossTakeProfit`, `BadVolume`, or `NoMoney` errors in the backtest are the cBot's own bugs surfacing — a "profitable" result produced by orders that silently failed is not a result at all.

## cTrader CLI — Backtests in CI

cTrader CLI (5.10+) runs the same backtest/optimisation engine headlessly, which makes regression-testing a cBot part of a pipeline. Local edition only.

```
ctrader-cli build     <algo.algo>
ctrader-cli metadata  <algo.algo>        # list optimisable property names
ctrader-cli backtest  <algo.algo> --symbol=EURUSD --period=2020-01-01/2024-01-01
ctrader-cli optimize  <algo.algo> --params=params.json --auto-select-best
```

A params file declares the sweep. The base timeframe must come from `--timeframe` or the file; `optimize` takes `--timeframe`, **not** `--period` (passing `--period` is rejected). The parameters file is authoritative — parameters left out entirely are reset to defaults, not held.

```json
{
  "parameters": [
    { "Name": "FastPeriod", "Optimize": true, "Min": "5", "Max": "30", "Step": "5" },
    { "Name": "RiskPercent", "Optimize": true, "Values": ["0.5", "1.0", "2.0"] },
    { "Name": "Label", "Optimize": false, "Value": "CI" }
  ]
}
```

Get property names from `metadata`, not from the parameter *display* label. A name not present in the cBot metadata is logged and skipped, and the run continues with the rest.

## Realism Gap — Backtest vs Live

Backtests do **not** model: partial fills, network latency, requotes, or spread spikes during news. Specifically:
- Add a **spread filter** parameter and verify it triggers in tick-data runs.
- Live slippage differs from modelled fill. Prefer `ExecuteMarketOrder` defaults over assuming your fill price.
- **cTrader has no `IsTradeAllowed`/`IsTradeContextBusy`** to handle, but it does have transient `ErrorCode`s (`Disconnected`, `Timeout`) that never occur in backtest. Handle them — see `ctrader-order-execution`.
- **Check your own code for look-ahead bias.** Reading `Last(0)` in `OnTick`/`OnBar` uses the forming bar, which does not exist at decision time in live. Prefer `OnBarClosed()` for signals. See `ctrader-cbot-basics`.
- Session, weekend, and holiday gaps: `BarClosed` is an alias of `BarOpened`, so a Friday bar "closes" on Monday's first tick. A cBot with a Friday-close guard and a Monday-open entry must be tested across that boundary.

## Demo-Before-Live Checklist

1. Tick-data backtest, walk-forward validated, out-of-sample confirmed once, PF > 1.5, drawdown within tolerance, 100+ trades, Log tab clean.
2. **Run on demo with live data for at least two weeks.** Compare the demo equity curve to the backtest curve — divergence is your first real signal of a modelling bug.
3. **Verify the circuit breakers actually fire in demo.** Do not assume. Force a losing streak in a backtest and confirm the guard halts entries.
4. Confirm broker specifics: min/max/step volume, stop distance, account mode (hedging vs netted), commission, and spread behaviour at news times.
5. Only then a small live pilot matching the risk plan.

## Reference

- `ctrader-risk-management` (the breakers that must survive the test)
- `ctrader-cbot-basics` (bar-close patterns, look-ahead avoidance)
- `ctrader-code-review` (look-ahead audit before you trust any curve)
- Companion MQL4 skill: `mql4-backtesting-optimization`.
