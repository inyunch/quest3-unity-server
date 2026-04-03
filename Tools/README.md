# Tools

Diagnostic and monitoring scripts for Unity Passthrough Camera API development and debugging.

## Available Tools

### Diagnostic Scripts

#### `collect_diagnostic_logs.bat`
Collects comprehensive diagnostic logs from Quest 3 device.

**Usage**:
```bash
.\collect_diagnostic_logs.bat
```

**What it collects**:
- Unity logs (filtered)
- System information
- Network status
- Build configuration
- Saves to timestamped file in `output/` folder

---

#### `verify_server_connection.bat`
Tests network connectivity between Quest 3 and inference server.

**Usage**:
```bash
.\verify_server_connection.bat
```

**What it checks**:
- Server reachability (ping test)
- HTTP endpoint availability
- Latency measurements
- Firewall issues

---

### Monitoring Scripts

#### `monitor_panel_logs.bat`
Real-time log monitoring with filtering for specific components.

**Usage**:
```bash
.\monitor_panel_logs.bat
```

**Features**:
- Live log streaming from device
- Filtered by component tags (POSE, HUD, LATENCY, etc.)
- Color-coded output (if supported)
- Ctrl+C to stop

---

#### `test_latency_display.bat`
Monitors latency metrics in real-time from HUD system.

**Usage**:
```bash
.\test_latency_display.bat
```

**What it displays**:
- End-to-end (E2E) latency
- Upload/Download times
- Server processing time
- Frame rate
- Detection count

---

## Prerequisites

All scripts require:
- **ADB (Android Debug Bridge)** installed and in PATH
- **Quest 3** connected via USB or WiFi
- **Developer Mode** enabled on Quest 3

### Setup ADB

1. Download Android Platform Tools:
   - Windows: https://dl.google.com/android/repository/platform-tools-latest-windows.zip
   - Mac: https://dl.google.com/android/repository/platform-tools-latest-darwin.zip
   - Linux: https://dl.google.com/android/repository/platform-tools-latest-linux.zip

2. Extract and add to PATH:
   ```bash
   # Windows (PowerShell as Admin)
   $env:Path += ";C:\path\to\platform-tools"

   # Mac/Linux
   export PATH=$PATH:/path/to/platform-tools
   ```

3. Verify installation:
   ```bash
   adb version
   # Should output: Android Debug Bridge version X.X.X
   ```

4. Connect Quest and verify:
   ```bash
   adb devices
   # Should show your device ID
   ```

---

## Troubleshooting

### "ADB not found"
- Ensure Android Platform Tools are installed
- Add platform-tools folder to system PATH
- Restart terminal/command prompt after PATH changes

### "No devices found"
- Connect Quest 3 via USB
- Enable Developer Mode in Meta Quest app
- Allow USB debugging prompt on headset
- Try `adb kill-server` then `adb devices`

### "Permission denied"
- Allow USB debugging on Quest 3 when prompted
- Check "Always allow from this computer"
- Reconnect USB cable

---

## Output Files

Scripts save output to:
- `output/` - Diagnostic logs and reports
- Filenames include timestamps for easy tracking

**Example**:
```
output/
├── diagnostic_20260403_142530.log
├── latency_test_20260403_143015.log
└── connection_test_20260403_143245.log
```

---

## Related Documentation

- [Latency HUD Guide](../Documentation/LATENCY_HUD_GUIDE.md) - Understanding latency metrics
- [Quick Start Guide](../Documentation/QUICK_START_GUIDE.md) - Initial setup
- [Main README](../README.md) - Project overview

---

**Last Updated**: 2026-04-03
