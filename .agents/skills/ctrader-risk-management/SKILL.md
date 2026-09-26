---
name: ctrader-risk-management
description: "Use when implementing or reviewing risk management in cTrader cBots: position sizing via the native Symbol.VolumeForFixedRisk/VolumeForProportionalRisk/AmountRisked/PipsForFixedRisk APIs, RoundingMode.Down vs ToNearest, the min-volume risk-creep check, volume-in-units normalisation, Account.Equity vs Balance, margin pre-checks with GetEstimatedMargin, ATR-based SL/TP, circuit breakers (daily loss limit, consecutive-loss guard, loss cooldown, drawdown halt), ATR trailing stops, break-even, and account-protection logic. Trigger on CalculateVolume, risk percent inputs, or any 'max loss / max drawdown' requirement."
metadata:
  author: opencode
  version: "1.0.0"
---

# cTrader Risk Management

## Core Principle

**Risk management decides survival long-term more than entry signals do.** A production cBot must protect the account before it chases profit, and every risk rule belongs behind a named `[Parameter]` so the optimiser and the operator can tune it.

## Use the Native Risk APIs — Do Not Hand-Roll the Math

cTrader 4.2+ ships risk-based sizing on `Symbol`. This is a genuine upgrade over MT4's `MODE_TICKVALUE`/`MODE_TICKSIZE` arithmetic: it handles account-currency conversion, symbol step/min/max, and commission for you. Re-deriving it by hand is how currency-conversion bugs get shipped.

```csharp
// Risk a fixed cash amount over a known pip stop -> volume in units
double units = Symbol.VolumeForFixedRisk(riskAmount, stopLossInPips);

// Risk a fraction of equity/balance -> volume in units
double units2 = Symbol.VolumeForProportionalRisk(ProportionalAmountType.Balance, 1.0, stopLossInPips);

// Inverse: how much am I risking with this volume and stop?
double amount = Symbol.AmountRisked(units, stopLossInPips);

// Inverse: what stop (pips) risks exactly this amount with this volume?
double pips = Symbol.PipsForFixedRisk(riskAmount, units);

// Margin needed before you commit
double margin = Symbol.GetEstimatedMargin(units);
```

`ProportionalAmountType` selects the base — `Balance`, `Equity`, or `MarginLevel`-style variants. Use **`Equity`** for live risk so open losses shrink position size; use `Balance` for a stable, reproducible backtest. Pick one and document it in a comment.

If you must hand-roll (legacy, or an exotic symbol), the MT4 formula still applies with these substitutions:

```
units = (riskAmount / (slPips * pipValuePerUnit))
pipValuePerUnit = Symbol.TickValue / Symbol.TickSize * Symbol.PipSize
```

Then `units = Symbol.NormalizeVolumeInUnits(units, RoundingMode.Down)`.

## Rounding: `RoundingMode.Down`, Never `ToNearest`

`RoundingMode` has `ToNearest`, `Up`, `Down`.

```csharp
Symbol.NormalizeVolumeInUnits(raw, RoundingMode.Down);   // correct for a risk cap
Symbol.NormalizeVolumeInUnits(raw, RoundingMode.ToNearest); // can round UP past your cap
```

`ToNearest` can round **up**, meaning the actual position risks **more** than the parameter promised. In an optimiser that silently corrupts the risk model: the optimiser is varying "Risk %" while the real risk drifts upward. A 1% strategy becomes 1.4% and the backtest never tells you. Use `Down` for every risk-derived volume.

## The Min-Volume Risk-Creep Check (most-skipped step)

When the correct risk-based volume is **smaller than the symbol's minimum**, normalisation floors it at `Symbol.VolumeInUnitsMin` — and the trade now risks **far more** than intended. This happens on small accounts, wide ATR stops, and low-risk parameters, and it is completely silent.

```csharp
double raw = Symbol.VolumeForFixedRisk(riskAmount, slPips);
double vol = Symbol.NormalizeVolumeInUnits(raw, RoundingMode.Down);

if (vol < Symbol.VolumeInUnitsMin)
{
    // Either skip the trade, or shrink the stop, or log loudly. Do NOT silently trade min.
    Print($"Risk-derived vol {raw} below {Symbol.VolumeInUnitsMin} min - trade would over-risk");
    return 0;
}
if (raw > Symbol.VolumeInUnitsMax)
{
    Print($"Risk-derived vol {raw} exceeds {Symbol.VolumeInUnitsMax} max - cap engaged");
    vol = Symbol.NormalizeVolumeInUnits(Symbol.VolumeInUnitsMax, RoundingMode.Down);
}
```

Return `0` and let the caller skip. A `Math.Max(VolumeInUnitsMin, ...)` clamp with no log is a bug wearing a disguise.

## Equity vs Balance vs Free Margin

- `Account.Equity` — balance + floating P/L. Correct basis for live risk sizing.
- `Account.Balance` — realised only. Stable across a backtest run.
- `Account.Margin` / `Account.FreeMargin` — for pre-trade affordability checks.

Size on Equity, gate entries on free margin. Use `Symbol.GetEstimatedMargin(units)` before committing; a `NoMoney` rejection mid-signal wastes the setup.

## SL/TP Placement with ATR

Let volatility size the trade, not a fixed pip count:

```csharp
private AverageTrueRange _atr;

// OnStart
_atr = Indicators.GetAverageTrueRange(AtrPeriod);

// on a closed bar
double atr = _atr.Result.Last(0);
if (atr <= 0) return;
double slPips = Symbol.NormalizePips(atr / Symbol.PipSize * SlAtrMultiplier);
double tpPips = Symbol.NormalizePips(atr / Symbol.PipSize * TpAtrMultiplier);
```

- `SlAtrMultiplier` ~1.5–2, `TpAtrMultiplier` ~3–5 gives asymmetric reward:risk. Win rate alone tells you nothing.
- **cTrader validates stop distance server-side** and returns `InvalidStopLossTakeProfit` — there is no `MODE_STOPLEVEL` to read. Two defences: (a) pick an ATR multiplier that cannot produce sub-minimum stops, (b) treat that error code as a signal to widen the stop or skip, not to retry. See `ctrader-order-execution`.
- A very wide ATR stop shrinks volume via the risk API automatically. That is the system working.

## Circuit Breakers

| Breaker | Pattern |
| --- | --- |
| **Daily loss limit** | Track `_dayStartBalance`, reset when `Server.Time.Date` changes. Breach when `(dayStartBalance - Account.Equity) >= dayStartBalance * MaxDailyLossPercent / 100`. Stop **new entries** for the day. |
| **Consecutive-loss guard** | Count net losses in `OnPositionClosed`. Past `MaxConsecLosses`, multiply risk by `ReducedRiskMult` (e.g. 0.5). A win resets the streak. |
| **Loss cooldown** | After a losing close, skip entries for `ReentryCooldownBars` bars. Track with a bar counter, not wall-clock. |
| **Max drawdown halt** | Track peak equity in `OnTick`/`OnBarClosed`. If equity falls > `MaxDrawdownPercent` below peak, stop trading and print loudly. |
| **Total exposure cap** | Sum `NetVolume` across your label. Skip new entries when exposure exceeds `MaxConcurrentPositions * RiskPercent`. |

**The integration bug to avoid:** a consecutive-loss counter rebuilt from history in `OnStart` but never incremented live. Wire it into `OnPositionClosed(TradeResult)` — that handler fires for every close, whether it was your SL, your TP, or a manual close. Using the polling path (`if (position.LosingTrade)` inside a tick loop) instead is the usual root cause of a guard that "does nothing".

```csharp
protected override void OnPositionClosed(TradeResult result)
{
    if (result.Position.Label != Label) return;      // never count someone else's close
    if (result.Position.NetProfit < 0)
    {
        _consecutiveLosses++;
        _barsSinceLastLoss = 0;
    }
    else
    {
        _consecutiveLosses = 0;
    }
}
```

Day rollover also needs the same care: a cBot restarted at 16:00 must reset `_dayStartBalance` to the *current* balance, not restore yesterday's.

## Managing Open Positions

- **ATR trailing:** activate only after price has moved `activationDistance` in your favour, then ratchet the stop to `current ∓ trailCushion`. **Only ever tighten.** Compare against the existing stop before calling `ModifyPosition` — a no-op modify still costs a server round-trip and can be rate-limited.
  ```csharp
  double targetSl = Math.Round(Symbol.Bid - trailDist, Symbol.Digits);
  if (position.StopLoss is null || targetSl > position.StopLoss.Value)
      CheckResult(ModifyPosition(position, targetSl, position.TakeProfit, ProtectionType.Absolute));
  ```
- **Break-even:** once past a threshold, move SL to entry ± a buffer that covers spread and commission, not exactly to entry.
- **Throttle management.** Running a trailing loop on every tick fires a modify on every tick that qualifies. Track the last-modified price or time and skip when nothing changed.
- **Reconcile on start.** Positions opened before a restart still exist. On `OnStart`, walk `Positions.FindAll(Label, SymbolName)` and re-establish any intended protection that is missing — a restart during a network blip otherwise leaves a position naked.

## Requirements Elicitation

Settle these before writing a line:
1. Hedging or netted account? Grid and hedging strategies are impossible on netted.
2. Is risk sized on equity or balance?
3. What happens when two signals land on the same bar — first wins, or skip?
4. On terminal disconnect mid-modify, what reconciles the position?
5. Which currency is the account in, and does the symbol quote convert? (The native risk APIs handle this — hand-rolled math does not.)
6. Do circuit breakers stop new entries only, or flatten open ones too?

## Reference

- `ctrader-order-execution` (`RoundingMode`, `ErrorCode` handling, pips normalisation)
- `ctrader-backtesting-optimization` (prove the breakers do not kill the strategy before going live)
- `ctrader-code-review` (the checklist that verifies breakers are actually wired)
- Companion MQL4 skill: `mql4-risk-management`.
