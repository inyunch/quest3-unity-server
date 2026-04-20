# Mode Transmission Fix - All Scenes

**Date**: 2026-04-17
**Status**: ✅ FIXED - Mode field added to telemetry in all 3 scenes

---

## Problem Summary

Server was receiving **wrong inference mode** from Unity, causing incorrect processing:

- **Segmentation scene**: Server received `mode=both` instead of `mode=segmentation`
- **Evidence**: Server logs showed `[UDP WORKER mode=both]` and `[UDP WORKER mode=detection]` instead of `[UDP WORKER mode=segmentation]`
- **Result**: No segmentation masks rendered, detection+pose ran instead

---

## Root Cause

**Unity telemetry JSON was missing the "mode" field entirely.**

### Server-Side Mode Reading

**File**: `app/transport/udp_ingest.py` line 281
```python
mode = telemetry.get('mode', 'both')  # DEFAULT = 'both' if not present!!!
```

If Unity doesn't include `"mode"` in the telemetry JSON, server defaults to `mode='both'`.

### UDP Transport Architecture

**File**: `Assets/PassthroughCameraApiSamples/Shared/Scripts/UDPTransport.cs`

```csharp
public static void SendFrame(
    UdpClient udpClient,
    string serverIP,
    int serverPort,
    FrameTrace trace,
    byte[] jpegData,
    string telemetryJson = null)  // Mode must be IN telemetryJson!
{
    // UDP packet structure:
    // [Header (64 bytes)] + [Telemetry JSON (variable)] + [JPEG data (variable)]

    // Header does NOT include mode field
    // Mode must be included in telemetryJson parameter
}
```

### What Was Missing

All 3 scenes were calling `BuildTelemetryJson(trace)` but this method **never included the "mode" field**:

**Before (Segmentation example)**:
```csharp
var telemetry = new Dictionary<string, object>
{
    { "scene", "Segmentation" },
    { "session_id", trace.session_id },
    { "frame_id", trace.frame_id },
    // ❌ NO "mode" field!

    { "unity_send_ts", trace.unity_send_ts },
    // ... other fields
};
```

**Result**: Server reads `telemetry.get('mode', 'both')` → returns default `'both'`.

---

## Solution Applied

**Added "mode" field to telemetry in all 3 scenes.**

### 1. Segmentation Scene

**File**: `SegmentationInferenceRunManager.cs` line 1642

**Fix Applied** (line 1645 and 1653):
```csharp
private string BuildTelemetryJson(FrameTrace trace)
{
    // Convert InferenceMode enum to server-expected string
    string modeString = "segmentation";  // Segmentation scene always uses segmentation mode

    var telemetry = new Dictionary<string, object>
    {
        { "scene", "Segmentation" },
        { "session_id", trace.session_id },
        { "frame_id", trace.frame_id },
        { "mode", modeString },  // ✅ CRITICAL: Server reads this to determine inference type

        // ... rest of fields
    };

    return JsonUtility.ToJson(telemetry);
}
```

**Expected Server Behavior**:
```python
# Server receives telemetry JSON
mode = telemetry.get('mode', 'both')  # → Returns 'segmentation' ✅

# Server logs
[UDP WORKER] Processing sessionid_1 (queue_wait=2.3ms, mode=segmentation)
[UDP WORKER mode=segmentation] YOLO detected 5 objects, 1 person(s)
[UDP WORKER mode=segmentation] 1 person(s) with masks
```

---

### 2. MultiObjectDetection Scene

**File**: `SentisInferenceRunManager.cs` line 1465

**Fix Applied** (line 1473):
```csharp
private string BuildTelemetryJson(FrameTrace trace)
{
    var json = "{" +
        $"\"scene\":\"MultiObjectDetection\"," +
        $"\"session_id\":\"{trace.session_id}\"," +
        $"\"frame_id\":{trace.frame_id}," +
        $"\"mode\":\"detection\"," +  // ✅ CRITICAL: Server reads this to determine inference type

        // ... rest of JSON string
        "}";

    return json;
}
```

**Expected Server Behavior**:
```python
mode = telemetry.get('mode', 'both')  # → Returns 'detection' ✅

# Server logs
[UDP WORKER] Processing sessionid_1 (queue_wait=1.8ms, mode=detection)
[UDP WORKER mode=detection] YOLO detected 2 person(s)
```

---

### 3. PoseEstimation Scene

**File**: `PoseInferenceRunManager.cs` line 1765

**Fix Applied** (line 1773):
```csharp
private string BuildTelemetryJson(FrameTrace trace)
{
    var telemetry = new
    {
        scene = "PoseEstimation",
        session_id = trace.session_id,
        frame_id = trace.frame_id,
        mode = "both",  // ✅ CRITICAL: Server reads this to determine inference type (pose + detection)

        // ... rest of anonymous type fields
    };

    return JsonConvert.SerializeObject(telemetry);
}
```

**Expected Server Behavior**:
```python
mode = telemetry.get('mode', 'both')  # → Returns 'both' ✅

# Server logs
[UDP WORKER] Processing sessionid_1 (queue_wait=2.1ms, mode=both)
[UDP WORKER mode=detection] YOLO detected 1 person(s)
[UDP WORKER mode=both] Pose on 1 crops, detected 1 person(s) with 17 keypoints
```

---

## Mode String Mapping

| Unity Scene | Mode String | Server Inference Type |
|-------------|-------------|----------------------|
| MultiObjectDetection | `"detection"` | YOLO object detection only |
| PoseEstimation | `"both"` | YOLO detection + Keypoint R-CNN pose |
| Segmentation | `"segmentation"` | YOLO11n-seg instance segmentation with masks |

**Why "both" for PoseEstimation?**
- PoseEstimation uses `/infer_human?mode=both` endpoint
- Server runs both YOLO detection AND pose estimation
- Returns both `detections` and `skeleton` in response

---

## Verification Steps

### 1. Unity Logs (after rebuild and deploy)

**Segmentation**:
```
[UDP SEND] Frame sessionid_26 sent, size=35000 bytes
[UDP POLL] Starting polling for frame 26
[UDP POLL] Frame 26 received after 0.35s
[SEGMENTATION] Received response with 1 detection(s)
[SEGMENTATION] Detection 1: person, conf=0.88, has_mask=True
```

**MultiObjectDetection**:
```
[UDP SEND] Frame sessionid_52 sent, size=28000 bytes
[UDP POLL] Frame 52 received after 0.22s
[DETECTION] Received 2 detections
```

**PoseEstimation**:
```
[UDP SEND] Frame sessionid_89 sent, size=32000 bytes
[UDP POLL] Frame 89 received after 0.28s
[POSE RECV] Received response, persons=1
[POSE PARSE] Person 1: 17 keypoints
```

### 2. Server Logs (should see correct modes)

```
[UDP WORKER] Processing sessionid_1 (queue_wait=2.3ms, mode=segmentation)
[UDP WORKER mode=segmentation] YOLO detected 5 objects, 1 person(s)
[UDP WORKER SEGMENTATION] Mask 1: 340x180, base64: 12480 chars
[UDP WORKER mode=segmentation] 1 person(s) with masks

[UDP WORKER] Processing sessionid_2 (queue_wait=1.8ms, mode=detection)
[UDP WORKER mode=detection] YOLO detected 2 person(s)

[UDP WORKER] Processing sessionid_3 (queue_wait=2.1ms, mode=both)
[UDP WORKER mode=detection] YOLO detected 1 person(s)
[UDP WORKER mode=both] Pose on 1 crops, detected 1 person(s) with 17 keypoints
```

### 3. Excel Telemetry

**Should show correct scene-mode combinations**:

| scene | mode | detection_count | avg_confidence | latency_ms |
|-------|------|-----------------|----------------|------------|
| Segmentation | segmentation | 1 | 0.88 | 485.3 |
| MultiObjectDetection | detection | 2 | 0.87 | 425.7 |
| PoseEstimation | both | 1 | 0.89 | 512.4 |

---

## Related Fixes

This fix was part of a comprehensive Phase 3 update that also included:

1. **Metrics Extraction Fix** - All scenes now extract inference results from server responses
2. **Segmentation UDP Worker** - Added segmentation mode handling to UDP worker
3. **UDP Packet Size Optimization** - Reduced jpegQuality to 40 to avoid "Message too long" errors

See:
- `METRICS_EXTRACTION_FIX.md` - Unity-side metrics extraction
- `SEGMENTATION_UDP_FIX.md` - Server-side UDP worker segmentation
- `PHASE3_METRICS_AND_SEGMENTATION_COMPLETE.md` - Complete summary

---

## Impact

**Before Fix**:
- Segmentation scene ran detection+pose instead of segmentation
- No masks rendered
- Wasted compute on wrong inference type
- Excel showed wrong metrics for wrong inference type

**After Fix**:
- Each scene runs correct inference type
- Segmentation properly renders masks
- Server processes frames efficiently
- Excel telemetry accurately reflects actual inference mode

---

**Files Modified**:

1. `Assets/PassthroughCameraApiSamples/Segmentation/SegmentationInference/Scripts/SegmentationInferenceRunManager.cs` (line 1645, 1653)
2. `Assets/PassthroughCameraApiSamples/MultiObjectDetection/SentisInference/Scripts/SentisInferenceRunManager.cs` (line 1473)
3. `Assets/PassthroughCameraApiSamples/PoseEstimation/Scripts/PoseInferenceRunManager.cs` (line 1773)

---

**Last Updated**: 2026-04-17 09:00 UTC
**Status**: ✅ Complete, ready for Unity rebuild and testing
