# QuantEdge — Configurable Trade Conditions (11-Condition Profile) Plan

## 1. Objective
The BUY engine (`SwingDecisionEngine`) and the SHORT engine (`SwingShortDecisionEngine`) judge every stock on the same
**11 conditions**: 3 hard filters plus 8 scoring factors. Today all 11 are always applied, for every user.

This feature adds a new **Trade Conditions** page where each user chooses which of the 11 conditions the bot applies.
The choice is saved per user, per trading mode (Auto Paper / Auto Real) and per side (BUY / SHORT). The scan workers
then trade by that selection.

**Non-negotiable:** the default profile (all 11 ON) must produce exactly today's decisions. Existing users see no
change until they edit their profile.

### The 11 conditions (stable keys)
| # | Key | Name | Type | Max pts | BUY rule | SHORT rule |
|---|---|---|---|---|---|---|
| 1 | `MARKET` | NIFTY Market Filter | Hard | – | NIFTY Close > SMA50 & EMA20 > EMA50 | NIFTY Close < SMA50 & EMA20 < EMA50 |
| 2 | `TREND` | EMA Trend | Hard | – | Close > EMA20 > EMA50, rising, EMA200 stable | Close < EMA20 < EMA50, falling, EMA200 not rising |
| 3 | `ADX` | ADX Strength | Hard | – | Daily ADX(14) ≥ 20 | Same |
| 4 | `BREAKOUT` | Breakout / Breakdown | Score | 20 | Above PDH / swing high / range / near 52W high | Below PDL / swing low / range / near 52W low |
| 5 | `VOLUME` | Volume Confirmation | Score | 15 | ≥ 1.5x usual (2.5x = full) | Same |
| 6 | `REL_STRENGTH` | Relative Strength / Weakness | Score | 15 | 1M return > NIFTY | 1M return < NIFTY |
| 7 | `MTF_60M` | 60m Confirmation | Score | 15 | 60m Close > EMA20 & RSI ≥ 40 | 60m Close < EMA20 & RSI ≤ 60 |
| 8 | `RSI` | RSI Zone (15m) | Score | 10 | 50–75 (55–70 best) | 25–50 (30–45 best) |
| 9 | `MACD` | MACD (15m) | Score | 10 | MACD > Signal | MACD < Signal |
| 10 | `CANDLE` | Candle Pattern (15m) | Score | 8 | Green, close near high / big body | Red, close near low / big body |
| 11 | `RISK_REWARD` | Risk : Reward | Score | 7 | R:R ≥ 1:2 | Same |

---

## 2. Design decisions (recommended defaults - confirm before build)

| # | Decision | Recommendation | Why |
|---|---|---|---|
| D1 | Scope of a profile | **Per user × mode (AUTO_PAPER / AUTO_REAL) × side (BUY / SHORT)** | Users commonly test a looser profile on Paper before Real; BUY and SHORT setups differ. |
| D2 | Score when scoring conditions are OFF | **Rescale**: `score = enabledPoints / enabledMaxPoints × 100` | Keeps `BuyScoreThreshold` (70) and `WatchScoreThreshold` (50) meaningful. Raw scores would make e.g. "only RSI + MACD" impossible to ever reach 70. |
| D3 | `MinConditionsMatch` | Counted **out of the enabled conditions** ("8 of 9 enabled"); saved value is clamped to 1..enabledCount | "10/11" means nothing once 3 conditions are off. |
| D4 | What a condition can do in phase 1 | **On/Off only.** Editable thresholds (ADX 20, RSI band, volume multiple…) come in Phase 6 | Keeps the first release small and easy to verify. |
| D5 | Condition #1 in REGIME mode | **Locked / shown as "Managed by Market Regime"** | In REGIME mode the regime policy already replaces the NIFTY filter (`RegimeGate.WithoutNiftyGate`). |
| D6 | Minimum selection | At least **one scoring condition (4–11)** must stay ON | With none, there is no score to compare to the threshold. |
| D7 | Turning OFF a hard filter on AUTO_REAL | Allowed, but needs an explicit **"I accept the risk"** checkbox (same pattern as Auto Short risk acceptance), logged to the audit trail | Hard filters are the main protection against buying into downtrends. |
| D8 | Which screens follow the profile | **Phase 1: only the bots** (Auto Paper / Auto Real scan workers). Signal Dashboard / Stock Verdict / Sector get a "Show with my profile" switch in Phase 5. Manual Paper is unaffected (manual trades are not condition-driven). | Shared dashboards keep one reference view; the bot follows the user. |

---

## 3. Engine changes (`QuantEdge.Infrastructure/Services`)

### 3.1 Condition catalog - `TradeConditions.cs` (new)
A single static catalog of the 11 conditions: `Key`, `Number`, `Name`, `IsHardFilter`, `MaxPoints`, `BuyRuleText`,
`ShortRuleText`, plus the checklist/factor codes each engine already writes (`HARD_EMA_TREND` / `HARD_EMA_DOWNTREND`,
`BREAKOUT_GROUP` / `BREAKDOWN_GROUP`, `MACD_BULLISH` / `MACD_BEARISH`, …). The UI and API both read texts from here
and never duplicate them.

### 3.2 Always score all 11
`SwingDecisionEngine.Evaluate` and `SwingShortDecisionEngine.Evaluate` currently `return` as soon as a hard filter
fails, so factors 4–11 are never computed. Change them to:
1. Compute the 3 hard-filter flags (unchanged).
2. **Always** compute the 8 factors, `Factors`, `StopLoss`, `Target1` and `DailyAtr`.
3. Set `Decision` / `Score` / `HardFiltersPassed` exactly as today (hard fail ⇒ `REJECT`, score 0), so every
   existing caller sees identical output.
4. New field `RawFactors` / per-item `IsMet` on the checklist is already present - nothing is lost.

Small cost: ~8 indicator calculations more for stocks that fail a hard filter. Measure on a full scan (target < 10%
slower).

### 3.3 Apply a profile - `ConditionProfileEvaluator.cs` (new, pure static)
```csharp
public static ProfiledResult Apply(SwingEvaluationResult r, ConditionProfile p, SwingStrategySettings s, bool isShort)
```
- **Hard filters:** only the enabled ones must pass (read from the checklist item `IsMet`). Condition #1 is skipped
  when the profile disables it or REGIME mode is on.
- **Score:** sum of `Factors.Points` for enabled scoring conditions, rescaled per D2, minus the soft-mode NIFTY
  penalty only if `MARKET` is enabled.
- **Decision:** `BUY`/`SHORT` ≥ `BuyScoreThreshold`, `WATCH` ≥ `WatchScoreThreshold`, else `NO SIGNAL`.
- **MetCount / TotalCount:** counted over enabled conditions only.
- Returns the reasons in the existing `PassedRules` / `FailedRules` style plus `"<Name> - OFF in your profile"` for
  skipped ones, so audit logs stay readable.

`ConditionProfile.AllOn` is the default; `Apply(r, AllOn, …)` must equal the engine's own result (parity test).

---

## 4. Database (`schema.sql`, `PostgreSQL`)

```sql
-- One row per user x mode x side x condition. Missing rows = condition ON (default profile).
CREATE TABLE IF NOT EXISTS trade_condition_profiles (
    user_id        INT          NOT NULL,
    trade_mode     VARCHAR(20)  NOT NULL,   -- 'AUTO_PAPER' | 'AUTO_REAL'
    side           VARCHAR(10)  NOT NULL,   -- 'BUY' | 'SHORT'
    condition_key  VARCHAR(30)  NOT NULL,   -- TradeConditions keys
    is_enabled     BOOLEAN      NOT NULL DEFAULT TRUE,
    params         JSONB        NULL,       -- reserved for Phase 6 (editable thresholds)
    risk_accepted_at TIMESTAMPTZ NULL,      -- D7: set when a hard filter is turned off on AUTO_REAL
    updated_at     TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    PRIMARY KEY (user_id, trade_mode, side, condition_key),
    CONSTRAINT chk_tcp_mode CHECK (trade_mode IN ('AUTO_PAPER','AUTO_REAL')),
    CONSTRAINT chk_tcp_side CHECK (side IN ('BUY','SHORT'))
);
```
- Row-per-condition (not 11 boolean columns) so Phase 6 thresholds fit in `params` without another migration.
- Every save writes an audit entry (`CONDITION_PROFILE_CHANGED`, old → new) to the existing execution log of that mode.
- **Open item:** `AutoTradeSettings.UserId` is a `string` (`"default_user"`) while Real/Manual use `int`. Map Auto
  Paper to the app user id the same way `AutoTradeSettings` is resolved today, or store `user_ref VARCHAR`.

---

## 5. Backend: repository, service, API

| Layer | Item |
|---|---|
| Repository | `ITradeConditionProfileRepository` / `TradeConditionProfileRepository` (Dapper, same style as `SwingStrategySettingsRepository`): `GetAsync(userId, mode)`, `SaveAsync(userId, mode, side, rows)`. |
| Service | `ITradeConditionProfileService`: validates D3/D6/D7, caches profiles in `ICacheService` (invalidate on save), exposes `GetProfileAsync(userId, mode, side)`. |
| API | New `TradeConditionsController` (`QuantEdge.API/Controllers`): |
| | `GET  /api/trade-conditions/catalog` - the 11 definitions (texts, type, points). |
| | `GET  /api/trade-conditions?userId=&mode=AUTO_REAL` - BUY and SHORT profile + `MinConditionsMatch` + thresholds + gate mode. |
| | `PUT  /api/trade-conditions` - save one mode (both sides), with `riskAccepted` flag. |
| | `POST /api/trade-conditions/reset` - delete rows (back to all ON). |

---

## 6. Scan worker integration (`QuantEdge.Worker/Workers`)

### `AutoTradeSignalScanWorker` (per user already)
- Load the user's AUTO_PAPER BUY and SHORT profiles once per scan.
- **Market gate:** skip the NIFTY "no entries this scan" early-exit when that user's `MARKET` condition is OFF
  (REGIME mode unchanged).
- Evaluate with NIFTY-gate-free engine settings, then `ConditionProfileEvaluator.Apply(...)`; qualify on
  `IsBuySignal || MetCount >= MinConditionsMatch` exactly as now, but with profiled values.

### `AutoRealTradeSignalScanWorker` (single pass, many users)
- Evaluate each stock **once** with all 11 computed (§3.2) and keep the raw results.
- Per user: apply their AUTO_REAL profile, then the same candidate/threshold logic as today. Applying a profile is
  pure arithmetic over the existing factors - no extra indicator work per user.
- The global `longGateOpen` / `shortGateOpen` NIFTY checks become per-user (OFF users are not blocked).

### Services
`AutoTradeService` / `AutoRealTradeService` keep all 13 pre-trade guards untouched. Guard 5 ("Signal strong
enough?") receives the profiled met-count and the audit text shows `x/N enabled` instead of the fixed `/11`.

---

## 7. UI - new page "Trade Conditions"

- `AppPages.TradeConditions` (group *Trading & Signals*, `DefaultForUser = true`) + sidebar entry + `[PageAccess]`.
- `QuantEdge.Web/Controllers/TradeConditionsController.cs`, `Views/TradeConditions/Index.cshtml`,
  `wwwroot/js/trade-conditions.js` (same fetch/toast patterns as `realtrading.js`).
- Links from the Auto Paper and Auto Real settings panels ("Configure conditions →"); their `MinConditionsMatch`
  input gets `max = enabled count`.

```
┌ Trade Conditions ───────────────────────────────────────────────────────────────┐
│ Mode: [ Auto Paper ] [ Auto Real ]        Gate mode: NIFTY_FILTER               │
│ Side: [ BUY ] [ SHORT ]                                                           │
├────┬──────────────────────────┬────────┬──────┬───────────────────────────┬──────┤
│ #  │ Condition                │ Type   │ Pts  │ Rule (BUY)                │  On  │
├────┼──────────────────────────┼────────┼──────┼───────────────────────────┼──────┤
│ 1  │ NIFTY Market Filter      │ HARD   │  –   │ NIFTY > 50DMA, EMA20>EMA50│ [■]  │
│ 2  │ EMA Trend                │ HARD   │  –   │ Close > EMA20 > EMA50 …   │ [■]  │
│ 3  │ ADX Strength             │ HARD   │  –   │ ADX(14) ≥ 20              │ [■]  │
│ 4  │ Breakout                 │ SCORE  │ 20   │ Above PDH / swing / 52W   │ [■]  │
│ …  │ …                        │        │      │                           │      │
│ 11 │ Risk : Reward            │ SCORE  │  7   │ R:R ≥ 1:2                 │ [□]  │
├────┴──────────────────────────┴────────┴──────┴───────────────────────────┴──────┤
│ Enabled: 10 of 11  ·  Scoring points in use: 93 → rescaled to 100                │
│ Min conditions to match: [ 9 ] of 10   ·   BUY ≥ 70   WATCH ≥ 50                 │
│ ⚠ A hard filter is OFF - the bot may buy against the trend. [ ] I accept the risk │
│                                                                                  │
│ [Reset to default]  [Copy BUY → SHORT]  [Copy to Auto Real]           [ Save ]   │
└──────────────────────────────────────────────────────────────────────────────────┘
```
Behaviour:
- Toggles show live "points in use" and clamp the Min-conditions input.
- Condition #1 row is disabled with "Managed by Market Regime" when gate mode is REGIME (D5).
- Save is blocked (with a message) if no scoring condition is on (D6), or if a hard filter is off on Auto Real
  without the risk checkbox (D7).
- SignalR / toast on save; profile change appears in the mode's execution log.

---

## 8. Phases

| Phase | Scope | Done when |
|---|---|---|
| 1 | Catalog, engine "always score all 11", `ConditionProfileEvaluator`, unit tests | Parity tests green; all dashboards unchanged |
| 2 | Table, repository, service + cache, API | API save/load/reset/preview working |
| 3 | Auto Paper worker integration | Paper bot trades by profile; audit shows `x/N enabled` |
| 4 | Trade Conditions page + permissions + links from settings | Users can configure both modes and sides |
| 5 | Auto Real worker integration (after a few days on Paper); Backtest takes a profile; "Show with my profile" on Signal Dashboard / Stock Verdict | Real bot trades by profile; a profile can be backtested before use |
| 6 (optional) | Editable thresholds per condition via `params` (ADX min, RSI band, volume multiple, R:R min, 52W distance) | Thresholds flow into both engines |

---

## 9. Tests (`QuantEdge.Tests`)
- **Parity:** for a set of fixture stocks (pass, hard-fail, watch, buy - long and short), `Apply(AllOn)` equals the
  engine's own `Decision`, `Score`, `MetCount`.
- **Hard-fail stocks now carry factors:** factors are populated even when `Decision == REJECT`.
- **Each toggle:** turning one condition off changes exactly that condition's effect (hard filter no longer rejects;
  scoring points removed and score rescaled).
- **Rescale maths:** e.g. only RSI (10) + MACD (10) on, both passed → 100; one passed → 50.
- **Validation:** zero scoring conditions rejected; Min-conditions clamped; AUTO_REAL hard-filter off without
  risk acceptance rejected.
- **Workers:** with `MARKET` OFF, a weak NIFTY no longer blocks that user but still blocks a default-profile user in
  the same Real scan.
- **REGIME mode:** condition #1 setting is ignored.

---

## 10. Risks & mitigations
| Risk | Mitigation |
|---|---|
| Users turn off protections and take bad Real trades | D7 risk acceptance + audit; Real integration ships after Paper (Phase 5); backtest a profile before using it |
| Engine change alters existing dashboards | Decision/score semantics unchanged for the default; parity tests |
| Scan slower because failing stocks are now fully scored | Measure; profile application is cheap arithmetic per user |
| Score meaning drifts when conditions are removed | Rescaling (D2) and "points in use" shown on the page |
| Auto Paper `string` user id vs `int` elsewhere | Resolve in Phase 2 (see §4 open item) |

---

## 11. Implementation notes (built 2026-10-28)
What was built differs from the plan above in these points:
- **Storage:** one row per user × mode × side (`disabled_conditions TEXT[]`, `min_conditions_match`, `risk_accepted_at`)
  instead of one row per condition. No row = default. A side saved with all 11 on and the mode setting's min is stored
  with `min_conditions_match = NULL`, so it keeps following the settings form.
- **Engine:** instead of always scoring all 11, both engines take an opt-in `skipHardFilterGate` flag. The workers call it
  only when a custom profile switched off a hard filter that the stock failed (at most once per stock per scan).
- **Default profile = original code path.** Workers only use `TradeConditionRules` for users with a custom profile, so
  users who never open the page trade exactly as before.
- **REGIME mode:** only the BUY side's condition 1 is locked (shorts keep their own NIFTY bearish filter).
- **Min Criteria on the settings forms** stays, relabelled "default": it applies to any side not customised on
  Trade Conditions.
- **Not done yet:** audit entries in the execution logs for profile changes (logged to the application log only),
  Backtest with a profile, "show with my profile" on the dashboards (Phase 5/6).
