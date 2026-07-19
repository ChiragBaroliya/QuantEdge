# Historical Data Gap Sync Execution Plan (15m)

This plan details how to execute and monitor the on-demand historical data gap sync service for the `15-minute` timeframe.

---

## Background Context
Unlike continuous auto-start jobs, the historical gap sync services are configured to run **on-demand** (`start=demand`). When started, they check for gaps in the target database, download historical candle data from Zerodha for active symbols where the `is_histry_stored_15m` flag in `stock_master` is not yet set to `1`, and stop automatically upon completion.

---

## Proposed Steps

### Step 1: Ensure Latest Binaries are Deployed
Ensure the latest compiled binaries from `D:\LearningProject\QuantEdge\publish\Worker\` have been copied to `/opt/quantedge/worker` on the Linux server.

```powershell
# Run this from Windows Command Prompt or PowerShell if you need to redeploy:
scp -r "D:\LearningProject\QuantEdge\publish\Worker\*" root@217.216.79.53:/opt/quantedge/worker/
```

---

### Step 2: Configure systemd Service on Linux (First Time Only)
Create the systemd service file `/etc/systemd/system/quantedge-worker-history-15m.service`:

```bash
sudo nano /etc/systemd/system/quantedge-worker-history-15m.service
```

Add the following configuration:

```ini
[Unit]
Description=QuantEdge Historical 15-Minute Data Gap Sync Service
After=network.target postgresql.service

[Service]
Type=simple
User=root
WorkingDirectory=/opt/quantedge/worker
ExecStart=/usr/bin/dotnet QuantEdge.Worker.dll history:15m
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

---

### Step 3: Trigger the Historical Sync Service
Run the following command in your SSH terminal to start the 15-Minute sync service:

```bash
# Start 15-Minute Historical Gap Sync
sudo systemctl start quantedge-worker-history-15m
```

---

### Step 4: Monitor Progress and Logs
Since this job runs in the background, you can inspect its real-time execution logs using `journalctl`. Run this in your SSH terminal:

```bash
# Monitor 15-Minute Gap Sync logs
sudo journalctl -u quantedge-worker-history-15m -f
```

### Key Logs to Watch For:
1. **Startup:** 
   `Starting QuantEdge.Worker with job: history:15m`
2. **Execution Begin:**
   `HistoricalDataSyncWorker is executing gap check and backfill for X symbols where at least one target timeframe history is missing...`
3. **Symbol Completion:**
   `Successfully backfilled and updated history stored to 1 for symbol <SYMBOL> (15m).`
4. **Shutdown:**
   `Stopping application hosted services as history sync is completed.`

---

## Verification Plan

### Manual Verification
- Run `systemctl status quantedge-worker-history-15m` to verify if the service is active, running, or has finished (inactive/dead) successfully.
- Log into your database via PostgreSQL cli (`psql -d quantedge`) and run this query to verify symbols are being processed and updated:
  ```sql
  SELECT symbol, is_histry_stored_15m FROM stock_master;
  ```
