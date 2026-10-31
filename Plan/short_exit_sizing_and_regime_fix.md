# Short exit-sizing + regime symmetry fix (Oct 2026)

## 1. Objective

A backtest of 1,950 trades (Oct 2024 - Oct 2026) came back at a **42.4% win rate**. Diagnosis (CSV analysis +
full code trace of `SwingTradeRules`, `MarketRegimeService`, `BacktestEngine`, `AutoRealTradeService`,
`AutoTradeService`) found two structural bugs in the SHORT side of the strategy - not a signal-quality problem:

1. **SHORT stop/target sizing used the same daily-ATR swing-trade distances as the multi-day LONG side**
   (1.5x / 3.0x ATR, roughly 5.65% / 11.7% of entry), on a trade type that is contractually **intraday-only**
   (every short is bought back the same day by 15:05-15:15 IST - cash-segment rule, can't be carried overnight).
   Average realized intraday price excursion on this universe is only about ±1.8%, so **85% of all trades**
   (1,666 / 1,950) never reached stop or target and were instead force-closed on a timer - effectively a coin
   flip. Of the 7.2% of trades that *did* hit a real price level, win rate was only **17.7%**, because the stop
   was sized for a multi-day move, not a few hours.
2. **Regime-based position/risk throttling (`RegimePolicy`) applied only to BUY signals.** SHORTs had their own
   NIFTY-bearish hard filter but faced zero regime-based throttle in any market condition. Combined with #1,
   this flooded the backtest with 87% SHORT trades, 65% of them in `STRONG_BEARISH`.

Goal: fix both, make every new number tunable from the UI without a redeploy, keep SHORT (intraday) and LONG
(swing) sizing as independently configurable parameters, and leave a clear record of every old -> new value.

## 2. What changed - Before -> After

| Setting | Before | After | Why |
|---|---|---|---|
| Short Stop Loss ATR multiple | Shared with LONG: `StopLossAtrMult` = 1.5x daily ATR (~5.65% of entry) | New, independent `ShortStopLossAtrMult` = **0.6x** daily ATR (~2.26% of entry) | Shorts only get a few intraday hours, not multi-day, to reach a stop/target. 0.6x sits just above the observed average adverse excursion (1.82%), so ordinary noise doesn't stop it out but a real move does. |
| Short Target ATR multiple | Shared with LONG: `TargetAtrMult` = 3.0x daily ATR (~11.7% of entry) | New, independent `ShortTargetAtrMult` = **1.2x** daily ATR (~4.52% of entry) | Same reasoning - a same-day target needs to be reachable in hours. R:R stays ~1:2 (same ratio as the swing side), just rescaled for the holding window. |
| Short Trailing SL ATR multiple | Shared with LONG: `TrailAtrMult` = 3.0x daily ATR | New, independent `ShortTrailAtrMult` = **1.2x** daily ATR | Keeps the trail proportionate to the new, smaller stop - was previously letting a real intraday gain fully reverse before the trail could ever engage. |
| LONG (swing) Stop/Trail/Target ATR multiples | 1.5x / 3.0x / 3.0x | **Unchanged** | Longs are genuinely multi-day swing trades; this bug was specific to shorts. |
| `RegimePolicy` - SHORT min score / max positions | Did not exist; shorts were never throttled by regime, in any market condition | New `ShortMinStockScore` / `ShortMaxPositions` per regime, **mirroring** the existing BUY columns in reverse (generous in bearish regimes, restrictive in bullish ones) - see table below | Removes the one-sided asymmetry where BUYs were throttled hard but SHORTs never were, which (combined with the sizing bug) flooded the backtest with low-quality shorts. |

**New regime policy table** (`regime_policy`, mirrors the existing BUY columns):

| Regime | BUY MinScore / MaxPos (unchanged) | SHORT MinScore / MaxPos (new) |
|---|---|---|
| Bullish | 70 / 10 | 95 / 0 (no shorts in a confirmed uptrend) |
| BullishWeakening | 75 / 7 | 90 / 1 |
| Sideways | 78 / 5 | 82 / 3 |
| Bearish | 82 / 3 | 75 / 7 |
| StrongBearish | 88 / 1 | 70 / 10 |

All six new ATR multiples (Real + Auto Trading) are editable on the **Real Trading** and **Auto Trading**
settings pages, next to their existing Short Entry Cut-off / Short Square-off fields - not the Trade Conditions
page, which governs the 11 scoring conditions (`SwingRuleParameters`), not execution/exit sizing. That split is
an existing, deliberate architectural separation in this codebase.

**These starting values are not guaranteed-optimal** - they are fully configurable specifically so they can be
tuned empirically against a fresh backtest, without another code change.

## 3. Files changed

- **Core logic**: `QuantEdge.Infrastructure/Services/SwingTradeRules.cs` - new `DefaultShortStopLossAtrMult` /
  `DefaultShortTrailAtrMult` / `DefaultShortTargetAtrMult` constants; `SwingTradeParams` record carries the three
  new fields; `ComputeShortEntryLevels`, `InferShortAtr` and `EvaluateShortExit`'s trail section now read them
  instead of the shared LONG multiples. LONG-side functions are untouched.
- **Regime symmetry**: `QuantEdge.Infrastructure/Services/MarketRegimeService.cs` - `RegimePolicy.ShortMinStockScore`
  / `ShortMaxPositions` (+ defaults), `RegimeGate.EvaluateShort`, `GetPoliciesAsync`/`SavePoliciesAsync` SQL.
- **Backtest engine**: `QuantEdge.Infrastructure/Services/Backtest/BacktestEngine.cs` (`GenerateSignals`'s
  `scanShort` block now gates on the regime policy in REGIME mode, mirroring the BUY block),
  `BacktestModels.cs` (`BacktestParams`), `BacktestService.cs` (`ResolveAsync`, trade-params construction).
- **Settings plumbing (Real + Auto only - Manual Paper is untouched, see Section 4)**:
  `QuantEdge.Domain/Entities/RealTradeSettings.cs`, `AutoTradeSettings.cs`;
  `QuantEdge.Infrastructure/DTOs/RealTradeDtos.cs`, `AutoTradeDtos.cs`;
  `QuantEdge.Infrastructure/Services/AutoRealTradeService.cs`, `AutoTradeService.cs` (`UpdateSettingsAsync`);
  `QuantEdge.Infrastructure/Persistence/Repositories/RealTradingRepository.cs`, `AutoTradeRepository.cs`;
  `QuantEdge.Infrastructure/Persistence/functions.sql` (`fn_get_real_trade_settings`,
  `fn_get_active_real_trade_settings`, `fn_upsert_real_trade_settings`, and the `auto_trade_settings` equivalents);
  `QuantEdge.Infrastructure/Persistence/schema.sql` (new columns on `real_trade_settings`, `auto_trade_settings`,
  `regime_policy`).
- **UI**: `QuantEdge.Web/Views/RealTrading/Index.cshtml` + `wwwroot/js/realtrading.js`;
  `QuantEdge.Web/Views/AutoTrading/Index.cshtml` + `wwwroot/js/autotrading.js` - three new "Short SL/Trail/Target
  (x ATR)" inputs on each page, next to the existing Short Entry Cut-off / Square-off fields.
- **Tests**: `SwingTradeRulesShortTests.cs`, `BacktestShortSellingTests.cs`, `AutoTradeServiceShortTests.cs`,
  `AutoRealTradeServiceShortTests.cs` updated for the new default values; added
  `ShortAndLongAtrMultiples_AreIndependentlyConfigurable` to lock in that the two sides can't bleed into each
  other again.

## 4. Scope decisions

- **`ManualPaperTradeSettings` / `ManualPaperTradeService` are not touched.** Manual shorts already use their
  own flat-percentage calculation (`ManualPaperTradeService.ComputeShortLevels`), not `SwingTradeRules`, and are
  low-volume/user-initiated - they are not the source of the backtest's win rate.
- **No live order-placement code was changed for regime symmetry.** Tracing `AutoRealTradeService` and
  `AutoTradeService` showed that `MarketRegimeService`'s regime policy is consulted today only by the backtest
  engine and two read-only displays (`SectorDashboardService`, `StockVerdictService`) - **not** by the live/paper
  BUY entry path either. So there was no existing live asymmetry to fix: both sides are equally ungated live
  today, and that stays true after this change. The new `RegimePolicy.Short*` fields are fully wired into the
  backtest now; if regime gating is later added to the live BUY entry path, the short side will already have its
  mirrored fields ready with zero extra work.
- **No new admin UI for `RegimePolicy`** - none existed for the BUY columns either (DB-seeded, edited via
  `SavePoliciesAsync` directly). The new Short* columns follow the same pattern. Building a regime-policy admin
  screen is a reasonable follow-up, not a gap introduced here.

## 5. Known issues - documented, not fixed this round

- **35 trades showed `Sessions = 8` against a 7-trading-day max-duration setting.** Traced to per-symbol
  session-index gaps on sparsely-traded stocks (a stock with a trading halt advances its own session counter by
  2 instead of 1 on the next available day) - not a logic bug in the max-duration check itself, which still
  fires correctly once `tradingDaysOpen >= maxDurationDays`. Left as-is.
- **160 trades (8.2%) used "no bar stored at the square-off time - last close of the day used".** This is a
  backtest-only candle-data-completeness artifact (`BacktestEngine`'s `MissingBarSquareOffReason`) - live trading
  runs on ticks and never depends on a stored candle existing, so this never happens live. Worth a look at
  15-minute candle ingestion gaps for the affected symbols/dates, but it's a data question, not a strategy bug.
- **Duplicate stop/target calculation.** `SwingDecisionEngine`/`SwingShortDecisionEngine` compute their own
  stop/target (`StopAtrMultiple` = 1.5, `Target1R` = 2) purely for the RISK_REWARD *scoring* factor (7 of 100
  points) - these are never the levels an order is actually placed with (that's `SwingTradeRules.
  Compute(Short)EntryLevels`, fixed in this change). Left as-is: changing the scoring formula would alter which
  signals qualify as BUY/SHORT at all, a separate and riskier change than execution sizing.

## 6. Expectation setting

With a ~1:2 R:R strategy, 80% win rate was never a realistic target even with these fixes - roughly 45-55% with
positive expectancy is a more realistic outcome once the mechanics are sound. **Re-run a fresh backtest over the
same period to measure the actual effect of this change before tuning further** - see the main implementation
plan's Verification section for the exact comparison to make against the 42.4%/85%-forced-exit/17.7%-real-win-rate
baseline this fix was built from.
