# Telemetry CSV File Retrieval Guide

This guide explains how to retrieve telemetry CSV files from your Meta Quest 3 device for analysis in Excel or other tools.

---

## Quick Reference

**Package Name**: `com.samples.passthroughcamera`

**File Location on Quest**:
```
/sdcard/Android/data/com.samples.passthroughcamera/files/
```

**File Naming Pattern**:
```
telemetry_{session_id}_{timestamp}.csv
```

**Example**:
```
telemetry_1b3e2a4c_2026-04-21_14-30-45.csv
```

---

## Command Line: Retrieve CSV Files

### Option 1: Pull All Telemetry Files (Recommended)

**Windows**:
```bash
adb pull /sdcard/Android/data/com.samples.passthroughcamera/files/telemetry_*.csv .
```

**Expected Output**:
```
/sdcard/Android/data/com.samples.passthroughcamera/files/telemetry_1b3e2a4c_2026-04-21_14-30-45.csv: 1 file pulled, 0 skipped. 45.2 MB/s (125834 bytes in 0.003s)
```

**What this does**:
- Pulls **all CSV files** matching `telemetry_*.csv` pattern
- Saves to current directory on your PC
- Wildcard `*` matches any session ID and timestamp

---

### Option 2: Pull Specific File

If you know the exact filename:

```bash
adb pull /sdcard/Android/data/com.samples.passthroughcamera/files/telemetry_1b3e2a4c_2026-04-21_14-30-45.csv .
```

---

### Option 3: Pull to Specific Folder

**Windows**:
```bash
adb pull /sdcard/Android/data/com.samples.passthroughcamera/files/telemetry_*.csv C:\Telemetry\
```

**Create folder first** (if needed):
```bash
mkdir C:\Telemetry
adb pull /sdcard/Android/data/com.samples.passthroughcamera/files/telemetry_*.csv C:\Telemetry\
```

---

## Step-by-Step Workflow

### 1. Run Your App on Quest

**Before starting**:
- Build and deploy app to Quest 3
- Run inference session (Segmentation, PoseEstimation, or MultiObjectDetection)
- Let it run for a few minutes to collect telemetry data

**CSV files are created automatically** when you start inference with server mode enabled.

---

### 2. Find Session ID (Optional)

If you want to know which file to retrieve, check Unity logs:

```bash
adb logcat -s Unity | findstr "LOCAL TELEMETRY"
```

**Look for**:
```
[LOCAL TELEMETRY] Initializing CSV writer: /storage/emulated/0/Android/data/com.samples.passthroughcamera/files/telemetry_1b3e2a4c_2026-04-21_14-30-45.csv
[LOCAL TELEMETRY] CSV writer ready: /storage/...
[LOCAL TELEMETRY] Wrote frame 1 (state=Displayed, row=1)
[LOCAL TELEMETRY] Wrote frame 2 (state=Displayed, row=2)
...
[LOCAL TELEMETRY] CSV closed: /storage/.../telemetry_1b3e2a4c_2026-04-21_14-30-45.csv (150 rows written)
```

**Extract filename**: `telemetry_1b3e2a4c_2026-04-21_14-30-45.csv`

---

### 3. List Available Files on Quest

**See all telemetry files** before pulling:

```bash
adb shell ls -lh /sdcard/Android/data/com.samples.passthroughcamera/files/telemetry_*.csv
```

**Expected Output**:
```
-rw-rw---- 1 u0_a123 u0_a123  122K 2026-04-21 14:35 telemetry_1b3e2a4c_2026-04-21_14-30-45.csv
-rw-rw---- 1 u0_a123 u0_a123   98K 2026-04-21 13:20 telemetry_7f8d3c2a_2026-04-21_13-15-30.csv
-rw-rw---- 1 u0_a123 u0_a123  156K 2026-04-21 12:10 telemetry_9a2b4e1f_2026-04-21_12-05-20.csv
```

**What this shows**:
- File permissions
- File size (122K = ~122 KB)
- Timestamp when created
- Filename

---

### 4. Pull Files to PC

**Pull all files**:
```bash
adb pull /sdcard/Android/data/com.samples.passthroughcamera/files/telemetry_*.csv .
```

**Files saved to**: Current directory (where you ran the command)

**Verify**:
```bash
# Windows
dir telemetry_*.csv

# Expected
telemetry_1b3e2a4c_2026-04-21_14-30-45.csv
telemetry_7f8d3c2a_2026-04-21_13-15-30.csv
```

---

### 5. Open in Excel

**Method 1: Double-click** (if .csv associated with Excel)
- Windows Explorer → Double-click `telemetry_*.csv`
- Excel opens automatically

**Method 2: Import in Excel**:
1. Open Excel
2. File → Open → Browse
3. Select CSV file
4. Data imports automatically

**Expected columns** (37 total):
```
timestamp, scene, session_id, frame_id, unity_send_ts, unity_receive_ts, unity_display_ts,
unity_drop_ts, server_receive_ts, server_process_start_ts, server_send_ts, latency_ms,
upload_ms, queue_wait_ms, server_proc_ms, download_ms, parse_ms, udp_send_ms, server_pct,
upload_pct, download_pct, detection_count, avg_confidence, keypoint_avg_conf, image_width,
image_height, upload_bytes_uncompressed, upload_bytes_compressed, download_bytes_uncompressed,
download_bytes_compressed, final_state, drop_reason, error_reason, freeze_frames_per_frame,
freeze_duration_ms, cumulative_freeze_frames, freeze_ratio, frame_gap, cumulative_dropped
```

---

## Troubleshooting

### Issue 1: `adb: error: failed to stat remote object`

**Error**:
```
adb: error: failed to stat remote object '/sdcard/Android/data/com.samples.passthroughcamera/files/telemetry_*.csv': No such file or directory
```

**Possible Causes**:
1. **No telemetry files exist yet**
   - Solution: Run the app and perform inference session first

2. **Wrong package name**
   - Solution: Verify package with `adb shell pm list packages | findstr passthrough`
   - Expected: `package:com.samples.passthroughcamera`

3. **App not installed on Quest**
   - Solution: Build and deploy app first

**Verify app is installed**:
```bash
adb shell pm list packages | findstr passthrough
```

**Expected**:
```
package:com.samples.passthroughcamera
```

---

### Issue 2: No Files Found

**Check if directory exists**:
```bash
adb shell ls -la /sdcard/Android/data/com.samples.passthroughcamera/files/
```

**If directory doesn't exist**:
```
ls: /sdcard/Android/data/com.samples.passthroughcamera/files/: No such file or directory
```

**Solution**: App hasn't run yet or never wrote files. Check Unity logs:
```bash
adb logcat -s Unity | findstr "LOCAL TELEMETRY"
```

**Look for initialization message**:
```
[LOCAL TELEMETRY] Initializing CSV writer: ...
[LOCAL TELEMETRY] CSV writer ready: ...
```

**If no initialization message**:
- App might be using local inference (not server mode)
- Check Inspector: `Use Server Inference` should be ✓
- LocalTelemetryWriter may not be enabled

---

### Issue 3: Permission Denied

**Error**:
```
adb: error: failed to copy '/sdcard/Android/data/com.samples.passthroughcamera/files/telemetry_*.csv' to './telemetry_*.csv': remote couldn't read file: Permission denied
```

**Solution 1**: Pull to user-accessible directory
```bash
# First copy to /sdcard (public directory)
adb shell cp /sdcard/Android/data/com.samples.passthroughcamera/files/telemetry_*.csv /sdcard/

# Then pull from /sdcard
adb pull /sdcard/telemetry_*.csv .
```

**Solution 2**: Use adb root (if Quest is rooted)
```bash
adb root
adb pull /sdcard/Android/data/com.samples.passthroughcamera/files/telemetry_*.csv .
```

---

### Issue 4: Wildcard Not Working on Windows

**Problem**: Windows CMD doesn't expand wildcards the same way

**Solution**: Use PowerShell instead
```powershell
adb pull /sdcard/Android/data/com.samples.passthroughcamera/files/telemetry_*.csv .
```

**Or pull directory recursively**:
```bash
adb pull /sdcard/Android/data/com.samples.passthroughcamera/files/ ./quest_files/
```

---

## Advanced Commands

### Pull Only Recent Files (Last 24 Hours)

```bash
# List files modified in last 24 hours
adb shell find /sdcard/Android/data/com.samples.passthroughcamera/files/ -name "telemetry_*.csv" -mtime -1

# Pull them
adb shell find /sdcard/Android/data/com.samples.passthroughcamera/files/ -name "telemetry_*.csv" -mtime -1 -exec basename {} \; | while read file; do adb pull /sdcard/Android/data/com.samples.passthroughcamera/files/$file .; done
```

**Note**: This is bash syntax, may not work directly in Windows CMD. Use Git Bash or WSL.

---

### Pull Files by Session ID

If you know the session ID (e.g., `1b3e2a4c`):

```bash
adb pull /sdcard/Android/data/com.samples.passthroughcamera/files/telemetry_1b3e2a4c_*.csv .
```

---

### Delete Old Files on Quest (Free Space)

**⚠️ WARNING**: This permanently deletes files!

**Delete all telemetry files**:
```bash
adb shell rm /sdcard/Android/data/com.samples.passthroughcamera/files/telemetry_*.csv
```

**Delete files older than 7 days**:
```bash
adb shell find /sdcard/Android/data/com.samples.passthroughcamera/files/ -name "telemetry_*.csv" -mtime +7 -delete
```

---

### Verify File Integrity After Pull

**Check file size matches**:

**On Quest**:
```bash
adb shell ls -lh /sdcard/Android/data/com.samples.passthroughcamera/files/telemetry_1b3e2a4c_*.csv
```

**On PC** (Windows):
```bash
dir telemetry_1b3e2a4c_*.csv
```

**File sizes should match exactly**.

---

### Stream Live Telemetry (Real-time Monitoring)

**Not directly possible** (CSV files are write-only), but you can tail the Unity logs:

```bash
adb logcat -s Unity | findstr "LOCAL TELEMETRY"
```

**Shows**:
```
[LOCAL TELEMETRY] Wrote frame 1 (state=Displayed, row=1)
[LOCAL TELEMETRY] Wrote frame 2 (state=Displayed, row=2)
...
```

**For detailed frame data**, see Unity logs with full trace info:
```bash
adb logcat -s Unity | findstr "FRAME TRACE"
```

---

## CSV File Format

### Header Row

```csv
timestamp,scene,session_id,frame_id,unity_send_ts,unity_receive_ts,unity_display_ts,unity_drop_ts,server_receive_ts,server_process_start_ts,server_send_ts,latency_ms,upload_ms,queue_wait_ms,server_proc_ms,download_ms,parse_ms,udp_send_ms,server_pct,upload_pct,download_pct,detection_count,avg_confidence,keypoint_avg_conf,image_width,image_height,upload_bytes_uncompressed,upload_bytes_compressed,download_bytes_uncompressed,download_bytes_compressed,final_state,drop_reason,error_reason,freeze_frames_per_frame,freeze_duration_ms,cumulative_freeze_frames,freeze_ratio,frame_gap,cumulative_dropped
```

### Sample Data Row

```csv
2026-04-21 14:30:45.123,Segmentation,1b3e2a4c,1,1713712245123,1713712245378,1713712245380,0,1713712245150,1713712245155,1713712245375,252.00,27.00,5.00,220.00,223.00,2.50,1.20,87.3,10.7,88.5,3,0.8542,0,640,480,614400,18234,25600,8192,Displayed,,,0,0.00,0,0.000,1,0
```

### Column Descriptions

See [TELEMETRY_TIMESTAMP_GUIDE.md](./TELEMETRY_TIMESTAMP_GUIDE.md) for detailed column descriptions.

**Key Metrics**:
- `latency_ms`: End-to-end latency (Unity send → Unity display)
- `upload_ms`: Time to upload frame to server
- `queue_wait_ms`: Time spent waiting in server queue
- `server_proc_ms`: Server inference processing time
- `download_ms`: Time to download result from server
- `parse_ms`: Time to parse JSON response in Unity
- `final_state`: Displayed, Dropped, or Error

---

## Automated Retrieval Script

### PowerShell Script (Windows)

Save as `pull_telemetry.ps1`:

```powershell
# Pull all telemetry CSV files from Quest to PC
# Usage: .\pull_telemetry.ps1

$packageName = "com.samples.passthroughcamera"
$remotePath = "/sdcard/Android/data/$packageName/files/"
$localPath = "C:\Telemetry\"

# Create local directory if not exists
if (!(Test-Path -Path $localPath)) {
    New-Item -ItemType Directory -Path $localPath | Out-Null
    Write-Host "Created directory: $localPath"
}

# Check if Quest is connected
$devices = adb devices
if ($devices -match "device$") {
    Write-Host "Quest connected, pulling files..."

    # Pull all telemetry CSV files
    adb pull "$remotePath/telemetry_*.csv" $localPath

    # List pulled files
    Write-Host "`nPulled files:"
    Get-ChildItem -Path $localPath -Filter "telemetry_*.csv" | Select-Object Name, Length, LastWriteTime

    Write-Host "`nFiles saved to: $localPath"
} else {
    Write-Host "ERROR: Quest not connected. Please connect via USB or WiFi."
    Write-Host "Run 'adb devices' to check connection."
}
```

**Run**:
```powershell
powershell -ExecutionPolicy Bypass -File pull_telemetry.ps1
```

---

### Python Script (Cross-platform)

Save as `pull_telemetry.py`:

```python
#!/usr/bin/env python3
"""
Pull telemetry CSV files from Quest to PC.
Usage: python pull_telemetry.py [output_directory]
"""

import subprocess
import sys
import os
from pathlib import Path

PACKAGE_NAME = "com.samples.passthroughcamera"
REMOTE_PATH = f"/sdcard/Android/data/{PACKAGE_NAME}/files/"

def check_adb():
    """Check if adb is available"""
    try:
        subprocess.run(["adb", "version"], capture_output=True, check=True)
        return True
    except (subprocess.CalledProcessError, FileNotFoundError):
        print("ERROR: adb not found. Please install Android Platform Tools.")
        return False

def check_device():
    """Check if Quest is connected"""
    result = subprocess.run(["adb", "devices"], capture_output=True, text=True)
    devices = [line for line in result.stdout.split("\n") if "device" in line and "List" not in line]

    if len(devices) == 0:
        print("ERROR: No Quest device connected.")
        print("Please connect Quest via USB or WiFi.")
        return False

    print(f"✓ Quest connected ({len(devices)} device(s))")
    return True

def list_remote_files():
    """List telemetry files on Quest"""
    cmd = ["adb", "shell", "ls", "-lh", f"{REMOTE_PATH}telemetry_*.csv"]
    result = subprocess.run(cmd, capture_output=True, text=True)

    if result.returncode != 0:
        print("No telemetry files found on Quest.")
        return []

    print("\nAvailable files on Quest:")
    print(result.stdout)

    # Extract filenames
    files = []
    for line in result.stdout.split("\n"):
        if "telemetry_" in line:
            filename = line.split()[-1]
            files.append(filename)

    return files

def pull_files(output_dir):
    """Pull all telemetry files to PC"""
    # Create output directory
    Path(output_dir).mkdir(parents=True, exist_ok=True)

    # Pull files
    cmd = ["adb", "pull", f"{REMOTE_PATH}telemetry_*.csv", output_dir]
    result = subprocess.run(cmd, capture_output=True, text=True)

    print(f"\n{result.stdout}")
    print(f"✓ Files saved to: {os.path.abspath(output_dir)}")

def main():
    # Get output directory from command line or use default
    output_dir = sys.argv[1] if len(sys.argv) > 1 else "./telemetry"

    print("=== Quest Telemetry Retrieval ===\n")

    # Checks
    if not check_adb():
        return 1

    if not check_device():
        return 1

    # List available files
    files = list_remote_files()

    if not files:
        print("\nNo telemetry files to pull.")
        return 0

    # Pull files
    print(f"\nPulling {len(files)} file(s)...")
    pull_files(output_dir)

    return 0

if __name__ == "__main__":
    sys.exit(main())
```

**Run**:
```bash
python pull_telemetry.py
# Or specify output directory:
python pull_telemetry.py C:\Telemetry
```

---

## Workflow Integration

### After Each Test Session

1. **Stop the app** on Quest (or let session complete)
2. **Pull telemetry files** immediately:
   ```bash
   adb pull /sdcard/Android/data/com.samples.passthroughcamera/files/telemetry_*.csv C:\Telemetry\$(date +%Y-%m-%d)\
   ```
3. **Open in Excel** for analysis
4. **Delete old files** on Quest (optional):
   ```bash
   adb shell rm /sdcard/Android/data/com.samples.passthroughcamera/files/telemetry_*.csv
   ```

---

### Continuous Testing Workflow

**For multiple test sessions** throughout the day:

1. Create timestamped directories:
   ```bash
   mkdir C:\Telemetry\2026-04-21_morning
   mkdir C:\Telemetry\2026-04-21_afternoon
   ```

2. Pull after each session:
   ```bash
   # Morning session
   adb pull /sdcard/Android/data/com.samples.passthroughcamera/files/telemetry_*.csv C:\Telemetry\2026-04-21_morning\

   # Afternoon session
   adb pull /sdcard/Android/data/com.samples.passthroughcamera/files/telemetry_*.csv C:\Telemetry\2026-04-21_afternoon\
   ```

3. Clean Quest storage between sessions:
   ```bash
   adb shell rm /sdcard/Android/data/com.samples.passthroughcamera/files/telemetry_*.csv
   ```

---

## Excel Analysis Tips

### Load Multiple CSV Files at Once

**Excel Power Query**:
1. Data → Get Data → From File → From Folder
2. Select folder with CSV files
3. Combine & Load
4. All CSV files merged into one table

### Useful Pivot Tables

**Latency by Scene**:
- Rows: `scene`
- Values: `Average of latency_ms`, `Max of latency_ms`, `Count of frame_id`

**Upload/Download Breakdown**:
- Rows: `frame_id`
- Values: `upload_ms`, `queue_wait_ms`, `server_proc_ms`, `download_ms`, `parse_ms`
- Chart: Stacked bar chart

**Detection Performance**:
- Rows: `frame_id`
- Values: `detection_count`, `avg_confidence`, `latency_ms`
- Filter: `final_state = "Displayed"`

---

## Summary

**Quick Commands**:

```bash
# List files on Quest
adb shell ls -lh /sdcard/Android/data/com.samples.passthroughcamera/files/telemetry_*.csv

# Pull all files to current directory
adb pull /sdcard/Android/data/com.samples.passthroughcamera/files/telemetry_*.csv .

# Pull to specific folder
adb pull /sdcard/Android/data/com.samples.passthroughcamera/files/telemetry_*.csv C:\Telemetry\

# Delete old files on Quest (after pulling)
adb shell rm /sdcard/Android/data/com.samples.passthroughcamera/files/telemetry_*.csv
```

**Recommended Workflow**:
1. Run app on Quest
2. Perform inference session
3. Check Unity logs for CSV path
4. Pull CSV files to PC
5. Open in Excel for analysis
6. Clean up Quest storage

---

**See Also**:
- [TELEMETRY_TIMESTAMP_GUIDE.md](./TELEMETRY_TIMESTAMP_GUIDE.md) - Column descriptions and timestamp meanings
- [V3_UDP_BUILD_DEPLOY_GUIDE.md](./V3_UDP_BUILD_DEPLOY_GUIDE.md) - Build and deployment workflow
- [FRAME_CADENCE_GUIDE.md](./FRAME_CADENCE_GUIDE.md) - Frame frequency configuration

---

**Last Updated**: 2026-04-21
**Version**: V3.0 UDP Architecture
