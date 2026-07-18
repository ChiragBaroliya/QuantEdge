# Historical Data Gap Sync Execution Plan

This plan details how to execute and monitor the on-demand historical data gap sync services for the `1-minute`, `5-minute`, and `15-minute` timeframes.

---

## Background Context
Unlike continuous auto-start jobs, the historical gap sync services are configured to run **on-demand** (`start=demand`). When started, they check for gaps in the target database, download historical candle data from Zerodha for symbols marked with `IsHistryStored = 0`, and stop automatically upon completion.

---

## Proposed Steps

### Step 1: Ensure Latest Binaries are Deployed
Before running the historical sync, ensure the latest compiled binaries from `D:\LearningProject\QuantEdge\publish\Worker\` have been copied to `/opt/quantedge/worker` on the Linux server. If you already did this during the Instrument Sync step, you can skip to Step 2.

```powershell
# Run this from Windows Command Prompt or PowerShell if you need to redeploy:
scp -r "D:\LearningProject\QuantEdge\publish\Worker\*" root@217.216.79.53:/opt/quantedge/worker/
```

---

### Step 2: Trigger the Historical Sync Services
Depending on which timeframe(s) you need to sync, run one or more of the following commands in your SSH terminal:

```bash
# Start 1-Minute Historical Gap Sync
sudo systemctl start quantedge-worker-history-1m

# Start 5-Minute Historical Gap Sync
sudo systemctl start quantedge-worker-history-5m

# Start 15-Minute Historical Gap Sync
sudo systemctl start quantedge-worker-history-15m
```

---

### Step 3: Monitor Progress and Logs
Since these jobs run in the background, you can inspect their real-time execution logs using `journalctl`. Run these in your SSH terminal:

```bash
# Monitor 1-Minute Gap Sync logs
sudo journalctl -u quantedge-worker-history-1m -f

# Monitor 5-Minute Gap Sync logs
sudo journalctl -u quantedge-worker-history-5m -f

# Monitor 15-Minute Gap Sync logs
sudo journalctl -u quantedge-worker-history-15m -f
```

### Key Logs to Watch For:
1. **Startup:** 
   `Starting QuantEdge.Worker with job: history:1m`
2. **Execution Begin:**
   `HistoricalDataSyncWorker is executing gap check and backfill for X symbols where IsHistryStored = 0...`
3. **Symbol Completion:**
   `Successfully backfilled and updated IsHistryStored to 1 for symbol <SYMBOL>.`
4. **Shutdown:**
   `HistoricalDataSyncWorker has stopped.`

---

## Verification Plan

### Manual Verification
- Run `systemctl status quantedge-worker-history-1m` (or other timeframes) to verify if the service is active, running, or has finished (inactive/dead) successfully.
- Log into your database via PostgreSQL cli (`psql -d quantedge`) and run this query to verify symbols are being processed and updated:
  ```sql
  SELECT COUNT(*) FROM stock_master WHERE is_history_stored = 1;
  ```
