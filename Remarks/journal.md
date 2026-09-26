# Updates

Last updated 2026-09-26

# Findings

First set of optimizations were done with a ~6mo backtest. The first three days were red, with multiple EA's triggering their Max Daily Loss. On Wednesday, I experimented with optimizing using a very short recent data (last 5 trading days). The magic numbers with repeating digits (11111, 22222...) are those with recent-data optimizations. They proved to be more quickly profitable, with the exception of XTIUSD - which, frustratingly, triggered stop-loss before immediately going into what would have been huge profit. I rechecked the backtesting in visual mode and noticed the MT4 strategy tester was not acting "per tick" like I chose, but with open/close (there were "touches" that did not trigger stop-loss). Moving forward, I will use another tester, else I would have to edit the bots to trade strictly on bar close (and not use stop-loss at all).

# MyFXBook

Forward-testing results will be published to https://www.myfxbook.com/portfolio/mt4-7008774/12211857

# Optimization

As mentioned, optimization seems to be profitable faster with recent short-term data. But how often I should optimize still needs exploration

## Optimization Flow

Current method:
- First I use a start set and optimize per session (eg. London):
    - Starting set is the defaults in the cBot. We just need to turn on the session we are optimizing, eg. London: true.
- Find a reliable signal. I optimaze for fastPeriod & slowPeriod, searching in the range: Fast: 10-35, Slow: 13-90. I'm looking for good avarage trade, profit factor, and minimal equity and balance drawdowns. I'm also looking for clusters (eg. fast 20-25 with slow 56-64 all look good; or (14, 13), (16, 15), (21, 20)... pullback signals all look good) and eliminationg outliers.
- Is the SMA filter helping or hindering? I optimize for SMA values from 50 all the way to 350, look for clusters of good results and choose a central point. Then test SMA filter true vs false.
- Will ADX filtering help? I change ADX filtering to true and I test ADX filtering from ~10 to ~60 and compare with when ADX filtering was off. Is is eliminating bad trades?
- SL & TP. Now that I have a reliable signal that survives 2xATR:2xATR risk/reward, I test to see if more breathing room is better. I do not like very narrow SL, so I don't go under 1.5x SL. Testing range: SL 1.5x-5x; TP 1.5x-15x.
- Trailing? After optimizing SL/TP, I check whether a trailing stop captures those near-misses, or if they add to the noise (a lot more trades with similar net profit).

## Optimization Time frame

- Last 5 trading days
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
