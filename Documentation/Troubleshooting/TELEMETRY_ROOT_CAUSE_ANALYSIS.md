# Telemetry Root Cause Analysis - Patch-Style Correction Plan

**Date**: 2026-04-14
**Status**: CRITICAL BUGS IDENTIFIED

---

## EXECUTIVE SUMMARY

### Critical Finding #1: Code Rollback Detected
**User has reverted Unity code back to single-variable `m_lastCompletedTrace` instead of queue-based implementation.**

Evidence:
- System reminder shows Segmentation line 87: `private FrameTrace m_lastCompletedTrace = null;`
- System reminder shows MultiObjectDetection line 90: `private FrameTrace m_lastCompletedTrace = null;`
- Our queue implementation used: `private Queue<FrameTrace> m_completedFramesQueue`

**Impact**: The queue-based fix for handling multiple dropped frames was discarded.

### Critical Finding #2: "Completed" Default Value Bug
**Location**: `C:\Repo\Github\vision_server\app\routes\infer_human.py` Line 828

```python
prev_final_state = request.headers.get("X-Prev-Final-State", "Completed")  # BUG!
```

**Root Cause**: When Unity does NOT send `X-Prev-Final-State` header, server defaults to "Completed".

**Why Unity doesn't send headers**:
1. First frame: No previous frame exists, so no headers sent → Default "Completed" ✓ Expected
2. Segmentation: Code appears to be old version (lines 626-638 exist BUT not being executed) → All frames "Completed" ❌ BUG
3. PoseEstimation: Code exists BUT queue was reverted to single variable → Some frames "Completed" ❌ BUG

### Critical Finding #3: Segmentation Completely Broken
**All timestamps are NaN, all states are "Completed"**

Possible causes:
1. Segmentation code not recompiled after our changes
2. Segmentation using different code path that bypasses telemetry
3. FrameTrace not being created/populated in Segmentation
4. Unity running old cached assembly

---

## ROOT CAUSE ANALYSIS BY MODE

### Mode 1: MultiObjectDetection ✅ WORKING (Reference Implementation)

**Status**: 100% Unity timestamps, 100% server timestamps, 98% Displayed

**Why it works**:
1. ✅ Code includes delayed telemetry headers (lines 596-608)
2. ✅ Uses `m_lastCompletedTrace` (line 90)
3. ✅ Calls `MarkDisplayed()` and `MarkDropped()` (lines 786, 794)
4. ✅ Saves completed trace (lines 790, 798)
5. ✅ Sends X-Prev-* headers in next request

**Why 2% Completed**:
- Last 1-2 frames in queue when scene stops
- These frames never get displayed, remain "Completed"
- This is EXPECTED behavior for tail frames

**Evidence it's the reference**:
- Consecutive frame IDs (no gaps)
- Server timestamps present (proving header transmission works)
- High Displayed percentage

### Mode 2: PoseEstimation ⚠️  PARTIALLY BROKEN

**Status**: 90% Unity timestamps, 0% server timestamps, 96% Displayed, 4% Completed

**What works**:
- ✅ Unity timestamps populated (unity_send_ts, unity_receive_ts, unity_display_ts)
- ✅ Most frames marked as Displayed
- ✅ Delayed telemetry headers code exists (our implementation)

**What's broken**:
- ❌ Server timestamps ALL NaN
- ❌ 4% Completed (more than MultiObjectDetection's 2%)

**Root Cause Analysis**:

**Issue 1: Server Timestamps Missing**

Check if PoseResponse includes server timestamp fields:
```csharp
// Should be at ~line 214-215
public double t_server_recv;
public double t_server_send;
```

Check if response parsing populates trace:
```csharp
// Should be at ~line 613-614
trace.server_receive_ts = (long)(response.t_server_recv * 1000);
trace.server_send_ts = (long)(response.t_server_send * 1000);
```

**Hypothesis**: PoseInferenceRunManager is NOT parsing server timestamps from response.

**Issue 2: More "Completed" Frames**

Possible causes:
- Scene restarted multiple times (frame IDs jump: 116→1, 11→1)
- Tail frames from each session remain "Completed"
- 4% = ~5 frames out of 136 = likely 3 sessions with ~1-2 tail frames each

### Mode 3: Segmentation ❌ COMPLETELY BROKEN

**Status**: 0% ALL timestamps, 100% Completed

**Critical Issues**:

**Issue 1: ALL Timestamps NaN**

This means ONE of:
1. FrameTrace is not being created at all
2. FrameTrace timestamps not being populated
3. FrameTrace not being saved to `m_lastCompletedTrace`
4. Delayed headers not being sent
5. Unity using old compiled code

**Issue 2: 100% Completed**

This means:
- NO frames ever reach Displayed or Dropped state
- OR TryDisplayNewestFrame() is not being called
- OR MarkDisplayed() is not being called

**Evidence from system reminder**:
- Lines 626-638: Delayed telemetry header code EXISTS
- Lines 816-835: TryDisplayNewestFrame code EXISTS (calls MarkDropped, MarkDisplayed)

**Conclusion**: Code exists but is NOT RUNNING. Unity is executing old assembly.

---

## WHY "Completed" APPEARS IN FINAL EXCEL

### The Delayed Telemetry Contract

**Expected flow**:
```
Frame N sent:
  - Create FrameTrace (state=Pending)
  - NO previous frame data to send

Frame N response received:
  - MarkCompleted() → state=Completed
  - Store in m_pendingDisplay queue
  - NOT YET in m_lastCompletedTrace

Update() processes display queue:
  - Call TryDisplayNewestFrame()
  - MarkDisplayed(newest) → state=Displayed
  - MarkDropped(older) → state=Dropped
  - Store in m_lastCompletedTrace

Frame N+1 sent:
  - Read m_lastCompletedTrace
  - Send X-Prev-Final-State = "Displayed" or "Dropped"
  - Server logs Frame N with FINAL state
```

**Actual broken flow**:
```
Frame N sent:
  - Create FrameTrace (state=Pending)
  - NO X-Prev-* headers (first frame)

Frame N response received:
  - MarkCompleted() → state=Completed
  - IMMEDIATELY stored to m_lastCompletedTrace  ← BUG!

Frame N+1 sent:
  - Read m_lastCompletedTrace (state still="Completed")
  - Send X-Prev-Final-State = "Completed"
  - Server logs Frame N with state="Completed"  ← WRONG!

Update() (too late):
  - MarkDisplayed(Frame N) → state=Displayed
  - But already sent to server as "Completed"
```

### Root Cause: Race Condition

**The bug**: `m_lastCompletedTrace` is set in the coroutine response handler, BEFORE Update() has a chance to call TryDisplayNewestFrame().

**Location**: Each manager's response handler (e.g., SegmentationInferenceRunManager ~line 735)

```csharp
// In coroutine after response received:
trace.MarkCompleted(receiveTimestamp);  // state = Completed

// Problem: If m_lastCompletedTrace is set HERE:
m_lastCompletedTrace = trace;  // ← TOO EARLY!

// Then next frame sends:
// X-Prev-Final-State = "Completed"  ← WRONG!
```

**The fix**: ONLY set `m_lastCompletedTrace` in TryDisplayNewestFrame(), AFTER calling MarkDisplayed/MarkDropped.

---

## EXACT FILE/FUNCTION INSPECTION TARGETS

### Unity C# Files to Inspect/Patch

1. **SegmentationInferenceRunManager.cs**
   - **Line 735-750**: Response handler - Check if FrameTrace is being created
   - **Line 743-744**: Check if server timestamps being parsed
   - **Line 746**: Check if MarkCompleted() is called
   - **Line 820, 835**: Check if m_lastCompletedTrace assignment timing
   - **Action**: Verify code is actually running (add Debug.Log)

2. **PoseInferenceRunManager.cs**
   - **Line 613-614**: Check if server timestamp parsing exists
   - **Line 976, 987**: Check m_lastCompletedTrace assignment
   - **Action**: Verify response includes t_server_recv, t_server_send

3. **MultiObjectDetection/SentisInferenceRunManager.cs**
   - **Reference implementation** - Use as template
   - **Line 713-714**: Server timestamp parsing (WORKING)
   - **Line 790, 798**: m_lastCompletedTrace assignment (WORKING)

4. **FrameTrace.cs**
   - **Line 58-60**: Constructor - Verify unity_send_ts set
   - **Line 73-77**: MarkCompleted() - Does NOT set server timestamps
   - **Line 84-88**: MarkDisplayed() - Sets unity_display_ts
   - **Line 94-99**: MarkDropped() - Sets unity_drop_ts
   - **Action**: Verify constructor is called with proper timestamp

### Python Server Files to Inspect/Patch

1. **app/routes/infer_human.py**
   - **Line 828**: CRITICAL BUG - Default "Completed" when header missing
   - **Line 869-878**: Pass delayed telemetry to frame_manager
   - **Action**: Change default to detect missing state properly

2. **debug/frame_state_manager.py**
   - **Line 144-177**: Creates current_frame from delayed headers
   - **Line 257**: Uses current_frame.final_state for logging
   - **Action**: Add validation - reject "Completed" state

3. **debug/inference_logger.py**
   - **Line ~120**: log_inference() - Writes to Excel
   - **Action**: Add filter - skip rows with final_state="Completed"

---

## REQUIRED STATE MACHINE CORRECTION

### Correct State Transitions

```
Pending → Completed   (response received)
Completed → Displayed (rendered in Update())
Completed → Dropped   (superseded in Update())
Pending → Failed      (timeout/error)
```

### State Assignment Rules

| State | When | Who Sets | Where |
|-------|------|----------|-------|
| Pending | Frame created | Constructor | SendInferenceRequest() |
| Completed | Response received | MarkCompleted() | Coroutine handler |
| Displayed | Frame rendered | MarkDisplayed() | TryDisplayNewestFrame() |
| Dropped | Frame superseded | MarkDropped() | TryDisplayNewestFrame() |
| Failed | Timeout/error | MarkFailed() | Error handler |

### Critical Timing Rule

**m_lastCompletedTrace assignment MUST happen AFTER final state determination:**

```csharp
// WRONG (current):
trace.MarkCompleted(time);
m_lastCompletedTrace = trace;  // State is still "Completed"!

// CORRECT:
trace.MarkCompleted(time);
// Wait for Update()...
// In TryDisplayNewestFrame():
trace.MarkDisplayed(time);     // State becomes "Displayed"
m_lastCompletedTrace = trace;  // Now state is final!
```

---

## REQUIRED EXPORTER CORRECTION

### Current Behavior (WRONG)

Excel receives frame data with:
- final_state = "Completed" (from delayed headers)
- All "Completed" frames are logged

### Required Behavior (CORRECT)

**Option A: Filter at Export Time**
```python
# In inference_logger.py log_inference():
if final_state == "Completed":
    print(f"[LOGGER] Skipping frame {frame_id} - state still Completed (not final)")
    return  # Don't write to Excel
```

**Option B: Validate at Receive Time**
```python
# In frame_state_manager.py process_frame():
if final_state == "Completed":
    print(f"[FRAME STATE] WARNING: Frame {prev_frame_id} has non-final state 'Completed'")
    # Option: treat as "Displayed" by default
    final_state = "Displayed"
```

**Option C: Fix at Source (BEST)**

Ensure Unity NEVER sends "Completed" in X-Prev-Final-State:
- Only set m_lastCompletedTrace AFTER MarkDisplayed/MarkDropped
- Remove race condition

---

## REQUIRED TELEMETRY CORRECTION BY MODE

### MultiObjectDetection (Reference - No Changes Needed)

**What it does correctly**:
1. Creates FrameTrace in SendInferenceRequest()
2. Populates server timestamps from response (line 713-714)
3. Calls MarkDisplayed/MarkDropped in TryDisplayNewestFrame()
4. Sets m_lastCompletedTrace ONLY in TryDisplayNewestFrame()

**Recommendation**: Use as template for other modes.

### PoseEstimation (Needs Server Timestamp Fix)

**Required Patch**:

**File**: `Assets/PassthroughCameraApiSamples/PoseEstimation/Scripts/PoseInferenceRunManager.cs`

**Location**: After line 616 (after MarkCompleted call)

**Add**:
```csharp
// Parse server timestamps from response
trace.server_receive_ts = (long)(response.t_server_recv * 1000);  // Convert to ms
trace.server_send_ts = (long)(response.t_server_send * 1000);
```

**Verify**: PoseResponse class has these fields (should be ~line 214-215):
```csharp
public double t_server_recv;
public double t_server_send;
```

### Segmentation (Needs Complete Debug)

**Diagnostic Steps**:

1. **Verify compilation**:
```powershell
# Check Unity Editor log for compilation errors
Get-Content 'C:\Users\user\AppData\Local\Unity\Editor\Editor.log' -Tail 200 | Select-String 'Segmentation|error'
```

2. **Add debug logging** to confirm code is running:

**File**: `SegmentationInferenceRunManager.cs`

**Location**: Line 580 (in SendInferenceRequest)
```csharp
Debug.Log($"[SEG DEBUG] Sending frame {m_frameId}, m_lastCompletedTrace={(m_lastCompletedTrace != null ? m_lastCompletedTrace.frame_id.ToString() : "null")}");
```

**Location**: Line 746 (after MarkCompleted)
```csharp
Debug.Log($"[SEG DEBUG] Frame {trace.frame_id} completed: unity_send_ts={trace.unity_send_ts}, server_recv={trace.server_receive_ts}");
```

**Location**: Line 831 (after MarkDisplayed)
```csharp
Debug.Log($"[SEG DEBUG] Frame {newest.frame_id} displayed: state={newest.state}, display_ts={newest.unity_display_ts}");
```

3. **Force recompilation**:
```powershell
# Delete Unity's compiled assemblies
Remove-Item -Recurse -Force "Library\ScriptAssemblies\*"
# Restart Unity Editor
```

---

## SERVER-SIDE BUG FIX

### Fix #1: Don't Default to "Completed"

**File**: `C:\Repo\Github\vision_server\app\routes\infer_human.py`

**Line 828**:
```python
# BEFORE:
prev_final_state = request.headers.get("X-Prev-Final-State", "Completed")

# AFTER:
prev_final_state = request.headers.get("X-Prev-Final-State", "")
if not prev_final_state or prev_final_state == "Pending":
    # If Unity sent blank or Pending, treat as Displayed (defensive)
    prev_final_state = "Displayed"
elif prev_final_state == "Completed":
    # Log warning - Unity sent non-final state
    print(f"[TELEMETRY WARNING] Frame {prev_frame_id} has non-final state 'Completed'")
    prev_final_state = "Displayed"  # Assume displayed by default
```

### Fix #2: Filter at Logger

**File**: `C:\Repo\Github\vision_server\debug\inference_logger.py`

**Add at start of log_inference()**:
```python
# Reject non-final states
if final_state in ["Completed", "Pending"]:
    print(f"[LOGGER] Skipping frame {frame_id} - non-final state '{final_state}'")
    return
```

---

## SUMMARY OF REQUIRED PATCHES

### Priority 1: Fix "Completed" in Excel

| File | Line | Change | Reason |
|------|------|--------|--------|
| infer_human.py | 828 | Change default from "Completed" to "" | Detect missing state |
| infer_human.py | 828 | Add validation for "Completed" | Reject non-final states |
| inference_logger.py | ~120 | Skip if state="Completed" | Filter at export |

### Priority 2: Fix Segmentation Timestamps

| File | Action | Reason |
|------|--------|--------|
| SegmentationInferenceRunManager.cs | Add debug logs | Verify code is running |
| Unity | Force recompile | Ensure new code loaded |
| SegmentationInferenceRunManager.cs | Verify FrameTrace creation | Check constructor called |

### Priority 3: Fix PoseEstimation Server Timestamps

| File | Line | Change | Reason |
|------|------|--------|--------|
| PoseInferenceRunManager.cs | ~616 | Add server timestamp parsing | Match MultiObjectDetection |

---

## VALIDATION CHECKLIST

After applying patches:

- [ ] NO "Completed" states in Excel final summary
- [ ] All modes have 90%+ Unity timestamps
- [ ] All modes have 90%+ server timestamps
- [ ] Final states are ONLY: Displayed, Dropped, Failed
- [ ] MultiObjectDetection still working (reference)
- [ ] PoseEstimation has server timestamps
- [ ] Segmentation has non-zero timestamps
- [ ] Frame IDs are consecutive (no unexplained gaps)
- [ ] Dropped frames appear when they should
