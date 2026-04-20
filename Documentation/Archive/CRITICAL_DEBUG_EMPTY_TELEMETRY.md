# CRITICAL DEBUG: Empty Telemetry Investigation

**Date**: 2026-04-17
**Status**: 🔍 Investigating why telemetry is empty despite code being correct

---

## Problem Summary

Server logs show:
```
[UDP WORKER] Processing c790a3a9_210 (mode=both)  ❌ Should be segmentation
[UDP EXCEL DEBUG] Processing frame 210, telemetry keys: []  ❌ Empty!
[UDP EXCEL] No telemetry for frame 210
```

**Expected**:
```
[UDP WORKER] Processing c790a3a9_210 (mode=segmentation)  ✅
[UDP EXCEL DEBUG] telemetry keys: ['scene', 'mode', 'frame_id', ...]  ✅
```

---

## Root Cause Analysis

### Unity N+1 Telemetry Pattern

**How it works**:
1. **Frame N** is sent → No telemetry (first frame)
2. **Frame N** response received → `ProcessServerResponse()` marks it as `Completed`
3. **Update()** → `TryDisplayNewestFrame()` → `DisplayFrame()` → marks it as `Displayed`
4. **Frame N+1** is sent → **Includes Frame N's telemetry** (now in final state `Displayed`)

**Why telemetry is empty**:

Session `c790a3a9` frames 209-215 ALL show empty telemetry, which means:
- **Either**: Frames are NOT reaching `Completed` state (response not received)
- **Or**: Frames are `Completed` but NOT reaching final states (`Displayed/Dropped/Failed`)

### Evidence from Server Logs

Server IS returning responses successfully:
```
[RESULT CACHE] Stored result for c790a3a9_210
[RESPONSE] Serving result for c790a3a9_210
INFO: "GET /response/c790a3a9.../210 HTTP/1.1" 200 OK  ✅ Response sent!
```

So the problem is **Unity-side**: frames are not transitioning through states correctly.

---

## Diagnostic Changes Applied

### File: SegmentationInferenceRunManager.cs

**1. ProcessServerResponse() - Line 1634**:
```csharp
// CRITICAL DEBUG: Log frame state transition
Debug.LogWarning($"[FRAME STATE] Frame {trace.frame_id} state={trace.state}, session={trace.session_id.Substring(0, 8)}");
```

**Expected log when working**:
```
[FRAME STATE] Frame 210 state=Completed, session=c790a3a9
```

---

**2. TryDisplayNewestFrame() - Line 1040**:
```csharp
// CRITICAL DEBUG: Log completed frame count
if (completedFrames.Count > 0)
{
    Debug.Log($"[DISPLAY CHECK] Found {completedFrames.Count} completed frames ready to display");
}
```

**Expected log when working**:
```
[DISPLAY CHECK] Found 3 completed frames ready to display
```

**If this log is MISSING** → Frames are stuck in `Completed` state, never being picked up for display.

---

**3. SendFrameUDP() - Line 1435**:
```csharp
Debug.LogWarning($"[UNITY TELEMETRY] ✓ Sending telemetry for frame {prevTrace.frame_id}, " +
          $"session={prevTrace.session_id.Substring(0, 8)}, " +
          $"final_state={prevTrace.state}, " +
          $"json_length={prevTelemetryJson?.Length ?? 0}");
```

**Expected log when working**:
```
[UNITY TELEMETRY] ✓ Sending telemetry for frame 210, session=c790a3a9, final_state=Displayed, json_length=850
```

**If you see**:
```
[UNITY TELEMETRY] ✗ Frame 210 not final yet (state=Completed)
```

→ Frame was received but **never displayed/dropped**, so telemetry can't be sent.

---

## Testing Instructions

### Step 1: Rebuild Unity APK

**CRITICAL**: You MUST rebuild for the new debug logs to appear.

1. Unity Editor → File → Build Settings
2. Click "Build And Run"
3. Wait for deployment

### Step 2: Run Segmentation Scene

1. Open Segmentation scene on Quest 3
2. Let it run for 10-20 frames
3. Collect Unity logs:
   ```bash
   adb logcat -s Unity > segmentation_debug.log
   ```

### Step 3: Analyze Unity Logs

**Search for these patterns**:

#### A. Are responses being received?
```bash
findstr "UDP POLL.*received" segmentation_debug.log
```

**Expected**:
```
[UDP POLL] Frame 210 received after 0.25s
[UDP POLL] Frame 211 received after 0.22s
```

**If MISSING** → Unity is not polling or server is not responding (check server logs).

---

#### B. Are frames transitioning to Completed?
```bash
findstr "FRAME STATE" segmentation_debug.log
```

**Expected**:
```
[FRAME STATE] Frame 210 state=Completed, session=c790a3a9
[FRAME STATE] Frame 211 state=Completed, session=c790a3a9
```

**If MISSING** → `ProcessServerResponse()` is not being called or crashing.

---

#### C. Are completed frames being displayed?
```bash
findstr "DISPLAY CHECK" segmentation_debug.log
```

**Expected**:
```
[DISPLAY CHECK] Found 3 completed frames ready to display
[DISPLAY CHECK] Found 2 completed frames ready to display
```

**If MISSING** → `TryDisplayNewestFrame()` finds no completed frames.

---

#### D. Is telemetry being sent?
```bash
findstr "UNITY TELEMETRY.*✓" segmentation_debug.log
```

**Expected**:
```
[UNITY TELEMETRY] ✓ Sending telemetry for frame 210, session=c790a3a9, final_state=Displayed, json_length=850
[UNITY TELEMETRY] ✓ Sending telemetry for frame 211, session=c790a3a9, final_state=Displayed, json_length=848
```

**If you see "✗" instead**:
```
[UNITY TELEMETRY] ✗ Frame 210 not final yet (state=Completed)
```

→ Frames are NOT transitioning from `Completed` to `Displayed`.

---

## Possible Issues and Solutions

### Issue 1: ProcessServerResponse() Not Called

**Symptom**: No `[FRAME STATE]` logs

**Causes**:
- Polling coroutine crashed
- JSON parsing failed
- Wrong session ID in response

**Check**:
```bash
findstr "UDP POLL.*Frame.*received" segmentation_debug.log
findstr "UDP RESPONSE.*parsed" segmentation_debug.log
findstr "JSON parse error" segmentation_debug.log
```

---

### Issue 2: Frames Stuck in Completed State

**Symptom**: See `[FRAME STATE]` logs but no `[DISPLAY CHECK]` logs

**Causes**:
- `Update()` not calling `TryDisplayNewestFrame()`
- `m_useServerInference` is false
- Lock deadlock in `m_frameTracesLock`

**Check**:
```bash
# Should see this in Update() every frame
findstr "PHASE 3.*Triggered inference" segmentation_debug.log
```

If missing → Update() is not running UDP mode path.

---

### Issue 3: Update() Not Running Properly

**Symptom**: No inference triggers, no display checks

**Causes**:
- `m_useUDPTransport` is false (still using HTTP mode)
- `m_cameraReady` is false
- Scene paused

**Check scene configuration**:
```csharp
// In Segmentation.unity, should have:
m_useUDPTransport: value: 1  ✅
m_useServerInference: value: 1  ✅
```

---

### Issue 4: DisplayFrame() Never Called

**Symptom**: See `[DISPLAY CHECK]` logs but frames stay `Completed`

**Causes**:
- `DisplayFrame()` crashes before calling `MarkDisplayed()`
- Response parsing fails
- Rendering crashes

**Check**:
```bash
findstr "DISPLAY.*Frame.*Converted" segmentation_debug.log
findstr "Exception" segmentation_debug.log
```

---

## Expected Complete Flow (With Logs)

**Frame 210 lifecycle**:

```
1. [UDP SEND] Frame 210 sent to 192.168.0.135:8002, upload_bytes=18850
   [UNITY TELEMETRY] ✗ Frame 209 not final yet (state=Completed)

2. [UDP POLL] Starting polling for frame 210
   [UDP POLL] Frame 210 received after 0.25s

3. [UDP RESPONSE] Response: {"detections":...
   [FRAME STATE] Frame 210 state=Completed, session=c790a3a9
   [TELEMETRY QUEUE] Frame 210 COMPLETED ➡queued (queue depth: 3)

4. [DISPLAY CHECK] Found 3 completed frames ready to display
   [DISPLAY] Frame 210: Converted 1 detections
   [TELEMETRY QUEUE] Frame 210 DISPLAYED ↦queued

5. [UDP SEND] Frame 211 sent to 192.168.0.135:8002
   [UNITY TELEMETRY] ✓ Sending telemetry for frame 210, final_state=Displayed, json_length=850
```

**Server receives**:
```
[UDP WORKER] Processing c790a3a9_211 (mode=segmentation)  ✅
[UDP EXCEL DEBUG] telemetry keys: ['scene', 'mode', 'frame_id', ...]  ✅
[UDP EXCEL] Frame 211 carries telemetry for frame 210
```

---

## Summary

**The code structure is correct**, but frames are not transitioning through states properly in the actual runtime.

**Next steps**:
1. Rebuild Unity APK with new debug logs
2. Run Segmentation scene
3. Collect Unity logs
4. Search for the specific log patterns above
5. Identify which step in the flow is failing
6. Report findings

The debug logs will pinpoint exactly where the flow breaks.

---

**Last Updated**: 2026-04-17 15:00 UTC
**Status**: 🔍 Debug version ready, waiting for rebuild and logs
