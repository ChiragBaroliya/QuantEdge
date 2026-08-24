# Auto Paper Trading Worker Job Deployment & Execution Plan

This plan details how to register, deploy, configure, and monitor the **Auto Paper Trading Worker Service** (`autotrade`) on your Linux server or Windows Environment.

---

## Background Context
The `autotrade` worker job runs two core continuous hosted services:
1. **`AutoTradeSignalScanWorker`**: Executes a 15-minute signal scan during the trading window (**09:15 AM – 15:30 PM IST**) over **active NSE symbols** (dynamically loaded from `stock_master`) using the 13-condition `SwingDecisionEngine`.
2. **`AutoTradePositionMonitorWorker`**: Real-time tick-by-tick monitoring via Zerodha KiteTicker WebSocket with automatic **REST Polling Fallback** (30s) and proactive **AccessToken expiry handling** for position exits (Target +5%, Stop Loss -3%, Max Duration 20 days).

---

## Service Identifiers

| Parameter | Value |
| :--- | :--- |
| **Job Identifier (`JobType`)** | `autotrade` (or `autotradescan`) |
| **Windows Service Name** | `Worker_autotrade` |
| **Linux systemd Service Name** | `quantedge-worker-autotrade` |
| **Description** | QuantEdge Automated Paper Trading Strategy Scanner & Position Exit Monitor |

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
   sudo nano /etc/systemd/system/quantedge-worker-autotrade.service
   ```
2. Paste the following configuration:
   ```ini
   [Unit]
   Description=QuantEdge Automated Paper Trading Strategy Scanner & Position Exit Monitor
   After=network.target postgresql.service

   [Service]
   Type=simple
   User=root
   WorkingDirectory=/opt/quantedge/worker
   ExecStart=/usr/bin/dotnet QuantEdge.Worker.dll autotrade
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
sudo systemctl enable quantedge-worker-autotrade

# Start the Auto Trading Worker Service
sudo systemctl start quantedge-worker-autotrade
```

---

### Step 4: Windows Service Installation (Optional for Windows Server)

```powershell
# Register Windows Service using sc.exe
sc.exe create "Worker_autotrade" binPath= "D:\LearningProject\QuantEdge\QuantEdge.Worker\bin\Release\net10.0\QuantEdge.Worker.exe autotrade" start= auto DisplayName= "QuantEdge Auto Paper Trading Worker"

# Start the Windows Service
sc.exe start "Worker_autotrade"
```

---

## Log Monitoring & Verification

### Linux Live Logs
```bash
sudo journalctl -u quantedge-worker-autotrade -f
```

### Key Log Signatures to Watch For:
- **Service Startup:**
  `AutoTradeSignalScanWorker background service starting up...`
  `AutoTradePositionMonitorWorker background service starting up...`
- **15-Min Scan Execution:**
  `Executing 15-minute Auto Trade Signal Scan for ~190 active stocks...`
  `BUY Signal detected for <Symbol> (Score: <MetCount>/13, Entry: ₹<Price>)`
- **Position Exit Monitor:**
  `Auto SELL Executed (Target Hit) @ ₹<Price>`
- **REST Fallback / Reconnect:**
  `Zerodha WebSocket is disconnected. Running REST Polling Fallback monitor...`

---

## Service Management Commands

| Action | Linux Command | Windows Command |
| :--- | :--- | :--- |
| **Check Status** | `sudo systemctl status quantedge-worker-autotrade` | `Get-Service -Name "Worker_autotrade"` |
| **Restart Service** | `sudo systemctl restart quantedge-worker-autotrade` | `Restart-Service -Name "Worker_autotrade"` |
| **Stop Service** | `sudo systemctl stop quantedge-worker-autotrade` | `Stop-Service -Name "Worker_autotrade"` |
