# Telemetry Unified Patch - All Three Modes

**Date**: 2026-04-15
**Applies To**: PoseEstimation, MultiObjectDetection, Segmentation

This patch fixes three critical issues across all three Unity inference modes to ensure consistent, complete telemetry logging.

---

## Priority 1: Dropped Frame Queue (CRITICAL)

### Problem

When multiple frames complete before TryDisplayNewestFrame(), only the newest frame is displayed and older frames are marked Dropped. However, `m_lastCompletedTrace` is overwritten multiple times, causing all dropped frames except the last one to be LOST.

Example:
- Frames 5, 6, 7 complete before next Update()
- Frame 7 displayed, frames 5 and 6 marked Dropped
- m_lastCompletedTrace = 5, then = 6, then = 7
- Only frame 7's telemetry is sent → frames 5 and 6 NEVER logged

### Solution

Replace single `m_lastCompletedTrace` with a queue `m_completedFramesQueue` that stores ALL completed frames (Displayed, Dropped, Failed) in FIFO order.

### Code Changes

#### Step 1: Add Queue Field

```csharp
// Add after m_lastCompletedTrace declaration
private FrameTrace m_lastCompletedTrace = null;  // DEPRECATED - will be removed after migration
private Queue<FrameTrace> m_completedFramesQueue = new Queue<FrameTrace>();  // NEW
```

#### Step 2: Modify TryDisplayNewestFrame()

**OLD CODE** (MultiObjectDetection lines 777-792):
```csharp
for (int i = 1; i < completedFrames.Count; i++)
{
    var olderFrame = completedFrames[i];
    olderFrame.MarkDropped(currentTimestamp, $"superseded_by_newer_{newest.frame_id}");
    m_droppedFrames++;

    // BUG: Overwrites previous dropped frames
    m_lastCompletedTrace = olderFrame;
}

DisplayFrame(newest);
newest.MarkDisplayed(currentTimestamp);
m_lastDisplayedFrameId = newest.frame_id;

// BUG: Overwrites dropped frames
m_lastCompletedTrace = newest;
```

**NEW CODE**:
```csharp
// Mark older frames as dropped and enqueue
for (int i = 1; i < completedFrames.Count; i++)
{
    var olderFrame = completedFrames[i];
    olderFrame.MarkDropped(currentTimestamp, $"superseded_by_newer_{newest.frame_id}");
    m_droppedFrames++;

    // FIXED: Add to queue instead of overwriting
    m_completedFramesQueue.Enqueue(olderFrame);
    Debug.Log($"[TELEMETRY QUEUE] Frame {olderFrame.frame_id} DROPPED → queued (queue depth: {m_completedFramesQueue.Count})");
}

// Display newest frame
DisplayFrame(newest);
newest.MarkDisplayed(currentTimestamp);
m_lastDisplayedFrameId = newest.frame_id;

// FIXED: Add displayed frame to queue
m_completedFramesQueue.Enqueue(newest);
Debug.Log($"[TELEMETRY QUEUE] Frame {newest.frame_id} DISPLAYED → queued (queue depth: {m_completedFramesQueue.Count})");

// LEGACY COMPATIBILITY: Also set m_lastCompletedTrace for now
m_lastCompletedTrace = newest;
```

#### Step 3: Modify SendImage() to Dequeue

**Find the section where delayed headers are sent** (varies per mode, but looks like):

```csharp
// OLD CODE - reads from m_lastCompletedTrace
if (m_lastCompletedTrace != null)
{
    request.SetRequestHeader("X-Prev-Frame-Id", m_lastCompletedTrace.frame_id.ToString());
    request.SetRequestHeader("X-Prev-Unity-Send-Ts", m_lastCompletedTrace.unity_send_ts.ToString());
    // ... more headers from m_lastCompletedTrace
}
```

**NEW CODE - dequeues from m_completedFramesQueue**:
```csharp
// FIXED: Dequeue from queue if available
FrameTrace traceToSend = null;
if (m_completedFramesQueue.Count > 0)
{
    traceToSend = m_completedFramesQueue.Dequeue();
    Debug.Log($"[TELEMETRY QUEUE] Dequeued frame {traceToSend.frame_id} (state={traceToSend.state}) for delayed headers (remaining: {m_completedFramesQueue.Count})");
}
else if (m_lastCompletedTrace != null)
{
    // LEGACY FALLBACK: Use m_lastCompletedTrace if queue is empty
    traceToSend = m_lastCompletedTrace;
    Debug.LogWarning($"[TELEMETRY QUEUE] Queue empty, using legacy m_lastCompletedTrace (frame {traceToSend.frame_id})");
}

if (traceToSend != null)
{
    request.SetRequestHeader("X-Prev-Frame-Id", traceToSend.frame_id.ToString());
    request.SetRequestHeader("X-Prev-Unity-Send-Ts", traceToSend.unity_send_ts.ToString());
    request.SetRequestHeader("X-Prev-Unity-Receive-Ts", traceToSend.unity_receive_ts.ToString());

    // Handle nullable timestamps
    request.SetRequestHeader("X-Prev-Unity-Display-Ts",
        traceToSend.unity_display_ts.HasValue ? traceToSend.unity_display_ts.Value.ToString() : "0");
    request.SetRequestHeader("X-Prev-Unity-Drop-Ts",
        traceToSend.unity_drop_ts.HasValue ? traceToSend.unity_drop_ts.Value.ToString() : "0");

    request.SetRequestHeader("X-Prev-Server-Receive-Ts", traceToSend.server_receive_ts.ToString());
    request.SetRequestHeader("X-Prev-Server-Send-Ts", traceToSend.server_send_ts.ToString());

    request.SetRequestHeader("X-Prev-Final-State", traceToSend.state.ToString());
    request.SetRequestHeader("X-Prev-Drop-Reason", traceToSend.drop_reason ?? "");
    request.SetRequestHeader("X-Prev-Error-Reason", traceToSend.error_reason ?? "");
}
```

#### Step 4: Add Failed Frames to Queue

**Find where frames are marked as Failed** (typically in error handling):

```csharp
// OLD CODE - just marks failed
trace.MarkFailed($"Timeout after {timeoutSeconds}s");

// NEW CODE - marks failed AND enqueues
trace.MarkFailed($"Timeout after {timeoutSeconds}s");
m_completedFramesQueue.Enqueue(trace);
Debug.Log($"[TELEMETRY QUEUE] Frame {trace.frame_id} FAILED → queued (queue depth: {m_completedFramesQueue.Count})");
```

### Expected Behavior After Fix

**Before**:
- Frames 5, 6, 7 complete
- Excel shows: Frame 7 (Displayed)
- Frames 5, 6 lost forever

**After**:
- Frames 5, 6, 7 complete
- Queue: [5:Dropped, 6:Dropped, 7:Displayed]
- Frame N+1's request sends Frame 5's telemetry → logs Frame 5 (Dropped)
- Frame N+2's request sends Frame 6's telemetry → logs Frame 6 (Dropped)
- Frame N+3's request sends Frame 7's telemetry → logs Frame 7 (Displayed)
- Excel shows all three frames with correct states

---

## Priority 2: session_id Initialization (HIGH)

### Problem

FrameTrace has a `session_id` field, but it's never initialized in any of the three modes. This violates the (session_id, frame_id) uniqueness requirement.

### Solution

1. Add `m_sessionId` field to each InferenceRunManager
2. Initialize with GUID in Start()
3. Set on each FrameTrace

### Code Changes

#### Step 1: Add session_id Field

```csharp
// Add near the top of class (after m_frameId declaration)
private int m_frameId = 0;
private string m_sessionId = null;  // NEW - unique session identifier
```

#### Step 2: Initialize in Start()

```csharp
void Start()
{
    // Generate unique session ID (GUID format)
    m_sessionId = System.Guid.NewGuid().ToString();
    Debug.Log($"[SESSION] Started new session: {m_sessionId}");

    // ... rest of Start() code
}
```

#### Step 3: Set on FrameTrace Creation

```csharp
// OLD CODE
FrameTrace trace = new FrameTrace(m_frameId);

// NEW CODE
FrameTrace trace = new FrameTrace(m_frameId);
trace.session_id = m_sessionId;  // FIXED: Set session ID
```

#### Step 4: Add to Delayed Headers

```csharp
// In SendImage() where delayed headers are set
request.SetRequestHeader("X-Prev-Session-Id", traceToSend.session_id);  // NEW
request.SetRequestHeader("X-Prev-Frame-Id", traceToSend.frame_id.ToString());
// ... rest of headers
```

### Expected Behavior After Fix

**Before**:
- session_id = null for all frames
- Cannot distinguish frames across sessions

**After**:
- session_id = "a3f5b8c1-..." (GUID) for all frames in session
- (session_id, frame_id) forms unique key
- Excel can distinguish Frame 1 from Session A vs Frame 1 from Session B

---

## Priority 3: Freeze Frame Calculation Fix (HIGH)

### Problem

Freeze frames are currently calculated as "total Unity frames without new inference result", which is NOT the correct definition.

**Correct definition**: freeze_frames = number of Unity Update() calls BETWEEN displayed inference frames (i.e., how long we were "frozen" showing stale result).

### Solution

1. Add `m_framesSinceLastDisplay` counter
2. Increment in Update() BEFORE TryDisplayNewestFrame()
3. Assign to displayed frame and reset on display

### Code Changes

#### Step 1: Add Counter Field

```csharp
// Add near other counters
private int m_droppedFrames = 0;
private int m_freezeFrames = 0;  // DEPRECATED - cumulative counter (keep for HUD display)
private int m_framesSinceLastDisplay = 0;  // NEW - per-frame freeze count
```

#### Step 2: Modify Update()

```csharp
// OLD CODE
void Update()
{
    // ... other code

    TryDisplayNewestFrame();
    CleanupOldFrames();

    if (no frame displayed this update)
    {
        m_freezeFrames++;  // WRONG: Cumulative, not per-frame
    }
}

// NEW CODE
void Update()
{
    // Increment freeze counter BEFORE trying to display
    // This counts Unity frames since last display
    m_framesSinceLastDisplay++;

    // Try to display newest completed frame
    TryDisplayNewestFrame();

    // Other Update logic
    CleanupOldFrames();
    CheckFrameTimeouts();

    // NOTE: m_freezeFrames is still incremented for HUD display (legacy)
    // but per-frame freeze count is now in m_framesSinceLastDisplay
}
```

#### Step 3: Modify TryDisplayNewestFrame()

```csharp
// Inside TryDisplayNewestFrame(), when displaying newest frame:

// Display newest frame
DisplayFrame(newest);
newest.MarkDisplayed(currentTimestamp);
m_lastDisplayedFrameId = newest.frame_id;

// FIXED: Assign freeze count to this frame (how long we were frozen before this display)
newest.freeze_frames = m_framesSinceLastDisplay - 1;  // -1 because current frame doesn't count as freeze
m_framesSinceLastDisplay = 0;  // Reset counter

Debug.Log($"[FREEZE METRICS] Frame {newest.frame_id} displayed after {newest.freeze_frames} Unity frames");

// Enqueue for telemetry
m_completedFramesQueue.Enqueue(newest);
```

#### Step 4: Add freeze_frames to FrameTrace (if not already there)

```csharp
// In FrameTrace.cs (likely already exists)
public class FrameTrace
{
    // ... existing fields

    public int freeze_frames;  // Number of Unity frames between this display and previous display

    // ... rest of class
}
```

#### Step 5: Add freeze_frames to Delayed Headers

```csharp
// In SendImage() where delayed headers are set
request.SetRequestHeader("X-Prev-Freeze-Frames", traceToSend.freeze_frames.ToString());  // NEW
```

### Expected Behavior After Fix

**Scenario**: Fixed 5 FPS inference, Unity runs at 60 FPS

**Before (WRONG)**:
- Every frame has freeze_frames incremented
- Frame 1: freeze_frames = 1200 (cumulative, meaningless)

**After (CORRECT)**:
- Frame 1 displays at Unity frame 12 → freeze_frames = 12 (was frozen for 12 Unity frames)
- Frame 2 displays at Unity frame 24 → freeze_frames = 12 (was frozen for 12 Unity frames)
- Frame 3 displays at Unity frame 36 → freeze_frames = 12
- etc.

**With Dropped Frames**:
- Frame 1 displays at Unity frame 12 → freeze_frames = 12
- Frame 2 completed but dropped (superseded by frame 3) → freeze_frames = 0 (never displayed, so no freeze)
- Frame 3 displays at Unity frame 36 → freeze_frames = 24 (was frozen for 24 frames, from frame 12 to 36)

---

## Server-Side Changes Required

### frame_state_manager.py

Add support for new fields:

```python
# In FrameState dataclass, add:
@dataclass
class FrameState:
    # ... existing fields

    # NEW: Per-frame freeze count (not cumulative)
    freeze_frames_per_frame: int = 0  # Unity frames between displays
```

```python
# In process_frame(), add to complete_frame_data:
complete_frame_data = {
    # ... existing fields

    # NEW: Per-frame freeze (from delayed headers)
    'freeze_frames_per_frame': current_frame.freeze_frames_per_frame,  # NEW

    # LEGACY: Keep old freeze_frames for compatibility
    'freeze_frames': current_frame.freeze_frames,  # Cumulative
}
```

### Server Endpoints (infer_human.py, segmentation.py, object_detection.py)

Add reading of new header:

```python
# In delayed header reading section:
try:
    prev_freeze_frames = int(request.headers.get("X-Prev-Freeze-Frames", "0"))  # NEW
except (ValueError, TypeError):
    prev_freeze_frames = 0
```

```python
# In frame_manager.process_frame() call:
complete_frame_data = frame_manager.process_frame(
    # ... existing params

    # NEW: Per-frame freeze
    freeze_frames_per_frame=prev_freeze_frames,  # NEW
)
```

### Excel Logger (inference_logger.py)

Add new column:

```python
# In EXCEL_COLUMNS list:
EXCEL_COLUMNS = [
    # ... existing columns

    'freeze_frames',  # Legacy cumulative
    'freeze_frames_per_frame',  # NEW: Per-frame freeze count
]
```

---

## Implementation Checklist

### For Each Mode (PoseEstimation, MultiObjectDetection, Segmentation)

- [ ] **Priority 1: Dropped Frame Queue**
  - [ ] Add `m_completedFramesQueue` field
  - [ ] Modify `TryDisplayNewestFrame()` to enqueue dropped frames
  - [ ] Modify `TryDisplayNewestFrame()` to enqueue displayed frame
  - [ ] Modify error handling to enqueue failed frames
  - [ ] Modify `SendImage()` to dequeue from queue
  - [ ] Add debug logging for queue operations

- [ ] **Priority 2: session_id**
  - [ ] Add `m_sessionId` field
  - [ ] Initialize `m_sessionId` in `Start()`
  - [ ] Set `trace.session_id` on FrameTrace creation
  - [ ] Add `X-Prev-Session-Id` header in `SendImage()`

- [ ] **Priority 3: Freeze Calculation**
  - [ ] Add `m_framesSinceLastDisplay` field
  - [ ] Increment `m_framesSinceLastDisplay` in `Update()` (before TryDisplayNewestFrame)
  - [ ] Assign `newest.freeze_frames` in `TryDisplayNewestFrame()` (before enqueue)
  - [ ] Reset `m_framesSinceLastDisplay` after assigning
  - [ ] Add `freeze_frames` field to FrameTrace (if not exists)
  - [ ] Add `X-Prev-Freeze-Frames` header in `SendImage()`

### Server-Side

- [ ] **frame_state_manager.py**
  - [ ] Add `freeze_frames_per_frame` to FrameState dataclass
  - [ ] Add `freeze_frames_per_frame` to process_frame() params
  - [ ] Add `freeze_frames_per_frame` to complete_frame_data dict

- [ ] **All Endpoints**
  - [ ] Read `X-Prev-Freeze-Frames` header
  - [ ] Pass `freeze_frames_per_frame` to frame_manager.process_frame()

- [ ] **inference_logger.py**
  - [ ] Add `freeze_frames_per_frame` column to Excel

### Testing

- [ ] **Test Dropped Frames**
  - [ ] Set high frame rate (10+ FPS) with slow server response
  - [ ] Verify dropped frames appear in Excel with state=Dropped
  - [ ] Verify drop_reason is populated
  - [ ] Verify unity_drop_ts is populated

- [ ] **Test session_id**
  - [ ] Record two separate sessions
  - [ ] Verify different session_id GUIDs
  - [ ] Verify (session_id, frame_id) uniqueness

- [ ] **Test Freeze Calculation**
  - [ ] Verify freeze_frames_per_frame matches Unity FPS / inference FPS ratio
  - [ ] Example: 60 FPS Unity, 5 FPS inference → freeze_frames_per_frame ≈ 12

---

## Mode-Specific Notes

### PoseEstimation

**File**: `Assets/PassthroughCameraApiSamples/PoseEstimation/Scripts/PoseInferenceRunManager.cs`

**Key Locations**:
- TryDisplayNewestFrame: line ~935
- SendImage delayed headers: line ~409
- FrameTrace creation: line ~295

### MultiObjectDetection

**File**: `Assets/PassthroughCameraApiSamples/MultiObjectDetection/SentisInference/Scripts/SentisInferenceRunManager.cs`

**Key Locations**:
- TryDisplayNewestFrame: line ~753
- SendImage delayed headers: line ~595
- FrameTrace creation: line ~481

### Segmentation

**File**: `Assets/PassthroughCameraApiSamples/Segmentation/SegmentationInference/Scripts/SegmentationInferenceRunManager.cs`

**Key Locations**:
- TryDisplayNewestFrame: line ~826
- SendImage delayed headers: line ~630
- FrameTrace creation: line ~514

**Special Note**: Segmentation has additional `TryDisplayNewestFrame()` call in `RunServerInference()` (line 507). This should be REMOVED after Priority 1 fix is applied, as it was a workaround for the state=Completed issue.

---

## Expected Excel Output After All Fixes

| frame_id | session_id | final_state | unity_send_ts | unity_receive_ts | unity_display_ts | unity_drop_ts | drop_reason | freeze_frames_per_frame |
|----------|-----------|-------------|---------------|------------------|------------------|---------------|-------------|------------------------|
| 0 | a3f5... | Displayed | 1776283439605 | 1776283439650 | 1776283439700 | NULL | | 12 |
| 1 | a3f5... | Displayed | 1776283439805 | 1776283439850 | 1776283439900 | NULL | | 12 |
| 2 | a3f5... | Dropped | 1776283440005 | 1776283440050 | NULL | 1776283440100 | superseded_by_newer_4 | 0 |
| 3 | a3f5... | Dropped | 1776283440105 | 1776283440150 | NULL | 1776283440200 | superseded_by_newer_4 | 0 |
| 4 | a3f5... | Displayed | 1776283440205 | 1776283440250 | 1776283440300 | NULL | | 36 |
| 5 | a3f5... | Failed | 1776283440405 | 0 | NULL | NULL | Timeout after 10s | 0 |

**Key Features**:
- All sent frames appear (including Dropped and Failed)
- session_id is consistent within session
- Displayed frames have unity_display_ts and freeze_frames_per_frame
- Dropped frames have unity_drop_ts, drop_reason, and freeze_frames_per_frame=0
- Failed frames have error_reason
- NO "Completed" or "Pending" states in final Excel

---

**End of Patch Document**
