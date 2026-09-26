---
name: mql4-ea-basics
description: "Use when writing, structuring, or reviewing MQL4 MetaTrader 4 Expert Advisors (EA), custom indicators, or scripts. Covers the event-driven lifecycle (OnInit/OnDeinit/OnTick/OnTimer/OnChartEvent), standard file section layout, input variable conventions (input vs extern vs sinput), clean-code and naming rules, single-responsibility organization, .mqh modularity, and common anti-patterns (blocking OnTick, magic literals, look-ahead bias). Trigger on any task that creates or edits a .mq4 source file."
metadata:
  author: opencode
  version: "1.0.0"
---

# MQL4 Expert Advisor Basics

## Core Principles

**1. MQL4 is event-driven — you do not write the loop.**
MetaTrader 4 calls the event handler functions; you implement them. Never write `while(true)` or busy-wait inside `OnTick()` — it blocks the terminal's chart thread, freezes the chart, and MT4 will eventually kill the EA.

**2. The three mandatory handlers:**
- `int OnInit()` — runs once when the EA attaches (and again on every input change, recompile, or terminal restart). Validate inputs here, cache anything derived from the symbol here, and return `INIT_SUCCEEDED` to continue, or `INIT_FAILED` / `INIT_PARAMETERS_INCORRECT` to refuse to start. Anything that must survive an input edit lives in a global variable — locals and `static` locals are lost.
- `void OnDeinit(const int reason)` — runs on the way out. Clean up chart objects, kill timers with `EventKillTimer()`, and close file/indicator handles. The `reason` code tells you why.
- `void OnTick()` — runs on every incoming price tick. This is where the strategy lives. Keep it fast: return early on every guard that fails. No ticks on weekends = the EA is never called, not "paused".

Additional optional handlers: `OnTimer()` (scheduled UI/logic refresh), `OnChartEvent()` (mouse/keypress/object clicks).

**3. Every tunable number belongs in an `input` variable.**
Hardcoding is what makes an EA impossible to optimize later. Use the modern `input` keyword (the old `extern` still compiles but is deprecated). `sinput` is a static input: user-editable but excluded from optimization passes — use it for switches you never want the optimizer touching. `input` variables are read-only inside the program and are re-initialized before `OnInit()`.

**4. One function = one responsibility.**
If a function reads market data, computes a decision, AND sends an order, that is three responsibilities. Split them. Function names should read like actions: `IsNewBar()`, `LookForEntries()`, `ManageOpenTrades()`, `CalculateDynamicLot()`. Reading your function names in order should describe what the EA does.

**5. Comment the WHY, not the WHAT.**
Explain why a decision was made or what rule is being enforced — not what each line of code obviously does.

## Standard File Section Layout

Every EA follows this predictable structure so you instantly feel at home in any codebase:

```
//+------------------------------------------------------------------+
//| Header / description / fix log                                    |
//+------------------------------------------------------------------+
#property copyright "..."
#property version   "1.00"
#property strict

//==============================
// INPUTS (user settings)
//==============================

//==============================
// GLOBAL VARIABLES (EA state)
//==============================

//==============================
// ONINIT / ONDEINIT
//==============================

//==============================
// ONTICK (controller — orchestrates, does not implement)
//==============================

//==============================
// LOGIC FUNCTIONS
//==============================

//==============================
// HELPER FUNCTIONS
//==============================
```

`OnTick()` should be a thin orchestrator: update rules, check guards (each an early `return`), then delegate to small functions. Do not bury strategy logic directly inside `OnTick()`.

## Clean-Code Rules

- **Names describe intent.** `RiskPercent`, `MaxTrades`, `TradingAllowed` — never `x`, `r`, `flag1`.
- **Be consistent.** Same indentation (MetaEditor default is 3 spaces), same naming case, same brace style.
- **Whitespace is visual clarity.** Group related lines with blank lines; a wall of text is unreadable.
- **Kill magic literals.** A bare `5` or `0.1` in trading logic is a number the optimizer cannot vary. Promote to an input or a named constant.
- **`#property strict`** enables strict type checking and catches overflow bugs at compile time.

## Anti-Patterns

| Anti-pattern | Symptom | Fix |
| --- | --- | --- |
| `while(true)` / blocking loop in `OnTick` | Chart freezes, terminal kills EA | Return from `OnTick`; use bar filter or state machine |
| Magic literals in logic | Impossible to optimize, unrecognizable in 6 months | Promote to `input` or constant |
| Look-ahead bias (`iMA(...,0)` used for decisions) | Backtest curve-fit, false entries | Use closed-bar data (`index >= 1`) so decisions match live reality |
| Monolithic `OnTick` | Hard to debug, hard to test | Delegate to single-purpose functions |
| Not cleaning up in `OnDeinit` | Phantom objects/objects persist after EA removed | `ObjectsDeleteAll(0, prefix)`, `EventKillTimer()` |
| Duplicating logic between UI and trading path | Display says one thing, EA does another | One shared computation, read by both |

## Modularity (.mqh includes)

Keep `Experts/` EAs as thin orchestrators. Move reusable code into `Include/` files:
- `Include/RiskManagement.mqh` — position sizing, drawdown limits
- `Include/TradeManager.mqh` — order execution + retries
- `Include/UTraFilter.mqh` — signal/filter modules

Go single-file when the EA has one signal, basic risk, and no reuse needs. Go modular when: multiple strategies, shared code across EAs, complex risk/filters, or team collaboration.

## Reference

- SOLID applied to MQL4: single-purpose modules, strategy injection via function pointers (Open/Closed), consistent function contracts, small focused includes, abstractions over direct calls.
- See the `mql4-order-execution`, `mql4-risk-management`, `mql4-backtesting-optimization`, `mql4-performance-tuning`, and `mql4-code-review` skills for the specialized layers.