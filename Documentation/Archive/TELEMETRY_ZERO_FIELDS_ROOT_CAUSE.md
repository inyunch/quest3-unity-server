# Root Cause Analysis: Why Telemetry Fields Are All Zeros

**Date**: 2026-04-14
**Status**: 🔴 IDENTIFIED - Complete data flow breakdown

---

## Executive Summary

**All new telemetry fields are zero because the data flow is completely broken at multiple points:**

1. **Unity C# never populates FrameTrace timestamps** (send_ts, receive_ts, display_ts, drop_ts)
2. **Unity never sends these timestamps to the server** (no HTTP headers)
3. **Server never receives or parses these timestamps** (not reading any Unity timestamp headers)
4. **Server never passes timestamps to the logger** (frame_state_manager doesn't forward them)
5. **Server never sets final_state field** (always defaults to "Displayed")

**Result**: The `log_inference()` function receives all default values (0.0) for the new fields, which get written to Excel as zeros.

---

## 1. Root Cause Analysis

### Field: `unity_send_ts` = 0

**Where it should be set**: In Unity C#, when creating the FrameTrace object

**Current status**: ❌ **NEVER ASSIGNED**

**Evidence**:
- File: `Assets/PassthroughCameraApiSamples/PoseEstimation/Scripts/PoseInferenceRunManager.cs`
- Line 443: `FrameTrace trace = new FrameTrace(m_frameId);`
- The constructor only takes `frame_id`, never sets `unity_send_ts`
- **File: `Assets/PassthroughCameraApiSamples/Shared/Scripts/FrameTrace.cs`**
  - Constructor: `public FrameTrace(int frameId)` - no timestamp parameter
  - Field `unity_send_ts` is declared but initialized to `0f` and never updated

**Why it stays zero**:
1. FrameTrace constructor doesn't set it
2. No code in `RunServerInference()` sets it after construction
3. Never sent to server as HTTP header
4. Server defaults it to 0.0 in `log_inference()`

---

### Field: `unity_receive_ts` = 0

**Where it should be set**: In Unity C#, when the coroutine receives the server response

**Current status**: ❌ **NEVER ASSIGNED**

**Evidence**:
- File: `PoseInferenceRunManager.cs`, line ~465-510 (response parsing section)
- The code parses the JSON response but never calls `trace.MarkCompleted()`
- `MarkCompleted()` is supposed to set `unity_receive_ts = Time.realtimeSinceStartup`
- **Instead, the code directly sets `trace.state = FrameState.Completed` and `trace.response = response`**

**Actual code** (PoseInferenceRunManager.cs:503-506):
```csharp
lock (m_frameTracesLock)
{
    trace.state = FrameState.Completed;
    trace.response = response;
}
```

**Missing code**:
```csharp
trace.MarkCompleted(Time.realtimeSinceStartup);  // This is NEVER called
```

**Why it stays zero**:
1. FrameTrace.MarkCompleted() is never invoked
2. unity_receive_ts remains at default value 0f
3. Never sent to server
4. Server defaults to 0.0

---

### Field: `unity_display_ts` = 0

**Where it should be set**: In Unity C#, in the `DisplayFrame()` method after rendering

**Current status**: ❌ **NEVER ASSIGNED**

**Evidence**:
- File: `PoseInferenceRunManager.cs`, line ~630-670 (DisplayFrame method)
- After calling `m_uiInference.DrawSkeleton()`, the code should call `trace.MarkDisplayed()`
- **This call is NEVER made**

**Current code** (PoseInferenceRunManager.cs:662):
```csharp
m_uiInference.DrawSkeleton(skeletons, cachedCameraPose);

// MISSING: trace.MarkDisplayed(Time.realtimeSinceStartup);
```

**Then in TryDisplayNewestFrame()** (line 580):
```csharp
DisplayFrame(newest);
newest.MarkDisplayed(Time.realtimeSinceStartup);  // ✅ THIS LINE EXISTS
```

**Problem**: The `MarkDisplayed()` call IS present in `TryDisplayNewestFrame()`, but:
1. Unity never sends this timestamp to the server
2. Server never reads it from headers
3. Server never passes it to logger

**Why it stays zero**:
1. Unity sets it locally in FrameTrace object
2. **But Unity never sends it to the server via HTTP headers**
3. Server has no way to receive this value
4. Server defaults to 0.0 in log_inference()

---

### Field: `unity_drop_ts` = 0

**Where it should be set**: In Unity C#, when marking a frame as dropped

**Current status**: ⚠️ **ASSIGNED IN UNITY, BUT NEVER SENT TO SERVER**

**Evidence**:
- File: `PoseInferenceRunManager.cs`, line 587-589
```csharp
completedFrames[i].MarkDropped(Time.realtimeSinceStartup, $"superseded_by_newer_{newest.frame_id}");
```

**Problem**: Same as unity_display_ts
1. Unity sets it locally in FrameTrace
2. **Unity never sends it to server** (no HTTP header)
3. Server never receives it
4. Server defaults to 0.0

---

### Field: `server_receive_ts` = 0

**Where it should be set**: In Python server, when request is received

**Current status**: ⚠️ **CALCULATED BUT NEVER PASSED TO LOGGER**

**Evidence**:
- File: `vision_server/app/routes/infer_human.py`
- Line 116: `t_recv = time.time()`  ✅ Server records this
- Line 722: `t_server_recv=t_recv`  ✅ Added to response JSON
- **Line 820-842: `frame_manager.process_frame()` - NEVER receives `server_receive_ts`**
- **Line 172-213: `complete_frame_data` dict - NEVER includes `server_receive_ts`**

**The data flow breaks here**:
```python
# Server calculates it:
t_recv = time.time()  # Line 116

# Server puts it in response:
response = HumanInferenceResponse(
    ...
    t_server_recv=t_recv,  # Line 722
    ...
)

# But frame_manager.process_frame() never receives it!
complete_frame_data = frame_manager.process_frame(
    server_proc_ms=processing_time_ms,  # Only passes processing time
    # MISSING: server_receive_ts=t_recv,
    # MISSING: server_send_ts=t_postprocess_end,
    ...
)

# And complete_frame_data never includes it:
complete_frame_data = {
    'server_proc_ms': previous_frame.server_proc_ms,
    # MISSING: 'server_receive_ts': ...
    # MISSING: 'server_send_ts': ...
}

# So log_async() never receives it:
log_async(**complete_frame_data)  # Missing server_receive_ts
```

**Why it stays zero**:
1. Server calculates `t_recv` but never stores it
2. `frame_manager.process_frame()` doesn't accept it as parameter
3. `complete_frame_data` dict doesn't include it
4. `log_inference()` defaults to 0.0

---

### Field: `server_send_ts` = 0

**Same problem as `server_receive_ts`**

**Evidence**:
- Line 711: `t_postprocess_end = time.time()` ✅ Calculated
- Line 723: `t_server_send=t_postprocess_end` ✅ In response
- But never passed to `frame_manager.process_frame()`
- Never in `complete_frame_data`
- Never reaches `log_inference()`

---

### Field: `final_state` = always "Displayed"

**Where it should be set**: Based on Unity's FrameTrace.state

**Current status**: ❌ **HARDCODED TO DEFAULT VALUE**

**Evidence**:
- File: `vision_server/debug/inference_logger.py`
- Line 121: `final_state="Displayed"`  (function parameter default)
- **No code ever passes a different value**

**The state information exists in Unity**:
```csharp
// Unity has this information:
trace.state = FrameState.Displayed;  // or Dropped, Failed, etc.
```

**But it's never communicated to the server**:
1. Unity doesn't send `X-Final-State` header
2. Server doesn't read it
3. `frame_manager.process_frame()` doesn't receive it
4. `complete_frame_data` doesn't include it
5. `log_inference()` uses default "Displayed"

---

### Field: `drop_reason` = always ""

**Where it should be set**: When Unity drops a frame

**Current status**: ❌ **NEVER COMMUNICATED TO SERVER**

**Evidence**:
- Unity code: `trace.MarkDropped(time, "superseded_by_newer_123")`
- Unity stores: `trace.drop_reason = "superseded_by_newer_123"`
- **But never sends to server**
- Server defaults to `drop_reason=""` in `log_inference()`

---

### Field: `dropped_frames` = 0 (cumulative count)

**Where it should be set**: Unity sends it as `X-Dropped-Frames` header

**Current status**: ✅ **SENT BY UNITY, BUT VALUE IS ALWAYS 0**

**Evidence**:
- File: `PoseInferenceRunManager.cs`, line 393
```csharp
request.SetRequestHeader("X-Dropped-Frames", m_droppedFrames.ToString());
```

**But `m_droppedFrames` is always 0 because**:
- Line 582 increments it: `m_droppedFrames += droppedCount;`
- But this happens in `TryDisplayNewestFrame()` which counts superseded frames
- **However, the logging happens on the SERVER when frame N+1 arrives**
- At that point, Unity hasn't yet detected/counted the drops
- By the time Unity counts drops, the server has already logged the previous frame with old count

**Timeline problem**:
```
Frame N: Unity sends (m_droppedFrames=5)
Frame N+1: Unity sends (m_droppedFrames=5) -> Server logs Frame N with dropped_frames=5
[Unity now processes Frame N, finds it was superseded]
Frame N+1: Unity increments m_droppedFrames to 6
Frame N+2: Unity sends (m_droppedFrames=6) -> Server logs Frame N+1 with dropped_frames=6
```

The count is always one frame behind!

---

## 2. Exact File/Function Review

### Unity C# - Request Send Path

**File**: `Assets/PassthroughCameraApiSamples/PoseEstimation/Scripts/PoseInferenceRunManager.cs`

**Function**: `RunServerInference()` (coroutine, starts ~line 360)

**Current code**:
```csharp
// Line 443: Create trace
FrameTrace trace = new FrameTrace(m_frameId);

// Line 376-395: Set headers (legacy metrics only)
request.SetRequestHeader("X-Scene-Name", "PoseEstimation");
request.SetRequestHeader("X-Frame-Id", m_frameId.ToString());
request.SetRequestHeader("X-E2E-Ms", m_lastE2eMs.ToString("F1"));
// ... etc (all legacy metrics)
request.SetRequestHeader("X-Dropped-Frames", m_droppedFrames.ToString());
request.SetRequestHeader("X-Freeze-Frames", m_frozenFrames.ToString());
```

**Missing code**:
```csharp
// MISSING: Set unity_send_ts
trace.unity_send_ts = Time.realtimeSinceStartup;

// MISSING: Send Unity timestamps to server
request.SetRequestHeader("X-Unity-Send-Ts", trace.unity_send_ts.ToString("F6"));
// (display_ts and drop_ts are set later, can't send here)
```

**Other 2 managers with same issue**:
- `MultiObjectDetection/SentisInference/Scripts/SentisInferenceRunManager.cs`
- `Segmentation/SegmentationInference/Scripts/SegmentationInferenceRunManager.cs`

---

### Unity C# - Response Receive Path

**File**: `PoseInferenceRunManager.cs`

**Function**: `RunServerInference()` - response parsing section (~line 465-510)

**Current code**:
```csharp
// Line 503-506
lock (m_frameTracesLock)
{
    trace.state = FrameState.Completed;
    trace.response = response;
}
```

**Missing code**:
```csharp
// MISSING: Call MarkCompleted to set unity_receive_ts
trace.MarkCompleted(Time.realtimeSinceStartup);

// Then state and response are already set inside MarkCompleted
```

**Also needed**: Parse `t_server_recv` and `t_server_send` from response JSON

---

### Unity C# - Display Path

**File**: `PoseInferenceRunManager.cs`

**Function**: `TryDisplayNewestFrame()` (~line 560-600)

**Current code**:
```csharp
// Line 580: ✅ Already calls MarkDisplayed
DisplayFrame(newest);
newest.MarkDisplayed(Time.realtimeSinceStartup);
```

**Problem**: This is correct in Unity, but the timestamp is never sent back to the server!

**What's needed**:
- Option A: Send display timestamp in NEXT request's headers
- Option B: Send async telemetry-only POST to server with final trace data
- Option C: Export trace data to local CSV in Unity (bypass server logging)

---

### Unity C# - Drop Decision Path

**File**: `PoseInferenceRunManager.cs`

**Function**: `TryDisplayNewestFrame()` (~line 586-589)

**Current code**:
```csharp
// Line 587-589: ✅ Already calls MarkDropped
completedFrames[i].MarkDropped(Time.realtimeSinceStartup, $"superseded_by_newer_{newest.frame_id}");
```

**Same problem**: Correct in Unity, never sent to server

---

### Server - Response JSON Parsing

**File**: `vision_server/app/routes/infer_human.py`

**Function**: `infer_human()` (line 77-851)

**Current code**: Server calculates timestamps but doesn't store them for logging

```python
# Line 116: Record receive time
t_recv = time.time()

# Line 711: Record send time
t_postprocess_end = time.time()

# Line 714-729: Put in response (Unity could read these)
response = HumanInferenceResponse(
    ...
    t_server_recv=t_recv,
    t_server_send=t_postprocess_end,
    ...
)
```

**Missing**: Pass these to `frame_manager.process_frame()`

---

### Server - Frame State Manager

**File**: `vision_server/debug/frame_state_manager.py`

**Function**: `process_frame()` (line 68-219)

**Current code**: Doesn't accept or forward Unity/server timestamps

```python
def process_frame(
    self,
    scene: str,
    frame_id: int,
    server_proc_ms: float,  # Only has processing time
    client_e2e_ms: float,
    # ... other legacy metrics
    # MISSING: unity_send_ts parameter
    # MISSING: unity_receive_ts parameter
    # MISSING: unity_display_ts parameter
    # MISSING: unity_drop_ts parameter
    # MISSING: server_receive_ts parameter
    # MISSING: server_send_ts parameter
    # MISSING: final_state parameter
    # MISSING: drop_reason parameter
):
```

**Return value** (`complete_frame_data` dict, line 172-213):
```python
complete_frame_data = {
    'scene': scene,
    'frame_id': prev_frame_id,
    'latency_ms': current_frame.client_e2e_ms,
    'server_proc_ms': previous_frame.server_proc_ms,
    # ... other legacy metrics
    # MISSING: 'unity_send_ts': ...
    # MISSING: 'unity_receive_ts': ...
    # MISSING: 'unity_display_ts': ...
    # MISSING: 'unity_drop_ts': ...
    # MISSING: 'server_receive_ts': ...
    # MISSING: 'server_send_ts': ...
    # MISSING: 'final_state': ...
    # MISSING: 'drop_reason': ...
}
```

---

### Server - Logger Export Path

**File**: `vision_server/debug/inference_logger.py`

**Function**: `log_inference()` (line 87-243)

**Current code**: Has parameters but they default to 0

```python
def log_inference(
    scene,
    frame_id,
    # PHASE 5: New per-frame timestamps
    unity_send_ts=0.0,         # ❌ Defaults to 0, never overridden
    unity_receive_ts=0.0,      # ❌ Defaults to 0, never overridden
    unity_display_ts=0.0,      # ❌ Defaults to 0, never overridden
    unity_drop_ts=0.0,         # ❌ Defaults to 0, never overridden
    server_receive_ts=0.0,     # ❌ Defaults to 0, never overridden
    server_send_ts=0.0,        # ❌ Defaults to 0, never overridden
    # ...
    final_state="Displayed",   # ❌ Defaults to "Displayed", never overridden
    drop_reason="",            # ❌ Defaults to "", never overridden
    # ...
):
```

**Called from** `infer_human.py` line 846:
```python
log_async(**complete_frame_data)
```

**Problem**: `complete_frame_data` doesn't contain these keys, so defaults are used

---

## 3. Required Code Changes

### Change Group A: Unity Sends Timestamps to Server (HTTP Headers)

**Limitation**: Unity can only send past/current data, not future data
- ✅ Can send: `unity_send_ts` (known at send time)
- ❌ Cannot send: `unity_receive_ts` (unknown until response arrives)
- ❌ Cannot send: `unity_display_ts` (unknown until frame is displayed, which is later)
- ❌ Cannot send: `unity_drop_ts` (unknown until drop decision, which is later)

**Implication**: We need a "delayed header" approach - send Frame N's final timestamps in Frame N+1's request

---

### Change Group B: Unity Populates FrameTrace Correctly

**File**: `PoseInferenceRunManager.cs` (and 2 other managers)

**Location 1**: Line 443 (trace creation)
```csharp
// BEFORE:
FrameTrace trace = new FrameTrace(m_frameId);

// AFTER:
FrameTrace trace = new FrameTrace(m_frameId);
trace.unity_send_ts = Time.realtimeSinceStartup;  // Record send time
```

**Location 2**: Line 503-506 (response receive)
```csharp
// BEFORE:
lock (m_frameTracesLock)
{
    trace.state = FrameState.Completed;
    trace.response = response;
}

// AFTER:
lock (m_frameTracesLock)
{
    trace.MarkCompleted(Time.realtimeSinceStartup);  // Sets state + unity_receive_ts
    trace.response = response;

    // Parse server timestamps from response
    trace.server_receive_ts = (float)response.t_server_recv;
    trace.server_send_ts = (float)response.t_server_send;
}
```

**Location 3**: Already correct (line 580 - MarkDisplayed is called)

**Location 4**: Already correct (line 587-589 - MarkDropped is called)

---

### Change Group C: Unity Sends Previous Frame's Final Telemetry

**Strategy**: Use "delayed headers" pattern - when sending Frame N+1, include Frame N's final state

**File**: `PoseInferenceRunManager.cs`, line ~376-395 (SetRequestHeader section)

**New member variable**:
```csharp
private FrameTrace m_lastCompletedTrace = null;  // Store previous frame's final trace
```

**Add headers** (new section after line 395):
```csharp
// Send previous frame's final telemetry (if available)
if (m_lastCompletedTrace != null)
{
    request.SetRequestHeader("X-Prev-Frame-Id", m_lastCompletedTrace.frame_id.ToString());
    request.SetRequestHeader("X-Prev-Unity-Send-Ts", m_lastCompletedTrace.unity_send_ts.ToString("F6"));
    request.SetRequestHeader("X-Prev-Unity-Receive-Ts", m_lastCompletedTrace.unity_receive_ts.ToString("F6"));
    request.SetRequestHeader("X-Prev-Unity-Display-Ts", m_lastCompletedTrace.unity_display_ts.ToString("F6"));
    request.SetRequestHeader("X-Prev-Unity-Drop-Ts", m_lastCompletedTrace.unity_drop_ts.ToString("F6"));
    request.SetRequestHeader("X-Prev-Server-Receive-Ts", m_lastCompletedTrace.server_receive_ts.ToString("F6"));
    request.SetRequestHeader("X-Prev-Server-Send-Ts", m_lastCompletedTrace.server_send_ts.ToString("F6"));
    request.SetRequestHeader("X-Prev-Final-State", m_lastCompletedTrace.state.ToString());
    request.SetRequestHeader("X-Prev-Drop-Reason", m_lastCompletedTrace.drop_reason ?? "");
}
```

**Update m_lastCompletedTrace** in `TryDisplayNewestFrame()`:
```csharp
// After MarkDisplayed or MarkDropped:
m_lastCompletedTrace = newest;  // Save for next request
```

---

### Change Group D: Server Reads Previous Frame Telemetry

**File**: `vision_server/app/routes/infer_human.py`

**Location**: After line 791 (after reading current frame headers)

```python
# Read previous frame's final telemetry (delayed from Frame N-1)
try:
    prev_frame_id = int(request.headers.get("X-Prev-Frame-Id", "-1"))
    if prev_frame_id >= 0:
        prev_unity_send_ts = float(request.headers.get("X-Prev-Unity-Send-Ts", "0"))
        prev_unity_receive_ts = float(request.headers.get("X-Prev-Unity-Receive-Ts", "0"))
        prev_unity_display_ts = float(request.headers.get("X-Prev-Unity-Display-Ts", "0"))
        prev_unity_drop_ts = float(request.headers.get("X-Prev-Unity-Drop-Ts", "0"))
        prev_server_receive_ts = float(request.headers.get("X-Prev-Server-Receive-Ts", "0"))
        prev_server_send_ts = float(request.headers.get("X-Prev-Server-Send-Ts", "0"))
        prev_final_state = request.headers.get("X-Prev-Final-State", "Displayed")
        prev_drop_reason = request.headers.get("X-Prev-Drop-Reason", "")

        # Log previous frame immediately (has complete data now)
        log_async(
            scene=scene,
            frame_id=prev_frame_id,
            unity_send_ts=prev_unity_send_ts,
            unity_receive_ts=prev_unity_receive_ts,
            unity_display_ts=prev_unity_display_ts,
            unity_drop_ts=prev_unity_drop_ts,
            server_receive_ts=prev_server_receive_ts,
            server_send_ts=prev_server_send_ts,
            final_state=prev_final_state,
            drop_reason=prev_drop_reason,
            # ... need to also send other metrics for Frame N-1 (detection_count, etc.)
            # This is the problem: we don't have Frame N-1's detection results anymore!
        )
except (ValueError, TypeError):
    pass  # Previous frame telemetry not available or invalid
```

**Problem with this approach**: We need Frame N-1's detection results, but we don't have them anymore!

---

### Change Group E: Server Stores Frame Data for Delayed Logging

**This is what `frame_state_manager.py` was supposed to do, but it's incomplete**

**File**: `vision_server/debug/frame_state_manager.py`

**Class**: `FrameState` dataclass (line 12-49)

**Add new fields**:
```python
@dataclass
class FrameState:
    # ... existing fields ...

    # NEW: Unity timestamps
    unity_send_ts: float = 0.0
    unity_receive_ts: float = 0.0
    unity_display_ts: float = 0.0
    unity_drop_ts: float = 0.0

    # NEW: Server timestamps
    server_receive_ts: float = 0.0
    server_send_ts: float = 0.0

    # NEW: Final state
    final_state: str = "Completed"  # Will be updated when final telemetry arrives
    drop_reason: str = ""
```

**Function**: `process_frame()` - add new parameters (line 68-104)

```python
def process_frame(
    self,
    scene: str,
    frame_id: int,
    server_proc_ms: float,

    # NEW: Add Unity/server timestamps
    unity_send_ts: float = 0.0,
    unity_receive_ts: float = 0.0,
    unity_display_ts: float = 0.0,
    unity_drop_ts: float = 0.0,
    server_receive_ts: float = 0.0,
    server_send_ts: float = 0.0,
    final_state: str = "Completed",
    drop_reason: str = "",

    # ... existing parameters ...
) -> Optional[Dict]:
```

**Update FrameState creation** (line 114-137):
```python
current_frame = FrameState(
    scene=scene,
    frame_id=frame_id,
    server_proc_ms=server_proc_ms,
    timestamp=time.time(),

    # NEW: Store Unity/server timestamps
    unity_send_ts=unity_send_ts,
    unity_receive_ts=unity_receive_ts,
    unity_display_ts=unity_display_ts,
    unity_drop_ts=unity_drop_ts,
    server_receive_ts=server_receive_ts,
    server_send_ts=server_send_ts,
    final_state=final_state,
    drop_reason=drop_reason,

    # ... existing fields ...
)
```

**Update `complete_frame_data` return dict** (line 172-213):
```python
complete_frame_data = {
    'scene': scene,
    'frame_id': prev_frame_id,

    # NEW: Unity timestamps (from CURRENT frame's headers, which are about PREVIOUS frame)
    'unity_send_ts': current_frame.unity_send_ts,
    'unity_receive_ts': current_frame.unity_receive_ts,
    'unity_display_ts': current_frame.unity_display_ts,
    'unity_drop_ts': current_frame.unity_drop_ts,

    # NEW: Server timestamps (from PREVIOUS frame when it was processed)
    'server_receive_ts': previous_frame.server_receive_ts,
    'server_send_ts': previous_frame.server_send_ts,

    # NEW: Final state (from CURRENT frame's headers)
    'final_state': current_frame.final_state,
    'drop_reason': current_frame.drop_reason,

    # ... existing fields ...
}
```

---

### Change Group F: Server Passes Timestamps to frame_manager

**File**: `vision_server/app/routes/infer_human.py`

**Location**: Line 820-842 (calling frame_manager.process_frame)

**BEFORE**:
```python
complete_frame_data = frame_manager.process_frame(
    scene=scene,
    frame_id=frame_id,
    server_proc_ms=processing_time_ms,
    client_e2e_ms=e2e_ms,
    # ... other params
)
```

**AFTER**:
```python
# Read previous frame telemetry from headers (Frame N-1's final data in Frame N's request)
prev_unity_send_ts = float(request.headers.get("X-Prev-Unity-Send-Ts", "0"))
prev_unity_receive_ts = float(request.headers.get("X-Prev-Unity-Receive-Ts", "0"))
prev_unity_display_ts = float(request.headers.get("X-Prev-Unity-Display-Ts", "0"))
prev_unity_drop_ts = float(request.headers.get("X-Prev-Unity-Drop-Ts", "0"))
prev_server_receive_ts = float(request.headers.get("X-Prev-Server-Receive-Ts", "0"))
prev_server_send_ts = float(request.headers.get("X-Prev-Server-Send-Ts", "0"))
prev_final_state = request.headers.get("X-Prev-Final-State", "Completed")
prev_drop_reason = request.headers.get("X-Prev-Drop-Reason", "")

complete_frame_data = frame_manager.process_frame(
    scene=scene,
    frame_id=frame_id,
    server_proc_ms=processing_time_ms,
    server_receive_ts=t_recv,  # NEW: Current frame's server timestamps
    server_send_ts=t_postprocess_end,  # NEW

    # NEW: Previous frame's Unity timestamps (from headers)
    unity_send_ts=prev_unity_send_ts,
    unity_receive_ts=prev_unity_receive_ts,
    unity_display_ts=prev_unity_display_ts,
    unity_drop_ts=prev_unity_drop_ts,

    # These are previous frame's server timestamps (from headers, copied from response)
    # Wait, this is confusing. Let me rethink...

    # Actually: Frame N-1's server timestamps should have been stored when Frame N-1 was processed
    # So we don't need to re-send them from Unity

    # ... other params
)
```

**Wait, this is getting complex. Let me simplify...**

---

## 4. Final-State Consistency Rules

The implementation must enforce:

### Rule 1: One frame_id → One final trace
- Each frame_id should appear exactly once in the log
- No duplicates allowed

### Rule 2: Exactly one final_state per frame
- Values: "Pending", "Completed", "Displayed", "Dropped", "Failed"
- Once set to final state (Displayed/Dropped/Failed), cannot change

### Rule 3: Displayed and Dropped are mutually exclusive
```python
if final_state == "Displayed":
    assert unity_display_ts > 0
    assert unity_drop_ts == 0
elif final_state == "Dropped":
    assert unity_drop_ts > 0
    assert unity_display_ts == 0
```

### Rule 4: Timestamp ordering
```python
assert unity_send_ts <= unity_receive_ts
assert unity_receive_ts <= unity_display_ts or unity_receive_ts <= unity_drop_ts
assert server_receive_ts <= server_send_ts
```

---

## 5. Patch Plan

### The Core Problem

The current implementation has a **fundamental architecture mismatch**:

**Original Design (what was planned)**:
- Unity would send complete per-frame telemetry to server
- Server would log complete frames immediately

**Actual Implementation**:
- Unity creates FrameTrace objects locally ✅
- Unity populates timestamps locally ⚠️ (partially)
- Unity NEVER sends these timestamps to server ❌
- Server has no way to receive them ❌
- Logger writes zeros because data never arrives ❌

**Why this happened**:
- FrameTrace.cs was created (Phase 1) ✅
- Unity managers use FrameTrace for local tracking (Phase 2-3) ✅
- **HTTP header propagation was never implemented** ❌
- **Server-side reception was never implemented** ❌
- Logger schema was updated but data pipeline was not ❌

---

### Recommended Solution: Dual-Path Logging

Since the data flow is broken, we have two options:

#### Option A: Fix the Unity→Server→Excel Pipeline (Complex)

**Pros**: Centralized logging in Python/Excel
**Cons**: Requires extensive changes across Unity + Server

**Steps**:
1. Unity sends "delayed telemetry headers" in Frame N+1 for Frame N
2. Server receives and stores in frame_state_manager
3. Server logs Frame N when Frame N+1 arrives with complete data

**Complexity**: HIGH (requires coordinated changes in 3 Unity managers + 2 Python files)

---

#### Option B: Unity Exports Traces Locally (Simple)

**Pros**:
- Minimal changes
- No server modifications needed
- Unity has complete frame lifecycle data already
- Can export anytime (no server dependency)

**Cons**:
- Separate CSV file instead of centralized Excel
- Need to merge data if analyzing server+Unity together

**Steps**:
1. Unity keeps current FrameTrace implementation (already working)
2. Add `FrameTraceExporter.cs` utility class
3. Export to CSV on scene close or periodically
4. Each Unity scene creates its own CSV: `PoseEstimation_traces_2026-04-14.csv`

**Complexity**: LOW (single new file, ~100 lines of code)

---

### Minimal Patch Plan (Recommendation: Option B)

Since you want **minimal changes** and the Unity-side tracking is already working, the fastest fix is:

**Step 1**: Verify Unity FrameTrace is populated correctly

Check that these calls exist:
- ✅ `trace.unity_send_ts = Time.realtimeSinceStartup` (add if missing)
- ✅ `trace.MarkCompleted(Time.realtimeSinceStartup)` (replace direct state assignment)
- ✅ `trace.MarkDisplayed(Time.realtimeSinceStartup)` (already exists)
- ✅ `trace.MarkDropped(Time.realtimeSinceStartup, reason)` (already exists)
- ✅ Parse `server_receive_ts` and `server_send_ts` from response JSON

**Step 2**: Add local CSV export in Unity

Create `FrameTraceExporter.cs`:
```csharp
public static class FrameTraceExporter
{
    public static void ExportToCSV(List<FrameTrace> traces, string sceneName)
    {
        string timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        string path = $"Logs/{sceneName}_traces_{timestamp}.csv";

        using (StreamWriter writer = new StreamWriter(path))
        {
            // Write header
            writer.WriteLine("frame_id,unity_send_ts,unity_receive_ts,unity_display_ts,unity_drop_ts," +
                           "server_receive_ts,server_send_ts,final_state,drop_reason");

            // Write data
            foreach (var trace in traces)
            {
                writer.WriteLine($"{trace.frame_id},{trace.unity_send_ts:F6},{trace.unity_receive_ts:F6}," +
                               $"{trace.unity_display_ts:F6},{trace.unity_drop_ts:F6}," +
                               $"{trace.server_receive_ts:F6},{trace.server_send_ts:F6}," +
                               $"{trace.state},{trace.drop_reason}");
            }
        }
    }
}
```

Call it in `OnDestroy()`:
```csharp
private void OnDestroy()
{
    if (m_useServerInference)
    {
        FrameTraceExporter.ExportToCSV(new List<FrameTrace>(m_frameTraces.Values), "PoseEstimation");
    }
}
```

**Step 3**: Keep server Excel logging for server-side metrics only

Leave the server logging as-is for server processing metrics (latency_ms, server_proc_ms, detection_count, etc.)

**Step 4**: Merge CSV + Excel offline if needed

Use Python/pandas to join:
- Unity CSV: frame-level lifecycle timestamps
- Server Excel: frame-level inference results

**Total code changes**:
- 1 new file: FrameTraceExporter.cs (~80 lines)
- 3 files modified: 3 InferenceRunManager.cs files (~5 lines each to call exporter)
- 0 server files modified

**Time to implement**: ~30 minutes

**Testing**: Immediate - run scene, close scene, check Logs/ folder for CSV

---

## Summary Table

| Field | Unity Sets? | Unity Sends to Server? | Server Receives? | Server Logs? | Result |
|-------|-------------|----------------------|------------------|--------------|--------|
| `unity_send_ts` | ❌ NO | ❌ NO | ❌ NO | ❌ Defaults to 0 | **Zero** |
| `unity_receive_ts` | ⚠️ Partial (not using MarkCompleted) | ❌ NO | ❌ NO | ❌ Defaults to 0 | **Zero** |
| `unity_display_ts` | ✅ YES | ❌ NO | ❌ NO | ❌ Defaults to 0 | **Zero** |
| `unity_drop_ts` | ✅ YES | ❌ NO | ❌ NO | ❌ Defaults to 0 | **Zero** |
| `server_receive_ts` | N/A | N/A | ✅ YES (calculates) | ❌ Never passed to logger | **Zero** |
| `server_send_ts` | N/A | N/A | ✅ YES (calculates) | ❌ Never passed to logger | **Zero** |
| `final_state` | ✅ YES | ❌ NO | ❌ NO | ❌ Defaults to "Displayed" | **"Displayed"** |
| `drop_reason` | ✅ YES | ❌ NO | ❌ NO | ❌ Defaults to "" | **Empty** |
| `dropped_frames` | ✅ YES | ✅ YES | ✅ YES | ✅ YES | **Off-by-one** (timing issue) |

**Root cause**: Data exists in Unity, but never transmitted to server, and server never logs it.

**Fastest fix**: Export Unity FrameTrace data locally to CSV instead of relying on server logging.
