# QuantEdge — Swing Trading 30-Minute Slot-Wise Stock Recommendations Plan

## 1. Overview & Objective
In the Swing Trading system, the `SwingTradingIntradayJobWorker` runs every 30 minutes during market hours (**09:15 AM – 03:30 PM IST**).
Currently, the live dashboard reflects the latest evaluated state, but historical recommendations generated during earlier 30-minute intervals (e.g. *09:45 AM, 10:15 AM, 10:45 AM, 11:15 AM*, etc.) are not stored separately by time slot.

This feature enables:
1. **Persistent Storage**: Recording all `BUY` and `WATCH` stock recommendations generated during each 30-minute scan cycle.
2. **Slot-Wise UI Navigation**: Viewing stock recommendations filtered by 30-minute time slots with a dynamic slot selector pill bar and date picker.
3. **SignalR Live Updates**: Automatically pushing newly completed slot recommendations to the UI in real-time.

---

## 2. Database Design (`PostgreSQL`)

### Table: `swing_slot_recommendations`
```sql
CREATE TABLE IF NOT EXISTS swing_slot_recommendations (
    id SERIAL PRIMARY KEY,
    scan_date DATE NOT NULL,
    slot_time TIMESTAMP WITH TIME ZONE NOT NULL,
    slot_label VARCHAR(20) NOT NULL, -- e.g. '09:45 AM', '10:15 AM', '10:45 AM', ...
    symbol VARCHAR(50) NOT NULL,
    decision VARCHAR(20) NOT NULL,    -- 'BUY', 'WATCH'
    score INT NOT NULL,
    confidence_pct NUMERIC(5, 2),
    entry_price NUMERIC(18, 4),
    stop_loss NUMERIC(18, 4),
    target1 NUMERIC(18, 4),
    target2 NUMERIC(18, 4),
    risk_reward_ratio NUMERIC(18, 4),
    volume_multiplier NUMERIC(18, 4),
    rsi14 NUMERIC(18, 4),
    adx14 NUMERIC(18, 4),
    ema20 NUMERIC(18, 4),
    ema50 NUMERIC(18, 4),
    ema200 NUMERIC(18, 4),
    passed_rules TEXT,
    failed_rules TEXT,
    reason TEXT,
    timeframe_used VARCHAR(10) DEFAULT '15m',
    checklist_json JSONB,
    created_at TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS ix_swing_slot_rec_date_slot 
ON swing_slot_recommendations (scan_date, slot_label);

CREATE INDEX IF NOT EXISTS ix_swing_slot_rec_symbol 
ON swing_slot_recommendations (symbol, scan_date);
```

### Stored Procedures
1. `sp_save_swing_slot_recommendations(...)`:
   - Bulk inserts/saves all signals produced in the completed 30-minute slot.
2. `sp_get_swing_scan_slots(p_date DATE)`:
   - Returns all distinct 30-minute slots executed on the specified date with counts of `BUY`, `WATCH`, and total recommendations.
3. `sp_get_swing_slot_recommendations(p_date DATE, p_slot_label VARCHAR)`:
   - Returns all stock recommendation records for the given date and slot label (or all slots for that date if `p_slot_label = 'all'`).

---

## 3. Backend Implementation Architecture

### 3.1 Worker & Service Layer
- **`SwingTradingIntradayJobWorker` / `SwingTradingService.RunIntraday30MinJobAsync`**:
  - Computes the slot label based on current IST time (e.g. `09:45 AM`, `10:15 AM`, `10:45 AM`, etc.).
  - Evaluates active stocks via `SwingDecisionEngine.Evaluate(...)`.
  - Filters signals (`BUY` or `WATCH` with score $\ge 50$).
  - Persists the batch into `swing_slot_recommendations`.
  - Emits SignalR event `ReceiveSwingSlotUpdate` with the newly saved slot info and recommendations.

### 3.2 DTOs & Contracts
- **`SwingScanSlotDto`**:
  ```csharp
  public record SwingScanSlotDto(
      string SlotLabel,
      DateTime SlotTime,
      int BuyCount,
      int WatchCount,
      int TotalCount,
      bool IsLatest
  );
  ```

### 3.3 API Endpoints (`SwingTradingController`)
- `GET /swing/slots?date=YYYY-MM-DD`
  - Returns array of `SwingScanSlotDto` for the selected date.
- `GET /swing/slot-recommendations?date=YYYY-MM-DD&slot=10:15 AM`
  - Returns array of `SwingStockSignalDto` for the selected slot.

---

## 4. Frontend UI Implementation (`Swing Trading`)

### 4.1 UI Components in Recommendations Tab
1. **Slot Navigation & Filter Bar**:
   - 📅 **Date Picker**: Defaults to current date, with capability to select past trading days.
   - ⏱️ **30-Min Slot Pills**:
     - `All Slots (Today)`
     - Dynamic pills for each executed scan slot: `09:45 AM (2)`, `10:15 AM (1)`, `10:45 AM (3)`, `11:15 AM (0)`, ..., `03:30 PM (EOD)`.
     - Active slot highlighted with glowing neon accent.
     - Live indicator badge on the latest slot.
2. **Slot Summary Header**:
   - Displays execution timestamp, market filter status (NIFTY 50 DMA), and total BUY/WATCH signals for that slot.
3. **Recommendations Table & Cards**:
   - Stock Symbol & Exchange
   - Decision badge (`BUY` / `WATCH`)
   - Entry Price, Stop Loss, Target 1, Target 2, Risk:Reward
   - Technical Indicators (RSI, ADX, Moving Averages, Volume Multiplier)
   - Expandable Condition Checklist Drawer (showing all 11 technical rules met/pending).
4. **SignalR Real-time Integration**:
   - Listens for `ReceiveSwingSlotUpdate`.
   - Adds the new slot pill automatically without requiring manual page refresh.
   - Triggers toast notification when new BUY recommendations appear.

---

## 5. Verification & Testing Steps

1. **Database Schema Verification**: Execute table creation and verify index performance.
2. **Job Execution Test**: Run manual scan and confirm records inserted into `swing_slot_recommendations`.
3. **API Endpoint Testing**: Test `/swing/slots` and `/swing/slot-recommendations` for response accuracy.
4. **UI Interaction Testing**:
   - Select different slot pills and verify table filtering.
   - Switch dates and verify previous slot history.
   - Test responsive cards view on mobile/tablet viewports.
