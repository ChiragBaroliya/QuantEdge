# 30-Minute Intraday Swing Trading Worker Deployment Plan

This plan details how to register, deploy, configure, and monitor the **30-Minute Intraday Swing Trading Worker Service** (`swingintraday`) on your Linux server or Windows environment.

---

## Background Context
Now that the **`SwingTradingIntradayJobWorker` (`swingintraday`)** runs every 30 minutes during market hours (**09:15 AM – 03:30 PM IST**), it automatically handles intraday scans **and** performs the market close EOD consolidation & analysis during its final 3:30 PM run.

Therefore, the legacy **`SwingTradingDailyJobWorker` (`swingtradingjob`)** service is **no longer required** and can be removed completely to avoid duplicate processing.

---

## Service Identifiers

| Parameter | Value |
| :--- | :--- |
| **Job Identifier (`JobType`)** | `swingintraday` |
| **Windows Service Name** | `Worker_swingintraday` |
| **Linux systemd Service Name** | `quantedge-worker-swingintraday` |
| **Schedule / Trigger** | Every 30 mins during market hours (**09:15 AM – 03:30 PM IST**) |
| **Description** | QuantEdge 30-Minute Intraday Swing Strategy Scan & Auto-Sync Service |

---

## Deployment Steps

### Step 1: Deploy Compiled Worker Binaries
Publish and upload the latest compiled worker binaries from `D:\LearningProject\QuantEdge\publish\Worker\` to `/opt/quantedge/worker` on the Linux server:

```powershell
# Run from Windows Command Prompt or PowerShell:
scp -r "D:\LearningProject\QuantEdge\publish\Worker\*" root@217.216.79.53:/opt/quantedge/worker/
```

---

### Step 2: Register systemd Service on Linux

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

---

### Step 3: Enable and Start Service on Linux

```bash
# Reload systemd manager configuration
sudo systemctl daemon-reload

# Enable service to start automatically on boot
sudo systemctl enable quantedge-worker-swingintraday

# Start the service
sudo systemctl start quantedge-worker-swingintraday
```

---

### Step 4: Windows Service Setup (Alternative for Windows Servers)

```powershell
# Register 30-Minute Intraday Worker Service
sc.exe create "Worker_swingintraday" binPath= "C:\QuantEdge\Worker\QuantEdge.Worker.exe swingintraday" start= auto
sc.exe description "Worker_swingintraday" "QuantEdge 30-Minute Intraday Swing Strategy Scan Service"
Start-Service -Name "Worker_swingintraday"
```

---

## Service Monitoring & Verification

### 1. Check Service Status:
```bash
sudo systemctl status quantedge-worker-swingintraday
```

### 2. View Live Logs:
```bash
sudo journalctl -u quantedge-worker-swingintraday -f
```

### Key Logs to Watch For:
- `SwingTradingIntradayJobWorker (30-Minute Job) background service starting up...`
- `Market open (HH:mm:ss IST). Running 30-Minute Swing Trading Intraday Job...`
- `30-Minute Intraday Swing Trading Job completed successfully!`
