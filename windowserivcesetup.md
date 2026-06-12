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
*These services capture WebSocket streams or maintain tokens. They are set to start automatically on system boot (`start= auto`).*

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

# 4. Zerodha Session Token Refresher (Window: 6:00 AM - 7:00 AM IST)
sc.exe create "Worker_activezerodhatoken" binPath= "C:\QuantEdge\Worker\QuantEdge.Worker.exe activezerodhatoken" start= auto
sc.exe description "Worker_activezerodhatoken" "QuantEdge Active Zerodha Access Token Maintainer Service"
```

### Category B: Run-Once/Historical Services (Manual Start)
*These services backfill historical gaps and calculate indicators, then exit automatically. They are set to start manually on-demand (`start= demand`).*

```powershell
# 5. History Gap Sync (All Timeframes)
sc.exe create "Worker_history" binPath= "C:\QuantEdge\Worker\QuantEdge.Worker.exe history" start= demand
sc.exe description "Worker_history" "QuantEdge Historical Data Gap Sync Service (Default)"

# 6. History Gap Sync (1m Only)
sc.exe create "Worker_history_1m" binPath= "C:\QuantEdge\Worker\QuantEdge.Worker.exe history:1m" start= demand
sc.exe description "Worker_history_1m" "QuantEdge Historical 1-Minute Data Gap Sync Service"

# 7. History Gap Sync (5m Only)
sc.exe create "Worker_history_5m" binPath= "C:\QuantEdge\Worker\QuantEdge.Worker.exe history:5m" start= demand
sc.exe description "Worker_history_5m" "QuantEdge Historical 5-Minute Data Gap Sync Service"
```

---

## 3. Manage Services via PowerShell

### Start Services
```powershell
# Start streaming feeds and session maintainer
Start-Service -Name "Worker_marketdatafeed_1m"
Start-Service -Name "Worker_marketdatafeed_5m"
Start-Service -Name "Worker_activezerodhatoken"

# Trigger a historical gap sync manually
Start-Service -Name "Worker_history_1m"
```

### Stop Services
```powershell
Stop-Service -Name "Worker_marketdatafeed_1m"
Stop-Service -Name "Worker_marketdatafeed_5m"
Stop-Service -Name "Worker_activezerodhatoken"
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
sc.exe delete "Worker_activezerodhatoken"
sc.exe delete "Worker_history"
sc.exe delete "Worker_history_1m"
sc.exe delete "Worker_history_5m"
```
