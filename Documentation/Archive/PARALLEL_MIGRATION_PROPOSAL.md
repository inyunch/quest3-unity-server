# Parallel Processing Migration Proposal
## Technical Migration Plan for Unity + Python Server Inference System

**Date**: 2026-04-14
**Current Architecture**: Serial request-response with delayed logging
**Target Architecture**: Parallel fire-and-forget with per-frame drop tracking

---

## 1. Current Architecture Assessment

### 1.1 Unity Request Flow

**Entry Point**: `PoseInferenceRunManager.cs` (similar pattern in 3 other managers)

**Current Serial Flow**:
```
Start() → while(true) → RunInference() → RunServerInference()
   ↓
Check FPS throttling (lines 118-132)
   ↓ if too soon → yield break (dropped)
Check m_inferenceInProgress flag (lines 135-143)
   ↓ if true → yield break (frozen)
Set m_inferenceInProgress = true (line 146)
   ↓
Encode image + send POST request (lines 269-384)
   ↓ yield return request.SendWebRequest()
Wait for response synchronously
   ↓
Parse JSON response (lines 399-468)
   ↓
Display skeletons immediately (line 536)
   ↓
Set m_inferenceInProgress = false (line 167)
```

**Key Serial Assumptions**:
1. **One request in flight**: `m_inferenceInProgress` boolean enforces this
2. **Blocking wait**: `yield return request.SendWebRequest()` blocks coroutine
3. **Immediate display**: Response is displayed immediately upon receipt
4. **Frame N-1 timing pattern**: Frame N sends timing data from Frame N-1 in headers (lines 355-376)
5. **Response order == send order**: No handling for out-of-order responses

### 1.2 Server Request Flow

**Entry Point**: `app/routes/infer_human.py`

**Current Flow**:
```python
@router.post("/infer_human")
async def infer_human(request, image):
    t_recv = time.time()                    # Line 116
    ↓
    contents = await image.read()           # Line 120 (only async operation)
    ↓
    pil_image = Image.open(...)             # Line 130
    t_process_start = time.time()           # Line 148
    ↓
    results = run_all_models_with_yolo()    # Line 166 (BLOCKING, synchronous)
    ↓
    t_process_end = time.time()             # Line 698
    ↓
    frame_manager.process_frame()           # Line 819 (delayed logging)
    ↓
    return response
```

**Key Serial Assumptions**:
1. **Single worker**: Default uvicorn with workers=1
2. **Synchronous inference**: `run_all_models_with_yolo()` blocks
3. **Python GIL**: Only one request processes at a time
4. **FrameStateManager expects order**: Stores single previous_frame per scene (line 65 in `frame_state_manager.py`)
5. **Sequential frame IDs**: Logging assumes frame_id increases monotonically

### 1.3 Lock/State Variables Enforcing Single-Flight

**Unity Side**:
| Variable | File | Line | Purpose | Type |
|----------|------|------|---------|------|
| `m_inferenceInProgress` | PoseInferenceRunManager.cs | 56 | Prevents concurrent requests | bool |
| `m_lastInferenceTime` | PoseInferenceRunManager.cs | 55 | FPS throttling | float |
| `m_frameId` | PoseInferenceRunManager.cs | 42 | Sequential counter | int |

**Server Side**:
| Variable | File | Line | Purpose | Type |
|----------|------|------|---------|------|
| `_previous_frames` | frame_state_manager.py | 65 | Stores ONE previous frame per scene | Dict[str, FrameState] |
| `_lock` | frame_state_manager.py | 66 | Thread safety (currently minimal contention) | threading.Lock |

### 1.4 Display Path and Frame Ownership

**Current Display Logic**:
```csharp
// Line 536 in PoseInferenceRunManager.cs
m_uiPose.DrawPoseSkeletons(response.skeleton.persons.ToArray(), ...)
```

**Characteristics**:
- **Immediate**: Display happens in same coroutine that received response
- **No queue**: No buffer of completed frames
- **No drop decision**: Every received frame is displayed
- **Synchronous update**: DrawPoseSkeletons() updates GameObjects directly
- **No frame state tracking**: No concept of "pending", "displayed", or "dropped"

### 1.5 Logging/Metrics Structure

**Excel Columns** (`debug/inference_logger.py` line 14):
```python
COLUMNS = [
    "timestamp", "scene", "frame_id", "latency_ms",
    "server_proc_ms", "upload_ms", "download_ms", "parse_ms",
    "upload_bytes_uncompressed", "upload_bytes_compressed",
    "download_bytes_uncompressed", "download_bytes_compressed",
    "server_pct", "upload_pct", "download_pct",
    "detection_count", "avg_confidence", "keypoint_avg_conf",
    "image_width", "image_height", "model_used",
    "target_fps", "dropped_frames", "freeze_frames", "freeze_ratio",
    "new_dropped", "new_frozen"
]
```

**Current Metrics** (`SharedInferenceHUD.cs` lines 42-46):
```csharp
private int m_totalFrames = 0;
private int m_droppedFrames = 0;  // Currently: FPS throttling drops
private int m_frozenFrames = 0;   // Currently: m_inferenceInProgress blocks
```

**Frame N-1 Pattern**:
- Unity sends Frame N's timing data with Frame N+1 request (lines 355-376)
- Server logs Frame N-1 when Frame N arrives (`frame_state_manager.py` lines 139-219)
- **Problem for parallel**: Assumes sequential arrival

### 1.6 Frame ID Mechanism

**Current**:
```csharp
// Line 272 in PoseInferenceRunManager.cs
m_frameId++;
```

**Properties**:
- Sequential integer counter
- Incremented before each request
- Sent in HTTP header `X-Frame-Id` (line 357)
- Used for logging correlation
- **Good**: Already exists and is globally unique
- **Limitation**: No timestamp, no state tracking

---

## 2. Compatibility Review

### 2.1 Conflicts with Parallel Architecture

#### **CONFLICT 1: m_inferenceInProgress Lock**
**Location**: `PoseInferenceRunManager.cs:56, 135-146, 167`

**Current Behavior**:
```csharp
if (m_inferenceInProgress) {
    m_frozenFrames++;
    yield break;
}
m_inferenceInProgress = true;
// ... send request, wait for response ...
m_inferenceInProgress = false;
```

**Conflict**:
- Prevents multiple concurrent requests
- Must be removed for parallel sending

**Risk**: **HIGH**
- Removing breaks freeze frame tracking
- Affects 4 inference managers (Pose, Sentis, Segmentation, Depth)

**What breaks if unchanged**:
- Only one request in flight at a time
- Parallel architecture impossible

---

#### **CONFLICT 2: Immediate Display Assumption**
**Location**: `PoseInferenceRunManager.cs:536`

**Current Behavior**:
```csharp
// Inside RunServerInference() coroutine
yield return request.SendWebRequest();
// Immediately display:
m_uiPose.DrawPoseSkeletons(response.skeleton.persons.ToArray(), ...)
```

**Conflict**:
- Display happens synchronously after response received
- No concept of "completed but not yet displayed"
- No queue to choose "newest completed frame"

**Risk**: **HIGH**
- Must decouple response receipt from display
- Display logic currently tightly coupled to request coroutine

**What breaks if unchanged**:
- Cannot implement "display only newest" logic
- Older frames will still be displayed

---

#### **CONFLICT 3: FrameStateManager Assumes Sequential Arrival**
**Location**: `debug/frame_state_manager.py:62-66, 139-143`

**Current Behavior**:
```python
self._previous_frames: Dict[str, FrameState] = {}  # ONE per scene

previous_frame = self._previous_frames.get(scene)
self._previous_frames[scene] = current_frame  # Overwrites!
```

**Conflict**:
- Stores only ONE previous frame per scene
- Assumes Frame N+1 arrives after Frame N completes
- Out-of-order arrival will corrupt frame_id mapping

**Risk**: **MEDIUM**
- Logging will break with parallel processing
- Frame N-1 timing pattern no longer valid

**What breaks if unchanged**:
- Excel logging will have incorrect frame associations
- `new_dropped` / `new_frozen` calculations invalid

---

#### **CONFLICT 4: Frame N-1 Timing Headers**
**Location**: `PoseInferenceRunManager.cs:355-376`

**Current Behavior**:
```csharp
// Frame N sends Frame N-1's timing
request.SetRequestHeader("X-E2E-Ms", m_lastE2eMs.ToString());
request.SetRequestHeader("X-Upload-Ms", m_lastUploadMs.ToString());
// ...
```

**Conflict**:
- Assumes frames complete in order
- `m_lastE2eMs` is overwritten by each response
- Parallel responses will race on these variables

**Risk**: **MEDIUM**
- Timing data will be corrupted in parallel mode
- Need per-frame timing storage, not shared variables

**What breaks if unchanged**:
- Excel logs will have garbage timing values
- Cannot correlate timing with correct frames

---

#### **CONFLICT 5: SharedInferenceHUD Metrics**
**Location**: `SharedInferenceHUD.cs:122-134`

**Current Behavior**:
```csharp
public void ReportDroppedFrame() {
    m_droppedFrames++;  // Called on FPS throttle
}
public void ReportFrozenFrame() {
    m_frozenFrames++;   // Called on m_inferenceInProgress
}
```

**Conflict**:
- `ReportDroppedFrame()` currently means "FPS throttled"
- New definition: "received but not displayed"
- Incompatible semantics

**Risk**: **LOW**
- Can be adapted
- Method names misleading but fixable

**What breaks if unchanged**:
- Metrics have wrong meaning
- Excel logs will be confusing

---

#### **CONFLICT 6: Synchronous Display Updates**
**Location**: `PoseSkeletonUiManager.cs:83`

**Current Behavior**:
```csharp
public void DrawPoseSkeletons(PersonSkeleton[] people, Pose cameraPose, ...) {
    // Immediately updates GameObjects
    // Creates/destroys joint/bone renderers
}
```

**Conflict**:
- Called from coroutine context, not Update()
- Direct GameObject manipulation
- No buffering for "display newest only" logic

**Risk**: **MEDIUM**
- Display must happen in Update() loop for parallel
- Need frame buffer to choose newest

**What breaks if unchanged**:
- Multiple responses could trigger concurrent display updates
- Race conditions on GameObject pools

---

### 2.2 Compatibility Summary Table

| Component | Risk | Impact | Can Adapt? | Removal Safe? |
|-----------|------|--------|------------|---------------|
| `m_inferenceInProgress` lock | HIGH | Blocks parallel | No | Must remove |
| Immediate display logic | HIGH | No drop decision | No | Must replace |
| FrameStateManager single prev | MEDIUM | Logging breaks | Yes | Must extend |
| Frame N-1 headers | MEDIUM | Timing corruption | Yes | Must replace |
| HUD metrics methods | LOW | Wrong semantics | Yes | Can redefine |
| Display Update() coupling | MEDIUM | Race conditions | Yes | Must decouple |

---

## 3. Proposed Migration Design

### 3.1 Core Architecture Changes

**Unity Side**:
```
Old:
SendRequest() → WaitForResponse() → DisplayImmediately()

New:
SendRequestAsync() → StoreInCompletedQueue()
Update() → FindNewestCompleted() → Display() → MarkOthersDropped()
```

**Server Side**:
```
Old:
Single worker → Process serially → Return

New:
Multiple workers (4) → Process in parallel → Return (may be out-of-order)
```

### 3.2 Frame ID and Timestamps

**Reuse existing `m_frameId`** - already incremented and sent in headers.

**New per-frame timestamp structure** (Unity-side):
```csharp
public class FrameTrace {
    public int frame_id;

    // Unity timestamps
    public float unity_capture_ts;      // Time.realtimeSinceStartup when texture captured
    public float unity_send_ts;         // Time.realtimeSinceStartup when request sent
    public float unity_receive_ts;      // Time.realtimeSinceStartup when response received
    public float unity_display_ts;      // Time.realtimeSinceStartup when displayed (or 0)
    public float unity_drop_ts;         // Time.realtimeSinceStartup when dropped (or 0)

    // Server timestamps (from response)
    public double server_receive_ts;    // From response.t_server_recv
    public double server_send_ts;       // From response.t_server_send

    // Derived timing (calculated from above)
    public float e2e_ms;                // unity_receive_ts - unity_send_ts
    public float server_proc_ms;        // From response.processing_time_ms
    public float upload_ms;             // Estimated
    public float download_ms;           // Estimated
    public float parse_ms;              // Measured in Unity

    // State
    public FrameState state;            // Pending, Completed, Displayed, Dropped, Failed
    public string drop_reason;          // "superseded_by_newer", "timeout", etc.
    public string error_reason;         // If Failed

    // Results
    public PoseServerResponse response; // Cached response
}

public enum FrameState {
    Pending,      // Request sent, waiting for response
    Completed,    // Response received, not yet displayed
    Displayed,    // Successfully displayed
    Dropped,      // Received but never displayed
    Failed        // Network or parse error
}
```

### 3.3 Final Frame State Determination

**State Transitions**:
```
Pending → Completed  (response received)
Completed → Displayed (chosen for display in Update())
Completed → Dropped  (superseded by newer frame)
Pending → Failed     (network error or timeout)
```

**Drop Logic**:
```csharp
void Update() {
    // Get all completed frames
    var completed = m_frameTraces.Where(f => f.state == FrameState.Completed)
                                  .OrderByDescending(f => f.frame_id)
                                  .ToList();

    if (completed.Count == 0) return;

    // Display newest
    FrameTrace newest = completed[0];
    Display(newest);
    newest.state = FrameState.Displayed;
    newest.unity_display_ts = Time.realtimeSinceStartup;

    // Drop others
    foreach (var frame in completed.Skip(1)) {
        frame.state = FrameState.Dropped;
        frame.drop_reason = $"superseded_by_newer_{newest.frame_id}";
        frame.unity_drop_ts = Time.realtimeSinceStartup;
    }
}
```

### 3.4 Display Decision Logic

**Replace**:
```csharp
// OLD (line 536):
m_uiPose.DrawPoseSkeletons(response.skeleton.persons.ToArray(), ...)
```

**With**:
```csharp
// NEW:
void Update() {
    TryDisplayNewestFrame();
}

void TryDisplayNewestFrame() {
    lock (m_frameTracesLock) {
        var newestCompleted = m_frameTraces.Values
            .Where(f => f.state == FrameState.Completed)
            .OrderByDescending(f => f.frame_id)
            .FirstOrDefault();

        if (newestCompleted == null) return;
        if (newestCompleted.frame_id <= m_lastDisplayedFrameId) return;

        // Display this frame
        m_uiPose.DrawPoseSkeletons(newestCompleted.response.skeleton.persons.ToArray(), ...);
        newestCompleted.state = FrameState.Displayed;
        newestCompleted.unity_display_ts = Time.realtimeSinceStartup;
        m_lastDisplayedFrameId = newestCompleted.frame_id;

        // Mark superseded frames as dropped
        var superseded = m_frameTraces.Values
            .Where(f => f.state == FrameState.Completed && f.frame_id < newestCompleted.frame_id)
            .ToList();

        foreach (var frame in superseded) {
            frame.state = FrameState.Dropped;
            frame.drop_reason = "superseded";
            frame.unity_drop_ts = Time.realtimeSinceStartup;
            m_droppedFrames++;
        }
    }
}
```

### 3.5 Older Frame Dropping

**Automatic on each Update()** - see above logic.

**Dropped frames**:
- State = Completed
- frame_id < newest frame_id
- Marked as Dropped before newest is Displayed

### 3.6 Data Structures

**Unity**:
```csharp
// Replace single-request tracking with multi-request tracking
private Dictionary<int, FrameTrace> m_frameTraces = new();  // frame_id -> FrameTrace
private Dictionary<int, UnityWebRequest> m_pendingRequests = new();  // frame_id -> request
private object m_frameTracesLock = new object();
private int m_lastDisplayedFrameId = -1;

// Remove:
// private bool m_inferenceInProgress;  ← DELETE
// private float m_lastE2eMs, m_lastUploadMs, ...  ← DELETE (now in FrameTrace)
```

**Server**:
```python
# Option A: Simple multi-worker (recommended for Phase 1)
uvicorn.run("app.main:app", workers=4)

# Option B: Task queue (future Phase)
# from celery import Celery
# app = Celery('vision_server', broker='redis://...')
```

---

## 4. Incremental Migration Plan

### Phase 0: Inspection and Baseline (COMPLETE)
**Goal**: Document current architecture
**Status**: ✅ Done
**Deliverable**: This proposal document

---

### Phase 1: Add Frame Trace Structure (Unity Only)
**Goal**: Add per-frame tracking without changing request flow
**Risk**: **LOW** - Additive only

**Changes**:
1. Create `FrameTrace` class
2. Add `Dictionary<int, FrameTrace> m_frameTraces`
3. Populate trace on request send (capture_ts, send_ts)
4. Populate trace on response receive (receive_ts, response)
5. Keep existing immediate display
6. Add logging of trace to Debug.Log()

**Files**:
- `PoseInferenceRunManager.cs`
- New: `FrameTrace.cs`

**Validation**:
- Console logs show frame traces
- Existing behavior unchanged
- Timing values match current implementation

**Rollback**: Remove FrameTrace, revert files

---

### Phase 2: Parallel Request Sending (Unity)
**Goal**: Remove `m_inferenceInProgress` lock, allow concurrent requests
**Risk**: **MEDIUM** - Changes control flow

**Changes**:
1. Remove `m_inferenceInProgress` checks (lines 135-143, 146, 167)
2. Change `RunServerInference()` to not block main loop
3. Store `UnityWebRequest` in `m_pendingRequests` dictionary
4. Let multiple coroutines run concurrently
5. Keep immediate display for now

**Files**:
- `PoseInferenceRunManager.cs` (and 3 others)

**Validation**:
- Multiple requests in flight simultaneously
- Responses may arrive out of order
- Display still happens (may show old frames briefly)
- No crashes or deadlocks

**Rollback**: Re-add `m_inferenceInProgress` checks

---

### Phase 3: Deferred Display with Update() Loop
**Goal**: Decouple response receipt from display
**Risk**: **HIGH** - Major behavior change

**Changes**:
1. Remove immediate `DrawPoseSkeletons()` call from `RunServerInference()`
2. Add `Update()` method that calls `TryDisplayNewestFrame()`
3. Implement newest-frame selection logic
4. Transition frames from Completed → Displayed or Dropped
5. Update HUD metrics

**Files**:
- `PoseInferenceRunManager.cs`
- `SharedInferenceHUD.cs`

**Validation**:
- Only newest frame is displayed
- Older frames marked as Dropped
- `m_droppedFrames` count increases correctly
- Visual display smooth (no flickering)

**Rollback**: Restore immediate display call

---

### Phase 4: Server Multi-Worker
**Goal**: Enable parallel server processing
**Risk**: **LOW** - Server-side only, no Unity changes

**Changes**:
1. Update `app/main.py`: `uvicorn.run(..., workers=4)`
2. Test with Unity sending parallel requests
3. Verify responses can arrive out-of-order

**Files**:
- `app/main.py`

**Validation**:
- Server handles 2-4 concurrent requests
- Inference throughput increases
- Out-of-order responses handled by Unity (Phase 3)
- No server crashes or deadlocks

**Rollback**: Set workers=1

---

### Phase 5: Update Logging Schema
**Goal**: Add new Excel columns for parallel tracking
**Risk**: **LOW** - Logging only

**Changes**:
1. Add columns: `unity_send_ts`, `unity_receive_ts`, `unity_display_ts`, `unity_drop_ts`, `final_state`, `drop_reason`
2. Remove columns: `freeze_frames`, `freeze_ratio`, `new_frozen` (obsolete)
3. Keep `dropped_frames`, redefine as "received but not displayed"
4. Update `FrameStateManager` or replace with new per-frame logger

**Files**:
- `debug/inference_logger.py`
- `debug/frame_state_manager.py` (may need major changes or replacement)
- `app/routes/infer_human.py`

**Validation**:
- Excel logs have new columns
- Timing values correct
- `dropped_frames` counts match Unity counts
- Logs can reconstruct full frame lifecycle

**Rollback**: Revert column schema, keep old logger

---

### Phase 6: Cleanup and Optimization
**Goal**: Remove obsolete code, optimize performance
**Risk**: **LOW** - Cleanup only

**Changes**:
1. Remove `m_frozenFrames` tracking (obsolete)
2. Remove Frame N-1 header pattern (replace with per-frame headers)
3. Add frame timeout logic (mark Pending → Failed after 5 seconds)
4. Limit `m_frameTraces` size (remove old Displayed/Dropped/Failed frames)
5. Add performance metrics (pending count, drop rate)

**Files**:
- All inference managers
- `SharedInferenceHUD.cs`
- `FrameTrace.cs`

**Validation**:
- No memory leaks
- Dictionary sizes stay bounded
- Old frames cleaned up
- Performance stable over long runs

**Rollback**: Keep old code (mostly harmless)

---

## 5. Data Model Proposal

### 5.1 Per-Frame Trace Model

```csharp
public class FrameTrace {
    // === Identity ===
    public int frame_id;                  // REQUIRED - unique identifier

    // === Unity Timestamps (Time.realtimeSinceStartup) ===
    public float unity_capture_ts;        // REQUIRED - when texture was captured
    public float unity_send_ts;           // REQUIRED - when HTTP request sent
    public float unity_receive_ts;        // OPTIONAL - when response received (0 if pending/failed)
    public float unity_display_ts;        // OPTIONAL - when displayed (0 if not displayed)
    public float unity_drop_ts;           // OPTIONAL - when dropped (0 if not dropped)

    // === Server Timestamps (from response, Unix epoch) ===
    public double server_receive_ts;      // OPTIONAL - from response.t_server_recv
    public double server_send_ts;         // OPTIONAL - from response.t_server_send

    // === Derived Timing (calculated) ===
    public float e2e_ms;                  // REQUIRED - unity_receive_ts - unity_send_ts
    public float server_proc_ms;          // OPTIONAL - from response.processing_time_ms
    public float upload_ms;               // OPTIONAL - estimated
    public float download_ms;             // OPTIONAL - estimated
    public float parse_ms;                // OPTIONAL - measured in Unity

    // === State Tracking ===
    public FrameState state;              // REQUIRED - current lifecycle state
    public string drop_reason;            // OPTIONAL - why dropped (if Dropped)
    public string error_reason;           // OPTIONAL - error message (if Failed)

    // === Results ===
    public PoseServerResponse response;   // OPTIONAL - cached response (null if pending/failed)
    public int detection_count;           // OPTIONAL - from response
    public float avg_confidence;          // OPTIONAL - from response
}
```

### 5.2 Required vs Optional Fields

**Phase 1 (Minimal)**:
- `frame_id` ✅
- `unity_send_ts` ✅
- `unity_receive_ts` ✅
- `state` ✅
- `e2e_ms` ✅

**Phase 3 (Display tracking)**:
- `unity_display_ts` ✅
- `unity_drop_ts` ✅
- `drop_reason` ✅

**Phase 5 (Full logging)**:
- All fields ✅

### 5.3 State Enum

```csharp
public enum FrameState {
    Pending,      // Request sent, no response yet
    Completed,    // Response received, waiting to display
    Displayed,    // Successfully shown to user
    Dropped,      // Received but superseded before display
    Failed        // Network error, timeout, or parse failure
}
```

---

## 6. API and Logging Proposal

### 6.1 Request Payload Changes

**Current** (Line 357):
```csharp
request.SetRequestHeader("X-Frame-Id", m_frameId.ToString());
request.SetRequestHeader("X-E2E-Ms", m_lastE2eMs.ToString());  // Frame N-1 data
// ...
```

**Proposed**:
```csharp
request.SetRequestHeader("X-Frame-Id", frame.frame_id.ToString());
request.SetRequestHeader("X-Unity-Send-Ts", frame.unity_send_ts.ToString("F6"));
// Remove: X-E2E-Ms, X-Upload-Ms, etc. (will calculate per-frame instead)
```

**Compatibility**: Keep old headers for backward compatibility during transition.

### 6.2 Response Payload

**No changes needed** - current response already has:
```python
{
    "processing_time_ms": float,
    "t_server_recv": double,
    "t_server_send": double,
    ...
}
```

### 6.3 Logging Format

**New Excel Columns**:
```python
COLUMNS = [
    # Identity
    "timestamp", "scene", "frame_id",

    # Unity Timestamps
    "unity_capture_ts", "unity_send_ts", "unity_receive_ts",
    "unity_display_ts", "unity_drop_ts",

    # Server Timestamps
    "server_receive_ts", "server_send_ts",

    # Timing
    "e2e_ms", "server_proc_ms", "upload_ms", "download_ms", "parse_ms",

    # Percentages
    "server_pct", "upload_pct", "download_pct",

    # Results
    "detection_count", "avg_confidence", "keypoint_avg_conf",

    # Image
    "image_width", "image_height",
    "upload_bytes_uncompressed", "upload_bytes_compressed",
    "download_bytes_uncompressed", "download_bytes_compressed",

    # State
    "final_state", "drop_reason", "error_reason",

    # Legacy (keep for comparison)
    "target_fps", "dropped_frames_cumulative"
]
```

**Remove**:
- `freeze_frames` (obsolete concept)
- `freeze_ratio` (obsolete)
- `new_frozen` (obsolete)

**Redefine**:
- `dropped_frames` → cumulative count of frames in Dropped state

### 6.4 Log Correlation

**Unity-side log** (Debug.Log):
```
[FRAME 42] State=Pending send_ts=123.456
[FRAME 42] State=Completed receive_ts=123.789 e2e=333ms
[FRAME 42] State=Displayed display_ts=123.850
```

**Server-side log**:
```
[API] Frame 42 received at t=1713123456.789
[API] Frame 42 completed in 45ms
```

**Excel correlation**:
- Match by `frame_id`
- Unity timestamps in `Time.realtimeSinceStartup` space
- Server timestamps in Unix epoch
- Can compute offset: `server_receive_ts - unity_send_ts - network_latency`

---

## 7. Concurrency and Ordering Risks

### 7.1 Out-of-Order Completion

**Risk**: Frame 3 completes before Frame 2

**Mitigation**:
- Update() loop always picks highest frame_id
- Frame 2 will be marked Dropped when Frame 3 is Displayed
- No visual glitch (user sees newest data)

**Example**:
```
Frame 1: send=0ms,    complete=250ms, display=260ms ✅
Frame 2: send=200ms,  complete=500ms, display=NEVER (dropped) ❌
Frame 3: send=400ms,  complete=480ms, display=490ms ✅
```

### 7.2 Stale Result Risks

**Risk**: Old frame displayed after newer one

**Mitigation**:
- `m_lastDisplayedFrameId` tracking
- Skip display if `frame_id <= m_lastDisplayedFrameId`

**Example** (bug):
```
Update 1: Display Frame 3 (newest)
Update 2: Frame 2 arrives late → Skip (frame_id=2 < m_lastDisplayedFrameId=3)
```

### 7.3 Race Conditions Around Display/Update

**Risk**: Multiple Update() calls, concurrent coroutines

**Mitigation**:
- `lock (m_frameTracesLock)` around dictionary access
- Update() runs on main thread (Unity single-threaded)
- Coroutines complete on main thread
- Dictionary mutations must be locked

**Critical Sections**:
```csharp
lock (m_frameTracesLock) {
    // Add to dictionary
    // Change state
    // Read for display decision
}
```

### 7.4 Thread-Safety / Coroutine-Safety

**Unity**:
- Main thread only (no Worker threads)
- Coroutines are cooperative multitasking (not preemptive)
- Lock only needed if accessing shared data from UnityWebRequest callback

**Recommendation**:
- Use `lock (m_frameTracesLock)` defensively
- All dictionary access must be locked
- GameObject updates (DrawPoseSkeletons) only from Update()

### 7.5 Server-Side Worker Safety

**Risk**: Workers sharing GPU/model state

**Analysis**:
- PyTorch models in shared GPU memory
- Models are thread-safe for inference (read-only after load)
- Each worker process has own Python interpreter (separate GIL)
- GPU handles concurrent inference internally

**Recommendation**:
- Start with `workers=4`
- Monitor GPU memory usage
- If GPU OOM → reduce workers to 2
- If CPU bottleneck → increase workers to 8

**Known Issues**:
- CUDA may serialize access to same model
- Effective parallelism may be < worker count
- Test actual throughput improvement

---

## 8. Recommendation

### 8.1 Migration Sequence

**Recommend: Unity-first, then Server**

**Rationale**:
1. Unity changes are riskier (display coupling, coroutine refactor)
2. Server can run single-worker during Unity migration
3. Can validate Unity parallel logic with serial server first
4. Reduces concurrent risk (one side at a time)

**Sequence**:
1. Phase 1-3 (Unity parallel + deferred display)
2. Test with server workers=1
3. Phase 4 (server multi-worker)
4. Phase 5-6 (logging + cleanup)

### 8.2 Worker Count

**Recommend: Start with workers=4**

**Rationale**:
- Quest 3 sends ~5 FPS = 1 frame every 200ms
- If server takes 50ms per frame, 4 workers can handle 20 FPS
- 4x headroom for burst traffic or slow frames
- GPU likely supports 4 concurrent inferences
- Easy to reduce to 2 or increase to 8 based on testing

**Test Plan**:
```python
# Test 1: workers=1 (baseline)
# Measure: throughput, latency, GPU util

# Test 2: workers=2
# Measure: throughput gain, latency P50/P95

# Test 3: workers=4
# Measure: throughput gain, latency P50/P95, GPU memory

# Test 4: workers=8 (if needed)
# Check if throughput plateaus
```

### 8.3 Task Queue vs Workers

**Recommend: Uvicorn workers for Phase 4, defer Celery to future**

**Rationale**:
- Uvicorn workers = simple, no new dependencies
- Celery + Redis = complex setup, more moving parts
- Current workload (5 FPS) doesn't need advanced queue
- Can migrate to Celery later if needed (>20 FPS, priority queue, etc.)

**When to use Celery**:
- Need >10 concurrent clients
- Need priority queuing
- Need distributed workers across machines
- Need task retry/failure handling

---

## 9. Conflict Check

### 9.1 Existing Behavior That Must Be Preserved

✅ **Frame ID assignment**: Sequential, unique, sent in header
✅ **Display accuracy**: Skeleton/bbox positions must be correct
✅ **HUD metrics**: E2E latency, server time, detection count
✅ **Excel logging**: Some columns (latency, server_proc_ms, detection_count)
✅ **Server response schema**: JSON structure unchanged
✅ **FPS throttling**: Still send at target FPS (5 FPS)

### 9.2 Existing Logic That Must Be Removed

❌ **m_inferenceInProgress lock**: Blocks parallel requests
❌ **Immediate display**: Couples response to display
❌ **Frame N-1 header pattern**: Assumes sequential arrival
❌ **Freeze frame tracking**: Obsolete concept
❌ **Single previous_frames dict**: Server-side sequential assumption

### 9.3 Existing Logic That Can Be Adapted

🔄 **FrameStateManager**: Extend to multi-frame tracking
🔄 **SharedInferenceHUD**: Redefine ReportDroppedFrame() semantics
🔄 **Excel columns**: Add new, keep most old ones
🔄 **Timing calculations**: Compute per-frame instead of Frame N-1
🔄 **Display methods**: Call from Update() instead of coroutine

### 9.4 Hidden Assumptions That May Cause Bugs

⚠️ **Assumption 1**: Response order == send order
**Impact**: Display logic expects this
**Fix**: Phase 3 - newest-frame selection

⚠️ **Assumption 2**: Only one DrawPoseSkeletons() call per frame
**Impact**: GameObject pools may not handle concurrent updates
**Fix**: Lock around pool access, or Update()-only display

⚠️ **Assumption 3**: m_lastE2eMs always correlates to current request
**Impact**: Parallel responses overwrite this variable
**Fix**: Phase 1 - per-frame FrameTrace storage

⚠️ **Assumption 4**: Server processes Frame N before Frame N+1 arrives
**Impact**: FrameStateManager assumes N+1 triggers N logging
**Fix**: Phase 5 - log each frame independently when response completes

⚠️ **Assumption 5**: Unity GC can keep up with coroutine allocation
**Impact**: Parallel may create 5-10 coroutines in flight
**Fix**: Monitor memory, limit max pending frames (e.g., 20)

⚠️ **Assumption 6**: UnityWebRequest is thread-safe for concurrent use
**Impact**: May not be safe to have 10+ concurrent requests
**Fix**: Test, add request pool if needed

---

## 10. Open Questions

### Critical Questions Needed Before Implementation:

1. **Display latency tolerance**: What is acceptable delay between frame completion and display? (e.g., is 1-2 Unity frames = 16-32ms OK?)

2. **Max pending frames limit**: Should we cap the number of in-flight requests? (Recommended: 20 max to prevent memory issues)

3. **Dropped frame logging**: Should Excel log EVERY frame (including Dropped), or only Displayed frames?

4. **Frame timeout**: How long before marking Pending → Failed? (Recommended: 5 seconds)

5. **Backward compatibility**: Do we need to keep old Excel column names during migration, or can we rename immediately?

6. **Testing environment**: Can we test parallel mode without affecting production, or do we need a separate test scene?

7. **GPU memory limit**: What is the GPU VRAM limit on the server? (Affects max worker count)

8. **Freeze frame metric**: Do you want a replacement metric for "freeze frames", or completely remove the concept?
   - Option A: Track "Unity frames without new display" (similar to old freeze)
   - Option B: Remove entirely, rely on drop_rate instead

9. **Error handling**: What should Unity do if ALL workers are busy and requests queue up? Display warning? Drop new requests?

10. **Migration rollback**: Do you want feature flags to toggle between serial/parallel mode during testing?

---

## Summary

This proposal provides a **phased, low-risk migration path** from the current serial architecture to parallel fire-and-forget processing. The approach:

- ✅ Preserves existing frame ID and response schema
- ✅ Minimizes breaking changes to display pipeline
- ✅ Allows incremental testing and rollback
- ✅ Redefines drop/freeze metrics to match new parallel semantics
- ✅ Extends logging without losing historical data compatibility

**Recommended Next Steps**:
1. Review this proposal and answer open questions
2. Approve Phase 1-3 (Unity-side changes)
3. Implement Phase 1 (FrameTrace structure)
4. Test Phase 2 (parallel sending with serial server)
5. Test Phase 3 (deferred display)
6. Only then proceed to Phase 4 (server multi-worker)

**Estimated Timeline**:
- Phase 1: 1-2 days
- Phase 2: 2-3 days
- Phase 3: 3-5 days (most complex)
- Phase 4: 1 day
- Phase 5: 2-3 days
- Phase 6: 1-2 days
- **Total: ~2 weeks with testing**
