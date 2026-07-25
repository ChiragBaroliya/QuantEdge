# QuantEdge Worker Deployment & Service Management

## 1. Deployment Command

Deployment artifact transfer to remote Linux server:
```bash
scp -r "D:\QuantEdge\Job\*" root@217.216.79.53:/opt/quantedge/worker/
```

---

## 2. List of All Worker Service Jobs

| Job Identifier (`JobType`) | Windows Service Name | Linux systemd Service Name | Description |
| :--- | :--- | :--- | :--- |
| `marketdatafeed:1m` | `Worker_marketdatafeed_1m` | `quantedge-worker-marketdatafeed-1m` | Live 1-Minute Candle Feed & Aggregation |
| `marketdatafeed:5m` | `Worker_marketdatafeed_5m` | `quantedge-worker-marketdatafeed-5m` | Live 5-Minute Candle Feed & Aggregation |
| `marketdatafeed:15m` | `Worker_marketdatafeed_15m` | `quantedge-worker-marketdatafeed-15m` | Live 15-Minute Candle Feed & Aggregation |
| `marketdatafeed:60m` | `Worker_marketdatafeed_60m` | `quantedge-worker-marketdatafeed-60m` | Live 60-Minute Candle Feed & Aggregation |
| `marketdatafeed:1d` | `Worker_marketdatafeed_1d` | `quantedge-worker-marketdatafeed-1d` | Live Daily Candle Feed & Aggregation |
| `activezerodhatoken` | `Worker_activezerodhatoken` | `quantedge-worker-activezerodhatoken` | Zerodha Session Token Refresher |
| `instrumentsync` | `Worker_instrumentsync` | `quantedge-worker-instrumentsync` | Zerodha Master Instrument List Sync |
| `history:<timeframe>` | `Worker_history_<tf>` | `quantedge-worker-history-<tf>` | Historical Data Backfill Sync |
| `swingtradingjob` | `Worker_swingtradingjob` | `quantedge-worker-swingtradingjob` | Daily Swing Trading Strategy Scan Job |
| `clearcache` | `Worker_clearcache` | `quantedge-worker-clearcache` | On-Demand Memory Cache Reset Job |

---

## 3. Commands to List Worker Service Jobs

### A. Remote Linux Server via SSH (Target Host: 217.216.79.53)
List all worker services:
```bash
ssh root@217.216.79.53 "systemctl list-units --type=service 'quantedge-worker-*'"
```

Detailed status check of remote worker services:
```bash
ssh root@217.216.79.53 "systemctl status 'quantedge-worker-*' --no-pager"
```

### B. Linux Environment (Local systemd)
List all active QuantEdge worker services:
```bash
systemctl list-units --type=service "quantedge-worker-*"
```

List status of all QuantEdge ecosystem services:
```bash
systemctl status "quantedge-*" --no-pager
```

### C. Windows Environment (PowerShell)
List all QuantEdge worker services:
```powershell
Get-Service -Name "Worker_*"
```

Detailed status view:
```powershell
Get-Service -Name "Worker_*" | Select-Object Name, Status, StartType, DisplayName
```