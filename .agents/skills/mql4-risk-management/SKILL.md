---
name: mql4-risk-management
description: "Use when implementing or reviewing risk management in MQL4 MetaTrader 4 Expert Advisors: position sizing (risk-per-trade formula), ATR-based stop/target placement, daily loss limits, drawdown circuit breakers, consecutive-loss guards with risk reduction, loss cooldowns, trailing stops, breakeven, and account-protection logic. Trigger on CalculateDynamicLot, risk inputs, equity/balance guards, or any 'max loss / max drawdown' requirement."
metadata:
  author: opencode
  version: "1.0.0"
---

# MQL4 Risk Management

## Core Principle

**Risk management decides survival long-term more than entry signals do.** A production EA must protect the account before it chases profit. Every risk rule belongs behind named inputs so the optimizer and the operator can tune it.

## Position Sizing — Risk % of Equity

Never a fixed lot for serious trading; size by risk so every trade risks the same fraction of equity.

```
lots = (RiskPercent / 100 * Equity) / (StopLossDistance / TickSize * TickValue)
     = RiskAmount / (TotalTicks * TickValue)
```

MQL4 reference implementation:

```mql4
double CalculateDynamicLot(double slDistance, double riskPercent)
  {
   if(!AutoMoneyManagement)
      return(LotSize);

   double equity    = AccountBalance();
   double riskAmount = equity * (riskPercent / 100.0);
   double tickValue = MarketInfo(Symbol(), MODE_TICKVALUE);
   double tickSize  = MarketInfo(Symbol(), MODE_TICKSIZE);
   double lotStep   = MarketInfo(Symbol(), MODE_LOTSTEP);
   double minLot    = MarketInfo(Symbol(), MODE_MINLOT);
   double maxLot    = MarketInfo(Symbol(), MODE_MAXLOT);

   if(slDistance <= 0 || tickValue <= 0 || tickSize <= 0)
      return(minLot);

   double ticks = slDistance / tickSize;
   double lots  = riskAmount / (ticks * tickValue);
   lots = MathRound(lots / lotStep) * lotStep;   // snap to broker step

   if(lots < minLot) lots = minLot;
   if(lots > maxLot) lots = maxLot;
   return(NormalizeDouble(lots, 2));
  }
```

Constraints to honor: `MODE_MINLOT` (floor — a 0.01 strategy cannot run on a 0.10-minimum broker), `MODE_MAXLOT` (ceiling — relevant for grid/recovery strategies), and `MODE_LOTSTEP`.

## SL/TP Placement with ATR

Let volatility size the trade, not a fixed pip count:

- `slDistance = ATR(period) * SlAtrMultiplier` (e.g., 2x)
- `tpDistance = ATR(period) * TpAtrMultiplier` (e.g., 5x — asymmetric reward/risk)
- For a buy: `SL = Ask - slDistance`, `TP = Ask + tpDistance`; sell mirrors.
- **Always validate against `MODE_STOPLEVEL`** after computing — clamp to `stoplevel * Point` or refuse the trade, otherwise error 130 kills the order at the first gap.

## Circuit Breakers

| Breaker | Implementation pattern |
| --- | --- |
| **Daily loss limit** | Track `gDayStartBalance` (reset when `TimeCurrent()` crosses a new day). Breach when `gDayStartBalance - AccountEquity() >= gDayStartBalance * MaxDailyLossPercent/100`. Stop new entries for the day. |
| **Consecutive-loss guard** | Count consecutive net losses (profit+swap+commission < 0). Past `MaxConsecLosses`, multiply `RiskPercent` by `ReducedRiskMult` (e.g., 0.5). A win resets the streak. |
| **Loss cooldown** | After any losing trade, skip new entries for `ReentryCooldownBars` completed bars. |
| **Max drawdown halt** | If equity falls more than `X%` below peak equity, stop trading entirely until reset. |

**Critical integration bug to avoid:** a consecutive-loss counter that is only rebuilt from order history in `OnInit()` but never incremented live. The streak must update on every closed losing trade — wire your close-tracking function into the order-scan / close logic, not just init.

## Managing Open Positions

- **Trailing stop (ATR-based):** activate only after price has moved `activationDistance` in your favor (`Bid - OrderOpenPrice() > atr * TrailActivation` for a buy), then ratchet the stop to `current - trailCushion`. Only ever move the stop toward profit; never loosen.
- **Breakeven:** when price passes some threshold, move SL to `OrderOpenPrice() ± small buffer`.
- Iterate positions backwards, filter by `OrderMagicNumber()` + `OrderSymbol()`, compare new SL vs current before `OrderModify` to avoid no-op rejects.

## Requirements Elicitation (before coding)

Answer these with the strategy owner before writing a line:
1. What happens if two signals fire on the same bar? (First-wins + log? Skip?)
2. What happens if the terminal disconnects mid `OrderModify`? (Recovery scan in `OnInit`, requeue pending SL/TL changes.)
3. At what equity level does risk reduction activate, and does it apply to new positions only or existing ones too?
4. Which currency is the account base — does `MODE_TICKVALUE` need symbol conversion for non-account-base instruments?

## Reference

- Companion skills: `mql4-order-execution` (normalization, stoplevel, errors), `mql4-backtesting-optimization` (prove the breakers don't kill the strategy).