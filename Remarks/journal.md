# Updates

Last updated 2026-09-26

# Findings

First set of optimizations were done with a ~6mo backtest. The first three days were red, with multiple EA's triggering their Max Daily Loss. On Wednesday, I experimented with optimizing using a very short recent data (last 5 trading days). The magic numbers with repeating digits (11111, 22222...) are those with recent-data optimizations. They proved to be more quickly profitable, with the exception of XTIUSD - which, frustratingly, triggered stop-loss before immediately going into what would have been huge profit. I rechecked the backtesting in visual mode and noticed the MT4 strategy tester was not acting "per tick" like I chose, but with open/close (there were "touches" that did not trigger stop-loss). Moving forward, I will use another tester, else I would have to edit the bots to trade strictly on bar close (and not use stop-loss at all).

# MyFXBook

Forward-testing results are and will be published to https://www.myfxbook.com/portfolio/mt4-7008774/12211857

# Optimization

As mentioned, optimization seems to be profitable faster with recent short-term data. But how often I should optimize still needs exploration

## Optimization Flow

Current method:
- First I use a start set and optimize per session (eg. London):
    - Starting set is the defaults in the cBot. We just need to turn on the session we are optimizing, eg. London: true.
- Find a reliable signal. I optimaze for fastPeriod & slowPeriod **only**, searching in the range: Fast: 10-35, Slow: 13-90. I'm looking for good avarage trade, profit factor, and minimal equity and balance drawdowns. I'm also looking for clusters (eg. fast 20-25 with slow 56-64 all look good; or (14, 13), (16, 15), (21, 20)... pullback signals all look good) and eliminationg outliers.
- Is the SMA filter helping or hindering? I optimize for SMA values from 50 all the way to 350, look for clusters of good results and choose a central point. Then test SMA filter true vs false.
- Will ADX filtering help? I change ADX filtering to true and I test ADX filtering from ~10 to ~60 and compare with when ADX filtering was off. Is is eliminating bad trades?
- SL & TP. Now that I have a reliable signal that survives 2xATR:2xATR risk/reward, I test to see if more breathing room is better. I do not like very narrow SL, so I don't go under 1.5x SL. Testing range: SL 1.5x-5x; TP 1.5x-15x.
- Trailing? After optimizing SL/TP, I check whether a trailing stop captures those near-misses, or if they add to the noise (a lot more trades with similar net profit).

## Optimization Time frame

- Last 5 trading days
- M5 timeframe, tick data.
- Optimize twice a week? 
    - On weekend (just because I have lots of time to do so).
    - After Wednesday (after Wednesday London [Mornings for me], I can test last 5 london sessions, after NY [~5pm for me] I can test last 5 NY sessions.) I have only tested Londons and NYs so far. I will try to add Asian sessions this week.

## Instruments

- I am concentrating on 3: USDJPY, XTIUSD, and XAUUSD. They display the most extended directional moves (sometimes for days) with minimal spreads.

## Magic Numbers/Labels

- Each session will have to have an individual Magic Number, even on the same symbol. Currently on MT4:
    - XAU-London	22222
    - XAU-NY	44444
    - JPY-London	55555
    - JPY-NY	66666
    - XTI-NY	11111
    - XTI-London	33333

# Example Run

- See folder "Test Run".
- On the first run, I noticed a good performance cluster with the slowHMA ~17-19. I settled on fast/slow (16,17).
- Testing SMA, I noticed the 10 SMA looked like it filtered half the 200SMA (bad trades?). Testing the lower end more finely shows almost random results. 10, 11 did same as 19-24. I chose to continue with 23SMA (middle of the bigger cluster).
- I turned ADX filter on and found a range 19-23 filtered out 2 trades, resulting in much higher PF and Avg Trades. I'll continue with ADX(21), middle of the cluster.
- SL/TP runs show that the same 4 trades can survive a tighter stop (good signal?) and can run up to 7xATR. 1.5xATR is my limit for tightness. I will continue with (1.6x: 7x).
- Testing the trailing, I discovered that early activation of a 1.5x trail skyrockets PF, but cut profits and average trade by almost half. I will still do a late activation with ample trail to possibly capture a near-miss. (Activation 6.3, Trail 2.1)
- Final settings for forward testing: I change the magic to 22222, and half the risks (Max Daily Loss and Risk %)
    - Signal (16, 17)
    - SMA (23)
    - ADX true, (21)
    - SL/TP (1.6, 7)
    - Trailing true, (6.3m 2.1)
    - Magic (22222)
    - London Session true
    - Turn on HUD for forward testing
    - Everything else default as in the cBot.

# 2026-09-27

I wondered if I would have spotted or even chosen my "optimal settings" for Sep 21-25 based on the previous week. First, I ran a backtest on "optimal settings" for Sep 14-18. It resulted in 3 successful trades, profit ~$1108. Profit factor showed "NaN" because there were no losers. "Optimal settings" can possibly do well 2 weeks straight. I decided to run a grid (exhaustive search) using my "optimal settings" and search signals only. Results were... interesting:
- Signal (16,17) had an unusually high average trade - $366.28, compared to 88.53. It was an outlier I would have probably dismissed.
- (16, 17) had NaN for NaN for fitness and - for PF. It does not even appear on the screen if I sort by either of these.
- The cluster with highest profits do so wit ~19-25 trades. (16,17) did it with 3. Again, an outlier.
Conclusions: I would in all likelyhood have NOT chosen (16,17) if given this set to choose from.
Questions:
- I don't choose signal last. Did the other settings (filters, stops) *make* (16,17) an outlier?
- Given this set, which would I choose?
- Would my choice be any good the following week?
[See Test Run\Signal-search-gold-london-1409-1809.optres]

Testing the first question would take me a long time. I'll try the other 2. I would have chosen somewhere in the middle of the top cluster ~(11,13) or (12,15). Running a signal check for Sep 21-25 revealed:
- (11,13) would have done fairly well: $444.54 profit; PF = 1.41; Average trade way down from previous week @ $27.78.
- (12,15) did similar: $362.64 profit; PF = 1.45; Average trade = $32.97.
- A new "outlier" signal formed: (18,19) [2 trades, $434 average trade] I should check it at the end of the week. Perhaps outliers are what I should choose after all?
- A (new?) good cluster formed aroound (low-mid 30s, mid-high 20s). These values *lost* money the previous week. Eg. (32, 27) ["passId": 387, "fitness": -41.70850583271343, "equity": 9977.239999999998].
- The same stops can provide good results for multiple weeks. What seems to decay/alter results by a significant amount is the signal.
[See Test Run\Signal-search-gold-london-2109-2509.optres]
I'll let Kit run a similar signal search for September 7-11 and look for the week-to-week pattern.

# 2026-09-28

Working with Kit taught me something - I was making "lottery tickets" and chasing noise. I taught Kit my flow, but the *reason* for my flow is my limited resources. Testing multiple dimentions would have taken me hours, if not days. What if picking the signal first was entirely messy? Testing had proved that money-management (stops) persist longer. We settled on zero XTIUSD combos using our current method. I decided to run a multi-setting test, just for the sake of it, using spaced-out samples. I thought it would take a long time. Surprisingly, tests run relatively fast on oil. Each test (one London & one NY) took ~100 minutes. I found favorable combos with good clusters. The ones I chose had ~84 and ~45 trades if I remember correctly. Logically, testing this way should reveal settings that might decay more slowly. I logged the combos to the settled spreadsheet - Kit, take a look at it.
Forward testing today, the XTIUSD settings are doing well, but it is too soon to call it a success. All the others are a mixed bag. One snag - MT4 is printing 5min bars as XX:01, XX:06... on XAUUSD. This bug caused MT4 to take a (losing) trade at 09:31 that cTrader did not enter. Additionally, MT4 trades are 1 minute "late".
I saved the XTI London results [Test Run\XTI-multi-dim-London.optres] and the multi-dimentional run settings in the repo [Test Run\START-h.optset]. Kit, I'm guessing you have more compute power to space the testing out a little finer, but even these found good sets. Give your opinion.
Going forward, I think we should adopt this multi-dimentional method. The downside is one session&symbol would (maybe?) take a long time. We can shedule 1 per hour on the weekends when market is closed. When we determine decay, we can change to every X weekends, or when Y happens.

# 2026-10-02

Added H1 areas of interest (order blocks, fair value gaps, breaker blocks, EQH/EQL) to the cTrader port. They are drawn on the M5 chart with plain chart objects and can also gate entries.

**Important for the optimization record:** every result in this journal was measured with the POI gate off, and the gate is off by default (`Use POI Confluence Filter` = false). Turning it on changes which trades are taken, so the settings settled above are only valid for an ungated run. If I want gated results, that is a fresh optimization pass, not a re-use of these numbers.

Defaults that keep the trading path identical to before: `Draw SMT POIs` false, `Use POI Confluence Filter` false. With both off the OnTick cost is a single null check.

Design notes for whoever picks this up:
- Detection reads only closed H1 bars and reruns once per H1 bar close, so there is no look-ahead and the per-tick cost is one DateTime read.
- A zone's chart-object name is derived from its origin bar, kind and side. Re-rendering updates the same objects instead of stacking new ones, and zones that stop being detected take their objects with them.
- Cleanup only ever touches objects prefixed `SMTPOI_`. Never `Chart.RemoveAllObjects` on a shared chart.

Verified outside cTrader with a small harness (47 assertions over synthetic bars): order block detection, breaker conversion on violation, FVG bounds, equal highs, swept levels, mitigated zones excluded from the gate, object reuse and orphan eviction across repeated renders, and that a different forming bar produces an identical snapshot.

Two real bugs were found and fixed by that harness, both index-direction errors that produced plausible-looking but wrong output rather than exceptions:
- True range was reading the previous close from a slot the loop had not written yet, so every ATR-based threshold (OB impulse, FVG gap, EQH tolerance, confluence proximity) was measuring against zero. ATR came out ~97 on a 99-105 price range.
- The liquidity sweep scan ran from the oldest bar instead of the newest, so it never saw the post-formation take-out that is the whole point of the level.

## Nothing was drawn

First run with `Draw SMT POIs` on produced an empty chart. Cause was in the parameter guards, not the detector:

- `POI Timeframe` is a `TimeFrame`, which is a **reference type** in the cTrader API, and `TimeFrame.Hour` is a static property rather than a const, so it cannot go in `DefaultValue`. With no default in the attribute the parameter arrived as `null`, and the guard read `null` as "not a higher timeframe" and disabled the whole feature. Compounding it, `null <= TimeFrame.Minute` evaluates to **False**, so the comparison half of that guard never actually tested anything useful - the null check was the only thing firing, and it fired for the wrong reason.
- Fixed by defaulting an unset or `Minute` value to H1 with an explicit log line, and by only treating a timeframe as invalid when it is genuinely not above the chart's.
- Zones were also drawn on **negative ZIndex** layers (body -10, border -9, label -8). cTrader documents ZIndex only as ordering between chart objects and says nothing about negative layers compositing above the chart background, so a negative layer can render invisibly - which looks identical to a detector that found nothing. Now 1/2/3.

Two failure modes stayed silent and are now reported: chart drawing being suppressed during a backtest (`Draw POIs In Backtest` is off by default), and a detection pass that finds zero zones because every layer is off, the history is shorter than the detector's 20-bar minimum, or the thresholds cannot be met. The first pass now logs bar count, zone count, which layers were enabled, the ATR it computed and how many objects reached the chart, so the next "nothing appeared" is answerable from the log instead of by guesswork.

## Nothing was visible even though objects existed

With the switches on, the log reported `21 zone(s) ... 6 chart object(s)` and an empty chart. So the detector was working - 21 zones, ATR 18.75 on gold - and the renderer was not.

Three separate defects stacked up:

1. **Zones were anchored with H1 timestamps on an M5 chart.** The renderer called the `DateTime` overloads of `DrawRectangle`/`DrawText` and let cTrader resolve the position, with no check that the time is actually a bar on the chart's series. A higher-timeframe timestamp that does not land on a chart bar yields no object, and an object that was never created looks identical to one that is off screen. Zones are now resolved to bar indices on the chart's own series with `OpenTimes.GetIndexByTime(...)` and drawn through the `barIndex` overloads. Only a zone spanning a single chart bar still uses the `DateTime` overload, because a bar-index anchor would collapse it to zero width.
2. **Bar indices run newest-first, so `from > to` for any zone that began in the past.** My first attempt at the fix tested `to > from` to decide whether the zone spanned more than one bar, which is false for essentially every historical zone - it quietly sent everything back down the broken `DateTime` path. The test is `to != from`. Caught by working the index arithmetic out against the stub rather than by running anything.
3. **A level zone (EQH/EQL) has `top == bottom`.** A rectangle with no height can render as nothing at all. Every zone is now given at least 1/300th of the chart's visible price range as height, centred on the actual level, so a level reads as a thin band at any zoom.

Also changed: orphan eviction now works from the names the renderer itself drew, not from enumerating `Chart.Objects`. cTrader documents `Objects` as `IReadOnlyList<ChartObject>` and does not say whether the list is limited to the visible range. If it is, a viewport-driven sweep would delete every zone that had scrolled out of view. `Clear()` still sweeps the chart by prefix so a restart cleans up after a previous session, and both are covered by tests.

The draw log now states how many zones were drawn, how many were skipped as outside the chart's bars, and the chart's own bar range and visible price range:

```
SMT POI drawing: 19 of 21 zone(s) drawn as 57 object(s); 2 skipped as outside the chart.
SMT POI chart: 14820 bars from 2026-08-27 00:00 to 2026-09-18 23:55, visible price 4280.5 to 4311.2
```

## The chart is much smaller than the lookback

The next run reported `0 of 21 zone(s) drawn; 21 skipped as outside the chart`, with the reason right there in the following line:

```
SMT POI chart: 200 bars from 2026-09-09 11:25 to 2026-09-14 03:00
```

The chart holds **200 M5 bars**, which is about 16 trading hours - and most of the span shown is a weekend. The detector was looking back **200 H1 bars**, roughly 8 trading days. Those two numbers are not comparable, and almost every zone origin predates the chart's history.

Two fixes:

- **Zones are clamped into the chart's range instead of being dropped.** A zone that formed before the chart's first bar but is still unmitigated is still live and still worth seeing, so it is drawn from the chart's oldest bar. Dropping those hides exactly the long-lived order blocks that matter most, and did so silently. The log now reports how many zones were clamped.
- **Stopped relying on `TimeSeries.GetIndexByTime`.** Its behaviour for a time that is not an exact bar open is not documented, and an unverified answer here is the same failure as no answer. The renderer now binary-searches the chart's `OpenTimes` itself (the array is newest-first, so it is sorted descending and the containing bar is the smallest index at or before the target). The search is asserted against every bar open and both boundaries.

## The array order bug: zones dated 2013

After the clamping fix the cBot still reported nonsense:

```
SMT POI: first pass over 43147 closed bars found 2 zone(s) (... ATR 31.463, 1 live/gateable)
SMT POI chart: 36139 bars from 2026-04-01 07:05 to 2026-10-02 18:35
SMT POI zones: oldest 2013-05-16 00:00, newest 2013-06-06 00:00
```

43147 H1 bars back from 2026-10 is roughly 4.9 years, and the two zones sat 13 years earlier still. The chart line is what gives the game away: 36139 M5 bars over six months is ~289 bars/day, so `OpenTimes[0]` is the **oldest** bar of the chart, 2026-04-01.

**A series' raw arrays in cTrader are chronological - index 0 is the oldest bar - while a bar index counts back from the newest.** The two are not interchangeable. The detector was filling its window with `source.OpenTimes[firstClosed + i]`, which does not throw; it silently walks the *oldest* part of the history. With `POI Lookback Bars = 20` that is the first 21 bars of the series, and XAUUSD's earliest available data is May 2013. Hence zones dated 2013, all off-chart, and ATR 31.46 measured across gold's May-2013 crash.

The renderer had the mirror-image bug: its binary search assumed a descending array, so `time >= OpenTimes[0]` was true for nearly every time and it returned bar 0 - every zone drawn on the most recent bar.

Two fixes:

- **Chronological index, converted explicitly.** Bar index `c` is chronological index `Count - 1 - c`, in both the detector and the renderer. The renderer's search now walks the array chronologically and converts to a bar index on the way out.
- **The detector refuses a window that is not newest-first.** `_times[0] <= _times[n - 1]` returns an empty snapshot. A wrong array order does not fail loudly anywhere else: every "newer bar" walk in the swing, impulse and mitigation routines would simply run backwards through history and emit confidently wrong zones. No POIs is a neutral input to the gate; 2013 zones are not.

The test harness had been hiding this. Its `Bars` stub reversed the incoming arrays into newest-first order with a comment claiming that was what cTrader does - so the stub encoded the same wrong assumption as the code it was testing, and every test passed. The stub now keeps the arrays chronological like the real API, which is what turned the bug red immediately. New tests pin every zone's `OriginTime` inside the requested window, assert the round trip between chronological and bar orderings, and check that a recent historical time does not collapse onto the newest bar.

The log also had a contributing lie. It printed `_source.Count - 1` - the whole series - as the number of bars scanned, so a 20-bar lookback appeared to have scanned 43147 bars. It now reports the window actually inspected, alongside its oldest and newest bar times:

```
SMT POI: first pass over 20 of 43147 closed bars (2026-10-01 19:00 to 2026-10-02 18:00) found N zone(s)
```

## Lookback is still not comparable to the chart

`POI Lookback Bars` is bounded by the source series, not by the chart, and the two are unrelated. If I want zones visible rather than clamped, I should bound the lookback to roughly the chart's own depth, or compare zone ages against the chart's first bar instead of a fixed bar count.

## Zones drawn near the start of the chart

`Draw SMT POIs` put every rectangle at the left edge instead of at the time it belongs to. The renderer's binary search was finding the right chronological index and then *un-converting* it: `index = Count - 1 - (lo - 1)`, because the code believed a chart bar index counts back from the newest bar.

It does not. A chart bar index **is** the series' chronological index, 0 = oldest. cTrader documents it directly - `DrawRiskReward`'s two overloads are shown as `Chart.LastVisibleBarIndex - 10` and `Bars.OpenTimes[Chart.LastVisibleBarIndex - 10]`, and the drawing guide offers `Bars.OpenTimes[Chart.LastVisibleBarIndex]` as the time form of `Chart.LastVisibleBarIndex` - and `OpenTimes[0]` being the oldest bar was already established above from the chart's own date range. So a zone 50 bars back was being handed to `Chart.DrawRectangle` as bar 50, i.e. 50 bars from the chart's *start*, while old zones were mirrored toward the right.

Two more errors in the same function pushed the same way:

- A time at or after the newest bar (the normal case for a live zone, whose edge is `origin + POI Extend`) returned index 0 - the **oldest** bar - instead of `Count - 1`, so the right edge was pinned to the left edge too.
- The "older than the chart's history" clamp used `Count - 1` for the origin, which under chronological indexing is the newest bar, not the oldest.

Fix, all in `PoiRenderer`: `TryGetBarIndex` returns the chronological index unchanged, clamps future times to `Count - 1`, reports pre-history times to the caller which clamps them to 0, and a zone that lands pinned inside the oldest bar is widened by one bar so it cannot collapse into a zero-width rectangle. The detector's comments that called a shift a "bar index" were reworded as well - that terminology is what produced the bug in the first place, and `BarsSince` in the robot core is the one place a `Count - 1 - index` shift is genuinely correct, because it returns "bars ago", not an index.

Worth re-checking on the next run: the draw log's `clamped to the chart's range` count should be near zero on a chart with real depth, and each zone's rectangle should start under the origin bar named in its `Comment`.

## Zones gate until price leaves them; the drawing budget stopped gating

Three coupled changes, all from the same observation: the robot was only allowed to trade inside the intersection of what it detected, what it drew, and what it had not yet touched.

- **The rectangle budget no longer decides what the robot can trade.** `PoiDetector` truncated its own zone list to `settings.MaxObjects` before building the snapshot, so with `Max POI Objects = 120` (40 zones) the gate saw only the 40 newest zones — the drawing budget was silently choosing the confluence. Detection now keeps everything it finds, and the cap lives in `PoiRenderer`, where the budget is actually spent: newest origins first, the remainder counted as `Capped` and reported in the draw log (`held back by the object budget`). Gate and chart are independent again; lowering `Max POI Objects` can hide a zone but cannot stop an entry inside one.
- **A first touch no longer retires a zone.** Mitigated zones were excluded from the confluence arrays, and a zone price pushed through was dropped from the list outright — only a violated OB escaped, by reborn as a breaker. `AppendZone` now keeps every reached zone marked mitigated, and converts a violated OB to a breaker only when `Show Breaker Blocks` is on; a violated FVG, or an OB with breakers off, is kept as mitigated instead of discarded. The `IsLive` filter in `BuildSnapshot` is gone, and `Poi.IsLive` with it — the proximity test against current price is now the only thing that decides whether a zone counts.
- **Swept EQH/EQL gate at the level only.** Swept liquidity is in the confluence arrays like everything else, and what confines it to "at the level" is the existing proximity test, evaluated against live price on each gate call: away from a taken level it contributes nothing, back within `Confluence Proximity (ATR mult)` it counts again. Nothing extra was needed — `Swept` is only ever set by `DetectLiquidity`, so no other layer can become sweep-tradeable.

`Keep breakers gating` is unchanged: the OB→breaker conversion still depends only on `Show Breaker Blocks`, while `Breaker Counts For Confluence` still decides whether the breaker counts.

The gate is much more permissive now — mitigated zones are the bulk of a lookback window, so `Min Confluence Zones` binds far earlier than before, and the `no zones count toward confluence` fail-open path will rarely trigger. The first-pass log says `gateable` rather than `live/gateable`. Worth confirming on the next backtest that entry counts rise mostly at zones the chart still shows, not somewhere the budget has drawn nothing.

## Touched is tradable, swept is not; stops move to swing high/low ± 0.25 ATR

The previous entry's rule needed one refinement, and the stop rule was replaced outright.

**Touched vs swept.** A touched (mitigated) FVG/OB gates — that stands. A zone price pushed *through* does not: `AppendZone` marks the trade-through `Swept` rather than `Mitigated`, and the new `CountsForGate` refuses any swept OB/FVG outright instead of letting it into the confluence arrays. The zone stays on the chart — the cut-through is drawn where it happened and the hover text reads `swept` — it simply never counts again. Two things are unchanged: an OB violated while `Show Breaker Blocks` is on still reborns as a breaker and gates as one, and a swept EQH/EQL still gates while price is back at its own band, because the proximity test is what confines it. That is the split the two rules asked for: touched tradable, swept not, liquidity the one exception at its own level only.

**Swing stops.** `SlAtrMultiplier × ATR` from the entry is no longer the rule — it survives only as the fallback when no swing can be found (start-up, or a one-way run with no pivot inside the window). The stop is placed beyond the most recent confirmed swing low (buy) / swing high (sell), pushed a further `SL Swing Buffer (ATR mult)` = 0.25 × ATR past it so it clears the wick that formed the pivot. Three parameters carry it: `SL Swing Strength (bars)` 2 — bars each side that must not exceed the pivot, the same definition the POI detector uses; `SL Swing Lookback (bars)` 50 — the scan takes the FIRST pivot whose stop level lies beyond the entry, so a pivot on the wrong side of the price is skipped rather than misused; and the buffer above.

Because the stop is now per side, `_sigStopLossDistance` became `_sigStopLossDistanceBuy` / `_sigStopLossDistanceSell`, each clamped to the broker minimum on its own before the send. Sizing is untouched: `CalculateDynamicVolume` still sizes off the stop distance, so a pivot further away simply buys fewer units for the same risk %.

Watch on the next run: stop distances should cluster just past pivots instead of sitting at a fixed ATR multiple, the `Lot floor` warning should appear when a distant pivot spreads the same risk money over a wider stop, and swept-through zones should be visible on the chart without ever producing an entry.
## Swept breakers and touched-then-swept zones stop gating

`The cBot should not trade swept breakers.` Two holes, one fix, both in `AppendZone`.

**The breaker hole.** A breaker is born when its parent order block is cut through, and it was created *at that bar* with `Swept = false`, `Mitigated = false`, and the scan returned immediately - nothing ever looked at the breaker's own band again. `CountsForGate` already refuses any swept zone that is not EQH/EQL, so the refusal existed; the flag never did. A breaker whose band price traded through the bar after it formed kept gating until it scrolled out of the lookback.

**The first-contact hole.** `AppendZone` stopped at the first bar that reached the zone. A zone touched at bar 50 and cut through at bar 90 stayed `Mitigated = true` and kept gating, though by then it is a swept zone - touched is tradable, swept is not. The early exit had been correct only while a touch retired the zone.

The fix is one scan used by both paths: `ScanContact(start, side, top, bottom, out firstContact, out firstViolated, out sweepBar)` walks forward from the bar after the zone completed - or, for a breaker, from the bar *before* its birth - recording the first contact and continuing until the earliest bar that traded **through** the whole band. From older bars toward newer ones, so the first violation found is the earliest one in time.

- `AppendZone` - untouched is still live with `EndTime = origin + extend`; a first contact that is a violation of an OB with `Show Breaker Blocks` on still converts (and only that first contact converts); everything else carries `Swept = sweepBar >= 0` with `EndTime` moved to the cut-through, and `Mitigated` only when the zone was touched and never cut. An earlier touch no longer shields a later cut-through.
- New `AppendBreaker` - the parent OB's violation is the breaker's *birth*, not its sweep, so its scan starts one bar earlier. Untouched is live from birth (`EndTime` unchanged, the birth bar); touched is a mitigated breaker that still gates; a band traded through is `Swept`, and the existing structural refusal in `CountsForGate` keeps it out of the confluence - no gate-side change was needed.

Conversion stays first-contact-only: an OB retested first and cut through later is the same failed OB, not a breaker - a breaker forms from an immediate failure, not from a retest that later fails. EQH/EQL are untouched: a level has no touch state, `beyond the level` is the only sweep, and trading back AT the level still counts.

Cost: a touched zone now scans to the oldest bar instead of stopping at first contact, which untouched zones already did, so the worst case is unchanged.

Watch on the next run: breakers and zones whose hover text reads `swept` should never produce an entry - including ones that were touched before they were cut - while `mitigated` and `live` zones gate as before; entry counts should drop only in those swept cases.
