# Segmentation Telemetry Debug Instructions

**Date**: 2026-04-15
**Status**: Debug Logging Added - Ready to Test
**Priority**: HIGH

## What Was Done

Added comprehensive debug logging to `SegmentationInferenceRunManager.cs` to trace the complete lifecycle of telemetry data.

## Debug Logs Added

### 1. FrameTrace Creation (Line 513)
```
[TELEMETRY DEBUG] Created FrameTrace {id}, unity_send_ts={ts}, session_id={guid}
```
**What to check**: Is unity_send_ts a valid Unix timestamp (large number like 1776279891690)?

### 2. MarkCompleted (Line 753)
```
[TELEMETRY DEBUG] MarkCompleted frame {id}, state={state}, server_recv={ts}, server_send={ts}, unity_recv={ts}
```
**What to check**:
- Are server_recv and server_send valid timestamps?
- Is state = "Completed"?
- Is unity_recv valid?

### 3. Set m_lastCompletedTrace (Line 836)
```
[TELEMETRY DEBUG] Set m_lastCompletedTrace to DISPLAYED frame {id}, state={state}, unity_send_ts={ts}, server_recv={ts}
```
**What to check**:
- Does this appear in the logs?
- Is state = "Displayed"?
- Are the timestamps valid?

### 4. Sending Delayed Headers (Lines 627, 641)
```
[TELEMETRY DEBUG] Sending delayed headers for frame {id}, state={state}, unity_send_ts={ts}, server_recv={ts}
```
OR
```
[TELEMETRY DEBUG] Frame {id}: m_lastCompletedTrace is NULL, NOT sending delayed headers
```
**What to check**:
- Which message appears?
- If sending headers, are the timestamps valid?

## Testing Procedure

### Step 1: Rebuild Unity
```
1. In Unity Editor
2. Assets > Reimport All (to force recompilation)
3. Wait for compilation to complete (no errors)
```

### Step 2: Run Segmentation Mode
```
1. Open Segmentation.unity scene
2. Enter Play Mode
3. Send 5-10 frames
4. Exit Play Mode
```

### Step 3: Analyze Unity Console

**Look for this sequence**:

```
[TELEMETRY DEBUG] Created FrameTrace 1, unity_send_ts=1776279891690, session_id=<guid>
[SERVER SEND] >>> Sending frame 1 to: http://...
[TELEMETRY DEBUG] Frame 1: m_lastCompletedTrace is NULL, NOT sending delayed headers  <-- Frame 1 should show this

[TELEMETRY DEBUG] MarkCompleted frame 1, state=Completed, server_recv=1776279891390, server_send=1776279891480, unity_recv=1776279891842
[TELEMETRY DEBUG] Set m_lastCompletedTrace to DISPLAYED frame 1, state=Displayed, unity_send_ts=1776279891690, server_recv=1776279891390

[TELEMETRY DEBUG] Created FrameTrace 2, unity_send_ts=1776279892106, session_id=<guid>
[SERVER SEND] >>> Sending frame 2 to: http://...
[TELEMETRY DEBUG] Sending delayed headers for frame 1, state=Displayed, unity_send_ts=1776279891690, server_recv=1776279891390  <-- Frame 2 should send Frame 1's data

[TELEMETRY DEBUG] MarkCompleted frame 2, state=Completed, server_recv=1776279891595, server_send=1776279891686, unity_recv=1776279892050
[TELEMETRY DEBUG] Set m_lastCompletedTrace to DISPLAYED frame 2, state=Displayed, unity_send_ts=1776279892106, server_recv=1776279891595
```

## Diagnostic Scenarios

### Scenario A: All Timestamps are 0
**Symptoms**:
```
[TELEMETRY DEBUG] Created FrameTrace 1, unity_send_ts=0, session_id=<guid>
```

**Diagnosis**: TimestampUtil.GetUnixTimestampMs() is broken
**Fix**: Check TimestampUtil.cs implementation

### Scenario B: m_lastCompletedTrace is Always NULL
**Symptoms**:
```
[TELEMETRY DEBUG] Frame 2: m_lastCompletedTrace is NULL, NOT sending delayed headers
[TELEMETRY DEBUG] Frame 3: m_lastCompletedTrace is NULL, NOT sending delayed headers
```

**Diagnosis**: TryDisplayNewestFrame() is not executing or not reaching line 835
**Fix**: Add breakpoint in TryDisplayNewestFrame(), check if it's called

### Scenario C: Timestamps Valid but Headers Not Sent
**Symptoms**:
```
[TELEMETRY DEBUG] Created FrameTrace 1, unity_send_ts=1776279891690, session_id=<guid>  ✅
[TELEMETRY DEBUG] MarkCompleted frame 1, state=Completed, server_recv=1776279891390, ...  ✅
[TELEMETRY DEBUG] Set m_lastCompletedTrace to DISPLAYED frame 1, state=Displayed, ...  ✅
[TELEMETRY DEBUG] Frame 2: m_lastCompletedTrace is NULL, NOT sending delayed headers  ❌
```

**Diagnosis**: m_lastCompletedTrace is being reset to null somewhere
**Fix**: Check for any code that sets m_lastCompletedTrace = null

### Scenario D: No Debug Logs at All
**Symptoms**: No [TELEMETRY DEBUG] messages in console

**Diagnosis**: Code not compiled or wrong scene
**Fix**:
1. Check Console filters (enable Warnings)
2. Verify you're in Segmentation.unity scene
3. Force recompile (delete Library/ folder)

## Expected vs Actual Comparison

### Expected Timeline (Frame 2):
1. **Before Request**: Frame 1 already Displayed, saved in m_lastCompletedTrace
2. **Request Start**: m_lastCompletedTrace != null, send Frame 1's data in headers
3. **Response**: Frame 2 marked Completed
4. **Update()**: Frame 2 marked Displayed, saved to m_lastCompletedTrace
5. **Next Request (Frame 3)**: m_lastCompletedTrace has Frame 2's data

### Actual Timeline (if broken):
1. **Before Request**: m_lastCompletedTrace is NULL
2. **Request Start**: No headers sent (using server defaults)
3. **Response**: Frame data lost
4. **Excel**: All NaN timestamps

## Quick Verification Commands

### Check if debug logs exist in Unity log file:
```powershell
Get-Content "C:\Users\user\AppData\Local\Unity\Editor\Editor.log" -Tail 200 | Select-String "TELEMETRY DEBUG"
```

### Count how many frames were created:
```powershell
Get-Content "C:\Users\user\AppData\Local\Unity\Editor\Editor.log" | Select-String "Created FrameTrace" | Measure-Object
```

### Check if m_lastCompletedTrace was ever set:
```powershell
Get-Content "C:\Users\user\AppData\Local\Unity\Editor\Editor.log" | Select-String "Set m_lastCompletedTrace"
```

## Next Steps After Testing

1. **Share Console Output**: Copy all [TELEMETRY DEBUG] messages
2. **Identify Pattern**: Which scenario (A, B, C, or D) matches?
3. **Apply Targeted Fix**: Based on diagnosis
4. **Retest**: Verify fix works
5. **Remove Debug Logs**: Clean up after confirmation

## Success Criteria

When working correctly, you should see:
```
Frame 1: m_lastCompletedTrace is NULL (expected for first frame)
Frame 2: Sending delayed headers for frame 1
Frame 3: Sending delayed headers for frame 2
Frame 4: Sending delayed headers for frame 3
...
```

And in Excel:
- Frame 1: May have NaN (no previous frame)
- Frame 2+: All timestamps populated
- final_state: Displayed (not Completed)

## Files Modified

- `Assets/PassthroughCameraApiSamples/Segmentation/SegmentationInference/Scripts/SegmentationInferenceRunManager.cs`
  - Line 513: FrameTrace creation log
  - Line 627: Delayed headers send log
  - Line 641: NULL check log
  - Line 753: MarkCompleted log
  - Line 836: Set m_lastCompletedTrace log

## Cleanup After Debug

Once issue is identified and fixed, remove these debug lines:
- Line 513: Remove Debug.LogWarning
- Line 627: Remove Debug.LogWarning
- Line 641: Remove entire else block
- Line 753: Remove Debug.LogWarning
- Line 836: Remove Debug.LogWarning
