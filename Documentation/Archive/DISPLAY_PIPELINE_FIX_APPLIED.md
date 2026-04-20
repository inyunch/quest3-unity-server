# Unity Display Pipeline Fix - APPLIED

## Problem Summary

**Symptoms**:
- Server produces valid detections (3-4 persons) with HTTP 200 OK responses
- Unity HUD shows all zeros: `e2e=0.0ms, upload=0.0ms, server=0.0ms, download=0.0ms, parse=0.0ms`
- No bounding boxes visible on Quest display
- Excel telemetry not saved

**Root Cause**:
The new UDP + `/latest` polling code path (`ProcessServerResponse()` → `DisplayFrame()`) **never calculated timing metrics or updated the HUD**.

The old HTTP POST code path had HUD update logic, but it was removed during Phase 3 refactoring without being added to the new display pipeline.

---

## Fixes Applied

### Fix 1: Calculate Timing Metrics in ProcessServerResponse()

**File**: `PoseInferenceRunManager.cs`
**Location**: Lines 1528-1565 (after storing server timestamps)

**What was added**:
```csharp
// Calculate timing metrics for HUD
float e2eMs = 0f;
if (receiveTs > 0 && trace.unity_send_ts > 0)
{
    e2eMs = (float)(receiveTs - trace.unity_send_ts);
}

float serverProcMs = response.processing_time_ms;
float networkMs = Mathf.Max(0f, e2eMs - serverProcMs);

// Allocate network time to upload/download based on byte ratio
int uploadBytes = trace.upload_bytes_compressed > 0 ? trace.upload_bytes_compressed : 10000;
int downloadBytes = jsonResponse.Length;

int totalBytes = uploadBytes + downloadBytes;
float uploadRatio = totalBytes > 0 ? (float)uploadBytes / totalBytes : 0.5f;
float downloadRatio = 1.0f - uploadRatio;

float uploadMs = networkMs * uploadRatio;
float downloadMs = networkMs * downloadRatio;
float parseMs = 5.0f;  // Small estimate

// Store in trace for later HUD update
trace.upload_ms = uploadMs;
trace.download_ms = downloadMs;
trace.parse_ms = parseMs;
trace.download_bytes_uncompressed = downloadBytes;
trace.download_bytes_compressed = downloadBytes;

Debug.Log($"[TIMING CALC] Frame {trace.frame_id}: e2e={e2eMs:F0}ms, upload={uploadMs:F0}ms, server={serverProcMs:F0}ms, download={downloadMs:F0}ms");

// Log parsed detection counts
int detectionCount = response.detections?.detections?.Length ?? 0;
int personCount = response.skeleton?.persons?.Count ?? 0;
Debug.Log($"[PARSE VERIFY] Frame {trace.frame_id}: detections={detectionCount}, persons={personCount}");
```

**Purpose**:
- Calculate E2E time from `unity_send_ts` to `receiveTs`
- Estimate upload/download split based on byte sizes
- Store metrics in `FrameTrace` for later use by `DisplayFrame()`
- Add diagnostic logging for parsed detection counts

**Expected logs**:
```
[TIMING CALC] Frame 554: e2e=250ms, upload=50ms, server=150ms, download=40ms
[PARSE VERIFY] Frame 554: detections=4, persons=4
```

---

### Fix 2: Update HUD in DisplayFrame()

**File**: `PoseInferenceRunManager.cs`
**Location**: Lines 1211-1296 (end of DisplayFrame() function)

**What was added**:
```csharp
// Update HUD with metrics from this frame
float e2eMs = trace.upload_ms + trace.server_proc_ms + trace.download_ms + trace.parse_ms;
float uploadMs = trace.upload_ms > 0 ? trace.upload_ms : 0f;
float serverProcMs = trace.server_proc_ms > 0 ? trace.server_proc_ms : 0f;
float downloadMs = trace.download_ms > 0 ? trace.download_ms : 0f;
float parseMs = trace.parse_ms > 0 ? trace.parse_ms : 0f;

int detectionCount = response.skeleton?.persons?.Count ?? 0;

// Average confidence
float avgConfidence = 0f;
if (response.detections != null && response.detections.detections != null && response.detections.detections.Length > 0)
{
    float sum = 0f;
    foreach (var det in response.detections.detections)
    {
        sum += det.confidence;
    }
    avgConfidence = sum / response.detections.detections.Length;
}

// Keypoint average confidence
float keypointAvgConf = 0f;
if (response.skeleton != null && response.skeleton.persons != null && response.skeleton.persons.Count > 0)
{
    List<float> allScores = new List<float>();
    foreach (var person in response.skeleton.persons)
    {
        if (person != null && person.keypoints != null)
        {
            foreach (var kp in person.keypoints)
            {
                if (kp.score > 0f)
                {
                    allScores.Add(kp.score);
                }
            }
        }
    }
    if (allScores.Count > 0)
    {
        keypointAvgConf = allScores.Average();
    }
}

int uploadBytes = trace.upload_bytes_compressed > 0 ? trace.upload_bytes_compressed : 0;
int downloadBytes = trace.download_bytes_uncompressed > 0 ? trace.download_bytes_uncompressed : 0;
int downloadBytesCompressed = trace.download_bytes_compressed > 0 ? trace.download_bytes_compressed : 0;

Debug.Log($"[HUD UPDATE] Frame {trace.frame_id}: e2e={e2eMs:F0}ms, upload={uploadMs:F0}ms, server={serverProcMs:F0}ms, download={downloadMs:F0}ms, parse={parseMs:F0}ms, count={detectionCount}");

// Update HUD
if (m_inferenceHUD != null)
{
    m_inferenceHUD.UpdateHUD(
        e2eMs,
        uploadMs,
        serverProcMs,
        downloadMs,
        parseMs,
        uploadBytes,
        downloadBytes,
        downloadBytesCompressed,
        detectionCount,
        avgConfidence,
        keypointAvgConf
    );
}

// Update SharedInferenceHUD
if (m_sharedHUD != null)
{
    m_sharedHUD.UpdateMetrics(
        e2eMs,
        uploadMs,
        serverProcMs,
        downloadMs,
        parseMs,
        uploadBytes,
        downloadBytes,
        downloadBytesCompressed,
        detectionCount,
        avgConfidence,
        keypointAvgConf
    );
}
```

**Purpose**:
- Extract timing metrics from `FrameTrace`
- Calculate detection count and confidence scores
- Call `UpdateHUD()` on both legacy and shared HUD components
- Add diagnostic logging for HUD update

**Expected logs**:
```
[HUD UPDATE] Frame 554: e2e=250ms, upload=50ms, server=150ms, download=40ms, parse=5ms, count=4
[HUD] E2E=250ms (up=50ms srv=150ms down=40ms parse=5ms) count=4
```

---

## Code Flow After Fixes

### 1. Response Received (UDP + Latest Polling)
```
ListenForResponseHTTP() (polling /latest)
  → HTTP 200 response
  → Extract frame_id from JSON
  → ProcessServerResponse(trace, jsonResponse, receiveTs)
```

### 2. ProcessServerResponse() - Parse and Calculate
```
ProcessServerResponse()
  → Parse JSON to PoseServerResponse
  → ✅ NEW: Calculate timing metrics (e2e, upload, server, download)
  → ✅ NEW: Store metrics in trace (upload_ms, download_ms, parse_ms)
  → ✅ NEW: Log parsed detection counts
  → Store response in trace.response
  → Mark trace as Completed
  → Enqueue for telemetry
```

### 3. TryDisplayNewestFrame() - Select Frame
```
Update()
  → TryDisplayNewestFrame()
    → Find all completed frames
    → Select newest frame
    → DisplayFrame(newest)
```

### 4. DisplayFrame() - Render and Update HUD
```
DisplayFrame(trace)
  → Extract response from trace
  → DrawPoseSkeletons() with response.skeleton.persons
  → ✅ NEW: Extract timing metrics from trace
  → ✅ NEW: Calculate detection count and confidence
  → ✅ NEW: UpdateHUD() with all metrics
  → ✅ NEW: UpdateMetrics() on SharedInferenceHUD
```

---

## Expected Behavior After Fix

### Server Logs (Unchanged)
```
[YOLO] Detected 4 persons in frame 554
[POSE] Detected 4 persons with keypoints
[BBOX] person 0: [x1=234, y1=156, x2=678, y2=891]
[RESULT CACHE] Stored result for session_554
[RESPONSE LATEST] Serving latest result: session=..., frame_id=554
INFO: "GET /response/.../latest" 200 OK
```

### Unity Logs (NEW - with fixes)
```
[UDP RESPONSE] Response length: 5234 bytes
[TIMING CALC] Frame 554: e2e=250ms, upload=50ms, server=150ms, download=40ms
[PARSE VERIFY] Frame 554: detections=4, persons=4
[TELEMETRY DEBUG] MarkCompleted frame 554, state=Completed
[TELEMETRY QUEUE] Frame 554 COMPLETED → queued (queue depth: 1)

[DIAGNOSTIC PATCH 3] Selected frame 554 for display from 1 completed frames
[PARALLEL DISPLAY] Displaying frame 554 with 4 person(s)
[HUD UPDATE] Frame 554: e2e=250ms, upload=50ms, server=150ms, download=40ms, parse=5ms, count=4
[HUD] E2E=250ms (up=50ms srv=150ms down=40ms parse=5ms) count=4
```

### Unity HUD Display (FIXED)
```
Before Fix:
  FPS: 72.0
  E2E: 0ms
   ├Upload: 0ms (0%)
   ├Server: 0ms (0%)
   ├Download: 0ms (0%)
   └Parse: 0ms (0%)
  Persons: 0        ← WRONG!
  Avg Conf: 0.00

After Fix:
  FPS: 72.0
  E2E: 250ms        ← CORRECT!
   ├Upload: 50ms (20%)
   ├Server: 150ms (60%)
   ├Download: 40ms (16%)
   └Parse: 5ms (2%)
  Persons: 4        ← CORRECT!
  Avg Conf: 0.87    ← CORRECT!
  Keypoint: 0.91
```

### Visual Display
- ✅ Bounding boxes should appear around detected people
- ✅ Skeleton keypoints should be visible (17 keypoints per person)
- ✅ HUD shows correct detection count and timing

### Excel Telemetry
- ✅ Files created in `vision_server/data/telemetry/`
- ✅ Timing data populated (e2e_ms, upload_ms, server_proc_ms, download_ms)
- ✅ Detection count logged

---

## Diagnostic Logs to Monitor

### Check if JSON parse succeeds:
```
✅ GOOD:
[PARSE VERIFY] Frame 554: detections=4, persons=4

❌ BAD:
[UDP RESPONSE] Failed to parse JSON response
```

### Check if timing calculated:
```
✅ GOOD:
[TIMING CALC] Frame 554: e2e=250ms, upload=50ms, server=150ms, download=40ms

❌ BAD:
[TIMING CALC] Frame 554: e2e=0ms, upload=0ms, server=0ms, download=0ms
```

### Check if HUD updated:
```
✅ GOOD:
[HUD UPDATE] Frame 554: e2e=250ms, ..., count=4
[HUD] E2E=250ms (... count=4)

❌ BAD:
[HUD] E2E=0ms (... count=0)
```

### Check if frames displayed:
```
✅ GOOD:
[PARALLEL DISPLAY] Displaying frame 554 with 4 person(s)

❌ BAD:
[PARALLEL DISPLAY] Frame 554 has no response data!
[PARALLEL DISPLAY] Frame 554 has no pose data, clearing skeletons
```

---

## Troubleshooting

### Issue: HUD still shows zeros after fix

**Check**:
1. Verify `[TIMING CALC]` logs show non-zero values
2. Verify `[HUD UPDATE]` logs appear
3. Check `trace.unity_send_ts` is set (needed for e2e calculation)

**Solution**:
- If `[TIMING CALC]` shows 0ms: `trace.unity_send_ts` not set properly
- If `[TIMING CALC]` OK but `[HUD UPDATE]` missing: `DisplayFrame()` not called
- If both logs OK but HUD zeros: HUD component null or not receiving updates

---

### Issue: Bounding boxes still not visible

**Check**:
1. Verify `[PARSE VERIFY]` shows `persons > 0`
2. Verify `[PARALLEL DISPLAY]` calls `DrawPoseSkeletons()`
3. Check `m_uiPose` is not null

**Solution**:
- If `persons=0`: Server not detecting people (check server logs)
- If `persons>0` but no display: Rendering issue in `DrawPoseSkeletons()` or `m_uiPose` is null
- Check coordinate conversion in UIPose component

---

### Issue: Detection count correct in HUD but no boxes

**This is a rendering issue, NOT a data pipeline issue.**

**Check**:
1. `m_uiPose.DrawPoseSkeletons()` is being called
2. Bounding box coordinates are valid (not all zeros, not out of range)
3. Coordinate conversion from normalized [0-1] to Unity UI space

**Solution**:
- Inspect `UIPose.cs` or `PoseRenderer.cs` (whatever draws the skeletons)
- Check if renderer is disabled or hidden
- Verify camera pose is valid

---

## Files Modified

**Single File**: `Assets/PassthroughCameraApiSamples/PoseEstimation/Scripts/PoseInferenceRunManager.cs`

**Changes**:
1. Lines 1528-1565: Added timing calculation in `ProcessServerResponse()`
2. Lines 1211-1296: Added HUD update in `DisplayFrame()`

**Total Lines Added**: ~90 lines

---

## Testing Checklist

### 1. Build and Deploy
```
File → Build Settings → Build And Run
```

### 2. Monitor Unity Logs
```bash
adb logcat -s Unity | findstr "TIMING CALC|PARSE VERIFY|HUD UPDATE"
```

**Expected Output**:
```
[TIMING CALC] Frame 554: e2e=250ms, upload=50ms, server=150ms, download=40ms
[PARSE VERIFY] Frame 554: detections=4, persons=4
[HUD UPDATE] Frame 554: e2e=250ms, upload=50ms, server=150ms, download=40ms, parse=5ms, count=4
```

### 3. Check HUD Display
- Look at Quest display
- HUD should show **non-zero** values for E2E, Upload, Server, Download, Parse
- Persons count should match server detections

### 4. Check Bounding Boxes
- Bounding boxes should appear around detected people
- Skeleton keypoints should be visible
- If HUD shows `Persons: 4` but no boxes visible → Rendering issue (separate from this fix)

### 5. Check Excel Telemetry
- Files created in `vision_server/data/telemetry/`
- Open latest `.xlsx` file
- Verify columns populated: `e2e_ms`, `upload_ms`, `server_proc_ms`, `download_ms`, `detection_count`

---

## Next Steps

### If HUD Now Shows Correct Data But No Boxes Visible

This means the **data pipeline is working** but **rendering is broken**.

**Investigate**:
1. `UIPose.cs` or equivalent pose rendering component
2. Bounding box coordinate conversion (normalized → pixel → Unity UI)
3. Renderer enabled state
4. Camera pose validity

**See**: `UNITY_DISPLAY_PIPELINE_DIAGNOSTIC_PATCH.md` Patch 5 for rendering diagnostics

---

### If HUD Still Shows Zeros

**Check logs for**:
- `[TIMING CALC]` - if missing or showing 0ms, timing calculation failed
- `[HUD UPDATE]` - if missing, `DisplayFrame()` not being called
- `[PARALLEL DISPLAY]` - if missing, no frames being selected for display

**Possible causes**:
- `trace.unity_send_ts` not set (check UDP send code)
- No completed frames (check `TryDisplayNewestFrame()` logs)
- HUD component null

---

## Summary

**Root Cause**: UDP + latest polling code path missing HUD update logic

**Fixes Applied**:
1. Added timing calculation in `ProcessServerResponse()`
2. Added HUD update in `DisplayFrame()`

**Expected Result**:
- HUD shows correct E2E time, upload/server/download breakdown, detection count
- Bounding boxes visible (if rendering works)
- Excel telemetry saved

**Status**: ✅ Code changes applied, ready for build and test

---

**Last Updated**: 2026-04-16
**Files Modified**: 1 (PoseInferenceRunManager.cs)
**Lines Added**: ~90
