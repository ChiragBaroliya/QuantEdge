# Live Market Data Feed Execution Plan (15m)

This plan details how to deploy, manage, and monitor the continuous, auto-starting live market data feed service for the `15-minute` timeframe.

---

## Background Context
Unlike on-demand historical gap syncs, these services run **continuously** (`start=auto`). They establish a live WebSocket connection to Zerodha (Kite Connect) to stream real-time tick/candle feeds, aggregate them into the `15-minute` timeframe, and persist the candles to the database.

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

### Step 3: Configure systemd Service on Linux (First Time Only)
Create the systemd service file `/etc/systemd/system/quantedge-worker-marketdatafeed-15m.service`:

```bash
sudo nano /etc/systemd/system/quantedge-worker-marketdatafeed-15m.service
```

Add the following configuration:

```ini
[Unit]
Description=QuantEdge Live Market Data Feed Service (15m)
After=network.target postgresql.service

[Service]
Type=simple
User=root
WorkingDirectory=/opt/quantedge/worker
ExecStart=/usr/bin/dotnet QuantEdge.Worker.dll marketdatafeed:15m
Restart=always
RestartSec=5
KillMode=process
Environment=DOTNET_ENVIRONMENT=Production

[Install]
WantedBy=multi-user.target
```

Reload the systemd daemon to recognize the new service:

```bash
sudo systemctl daemon-reload
```

Enable the service to start automatically on system boot:

```bash
sudo systemctl enable quantedge-worker-marketdatafeed-15m
```

---

### Step 4: Manage and Start the Market Data Feed Service

#### Start the Service
```bash
sudo systemctl start quantedge-worker-marketdatafeed-15m
```

#### Stop the Service
```bash
sudo systemctl stop quantedge-worker-marketdatafeed-15m
```

---

### Step 5: Monitor Live Streaming Logs
To verify that the WebSocket connection is successfully established and ticks are being aggregated into candles:

```bash
# View live logs for 15-Minute Feed
sudo journalctl -u quantedge-worker-marketdatafeed-15m -f
```

---

## Verification Plan

### Manual Verification
- Check service active state:
  ```bash
  sudo systemctl status quantedge-worker-marketdatafeed-15m
  ```
- Query the database to verify new candles are actively being inserted:
  ```sql
  -- Check latest records in market_candles_15m table
  SELECT * FROM market_candles_15m ORDER BY candle_time DESC LIMIT 5;
  ```
