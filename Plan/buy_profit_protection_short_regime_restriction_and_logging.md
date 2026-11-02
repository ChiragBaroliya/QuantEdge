# BUY early profit protection + SHORT regime restriction + rule-level logging (Oct 2026)

## 1. Objective

Follow-up to the signal-quality investigation (two parallel research passes over backtest-13/14/15, after the
exit-sizing and regime-throttle fixes already shipped). Three findings, three changes - all off by default,
none touching live trading yet:

1. **BUY**: 46% of all losing BUY trades were up 2%+ before reversing into a full stop-loss, in both
   independent 12-month halves of the backtest window. The trailing stop only starts protecting a gain once
   price is up a full ATR (~3.8%) - before that, it's "ride the original stop or nothing."
2. **SHORT**: shorting on a merely-`BEARISH` day performs far worse (~27% win rate, both years) than shorting
   on a `STRONG_BEARISH` day (~38-43%, both years).
3. **Logging gap**: neither engine recorded *which* sub-condition qualified a breakout/breakdown (a stock
   simply being near its 52-week extreme - a short-squeeze/mean-reversion risk - scored identically to a
   genuine fresh breakout/breakdown).

Two bugs were found and fixed along the way, both pre-existing and unrelated to the backtest numbers above:
- The backtest CSV already computed a per-rule points breakdown (`BacktestTrade.Factors`) but never wrote it
  to the CSV.
- Saving the Trade Conditions page silently reset every regime's SHORT policy (`ShortMinStockScore`/
  `ShortMaxPositions`) back to the class's 82/3 defaults, because `StrategyRulesService.SaveAsync` rebuilt
  each `RegimePolicy` without carrying them over. This blocked "make it configurable" outright, so it was
  fixed as a prerequisite.

## 2. What changed - Before -> After

| Setting | Before | After | Why |
|---|---|---|---|
| LONG trail activation | Fixed at entry + 1x ATR (~3.8%) for every account, not configurable | New `TrailActivationPct` setting (Real + Auto Trading pages), default **0 = disabled** (identical to today) | Lets the activation point move to a flat % gain (e.g. 1% or 1.5%) instead of waiting for a full ATR move, without touching the trail *distance* (`TrailAtrMult`, untouched). The existing breakeven floor (`Math.Max(entry, candidate)`) does the rest: once activated, a loser that was up 1%+ stops at breakeven instead of riding the original stop down. |
| SHORT regime policy for BEARISH | `ShortMinStockScore=75`, `ShortMaxPositions=7` (seed unchanged in `schema.sql`) | **Unchanged by default.** Now genuinely editable from the Trade Conditions page (previously had no live edit path at all, and the one path that existed silently reset it). Candidate to test: `ShortMinStockScore=90`, `ShortMaxPositions=1`. | Directly motivated by the ~27% vs ~38-43% two-year gap. STRONG_BEARISH (`70/10`) is left untouched - already the better-performing, already-prioritized regime. |
| Rule-level logging | CSV only showed aggregate `Score`; no record of which breakout/breakdown sub-condition fired, no indicator snapshot | New CSV columns: `Sub Type`, `Factors` (per-rule points, already computed, previously dropped), `RSI 15m`, `RSI 60m`, `Volume x`, `ADX`, `ATR` | Makes the "near 52-week low/high" hypothesis from the investigation directly testable on the next backtest, instead of a guess. |

**Nothing above changes behavior until you explicitly opt in** via the Real/Auto Trading pages or the Trade
Conditions page. The persisted defaults (`schema.sql` seed rows, entity property defaults) are unchanged.

## 3. Files changed

- **BUY trail activation**: `SwingTradeRules.cs` (`DefaultTrailActivationAtrMult` rename, new `TrailActivationPct`
  field on `SwingTradeParams`, `GetTrailActivationPrice`); `RealTradeSettings.cs`/`AutoTradeSettings.cs`
  (+property); `RealTradeDtos.cs`/`AutoTradeDtos.cs` (+validated property); `AutoRealTradeService.cs`/
  `AutoTradeService.cs` (`UpdateSettingsAsync`); `RealTradingRepository.cs`/`AutoTradeRepository.cs`
  (upsert param); `functions.sql` (6 functions: upsert/get/get-active x Real/Auto); `schema.sql` (new column,
  both tables); `BacktestModels.cs`/`BacktestService.cs` (per-run override, same pattern as the other ATR
  multiples); `RealTrading/Index.cshtml`+`realtrading.js`, `AutoTrading/Index.cshtml`+`autotrading.js` (new
  field); `TradeLogicDtos.cs`+`tradelogic.js` (Trade Flow diagram now shows the active activation rule).
  SHORT's own activation (`EvaluateShortExit`) is untouched - confirmed by a regression test.
- **SHORT regime restriction**: `StrategyRulesService.cs` (`SaveAsync` bug fix + validation + change-logging
  for `ShortMinStockScore`/`ShortMaxPositions`); `strategy-rules.js`+`TradeConditions/Index.cshtml` (two new
  editable columns on the regime-policy table). No change to `MarketRegimeService.cs`, `schema.sql` seed rows,
  or `BacktestEngine.cs` - the mechanism from the prior fix is reused as-is, only the edit path was fixed/added.
- **Rule-level logging**: `SwingDecisionEngine.cs`/`SwingShortDecisionEngine.cs` (`BreakoutSubType`/
  `BreakdownSubType` on the shared `SwingEvaluationResult`); `BacktestModels.cs` (`SubType` + indicator
  snapshot fields on `BacktestSignal`/`BacktestTrade`); `BacktestEngine.cs` (set from `r.BreakoutSubType`/
  `r.BreakdownSubType` at both signal-construction sites); `BacktestPortfolio.cs` (carried into
  `BacktestTrade`); `schema.sql` (`backtest_trades` new columns); `BacktestService.cs` (INSERT/SELECT);
  `BacktestController.cs` (CSV header + row).
- **Tests**: `SwingTradeRulesTrailActivationTests.cs` (new - activation at 0/1.0/1.5%, SHORT-unaffected
  regression), `SwingBreakoutSubTypeTests.cs` (new - sub-type recorded correctly on each side),
  `SwingRuleParametersTests.cs` (new regression test + 2 validation cases for the SaveAsync bug fix).
  Full suite: 337/337 passing (326 pre-existing + 11 new), `dotnet build` clean.

## 4. Validation plan - run these before touching live settings

**Safety rule for the whole validation phase**: leave `TrailActivationPct = 0` on **Real Trading** (live
money) throughout. Only set it on **Auto Trading** (paper) and/or as a backtest run's resolved value. The
SHORT regime-policy change is backtest/display-only today (not consulted by any live order-placement path),
so it carries no live-trading risk by construction - confirmed in the prior investigation and unchanged here.

Four comparison runs, same Oct 2024-Oct 2026 window, **Market regime** gate mode (matching the existing
`backtest-15` baseline):

1. **Baseline** - already have it: `backtest-15-trades.csv`. No rerun needed.
2. **BUY-only**: on the Auto Trading (or Real Trading, since the setting itself doesn't execute live trades in
   a backtest) page, set **Trail Activation (% gain)** to `1.0`, leave the Trade Conditions SHORT columns at
   `75`/`7` for Bearish, rerun. Then repeat with `1.5` for a second data point.
3. **SHORT-only**: set Trail Activation back to `0`, go to Trade Conditions and set Bearish's **Short min
   score** to `90` and **Short max positions** to `1`, rerun.
4. **Both together**: combine whichever of 1.0/1.5 performed better in run 2 with the SHORT change from run 3,
   rerun.

For each run, check: win rate, net P&L after charges, profit factor (sum of gross wins / abs(sum of gross
losses)), average win vs average loss, and - using the new `Sub Type` column - whether `NEAR_52W_HIGH`/
`NEAR_52W_LOW`-only trades (no `PRIOR_HIGH`/`PRIOR_LOW` or `CONSOLIDATION` alongside) have a materially worse
win rate than fresh-breakout/breakdown trades, which would confirm the investigation's open hypothesis.
Split any apparent improvement by entry date (first vs second 12 months) before trusting it, same as the
original investigation.

Send me the CSVs the same way as before and I'll run the comparison and report back - including anything that
looks like a regression - before recommending any change to the live `RealTradeSettings` defaults or a move
to paper/live trading.
