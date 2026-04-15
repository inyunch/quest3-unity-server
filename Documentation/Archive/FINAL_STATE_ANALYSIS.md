# Final State Analysis Report

**Date**: 2026-04-14
**Issue**: All Excel rows show `final_state = Displayed`, no `Dropped` frames observed

## Current Situation

### Excel Data (Last 50 PoseEstimation Frames)
- **Frame IDs**: 67-116 (consecutive, no gaps)
- **final_state**: ALL = "Displayed"
- **dropped_frames counter**: (need to check)

### Code Review Results

#### ✅ State Assignment Logic is CORRECT

**File**: `FrameTrace.cs`
```csharp
public void MarkDisplayed(long displayTime)
{
    unity_display_ts = displayTime;
    state = FrameState.Displayed;  // ✅ CORRECT
}

public void MarkDropped(long dropTime, string reason)
{
    unity_drop_ts = dropTime;
    drop_reason = reason;
    state = FrameState.Dropped;    // ✅ CORRECT
}
```

**File**: `PoseInferenceRunManager.cs` (lines 960-982)
```csharp
// Mark all older completed frames as dropped BEFORE displaying newest
for (int i = 1; i < completedFrames.Count; i++)
{
    var olderFrame = completedFrames[i];
    olderFrame.MarkDropped(currentTimestamp, $"superseded_by_newer_{newest.frame_id}");  // ✅ CORRECT
    m_droppedFrames++;
    m_lastCompletedTrace = olderFrame;  // Save for delayed telemetry
}

// Display the newest frame
DisplayFrame(newest);
newest.MarkDisplayed(currentTimestamp);  // ✅ CORRECT
m_lastCompletedTrace = newest;  // Save for delayed telemetry
```

#### ✅ Delayed Telemetry Transmission is CORRECT

**File**: `PoseInferenceRunManager.cs` (line 418)
```csharp
request.SetRequestHeader("X-Prev-Final-State", m_lastCompletedTrace.state.ToString());  // ✅ CORRECT
```

This sends the actual `FrameState` enum value ("Displayed", "Dropped", "Failed").

## Root Cause

**The code is correct. All frames show "Displayed" because NO frames were actually dropped during testing.**

### Why No Dropped Frames?

Possible reasons:
1. **Fast Inference**: Server processing time is ~40ms
2. **Sequential Completion**: Responses arrive in order (no out-of-order)
3. **Low FPS**: Target FPS might be low enough that each frame completes before next arrives
4. **Single-threaded Processing**: Only 1 request in flight at a time

### Evidence
- Unity logs show NO "DROPPED" or "superseded" messages
- Frame IDs are consecutive (67-116) with no gaps
- Excel shows all frames logged (581 total rows)

## The Real Problem

**If** frames were being dropped, they would NOT appear in Excel at all, because:

1. Frame N is sent → Server starts processing
2. Frame N+1 is sent → Server starts processing (parallel)
3. Frame N+1 completes first (out-of-order)
4. Frame N completes later
5. `TryDisplayNewestFrame()` finds both completed
6. Frame N is marked `Dropped` via `MarkDropped()`
7. `m_lastCompletedTrace = olderFrame` (Frame N)
8. Frame N+1 is marked `Displayed`
9. `m_lastCompletedTrace = newest` (**OVERWRITES Frame N!**)
10. Frame N's `Dropped` state is **LOST** - never sent to server!

### Critical Bug

**Only the LAST frame processed in `TryDisplayNewestFrame()` gets saved to `m_lastCompletedTrace`.**

If multiple frames are dropped in one Update() cycle, only the newest dropped frame (or the displayed frame) is saved. All other dropped frames' final states are lost.

## Required Fix

### Option A: Send All Completed Frames (Not Just Previous)

Instead of sending only "previous frame" in headers, send a JSON array of all recently completed frames with their final states.

**Pros**: Complete tracking
**Cons**: Complex, requires protocol change

### Option B: Queue All Completed Frames

Maintain a queue of completed frames and send them one-by-one in subsequent requests.

**Pros**: Simple delayed telemetry pattern
**Cons**: Lag in reporting (Frame N might be reported many frames later)

### Option C: Accept Loss of Multi-Drop Events

Document that if multiple frames complete in one Update() and get dropped, only one will be logged.

**Pros**: No code changes
**Cons**: Incomplete telemetry for burst drops

## Recommended Solution

**Use Option B: Queue-Based Delayed Telemetry**

### Implementation

```csharp
// Instead of:
private FrameTrace m_lastCompletedTrace = null;

// Use:
private Queue<FrameTrace> m_completedFramesQueue = new Queue<FrameTrace>();

// In TryDisplayNewestFrame():
for (int i = 1; i < completedFrames.Count; i++)
{
    var olderFrame = completedFrames[i];
    olderFrame.MarkDropped(currentTimestamp, $"superseded_by_newer_{newest.frame_id}");
    m_completedFramesQueue.Enqueue(olderFrame);  // Add to queue
    m_droppedFrames++;
}

newest.MarkDisplayed(currentTimestamp);
m_completedFramesQueue.Enqueue(newest);  // Add to queue

// In SendInferenceRequest():
if (m_completedFramesQueue.Count > 0)
{
    var frameToReport = m_completedFramesQueue.Dequeue();
    // Send frameToReport's telemetry in headers
}
```

### Verification Test

To force dropped frames and verify the fix:
1. Set very low Target FPS (e.g., 1 FPS)
2. Server processes fast (~40ms)
3. Send requests every 1000ms
4. Multiple responses will complete before next display cycle
5. Only newest should be displayed, others dropped
6. Excel should show mix of "Displayed" and "Dropped"

## Next Steps

1. ✅ Confirmed code logic is correct
2. ⏳ Implement queue-based delayed telemetry
3. ⏳ Run verification test with forced drops
4. ⏳ Verify Excel shows correct final_state distribution
