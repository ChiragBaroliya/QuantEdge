# QuantEdge Windows Services Setup Guide (PowerShell)

This document lists all the PowerShell commands required to register, configure, manage, and remove the 7 distinct QuantEdge worker jobs as native Windows Services.

> [!IMPORTANT]
> * Run PowerShell as **Administrator** to execute these commands.
> * In PowerShell, `sc` is an alias for `Set-Content`. You **must** type `sc.exe` to invoke the Windows Service Control tool.
> * The space after `binPath=` and `start=` is **strictly mandatory** for `sc.exe` parsing.

---

## 1. Publish the Project
First, publish the worker project to a standalone deployment directory:
```powershell
# Navigate to project root
cd D:\LearningProject\QuantEdge

# Publish release build
dotnet publish QuantEdge.Worker/QuantEdge.Worker.csproj -c Release -o C:\QuantEdge\Worker
```

---

## 2. Register Windows Services

### Category A: Continuous Services (Auto-Start)
*These services capture WebSocket streams, maintain tokens, or sync database instruments. They are set to start automatically on system boot (`start= auto`).*

```powershell
# 1. Market Data Feed (All Timeframes)
sc.exe create "Worker_marketdatafeed" binPath= "C:\QuantEdge\Worker\QuantEdge.Worker.exe marketdatafeed" start= auto
sc.exe description "Worker_marketdatafeed" "QuantEdge Live Market Data Feed Service (Default)"

# 2. Market Data Feed (1m Only)
sc.exe create "Worker_marketdatafeed_1m" binPath= "C:\QuantEdge\Worker\QuantEdge.Worker.exe marketdatafeed:1m" start= auto
sc.exe description "Worker_marketdatafeed_1m" "QuantEdge Live 1-Minute Market Data Feed Service"

# 3. Market Data Feed (5m Only)
sc.exe create "Worker_marketdatafeed_5m" binPath= "C:\QuantEdge\Worker\QuantEdge.Worker.exe marketdatafeed:5m" start= auto
sc.exe description "Worker_marketdatafeed_5m" "QuantEdge Live 5-Minute Market Data Feed Service"

# 4. Market Data Feed (15m Only)
sc.exe create "Worker_marketdatafeed_15m" binPath= "C:\QuantEdge\Worker\QuantEdge.Worker.exe marketdatafeed:15m" start= auto
sc.exe description "Worker_marketdatafeed_15m" "QuantEdge Live 15-Minute Market Data Feed Service"

# 5. Zerodha Session Token Refresher (Window: 6:00 AM - 7:00 AM IST)
sc.exe create "Worker_activezerodhatoken" binPath= "C:\QuantEdge\Worker\QuantEdge.Worker.exe activezerodhatoken" start= auto
sc.exe description "Worker_activezerodhatoken" "QuantEdge Active Zerodha Access Token Maintainer Service"

# 6. Zerodha Instruments Synchronizer (Cron: Monday 8:00 AM IST)
sc.exe create "Worker_instrumentsync" binPath= "C:\QuantEdge\Worker\QuantEdge.Worker.exe instrumentsync" start= auto
sc.exe description "Worker_instrumentsync" "QuantEdge Zerodha Instruments Synchronizer Service"

# 7. Daily Swing Trading Strategy EOD Analysis Job (Runs daily at 15:45 IST)
sc.exe create "Worker_swingtradingjob" binPath= "C:\QuantEdge\Worker\QuantEdge.Worker.exe swingtradingjob" start= auto
sc.exe description "Worker_swingtradingjob" "QuantEdge Daily Swing Trading strategy EOD analysis service"
```

### Category B: Run-Once/Historical Services (Manual Start)
*These services backfill historical gaps and calculate indicators, then exit automatically. They are set to start manually on-demand (`start= demand`).*

```powershell
# 7. History Gap Sync (All Timeframes)
sc.exe create "Worker_history" binPath= "C:\QuantEdge\Worker\QuantEdge.Worker.exe history" start= demand
sc.exe description "Worker_history" "QuantEdge Historical Data Gap Sync Service (Default)"

# 8. History Gap Sync (1m Only)
sc.exe create "Worker_history_1m" binPath= "C:\QuantEdge\Worker\QuantEdge.Worker.exe history:1m" start= demand
sc.exe description "Worker_history_1m" "QuantEdge Historical 1-Minute Data Gap Sync Service"

# 9. History Gap Sync (5m Only)
sc.exe create "Worker_history_5m" binPath= "C:\QuantEdge\Worker\QuantEdge.Worker.exe history:5m" start= demand
sc.exe description "Worker_history_5m" "QuantEdge Historical 5-Minute Data Gap Sync Service"

# 10. History Gap Sync (15m Only)
sc.exe create "Worker_history_15m" binPath= "C:\QuantEdge\Worker\QuantEdge.Worker.exe history:15m" start= demand
sc.exe description "Worker_history_15m" "QuantEdge Historical 15-Minute Data Gap Sync Service"
```

---

## 3. Manage Services via PowerShell

### Start Services
```powershell
# Start streaming feeds, session maintainer, and instrument sync
Start-Service -Name "Worker_marketdatafeed_1m"
Start-Service -Name "Worker_marketdatafeed_5m"
Start-Service -Name "Worker_marketdatafeed_15m"
Start-Service -Name "Worker_activezerodhatoken"
Start-Service -Name "Worker_instrumentsync"
Start-Service -Name "Worker_swingtradingjob"

# Trigger a historical gap sync manually
Start-Service -Name "Worker_history_1m"
```

### Stop Services
```powershell
Stop-Service -Name "Worker_marketdatafeed_1m"
Stop-Service -Name "Worker_marketdatafeed_5m"
Stop-Service -Name "Worker_marketdatafeed_15m"
Stop-Service -Name "Worker_activezerodhatoken"
Stop-Service -Name "Worker_instrumentsync"
Stop-Service -Name "Worker_swingtradingjob"
```

### Get Service Status
```powershell
Get-Service -Name "Worker_*"
```

---

## 4. Uninstall/Remove Services
To delete any service registrations from your system:
```powershell
sc.exe delete "Worker_marketdatafeed"
sc.exe delete "Worker_marketdatafeed_1m"
sc.exe delete "Worker_marketdatafeed_5m"
sc.exe delete "Worker_marketdatafeed_15m"
sc.exe delete "Worker_activezerodhatoken"
sc.exe delete "Worker_instrumentsync"
sc.exe delete "Worker_swingtradingjob"
sc.exe delete "Worker_history"
sc.exe delete "Worker_history_1m"
sc.exe delete "Worker_history_5m"
sc.exe delete "Worker_history_15m"
```
