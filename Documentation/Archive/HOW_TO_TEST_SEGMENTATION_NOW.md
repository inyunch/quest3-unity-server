# How to Test Segmentation NOW (Without Unity Rebuild)

**Date**: 2026-04-17 23:15
**Status**: ✅ Server code updated, ready to test

---

## Quick Summary

I've implemented an **environment variable workaround** that lets you test Segmentation immediately without waiting for Unity rebuild.

### What I Did

1. **Updated server code** (`app/transport/udp_ingest.py`) to support `UDP_DEFAULT_MODE` environment variable
2. **Created batch files** for easy server startup with different modes
3. **Server is now ready** to use Segmentation mode

---

## How to Start Server for Segmentation Testing

### Option 1: Use the Batch File (Easiest)

**Open Command Prompt (cmd.exe)** and run:
```cmd
C:\Repo\Github\vision_server\start_server_segmentation.bat
```

This automatically:
- Sets `UDP_DEFAULT_MODE=segmentation`
- Starts server on port 8001
- Uses Segmentation mode for all incoming frames

### Option 2: Manual Command

**Open Command Prompt (cmd.exe)** and run:
```cmd
cd C:\Repo\Github\vision_server
set UDP_DEFAULT_MODE=segmentation
python -m uvicorn app.main:app --host 0.0.0.0 --port 8001 --workers 1
```

---

## Expected Server Logs

Once server starts, you should see:
```
[YOLO] Model ready: yolov8n (80 classes, person at index 0)
[SEGMENTATION] YOLO11n-seg model loaded successfully
INFO:     Uvicorn running on http://0.0.0.0:8001
[UDP WORKER] Worker loop started, waiting for UDP frames...
```

When Segmentation scene sends frames (with empty telemetry):
```
[UDP INGEST] Frame 137: Telemetry empty or missing mode field, using default_mode=segmentation
[UDP WORKER] Processing sessionid_137 (mode=segmentation)
[UDP WORKER mode=segmentation] YOLO detected 2 objects, 1 person(s)
[UDP WORKER SEGMENTATION] Mask 1: 340x180, base64: 12480 chars
[UDP WORKER mode=segmentation] 1 person(s) with masks
```

**Key logs to look for**:
- `using default_mode=segmentation` ← Confirms env var working
- `mode=segmentation` ← Correct inference mode
- `Mask 1: ...` ← Segmentation masks generated

---

## Testing Steps

### Step 1: Start Server
Run the batch file or manual command above.

### Step 2: Run Segmentation Scene on Quest 3
1. Open Segmentation scene
2. Stand in front of camera (1-2 meters, well-lit)
3. Let it run for 10-20 frames

### Step 3: Check Server Console
Look for the logs mentioned above. Specifically:
- `[UDP INGEST] ... using default_mode=segmentation`
- `[UDP WORKER] ... (mode=segmentation)`
- `[UDP WORKER SEGMENTATION] Mask 1: ...`

### Step 4: Check Unity (Quest) Logs
```bash
adb logcat -s Unity | findstr "SEGMENTATION\|MASK"
```

Expected when person detected:
```
[DISPLAY] Frame 165: Converted 1 detections
[MASK] Loading mask texture: 340x180
[UI INFERENCE] RenderMask called for mask 0
```

---

## Testing Other Scenes

### For PoseEstimation
Stop server (Ctrl+C), then restart with:
```cmd
cd C:\Repo\Github\vision_server
set UDP_DEFAULT_MODE=both
python -m uvicorn app.main:app --host 0.0.0.0 --port 8001 --workers 1
```

### For MultiObjectDetection
Stop server (Ctrl+C), then restart with:
```cmd
cd C:\Repo\Github\vision_server
set UDP_DEFAULT_MODE=detection
python -m uvicorn app.main:app --host 0.0.0.0 --port 8001 --workers 1
```

---

## Important Notes

### This is a Temporary Workaround

**Limitations**:
- You can only test ONE scene at a time (need to restart server to switch modes)
- Telemetry is still empty (no metrics in Excel except mode)
- Not a production solution

**Permanent Fix**: Still requires Unity APK rebuild

### Why This Works

**The Problem**: PoseEstimation "works" because it needs `mode='both'`, which is the server default when telemetry is empty.

**The Solution**: Change the default to `'segmentation'` via environment variable, so Segmentation scene works even with empty telemetry.

### Excel Logging Status

**What will work**:
- `mode`: Will show "segmentation"
- `scene`: Will still be empty (not transmitted)
- Detection results: Will work if person detected

**What won't work** (until Unity rebuild):
- Full telemetry fields (latency, upload_bytes, etc.)
- All will be 0 or empty

---

## Troubleshooting

### Server doesn't show "using default_mode=segmentation"
**Problem**: Environment variable not set
**Solution**: Make sure you're using the batch file or manually `set UDP_DEFAULT_MODE=segmentation` before starting server

### Still shows "mode=both"
**Problem**: Old server still running
**Solution**:
1. Find and kill old server: `netstat -ano | findstr "8001"`
2. `taskkill /F /PID <PID>`
3. Restart with batch file

### No segmentation masks generated
**Problem**: No person in camera view
**Solution**:
1. Stand in front of Quest 3 camera
2. 1-2 meters distance
3. Well-lit environment
4. Full upper body visible

---

## Summary

**To test Segmentation NOW**:
1. Run: `C:\Repo\Github\vision_server\start_server_segmentation.bat`
2. Open Segmentation scene on Quest 3
3. Stand in front of camera
4. Check server logs for `mode=segmentation` and `Mask 1: ...`

**Server is ready!** Just run the batch file and test.

---

**Last Updated**: 2026-04-17 23:15 UTC
**Server Code**: Updated with UDP_DEFAULT_MODE support
**Batch File**: Created at `C:\Repo\Github\vision_server\start_server_segmentation.bat`
**Status**: ✅ Ready to test!
