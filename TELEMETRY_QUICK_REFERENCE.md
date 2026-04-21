# Telemetry Retrieval - Quick Reference Card

**Package**: `com.samples.passthroughcamera`
**Quest Path**: `/sdcard/Android/data/com.samples.passthroughcamera/files/`
**File Pattern**: `telemetry_{session_id}_{timestamp}.csv`

---

## 🚀 Quick Commands

### Pull All Files
```bash
adb pull /sdcard/Android/data/com.samples.passthroughcamera/files/telemetry_*.csv .
```

### List Files on Quest
```bash
adb shell ls -lh /sdcard/Android/data/com.samples.passthroughcamera/files/telemetry_*.csv
```

### Pull to Specific Folder
```bash
adb pull /sdcard/Android/data/com.samples.passthroughcamera/files/telemetry_*.csv C:\Telemetry\
```

### Delete Files on Quest
```bash
adb shell rm /sdcard/Android/data/com.samples.passthroughcamera/files/telemetry_*.csv
```

---

## 🔧 Automated Scripts

### PowerShell (Windows)
```powershell
.\Tools\pull_telemetry.ps1
# Or specify output directory:
.\Tools\pull_telemetry.ps1 C:\Telemetry
```

### Python (Cross-platform)
```bash
python Tools/pull_telemetry.py
# Or specify output directory:
python Tools/pull_telemetry.py C:/Telemetry
```

---

## 🔍 Troubleshooting

### No Files Found
```bash
# Check Unity logs for telemetry file path
adb logcat -s Unity | findstr "LOCAL TELEMETRY"
```

### Verify App Installed
```bash
adb shell pm list packages | findstr passthrough
# Expected: package:com.samples.passthroughcamera
```

### Permission Denied
```bash
# Copy to public directory first
adb shell cp /sdcard/Android/data/com.samples.passthroughcamera/files/telemetry_*.csv /sdcard/
adb pull /sdcard/telemetry_*.csv .
```

---

## 📊 CSV Columns (37 total)

**Key Metrics**:
- `latency_ms` - End-to-end latency
- `upload_ms` - Upload time
- `queue_wait_ms` - Server queue wait
- `server_proc_ms` - Server processing
- `download_ms` - Download time
- `parse_ms` - JSON parse time
- `final_state` - Displayed/Dropped/Error

**See**: [TELEMETRY_TIMESTAMP_GUIDE.md](./Documentation/TELEMETRY_TIMESTAMP_GUIDE.md) for full column list

---

## 📚 Full Documentation

- **[TELEMETRY_RETRIEVAL_GUIDE.md](./Documentation/TELEMETRY_RETRIEVAL_GUIDE.md)** - Complete guide with advanced commands
- **[TELEMETRY_TIMESTAMP_GUIDE.md](./Documentation/TELEMETRY_TIMESTAMP_GUIDE.md)** - Column descriptions
- **[FRAME_CADENCE_GUIDE.md](./Documentation/FRAME_CADENCE_GUIDE.md)** - Frame frequency configuration
