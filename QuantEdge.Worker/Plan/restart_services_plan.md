# Plan: Restart All QuantEdge Services

This document details the step-by-step procedures to cleanly stop, start, and verify all QuantEdge background services. Use this plan after applying code updates, configuration edits (in `appsettings.json`), or performing database maintenance.

---

## 1. List of Active Services

The QuantEdge ecosystem consists of the following continuous services:

| Component | Windows Service Name | Linux systemd Service Name | Auto-Start |
| :--- | :--- | :--- | :--- |
| **QuantEdge API** | Managed via IIS Express / VS | `quantedge-api` | Yes |
| **QuantEdge Web Dashboard** | Managed via IIS Express / VS | `quantedge-web` | Yes |
| **Market Data Feed (1m)** | `Worker_marketdatafeed_1m` | `quantedge-worker-marketdatafeed-1m` | Yes |
| **Market Data Feed (5m)** | `Worker_marketdatafeed_5m` | `quantedge-worker-marketdatafeed-5m` | Yes |
| **Market Data Feed (15m)** | `Worker_marketdatafeed_15m` | `quantedge-worker-marketdatafeed-15m` | Yes |
| **Market Data Feed (60m)** | `Worker_marketdatafeed_60m` | `quantedge-worker-marketdatafeed-60m` | Yes |
| **Market Data Feed (1d)** | `Worker_marketdatafeed_1d` | `quantedge-worker-marketdatafeed-1d` | Yes |
| **Zerodha Session Refresher** | `Worker_activezerodhatoken` | `quantedge-worker-activezerodhatoken` | Yes |
| **Zerodha Instrument Sync** | `Worker_instrumentsync` | `quantedge-worker-instrumentsync` | Yes |

---

## 2. Windows Environment Restart Procedure

> [!IMPORTANT]
> Always open **PowerShell** as **Administrator** before executing these commands.

### Option A: One-Liner Restart (Fastest)
Run this single pipeline command to stop and restart all active QuantEdge workers:
```powershell
Get-Service -Name "Worker_*" | Restart-Service -Verbose
```

### Option B: Step-by-Step Restart (Safest)
Run this sequence to ensure processes are cleanly terminated before starting:

1. **Stop all active workers**:
   ```powershell
   Stop-Service -Name "Worker_marketdatafeed_1m"
   Stop-Service -Name "Worker_marketdatafeed_5m"
   Stop-Service -Name "Worker_marketdatafeed_15m"
   Stop-Service -Name "Worker_marketdatafeed_60m"
   Stop-Service -Name "Worker_marketdatafeed_1d"
   Stop-Service -Name "Worker_activezerodhatoken"
   Stop-Service -Name "Worker_instrumentsync"
   ```

2. **Start the workers**:
   ```powershell
   Start-Service -Name "Worker_marketdatafeed_1m"
   Start-Service -Name "Worker_marketdatafeed_5m"
   Start-Service -Name "Worker_marketdatafeed_15m"
   Start-Service -Name "Worker_marketdatafeed_60m"
   Start-Service -Name "Worker_marketdatafeed_1d"
   Start-Service -Name "Worker_activezerodhatoken"
   Start-Service -Name "Worker_instrumentsync"
   ```

3. **Verify running status**:
   ```powershell
   Get-Service -Name "Worker_*"
   ```

---

## 3. Linux (Ubuntu systemd) Environment Restart Procedure

> [!IMPORTANT]
> Run these commands as the **`root`** user or prefix with `sudo`.

### Option A: One-Liner Restart (Fastest)
Restart all services in parallel using pattern matching:
```bash
sudo systemctl restart "quantedge-*"
```

### Option B: Step-by-Step Restart (Safest)
For a sequenced restart that ensures dependency ordering:

1. **Restart Core Web Apps first**:
   ```bash
   sudo systemctl restart quantedge-api
   sudo systemctl restart quantedge-web
   ```

2. **Restart Background Workers**:
   ```bash
   sudo systemctl restart quantedge-worker-marketdatafeed-1m
   sudo systemctl restart quantedge-worker-marketdatafeed-5m
   sudo systemctl restart quantedge-worker-marketdatafeed-15m
   sudo systemctl restart quantedge-worker-marketdatafeed-60m
   sudo systemctl restart quantedge-worker-marketdatafeed-1d
   sudo systemctl restart quantedge-worker-activezerodhatoken
   sudo systemctl restart quantedge-worker-instrumentsync
   ```

3. **Verify Status**:
   ```bash
   sudo systemctl status "quantedge-*" --no-pager
   ```

---

## 4. Verification and Live Log Inspection

To ensure the services booted successfully and are active:

### Windows Event Viewer Logs
Check the Application logs in the Event Viewer, or inspect console transcripts if running worker processes interactively via CLI:
```powershell
# If running workers manually for testing:
dotnet run --project QuantEdge.Worker/QuantEdge.Worker.csproj marketdatafeed:1m
```

### Linux systemd Log Tailing (journalctl)
View real-time scrolling logs to confirm live connections (e.g. SignalR/WebSockets):
```bash
# View 1-minute data feed logs:
sudo journalctl -u quantedge-worker-marketdatafeed-1m -f

# View API logs:
sudo journalctl -u quantedge-api -f
```
