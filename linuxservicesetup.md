# QuantEdge Linux Services Setup Guide (systemd)

This document provides step-by-step instructions to configure, run, and manage the 12 QuantEdge services as background daemons (services) on a Linux server (Ubuntu 24.04 LTS) using **systemd**.

> [!NOTE]
> * You should run these commands as the **`root`** user (as seen in your SSH session `root@vmi3385493:~#`).
> * On Linux, background services are managed by **`systemd`** using utility commands like `systemctl` and `journalctl`.
> * In configuration files, paths are case-sensitive and use forward slashes (e.g., `/opt/quantedge`).

---

## 1. Install Prerequisites on Linux

To run a .NET 10 application and database on your Ubuntu 24.04 LTS VPS, follow these installation steps.

### A. Install .NET 10 Runtime & SDK
Run the following commands in your SSH terminal to add the official Microsoft repository and install .NET 10:

```bash
# Register the Microsoft package repository
wget https://packages.microsoft.com/config/ubuntu/24.04/packages-microsoft-prod.deb -O packages-microsoft-prod.deb
sudo dpkg -i packages-microsoft-prod.deb
rm packages-microsoft-prod.deb

# Update apt package listings and install .NET 10 SDK
sudo apt-get update
sudo apt-get install -y dotnet-sdk-10.0
```

Verify that .NET 10 is successfully installed:
```bash
dotnet --version
```

### B. Install & Configure PostgreSQL (If not already configured)
If your database is hosted on the same Linux server, install PostgreSQL:

```bash
# Install PostgreSQL database server
sudo apt-get install -y postgresql postgresql-contrib

# Enable and start PostgreSQL service
sudo systemctl enable postgresql
sudo systemctl start postgresql
```

Configure the database and username password:
```bash
# Log in to PostgreSQL prompt and create the database
sudo -i -u postgres psql -c "CREATE DATABASE quantedge;"

# Set the password for 'postgres' user to match your configuration
sudo -i -u postgres psql -c "ALTER USER postgres PASSWORD 'postgres';"
```

---

## 2. Deploy Applications to Linux

You can deploy the files using one of two methods:

### Method A: Build Directly on Linux (Recommended)
Since your server is connected, you can clone your git repository on the Linux server and build it:

```bash
# Clone the repository (replace with your git URL)
cd /root
git clone <your-git-repository-url> QuantEdge
cd QuantEdge

# Publish the Worker, API, and Web applications
dotnet publish QuantEdge.Worker/QuantEdge.Worker.csproj -c Release -o /opt/quantedge/worker
dotnet publish QuantEdge.API/QuantEdge.API.csproj -c Release -o /opt/quantedge/api
dotnet publish QuantEdge.Web/QuantEdge.Web.csproj -c Release -o /opt/quantedge/web
```

### Method B: Copy Compiled Binaries from Windows
If you want to compile on Windows and upload via SFTP/SCP:

1. **Publish on Windows** via PowerShell:
   ```powershell
   cd D:\LearningProject\QuantEdge
   dotnet publish QuantEdge.Worker/QuantEdge.Worker.csproj -c Release -o ./publish/Worker
   dotnet publish QuantEdge.API/QuantEdge.API.csproj -c Release -o ./publish/API
   dotnet publish QuantEdge.Web/QuantEdge.Web.csproj -c Release -o ./publish/Web
   ```
2. **Transfer** the contents of `./publish/Worker`, `./publish/API`, and `./publish/Web` to the Linux server folders `/opt/quantedge/worker`, `/opt/quantedge/api`, and `/opt/quantedge/web` respectively, using SFTP (such as FileZilla or WinSCP).

---

## 3. Database Schema Setup

If this is a fresh database on Linux, initialize the tables and stored procedures using the `schema.sql` script:

```bash
# Locate your schema.sql file and apply it to the database
# (If built on server, it will be at /root/QuantEdge/QuantEdge.MarketData/Persistence/schema.sql)
sudo -i -u postgres psql -d quantedge -f /root/QuantEdge/QuantEdge.MarketData/Persistence/schema.sql
```

---

## 4. Automated Service Setup Script

To save you from creating 12 separate configuration files manually, copy and run this automated script. It will create all service folders, generate the systemd files, reload systemd, and enable all auto-start services.

### How to use:
1. Create a script file on Linux:
   ```bash
   nano /root/setup-services.sh
   ```
2. Copy the script below, paste it into the terminal (Right-Click to paste in most SSH terminals), then press `Ctrl+O`, `Enter`, and `Ctrl+X` to save and exit.
3. Make it executable and run it:
   ```bash
   chmod +x /root/setup-services.sh
   sudo /root/setup-services.sh
   ```

### Script Content (`setup-services.sh`):
```bash
#!/bin/bash

# Define variables
DOTNET_PATH="/usr/bin/dotnet"
INSTALL_DIR="/opt/quantedge"
ENV_MODE="Production"

echo "==========================================="
echo "  Setting up QuantEdge Background Services  "
echo "==========================================="

# Create directories if they don't exist
mkdir -p "${INSTALL_DIR}/worker"
mkdir -p "${INSTALL_DIR}/api"
mkdir -p "${INSTALL_DIR}/web"

# Define Service Configurations
# Syntax: "service_suffix|job_argument|is_autostart|description"
declare -a SERVICES=(
    # Web Applications
    "api|web_api|true|QuantEdge Web API Service"
    "web|web_ui|true|QuantEdge Web MVC Dashboard Service"
    
    # Worker Services - Category A (Continuous Auto-Start)
    "worker-marketdatafeed|marketdatafeed|true|QuantEdge Live Market Data Feed Service (Default)"
    "worker-marketdatafeed-1m|marketdatafeed:1m|true|QuantEdge Live 1-Minute Market Data Feed Service"
    "worker-marketdatafeed-5m|marketdatafeed:5m|true|QuantEdge Live 5-Minute Market Data Feed Service"
    "worker-marketdatafeed-15m|marketdatafeed:15m|true|QuantEdge Live 15-Minute Market Data Feed Service"
    "worker-activezerodhatoken|activezerodhatoken|true|QuantEdge Active Zerodha Access Token Maintainer Service"
    "worker-instrumentsync|instrumentsync|true|QuantEdge Zerodha Instruments Synchronizer Service"
    
    # Worker Services - Category B (Manual Run-Once / Historical - Start on Demand)
    "worker-history|history|false|QuantEdge Historical Data Gap Sync Service (Default)"
    "worker-history-1m|history:1m|false|QuantEdge Historical 1-Minute Data Gap Sync Service"
    "worker-history-5m|history:5m|false|QuantEdge Historical 5-Minute Data Gap Sync Service"
    "worker-history-15m|history:15m|false|QuantEdge Historical 15-Minute Data Gap Sync Service"
)

# Loop to generate each systemd service file
for item in "${SERVICES[@]}"; do
    IFS="|" read -r suffix arg autostart desc <<< "$item"
    
    SERVICE_NAME="quantedge-${suffix}"
    SERVICE_FILE="/etc/systemd/system/${SERVICE_NAME}.service"
    
    # Determine working directory and binary
    if [ "$suffix" == "api" ]; then
        WORK_DIR="${INSTALL_DIR}/api"
        EXEC_CMD="${DOTNET_PATH} QuantEdge.API.dll"
    elif [ "$suffix" == "web" ]; then
        WORK_DIR="${INSTALL_DIR}/web"
        EXEC_CMD="${DOTNET_PATH} QuantEdge.Web.dll"
    else
        WORK_DIR="${INSTALL_DIR}/worker"
        EXEC_CMD="${DOTNET_PATH} QuantEdge.Worker.dll ${arg}"
    fi

    echo "Creating Systemd service: ${SERVICE_NAME}"
    
    # Create the service configuration file
    cat <<EOF > "$SERVICE_FILE"
[Unit]
Description=${desc}
After=network.target postgresql.service

[Service]
Type=simple
User=root
WorkingDirectory=${WORK_DIR}
ExecStart=${EXEC_CMD}
Restart=always
RestartSec=5
KillMode=process
Environment=DOTNET_ENVIRONMENT=${ENV_MODE}

[Install]
WantedBy=multi-user.target
EOF

    # Configure auto-start behavior
    if [ "$autostart" == "true" ]; then
        systemctl enable "${SERVICE_NAME}"
        echo " -> Enabled auto-start on boot"
    else
        echo " -> Service registered for manual start (on-demand)"
    fi
done

# Reload systemd manager configuration
echo "Reloading systemd daemon..."
systemctl daemon-reload

echo "==========================================="
echo "  Setup Complete! All services registered. "
echo "==========================================="
```

---

## 5. Linux Service Management Reference

Here is a list of commands you will use to manage your newly registered background services. These are the direct equivalents to the Windows PowerShell `Start-Service` / `Stop-Service` commands.

### Start Services
```bash
# Start Web Apps
sudo systemctl start quantedge-api
sudo systemctl start quantedge-web

# Start Streaming Workers and Token/Instrument Maintainers
sudo systemctl start quantedge-worker-marketdatafeed-1m
sudo systemctl start quantedge-worker-marketdatafeed-5m
sudo systemctl start quantedge-worker-marketdatafeed-15m
sudo systemctl start quantedge-worker-activezerodhatoken
sudo systemctl start quantedge-worker-instrumentsync

# Manually Trigger historical gaps sync
sudo systemctl start quantedge-worker-history-1m
```

### Stop Services
```bash
sudo systemctl stop quantedge-worker-marketdatafeed-1m
sudo systemctl stop quantedge-worker-marketdatafeed-5m
sudo systemctl stop quantedge-worker-marketdatafeed-15m
sudo systemctl stop quantedge-worker-activezerodhatoken
sudo systemctl stop quantedge-worker-instrumentsync
```

### Restart Services
If you update your configurations or binaries, restart the services to load the changes:
```bash
sudo systemctl restart quantedge-worker-marketdatafeed-1m
```

### Check Service Status
See if a service is currently running, inactive, or failed:
```bash
sudo systemctl status quantedge-worker-marketdatafeed-1m
```

---

## 6. How to View Live Logs (Crucial for Debugging)

Since Linux services run in the background, you cannot see console output directly. Instead, all stdout/stderr output is captured by `journald`. Use the `journalctl` utility to inspect logs.

### View live scrolling logs (like `tail -f`)
```bash
# Show real-time scrolling logs for 1-minute market data feed
sudo journalctl -u quantedge-worker-marketdatafeed-1m -f

# Show real-time scrolling logs for the API service
sudo journalctl -u quantedge-api -f
```

### View last N lines of logs
```bash
# View the last 100 log lines of the 5-minute market data feed
sudo journalctl -u quantedge-worker-marketdatafeed-5m -n 100 --no-pager
```

### View logs filtered by timeframe
```bash
# View logs from today
sudo journalctl -u quantedge-worker-marketdatafeed-1m --since today
```

---

## 7. Configuration Adjustment

Your configuration is stored in `appsettings.json` in the respective directory. If you need to edit connection strings or credentials (e.g., Zerodha API Key, Database connection details):

1. Edit the file on Linux:
   ```bash
   nano /opt/quantedge/worker/appsettings.json
   ```
2. Edit the variables, press `Ctrl+O`, `Enter`, and `Ctrl+X` to save.
3. Restart the corresponding service:
   ```bash
   sudo systemctl restart quantedge-worker-marketdatafeed-1m
   ```
