# ADB Telemetry Monitoring Guide

**Purpose**: Use ADB to monitor Segmentation telemetry in real-time on Quest 3

## Prerequisites

- Quest 3 connected via USB
- Developer mode enabled
- ADB installed and in PATH

**Quick Check**:
```bash
adb devices
```
Should show: `2G97C5ZH5100P2	device`

## Quick Start (Recommended)

### Option 1: Quick Check (Fastest)

**Use this to quickly check if telemetry is working**

```bash
quick_check_telemetry.bat
```

**What it does**:
- Checks existing logcat buffer (no need to restart app)
- Shows summary of frames created, completed, displayed
- Shows if delayed headers are being sent
- Identifies the problem immediately

**Expected Output (Working)**:
```
Frames Created:           10
Frames Completed:         10
Frames Displayed (saved): 10
Delayed Headers Sent:     9

Status: SEGMENTATION IS RUNNING
Telemetry: HEADERS ARE BEING SENT
```

**Expected Output (Broken - m_lastCompletedTrace NULL)**:
```
Frames Created:           10
Frames Completed:         10
Frames Displayed (saved): 10
Delayed Headers Sent:     0

Status: SEGMENTATION IS RUNNING
Telemetry: NO HEADERS SENT - m_lastCompletedTrace might be NULL
```

### Option 2: Real-Time Monitor (Most Detailed)

**Use this to watch telemetry as it happens**

```bash
python monitor_segmentation_telemetry.py
```

**What it does**:
- Clears logcat buffer
- Shows color-coded real-time events:
  - 🚀 Scene start
  - 📝 Frame created
  - ✓ Frame completed
  - 🎬 Frame displayed
  - 📤 Delayed headers sent
  - ⚠️ NULL warnings

**How to use**:
1. Run the script
2. Launch Segmentation on Quest 3
3. Send frames
4. Watch the output
5. Press Ctrl+C when done

### Option 3: Simple Monitor (Lightweight)

**Use this for basic monitoring**

```bash
check_segmentation_telemetry.bat
```

**What it does**:
- Clears logcat
- Shows only TELEMETRY DEBUG and SEGMENTATION messages
- Lightweight, no parsing

## Interpreting Results

### Scenario 1: Everything Working ✅

```
📝 Frame 1 Created
   unity_send_ts: 1776279891690 ✅

⚠️  Frame 1: No Previous Frame (Expected for Frame 0)

✓ Frame 1 Completed
   state: Completed
   server_recv: 1776279891390 ✅

🎬 Frame 1 Displayed & Saved
   state: Displayed

📝 Frame 2 Created
   unity_send_ts: 1776279892106 ✅

📤 Sending Delayed Headers (Frame 1)
   state: Displayed
   unity_send_ts: 1776279891690
```

**Diagnosis**: Perfect! Telemetry working correctly.

### Scenario 2: Timestamps All 0 ❌

```
📝 Frame 1 Created
   unity_send_ts: 0 ❌
```

**Diagnosis**: `TimestampUtil.GetUnixTimestampMs()` is broken
**Fix**: Check TimestampUtil.cs implementation

### Scenario 3: m_lastCompletedTrace Always NULL ❌

```
📝 Frame 1 Created
   unity_send_ts: 1776279891690 ✅

⚠️  Frame 1: No Previous Frame (Expected)

✓ Frame 1 Completed
   state: Completed
   server_recv: 1776279891390 ✅

🎬 Frame 1 Displayed & Saved
   state: Displayed

📝 Frame 2 Created
   unity_send_ts: 1776279892106 ✅

⚠️  Frame 2: No Previous Frame (NOT EXPECTED!)
```

**Diagnosis**: `m_lastCompletedTrace` is being reset to null
**Fix**: Check if something is setting it to null between frames

### Scenario 4: Frames Created but Never Completed ❌

```
📝 Frame 1 Created
   unity_send_ts: 1776279891690 ✅

📝 Frame 2 Created
   unity_send_ts: 1776279892106 ✅

📝 Frame 3 Created
   unity_send_ts: 1776279892500 ✅

(No "Frame Completed" messages)
```

**Diagnosis**: Server not responding or response callback not executing
**Fix**: Check network connection, server logs

### Scenario 5: Frames Completed but Never Displayed ❌

```
✓ Frame 1 Completed
   state: Completed

✓ Frame 2 Completed
   state: Completed

(No "Frame Displayed" messages)
```

**Diagnosis**: `TryDisplayNewestFrame()` not being called or not executing
**Fix**: Check Update() method, check if scene is active

## Advanced Debugging

### Check if Scene is Running

```bash
adb logcat -d -s Unity:E | findstr /C:"SEGMENTATION INFERENCE RUN MANAGER START"
```

Should show:
```
========================================
SEGMENTATION INFERENCE RUN MANAGER START!
========================================
```

If not found: Scene not loaded or script not executing

### Get Full Frame Lifecycle for Specific Frame

```bash
adb logcat -d -s Unity:W | findstr /C:"Frame 5"
```

Shows all events for frame 5:
- Created
- Sent to server
- Completed
- Displayed
- Headers sent in next request

### Check Server Timestamps

```bash
adb logcat -d -s Unity:W | findstr /C:"server_recv"
```

Should show non-zero values:
```
server_recv: 1776279891390 ✅
```

If all 0:
```
server_recv: 0 ❌
```
Then server timestamp parsing is broken

### Export Full Log for Analysis

```bash
adb logcat -d -s Unity:V Unity:D Unity:I Unity:W Unity:E > segmentation_full_log.txt
```

Then search in the file for specific patterns

## Troubleshooting

### "adb: command not found"

ADB not in PATH. Add Android SDK platform-tools to PATH or use full path:
```bash
"C:\Users\user\AppData\Local\Android\Sdk\platform-tools\adb.exe" devices
```

### No Output from Scripts

1. Check Quest 3 connection: `adb devices`
2. Verify app is running on Quest 3
3. Check if Segmentation scene is active (not other scenes)
4. Verify debug code was compiled (check Unity build timestamp)

### Too Much Output

Use filtering:
```bash
adb logcat -s Unity:W | findstr /C:"TELEMETRY DEBUG"
```

### Want to Save to File

```bash
adb logcat -d -s Unity:W > telemetry_log.txt
findstr /C:"TELEMETRY" telemetry_log.txt
```

## What to Share with Developer

After running `quick_check_telemetry.bat`, share:

1. **The summary section**:
   ```
   Frames Created:           10
   Frames Completed:         10
   Frames Displayed (saved): 10
   Delayed Headers Sent:     0
   ```

2. **Sample of debug messages** (first 3 frames):
   ```bash
   adb logcat -d -s Unity:W | findstr /C:"TELEMETRY DEBUG" | head -20
   ```

3. **Any error messages**:
   ```bash
   adb logcat -d -s Unity:E | findstr /C:"error" /C:"Error" /C:"ERROR"
   ```

This will provide enough information to diagnose the exact issue!

## Files Created

- `quick_check_telemetry.bat` - Quick summary check (recommended)
- `monitor_segmentation_telemetry.py` - Real-time color-coded monitor
- `check_segmentation_telemetry.bat` - Simple real-time filter
- `capture_segmentation_logs.bat` - Full log capture with background save
- `ADB_TELEMETRY_MONITORING.md` - This guide

## Next Steps

1. Run `quick_check_telemetry.bat` first
2. If issue found, run `python monitor_segmentation_telemetry.py` to see detailed flow
3. Share results to get targeted fix
