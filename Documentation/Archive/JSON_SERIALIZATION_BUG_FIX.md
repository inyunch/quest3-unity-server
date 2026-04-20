# JSON Serialization Bug Fix - Segmentation Mode Field Missing

**Date**: 2026-04-17
**Status**: ✅ FIXED - Changed from JsonUtility.ToJson() to manual JSON string

---

## Problem

After adding `{ "mode", "segmentation" }` to Segmentation's `BuildTelemetryJson()`, user rebuilt Unity but **server still received mode=both**.

---

## Root Cause: JsonUtility.ToJson() Doesn't Support Dictionary

### What Happened

**Segmentation Scene** (BEFORE FIX):
```csharp
private string BuildTelemetryJson(FrameTrace trace)
{
    var telemetry = new Dictionary<string, object>
    {
        { "scene", "Segmentation" },
        { "session_id", trace.session_id },
        { "frame_id", trace.frame_id },
        { "mode", "segmentation" },  // ✅ Added
        // ... more fields
    };

    return JsonUtility.ToJson(telemetry);  // ❌ BUG: Returns "{}" empty JSON!
}
```

**Problem**: Unity's `JsonUtility.ToJson()` **only supports serializable classes**, NOT `Dictionary<string, object>`.

**Result**:
```json
// Expected:
{"scene":"Segmentation","session_id":"abc123","frame_id":1,"mode":"segmentation",...}

// Actual output from JsonUtility.ToJson(dictionary):
{}

// Server receives empty telemetry → defaults to mode=both!
```

---

## Why Other Scenes Worked

### MultiObjectDetection - Manual JSON String ✅
```csharp
private string BuildTelemetryJson(FrameTrace trace)
{
    var json = "{" +
        $"\"scene\":\"MultiObjectDetection\"," +
        $"\"session_id\":\"{trace.session_id}\"," +
        $"\"frame_id\":{trace.frame_id}," +
        $"\"mode\":\"detection\"," +  // ✅ Works!
        // ... more fields
        "}";

    return json;  // ✅ Returns correct JSON string
}
```

### PoseEstimation - Newtonsoft.Json ✅
```csharp
using Newtonsoft.Json;  // ✅ Import JsonConvert

private string BuildTelemetryJson(FrameTrace trace)
{
    var telemetry = new
    {
        scene = "PoseEstimation",
        session_id = trace.session_id,
        frame_id = trace.frame_id,
        mode = "both",  // ✅ Works!
        // ... more fields
    };

    return JsonConvert.SerializeObject(telemetry);  // ✅ Returns correct JSON
}
```

**Newtonsoft.Json supports both anonymous types and Dictionary.**

---

## Solution Applied

**Changed Segmentation to use manual JSON string construction** (same as MultiObjectDetection).

**File**: `SegmentationInferenceRunManager.cs` line 1642

**AFTER FIX**:
```csharp
private string BuildTelemetryJson(FrameTrace trace)
{
    // Build telemetry JSON manually (JsonUtility doesn't support Dictionary!)
    // CRITICAL: mode field must be included for server to use correct inference type
    var json = "{" +
        $"\"scene\":\"Segmentation\"," +
        $"\"session_id\":\"{trace.session_id}\"," +
        $"\"frame_id\":{trace.frame_id}," +
        $"\"mode\":\"segmentation\"," +  // ✅ Now properly included in JSON!

        // Unity-side timing
        $"\"unity_send_ts\":{trace.unity_send_ts}," +
        $"\"unity_receive_ts\":{trace.unity_receive_ts}," +
        $"\"unity_display_ts\":{trace.unity_display_ts ?? 0}," +
        $"\"unity_drop_ts\":{trace.unity_drop_ts ?? 0}," +

        // Server-side timing
        $"\"server_receive_ts\":{trace.server_receive_ts}," +
        $"\"server_process_start_ts\":{trace.server_process_start_ts}," +
        $"\"server_send_ts\":{trace.server_send_ts}," +

        // Latency breakdown
        $"\"latency_ms\":{trace.e2e_ms:F2}," +
        $"\"upload_ms\":{trace.upload_ms:F2}," +
        $"\"queue_wait_ms\":{(trace.server_process_start_ts - trace.server_receive_ts):F2}," +
        $"\"server_proc_ms\":{trace.server_proc_ms:F2}," +
        $"\"download_ms\":{trace.download_ms:F2}," +
        $"\"parse_ms\":{trace.parse_ms:F2}," +

        // Payload sizes
        $"\"upload_bytes_uncompressed\":{trace.upload_bytes_uncompressed}," +
        $"\"upload_bytes_compressed\":{trace.upload_bytes_compressed}," +
        $"\"download_bytes_uncompressed\":{trace.download_bytes_uncompressed}," +
        $"\"download_bytes_compressed\":{trace.download_bytes_compressed}," +

        // State and results
        $"\"final_state\":\"{trace.state}\"," +
        $"\"drop_reason\":\"{EscapeJson(trace.drop_reason ?? "")}\"," +
        $"\"error_reason\":\"{EscapeJson(trace.error_reason ?? "")}\"," +
        $"\"detection_count\":{trace.detection_count ?? 0}," +
        $"\"avg_confidence\":{trace.avg_confidence:F4}," +

        // Legacy/compatibility
        $"\"freeze_frames_per_frame\":{trace.freeze_frames}," +
        $"\"target_fps\":{m_inferenceConfig.targetFPS:F1}" +
        "}";

    return json;  // ✅ Returns valid JSON string with all fields
}

/// <summary>
/// Escape string for JSON (handle quotes and backslashes)
/// </summary>
private string EscapeJson(string value)
{
    if (string.IsNullOrEmpty(value))
        return "";

    return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
```

**Added `EscapeJson()` helper method** to properly escape special characters in string fields.

---

## Expected Output Now

### Telemetry JSON Sent to Server

```json
{
  "scene": "Segmentation",
  "session_id": "b6c0976f-1234",
  "frame_id": 26,
  "mode": "segmentation",
  "unity_send_ts": 1713345678901,
  "unity_receive_ts": 1713345679386,
  "unity_display_ts": 1713345679400,
  "unity_drop_ts": 0,
  "server_receive_ts": 1713345678950,
  "server_process_start_ts": 1713345678952,
  "server_send_ts": 1713345679350,
  "latency_ms": 485.30,
  "upload_ms": 49.00,
  "queue_wait_ms": 2.00,
  "server_proc_ms": 398.00,
  "download_ms": 36.00,
  "parse_ms": 2.30,
  "upload_bytes_uncompressed": 45000,
  "upload_bytes_compressed": 45000,
  "download_bytes_uncompressed": 18500,
  "download_bytes_compressed": 18500,
  "final_state": "Displayed",
  "drop_reason": "",
  "error_reason": "",
  "detection_count": 1,
  "avg_confidence": 0.8800,
  "freeze_frames_per_frame": 0,
  "target_fps": 10.0
}
```

### Server Logs (After Fix)

```
[UDP WORKER] Processing sessionid_1 (queue_wait=2.0ms, mode=segmentation)  ✅
[UDP WORKER mode=segmentation] YOLO detected 5 objects, 1 person(s)
[UDP WORKER SEGMENTATION] Mask 1: 340x180, base64: 12480 chars
[UDP WORKER mode=segmentation] 1 person(s) with masks
[UDP WORKER] ✓ Completed sessionid_1 (processing=398.0ms, total=400.0ms)
```

**No more `mode=both`!**

---

## Verification Steps

### 1. Rebuild Unity APK
```bash
# In Unity Editor:
File → Build Settings → Build And Run
```

### 2. Check Unity Logs
```bash
adb logcat -s Unity | findstr "UDP"
```

**Should see**:
```
[UDP SEND] Frame sessionid_26 sent, size=45000 bytes
[UDP POLL] Starting polling for frame 26
[UDP POLL] Frame 26 received after 0.35s
[SEGMENTATION] Received response with 1 detection(s)
[SEGMENTATION] Detection 1: person, conf=0.88, has_mask=True
```

### 3. Check Server Logs

**Should see**:
```
[UDP WORKER] Processing sessionid_1 (mode=segmentation)  ← Correct!
[UDP WORKER mode=segmentation] 1 person(s) with masks
```

**Should NOT see**:
```
[UDP WORKER] Processing sessionid_1 (mode=both)  ← Wrong!
```

### 4. Check Excel Output

| scene | mode | detection_count | avg_confidence | latency_ms |
|-------|------|-----------------|----------------|------------|
| Segmentation | segmentation | 1 | 0.88 | 485.3 |

**Mode column should show "segmentation", not "both"!**

---

## Lessons Learned

### Unity JSON Serialization Options

| Method | Supports | Pros | Cons |
|--------|----------|------|------|
| **JsonUtility.ToJson()** | Serializable classes only | Built-in, fast | ❌ No Dictionary, no anonymous types |
| **Newtonsoft.Json** | Dictionary, anonymous types, everything | Powerful, flexible | Requires package import |
| **Manual string** | Everything | No dependencies, full control | Verbose, manual escaping needed |

### Best Practices

1. **Use manual JSON string** for simple telemetry (like MultiObjectDetection)
2. **Use Newtonsoft.Json** for complex nested objects (like PoseEstimation)
3. **Never use JsonUtility.ToJson() with Dictionary** - it silently fails!

### Testing JSON Serialization

Always test by printing the JSON output:
```csharp
string json = BuildTelemetryJson(trace);
Debug.Log($"[TELEMETRY JSON] {json}");  // Verify it's not empty!
```

---

## Files Modified

1. **SegmentationInferenceRunManager.cs** (line 1642)
   - Changed from `JsonUtility.ToJson(dictionary)` to manual JSON string
   - Added `EscapeJson()` helper method (line 1695)

---

## Summary

**Problem**: JsonUtility.ToJson() silently failed on Dictionary, returning `{}` empty JSON
**Impact**: Server never received "mode" field, defaulted to mode=both
**Solution**: Changed to manual JSON string construction (same as MultiObjectDetection)
**Result**: mode="segmentation" now properly transmitted to server

---

**Last Updated**: 2026-04-17 10:00 UTC
**Status**: ✅ Fixed, ready for Unity rebuild
