# Final Fix: Timing Issue in N+1 Telemetry

**Date**: 2026-04-17
**Status**: 🔧 Fix applied, requires Unity rebuild

---

## Root Cause Identified

**Problem**: `RunInferenceNonBlocking()` was missing the `TryDisplayNewestFrame()` call at the beginning.

**Effect**:
1. Frame N response received → state = `Completed`
2. Frame N+1 starts → `SendFrameUDP()` checks Frame N
3. Frame N is still `Completed` (not `Displayed` yet)
4. Telemetry check fails: `state != Displayed/Dropped/Failed`
5. Frame N+1 sent with **empty telemetry**
6. Later, Update() → `TryDisplayNewestFrame()` → Frame N becomes `Displayed`
7. Too late! Frame N+1 already sent

**Evidence from Unity logs**:
```
[UNITY TELEMETRY] ✗ Frame 135 not final yet (state=Completed)  ← When sending Frame 136
[TELEMETRY QUEUE] Frame 135 DISPLAYED ↦queued  ← Later, but Frame 136 already sent
[UDP SEND] Frame 136 sent, telemetry=0  ← Empty!
```

---

## Fix Applied

**File**: `SegmentationInferenceRunManager.cs` line 417-421

**Added**:
```csharp
private IEnumerator RunInferenceNonBlocking()
{
    // CRITICAL: Display completed frames BEFORE starting new inference
    // This ensures previous frames reach Displayed state before N+1 telemetry is sent
    TryDisplayNewestFrame();  ← NEW!

    // Quick checks...
    if (!m_cameraAccess.IsPlaying)
    {
        // ...
    }
    // ... rest of method
}
```

**This matches the HTTP version** (line 630 in `RunServerInference()`):
```csharp
private IEnumerator RunServerInference(Texture texture)
{
    // CRITICAL: Display completed frames BEFORE starting new inference
    TryDisplayNewestFrame();  ← HTTP version has this!

    // Increment frame counter and start E2E timing
    m_frameId++;
    // ...
}
```

---

## Why This Fix Works

**Old timing** (broken):
```
Update() called:
  → TryDisplayNewestFrame() (finds nothing, Frame N still Completed)
  → Trigger new inference
    → RunInferenceNonBlocking()
      → SendFrameUDP() checks Frame N → state=Completed → No telemetry
      → Send Frame N+1 with empty telemetry
  → (later in same Update()) TryDisplayNewestFrame() → Frame N becomes Displayed
```

**New timing** (fixed):
```
Update() called:
  → TryDisplayNewestFrame() (background, may process some frames)
  → Trigger new inference
    → RunInferenceNonBlocking()
      → TryDisplayNewestFrame() ← NEW! Process Frame N → Displayed
      → SendFrameUDP() checks Frame N → state=Displayed → ✓ Send telemetry!
      → Send Frame N+1 with Frame N's telemetry
```

---

## Expected Results After Rebuild

### Unity Logs

**Before fix**:
```
[UNITY TELEMETRY] ✗ Frame 135 not final yet (state=Completed)
[UDP SEND] Frame 136 sent, size=23038 bytes (telemetry=0)  ← Empty!
```

**After fix**:
```
[DISPLAY CHECK] Found 1 completed frames ready to display
[TELEMETRY QUEUE] Frame 135 DISPLAYED ↦queued
[UNITY TELEMETRY] ✓ Sending telemetry for frame 135, final_state=Displayed, json_length=850
[UDP SEND] Frame 136 sent, size=23888 bytes (telemetry=850)  ← Non-zero!
```

---

### Server Logs

**Before fix**:
```
[UDP EXCEL DEBUG] Processing frame 136, telemetry keys: []
[UDP EXCEL] No telemetry for frame 136
[UDP WORKER] Processing e9aebea5_137 (mode=both)  ← Wrong mode (default)
```

**After fix**:
```
[UDP EXCEL DEBUG] Processing frame 136, telemetry keys: ['scene', 'mode', 'frame_id', ...]
[UDP EXCEL] Frame 136 carries telemetry for frame 135
[UDP EXCEL] Logged frame 135 (final_state=Displayed)
[UDP WORKER] Processing e9aebea5_137 (mode=segmentation)  ← Correct mode!
```

---

### Excel Output

**Before fix**:
```
scene | mode | detection_count | avg_confidence
------|------|-----------------|---------------
      |      | 0               | 0
```

**After fix**:
```
scene        | mode         | detection_count | avg_confidence | latency_ms
-------------|--------------|-----------------|----------------|------------
Segmentation | segmentation | 1               | 0.67           | 192.6
Segmentation | segmentation | 1               | 0.63           | 118.8
```

---

## Rebuild Instructions

### Step 1: Rebuild Unity APK

```
Unity Editor → File → Build Settings → Build And Run
```

**Wait for**:
- Compilation to complete (check Unity Console for errors)
- Build to finish (~5-10 minutes)
- Deployment to Quest 3

### Step 2: Verify New Code is Running

After deployment, run Segmentation scene and check Unity logs:

```bash
adb logcat -s Unity | findstr "UNITY TELEMETRY.*✓"
```

**Should see**:
```
[UNITY TELEMETRY] ✓ Sending telemetry for frame 135, session=e9aebea5, final_state=Displayed, json_length=850
```

**If still seeing "✗ not final yet"** → APK didn't update, rebuild again.

---

### Step 3: Verify Server Receives Telemetry

Check server logs for non-empty telemetry:

**Should see**:
```
[UDP EXCEL DEBUG] Processing frame 137, telemetry keys: ['scene', 'session_id', 'frame_id', 'mode', ...]
[UDP WORKER] Processing e9aebea5_137 (mode=segmentation)
```

**Should NOT see**:
```
[UDP EXCEL DEBUG] Processing frame 137, telemetry keys: []  ← Empty = broken
[UDP WORKER] Processing e9aebea5_137 (mode=both)  ← Wrong mode = broken
```

---

### Step 4: Verify Excel Logging

Check Excel file (should appear in server directory):

**Expected columns with values**:
- scene: "Segmentation"
- mode: "segmentation"
- detection_count: 1 (when person detected)
- avg_confidence: 0.6-0.9
- latency_ms: 100-250
- upload_bytes: ~22000
- download_bytes: ~5000-15000

**All fields should have non-zero values!**

---

## Alternative: Server-Side Workaround (If Unity Fix Doesn't Work)

If Unity rebuild still doesn't work, we can implement a server-side workaround:

### Option 1: Session-Based Mode Memory

**File**: `app/transport/udp_ingest.py`

```python
# Add at class level
self.session_modes = {}  # session_id -> mode

# In enqueue_frame(), after extracting telemetry:
scene = telemetry.get('scene', 'unknown')
mode = telemetry.get('mode', None)

# If no mode in telemetry, use last known mode for this session
if mode is None:
    mode = self.session_modes.get(session_id, 'both')  # Default to 'both'
else:
    # Remember mode for this session
    self.session_modes[session_id] = mode

# Infer mode from scene if still unknown
if mode == 'both' and scene == 'Segmentation':
    mode = 'segmentation'
elif mode == 'both' and scene == 'MultiObjectDetection':
    mode = 'detection'
```

**Pros**: Works without Unity changes
**Cons**: First frame of each session will still use wrong mode

---

### Option 2: Scene-Based Mode Inference

**File**: `app/transport/udp_ingest.py`

```python
# After extracting telemetry:
scene = telemetry.get('scene', 'unknown')
mode = telemetry.get('mode', None)

# Infer mode from scene if not provided
if mode is None or mode == 'both':
    if scene == 'Segmentation':
        mode = 'segmentation'
        print(f"[UDP INGEST] Inferred mode=segmentation from scene={scene}")
    elif scene == 'MultiObjectDetection':
        mode = 'detection'
        print(f"[UDP INGEST] Inferred mode=detection from scene={scene}")
    elif scene == 'PoseEstimation':
        mode = 'both'
    else:
        mode = 'both'  # Default
```

**Pros**: Simple, works immediately
**Cons**: Relies on scene name, not explicit mode field

---

## Current Status

**Unity-side**:
- ✅ Mode field added to `BuildTelemetryJson()`
- ✅ Timing fix applied (`TryDisplayNewestFrame()` added)
- ❌ APK not yet rebuilt with new code

**Server-side**:
- ✅ Segmentation mode handler implemented
- ✅ Excel logger working
- ✅ Mode extraction from telemetry working
- ⚠️ Defaulting to `mode=both` when telemetry is empty

**Blocking issue**: Unity APK needs rebuild for fixes to take effect.

---

## Summary

The fix is simple and correct: add `TryDisplayNewestFrame()` at the start of `RunInferenceNonBlocking()`.

This ensures frames transition to `Displayed` state **before** the next frame tries to send their telemetry.

**Once Unity is rebuilt**, the N+1 telemetry pattern will work correctly and server will receive:
- `mode=segmentation` for Segmentation scene
- Complete metrics in telemetry JSON
- Excel with non-zero values

---

**Last Updated**: 2026-04-17 16:00 UTC
**Status**: 🔧 Fix applied, waiting for Unity rebuild
