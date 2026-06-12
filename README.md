# QuantEdge Trading Platform

QuantEdge is a real-time, AI-assisted trading platform designed to analyze market conditions and deliver high-probability buy, sell, and hold recommendations. 

The platform is designed using modern software engineering patterns, embracing **Clean Architecture**, **SOLID Principles**, and robust **Thread-Safe concurrency** in .NET 10.

---

## Solutions & Modules

### 1. `QuantEdge.Worker` (Worker Service)
A dedicated microservice project responsible for establishing real-time connections to market feed streams (Mock, Live, or Zerodha Kite Connect WebSocket), aggregating raw transactions into multi-timeframe OHLCV candlesticks, and persisting them in PostgreSQL.

#### Folder Structure & Organization:
*   **`Configurations/`**: Options parameters mapping broker connection details and credentials.
*   **`Constants/`**: Platform-wide definitions such as supported asset symbols (`NIFTY`, `BANKNIFTY`) and chart timeframes (e.g. 5-second, 1-minute, 5-minute).
*   **`DTOs/`**: Immutable record models defining the core messaging contract for `TickDataDto`, `CandleDto`, and `MarketDepthDto`.
*   **`Models/`**: Domain models like order book `DepthLevel`.
*   **`Interfaces/`**: Strongly typed, decoupled service contracts:
    *   `IWebSocketMarketDataService`: Connects and streams feed ticks and book depth.
    *   `ICandleBuilderService`: Takes raw ticks and aggregates them into multi-interval candlesticks.
    *   `IMarketDataProcessor`: Core orchestrator receiving and routing raw events to aggregators.
    *   `IReconnectPolicyService`: Computes exponential backoff connection restore delays.
*   **`Services/`**: Realizations of the service contracts:
    *   `WebSocketMarketDataService`: Production live WebSocket integration service. Implements automatic session reconnects, heartbeat ping triggers every 25 seconds, local symbol caches, and flexible parsing.
    *   `ZerodhaWebSocketMarketDataService`: Production live Zerodha Kite Connect WebSocket service using the official `Tech.Zerodha.KiteConnect` library, mapping instrument tokens to symbols, and managing auto-reconnections.
    *   `WebSocketConnectionManager`: Thread-safely wraps `ClientWebSocket` with `SemaphoreSlim` send locks and yields an async stream (`IAsyncEnumerable<string>`) of text chunks.
    *   `ReconnectPolicyService`: Computes exponential delays with 30% random jitter to avoid thundering herd conditions.
    *   `CandleBuilderService`: Performs fine-grained local locking per asset & timeframe key, ensuring zero race conditions under high concurrent tick frequencies while preserving in-memory circular logs of completed candles.
    *   `MockWebSocketMarketDataService`: Emulates real-world broker stream sessions, generating Geometric random walks for `NIFTY` and `BANKNIFTY`.
    *   `MarketDataProcessor`: Decoupled pipeline routing raw ticks to indicators and aggregate builders, and persisting closed candles into PostgreSQL via high-performance Dapper repositories.
*   **`Workers/`**: Features `MarketDataFeedWorker` inheriting from standard `BackgroundService` to manage long-running data feeds.
*   **`Extensions/`**: Houses `ServiceCollectionExtensions` to register all Worker components into Dependency Injection containers with a single extension call.


---

## Design and Concurrency Strategy

1.  **Thread Safety**: 
    The `CandleBuilderService` uses a fine-grained concurrency model. Instead of global locks that block all incoming ticks, it locks on a per-symbol, per-interval state basis using a `ConcurrentDictionary`. This allows ticks for `NIFTY` and `BANKNIFTY` to be processed in parallel across multiple CPU cores without race conditions or memory corruption.
2.  **Dependency Inversion**:
    High-level orchestration components (`MarketDataProcessor`, `MarketDataFeedWorker`) interact solely through abstraction interfaces (`IWebSocketMarketDataService`, `ICandleBuilderService`), isolating the core logic from specific broker connection details or data providers.
3.  **Broker Agnosticism**:
    The system is configured via `BrokerConfig`, paving a clear path for future production integrations (such as the **Zerodha Kite Connect WebSocket**, AngelOne, or the **Grow API**) by simply swapping out `IWebSocketMarketDataService` implementations.
4.  **Live WebSocket Concurrency & Resilience**:
    *   `ClientWebSocket` Concurrency: Since `ClientWebSocket` does not support concurrent send/receive execution, `WebSocketConnectionManager` thread-safely throttles transmissions using a `SemaphoreSlim`.
    *   Asynchronous Stream Processing: Incoming payloads are streamed as they arrive using C# 10 `IAsyncEnumerable<string>` streams, minimizing memory allocations.
    *   Resilience & Reconnection: Reconnection calculates exponential backoffs with a 30% randomized jitter. Symbol subscription lists are locally stored in a thread-safe `ConcurrentDictionary` and dynamically restored on connection recovery.
    *   Heartbeats: A secondary background heartbeat worker pings the WebSocket host every 25 seconds to preserve active channel connections.
    *   Zerodha Ticker: Integrates the official `Tech.Zerodha.KiteConnect` library to stream binary ticks. Standard indices (NIFTY/BANKNIFTY) are mapped dynamically from/to instrument tokens.
5.  **Automated PostgreSQL Data Storage**:
    The background `MarketDataFeedWorker` feeds tick updates to `MarketDataProcessor`, which in turn leverages `CandleBuilderService` to aggregate high-frequency ticks into multi-timeframe candles. Whenever a candlestick closes, the pipeline automatically intercepts the event and persists it to the PostgreSQL `market_candles` table thread-safely via the decoupled `IMarketCandleRepository`.



---

## Database Schema & Persistence (`QuantEdge.MarketData/Persistence/`)

We utilize a high-performance **Dapper & ADO.NET** persistence layer with a **PostgreSQL** database provider, incorporating a **TimescaleDB-ready** timeseries structure and dynamic **snake_case** database mappings.

### 1. Database Connections
Database connections are managed via decoupled factories:
*   `IDbConnectionFactory`: Defines the interface for creating ADO.NET database connections.
*   `NpgsqlConnectionFactory`: Implementation utilizing `NpgsqlConnection` referencing a standard PostgreSQL connection string injected through `IOptions<BrokerConfig>`.

### 2. Auto-Mapping (`snake_case` to `PascalCase`)
We register the Dapper option globally in the Dependency Injection container:
```csharp
Dapper.DefaultTypeMap.MatchNamesWithUnderscores = true;
```
This enables seamless auto-mapping of standard PostgreSQL `snake_case` table columns (`candle_time`, `signal_strength`, etc.) to C# strongly typed `PascalCase` properties (`CandleTime`, `SignalStrength`, etc.) without any manual conversion logic or overhead.

### 3. Stored Procedures (`Persistence/schema.sql`)
All ingestion and extraction tasks are executed strictly via PostgreSQL Stored Procedures and Table-Valued Functions, ensuring optimal performance and decoupling C# code from specific database schema details:
*   `sp_insert_market_candle`: Inserts or updates (UPSERT) aggregated OHLCV candle bars.
*   `sp_get_market_candles`: Fetches candle bars for a symbol and timeframe, ordered by time descending.
*   `sp_insert_market_indicator`: Inserts or updates calculated indicator sets.
*   `sp_get_market_indicators`: Queries indicator logs for technical analysis.
*   `sp_insert_trading_signal`: Persists AI-generated BUY/SELL signals.
*   `sp_get_recent_trading_signals`: Retrieves latest signals.

### 4. TimescaleDB Ready Structure & Indexes
To support TimescaleDB's timeseries chunk partitioning (hypertables) and maximize search queries:
*   **Primary Keys**: Composite keys `(id, candle_time)` are enforced on all tables, making them fully compliant with TimescaleDB hypertable constraints.
*   **Timezones**: Timestamps are mapped to `timestamp with time zone` (UTC standard) to prevent time-shifting.
*   **Composite Index** on `(symbol, timeframe, candle_time DESC)` for `market_candles` and `market_indicators` to maximize timeseries lookup speed.
*   **Composite Index** on `(symbol, candle_time DESC)` for `trading_signals` to optimize signal history queries.

### 5. Running Database Setup
The complete database script containing table creation, composite optimization indexes, and all Stored Procedures is defined in:
*   [schema.sql](file:///d:/LearningProject/QuantEdge/QuantEdge.MarketData/Persistence/schema.sql)

You can apply the schema to your PostgreSQL database using the standard `psql` command or any migration runner:
```bash
psql -U <username> -d <database_name> -f d:/LearningProject/QuantEdge/QuantEdge.MarketData/Persistence/schema.sql
```

---

## Getting Started

### Prerequisites
*   [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)

### Building the Project
From the solution root directory, execute the standard build command:
```bash
dotnet build
```

### Dependency Injection Registration
To integrate this module into a Web API or Worker host, register the services in your application's entrypoint (`Program.cs`):
```csharp
using QuantEdge.MarketData.Extensions;

// Register all MarketData configurations, simulator streams, candle builders, and background workers
builder.Services.AddMarketDataServices(builder.Configuration);
```

Configure your broker parameters within your `appsettings.json`:
```json
{
  "MarketDataSettings": {
    "BrokerConfig": {
      "ActiveBroker": "SIMULATOR",
      "WebSocketUrl": "wss://feed.quantedge.internal/v1/marketdata",
      "ApiKey": "YOUR-API-KEY",
      "ApiSecret": "YOUR-API-SECRET"
    }
  }
}
```
