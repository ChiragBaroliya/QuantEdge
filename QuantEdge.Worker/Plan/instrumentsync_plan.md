# Deployment and Instrument Sync Job Execution Plan

This plan details the step-by-step instructions to copy your latest published code to the Linux Server using Windows Command Line / PowerShell and execute the **Instrument Sync Job** using the existing service configuration.

---

## Proposed Steps

### Step 1: Stop the Running Services on Linux
To prevent file-in-use errors while transferring files, run the following command in your active SSH terminal to stop the existing worker services:
```bash
# Stop the instrument sync service (if running)
sudo systemctl stop quantedge-worker-instrumentsync

# If other worker services are running, stop them as well
sudo systemctl stop quantedge-worker-marketdatafeed-1m
sudo systemctl stop quantedge-worker-marketdatafeed-5m
sudo systemctl stop quantedge-worker-marketdatafeed-15m
sudo systemctl stop quantedge-worker-activezerodhatoken
```

---

### Step 2: Copy the Published Binaries to the Linux Server using `scp`
Open a **new PowerShell or Command Prompt window on Windows** (not the SSH session), and run the following command to copy all files to the Linux server using `scp` (Windows has `scp` built-in):

```powershell
scp -r "D:\LearningProject\QuantEdge\publish\Worker\*" root@217.216.79.53:/opt/quantedge/worker/
```

> [!NOTE]
> * Replace `217.216.79.53` in the command above with the actual IP address of your Linux server if it differs.
> * When prompted, enter the password for the `root` user.

---

### Step 3: Configure Database and Startup Action
1. Back in your SSH terminal, open the configuration file using `nano`:
   ```bash
   sudo nano /opt/quantedge/worker/appsettings.json
   ```
2. Double check the database credentials in the `ConnectionString` block to make sure it matches your server's target database configuration.
3. Set the `"RunInstrumentsSyncImmediately"` configuration to `true` to force the sync to run on startup:
   ```json
   "RunInstrumentsSyncImmediately": true
   ```
4. Save and exit the editor (`Ctrl+O`, `Enter`, and then `Ctrl+X`).

---

### Step 4: Run the Instrument Sync Job
Start the existing service, which will launch the worker with the `instrumentsync` argument and run the sync immediately:
```bash
sudo systemctl start quantedge-worker-instrumentsync
```

---

### Step 5: Monitor the Sync Progress and Logs
Inspect the live output to ensure that the instruments are successfully fetched and saved:
```bash
sudo journalctl -u quantedge-worker-instrumentsync -f
```

Look for the following entries in the log:
* `InstrumentSyncWorker started.`
* `RunInstrumentsSyncImmediately flag is TRUE. Running instrument sync immediately...`
* `Immediate instrument sync completed successfully.`

---

## Verification Plan

### Manual Verification
- Monitor `journalctl -u quantedge-worker-instrumentsync -f` to see the fetch results.
- Connect to your database (e.g. `psql -d quantedge`) to verify that the instruments tables are populated.
