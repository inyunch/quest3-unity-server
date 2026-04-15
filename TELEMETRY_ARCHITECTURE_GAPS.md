# Telemetry Architecture - Critical Gaps Analysis

**Date**: 2026-04-15
**Status**: CRITICAL ISSUES IDENTIFIED

## Executive Summary

The current telemetry implementation has a **CRITICAL ARCHITECTURAL MISMATCH** between Unity client and Server that prevents dropped frames from being logged. This affects all three modes (PoseEstimation, MultiObjectDetection, Segmentation).

**Root Cause**: Unity can mark multiple frames as Dropped in a single Update cycle, but the delayed telemetry architecture only allows ONE frame's data to be sent per request. Currently, m_lastCompletedTrace is overwritten, causing all dropped frames except the last one to be LOST.

---

## 1. Requirement Alignment Review

### REQUIREMENT: Parallel Architecture with Out-of-Order Completion

**Target Behavior (from requirements)**:
- Fixed FPS send (5-10 FPS)
- Fire-and-forget requests (no waiting)
- Track multiple pending requests in parallel
- Display ONLY the latest completed frame
- If frame 3 finishes before frame 2, and frame 3 is displayed first, then frame 2 must be marked Dropped

**Current Implementation Status**: PARTIALLY IMPLEMENTED

**Gaps**:
1. All three modes DO support parallel requests (fire-and-forget)
2. All three modes DO track pending frames in m_frameTraces dictionary
3. All three modes DO mark older frames as Dropped when newer frame displayed
4. BUT: Dropped frames are NOT being logged to Excel (see Section 2)

---

### REQUIREMENT: Dropped Frame Definition

**Definition (from requirements)**:
> Dropped Frame = a frame whose response was already received by Unity, but which was never displayed because a newer completed frame superseded it before display.

**Current Implementation Status**: CORRECTLY IMPLEMENTED IN CODE, NOT LOGGED

**Evidence**:
All three modes have correct MarkDropped logic:

```csharp
// MultiObjectDetection line 780
olderFrame.MarkDropped(currentTimestamp, $"superseded_by_newer_{newest.frame_id}");
```

But dropped frames never appear in Excel because of the telemetry pipeline bug (see Section 2).

---

### REQUIREMENT: Freeze Frame Definition

**Definition (from requirements)**:
> Freeze Frames = the number of Unity render/update frames during which no newly completed inference result was available to display.

**Current Implementation Status**: NOT IMPLEMENTED CORRECTLY

**Current (WRONG) Implementation**:
All three modes calculate freeze incorrectly:

```csharp
// Example from MultiObjectDetection
m_freezeFrames++; // Incremented every Update() when no new frame displayed
```

This counts ALL Unity frames without new results, which is NOT the same as "freeze duration between displayed inference results".

**What Should Be**:
- freeze_frames should count Unity frames BETWEEN displayed inference frames
- Reset to 0 when a frame is displayed
- Only counts frames where we're "frozen" (showing stale result)

**Gap**: Freeze calculation is fundamentally incorrect across all modes.

---

### REQUIREMENT: Telemetry Identity (session_id + frame_id)

**Definition (from requirements)**:
> frame_id may restart from 1 every new recording session.
> Therefore frame_id is NOT globally unique.
> You must enforce: (session_id, frame_id) as logical unique key.

**Current Implementation Status**: PARTIALLY IMPLEMENTED

**Evidence**:
```csharp
// FrameTrace.cs line 17
public string session_id;  // Field exists

// But in MultiObjectDetection line 481-486:
FrameTrace trace = new FrameTrace(m_frameId);
lock (m_frameTracesLock)
{
    m_frameTraces[m_frameId] = trace;  // BUG: No session_id set!
}
```

**Gap**: session_id field exists in FrameTrace but is NEVER INITIALIZED in any of the three modes!

**Impact**:
- session_id will be null in all traces
- Violates (session_id, frame_id) uniqueness requirement
- Cannot distinguish frames across recording sessions

---

### REQUIREMENT: Timestamp Requirements

**Definition (from requirements)**:
> Unity timestamps are allowed and preferred to use Unix timestamps in milliseconds.
> Server timestamps should also use Unix milliseconds.

**Current Implementation Status**: CORRECTLY IMPLEMENTED

**Evidence**:
```csharp
// FrameTrace.cs uses TimestampUtil.GetUnixTimestampMs()
// All timestamps are long (Unix ms)
// Server timestamps are parsed from response
```

**No gaps here - timestamps are correct.**

---

### REQUIREMENT: Final Log Requirements

**Definition (from requirements)**:
> The main Excel sheet must be a FINAL SUMMARY sheet.
> - one row per sent frame
> - every sent frame must appear (even if Dropped, Failed, or detection_count=0)
>
> Allowed final states: Displayed, Dropped, Failed
> NOT allowed: Completed, Pending

**Current Implementation Status**: VIOLATED

**Evidence from Excel Analysis** (from SEGMENTATION_TELEMETRY_VERIFIED.md):
```
Total Segmentation rows: 94
States: Displayed=100%, Dropped=0%, Failed=0%
```

**Gaps**:
1. NO dropped frames in Excel (should have some if parallel out-of-order occurs)
2. Server side has validation that filters Completed/Pending:
   ```python
   # segmentation.py line 302-307
   if not prev_final_state or prev_final_state in ("Pending", "Completed"):
       print(f"[TELEMETRY WARNING] Frame {prev_frame_id} has non-final state...")
       prev_final_state = "Displayed"
   ```
   This is a defensive fix, but shouldn't be needed if Unity always sends final states.

3. **CRITICAL**: Zero Dropped frames in a parallel architecture is IMPOSSIBLE if:
   - Multiple frames are in-flight
   - Out-of-order completion occurs
   - "Display only newest" policy is enforced

**Conclusion**: Either:
- Dropped frames are not occurring (parallel architecture not actually working), OR
- Dropped frames ARE occurring but not being logged (telemetry pipeline bug)

Based on code review, it's the latter (see Section 2).

---

### REQUIREMENT: A frame with no detections must still be logged

**Definition (from requirements)**:
> - detection_count = 0 is valid
> - no detection does NOT mean no row

**Current Implementation Status**: UNKNOWN (needs testing)

**Gap**: No evidence in current Excel data of frames with detection_count=0 being logged.

---

## 2. CRITICAL BUG: Dropped Frames Not Logged

### The Problem

**Unity Side** (all three modes have this bug):

```csharp
// MultiObjectDetection TryDisplayNewestFrame() lines 777-792
for (int i = 1; i < completedFrames.Count; i++)
{
    var olderFrame = completedFrames[i];
    olderFrame.MarkDropped(currentTimestamp, $"superseded_by_newer_{newest.frame_id}");
    m_droppedFrames++;

    // BUG: This line overwrites m_lastCompletedTrace for EACH dropped frame
    m_lastCompletedTrace = olderFrame;  // ← WRONG!
}

DisplayFrame(newest);
newest.MarkDisplayed(currentTimestamp);
m_lastDisplayedFrameId = newest.frame_id;

// Then immediately overwrites again with the displayed frame
m_lastCompletedTrace = newest;  // ← This loses all the dropped frames!
```

**What Happens**:
1. Suppose frames 5, 6, 7 all complete before next Update()
2. Frame 7 is newest → will be displayed
3. Frames 5 and 6 are marked Dropped
4. Loop sets m_lastCompletedTrace = frame 5, then = frame 6
5. Then line 792 sets m_lastCompletedTrace = frame 7 (displayed)
6. Next request only sends frame 7's telemetry (Displayed)
7. **Frames 5 and 6 are LOST FOREVER** - never logged to Excel!

**Server Side** (frame_state_manager.py):

The server architecture expects ONE frame's telemetry per request:
- Frame N+1's request contains Frame N's final telemetry
- Server logs Frame N when Frame N+1 arrives
- **Can only log ONE frame per request**

This is a fundamental architectural limitation!

### The Fix Options

**Option 1: Queue All Completed Frames (RECOMMENDED)**

Instead of m_lastCompletedTrace (single frame), use a queue:

```csharp
private Queue<FrameTrace> m_completedFramesQueue = new Queue<FrameTrace>();

// In TryDisplayNewestFrame:
for (int i = 1; i < completedFrames.Count; i++)
{
    olderFrame.MarkDropped(...);
    m_completedFramesQueue.Enqueue(olderFrame);  // Add to queue
}
newest.MarkDisplayed(...);
m_completedFramesQueue.Enqueue(newest);  // Add displayed frame too

// In SendImage:
if (m_completedFramesQueue.Count > 0)
{
    FrameTrace trace = m_completedFramesQueue.Dequeue();
    // Send trace's telemetry as delayed headers
}
```

**Pros**:
- All frames get logged (no data loss)
- FIFO order preserved
- Minimal server changes needed

**Cons**:
- Telemetry more delayed (frame N logged in frame N+K request, where K = queue depth)
- Queue can grow if frames complete faster than sent

**Option 2: Batch Telemetry in Single Request**

Send multiple frames' telemetry in one request:

```csharp
// Send array of completed frames
X-Completed-Frames-Count: 3
X-Frame-0-Id: 5
X-Frame-0-State: Dropped
X-Frame-0-Display-Ts: ...
X-Frame-1-Id: 6
X-Frame-1-State: Dropped
...
```

**Pros**:
- No data loss
- Less delay

**Cons**:
- Requires significant server changes (frame_manager must handle multiple frames)
- HTTP header size limits

**Option 3: Separate Telemetry Endpoint**

POST completed frames to /telemetry endpoint immediately:

```csharp
// When MarkDropped or MarkDisplayed called:
StartCoroutine(SendTelemetryAsync(trace));
```

**Pros**:
- No data loss
- Real-time logging

**Cons**:
- Extra HTTP requests
- Decouples telemetry from inference requests
- May not align with delayed architecture

---

## 3. session_id Not Initialized

**All three modes have this bug:**

```csharp
// MultiObjectDetection line 481
FrameTrace trace = new FrameTrace(m_frameId);
// trace.session_id is null!

// Should be:
FrameTrace trace = new FrameTrace(m_frameId);
trace.session_id = m_sessionId;  // Need to add m_sessionId field
```

**Fix Required**:
1. Add `private string m_sessionId;` to each InferenceRunManager
2. Initialize in Start(): `m_sessionId = System.Guid.NewGuid().ToString();`
3. Set on each trace: `trace.session_id = m_sessionId;`

---

## 4. Freeze Frame Calculation Wrong

**Current (WRONG)**:
```csharp
// In Update() - incremented every frame without new result
m_freezeFrames++;
```

This counts ALL Unity frames, not freeze duration.

**Correct Implementation**:
```csharp
private int m_framesSinceLastDisplay = 0;

void Update()
{
    // Before TryDisplayNewestFrame
    m_framesSinceLastDisplay++;

    TryDisplayNewestFrame();
}

void TryDisplayNewestFrame()
{
    if (frame displayed)
    {
        // This is the freeze count for the DISPLAYED frame
        newest.freeze_frames = m_framesSinceLastDisplay;
        m_framesSinceLastDisplay = 0;  // Reset
    }
}
```

**Derived metrics should be**:
- freeze_frames = count of Unity Update() calls between displayed frames
- freeze_duration_ms = time between displayed frames (already captured via unity_display_ts)
- freeze_ratio = freeze_frames / (target_fps * freeze_duration_ms / 1000)

---

## 5. Mode-Specific Issues

### 5.1 PoseEstimation

**File**: `Assets/PassthroughCameraApiSamples/PoseEstimation/Scripts/PoseInferenceRunManager.cs`

Issues found:
1. Same m_lastCompletedTrace overwrite bug (line ~974)
2. No session_id initialization
3. Freeze calculation wrong

**Code Pattern**: Uses timer-driven (Update() checks timer → SendImage)

### 5.2 MultiObjectDetection

**File**: `Assets/PassthroughCameraApiSamples/MultiObjectDetection/SentisInference/Scripts/SentisInferenceRunManager.cs`

Issues found:
1. Same m_lastCompletedTrace overwrite bug (line 780-792)
2. No session_id initialization
3. Freeze calculation wrong

**Code Pattern**: Uses timer-driven (Update() checks timer → SendImage)

### 5.3 Segmentation

**File**: `Assets/PassthroughCameraApiSamples/Segmentation/SegmentationInference/Scripts/SegmentationInferenceRunManager.cs`

Issues found:
1. Same m_lastCompletedTrace overwrite bug (line ~854)
2. No session_id initialization
3. Freeze calculation wrong
4. Additional bug: TryDisplayNewestFrame called in RunServerInference (line 507) before sending
   - This was added as a fix for state=Completed issue
   - But it's a workaround, not root cause fix

**Code Pattern**: Uses event-driven (camera callback → RunServerInference)

---

## 6. Server-Side Issues

### 6.1 frame_state_manager.py

**Limitation**: Can only log ONE frame per request

This is a fundamental architectural constraint that conflicts with Unity's ability to mark multiple frames as Dropped in a single cycle.

**No bugs found** - server code is correct given the single-frame-per-request architecture.

### 6.2 Server Endpoints (infer_human.py, segmentation.py, object_detection.py)

All three endpoints have correct delayed header reading and frame_manager.process_frame() calls.

**Gaps**:
- No support for batch telemetry (multiple frames per request)
- Defensive validation converts non-final states to final states (should not be needed)

---

## 7. Summary of Gaps

| Requirement | Status | Gap | Severity |
|-------------|--------|-----|----------|
| Parallel architecture | Partial | Dropped frames not logged | CRITICAL |
| Dropped frame definition | Code OK, Not logged | m_lastCompletedTrace overwrite bug | CRITICAL |
| Freeze frame definition | Wrong | Incorrect calculation | HIGH |
| session_id uniqueness | Not implemented | session_id never initialized | HIGH |
| Timestamp requirements | OK | None | - |
| Final log completeness | Violated | Missing dropped frames | CRITICAL |
| detection_count=0 logging | Unknown | Needs testing | MEDIUM |
| Final states only in Excel | Violated (workaround exists) | Unity sends Completed sometimes | MEDIUM |

---

## 8. Recommended Fix Priority

### Priority 1: CRITICAL - Dropped Frame Logging

**Fix**: Implement Queue<FrameTrace> m_completedFramesQueue (Option 1 from Section 2)

**Impact**: All frames will be logged, no data loss

**Affects**: All three modes

### Priority 2: HIGH - session_id Initialization

**Fix**: Add m_sessionId field and initialize on Start()

**Impact**: Enables (session_id, frame_id) uniqueness

**Affects**: All three modes

### Priority 3: HIGH - Freeze Frame Calculation

**Fix**: Implement m_framesSinceLastDisplay counter (Section 4)

**Impact**: Correct freeze metrics

**Affects**: All three modes

### Priority 4: MEDIUM - Remove TryDisplayNewestFrame from RunServerInference

**Fix**: Remove workaround in Segmentation mode (line 507)

**Impact**: Cleaner architecture (only needed after Priority 1 fix)

**Affects**: Segmentation only

---

## 9. Next Steps

1. Create patch for Priority 1 (Dropped frame queue) - apply to all three modes
2. Create patch for Priority 2 (session_id) - apply to all three modes
3. Create patch for Priority 3 (freeze calculation) - apply to all three modes
4. Test with high frame rate to verify dropped frames appear in Excel
5. Verify (session_id, frame_id) uniqueness across sessions
6. Verify freeze metrics are correct

---

**End of Analysis**
