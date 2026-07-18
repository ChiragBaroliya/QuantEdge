# Live Market Data Feed Execution Plan

This plan details how to deploy, manage, and monitor the continuous, auto-starting live market data feed services for the `1-minute`, `5-minute`, and `15-minute` timeframes.

---

## Background Context
Unlike on-demand historical gap syncs, these services run **continuously** (`start=auto`). They establish a live WebSocket connection to Zerodha (Kite Connect) to stream real-time tick/candle feeds, aggregate them into their respective timeframes, and persist the candles to the database.

---

## Proposed Steps

### Step 1: Ensure Latest Binaries are Deployed
Ensure the latest compiled binaries from `D:\LearningProject\QuantEdge\publish\Worker\` have been copied to `/opt/quantedge/worker` on the Linux server.

```powershell
# Run this from Windows Command Prompt or PowerShell if you need to redeploy:
scp -r "D:\LearningProject\QuantEdge\publish\Worker\*" root@217.216.79.53:/opt/quantedge/worker/
```

---

### Step 2: Configure Live Market Feed Credentials
Since live streaming feeds require active authentication session details:
1. Open `/opt/quantedge/worker/appsettings.json` on the Linux server:
   ```bash
   sudo nano /opt/quantedge/worker/appsettings.json
   ```
2. Verify that the `MarketDataSettings` block contains correct, active Zerodha credentials (`ApiKey`, `UserId`, `Password`, `TotpSecret`, `AccessToken`).
3. Save and close the editor (`Ctrl+O`, `Enter`, and `Ctrl+X`).

---

### Step 3: Manage and Start the Market Data Feed Services
Use `systemctl` to start, stop, or manage the autostart status of these services:

#### Start Services
```bash
# Start 1-Minute Live Feed
sudo systemctl start quantedge-worker-marketdatafeed-1m

# Start 5-Minute Live Feed
sudo systemctl start quantedge-worker-marketdatafeed-5m

# Start 15-Minute Live Feed
sudo systemctl start quantedge-worker-marketdatafeed-15m
```

#### Stop Services (If needed for maintenance)
```bash
sudo systemctl stop quantedge-worker-marketdatafeed-1m
sudo systemctl stop quantedge-worker-marketdatafeed-5m
sudo systemctl stop quantedge-worker-marketdatafeed-15m
```

#### Enable Autostart on Boot (Usually enabled by default)
```bash
sudo systemctl enable quantedge-worker-marketdatafeed-1m
sudo systemctl enable quantedge-worker-marketdatafeed-5m
sudo systemctl enable quantedge-worker-marketdatafeed-15m
```

---

### Step 4: Monitor Live Streaming Logs
To verify that the WebSocket connection is successfully established and ticks are being aggregated into candles:

```bash
# View live logs for 1-Minute Feed
sudo journalctl -u quantedge-worker-marketdatafeed-1m -f

# View live logs for 5-Minute Feed
sudo journalctl -u quantedge-worker-marketdatafeed-5m -f

# View live logs for 15-Minute Feed
sudo journalctl -u quantedge-worker-marketdatafeed-15m -f
```

---

## Verification Plan

### Manual Verification
- Check service active state:
  ```bash
  sudo systemctl status quantedge-worker-marketdatafeed-1m
  ```
- Query the database to verify new candles are actively being inserted:
  ```sql
  -- Check latest records in candle database tables
  SELECT * FROM stock_candles_1m ORDER BY timestamp DESC LIMIT 5;
  ```
