# Temporary Segmentation Test Workaround

**Date**: 2026-04-17 23:30
**Purpose**: Allow immediate Segmentation testing without Unity rebuild

---

## Problem Analysis

You correctly identified the key issue: **PoseEstimation "works" because `mode='both'` is the server default!**

### Why PoseEstimation Appears to Work

**PoseEstimation needs**: `mode='both'` (detection + pose)
**Server default when telemetry empty**: `mode='both'`
**Result**: Works by coincidence ✅

### Why Segmentation Doesn't Work

**Segmentation needs**: `mode='segmentation'`
**Server default when telemetry empty**: `mode='both'`
**Result**: Wrong inference type ❌

### Why Scene-Based Workaround Failed

My earlier workaround checked `scene` field to infer mode:
```python
if mode == 'both' and scene != 'unknown':
    if scene == 'Segmentation':
        mode = 'segmentation'
```

**Problem**: When telemetry is **completely empty** (`{}`), even `scene` is missing:
```python
scene = telemetry.get('scene', 'unknown')  # → 'unknown'
```

So workaround never triggers.

---

## Temporary Solution for Testing

Since you want to test Segmentation NOW without waiting for Unity rebuild, here's a quick server-side hack:

### Option 1: Force Segmentation Mode (Testing Only)

**Edit**: `app/transport/udp_ingest.py` Line 281

**Change from**:
```python
mode = telemetry.get('mode', 'both')
```

**Change to** (TEMPORARY!):
```python
# TEMPORARY: Force segmentation mode for testing
# TODO: Remove after Unity APK rebuild
mode = telemetry.get('mode', 'segmentation')  # Default to segmentation instead of 'both'
```

**Effect**:
- ALL scenes will default to segmentation mode
- PoseEstimation will break (no pose, only segmentation masks)
- MultiObjectDetection will break (segmentation instead of detection)
- **But Segmentation scene will work!** ✅

**To test Segmentation**:
1. Apply this change
2. Restart server
3. Run Segmentation scene on Quest
4. Verify server logs show `mode=segmentation`

**To test other scenes**:
Revert the change back to `'both'`

---

### Option 2: Environment Variable Switch

**Better approach**: Use environment variable to control default mode for testing.

**Edit**: `app/transport/udp_ingest.py` Line 281

**Change from**:
```python
mode = telemetry.get('mode', 'both')
```

**Change to**:
```python
import os
# Allow override via environment variable for testing
default_mode = os.getenv('UDP_DEFAULT_MODE', 'both')
mode = telemetry.get('mode', default_mode)
print(f"[UDP INGEST DEBUG] Telemetry mode: {telemetry.get('mode', 'MISSING')}, using: {mode}")
```

**Usage**:

**Test Segmentation**:
```bash
# Set environment variable
$env:UDP_DEFAULT_MODE = "segmentation"

# Start server
python -m uvicorn app.main:app --host 0.0.0.0 --port 8001 --workers 1
```

**Test PoseEstimation**:
```bash
# Set environment variable
$env:UDP_DEFAULT_MODE = "both"

# Start server
python -m uvicorn app.main:app --host 0.0.0.0 --port 8001 --workers 1
```

**Test MultiObjectDetection**:
```bash
# Set environment variable
$env:UDP_DEFAULT_MODE = "detection"

# Start server
python -m uvicorn app.main:app --host 0.0.0.0 --port 8001 --workers 1
```

---

### Option 3: Session-Based Manual Configuration (Most Flexible)

Create a config file where you manually map session IDs to modes.

**Create file**: `server_session_modes.json`
```json
{
  "default_mode": "both",
  "session_overrides": {
    "your-segmentation-session-id-here": "segmentation",
    "your-pose-session-id-here": "both",
    "your-detection-session-id-here": "detection"
  }
}
```

Then modify `udp_ingest.py` to read this config and override mode based on session_id.

**Pros**: Can test all three scenes without server restart
**Cons**: Need to get session IDs from Unity logs first

---

## Recommendation

**For immediate testing**: Use **Option 2 (Environment Variable)**

**Steps**:
1. Add the environment variable code to `udp_ingest.py`
2. Restart server with `UDP_DEFAULT_MODE=segmentation`
3. Test Segmentation scene
4. Verify server logs show:
   ```
   [UDP INGEST DEBUG] Telemetry mode: MISSING, using: segmentation
   [UDP WORKER] Processing ... (mode=segmentation)
   [UDP WORKER SEGMENTATION] Mask 1: 340x180, base64: 12480 chars
   ```

5. To test other scenes, stop server and restart with different `UDP_DEFAULT_MODE`

---

## Long-Term Solution

**None of these workarounds are permanent fixes!**

The only real solution is **Unity APK rebuild** with the timing fix (`TryDisplayNewestFrame()` added to `RunInferenceNonBlocking()`).

Once rebuilt:
- Telemetry will include `mode` field
- Server will use telemetry mode (not default)
- All scenes will work correctly without workarounds

---

## Summary

**Your observation is correct**: PoseEstimation "works" because it happens to need the same mode as the server default.

**Best temporary solution**: Add environment variable to control default mode, then test each scene separately by changing the variable.

**Permanent solution**: Unity rebuild (still required for complete telemetry and metrics).

---

**Last Updated**: 2026-04-17 23:30 UTC
**Status**: Workaround documented, awaiting your choice of implementation
