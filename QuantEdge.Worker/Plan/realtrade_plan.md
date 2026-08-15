# Auto Real Trading Worker Job Deployment & Execution Plan (Live Broker Money)

This plan details how to register, deploy, configure, and monitor the **Auto Real Trading Worker Service** (`realtrade`) on your Linux server or Windows Environment.

---

## Background Context
The `realtrade` worker job executes continuous live automated trading using real money via the **Zerodha KiteConnect REST API** and WebSocket tick streaming:
1. **`AutoRealTradeSignalScanWorker`**:
   - Executes a 15-minute signal scan cycle during the live trading window (**09:15 AM – 15:30 PM IST**).
   - Scans active NSE symbols dynamically using the 13-condition `SwingDecisionEngine` and NIFTY trend filters.
   - Evaluates multi-layered risk guards: Master Toggle Switch ON, Zerodha Token validity, Daily Max Trade limit (e.g. 5 trades/day), Daily Loss Circuit Breaker, and Available Capital (e.g. ₹2,000 capital, ₹400/trade).
   - Places live **Real Market BUY Orders** directly to Zerodha Kite OMS.
2. **`AutoRealPositionMonitorWorker`**:
   - Continuous real-time position monitoring over live broker ticks and REST polling fallback.
   - Monitors open real positions for:
     * **Profit Target Exit** (+5.0% or user-defined target).
     * **Stop Loss Exit (Optional)** (e.g. -3.0%).
     * **Trailing Stop Loss Exit (Optional)** (Dynamic high-water mark trail locking in profits).
     * **Max Duration Hold Exit** (e.g. 20 Days).
   - Automatically executes live **Real Market SELL Orders** (Square-off) to Zerodha Kite OMS and broadcasts real-time SignalR toast alerts and dashboard updates.

---

## Service Identifiers

| Parameter | Value |
| :--- | :--- |
| **Job Identifier (`JobType`)** | `realtrade` (or `autorealtrade`) |
| **Windows Service Name** | `Worker_realtrade` |
| **Linux systemd Service Name** | `quantedge-worker-realtrade` |
| **Description** | QuantEdge Automated Real Trading (Live Broker Money) Scanner & Position Exit Monitor |

---

## Deployment Steps

### Step 1: Deploy Compiled Binaries
Publish and upload the latest compiled worker binaries to the target Linux server:

```powershell
# Run from Windows Command Prompt or PowerShell:
scp -r "D:\LearningProject\QuantEdge\publish\Worker\*" root@217.216.79.53:/opt/quantedge/worker/
```

---

### Step 2: Register systemd Service on Linux

1. Create the systemd service unit file:
   ```bash
   sudo nano /etc/systemd/system/quantedge-worker-realtrade.service
   ```
2. Paste the following configuration:
   ```ini
   [Unit]
   Description=QuantEdge Automated Real Trading (Live Broker Money) Scanner & Position Exit Monitor
   After=network.target postgresql.service redis.service

   [Service]
   Type=simple
   User=root
   WorkingDirectory=/opt/quantedge/worker
   ExecStart=/usr/bin/dotnet QuantEdge.Worker.dll realtrade
   Restart=always
   RestartSec=5
   KillMode=process
   Environment=DOTNET_ENVIRONMENT=Production

   [Install]
   WantedBy=multi-user.target
   ```
3. Save and exit (`Ctrl+O`, `Enter`, `Ctrl+X`).

---

### Step 3: Enable and Start the Service

```bash
# Reload systemd manager configuration
sudo systemctl daemon-reload

# Enable service to start automatically on system boot
sudo systemctl enable quantedge-worker-realtrade

# Start the Auto Real Trading Worker Service
sudo systemctl start quantedge-worker-realtrade
```

---

### Step 4: Windows Service Installation (Optional for Windows Server / Local)

```powershell
# Register Windows Service using sc.exe (Run as Administrator)
sc.exe create "Worker_realtrade" binPath= "D:\LearningProject\QuantEdge\QuantEdge.Worker\bin\Release\net10.0\QuantEdge.Worker.exe realtrade" start= auto DisplayName= "QuantEdge Auto Real Trading Worker"

# Start the Windows Service
sc.exe start "Worker_realtrade"
```

---

## Log Monitoring & Verification

### Linux Live Logs
```bash
sudo journalctl -u quantedge-worker-realtrade -f
```

### Key Log Signatures to Watch For:
- **Service Startup:**
  `AutoRealTradeSignalScanWorker (REAL MONEY) background service starting up...`
  `AutoRealPositionMonitorWorker (REAL POSITIONS MONITOR) background service starting up...`
- **15-Min Scan Execution:**
  `Executing 15-minute REAL MONEY Auto Trade Signal Scan for 1 active user(s)...`
  `REAL BUY Signal detected for <Symbol> for User '1' (Score: <Score>/100, Met: <MetCount>/13, Entry: ₹<Price>)`
  `⚡ Live BUY Executed @ ₹<Price> (Qty: <Qty>, Target: ₹<Target>, Order #<BrokerOrderId>)`
- **Position Exit Monitor:**
  `[REAL MONEY SELL TRIGGERED] Position #<Id> <Symbol> Qty:<Qty> @ <Ltp>. Reason: Target Hit / Trailing SL Hit`
  `⚡ Live SELL (Target Hit) @ ₹<Price> | P&L: +₹<PnL> (Order #<BrokerOrderId>)`
- **Panic Kill Switch / Circuit Breaker:**
  `🚨 EMERGENCY KILL SWITCH EXECUTED: Bot Stopped, <Count> live positions squared off.`

---

## Service Management Commands

| Action | Linux Command | Windows Command |
| :--- | :--- | :--- |
| **Check Status** | `sudo systemctl status quantedge-worker-realtrade` | `Get-Service -Name "Worker_realtrade"` |
| **Restart Service** | `sudo systemctl restart quantedge-worker-realtrade` | `Restart-Service -Name "Worker_realtrade"` |
| **Stop Service** | `sudo systemctl stop quantedge-worker-realtrade` | `Stop-Service -Name "Worker_realtrade"` |
