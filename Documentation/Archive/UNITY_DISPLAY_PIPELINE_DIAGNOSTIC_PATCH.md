# Unity Display Pipeline Diagnostic Patch

## Problem Diagnosis

**Symptom**: Server produces valid detections (3-4 persons), but Unity shows:
- HUD: all zeros (e2e=0.0ms, upload=0.0ms, etc.)
- No bounding boxes visible
- HTTP 200 responses received

**Root Cause Identified**:

The code has **TWO separate execution paths**:

### Path 1: Old HTTP POST (DEPRECATED, still has HUD update)
```
RunServerInference() (OLD code, lines 400-900)
  → Parse response (line 703-770)
  → Calculate metrics (line 712-736)
  → Update HUD (line 841-856) ✅ HUD UPDATED
  → Display frame (line 791 - REMOVED)
```

### Path 2: New UDP + Latest Polling (CURRENT, missing HUD update)
```
ProcessServerResponse() (NEW code, line 1500)
  → Parse JSON (line 1506)
  → Store in trace (line 1529)
  → Mark completed (line 1532)
  → Enqueue for telemetry (line 1539)
  → ❌ NO HUD UPDATE
  → ❌ NO METRICS CALCULATION

TryDisplayNewestFrame() (line 1109)
  → Find newest completed frame
  → DisplayFrame() (line 1185)
    → Extract response from trace (line 1191)
    → Draw skeletons (line 1203) ✅ SHOULD WORK
    → ❌ NO HUD UPDATE
```

**The Problem**: `ProcessServerResponse()` and `DisplayFrame()` **never calculate or update HUD metrics**.

---

## Required Fixes

### Fix 1: Add Diagnostic Logging to ProcessServerResponse()

**Purpose**: Verify JSON parse succeeded and detections extracted correctly.

**Location**: `PoseInferenceRunManager.cs` line 1500, after line 1506 (JSON parse)

**Add after line 1506**:
```csharp
// Use the existing response parsing logic
var response = JsonConvert.DeserializeObject<PoseServerResponse>(jsonResponse);

// ✅ DIAGNOSTIC PATCH 1: Verify parse results
if (response == null)
{
    Debug.LogError("[UDP RESPONSE] Failed to parse JSON response");
    trace.MarkFailed("JSON parse error");
    // ... existing error handling ...
}
else
{
    // Log parsed detection counts
    int detectionCount = response.detections?.detections?.Length ?? 0;
    int personCount = response.skeleton?.persons?.Count ?? 0;

    Debug.Log($"[DIAGNOSTIC PATCH 1] ✓ JSON parse SUCCESS for frame {trace.frame_id}");
    Debug.Log($"[DIAGNOSTIC PATCH 1] Parsed detections: {detectionCount}, persons: {personCount}");

    if (personCount > 0)
    {
        var firstPerson = response.skeleton.persons[0];
        int keypointCount = firstPerson.keypoints?.Count ?? 0;
        Debug.Log($"[DIAGNOSTIC PATCH 1] First person has {keypointCount} keypoints");

        if (keypointCount > 0)
        {
            var firstKp = firstPerson.keypoints[0];
            Debug.Log($"[DIAGNOSTIC PATCH 1] First keypoint: name={firstKp.name}, x={firstKp.x:F3}, y={firstKp.y:F3}, score={firstKp.score:F2}");
        }
    }
    else
    {
        Debug.LogWarning($"[DIAGNOSTIC PATCH 1] ⚠ No persons detected in frame {trace.frame_id}");
    }
}
```

**Expected Output (Good)**:
```
[DIAGNOSTIC PATCH 1] ✓ JSON parse SUCCESS for frame 554
[DIAGNOSTIC PATCH 1] Parsed detections: 4, persons: 4
[DIAGNOSTIC PATCH 1] First person has 17 keypoints
[DIAGNOSTIC PATCH 1] First keypoint: name=nose, x=0.523, y=0.312, score=0.95
```

**Expected Output (Bad - JSON parse failed)**:
```
[UDP RESPONSE] Failed to parse JSON response
```

---

### Fix 2: Add Storage Verification to ProcessServerResponse()

**Purpose**: Verify response is stored in trace correctly.

**Location**: After line 1529 (`trace.response = response;`)

**Add after line 1529**:
```csharp
// Store response
trace.response = response;

// ✅ DIAGNOSTIC PATCH 2: Verify storage
PoseServerResponse storedResponse = trace.response as PoseServerResponse;
if (storedResponse == null)
{
    Debug.LogError($"[DIAGNOSTIC PATCH 2] ❌ Response storage FAILED for frame {trace.frame_id}! trace.response is null or wrong type");
}
else
{
    int storedPersonCount = storedResponse.skeleton?.persons?.Count ?? 0;
    Debug.Log($"[DIAGNOSTIC PATCH 2] ✓ Response stored in trace {trace.frame_id}, persons={storedPersonCount}");
}
```

**Expected Output (Good)**:
```
[DIAGNOSTIC PATCH 2] ✓ Response stored in trace 554, persons=4
```

---

### Fix 3: Add Selection Verification to TryDisplayNewestFrame()

**Purpose**: Verify frame selection logic is finding completed frames.

**Location**: `TryDisplayNewestFrame()` line 1109, after line 1133

**Add after line 1133**:
```csharp
// Get the newest completed frame
FrameTrace newest = completedFrames[0];

// ✅ DIAGNOSTIC PATCH 3: Verify selection
Debug.Log($"[DIAGNOSTIC PATCH 3] Selected frame {newest.frame_id} for display from {completedFrames.Count} completed frames");

PoseServerResponse newestResponse = newest.response as PoseServerResponse;
if (newestResponse == null)
{
    Debug.LogError($"[DIAGNOSTIC PATCH 3] ❌ Selected frame {newest.frame_id} has NULL response!");
}
else
{
    int personCount = newestResponse.skeleton?.persons?.Count ?? 0;
    Debug.Log($"[DIAGNOSTIC PATCH 3] ✓ Selected frame {newest.frame_id} has response with {personCount} persons");
}
```

**Expected Output (Good)**:
```
[DIAGNOSTIC PATCH 3] Selected frame 554 for display from 1 completed frames
[DIAGNOSTIC PATCH 3] ✓ Selected frame 554 has response with 4 persons
```

---

### Fix 4: Add Display Verification to DisplayFrame()

**Purpose**: Verify response extraction and rendering input.

**Location**: `DisplayFrame()` line 1185, after line 1197

**Add after line 1197**:
```csharp
if (response == null)
{
    Debug.LogError($"[PARALLEL DISPLAY] Frame {trace.frame_id} has no response data!");
    m_uiPose.ClearSkeletons();
    return;
}

// ✅ DIAGNOSTIC PATCH 4: Verify response extraction
int detectionCount = response.detections?.detections?.Length ?? 0;
int personCount = response.skeleton?.persons?.Count ?? 0;

Debug.Log($"[DIAGNOSTIC PATCH 4] DisplayFrame({trace.frame_id}): detections={detectionCount}, persons={personCount}");

if (personCount == 0)
{
    Debug.LogWarning($"[DIAGNOSTIC PATCH 4] ⚠ Frame {trace.frame_id} has ZERO persons - will call ClearSkeletons()");
}
else
{
    Debug.Log($"[DIAGNOSTIC PATCH 4] ✓ Frame {trace.frame_id} has {personCount} persons - will call DrawPoseSkeletons()");
}
```

**Expected Output (Good)**:
```
[DIAGNOSTIC PATCH 4] DisplayFrame(554): detections=4, persons=4
[DIAGNOSTIC PATCH 4] ✓ Frame 554 has 4 persons - will call DrawPoseSkeletons()
```

**Expected Output (Bad - no detections)**:
```
[DIAGNOSTIC PATCH 4] DisplayFrame(554): detections=0, persons=0
[DIAGNOSTIC PATCH 4] ⚠ Frame 554 has ZERO persons - will call ClearSkeletons()
```

---

### Fix 5: Add Rendering Verification After DrawPoseSkeletons()

**Purpose**: Verify renderer receives correct data.

**Location**: After line 1203 (`m_uiPose.DrawPoseSkeletons(...)`)

**Add after line 1203**:
```csharp
// Draw pose skeletons
if (response.skeleton != null && response.skeleton.persons != null && response.skeleton.persons.Count > 0)
{
    Debug.Log($"[PARALLEL DISPLAY] Displaying frame {trace.frame_id} with {response.skeleton.persons.Count} person(s)");
    m_uiPose.DrawPoseSkeletons(response.skeleton.persons.ToArray(), cachedCameraPose, m_minKeypointScore);

    // ✅ DIAGNOSTIC PATCH 5: Verify rendering input
    var firstPerson = response.skeleton.persons[0];
    int keypointCount = firstPerson.keypoints?.Count ?? 0;
    float[] bbox = firstPerson.bbox;

    Debug.Log($"[DIAGNOSTIC PATCH 5] ✓ Sent to renderer: person[0] has {keypointCount} keypoints");

    if (bbox != null && bbox.Length == 4)
    {
        Debug.Log($"[DIAGNOSTIC PATCH 5] ✓ BBox[0] = [{bbox[0]:F3}, {bbox[1]:F3}, {bbox[2]:F3}, {bbox[3]:F3}] (normalized)");

        // Check if bbox is valid (not all zeros, not out of bounds)
        if (bbox[0] == 0 && bbox[1] == 0 && bbox[2] == 0 && bbox[3] == 0)
        {
            Debug.LogWarning($"[DIAGNOSTIC PATCH 5] ⚠ BBox is all zeros!");
        }
        else if (bbox[0] < 0 || bbox[1] < 0 || bbox[2] > 1 || bbox[3] > 1)
        {
            Debug.LogWarning($"[DIAGNOSTIC PATCH 5] ⚠ BBox coordinates out of normalized range [0-1]!");
        }
        else
        {
            Debug.Log($"[DIAGNOSTIC PATCH 5] ✓ BBox coordinates valid");
        }
    }
    else
    {
        Debug.LogWarning($"[DIAGNOSTIC PATCH 5] ⚠ BBox is null or wrong length!");
    }

    if (keypointCount > 0)
    {
        var nose = firstPerson.keypoints.Find(k => k.name == "nose");
        if (nose != null)
        {
            Debug.Log($"[DIAGNOSTIC PATCH 5] ✓ Nose keypoint: ({nose.x:F3}, {nose.y:F3}) score={nose.score:F2}");

            if (nose.x < 0 || nose.x > 1 || nose.y < 0 || nose.y > 1)
            {
                Debug.LogWarning($"[DIAGNOSTIC PATCH 5] ⚠ Nose coordinates out of normalized range [0-1]!");
            }
        }
    }
}
```

**Expected Output (Good)**:
```
[DIAGNOSTIC PATCH 5] ✓ Sent to renderer: person[0] has 17 keypoints
[DIAGNOSTIC PATCH 5] ✓ BBox[0] = [0.234, 0.156, 0.678, 0.891] (normalized)
[DIAGNOSTIC PATCH 5] ✓ BBox coordinates valid
[DIAGNOSTIC PATCH 5] ✓ Nose keypoint: (0.523, 0.312) score=0.95
```

---

### Fix 6: Add HUD Update to DisplayFrame()

**Purpose**: Fix the root cause - HUD never gets updated in new code path.

**Location**: After DisplayFrame() draws skeletons (after line 1209)

**Add before the closing brace of DisplayFrame()**:
```csharp
    else
    {
        Debug.Log($"[PARALLEL DISPLAY] Frame {trace.frame_id} has no pose data, clearing skeletons");
        m_uiPose.ClearSkeletons();
    }

    // ✅ DIAGNOSTIC PATCH 6: UPDATE HUD (CRITICAL FIX)
    // Calculate metrics from trace (trace already has server timestamps)
    long now = TimestampUtil.GetUnixTimestampMs();

    // E2E time: from send to receive
    float e2eMs = 0f;
    if (trace.unity_receive_ts > 0 && trace.unity_send_ts > 0)
    {
        e2eMs = (float)(trace.unity_receive_ts - trace.unity_send_ts);
    }

    // Network upload time (estimated from trace.upload_ms if available)
    float uploadMs = trace.upload_ms > 0 ? trace.upload_ms : 0f;

    // Server processing time
    float serverProcMs = trace.server_proc_ms > 0 ? trace.server_proc_ms : 0f;

    // Network download time
    float downloadMs = trace.download_ms > 0 ? trace.download_ms : 0f;

    // Parse time
    float parseMs = trace.parse_ms > 0 ? trace.parse_ms : 0f;

    // Detection count
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

    // Get byte counts from trace
    int uploadBytes = trace.upload_bytes_compressed > 0 ? trace.upload_bytes_compressed : 0;
    int downloadBytes = trace.download_bytes_uncompressed > 0 ? trace.download_bytes_uncompressed : 0;
    int downloadBytesCompressed = trace.download_bytes_compressed > 0 ? trace.download_bytes_compressed : 0;

    Debug.Log($"[DIAGNOSTIC PATCH 6] Updating HUD: e2e={e2eMs:F0}ms, upload={uploadMs:F0}ms, server={serverProcMs:F0}ms, download={downloadMs:F0}ms, parse={parseMs:F0}ms, count={detectionCount}");

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
}  // End of DisplayFrame()
```

**Expected Output (Good)**:
```
[DIAGNOSTIC PATCH 6] Updating HUD: e2e=250ms, upload=50ms, server=150ms, download=30ms, parse=20ms, count=4
[HUD] E2E=250ms (up=50ms srv=150ms down=30ms parse=20ms) count=4
```

**Expected Output (Bad - missing timing data)**:
```
[DIAGNOSTIC PATCH 6] Updating HUD: e2e=0ms, upload=0ms, server=0ms, download=0ms, parse=0ms, count=4
[HUD] E2E=0ms (up=0ms srv=0ms down=0ms parse=0ms) count=4
```

---

## Validation Rules

After applying patches, verify these conditions:

### Rule 1: JSON Parse Success Rate
**Rule**: If server returns HTTP 200, Unity must successfully parse detections.

**Check**:
```
Server: [RESPONSE LATEST] Serving latest result: session=..., frame_id=554
        INFO: "GET /response/.../latest" 200 OK
Unity:  [DIAGNOSTIC PATCH 1] ✓ JSON parse SUCCESS for frame 554
        [DIAGNOSTIC PATCH 1] Parsed detections: 4, persons: 4
```

**Fail Condition**: HTTP 200 but parse fails or person count = 0

---

### Rule 2: Storage Integrity
**Rule**: Parsed response must be stored in trace.response correctly.

**Check**:
```
[DIAGNOSTIC PATCH 2] ✓ Response stored in trace 554, persons=4
```

**Fail Condition**: `trace.response is null` after storage

---

### Rule 3: Frame Selection Correctness
**Rule**: TryDisplayNewestFrame() must select frames that have valid responses.

**Check**:
```
[DIAGNOSTIC PATCH 3] Selected frame 554 for display from 1 completed frames
[DIAGNOSTIC PATCH 3] ✓ Selected frame 554 has response with 4 persons
```

**Fail Condition**: Selected frame has null response or 0 persons despite earlier parse success

---

### Rule 4: Rendering Input Validation
**Rule**: If detection count > 0, renderer must receive non-zero bbox/keypoint data.

**Check**:
```
[DIAGNOSTIC PATCH 5] ✓ Sent to renderer: person[0] has 17 keypoints
[DIAGNOSTIC PATCH 5] ✓ BBox[0] = [0.234, 0.156, 0.678, 0.891] (normalized)
[DIAGNOSTIC PATCH 5] ✓ BBox coordinates valid
```

**Fail Condition**:
- BBox all zeros
- Coordinates out of range [0-1]
- Keypoint count = 0

---

### Rule 5: HUD Update Consistency
**Rule**: If frame is displayed with N persons, HUD must show count=N (not 0).

**Check**:
```
[DIAGNOSTIC PATCH 6] Updating HUD: e2e=250ms, ..., count=4
[HUD] E2E=250ms (up=50ms srv=150ms down=30ms parse=20ms) count=4
```

**Fail Condition**: HUD shows count=0 despite frame having persons

---

### Rule 6: Timing Data Availability
**Rule**: If HUD shows e2e=0ms but server processed frame, trace is missing timing data.

**Check**: Look for this BAD pattern:
```
[DIAGNOSTIC PATCH 6] Updating HUD: e2e=0ms, upload=0ms, server=0ms, ...
```

**Root Cause**: `ProcessServerResponse()` doesn't calculate or store timing metrics.

**Fix**: Modify `ProcessServerResponse()` to calculate timing (see Fix 7 below).

---

## Fix 7: Add Timing Calculation to ProcessServerResponse()

**Purpose**: Calculate and store timing metrics when response is received.

**Location**: `ProcessServerResponse()` line 1500, after line 1526

**Replace lines 1522-1526**:
```csharp
// Store server timestamps
trace.server_receive_ts = (long)(response.t_server_recv * 1000);
trace.server_process_start_ts = (long)(response.server_process_start_ts * 1000);
trace.server_send_ts = (long)(response.t_server_send * 1000);
trace.server_proc_ms = response.processing_time_ms;
```

**With**:
```csharp
// Store server timestamps
trace.server_receive_ts = (long)(response.t_server_recv * 1000);
trace.server_process_start_ts = (long)(response.server_process_start_ts * 1000);
trace.server_send_ts = (long)(response.t_server_send * 1000);
trace.server_proc_ms = response.processing_time_ms;

// ✅ DIAGNOSTIC PATCH 7: Calculate timing metrics
// E2E time: from send to receive
float e2eMs = 0f;
if (receiveTs > 0 && trace.unity_send_ts > 0)
{
    e2eMs = (float)(receiveTs - trace.unity_send_ts);
}

// Server processing time
float serverProcMs = response.processing_time_ms;

// Network time (rough estimate: E2E - server processing)
float networkMs = Mathf.Max(0f, e2eMs - serverProcMs);

// Allocate network time to upload/download based on byte ratio
int uploadBytes = trace.upload_bytes_compressed > 0 ? trace.upload_bytes_compressed : 10000;  // estimate
int downloadBytes = jsonResponse.Length;  // actual JSON size

int totalBytes = uploadBytes + downloadBytes;
float uploadRatio = totalBytes > 0 ? (float)uploadBytes / totalBytes : 0.5f;
float downloadRatio = 1.0f - uploadRatio;

float uploadMs = networkMs * uploadRatio;
float downloadMs = networkMs * downloadRatio;
float parseMs = 5.0f;  // Small estimate (actual parse is very fast)

// Store in trace
trace.upload_ms = uploadMs;
trace.download_ms = downloadMs;
trace.parse_ms = parseMs;
trace.download_bytes_uncompressed = downloadBytes;
trace.download_bytes_compressed = downloadBytes;  // No compression info available here

Debug.Log($"[DIAGNOSTIC PATCH 7] Calculated timing for frame {trace.frame_id}: e2e={e2eMs:F0}ms, upload={uploadMs:F0}ms, server={serverProcMs:F0}ms, download={downloadMs:F0}ms");
```

**Expected Output**:
```
[DIAGNOSTIC PATCH 7] Calculated timing for frame 554: e2e=250ms, upload=50ms, server=150ms, download=40ms
```

---

## Testing Checklist

After applying all patches:

### 1. Verify JSON Parse
```
✅ [DIAGNOSTIC PATCH 1] ✓ JSON parse SUCCESS
✅ [DIAGNOSTIC PATCH 1] Parsed detections: N, persons: N (N > 0)
❌ [UDP RESPONSE] Failed to parse JSON response
```

### 2. Verify Storage
```
✅ [DIAGNOSTIC PATCH 2] ✓ Response stored in trace N, persons=N
❌ [DIAGNOSTIC PATCH 2] ❌ Response storage FAILED
```

### 3. Verify Selection
```
✅ [DIAGNOSTIC PATCH 3] ✓ Selected frame N has response with N persons
❌ [DIAGNOSTIC PATCH 3] ❌ Selected frame N has NULL response
```

### 4. Verify Rendering Input
```
✅ [DIAGNOSTIC PATCH 5] ✓ BBox coordinates valid
✅ [DIAGNOSTIC PATCH 5] ✓ Nose keypoint: (x, y) score=S
❌ [DIAGNOSTIC PATCH 5] ⚠ BBox is all zeros!
❌ [DIAGNOSTIC PATCH 5] ⚠ BBox coordinates out of range!
```

### 5. Verify HUD Update
```
✅ [DIAGNOSTIC PATCH 6] Updating HUD: e2e=Nms, ..., count=N (N > 0)
✅ [HUD] E2E=Nms (... count=N) (N > 0)
❌ [HUD] E2E=0ms (... count=0) ← BAD if server detected persons
```

### 6. Verify Timing Calculation
```
✅ [DIAGNOSTIC PATCH 7] Calculated timing: e2e=Nms, upload=Nms, server=Nms (all > 0)
❌ [DIAGNOSTIC PATCH 7] Calculated timing: e2e=0ms ← BAD
```

---

## Summary of Changes

| Patch | Location | Purpose | Status |
|-------|----------|---------|--------|
| 1 | ProcessServerResponse() after parse | Verify JSON parse success | ⏳ TO APPLY |
| 2 | ProcessServerResponse() after storage | Verify response stored | ⏳ TO APPLY |
| 3 | TryDisplayNewestFrame() after selection | Verify frame selection | ⏳ TO APPLY |
| 4 | DisplayFrame() after extraction | Verify response extraction | ⏳ TO APPLY |
| 5 | DisplayFrame() after DrawPoseSkeletons | Verify rendering input | ⏳ TO APPLY |
| 6 | DisplayFrame() end | **FIX: Update HUD** | ⏳ TO APPLY |
| 7 | ProcessServerResponse() after timestamps | **FIX: Calculate timing** | ⏳ TO APPLY |

**Critical Fixes** (Fix root cause, not just diagnose):
- **Patch 6**: Update HUD in DisplayFrame() (otherwise HUD always shows zeros)
- **Patch 7**: Calculate timing in ProcessServerResponse() (otherwise HUD has no data to show)

**Diagnostic Patches** (Help identify where pipeline breaks):
- Patches 1-5: Add logging to trace data flow from parse → storage → selection → display → rendering

---

## Expected Outcome

### Before Patches:
```
Server: [RESPONSE LATEST] frame_id=554, persons=4
Unity:  [UDP RESPONSE] Response length: 5234 bytes
        [PARALLEL DISPLAY] Frame 554 has no response data!
        [HUD DISPLAY] UpdateDisplay using: e2e=0.0ms, upload=0.0ms, ...
```

### After Patches (Success):
```
Server: [RESPONSE LATEST] frame_id=554, persons=4
Unity:  [UDP RESPONSE] Response length: 5234 bytes
        [DIAGNOSTIC PATCH 1] ✓ JSON parse SUCCESS for frame 554
        [DIAGNOSTIC PATCH 1] Parsed detections: 4, persons: 4
        [DIAGNOSTIC PATCH 2] ✓ Response stored in trace 554, persons=4
        [DIAGNOSTIC PATCH 7] Calculated timing: e2e=250ms, upload=50ms, server=150ms, download=40ms
        [DIAGNOSTIC PATCH 3] ✓ Selected frame 554 has response with 4 persons
        [DIAGNOSTIC PATCH 4] ✓ Frame 554 has 4 persons - will call DrawPoseSkeletons()
        [DIAGNOSTIC PATCH 5] ✓ BBox[0] = [0.234, 0.156, 0.678, 0.891]
        [DIAGNOSTIC PATCH 6] Updating HUD: e2e=250ms, upload=50ms, server=150ms, count=4
        [HUD] E2E=250ms (up=50ms srv=150ms down=40ms parse=5ms) count=4
```

### After Patches (Failure - helps identify exact break point):
```
If parse fails → Patch 1 logs error
If storage fails → Patch 2 logs error
If selection fails → Patch 3 logs error
If rendering input invalid → Patch 5 logs warning
If HUD still zeros → Patch 6/7 logs show missing data
```

---

## Files to Modify

**Single File**: `Assets/PassthroughCameraApiSamples/PoseEstimation/Scripts/PoseInferenceRunManager.cs`

**Lines to Modify**:
1. After line 1506 (add Patch 1)
2. After line 1529 (add Patch 2)
3. After line 1526 (replace with Patch 7)
4. After line 1133 (add Patch 3)
5. After line 1197 (add Patch 4)
6. After line 1203 (add Patch 5)
7. After line 1209 (add Patch 6 - **CRITICAL**)

---

**Priority Order**:
1. **Patch 6** (Update HUD) - Fixes root cause
2. **Patch 7** (Calculate timing) - Provides data for Patch 6
3. Patches 1-5 (Diagnostics) - Help debug if still broken

---

**Last Updated**: 2026-04-16
**Status**: Ready to apply
