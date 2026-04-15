# Queue-Based Delayed Telemetry Implementation

**Date**: 2026-04-14
**Status**: ✅ IMPLEMENTATION COMPLETE - READY FOR TESTING

## Problem Summary

### Original Issue
Using `m_lastCompletedTrace` (single variable) to store the previous frame meant that when multiple frames completed in one Update() cycle, only the last frame's final state was reported to the server.

**Example Scenario**:
1. Frame 100, 101, 102 all complete in same Update()
2. Frame 102 is newest → displayed
3. Frame 100, 101 marked as dropped
4. But `m_lastCompletedTrace` is overwritten 3 times:
   - First: Frame 100 (dropped)
   - Then: Frame 101 (dropped) - **Frame 100 lost!**
   - Finally: Frame 102 (displayed) - **Frame 101 lost!**
5. Only Frame 102's state is ever reported
6. Excel shows all frames as "Displayed" (missing the dropped ones)

## Solution: Queue-Based Delayed Telemetry

Replace single variable with a Queue to store ALL completed frames and report them one-by-one in subsequent requests.

### Architecture

```
Update() Cycle N:
  - Frames [100, 101, 102] complete
  - Mark 100, 101 as Dropped → Enqueue(100), Enqueue(101)
  - Mark 102 as Displayed → Enqueue(102)
  - Queue now contains: [100, 101, 102]

SendInferenceRequest() for Frame 103:
  - Dequeue() → Frame 100
  - Send Frame 100's telemetry in X-Prev-* headers
  - Queue now contains: [101, 102]

SendInferenceRequest() for Frame 104:
  - Dequeue() → Frame 101
  - Send Frame 101's telemetry
  - Queue now contains: [102]

SendInferenceRequest() for Frame 105:
  - Dequeue() → Frame 102
  - Send Frame 102's telemetry
  - Queue now empty
```

## Implementation Changes

### All Three Managers Updated

1. **PoseInferenceRunManager.cs**
2. **SentisInferenceRunManager.cs**
3. **SegmentationInferenceRunManager.cs**

### Change 1: Replace Single Variable with Queue

**Before**:
```csharp
private FrameTrace m_lastCompletedTrace = null;
```

**After**:
```csharp
private Queue<FrameTrace> m_completedFramesQueue = new Queue<FrameTrace>();
```

### Change 2: Dequeue in SendInferenceRequest()

**Before (PoseInferenceRunManager.cs ~line 410)**:
```csharp
if (m_lastCompletedTrace != null)
{
    var frameToReport = m_lastCompletedTrace;
    request.SetRequestHeader("X-Prev-Frame-Id", frameToReport.frame_id.ToString());
    // ... 10 more headers
}
```

**After**:
```csharp
if (m_completedFramesQueue.Count > 0)
{
    var frameToReport = m_completedFramesQueue.Dequeue();
    request.SetRequestHeader("X-Prev-Frame-Id", frameToReport.frame_id.ToString());
    // ... 10 more headers

    Debug.Log($"[DELAYED TEL] Frame {m_frameId} reporting completed frame {frameToReport.frame_id}: " +
              $"state={frameToReport.state}, queue_remaining={m_completedFramesQueue.Count}");
}
```

### Change 3: Enqueue in TryDisplayNewestFrame()

**Before (PoseInferenceRunManager.cs ~line 970)**:
```csharp
for (int i = 1; i < completedFrames.Count; i++)
{
    var olderFrame = completedFrames[i];
    olderFrame.MarkDropped(currentTimestamp, $"superseded_by_newer_{newest.frame_id}");
    m_droppedFrames++;
    m_lastCompletedTrace = olderFrame;  // OVERWRITES previous!
}

newest.MarkDisplayed(currentTimestamp);
m_lastCompletedTrace = newest;  // OVERWRITES dropped frames!
```

**After**:
```csharp
for (int i = 1; i < completedFrames.Count; i++)
{
    var olderFrame = completedFrames[i];
    olderFrame.MarkDropped(currentTimestamp, $"superseded_by_newer_{newest.frame_id}");
    m_droppedFrames++;
    m_completedFramesQueue.Enqueue(olderFrame);  // ALL dropped frames saved!
}

newest.MarkDisplayed(currentTimestamp);
m_completedFramesQueue.Enqueue(newest);  // Displayed frame also saved!
```

## Expected Behavior After Fix

### Scenario: 3 Frames Complete, 2 Dropped

**Frame Lifecycle**:
1. Frame 100: Sent → Completed → **Dropped** (superseded by 102)
2. Frame 101: Sent → Completed → **Dropped** (superseded by 102)
3. Frame 102: Sent → Completed → **Displayed**

**Queue Evolution**:
```
Update() at T=1000ms:
  Queue: [100(Dropped), 101(Dropped), 102(Displayed)]

Frame 103 sent at T=1100ms:
  Reports Frame 100 via X-Prev-* headers
  Queue: [101(Dropped), 102(Displayed)]

Frame 104 sent at T=1200ms:
  Reports Frame 101 via X-Prev-* headers
  Queue: [102(Displayed)]

Frame 105 sent at T=1300ms:
  Reports Frame 102 via X-Prev-* headers
  Queue: []
```

**Excel Result**:
| frame_id | final_state | drop_reason |
|----------|-------------|-------------|
| 100 | Dropped | superseded_by_newer_102 |
| 101 | Dropped | superseded_by_newer_102 |
| 102 | Displayed | (empty) |
| 103 | Displayed | (empty) |
| 104 | Displayed | (empty) |
| 105 | Displayed | (empty) |

Note: Frame 103-105 are displayed because no newer frame superseded them.

## Testing Instructions

### Test 1: Verify Queue Logging

Run any scene and check Unity console for `[DELAYED TEL]` messages:
```
[DELAYED TEL] Frame 5 reporting completed frame 3: state=Displayed, queue_remaining=2
[DELAYED TEL] Frame 6 reporting completed frame 4: state=Displayed, queue_remaining=1
[DELAYED TEL] Frame 7 reporting completed frame 5: state=Displayed, queue_remaining=0
```

### Test 2: Force Dropped Frames

**Method 1: Set Very Low FPS**
1. Open Unity scene settings
2. Set `Target FPS = 1` (1 frame per second)
3. Server will process ~40ms, so multiple responses complete per display cycle
4. Expect to see dropped frames in console:
   ```
   [PARALLEL DISPLAY] Frame 5 DROPPED (superseded by 8)
   [PARALLEL DISPLAY] Frame 6 DROPPED (superseded by 8)
   [PARALLEL DISPLAY] Frame 7 DROPPED (superseded by 8)
   [PARALLEL DISPLAY] Frame 8 DISPLAYED. Dropped 3 older frames.
   ```

**Method 2: Add Artificial Delay**
Temporarily add delay in `DisplayFrame()`:
```csharp
private void DisplayFrame(FrameTrace trace)
{
    System.Threading.Thread.Sleep(200);  // Force 200ms delay
    // ... rest of method
}
```

### Test 3: Verify Excel Has Dropped States

After running test with forced drops:
```powershell
cd C:\Repo\Github\vision_server
python check_telemetry.py
```

Expected output:
```
[VALUE STATISTICS - Last 100 Rows]
  final_state: 100/100 non-zero (100.0%) - sample: ['Displayed', 'Dropped', 'Displayed']
  drop_reason: 30/100 non-zero (30.0%) - sample: ['superseded_by_newer_45', '', '']
```

## Validation Criteria

✅ **Success Criteria**:
1. Excel shows mix of "Displayed" and "Dropped" states (not all Displayed)
2. `drop_reason` is populated for Dropped frames
3. `drop_reason` is empty for Displayed frames
4. No frame IDs are missing from Excel (all frames logged)
5. Unity console shows `[DELAYED TEL]` messages with decreasing queue counts

❌ **Failure Indicators**:
1. ALL frames still show "Displayed"
2. `drop_reason` is always empty
3. Frame ID gaps in Excel
4. No `[DELAYED TEL]` messages in console
5. Compilation errors in Unity

## Known Limitations

### Reporting Lag
Completed frames are reported in subsequent requests, creating a variable lag:
- If queue has 10 frames, Frame 100's data appears when Frame 110 is sent
- This is acceptable because we prioritize **completeness** over **immediacy**

### Queue Growth
If display stops but inference continues, queue will grow unbounded:
- **Mitigation**: Monitor `queue_remaining` in logs
- **Future**: Add max queue size (e.g., 100) with overflow warning

### First Frame
First sent frame has no previous frame to report:
- Frame 1: No X-Prev-* headers sent (queue empty)
- Frame 2: Reports Frame 1's final state

## Files Modified

### Unity C# (3 files)
1. `Assets/PassthroughCameraApiSamples/PoseEstimation/Scripts/PoseInferenceRunManager.cs`
   - Line 76: Changed to Queue
   - Line 410-426: Dequeue logic
   - Line 976, 987: Enqueue logic

2. `Assets/PassthroughCameraApiSamples/MultiObjectDetection/SentisInference/Scripts/SentisInferenceRunManager.cs`
   - Line 91: Changed to Queue
   - Line 597-613: Dequeue logic
   - Line 790, 798: Enqueue logic

3. `Assets/PassthroughCameraApiSamples/Segmentation/SegmentationInference/Scripts/SegmentationInferenceRunManager.cs`
   - Line 88: Changed to Queue
   - Line 626-642: Dequeue logic
   - Line 820, 835: Enqueue logic

### Python Server (0 files)
No server changes needed - existing delayed telemetry infrastructure remains unchanged.

## Rollback Plan

If issues occur, revert by changing:
```csharp
// Revert to single variable
private FrameTrace m_lastCompletedTrace = null;

// Revert dequeue
if (m_lastCompletedTrace != null)
{
    var frameToReport = m_lastCompletedTrace;
    // ...
}

// Revert enqueue to assignment
m_lastCompletedTrace = olderFrame;  // In loop
m_lastCompletedTrace = newest;      // After display
```

## Next Steps

1. ✅ Implementation complete
2. ⏳ Unity compilation (auto-recompile on save)
3. ⏳ Run Test 1 (verify queue logging)
4. ⏳ Run Test 2 (force dropped frames)
5. ⏳ Run Test 3 (verify Excel has Dropped states)
6. ⏳ User validation
