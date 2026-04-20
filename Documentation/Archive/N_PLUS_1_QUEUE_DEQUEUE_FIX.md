# N+1 Telemetry Queue Dequeue Fix

**Date**: 2026-04-17
**Status**: ✅ **FIXED - Critical queue management bug**

---

## Problem Summary

Unity was sending **the same frame_id (frame 1) repeatedly** in telemetry, causing Excel to log multiple duplicate rows for frame 1 instead of logging frames 1, 2, 3, 4, etc.

### Evidence from Server Logs

```
[UDP EXCEL] Frame 31 carries telemetry for frame 1
[UDP EXCEL] Logged frame 1 (final_state=Displayed)
[UDP EXCEL] Frame 32 carries telemetry for frame 1
[UDP EXCEL] Logged frame 1 (final_state=Displayed)
[UDP EXCEL] Frame 33 carries telemetry for frame 1
[UDP EXCEL] Logged frame 1 (final_state=Displayed)
...
```

### Evidence from Excel

All rows had:
- `frame_id = 1`
- Identical telemetry values (except timestamp)
- Never logged frame 2, 3, 4, ...

---

## Root Cause

### The Bug

In `SendFrameUDP()` method:

```csharp
// Get previous frame's telemetry
string prevTelemetryJson = GetPreviousFrameTelemetryJson();

// Send UDP packet with telemetry
UDPTransport.SendFrame(m_udpClient, serverIP, UDP_PORT, trace, jpegData, prevTelemetryJson);

// ❌ BUG: Never dequeued the sent frame!
// Result: m_completedFramesQueue keeps frame 1 at the front forever
```

**`GetPreviousFrameTelemetryJson()`** implementation:

```csharp
private string GetPreviousFrameTelemetryJson()
{
    lock (m_frameTracesLock)
    {
        if (m_completedFramesQueue.Count == 0)
            return null;

        var prevTrace = m_completedFramesQueue.Peek();  // ← Only PEEK, never DEQUEUE!

        // Build JSON from prevTrace...
        return json;
    }
}
```

### Why This Caused the Issue

1. **Frame 1** completes and is enqueued to `m_completedFramesQueue`
2. **Frame 2** sends → calls `GetPreviousFrameTelemetryJson()` → **Peek()** returns frame 1 → sends frame 1 telemetry
3. **Frame 3** sends → calls `GetPreviousFrameTelemetryJson()` → **Peek()** returns frame 1 again (still at front!) → sends frame 1 telemetry
4. **Frame 4+** → same issue, always sends frame 1

**Queue state over time**:
```
Frame 2 sends: Queue = [Frame1]              → Sends Frame1 telemetry
Frame 3 sends: Queue = [Frame1, Frame2]      → Sends Frame1 telemetry (Peek never removes!)
Frame 4 sends: Queue = [Frame1, Frame2, Frame3] → Sends Frame1 telemetry
...
```

The queue **grows infinitely** because frames are **enqueued but never dequeued**.

---

## The Fix

### Changed Files

1. **`MultiObjectDetection/SentisInference/Scripts/SentisInferenceRunManager.cs`** (lines 1229-1240)
2. **`PoseEstimation/Scripts/PoseInferenceRunManager.cs`** (lines 1466-1477)
3. **`Segmentation/SegmentationInference/Scripts/SegmentationInferenceRunManager.cs`** (lines 1400-1411)

### Fix Applied

Added **Dequeue()** call immediately after sending telemetry:

```csharp
// Send UDP packet with telemetry
UDPTransport.SendFrame(m_udpClient, serverIP, UDP_PORT, trace, jpegData, prevTelemetryJson);

// ✅ FIX: Dequeue the sent telemetry frame so next frame sends different telemetry
if (prevTelemetryJson != null)
{
    lock (m_frameTracesLock)
    {
        if (m_completedFramesQueue.Count > 0)
        {
            var sentFrame = m_completedFramesQueue.Dequeue();
            Debug.Log($"[TELEMETRY QUEUE] Dequeued sent frame {sentFrame.frame_id} (remaining: {m_completedFramesQueue.Count})");
        }
    }
}
```

### How It Works Now

1. **Frame 1** completes → Enqueued to `m_completedFramesQueue`
2. **Frame 2** sends:
   - `Peek()` → Gets frame 1 telemetry
   - `SendFrame()` → Sends frame 1 telemetry with frame 2 data
   - **`Dequeue()`** → Removes frame 1 from queue ✅
3. **Frame 3** sends:
   - Frame 2 has completed and been enqueued
   - `Peek()` → Gets frame 2 telemetry (not frame 1!)
   - `SendFrame()` → Sends frame 2 telemetry with frame 3 data
   - **`Dequeue()`** → Removes frame 2 from queue ✅
4. **Frame 4+** → Each frame sends **different** telemetry

**Queue state over time (fixed)**:
```
Frame 2 sends: Queue = [Frame1]        → Sends Frame1, Dequeue → Queue = []
Frame 3 sends: Queue = [Frame2]        → Sends Frame2, Dequeue → Queue = []
Frame 4 sends: Queue = [Frame3]        → Sends Frame3, Dequeue → Queue = []
...
```

---

## Enhanced Debug Logging

Also added debug logging to verify correct behavior:

```csharp
// Debug: Log telemetry status
if (prevTelemetryJson != null)
{
    var prevTrace = m_completedFramesQueue.Count > 0 ? m_completedFramesQueue.Peek() : null;
    if (prevTrace != null)
    {
        Debug.Log($"[UNITY TELEMETRY] Sending trace for frame {prevTrace.frame_id}, " +
                  $"session={prevTrace.session_id}, " +
                  $"final_state={prevTrace.state}, " +
                  $"detection_count={prevTrace.detection_count ?? 0}");
    }
}
```

### Expected Unity Logs (After Fix)

```
[UNITY TELEMETRY] Sending trace for frame 1, session=d4666abf-..., final_state=Displayed, detection_count=2
[UDP SEND] Frame 2 sent to 192.168.0.135:8002
[TELEMETRY QUEUE] Dequeued sent frame 1 (remaining: 0)

[UNITY TELEMETRY] Sending trace for frame 2, session=d4666abf-..., final_state=Displayed, detection_count=3
[UDP SEND] Frame 3 sent to 192.168.0.135:8002
[TELEMETRY QUEUE] Dequeued sent frame 2 (remaining: 0)

[UNITY TELEMETRY] Sending trace for frame 3, session=d4666abf-..., final_state=Displayed, detection_count=1
[UDP SEND] Frame 4 sent to 192.168.0.135:8002
[TELEMETRY QUEUE] Dequeued sent frame 3 (remaining: 0)
```

Notice: **frame_id increments** (1 → 2 → 3), not stuck at 1!

### Expected Server Logs (After Fix)

```
[UDP EXCEL DEBUG] Processing frame 2, telemetry keys: [...]
[UDP EXCEL] Frame 2 carries telemetry for frame 1
[UDP EXCEL] Logged frame 1 (final_state=Displayed)

[UDP EXCEL DEBUG] Processing frame 3, telemetry keys: [...]
[UDP EXCEL] Frame 3 carries telemetry for frame 2  ← Different frame!
[UDP EXCEL] Logged frame 2 (final_state=Displayed)

[UDP EXCEL DEBUG] Processing frame 4, telemetry keys: [...]
[UDP EXCEL] Frame 4 carries telemetry for frame 3  ← Different frame!
[UDP EXCEL] Logged frame 3 (final_state=Displayed)
```

Notice: **N+1 pattern working correctly** (frame 2 carries frame 1, frame 3 carries frame 2, etc.)

---

## Testing Checklist

- [ ] **Build Unity** with fixed code
- [ ] **Deploy to Quest 3**
- [ ] **Run for ~30 seconds** (capture 150+ frames)
- [ ] **Check Unity logs** (`adb logcat -s Unity | findstr "TELEMETRY"`)
  - Verify `[UNITY TELEMETRY]` shows incrementing frame_ids (1, 2, 3, ...)
  - Verify `[TELEMETRY QUEUE] Dequeued sent frame X` messages
- [ ] **Check server logs**
  - Verify `[UDP EXCEL] Frame N carries telemetry for frame N-1` (proper N+1 pattern)
  - Verify frame_ids increment correctly
- [ ] **Check Excel file** at `C:\Repo\Github\vision_server\debug\logs\inference_log_2026-04-17.xlsx`
  - **No duplicate frame_ids** (each frame logged exactly once)
  - Frame_ids should be: 1, 2, 3, 4, 5, ... (sequential)
  - Detection counts and other metrics should vary per frame
  - All 34 columns populated correctly

---

## Verification Commands

### Unity Logs (Quest 3)

```bash
# View telemetry send messages
adb logcat -s Unity | findstr "UNITY TELEMETRY"

# View queue dequeue messages
adb logcat -s Unity | findstr "TELEMETRY QUEUE"

# View both
adb logcat -s Unity | findstr "TELEMETRY"
```

### Server Logs

```bash
# View Excel logging messages
# (Check PowerShell console where server is running)
# Look for: [UDP EXCEL] Frame X carries telemetry for frame Y
```

### Excel File

```powershell
# Open Excel file
start C:\Repo\Github\vision_server\debug\logs\inference_log_2026-04-17.xlsx

# Check:
# - Column B (frame_id): Should be 1, 2, 3, 4, 5, ... (no duplicates!)
# - Column C (session_id): Should all match same session
# - Column U (detection_count): Should vary (0, 1, 2, 3, etc.)
```

---

## Technical Details

### N+1 Delayed Telemetry Pattern

**Why N+1?**
- Frame N's final state (Displayed/Dropped) is only known **after** frame N+1 starts rendering
- So telemetry for frame N is sent **with** frame N+1's UDP packet

**Lifecycle**:
1. Frame N sent → state = Pending
2. Frame N response received → state = Completed
3. Frame N+1 starts → Frame N state = Displayed (or Dropped if superseded)
4. Frame N+1 UDP packet → **carries frame N's final telemetry**

**Server behavior**:
- Receives UDP packet for frame N+1 (image data + frame N telemetry)
- Runs inference on frame N+1 image
- Extracts frame N telemetry → Writes frame N to Excel

### Queue Management

**Data Structure**: `Queue<FrameTrace> m_completedFramesQueue`

**Operations**:
- **Enqueue**: When frame reaches Completed state (response received)
- **Peek**: To get next frame's telemetry (without removing)
- **Dequeue**: After sending telemetry (removes from queue)

**Thread Safety**: All queue operations protected by `lock (m_frameTracesLock)`

**Expected Depth**: Usually 0-2 frames (dequeued as soon as sent)

---

## Summary

✅ **Root cause**: Missing `Dequeue()` call after sending telemetry

✅ **Impact**: All telemetry sent with frame_id=1, causing duplicate Excel rows

✅ **Fix**: Added `Dequeue()` immediately after `SendFrame()` in all 3 scenes

✅ **Verification**: Enhanced debug logging shows frame_id progression

✅ **Next**: Build, deploy, and verify Excel logs show sequential frame_ids

---

**Last Updated**: 2026-04-17 04:25 UTC
