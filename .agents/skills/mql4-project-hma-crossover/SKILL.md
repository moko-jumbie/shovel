---
name: mql4-project-hma-crossover
description: "Use when editing, fixing, extending, or backtesting the HMA_Crossover_Smart_Run4 EA (currently MT4EAv4.txt in C:\\AI\\mimo). Captures its project conventions: magic number 84721, HMAX_HUD_ panel prefix, CYBER MATRIX HUD color scheme, bar-close OnTick design, circuit-breaker matrix (daily loss, consecutive-loss, cooldown), ATR-based SL/TP and trailing, session toggles in broker-time UTC+3, spread filter, and its known fix log. Trigger on any change to this EA file or a copy of it."
metadata:
  author: opencode
  version: "1.0.0"
---

# HMA Crossover Smart Run v4 — Project Conventions

## Overview

`HMA_Crossover_Smart_Run4` is a bar-close HMA (Hull Moving Average) crossover Expert Advisor with a heavy protection matrix and a cyberpunk on-chart HUD. Source lives at `C:\AI\mimo\MT4EAv4.txt`. Version v4 (FIX-1..FIX-9) resolves nine prior issues documented in the header fix log.

## Non-Negotiables (do not break)

- **Magic Number `84721`** — every order must carry it; every scan must filter `OrderSymbol() == Symbol()` **and** `OrderMagicNumber() == MagicNumber`. Never trade or manage positions outside this magic.
- **Bar-close execution** — entries fire only on a completed bar (`Time[0]` guard via `lastBarTime`). Keep it.
- **One position per side** — `CountOpenPositions(OP_BUY)` / `(OP_SELL)` gates entries. v4 FIX-8 removed force-closing of the opposite side; do not reintroduce it.
- **HUD prefix `HMAX_HUD_`** — all chart objects use this prefix; `OnDeinit` and `RenderDashboard` clean objects with it (`ObjectsDeleteAll(0, PanelPrefix)`). The dashboard must be destroyable without leaking.

## Trading Flow (OnTick order — keep this order)

1. `ApplyAtrTrailingStop()` — every tick
2. Friday close guard (Fri, hour >= `FridayCloseHour`)
3. Daily loss limit breaker (`IsDailyLossLimitBreached()`)
4. Bar-close filter (`iTime(...,0) == lastBarTime` → return)
5. `gBarsSinceLastLoss++`
6. Spread filter (points, see units note below)
7. Session filter (`IsWithinAllowedSession()`)
8. ADX >= `AdxThreshold` filter
9. HMA fast/slow values (index 1 current, index 2 prior)
10. SMA macro filter (200)
11. ATR SL/TP distances
12. Crossover detection (prior-to-current states)
13. Cooldown block (`IsCooldownActive()` on cross)
14. Execution (`OrderSend` with adjusted risk → dynamic lots) — the ONLY place orders open
15. HUD refresh

## Key Inputs & IDs

| Area | Inputs |
| --- | --- |
| Circuit breakers | `UseDailyLossLimit` (0.8%), `UseConsecLossGuard` (max 3, `ReducedRiskMult` 0.5), `ReentryCooldownBars`=3 |
| Strategy | `FastPeriod`=20, `SlowPeriod`=30 |
| Filters | `UseSmaFilter` (200), `UseAdxFilter` (28), `UseTrailingStop` (activation 3.0 / cushion 1.5 ATR) |
| Protection | `FridayAutoClose` 22:00, `UseSpreadFilter` (20), session toggles Sydney/London/NY/Asia |
| Risk | `AutoMoneyManagement`, `RiskPercentPerTrade` 1.0, fixed `LotSize` 0.10, `AtrPeriod` 14, `SlAtrMultiplier` 2.0, `TpAtrMultiplier` 5.0, `Slippage`=3, `MagicNumber` 84721 |
| HUD | `ShowDashboard`, `InpXOffset`/`InpYOffset`, `InpFontSize`, `RefreshRateSec`, full cyberpunk color set (`ClrBorder` C'0,255,204', `ClrHeader` C'255,102,0', etc.) |
| Sessions | Broker time is **UTC+3**; Sydney 22–07, Tokyo(Asia) 02–11, London 09–18, NY 15–00. Toggles OR together; all-off = trade all hours. |

## Units Convention (critical)

- `MarketInfo(Symbol(), MODE_SPREAD)` returns **points**, not pips. On a 5/3-digit account `1 pip = 10 points`. The `MaxSpreadPips` input is interpreted as **pips** in the strategy docs — convert before comparing (`points / 10`).
- ATR display uses `atrVal / pointVal` to show points on the HUD.
- Our HMA/WMA implementation is custom (`CalculateHMA`/`CalculateWMA`) — do NOT replace with `iMA`-based approximations; the strategy's behavior depends on the exact weighting.

## Style Rules

- `#property strict`; section banner comments `//+------+` throughout.
- Header fix-log block documents version deltas as `FIX-N:` lines — a new round of fixes must extend that log.
- Every trade call must be checked and logged with `GetLastError()` context (still pending across several call sites in v4).
- Risk-path functions (`GetAdjustedRiskMultiplier`, `CalculateDynamicLot`) take risk params explicitly — keep the signature style.

## Known Open Items (v4 audit)

- `TrackOrderClose()` is defined but **not called** — wire it into close/reconciliation so `gConsecLosses`/`gBarsSinceLastLoss` update on live losses (currently only restored from history at init).
- No `IsTradeAllowed()` / `IsTradeContextBusy()` guard before entry.
- Spread filter compares raw points to `MaxSpreadPips` (10× unit error on 5-digit).
- No `MODE_STOPLEVEL` validation on SL/TP distances.
- `CalculateHMA` allocates a temp buffer per call (performance hotspot on live ticks).
- Duplicate comment header on `IsWithinAllowedSession`.

## Reference

- Strategy + risk/filter behavior per `mql4-ea-basics` and `mql4-risk-management`.
- Testing guidance per `mql4-backtesting-optimization` (Every tick, walk-forward, out-of-sample).