# MT4 Workspace Orientation (for AI agents)

Quick orientation for agents working in `C:\AI\mt4`. Read this before touching any file.

## Purpose
This folder holds a MetaTrader 4 (MQL4) HMA crossover trading system and its backtest study material. Most files are plain-text `.txt` copies of `.mq4` source so they can be edited outside MetaTrader.

## File Layout

| File | What it is |
| --- | --- |
| `MT4EAv4.txt` | Main EA: `HMA_Crossover_Smart_Run4_FIXED` v4.00 (1329 lines). Bar-close HMA(20/30) crossover EA with circuit-breaker matrix, cyberpunk on-chart HUD (`HMAX_HUD_` prefix). Header holds the FIX-1..FIX-12 fix log — extend it when fixing further. |
| `chartInfo.txt` | `CyberChartInfo.mq4` — standalone chart HUD indicator (`CyberInfo_` prefix). Cosmetic only, no trading. |
| `dashboard.txt` | `CyberMatrixDash.mq4` — multi-magic / multi-symbol performance dashboard (`CyberDash_` prefix). Cosmetic only, no trading. |
| `Runs/` | Strategy Tester output: `<NN>-Optimization-*.htm` reports + `.gif` equity graphs (XAU-focused). Optimization pass history. |
| `ORIENTATION.md` | This file. |

## The EA — Non-Negotiables (do not break)
- **Magic number `84721`** — every order carries it; scans must filter `OrderSymbol()==Symbol()` **and** `OrderMagicNumber()==MagicNumber`. Never trade/manage positions outside this magic.
- **Bar-close execution** — entries only on a completed bar (`Time[0]` vs `lastBarTime` guard). Keep it.
- **One position per side** — gated by `CountOpenPositions()`. FIX-8 removed opposite-side force closing; do not reintroduce.
- **Custom HMA/WMA** (`CalculateHMA`/`CalculateWMA`) — do not replace with `iMA`-based approximations; strategy depends on exact weighting.
- **HUD prefix `HMAX_HUD_`** — `OnDeinit`/`RenderDashboard` clean via `ObjectsDeleteAll(0, PanelPrefix)`.
- **OnTick order (keep)**: trailing stop → Friday close guard → FIX-13 account-wide daily-loss breaker (closes ALL positions at `AccountDailyClosePercent` default 4%) → daily loss breaker → bar-close filter → `gBarsSinceLastLoss++` → spread filter → session filter → ADX filter → HMA values → SMA(200) macro filter → ATR SL/TP → crossover detection → cooldown → `OrderSend` (only place orders open) → HUD refresh.
- **Broker time is UTC+3** — Sydney 22–07, Asia 02–11, London 09–18, NY 15–00. Toggles OR together; all-off = trade all hours.
- **Units**: `MarketInfo(Symbol(), MODE_SPREAD)` returns **points**, not pips (`1 pip = 10 points` on 5/3-digit). FIX-11 added `SpreadPipDivisor` (0 = auto). ATR displays in points via `atrVal / pointVal`.

## Key Functions (MT4EAv4.txt)
- `OnInit` / `RestoreStateFromHistory` / `OnDeinit` / `OnTimer` / `OnTick`
- `IsDailyLossLimitBreached()` · `GetAdjustedRiskMultiplier()` · `IsCooldownActive()`
- `TrackOrderClose()` (defined but needs wiring into close/reconciliation on live losses)
- `BarsSince()` · `RebuildLossStateFromHistory()` · `GetSpreadPips()` · `GetStopLevelDistance()`
- `IsTradePermitted()` · `SendMarketOrder()` · `IsWithinAllowedSession()`
- `CalculateHMA()` / `CalculateWMA()` · `ApplyAtrTrailingStop()` · `CalculateDynamicLot()` · `CountOpenPositions()`
- HUD: `UpdateStrategySnapshot()` · `ScanEAPerformance()` · `RenderDashboard()` + row/panel helpers.

## Skills To Use (load before working on this EA)
| Skill | When |
| --- | --- |
| `mql4-project-hma-crossover` | **ALWAYS** for any change to this EA — captures project conventions, fix log, and known issues. Note: it documents up to FIX-9; this copy already contains **FIX-10..FIX-12** (loss-state rebuild from history, pip-vs-point spread filter + stop-level clamp, trade-permission guard + retry/logging). Treat its "Known Open Items" list against this newer copy before acting. |
| `mql4-ea-basics` | Structuring/editing any `.mq4`/`.mqh` source (lifecycle, input conventions, anti-patterns). |
| `mql4-code-review` | Pre-deployment / "is this EA safe" audits. |
| `mql4-risk-management` | Circuit breakers, position sizing, ATR stops, daily-loss / consecutive-loss logic. |
| `mql4-order-execution` | Any `OrderSend`/`OrderClose`/`OrderModify` work, error 130+, requotes, retry policy. |
| `mql4-backtesting-optimization` | Interpreting `Runs/` reports, new optimization passes, walk-forward / overfit checks. |
| `mql4-performance-tuning` | Backtest speed or chart-lag issues (e.g., the per-call temp buffer in `CalculateHMA`). |

## Notes
- Source file lives in plain-text form; re-copy to `MQL4\Experts\` (and `.mq4` extension) to compile in MetaEditor.
- `chartInfo.txt` / `dashboard.txt` are indicators (compile to `MQL4\Indicators\`), attached to a chart for display only.
- Known open items (v4 audit, per skill, verify against this file): `TrackOrderClose()` not called; `CalculateHMA` temp-buffer hot path; duplicate comment header on `IsWithinAllowedSession`; SessionToggleStatus vs session-toggled logic.