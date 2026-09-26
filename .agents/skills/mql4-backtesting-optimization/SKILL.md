---
name: mql4-backtesting-optimization
description: "Use when setting up MetaTrader 4 Strategy Tester runs, interpreting backtest reports, or optimizing MQL4 Expert Advisor parameters. Covers modeling modes (Every tick vs Open prices), walk-forward and out-of-sample validation, overfitting avoidance, metric interpretation (profit factor, max drawdown, Sharpe, trade count), look-ahead/spread/slippage realism, and the demo-before-live checklist. Trigger on Test, Optimize, 'graph', 'is it overfitted', backtest screenshots."
metadata:
  author: opencode
  version: "1.0.0"
---

# MQL4 Backtesting & Optimization

## Core Principle

**A backtest that looks good is the start, not the finish.** The goal is a strategy that survives unseen data — anything that only fits the past is worthless.

## Test Setup

- **Symbol + timeframe + date range** must be meaningful: at least several market regimes (trend up / trend down / range), minimum several hundred trades for statistical significance. 10 winning trades might be luck; 500+ provide evidence.
- **Modeling mode: "Every tick" (or "Every tick based on real ticks" when available).** Lower-quality modes (Open prices) produce misleading results that never match live. Decide bar-close vs intra-bar execution deliberately — bar-close is the reproducible default; intra-bar (scalping/trailing) makes every test dependent on tick quality.
- **Run on a clean setup**: no other EAs/indicators on the chart, realistic spread and slippage settings. Remove other scripts that add noise.
- **MQL4 note:** the tester models ticks; the EA also runs `OnTick` only when a modeled tick arrives — weekend/holiday gaps are natural.

## Metrics That Matter

| Metric | Threshold / meaning |
| --- | --- |
| **Profit factor** | `GrossProfit / GrossLoss`. >1.5 indicates robust profitability; below ~1.3 is marginal. |
| **Max drawdown** | Must be acceptable to the actual risk tolerance. Watch **relative-to-broker-max-load** DD too, and day-by-day curves. |
| **Win rate** | Meaningless alone — a low win-rate high reward:risk strategy can be excellent. |
| **Reward:risk** | Average win vs average loss; check it matches your SL/TP spec. |
| **Sharpe / risk-adjusted** | Higher = better for the risk taken. |
| **Trade count** | The number of trades matters for trust: low counts = unreliable statistics. |

Also inspect the report's error/trade logs: `130` (invalid stops), overly high `requotes`, or entries that don't match your intended execution path.

## Optimization Protocol

1. **Define parameter ranges + steps** for the inputs you deliberately want to vary (periods, thresholds). Use `sinput` for switches you never want the optimizer touching.
2. **Run complete or genetic optimization.** The genetic algorithm is much faster for broad ranges.
3. **Validation is mandatory:**
   - **Walk-forward analysis** — optimize on one in-sample window, test on the next out-of-sample window, roll forward.
   - **Out-of-sample period** — reserve data the optimizer never saw and test only once at the end.
4. **Beware overfitting / curve-fitting.** Optimized parameters that work perfectly on history and fail live are the norm, not the exception. Favor simple, few-parameter strategies with sensible economic logic. If the profit collapses off the top of the optimization grid, it is fragile.

## Realism Gap — Backtest vs Live

Backtests do **not** model: requotes, partial fills, network latency, or spread widening during news. Plan for the gap:
- Add a **spread filter** input for high-spread periods.
- Expect **slippage** to differ from test settings.
- Handle **`IsTradeAllowed()` / `IsTradeContextBusy()`** in live code even though they always pass in the tester.
- Check for **look-ahead bias** in your own code: any use of `iMA(...,0)` or `Close[0]` in a decision = future data in "bar-close" mode.

## Demo-Before-Live Checklist

1. Backtest "Every tick", walk-forward validated, out-of-sample confirmed — profit factor >1.5, DD within tolerance, 500+ trades.
2. Run on **demo with live data for at least 2 weeks**; compare demo results to the backtest curve.
3. Confirm circuit breakers, daily loss limits, and cooldowns actually trigger in demo (don't assume).
4. Check broker constraints in demo: min/max lots, stop level, margin mode, spread behavior during news.
5. Only then consider a small live pilot matching your risk plan.

## Reference

- Companion skills: `mql4-risk-management` (breakers that must survive the test), `mql4-ea-basics` (bar-close patterns), `mql4-code-review` (look-ahead audits).