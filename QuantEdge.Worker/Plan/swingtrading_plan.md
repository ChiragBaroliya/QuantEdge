# Swing Trading Daily Job Deployment & Execution Plan

This plan details how to register, deploy, configure, and monitor the Daily Swing Trading strategy End-of-Day (EOD) analysis worker service on your Linux server.

---

## Background Context
The `SwingTradingDailyJobWorker` is a continuous service configured to execute daily at **15:45 (3:45 PM IST)**. It runs end-of-day calculations and analysis for your swing trading strategy.
Since this systemd service is currently missing from your Linux server, you need to create and register it first.

---

## Proposed Steps

### Step 1: Ensure Latest Binaries are Deployed
Ensure the latest compiled binaries from `D:\LearningProject\QuantEdge\publish\Worker\` are uploaded to `/opt/quantedge/worker` on the Linux server.

```powershell
# Run this from Windows Command Prompt or PowerShell:
scp -r "D:\LearningProject\QuantEdge\publish\Worker\*" root@217.216.79.53:/opt/quantedge/worker/
```

---

### Step 2: Create the systemd Service File on Linux
Since `quantedge-worker-swingtradingjob` is not registered, you must create it:

1. Open your SSH terminal and create the service configuration:
   ```bash
   sudo nano /etc/systemd/system/quantedge-worker-swingtradingjob.service
   ```
2. Paste the following configuration:
   ```ini
   [Unit]
   Description=QuantEdge Daily Swing Trading strategy EOD analysis service
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
3. Save and close the editor (`Ctrl+O`, `Enter`, and `Ctrl+X`).

---

### Step 3: Register and Start the Service
Reload systemd to detect the new service, then start and enable it:

```bash
# Reload systemd manager configuration
sudo systemctl daemon-reload

# Enable the service to automatically start on boot
sudo systemctl enable quantedge-worker-swingtradingjob

# Start the Swing Trading Daily Job service
sudo systemctl start quantedge-worker-swingtradingjob
```

---

### Step 4: Monitor Logs
Monitor the logs to confirm the service starts up successfully and schedules its next run:

```bash
sudo journalctl -u quantedge-worker-swingtradingjob -f
```

### Key Logs to Watch For:
- **Startup:**
  `SwingTradingDailyJobWorker background service starting up...`
- **Schedule Confirmation:**
  `Next Swing Trading EOD Job scheduled at <TargetTime> IST (Delay: <TimeRemaining>)`

---

## Verification Plan

### Manual Verification
- Check the service status to verify it's active and running:
  ```bash
  sudo systemctl status quantedge-worker-swingtradingjob
  ```
- Look for generated trade signals or database logs updated after 3:45 PM IST in your PostgreSQL database.
