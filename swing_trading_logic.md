# QuantEdge — Multi-Timeframe Swing Trading Strategy Specification

This document details the complete quantitative logic, mathematical formulas, multi-timeframe candle mapping (`1d`, `15m`, `60m`), Hard Filters, 100-Point Weighted Scoring Matrix, Risk Management Engine, and Trade Exit Rules implemented in the **QuantEdge Swing Trading Engine**.

---

## 1. Executive System Flow

```mermaid
flowchart TD
    A[30-Min Worker Trigger<br/>Candle-Close Aligned] --> B{Market Hours & Data Check<br/>09:15 AM - 03:30 PM IST}
    B -->|Passed| C[Stage A: 1D Hard Filters<br/>Mandatory Gate]
    B -->|Holiday / Closed| Z[Idle / Skip Cycle]
    
    C -->|1. MARKET_FILTER<br/>2. EMA_TREND<br/>3. ADX_STRENGTH| D{All 3 Hard<br/>Filters Passed?}
    
    D -->|NO| E[Status: REJECT<br/>Score = 0 | Skip Scoring]
    D -->|YES| F[Stage B: 100-Point Weighted Scoring Matrix]
    
    F --> G[4. BREAKOUT_GROUP - 20 pts<br/>5. VOL_CONFIRMATION - 15 pts<br/>6. RELATIVE_STRENGTH - 15 pts<br/>7. MULTITIMEFRAME - 15 pts<br/>8. RSI_MOMENTUM - 10 pts<br/>9. MACD_BULLISH - 10 pts<br/>10. BULLISH_CANDLE - 8 pts<br/>11. RISK_REWARD - 7 pts]
    
    G --> H[Stage C: Position Deduplication Check]
    H -->|Already Open?| I[Downgrade BUY -> WATCH<br/>Prevent Duplicate Position]
    H -->|Not Open| J{Total Score<br/>Threshold Check}
    
    J -->|Score >= 70| K[Signal: BUY]
    J -->|Score 50 - 69| L[Signal: WATCH]
    J -->|Score < 50| M[Signal: NO SIGNAL]
    
    K --> N[Stage D: Risk & Position Sizing Engine<br/>1.5x 15m ATR SL | 1:2 Target | 1% Capital Risk]
    N --> O[Dual Output Dispatch]
    O --> P[Swing Dashboard - SignalR Live Stream]
    O --> Q[Paper Trade Execution Engine]
```

---

## 2. Multi-Timeframe Architecture Overview

| Timeframe | Layer / Purpose | Primary Responsibilities & Indicators |
| :-: | :--- | :--- |
| **`1d` (Daily)** | **Macro Trend & Safety Filter** | • Broad Market Health (`NIFTY 50 > 50 DMA` & `EMA20 > EMA50`)<br/>• Stock Moving Average Alignment (`Close > EMA20 > EMA50`)<br/>• ADX Trend Strength (`ADX >= 20.0`)<br/>• Relative Strength vs NIFTY 50 (1M / 3M return) |
| **`15m` (Intraday)** | **Live Entry & Risk Engine** | • Intraday Breakout over Previous Day High (PDH) / Swing High<br/>• Volume Expansion (`15m Vol >= 2.5x 15m Avg Vol`)<br/>• RSI Momentum Zone (`15m RSI 50–75`)<br/>• MACD Crossover (`15m MACD > Signal`)<br/>• Bullish Candle Pattern & 15m ATR Stop Loss |
| **`60m` (Hourly)** | **Intermediate Trend Confirmation** | • 1-Hour Trend Support (`60m Close > 60m EMA20`)<br/>• 1-Hour RSI Momentum (`60m RSI >= 40`) |

---

## 3. Stage A: Mandatory Hard Filters (1D Timeframe)

Hard Filters act as a **strict security gate**. If **ANY single filter fails**, the system immediately flags the stock as **`REJECT`** with a score of `0` and halts further indicator computations for that symbol.

| # | Filter Key | Timeframe | Exact Formula / Condition | Failure Reason |
| :-: | :--- | :-: | :--- | :--- |
| **1** | `MARKET_FILTER` | `1d` (NIFTY) | $\text{Close}_{\text{NIFTY}} > \text{SMA50}_{\text{NIFTY}} \quad \mathbf{AND} \quad \text{EMA20}_{\text{NIFTY}} > \text{EMA50}_{\text{NIFTY}}$ | Market broad trend in correction / defensive mode |
| **2** | `EMA_TREND` | `1d` (Stock) | $\text{Close} > \text{EMA20} > \text{EMA50} \quad \mathbf{AND} \quad \text{EMA20}_{\text{slope}} > 0 \quad \mathbf{AND} \quad \text{EMA50}_{\text{slope}} > 0$ | Stock trend structure weak or below moving averages |
| **3** | `ADX_STRENGTH` | `1d` (Stock) | $\text{ADX}(14) \ge 20.0$ | Trend weak or sideways (choppy market filter) |

---

## 4. Stage B: 100-Point Weighted Scoring Matrix

Evaluated **ONLY** when all 3 Hard Filters pass. Points are accumulated up to a maximum of **100 points**.

### Breakdown of Scoring Factors

```
Total Score = BREAKOUT_GROUP (20) + VOL_CONFIRMATION (15) + RELATIVE_STRENGTH (15)
            + MULTITIMEFRAME (15) + RSI_MOMENTUM (10) + MACD_BULLISH (10)
            + BULLISH_CANDLE (8)  + RISK_REWARD (7)
```

| # | Factor Key | Timeframe | Weight | Mathematical Condition & Scoring Rules |
| :-: | :--- | :-: | :-: | :--- |
| **4** | `BREAKOUT_GROUP` | `15m` / `1d` | **20 Pts** | **MAX of:**<br/>• $15\text{m Close} > \text{Previous Day High (PDH)}$ OR Swing High (+20 pts)<br/>• Breakout after 10–20 session consolidation ($\text{Range} \le 10\%$) (+20 pts)<br/>• $\text{Close} \ge 90\% \text{ of 52-Week High}$ (+20 pts) |
| **5** | `VOL_CONFIRMATION` | `15m` | **15 Pts** | • $15\text{m Vol} \ge 2.5\times \text{SMA}_{\text{Vol}}(20)$ AND $15\text{m Vol} > \text{Prev } 15\text{m Vol}$ (+15 pts)<br/>• $15\text{m Vol} \ge 1.5\times \text{SMA}_{\text{Vol}}(20)$ (+10 pts) |
| **6** | `RELATIVE_STRENGTH` | `1d` | **15 Pts** | $\text{Return}_{\text{Stock}}(1\text{M or }3\text{M}) > \text{Return}_{\text{NIFTY}}(1\text{M or }3\text{M})$ (+15 pts) |
| **7** | `MULTITIMEFRAME` | `60m` | **15 Pts** | $60\text{m Close} > 60\text{m EMA20} \quad \mathbf{AND} \quad 60\text{m RSI}(14) \ge 40.0$ (+15 pts) |
| **8** | `RSI_MOMENTUM` | `15m` | **10 Pts** | • $15\text{m RSI} \in [55, 70]$ (Sweet Spot) (+10 pts)<br/>• $15\text{m RSI} \in [50, 55) \cup (70, 75]$ (+7 pts) |
| **9** | `MACD_BULLISH` | `15m` | **10 Pts** | $15\text{m MACD Line} > 15\text{m Signal Line}$ (+10 pts) |
| **10** | `BULLISH_CANDLE` | `15m` | **8 Pts** | $15\text{m Close} > 15\text{m Open} \quad \mathbf{AND} \quad (\text{Close Near High} \ge 75\% \mathbf{OR} \Delta \text{Price} \ge 1.2\times 15\text{m ATR})$ (+8 pts) |
| **11** | `RISK_REWARD` | `15m` | **7 Pts** | $\text{Risk-to-Reward Ratio} = \frac{\text{Target 1} - \text{Entry}}{\text{Entry} - \text{Stop Loss}} \ge 2.0$ (+7 pts) |

---

## 5. Stage C: Signal Decision Thresholds

| Final Decision | Score Range | System Action & Description |
| :-: | :-: | :--- |
| **`BUY`** | **$\ge 70$** | High probability setup. Approved for SignalR streaming and Paper Trade Auto-Execution. |
| **`WATCH`** | **$50 - 69$** | Bullish setup forming, but pending breakout or volume surge. Added to Watchlist. |
| **`NO SIGNAL`** | **$< 50$** | Insufficient score. No action taken. |
| **`REJECT`** | **Hard Filter Fail** | Failed mandatory Market, EMA, or ADX filter. Excluded from watchlist. |

---

## 6. Stage D: Risk Management & Position Sizing Engine

### Stop Loss & Target Formulas

$$\text{Stop Loss (SL)} = \max\left(0.01, \text{Entry Price} - (1.5 \times \text{ATR}_{15\text{m}})\right)$$

$$\text{Risk Per Share} = \text{Entry Price} - \text{Stop Loss}$$

$$\text{Target 1 (1:2 R:R)} = \text{Entry Price} + (2.0 \times \text{Risk Per Share})$$

$$\text{Target 2 (1:3 R:R)} = \text{Entry Price} + (3.0 \times \text{Risk Per Share})$$

### Position Sizing Rule (1% Account Risk)

$$\text{Max Risk Capital} = \text{Total Portfolio Value} \times 0.01$$

$$\text{Recommended Quantity} = \left\lfloor \frac{\text{Max Risk Capital}}{\text{Risk Per Share}} \right\rfloor$$

*Example:* With ₹10,00,000 portfolio value and ₹20 risk per share:
$$\text{Max Risk Capital} = 10,00,000 \times 0.01 = \text{₹10,000}$$
$$\text{Quantity} = \left\lfloor \frac{10,000}{20} \right\rfloor = 500 \text{ Shares}$$

---

## 7. Trade Exit Engine (Position Closure Rules)

For any active open trade in the portfolio, the position is monitored and closed based on **4 strict rules**:

```
                       [Active Trade Monitoring]
                                   │
       ┌───────────────────────────┼───────────────────────────┐
       ▼                           ▼                           ▼
1. Hard SL Hit              2. Target 1 Reached         3. Trailing SL Hit
Price <= Initial SL        Price >= Target 1           15m Close < 15m Supertrend
       │                           │                           │
       ▼                           ▼                           ▼
 [100% Exit at SL]          [50% Quantity Booked]       [100% Exit at Trailing SL]
                            [Move SL to Cost Price]
```

1. **Hard Stop Loss Hit:** Exit 100% quantity if $15\text{m Price} \le \text{Stop Loss}$.
2. **Target 1 Booking (1:2 R:R):** Book 50% profits when $15\text{m Price} \ge \text{Target 1}$, and trail Stop Loss to Cost Price (Break-even).
3. **Trailing Stop Loss (Trend Reversal):** Exit remaining quantity if $15\text{m Close} < 15\text{m Supertrend(7,3)}$ OR $15\text{m Close} < 15\text{m EMA20}$.
4. **Emergency Defensive Exit:** Exit all positions defensively if NIFTY 50 fails `MARKET_FILTER` ($NIFTY < 50 \text{ DMA}$).

---

## 8. Code Reference Architecture

| Component | Class / File Path | Key Responsibilities |
| :--- | :--- | :--- |
| **Decision Engine** | [SwingDecisionEngine.cs](file:///d:/LearningProject/QuantEdge/QuantEdge.Infrastructure/Services/SwingDecisionEngine.cs) | Implements Hard Filters, 100-Point Matrix, and Checklist DTO generation. |
| **Service Layer** | [SwingTradingService.cs](file:///d:/LearningProject/QuantEdge/QuantEdge.Infrastructure/Services/SwingTradingService.cs) | Syncs `15m`/`1d` candles, performs position deduplication, and streams SignalR payload. |
| **Background Worker** | [SwingTradingIntradayJobWorker.cs](file:///d:/LearningProject/QuantEdge/QuantEdge.Worker/Workers/SwingTradingIntradayJobWorker.cs) | Triggers 30-minute scan cycle during market trading hours (09:15 AM – 03:30 PM IST). |
| **Dashboard DTOs** | [SwingTradingDashboardDto.cs](file:///d:/LearningProject/QuantEdge/QuantEdge.Infrastructure/DTOs/SwingTradingDashboardDto.cs) | Data contract for `SwingStockSignalDto` with Hard Filter and Position Sizing properties. |
| **Web UI Views** | [Index.cshtml](file:///d:/LearningProject/QuantEdge/QuantEdge.Web/Views/SwingTrading/Index.cshtml) | Renders Swing Dashboard UI, active scan status badges, and signal tables. |
| **Strategy Formulas Modal** | [_StrategyFormulasModal.cshtml](file:///d:/LearningProject/QuantEdge/QuantEdge.Web/Views/Shared/_StrategyFormulasModal.cshtml) | Renders in-app strategy formulas reference documentation for users. |
