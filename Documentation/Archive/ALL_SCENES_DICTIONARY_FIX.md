# All Scenes Updated - Dictionary-Based N+1 Telemetry

**Date**: 2026-04-17
**Status**: ✅ **ALL 3 SCENES UPDATED**

---

## Summary

Updated **all 3 scenes** to use the Dictionary-based N+1 telemetry architecture:
1. ✅ **MultiObjectDetection**
2. ✅ **PoseEstimation**
3. ✅ **Segmentation**

All scenes now use:
- Frame arithmetic (`currentFrameId - 1`)
- Dictionary lookup (`m_frameTraces.TryGetValue`)
- Send-once flag (`telemetry_sent`)
- **No queue operations**

---

## Files Modified

### 1. FrameTrace.cs (Shared)
- Added `telemetry_sent` flag

### 2. MultiObjectDetection/SentisInferenceRunManager.cs
- `SendFrameUDP()`: Dictionary-based telemetry selection
- `BuildTelemetryJson(FrameTrace)`: Renamed from `GetPreviousFrameTelemetryJson()`
- Removed queue enqueue operations

### 3. PoseEstimation/PoseInferenceRunManager.cs
- `SendFrameUDP()`: Dictionary-based telemetry selection
- `BuildTelemetryJson(FrameTrace)`: Renamed from `GetPreviousFrameTelemetryJson()`
- Added `scene = "PoseEstimation"` field

### 4. Segmentation/SegmentationInferenceRunManager.cs
- `SendFrameUDP()`: Dictionary-based telemetry selection
- `BuildTelemetryJson(FrameTrace)`: Renamed from `GetPreviousFrameTelemetryJson()`
- Added `scene = "Segmentation"` field

---

## Implementation Pattern (Same for All Scenes)

```csharp
private void SendFrameUDP(FrameTrace trace, byte[] jpegData)
{
    // ... get server IP ...

    // N+1 delayed telemetry: Get telemetry for previous frame
    int prevFrameId = trace.frame_id - 1;
    string prevTelemetryJson = null;

    lock (m_frameTracesLock)
    {
        // Check if previous frame exists and is ready to send
        if (prevFrameId > 0 && m_frameTraces.TryGetValue(prevFrameId, out var prevTrace))
        {
            bool isFinalState = (prevTrace.state == FrameState.Displayed ||
                                prevTrace.state == FrameState.Dropped ||
                                prevTrace.state == FrameState.Failed);

            if (isFinalState && !prevTrace.telemetry_sent)
            {
                prevTelemetryJson = BuildTelemetryJson(prevTrace);
                prevTrace.telemetry_sent = true;  // Prevent re-send
            }
        }
    }

    UDPTransport.SendFrame(..., prevTelemetryJson);
}

private string BuildTelemetryJson(FrameTrace trace)
{
    // Build JSON for THIS specific trace
    // Include scene name for identification
}
```

---

## Expected Behavior (All Scenes)

### Unity Logs
```
[UNITY TELEMETRY] Sending trace for frame 1, session=..., final_state=Displayed
[UNITY TELEMETRY] Sending trace for frame 2, session=..., final_state=Displayed
[UNITY TELEMETRY] Sending trace for frame 3, session=..., final_state=Displayed
```

### Server Logs
```
[UDP EXCEL] Frame 2 carries telemetry for frame 1
[UDP EXCEL] Logged frame 1 (final_state=Displayed)
[UDP EXCEL] Frame 3 carries telemetry for frame 2
[UDP EXCEL] Logged frame 2 (final_state=Displayed)
[UDP EXCEL] Frame 4 carries telemetry for frame 3
[UDP EXCEL] Logged frame 3 (final_state=Displayed)
```

### Excel File
- Column B (scene): "MultiObjectDetection" or "PoseEstimation" or "Segmentation"
- Column D (frame_id): 1, 2, 3, 4, 5, ... (**sequential, no duplicates**)

---

## Testing Checklist

- [ ] **Build Unity** with all 3 scenes updated
- [ ] **Deploy to Quest 3**
- [ ] **Test MultiObjectDetection scene**:
  - Check Excel: frame_ids increment (1, 2, 3, ...)
  - Check scene column = "MultiObjectDetection"
- [ ] **Test PoseEstimation scene**:
  - Check Excel: frame_ids increment (1, 2, 3, ...)
  - Check scene column = "PoseEstimation"
- [ ] **Test Segmentation scene**:
  - Check Excel: frame_ids increment (1, 2, 3, ...)
  - Check scene column = "Segmentation"
  - Verify segmentation mask rendering works correctly

---

## Segmentation Scene - Additional Question

User asked: "另外segmentation的模式有換成新的傳輸模式和正常渲染嗎?"

### Answer

是的，Segmentation場景已經更新：

1. ✅ **新傳輸模式**: 已使用Dictionary-based N+1 telemetry
2. ✅ **UDP transport**: SendFrameUDP() 已更新
3. ❓ **正常渲染**: 需要檢查`SegmentationOverlayRenderer`是否正常工作

Segmentation場景的渲染流程：
- `SegmentationInferenceRunManager.cs` - 管理推理和UDP傳輸
- `SegmentationOverlayRenderer.cs` - 渲染分割遮罩
- `QuestDepthCaptureManager.cs` - 捕獲深度數據

如果渲染有問題，可能需要檢查：
1. SegmentationOverlayRenderer是否正確接收推理結果
2. Shader是否正確渲染遮罩
3. Quest native depth API是否正常工作

---

**Last Updated**: 2026-04-17 05:30 UTC
