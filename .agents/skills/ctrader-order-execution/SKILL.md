---
name: ctrader-order-execution
description: "Use when writing, reviewing, or debugging trade execution code in cTrader cBots: ExecuteMarketOrder/ExecuteLimitOrder/ExecuteStopOrder/ExecuteStopLimitOrder, Place* vs Execute*, ModifyPosition with ProtectionType.Absolute|Relative, ClosePosition/ClosePositions, pending-order cancel, the pips-parameter contract and NormalizePips, pips-vs-units conversion, TradeResult.IsSuccessful and ErrorCode handling (BadVolume, NoMoney, MarketClosed, InvalidStopLossTakeProfit), retry policy, hedging vs netting accounts, and label+symbol position filtering. Trigger on any Execute*/Place*/Modify*/Close* call, order rejection, or failed fill."
metadata:
  author: opencode
  version: "1.0.0"
---

# cTrader Order Execution

## Core Principles

**1. Check the `TradeResult` of every single trade call.** No exceptions.
`Execute*` / `Place*` / `ModifyPosition` / `ClosePosition` all return a `TradeResult` with `.IsSuccessful`, `.Error` (`ErrorCode?`), and on success `.Position` or `.Order`. Fire-and-forget is the single most expensive habit in cTrader: the backtest will look fine while live the cBot silently does nothing half the time.

```csharp
var result = ExecuteMarketOrder(TradeType.Buy, SymbolName, volume, Label, slPips, tpPips);
if (result.IsSuccessful)
    Print($"Opened {result.Position.Id} @ {result.Position.EntryPrice}");
else
    Print($"BUY failed: {result.Error} vol={volume} sl={slPips} tp={tpPips}");
```

On failure, log the **values you attempted** — the error code says what the server disliked, never what you sent it.

**2. Volume is in UNITS, not lots.** This is the single most common porting error from MT4.
`ExecuteMarketOrder`'s volume parameter is units of base currency. A "0.01 lot" EURUSD trade is `1000`, not `0.01` — passing `0.01` yields a rejected order or a microscopic position.

```csharp
double units = Symbol.QuantityToVolumeInUnits(0.01);   // lots  -> units
double lots  = Symbol.VolumeInUnitsToQuantity(units);  // units -> lots
```

Always normalise before sending, and always round **down** (see `ctrader-risk-management`):

```csharp
double vol = Symbol.NormalizeVolumeInUnits(rawUnits, RoundingMode.Down);
```

**3. SL/TP parameters are in PIPS, not prices.** And they are nullable — pass `null` for "no protection". The price-equivalent form is `Symbol.NormalizePips(pips) * Symbol.PipSize`.

```csharp
double slPrice = Symbol.NormalizePips(slPips) * Symbol.PipSize;
```

**Never hand a pip value straight through un-normalised.** A computed `47.3642108327` pip value triggers an "invalid decimal places" rejection. `Symbol.NormalizePips()` snaps to a valid pip multiple for the symbol. This is the cTrader equivalent of MT4's error-130 class of bug, and it is not optional.

**4. `ProtectionType` is the #1 `ModifyPosition` mistake.**
`ModifyPosition` has an absolute and a relative overload. Getting it wrong moves the stop to a price you never intended.

```csharp
// PIPS overload - slPips/tpPips are distances from the current price
ModifyPosition(position, slPips, tpPips, ProtectionType.Absolute);

// PRICES overload - sl/tp are absolute price levels
ModifyPosition(position, slPrice, tpPrice, ProtectionType.Absolute);
```

Relative protection is measured from the **entry price**, absolute from the **current market price**. Mixing them up silently ratchets or loosens the stop. Compute prices explicitly and always use `ProtectionType.Absolute` when you derived a level yourself:

```csharp
double targetSl = Math.Round(Symbol.Bid - trailDist, Symbol.Digits);
var r = ModifyPosition(position, targetSl, position.TakeProfit, ProtectionType.Absolute);
if (!r.IsSuccessful) Print($"Trail failed: {r.Error}");
```

**5. Filter by Label AND SymbolName in every scan.** Always. `Positions` and `PendingOrders` include manually-placed and other-algo trades on the same account.

```csharp
Position[] mine = Positions.FindAll(Label, SymbolName, TradeType.Buy);
Position one    = Positions.Find(Label, SymbolName, TradeType.Sell);
```

**6. There is no `IsTradeAllowed()` / `IsTradeContextBusy()`.** cTrader owns execution — it queues requests and validates server-side, surfacing problems as `ErrorCode` values. Do not port those MT4 guards over; there is nothing to guard. Do log and react to the error codes.

## Method Reference

### Opening positions

```csharp
TradeResult ExecuteMarketOrder(TradeType, string symbolName, double volume,
                               string label = null, double? stopLossPips = null,
                               double? takeProfitPips = null, string comment = null,
                               DateTime? expirationTime = null)

TradeResult ExecuteLimitOrder(TradeType, string symbolName, double volume, double targetPrice,
                              string label = null, double? stopLossPips = null,
                              double? takeProfitPips = null, string comment = null,
                              DateTime? expirationTime = null)

TradeResult ExecuteStopOrder(...)     // same shape, targetPrice
TradeResult ExecuteStopLimitOrder(...)// two prices, + quantityLimit
```

### `Execute*` vs `Place*` — the important distinction

| | `Execute*` | `Place*` |
| --- | --- | --- |
| Returns | filled **position** (market) or order | **pending order** |
| Market semantics | fills at current market | n/a |
| Use when | you want the trade now | you want a resting entry |

`ExecuteLimitOrder`/`ExecuteStopOrder` submit a request that the server turns into a pending order when the target is not immediately reachable. `PlaceLimitOrder`/`PlaceStopOrder`/`PlaceStopLimitOrder` explicitly rest an order. For entry logic that is "market if no barrier", use `Execute*`.

**Pending orders are unmanaged unless you manage them.** A resting stop/limit survives cBot restarts. Reconcile them in `OnStart` or they become orphans.

### Modifying and closing

```csharp
TradeResult ModifyPosition(Position, double? slPips, double? tpPips, ProtectionType, string comment = null)
TradeResult ModifyPosition(Position, double? slPrice, double? tpPrice, ProtectionType, string comment = null)

TradeResult ClosePosition(Position position)
TradeResult ClosePosition(Position position, double volume)   // partial
```

`ClosePositions(TradeType)` has **no `TradeResult`** — it returns void and you cannot tell whether it worked. If you need per-close error context, iterate `Positions.FindAll(...)` and call `ClosePosition(pos)` yourself, checking each result. For anything that matters (Friday flatten, circuit-breaker liquidation), do that.

```csharp
foreach (var pos in Positions.FindAll(Label, SymbolName))   // snapshot first
{
    var r = ClosePosition(pos);
    if (!r.IsSuccessful) Print($"Close failed {pos.Id}: {r.Error}");
}
```

Snapshot before iterating: closing mutates the collection.

### Pending orders

```csharp
PendingOrder[] orders = PendingOrders.FindAll(Label, SymbolName);
foreach (var o in orders)
{
    var r = ModifyOrder(o, o.StopLossPips, o.TakeProfitPips, null, o.Volume);
    if (!r.IsSuccessful) Print($"Modify pending failed: {r.Error}");
}
var c = CancelOrder(o);
```

## Error Handling

`TradeResult.Error` is a nullable `ErrorCode`. `OnError(Error error)` catches algo/runtime errors (not per-order rejections) — switch on `error.Code` and log it; decide whether to stop the instance.

| `ErrorCode` | Meaning | Action |
| --- | --- | --- |
| `InvalidStopLossTakeProfit` | SL/TP too close, wrong side, or too many decimals | **Permanent.** Re-apply `Symbol.NormalizePips`, widen distance |
| `BadVolume` | Volume not on the symbol's step, or below/above min/max | **Permanent.** `NormalizeVolumeInUnits(..., RoundingMode.Down)` |
| `NoMoney` | Insufficient margin | **Permanent.** Check free margin, reduce risk, stop |
| `MarketClosed` | Outside trading hours / session closed | **Transient** (time-based). Check `Symbol.MarketHours.IsOpened()` |
| `Disconnected` | Lost connection to trade server | **Transient.** Retry on the next bar/tick |
| `Timeout` | Server did not respond in time | **Transient.** Retry with backoff — but check `Positions` first, the fill may have landed |
| `EntityNotFound` | Position/order no longer exists | **Benign.** It closed or was modified out from under you — re-sync state |
| `InvalidRequest` | Malformed request (bad label, bad params) | **Permanent.** Fix the call |
| `UnknownSymbol` | Symbol not available on this account | **Permanent.** Guard multi-symbol cBots with `Symbols.GetSymbol(name)` |
| `TechnicalError` | Server-side problem | **Transient.** Retry with a cap |

**Rule: retry the transient ones, fail loudly on the permanent ones.**
`Disconnected` and `Timeout` are worth one or two retries on the *next bar*, not in a tight loop. Critically: **after a `Timeout`, re-check `Positions` before retrying** — the order may have filled and the response was lost. Blind retry after timeout is how you get duplicate positions.

```csharp
TradeResult SendWithRetry(TradeType tt, double vol, double? sl, double? tp)
{
    for (var attempt = 0; attempt < 3; attempt++)
    {
        var r = ExecuteMarketOrder(tt, SymbolName, vol, Label, sl, tp);
        if (r.IsSuccessful) return r;
        if (r.Error != ErrorCode.Disconnected && r.Error != ErrorCode.Timeout) return r;
        // Timeout may have filled server-side - do not blindly resend
        if (r.Error == ErrorCode.Timeout && Positions.Find(Label, SymbolName, tt) != null)
            return r;
    }
    return ExecuteMarketOrder(tt, SymbolName, vol, Label, sl, tp);
}
```

## Hedging vs Netting

Account accounting mode changes position semantics and is a common source of "why did my position flip".

- **Hedging** — multiple independent positions per symbol, both directions simultaneously. `Position.Id` is stable; partial closes and per-position management are meaningful. Grid/martingale/HMAC-style strategies **require** hedging.
- **Netting** — one net position per symbol. An opposite order **reduces, closes, or reverses** the existing position. There is no second ticket. "Close all buys" is meaningless because there is at most one.

Design for one or the other explicitly, and never assume a `ClosePosition` leaves other positions untouched on a netted account. `Account` exposes the mode — check it if the strategy's correctness depends on it.

## Worked Hardening Snippet

```csharp
private bool OpenWithStop(TradeType tradeType, double volume, double? slPips, double? tpPips)
{
    // Normalise pips: prevents "invalid decimal places" rejections
    if (slPips.HasValue) slPips = Symbol.NormalizePips(slPips.Value);
    if (tpPips.HasValue) tpPips = Symbol.NormalizePips(tpPips.Value);

    // Normalise volume: RoundingMode.Down, never ToNearest on a risk cap
    volume = Symbol.NormalizeVolumeInUnits(volume, RoundingMode.Down);
    if (volume < Symbol.VolumeInUnitsMin) { Print("Volume below symbol minimum"); return false; }

    if (!Symbol.MarketHours.IsOpened()) return false;

    var result = ExecuteMarketOrder(tradeType, SymbolName, volume, Label, slPips, tpPips);
    if (result.IsSuccessful) return true;

    Print($"{tradeType} failed: {result.Error} vol={volume} sl={slPips} tp={tpPips}");
    return false;
}
```

## Reference

- `ctrader-risk-management` (volume computation, `RoundingMode.Down`, margin pre-checks)
- `ctrader-cbot-basics` (lifecycle, label identity, bar indexing)
- `ctrader-code-review` (checklist that audits every execution call site)
- Companion MQL4 skill: `mql4-order-execution` covers the same ground with MT4 error codes.
