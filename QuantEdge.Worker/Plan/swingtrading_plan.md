# Swing Trading Worker Services Deployment & Execution Plan

This plan details how to register, deploy, configure, and monitor the **30-Minute Intraday Swing Trading Worker Service** (`swingintraday`) and the **Daily EOD Swing Trading Worker Service** (`swingtradingjob`) on your Linux server or Windows environment.

---

## Background Context
The Swing Trading module is powered by two specialized worker services:

1. **`SwingTradingIntradayJobWorker` (`swingintraday`)**:
   - Executes an intraday strategy scan **every 30 minutes** during the active trading window (**09:15 AM – 03:30 PM IST**, Monday–Friday).
   - Syncs intraday candles, computes indicators (EMA, RSI, MACD, ADX, ATR, 52W High), evaluates the 13-rule `SwingDecisionEngine`, updates PostgreSQL databases, and broadcasts live updates to the Web UI via SignalR.

2. **`SwingTradingDailyJobWorker` (`swingtradingjob`)**:
   - Executes daily after market close at **15:45 PM (3:45 PM IST)** for end-of-day daily candle consolidation, trade setup generation, and historical performance tracking.

---

## Service Identifiers

| Parameter | Intraday 30-Min Service | Daily EOD Service |
| :--- | :--- | :--- |
| **Job Identifier (`JobType`)** | `swingintraday` (or `swing30min`) | `swingtradingjob` |
| **Windows Service Name** | `Worker_swingintraday` | `Worker_swingtradingjob` |
| **Linux systemd Service Name** | `quantedge-worker-swingintraday` | `quantedge-worker-swingtradingjob` |
| **Schedule / Trigger** | Every 30 mins (09:15 AM – 03:30 PM IST) | Daily at 15:45 PM IST |
| **Description** | QuantEdge 30-Minute Intraday Swing Strategy Scan | QuantEdge Daily Swing Strategy EOD Analysis |

---

## Deployment Steps

### Step 1: Deploy Compiled Worker Binaries
Publish and upload the latest compiled worker binaries from `D:\LearningProject\QuantEdge\publish\Worker\` to `/opt/quantedge/worker` on the Linux server:

```powershell
# Run from Windows Command Prompt or PowerShell:
scp -r "D:\LearningProject\QuantEdge\publish\Worker\*" root@217.216.79.53:/opt/quantedge/worker/
```

---

### Step 2: Register systemd Services on Linux

#### A. Register 30-Minute Intraday Worker Service (`quantedge-worker-swingintraday`)
1. Create the systemd service file:
   ```bash
   sudo nano /etc/systemd/system/quantedge-worker-swingintraday.service
   ```
2. Paste the following configuration:
   ```ini
   [Unit]
   Description=QuantEdge 30-Minute Intraday Swing Strategy Scan Service
   After=network.target postgresql.service

   [Service]
   Type=simple
   User=root
   WorkingDirectory=/opt/quantedge/worker
   ExecStart=/usr/bin/dotnet QuantEdge.Worker.dll swingintraday
   Restart=always
   RestartSec=5
   KillMode=process
   Environment=DOTNET_ENVIRONMENT=Production

   [Install]
   WantedBy=multi-user.target
   ```

#### B. Register Daily EOD Worker Service (`quantedge-worker-swingtradingjob`)
1. Create the systemd service file:
   ```bash
   sudo nano /etc/systemd/system/quantedge-worker-swingtradingjob.service
   ```
2. Paste the following configuration:
   ```ini
   [Unit]
   Description=QuantEdge Daily Swing Strategy EOD Analysis Service
   After=network.target postgresql.service

   [Service]
   Type=simple
   User=root
   WorkingDirectory=/opt/quantedge/worker
   ExecStart=/usr/bin/dotnet QuantEdge.Worker.dll swingtradingjob
   Restart=always
   RestartSec=5
   KillMode=process
   Environment=DOTNET_ENVIRONMENT=Production

   [Install]
   WantedBy=multi-user.target
   ```

---

### Step 3: Enable and Start Services on Linux

```bash
# Reload systemd manager configuration
sudo systemctl daemon-reload

# Enable services to start automatically on boot
sudo systemctl enable quantedge-worker-swingintraday
sudo systemctl enable quantedge-worker-swingtradingjob

# Start both services
sudo systemctl start quantedge-worker-swingintraday
sudo systemctl start quantedge-worker-swingtradingjob
```

---

### Step 4: Windows Service Setup (Alternative for Windows Servers)

```powershell
# Register 30-Minute Intraday Worker Service
sc.exe create "Worker_swingintraday" binPath= "C:\QuantEdge\Worker\QuantEdge.Worker.exe swingintraday" start= auto
sc.exe description "Worker_swingintraday" "QuantEdge 30-Minute Intraday Swing Strategy Scan Service"
Start-Service -Name "Worker_swingintraday"

# Register Daily EOD Worker Service
sc.exe create "Worker_swingtradingjob" binPath= "C:\QuantEdge\Worker\QuantEdge.Worker.exe swingtradingjob" start= auto
sc.exe description "Worker_swingtradingjob" "QuantEdge Daily Swing Strategy EOD Analysis Service"
Start-Service -Name "Worker_swingtradingjob"
```

---

## Service Monitoring & Verification

### 1. Check Service Status:
```bash
# Check 30-minute intraday worker status
sudo systemctl status quantedge-worker-swingintraday

# Check daily EOD worker status
sudo systemctl status quantedge-worker-swingtradingjob
```

### 2. View Live Logs:
```bash
# Stream 30-minute intraday worker logs
sudo journalctl -u quantedge-worker-swingintraday -f

# Stream daily EOD worker logs
sudo journalctl -u quantedge-worker-swingtradingjob -f
```

### Key Logs to Watch For:
- **30-Min Intraday Worker:**
  - `SwingTradingIntradayJobWorker (30-Minute Job) background service starting up...`
  - `Market open (HH:mm:ss IST). Running 30-Minute Swing Trading Intraday Job...`
  - `30-Minute Intraday Swing Trading Job completed successfully!`
- **Daily EOD Worker:**
  - `Next Swing Trading EOD Job scheduled at <TargetTime> IST`
  - `Executing EOD Swing Trading Job...`
