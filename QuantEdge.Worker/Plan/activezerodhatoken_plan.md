# Active Zerodha Token Worker Job Deployment & Execution Plan

This plan details how to register, deploy, configure, and monitor the **Active Zerodha Token Monitor Service** (`activezerodhatoken`) on your Linux server or Windows Environment.

---

## Background Context
The `activezerodhatoken` worker runs periodically (every 5 minutes) to monitor and validate the Zerodha KiteConnect session token:
1. Verifies if the access token in `zerodha_session` is active and unexpired.
2. Checks connectivity and profile permissions with Zerodha KiteConnect API.
3. Automatically notifies trading services and sets system status (`TOKEN_EXPIRED` / `ACTIVE`) to protect automated orders from authentication failures.

---

## Service Identifiers

| Parameter | Value |
| :--- | :--- |
| **Job Identifier (`JobType`)** | `activezerodhatoken` |
| **Windows Service Name** | `Worker_activezerodhatoken` |
| **Linux systemd Service Name** | `quantedge-worker-activezerodhatoken` |
| **Description** | QuantEdge Zerodha Session Token Health & Expiry Monitor |

---

## Deployment Steps

### Step 1: Register systemd Service on Linux

1. Create the systemd service unit file:
   ```bash
   sudo nano /etc/systemd/system/quantedge-worker-activezerodhatoken.service
   ```
2. Paste the following configuration:
   ```ini
   [Unit]
   Description=QuantEdge Zerodha Session Token Health & Expiry Monitor
   After=network.target postgresql.service redis.service

   [Service]
   Type=simple
   User=root
   WorkingDirectory=/opt/quantedge/worker
   ExecStart=/usr/bin/dotnet QuantEdge.Worker.dll activezerodhatoken
   Restart=always
   RestartSec=10
   KillMode=process
   Environment=DOTNET_ENVIRONMENT=Production

   [Install]
   WantedBy=multi-user.target
   ```
3. Save and exit (`Ctrl+O`, `Enter`, `Ctrl+X`).

---

### Step 2: Enable and Start the Service

```bash
# Reload systemd manager configuration
sudo systemctl daemon-reload

# Enable service to start automatically on system boot
sudo systemctl enable quantedge-worker-activezerodhatoken

# Start the Service
sudo systemctl start quantedge-worker-activezerodhatoken
```

---

## Service Management Commands

| Action | Linux Command | Windows Command |
| :--- | :--- | :--- |
| **Check Status** | `sudo systemctl status quantedge-worker-activezerodhatoken` | `Get-Service -Name "Worker_activezerodhatoken"` |
| **Restart Service** | `sudo systemctl restart quantedge-worker-activezerodhatoken` | `Restart-Service -Name "Worker_activezerodhatoken"` |
| **Stop Service** | `sudo systemctl stop quantedge-worker-activezerodhatoken` | `Stop-Service -Name "Worker_activezerodhatoken"` |
