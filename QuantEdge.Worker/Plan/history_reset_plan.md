# History Reset & Bulk Store Worker Plan (`historyreset`)

This plan details how to execute and monitor the on-demand **History Reset & Store Worker** service (`historyreset`) for clearing existing market candles and technical indicators for specified date ranges and backfilling fresh data from Zerodha API across **Single or Multiple Symbols**, **Multiple Timeframes**, and **Multiple Date Ranges**.

---

## Background Context
The `historyreset` worker allows operators to purge and recreate historical candles (`market_candles_{tf}`) and technical indicators (`market_indicators_{tf}`) for specific stocks or all active instruments across specified timeframes and date ranges.

It can be run interactively via CLI, invoked with batch scripts for historical range backfills, deployed as an on-demand systemd service on Linux, or executed as a Windows Background Service.

---

## Command Line Arguments & Parameters

| Parameter | Type | Description | Format / Values | Default | Example |
| :--- | :--- | :--- | :--- | :--- | :--- |
| `jobType` | Positional | The worker job type to run. | `historyreset`, `todayreset`, `reset` | `marketdatafeed` | `historyreset` |
| `--symbol` | Option | Single symbol, comma-separated list of symbols, or `All` for all active instruments. | `All`, `INFY`, `INFY,TCS,RELIANCE` | `All` | `--symbol INFY,TCS,RELIANCE` |
| `--startDate` | Option | Start date for history reset/store. | `dd/MM/yyyy` (e.g. `10/08/2026`) | Today (`dd/MM/yyyy`) | `--startDate 01/08/2026` |
| `--endDate` | Option | End date for history reset/store. | `dd/MM/yyyy` (e.g. `10/08/2026`) | Today (`dd/MM/yyyy`) | `--endDate 10/08/2026` |
| `--timeframe` | Option | Target timeframe or comma-separated timeframes. | `1m`, `5m`, `15m`, `60m`, `1d`, `1m,5m,15m`, `all` | `all` | `--timeframe 1m,5m` |

---

## Execution Commands Guide

### 1. Single Stock & Single Date (10/08/2026)
```powershell
dotnet run --project d:\LearningProject\QuantEdge\QuantEdge.Worker\QuantEdge.Worker.csproj -- historyreset --symbol INFY --startDate 10/08/2026 --endDate 10/08/2026 --timeframe 1m
```

### 2. Multiple Stock Symbols (Comma-separated)
```powershell
# Reset & store history for INFY, TCS, RELIANCE, and HDFCBANK for 1m and 5m timeframes
dotnet run --project d:\LearningProject\QuantEdge\QuantEdge.Worker\QuantEdge.Worker.csproj -- historyreset --symbol INFY,TCS,RELIANCE,HDFCBANK --startDate 10/08/2026 --endDate 10/08/2026 --timeframe 1m,5m
```

### 3. All Active Stocks (`All`) for Today across All Timeframes
```powershell
dotnet run --project d:\LearningProject\QuantEdge\QuantEdge.Worker\QuantEdge.Worker.csproj -- historyreset --symbol All --startDate 10/08/2026 --endDate 10/08/2026 --timeframe all
```

### 4. Custom Date Range (01/08/2026 to 10/08/2026) for Multiple Timeframes
```powershell
dotnet run --project d:\LearningProject\QuantEdge\QuantEdge.Worker\QuantEdge.Worker.csproj -- historyreset --symbol All --startDate 01/08/2026 --endDate 10/08/2026 --timeframe 1m,5m,15m
```

---

## Batch Scripts for Multiple Date Ranges (Bulk Store)

### Windows PowerShell Batch Script (`BatchHistoryStore.ps1`)
Use this script to run multi-month or multi-range historical data store jobs sequentially:

```powershell
# Batch script to backfill multiple historical date ranges
$WorkerProj = "d:\LearningProject\QuantEdge\QuantEdge.Worker\QuantEdge.Worker.csproj"

$DateRanges = @(
    @{ Start = "01/06/2026"; End = "30/06/2026"; Timeframes = "1m,5m" },
    @{ Start = "01/07/2026"; End = "31/07/2026"; Timeframes = "1m,5m" },
    @{ Start = "01/08/2026"; End = "10/08/2026"; Timeframes = "all" }
)

foreach ($range in $DateRanges) {
    Write-Host ">>> Executing History Store for Range: $($range.Start) to $($range.End) ($($range.Timeframes))..." -ForegroundColor Green
    dotnet run --project $WorkerProj -- historyreset --symbol All --startDate $($range.Start) --endDate $($range.End) --timeframe $($range.Timeframes)
}
```

### Linux Shell Batch Script (`batch_history_store.sh`)
```bash
#!/bin/bash
# Batch script to run multiple date range historical stores on Linux
WORKER_DLL="/opt/quantedge/worker/QuantEdge.Worker.dll"

# Array of Date Ranges: StartDate EndDate Timeframes Symbol
declare -a RANGES=(
    "01/06/2026 30/06/2026 1m,5m All"
    "01/07/2026 31/07/2026 1m,5m All"
    "01/08/2026 10/08/2026 all All"
)

for range in "${RANGES[@]}"; do
    read -r START END TF SYM <<< "$range"
    echo ">>> Running History Store: Symbol=$SYM | Range=$START to $END | Timeframe=$TF"
    dotnet "$WORKER_DLL" historyreset --symbol "$SYM" --startDate "$START" --endDate "$END" --timeframe "$TF"
done
```

---

## Linux systemd Service Deployment (`quantedge-worker-historyreset.service`)

### Step 1: Create Service File
Create `/etc/systemd/system/quantedge-worker-historyreset.service`:

```bash
sudo nano /etc/systemd/system/quantedge-worker-historyreset.service
```

Add the configuration:

```ini
[Unit]
Description=QuantEdge History Reset & Store Worker Service
After=network.target postgresql.service

[Service]
Type=simple
User=root
WorkingDirectory=/opt/quantedge/worker
ExecStart=/usr/bin/dotnet QuantEdge.Worker.dll historyreset --symbol All --startDate 10/08/2026 --endDate 10/08/2026 --timeframe all
Restart=no
KillMode=process
Environment=DOTNET_ENVIRONMENT=Production
```

### Step 2: Reload and Execute Service
```bash
sudo systemctl daemon-reload
sudo systemctl start quantedge-worker-historyreset.service
```

### Step 3: Monitor Live Journal Logs
```bash
sudo journalctl -u quantedge-worker-historyreset.service -f
```

---

## Verification Plan

### 1. Console & Serilog Verification
Verify worker log header output:
```text
================================================================================
 History Reset Worker Executing Job:
 - Target Symbol(s) : INFY, TCS, RELIANCE (3 stock(s))
 - Start Date       : 10/08/2026
 - End Date         : 10/08/2026
 - Timeframe(s)     : [1m, 5m]
================================================================================
```

### 2. Database Record Count SQL Verification
Inspect PostgreSQL counts for target symbols and date bounds:

```sql
-- Check candle counts for 10/08/2026
SELECT symbol, timeframe, COUNT(*) 
FROM market_candles_1m 
WHERE candle_time >= '2026-08-10 00:00:00' AND candle_time <= '2026-08-10 23:59:59' 
GROUP BY symbol, timeframe;

-- Check technical indicator counts for 10/08/2026
SELECT symbol, timeframe, COUNT(*) 
FROM market_indicators_1m 
WHERE candle_time >= '2026-08-10 00:00:00' AND candle_time <= '2026-08-10 23:59:59' 
GROUP BY symbol, timeframe;
```
