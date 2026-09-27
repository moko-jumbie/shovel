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

Testing the first question would take me a long time. I'll try the other 2. I would have chosen somewhere in the middle of the top cluster ~(11,13) or (12,15). Running a signal check for Sep 21-25 revealed:
- (11,13) would have done fairly well: $444.54 profit; PF = 1.41; Average trade way down from previous week @ $27.78.
- (12,15) did similar: $362.64 profit; PF = 1.45; Average trade = $32.97.
- A new "outlier" signal formed: (18,19) [2 trades, $434 average trade] I should check it at the end of the week. Perhaps outliers are what I should choose after all?
- A (new?) good cluster formed aroound (low-mid 30s, mid-high 20s). These values *lost* money the previous week. Eg. (32, 27) ["passId": 387, "fitness": -41.70850583271343, "equity": 9977.239999999998].
- The same stops can provide good results for multiple weeks. What seems to decay/alter results by a significant amount is the signal.
I'll let Kit run a similar signal search for September 7-11 and look for the week-to-week pattern.