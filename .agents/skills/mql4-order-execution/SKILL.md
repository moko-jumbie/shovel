---
name: mql4-order-execution
description: "Use when writing, reviewing, or debugging trade execution code in MQL4 MetaTrader 4 Expert Advisors: OrderSend/OrderModify/OrderClose/OrderSelect, price and lot normalization, magic-number and symbol filtering, trade-server error codes (130/131/134/135/136/138), retry policy, GetLastError logging, IsTradeAllowed/IsTradeContextBusy, MODE_STOPLEVEL compliance, and pips-vs-points conversions. Trigger on any Order* call, error 130 handling, execution failures, or requotes."
metadata:
  author: opencode
  version: "1.0.0"
---

# MQL4 Order Execution

## Core Principles

**1. Normalize every price and volume before sending.**
- `NormalizeDouble(price, Digits)` on every price argument. The trade server rejects unnormalized floating-point values outright.
- Normalize lots against the symbol, never assumptions: read `MODE_MINLOT`, `MODE_MAXLOT`, `MODE_LOTSTEP` from `MarketInfo()` and round to the step. A volume that works on EURUSD at one broker gets rejected on gold at another (error 131).

**2. Check the return value of every single trade call.**
`OrderSend` returns the new ticket or `-1`. `OrderModify` and `OrderClose` return `bool`. An EA that fires and forgets silently does nothing half the time. On failure, log `GetLastError()` **with context** — the error number alone tells you what the server disliked, never what you sent it. Silent failure is the most expensive habit in MQL4.

**3. Filter by OrderSymbol() AND OrderMagicNumber() together — everywhere.**
The magic number is the only reliable way to recognize your own trades. Without both checks, your EA will move the stop on a hand-placed trade or another EA's position on the same account.

**4. Iterate order loops backwards**, from `OrdersTotal() - 1` down to `0`. Closing/modifying orders re-indexes the pool, so a forward loop silently skips entries.

## Order Functions

### OrderSend — place the order (11 params)
```mql4
int ticket = OrderSend(
   Symbol(),      // symbol
   OP_BUY,        // OP_BUY / OP_SELL / OP_BUYLIMIT / OP_BUYSTOP / OP_SELLLIMIT / OP_SELLSTOP
   lots,          // normalized volume
   Ask,           // price (Ask for buy, Bid for sell)
   Slippage,      // allowed slippage in points
   slPrice,       // normalized stop loss
   tpPrice,       // normalized take profit
   "comment",
   MagicNumber,
   0,             // pending-order expiry (0 = GTC)
   clrGreen);
if(ticket > 0) { /* success path */ }
else { PrintFormat("OrderSend failed err=%d ask=%.%d", ...); }
```

### OrderModify — change stop/target on a live order
- For a market position the `price` argument is meaningless — pass `OrderOpenPrice()` back unchanged.
- MT4 rejects a modify that changes nothing. Compare new values against current before calling.
- Returns `bool`.

### OrderClose — close a position
- `OrderClose(ticket, lots, price, slippage, color)` closes at `Bid` for a buy and `Ask` for a sell.
- Passing fewer lots than the position holds is a partial close; the remainder stays open under a new ticket.
- Returns `bool`; `false` means the server said no.

### OrderSelect — the gate to every order property
- Loop pattern: `for(int i = OrdersTotal() - 1; i >= 0; i--) { if(OrderSelect(i, SELECT_BY_POS, MODE_TRADES)) { ... } }`
- History: `MODE_HISTORY` over `OrdersHistoryTotal()`. Hidden/pending orders from other EAs also appear — always filter.

## The Broker-Constraint Trinity

1. **Stop level (`MODE_STOPLEVEL`)** — minimum distance in points between price and SL/TP. Send anything tighter and you get **error 130 (invalid stops)** — the most-searched MQL4 error. On ECN brokers it is frequently reported as zero in docs but non-zero in practice. Always validate: `if(slDistance < stoplevel * Point) clamp/refuse`.
2. **Lot grid (`MODE_LOTSTEP/MINLOT/MAXLOT`)** — see "Normalize every volume" above.
3. **Points vs pips** — on a 5-digit account (5 or 3 decimal quotes), `Point` is 0.00001, so "pips" are 10 points. A spread/pip confusion produces error 130 and wrong filter thresholds almost every time. Conversion table: 5/3-digit → `1 pip = 10 * Point`; 4/2-digit → `1 pip = 1 * Point`.

## Trade Permissions

Check before **every** attempt, not once at startup:
- `IsTradeAllowed()` — false when AutoTrading is off, the EA lacks algo permission, or the symbol session is closed.
- `IsTradeContextBusy()` — true when another EA already holds the terminal's single trade thread. No amount of correct order code gets around this; back off and retry next tick.

## Error Handling & Retry Policy

| Code | Meaning | Action |
| --- | --- | --- |
| 130 | Invalid stops | Permanent — fix distance/normalization, don't retry blindly |
| 131 | Invalid volume | Permanent — normalize lots, log, stop |
| 134 | Not enough money | Permanent — log, reduce risk or stop |
| 135 | Price changed | Transient — `RefreshRates()` and resend |
| 136 | Off quotes | Transient — `RefreshRates()` and resend |
| 138 | Requote | Transient — `RefreshRates()` and resend |

**Rule: retry the transient rejections, fail loudly on the permanent ones.** A requote means "the price moved, try again" — call `RefreshRates()` and resend with a retry cap (e.g., 3 attempts) to avoid hammering the server. Not enough money / invalid volume will fail identically forever.

## Worked Hardening Snippet

```mql4
bool SafeOrderSend(...)
  {
   for(int attempt = 0; attempt < 3; attempt++)
     {
      if(!IsTradeAllowed() || IsTradeContextBusy())
         return(false);
      RefreshRates();
      int ticket = OrderSend(Symbol(), OP_BUY, lots, Ask, Slippage, sl, tp, "ea", MagicNumber, 0, clrGreen);
      if(ticket > 0)
         return(true);
      int err = GetLastError();
      if(err != 135 && err != 136 && err != 138)
        {
         PrintFormat("OrderSend failed: err=%d lots=%g ask=%.%df", err, lots, Digits, Ask);
         return(false);
        }
      Sleep(100);
     }
   return(false);
  }
```

## Reference

- Companion skills: `mql4-risk-management` (sizing + circuit breakers), `mql4-code-review` (checklist that audits all Order* call sites).