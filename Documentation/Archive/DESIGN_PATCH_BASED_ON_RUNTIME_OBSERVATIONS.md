# Design Patch - Based on Runtime Observations

**Date**: 2026-04-16
**Status**: Patch to existing design (NOT a full rewrite)
**Purpose**: Fix specific issues observed in runtime logs while preserving overall architecture

---

## Executive Summary: Root Cause Analysis

Based on actual runtime logs, the system has **three specific bugs**, NOT a fundamental architecture problem:

### What Works ✅
- Server inference pipeline (YOLO + Pose) is functioning correctly
- Worker logs show valid detections: `[UDP WORKER BBOX] conf=0.79, px=(324,118,589,429)`
- Result cache is working: `[RESULT CACHE] Stored result for ...`
- Unity receives HTTP 200 OK responses with valid JSON

### What's Broken ❌

**Bug 1: Response JSON Schema Mismatch**
- Server console logs show bbox coordinates (`px=(x1,y1,x2,y2)`)
- BUT: These may not be serialized correctly in HTTP response JSON
- OR: Field names don't match between server JSON and Unity C# DTO

**Bug 2: Unity Overlay Not Rendering**
- Unity receives valid JSON with bbox data
- BUT: Bboxes not displayed on screen
- Likely causes: Parse failure, coordinate conversion error, or rendering disabled

**Bug 3: Excel Logging Model Incorrect**
- Same `frame_id` appears in multiple rows
- `final_state=Displayed` but has BOTH `unity_display_ts` AND `unity_drop_ts`
- Polling 404s are logged as final states (should be intermediate events only)

---

## PATCH A: Response JSON Contract Guarantee

### Current Problem
Worker logs show bbox in console, but no guarantee it reaches Unity:
```
[UDP WORKER BBOX] person conf=0.79, px=(324,118,589,429)
[POSE CROP] Person 0 ... 17 keypoints extracted
```

### Required Fix: Explicit JSON Schema

**Location**: `app/routes/response.py` (GET `/response/{session_id}/latest` and `/response/{session_id}/{frame_id}`)

**JSON Schema** (MUST match this structure):

```json
{
  "frame_id": 85,
  "session_id": "3cba1d85-7263-4bf7-958a-3835d5276294",
  "server_receive_ts": 1776389845280.822,
  "server_process_start_ts": 1776389845692.100,
  "server_process_end_ts": 1776389845894.700,
  "processing_time_ms": 202.6,

  "detections": [
    {
      "class_name": "person",
      "confidence": 0.79,
      "x1": 324,
      "y1": 118,
      "x2": 589,
      "y2": 429
    },
    {
      "class_name": "person",
      "confidence": 0.85,
      "x1": 650,
      "y1": 95,
      "x2": 890,
      "y2": 410
    }
  ],

  "poses": [
    {
      "person_index": 0,
      "bbox": [324, 118, 589, 429],
      "keypoints": [
        {"name": "nose", "x": 456, "y": 145, "score": 0.95},
        {"name": "left_eye", "x": 448, "y": 138, "score": 0.92},
        {"name": "right_eye", "x": 464, "y": 139, "score": 0.91}
      ],
      "num_keypoints": 17,
      "avg_score": 0.89
    }
  ],

  "num_persons": 2,
  "input_image_width": 640,
  "input_image_height": 480
}
```

### Field Mapping Rules

| Server Internal | Worker Log Format | JSON Response Field | Unity C# Field |
|----------------|-------------------|---------------------|----------------|
| YOLO bbox | `px=(x1,y1,x2,y2)` | `detections[].x1/y1/x2/y2` | `detections[i].x1/y1/x2/y2` |
| YOLO confidence | `conf=0.79` | `detections[].confidence` | `detections[i].confidence` |
| Pose bbox | Same as YOLO | `poses[].bbox` | `poses[i].bbox` |
| Keypoint coords | Internal array | `poses[].keypoints[].x/y` | `poses[i].keypoints[j].x/y` |

### Implementation Checklist

**Server Side** (`app/workers/udp_inference_worker.py`):

```python
# After YOLO + Pose inference completes:
result = {
    "frame_id": frame_id,
    "session_id": session_id,
    "server_receive_ts": receive_ts,
    "server_process_start_ts": process_start_ts,
    "server_process_end_ts": time.time(),
    "processing_time_ms": (time.time() - process_start_ts) * 1000,

    # ✅ CRITICAL: Include detections array with pixel coords
    "detections": [
        {
            "class_name": "person",
            "confidence": float(conf),  # numpy → Python float
            "x1": int(x1),
            "y1": int(y1),
            "x2": int(x2),
            "y2": int(y2)
        }
        for conf, (x1, y1, x2, y2) in yolo_results
        if conf >= 0.5  # Apply confidence threshold HERE
    ],

    # ✅ Include poses array
    "poses": [
        {
            "person_index": i,
            "bbox": [int(x1), int(y1), int(x2), int(y2)],
            "keypoints": [
                {
                    "name": kp_name,
                    "x": int(x_pixel),
                    "y": int(y_pixel),
                    "score": float(score)
                }
                for kp_name, (x_pixel, y_pixel, score) in person_keypoints
            ],
            "num_keypoints": len(person_keypoints),
            "avg_score": float(avg_score)
        }
        for i, (person_keypoints, avg_score) in enumerate(pose_results)
    ],

    "num_persons": len(yolo_results),
    "input_image_width": 640,
    "input_image_height": 480
}

# Store in cache (already converted to JSON-safe types)
await result_cache.set(session_id, frame_id, result)
```

**Validation Rule**:
- If worker log shows `[UDP WORKER BBOX] px=(324,118,589,429)`, then `detections[0]` MUST have `x1=324, y1=118, x2=589, y2=429`
- Run test: `curl http://localhost:8001/response/{session}/{frame} | jq '.detections[0]'` → should show bbox

---

## PATCH B: Unity Integration Checklist

### B.1 C# DTO Schema Alignment

**File**: `PoseInferenceRunManager.cs` or dedicated DTO file

**Required C# Classes** (field names MUST match JSON exactly):

```csharp
[Serializable]
public class InferenceResponse
{
    public int frame_id;
    public string session_id;
    public double server_receive_ts;
    public double server_process_start_ts;
    public double server_process_end_ts;
    public float processing_time_ms;

    public Detection[] detections;  // ✅ Match JSON: "detections"
    public PoseData[] poses;        // ✅ Match JSON: "poses"

    public int num_persons;
    public int input_image_width;
    public int input_image_height;
}

[Serializable]
public class Detection
{
    public string class_name;
    public float confidence;
    public int x1;  // ✅ Pixel coordinates (0-640)
    public int y1;  // ✅ Pixel coordinates (0-480)
    public int x2;
    public int y2;
}

[Serializable]
public class PoseData
{
    public int person_index;
    public int[] bbox;  // [x1, y1, x2, y2]
    public Keypoint[] keypoints;
    public int num_keypoints;
    public float avg_score;
}

[Serializable]
public class Keypoint
{
    public string name;
    public int x;  // ✅ Pixel coordinates
    public int y;
    public float score;
}
```

**Validation**: After deserialization, add temporary debug log:

```csharp
var response = JsonConvert.DeserializeObject<InferenceResponse>(jsonResponse);

// ✅ DIAGNOSTIC: Verify parse succeeded
if (response.detections != null && response.detections.Length > 0)
{
    var firstDet = response.detections[0];
    Debug.Log($"[PARSE VERIFY] First detection: {firstDet.class_name} " +
              $"conf={firstDet.confidence:F2} " +
              $"bbox=({firstDet.x1},{firstDet.y1},{firstDet.x2},{firstDet.y2})");
}
else
{
    Debug.LogWarning($"[PARSE VERIFY] ⚠ No detections in response! num_persons={response.num_persons}");
}
```

**Expected Log** (if server has bbox):
```
[PARSE VERIFY] First detection: person conf=0.79 bbox=(324,118,589,429)
```

---

### B.2 Coordinate Transformation: Source Image → Unity Overlay

**Source Image**: 640×480 (server processing resolution)
**Unity Overlay**: Canvas/RawImage with arbitrary size (e.g., 1920×1080 or screen resolution)

**Transformation Steps**:

```csharp
// Input: Bbox from server (pixel coords in 640×480 space)
int x1_source = detection.x1;  // e.g., 324
int y1_source = detection.y1;  // e.g., 118
int x2_source = detection.x2;  // e.g., 589
int y2_source = detection.y2;  // e.g., 429

// Source image dimensions (from response)
float sourceWidth = response.input_image_width;   // 640
float sourceHeight = response.input_image_height; // 480

// Unity overlay dimensions (Canvas RectTransform)
RectTransform overlayRect = overlayCanvas.GetComponent<RectTransform>();
float overlayWidth = overlayRect.rect.width;   // e.g., 1920
float overlayHeight = overlayRect.rect.height; // e.g., 1080

// Scale factors
float scaleX = overlayWidth / sourceWidth;   // 1920 / 640 = 3.0
float scaleY = overlayHeight / sourceHeight; // 1080 / 480 = 2.25

// Transform to overlay space
float x1_overlay = x1_source * scaleX;  // 324 * 3.0 = 972
float y1_overlay = y1_source * scaleY;  // 118 * 2.25 = 265.5
float x2_overlay = x2_source * scaleX;  // 589 * 3.0 = 1767
float y2_overlay = y2_source * scaleY;  // 429 * 2.25 = 965.25

// ✅ Handle mirror flip if needed (Quest camera might be mirrored)
if (isMirrored)
{
    // Flip X coordinates
    float temp_x1 = overlayWidth - x2_overlay;
    float temp_x2 = overlayWidth - x1_overlay;
    x1_overlay = temp_x1;
    x2_overlay = temp_x2;
}

// ✅ Unity UI uses bottom-left origin, may need Y flip
// If server uses top-left origin (typical for images):
float y1_overlay_flipped = overlayHeight - y2_overlay;
float y2_overlay_flipped = overlayHeight - y1_overlay;

// Create RectTransform for bbox overlay
Vector2 position = new Vector2(x1_overlay, y1_overlay_flipped);
Vector2 size = new Vector2(x2_overlay - x1_overlay, y2_overlay_flipped - y1_overlay_flipped);
```

**Diagnostic Log**:
```csharp
Debug.Log($"[COORD TRANSFORM] Source bbox: ({x1_source},{y1_source},{x2_source},{y2_source}) " +
          $"→ Overlay: ({x1_overlay:F0},{y1_overlay:F0},{x2_overlay:F0},{y2_overlay:F0}) " +
          $"scale=({scaleX:F2},{scaleY:F2}) mirror={isMirrored}");
```

---

### B.3 Mode=Both: Display BOTH Bbox AND Skeleton

**Current Problem**: In `mode=both`, Unity might only draw skeleton, ignoring bboxes.

**Required Behavior**:

```csharp
void DisplayFrame(FrameTrace trace)
{
    var response = trace.response as InferenceResponse;

    // ✅ Draw bounding boxes (if detections exist)
    if (response.detections != null && response.detections.Length > 0)
    {
        foreach (var det in response.detections)
        {
            DrawBoundingBox(det);  // Draw green box around person
        }
        Debug.Log($"[DISPLAY] Drew {response.detections.Length} bboxes");
    }

    // ✅ Draw pose skeletons (if poses exist)
    if (response.poses != null && response.poses.Length > 0)
    {
        foreach (var pose in response.poses)
        {
            DrawPoseSkeleton(pose);  // Draw keypoints + connections
        }
        Debug.Log($"[DISPLAY] Drew {response.poses.Length} skeletons");
    }
}
```

**UI Controls** (allow independent toggling):

```csharp
[SerializeField] private bool showBoundingBoxes = true;
[SerializeField] private bool showSkeletons = true;

void DisplayFrame(FrameTrace trace)
{
    if (showBoundingBoxes && response.detections != null)
    {
        // Draw bboxes
    }

    if (showSkeletons && response.poses != null)
    {
        // Draw skeletons
    }
}
```

**Validation**:
- In `mode=both`: BOTH bboxes and skeletons should be visible
- User can disable skeleton but keep bboxes visible
- If server detects 4 persons → Unity draws 4 bboxes + 4 skeletons

---

## PATCH C: Excel Logging Model Corrections

### C.1 Polling 404 is NOT a Final State

**Current Problem**: Logs show hundreds of 404s, then later 200 OK for same frame.

**Rule**: `GET /response/.../123 → 404 Not Found` means "result not ready yet", NOT "frame failed".

**Implementation**:

```python
# Unity polling loop
while elapsed < timeout:
    response = requests.get(f"{base_url}/response/{session_id}/{frame_id}")

    if response.status_code == 200:
        # ✅ Result ready - process and mark Completed
        result = response.json()
        trace.MarkCompleted(receiveTs)
        break
    elif response.status_code == 404:
        # ⏳ Not ready yet - continue polling (NOT an error)
        # Do NOT write to Excel here
        # Do NOT set final_state
        yield WaitForSeconds(0.1)
    else:
        # ❌ Actual error (500, 502, etc.)
        trace.MarkFailed(f"HTTP {response.status_code}")
        break
```

**Event Logging** (separate from final state):
- You can log `[POLL EVENT] Frame 123 polled, got 404` to EventLog sheet
- But do NOT set `final_state=Failed` or write to InferenceLog main sheet

---

### C.2 InferenceLog Main Sheet: One Frame One Row

**Schema** (`InferenceLog` sheet):

| Column | Type | Description | Constraints |
|--------|------|-------------|-------------|
| `frame_id` | int | Unique frame ID | **PRIMARY KEY** |
| `session_id` | string | Session GUID | NOT NULL |
| `unity_send_ts` | long | When Unity sent frame (ms) | NOT NULL |
| `server_receive_ts` | long | When server received UDP packet | |
| `server_process_start_ts` | long | When worker started inference | |
| `server_process_end_ts` | long | When inference completed | |
| `unity_response_receive_ts` | long | When Unity got 200 OK | |
| `unity_display_ts` | long | When Unity displayed frame | **XOR with drop_ts** |
| `unity_drop_ts` | long | When Unity dropped frame | **XOR with display_ts** |
| `final_state` | string | Pending / Displayed / Dropped / Failed | NOT NULL |
| `drop_reason` | string | Why dropped (if Dropped) | |
| `error_reason` | string | Error details (if Failed) | |
| `num_persons` | int | Detections count | |
| `bbox_count` | int | Same as num_persons | |
| `avg_confidence` | float | Average detection confidence | |
| `processing_time_ms` | float | Server processing time | |
| `e2e_latency_ms` | float | Unity send → receive | |

**Validation Rules** (enforced before Excel export):

```python
def validate_inference_log(rows):
    frame_ids = [row["frame_id"] for row in rows]

    # Rule 1: No duplicate frame_ids
    assert len(frame_ids) == len(set(frame_ids)), "Duplicate frame_ids found!"

    for row in rows:
        # Rule 2: Exactly one final_state
        assert row["final_state"] in ["Pending", "Displayed", "Dropped", "Failed"]

        # Rule 3: Displayed XOR Dropped
        if row["final_state"] == "Displayed":
            assert row["unity_display_ts"] is not None
            assert row["unity_drop_ts"] is None
        elif row["final_state"] == "Dropped":
            assert row["unity_drop_ts"] is not None
            assert row["unity_display_ts"] is None

        # Rule 4: All rows have send timestamp
        assert row["unity_send_ts"] is not None

        # Rule 5: Dropped frames must have server completion
        if row["final_state"] == "Dropped":
            assert row["server_process_end_ts"] is not None, \
                "Dropped frame must have completed inference"
```

**When to Write Final Row**:

```csharp
// Unity side - when frame's final state is determined
void TryDisplayNewestFrame()
{
    // Select newest completed frame
    FrameTrace newest = GetNewestCompleted();

    // Mark older frames as Dropped
    foreach (var older in olderFrames)
    {
        older.MarkDropped(timestamp, "superseded_by_newer");
        // ✅ NOW write to Excel (final state determined)
        ExportToExcel(older);
    }

    // Display newest
    DisplayFrame(newest);
    newest.MarkDisplayed(timestamp);
    // ✅ NOW write to Excel (final state determined)
    ExportToExcel(newest);
}
```

**NOT here**:
```csharp
// ❌ WRONG: Don't export during polling
IEnumerator PollForResult(int frameId)
{
    response = await Get(url);
    if (response.status == 404)
    {
        // ❌ DON'T write to Excel here!
        continue;
    }
}
```

---

### C.3 EventLog Sheet (Optional, for Debugging)

If you want to log intermediate events (polling 404s, cache hits, etc.), use a separate sheet:

**Schema** (`EventLog` sheet):

| Column | Type | Description |
|--------|------|-------------|
| `timestamp` | long | Event time (ms) |
| `session_id` | string | Session GUID |
| `frame_id` | int | Frame ID (nullable) |
| `event_type` | string | UDP_INGEST / WORKER_START / WORKER_END / POLL_404 / POLL_200 / DISPLAY / DROP |
| `details` | string | Additional info (JSON or text) |

**Example Rows**:
```
timestamp,session_id,frame_id,event_type,details
1776389845280,3cba1d85,85,UDP_INGEST,"size=9304 bytes"
1776389845692,3cba1d85,85,WORKER_START,"admitted to queue"
1776389845894,3cba1d85,85,WORKER_END,"processing_time=202ms"
1776389845900,3cba1d85,85,POLL_404,"result not ready"
1776389845920,3cba1d85,85,POLL_200,"result received"
1776389846100,3cba1d85,85,DISPLAY,"displayed on frame"
```

**Separation of Concerns**:
- **EventLog**: All intermediate events (debugging, analysis)
- **InferenceLog**: One final summary row per frame (production metrics)

---

## PATCH D: Verification Checklist (Post-Fix)

After applying these patches, verify:

### Server Side
```bash
# 1. Check worker log shows bbox
grep "UDP WORKER BBOX" server.log
# Expected: [UDP WORKER BBOX] person conf=0.79, px=(324,118,589,429)

# 2. Check JSON response includes bbox
curl http://localhost:8001/response/{session}/{frame} | jq '.detections[0]'
# Expected:
# {
#   "class_name": "person",
#   "confidence": 0.79,
#   "x1": 324,
#   "y1": 118,
#   "x2": 589,
#   "y2": 429
# }
```

### Unity Side
```csharp
// Check logs after receiving 200 OK
// Expected:
// [PARSE VERIFY] First detection: person conf=0.79 bbox=(324,118,589,429)
// [COORD TRANSFORM] Source bbox: (324,118,589,429) → Overlay: (972,266,1767,965)
// [DISPLAY] Drew 2 bboxes
// [DISPLAY] Drew 2 skeletons
```

### Excel Export
```python
# Open InferenceLog sheet
df = pd.read_excel("telemetry.xlsx", sheet_name="InferenceLog")

# Validation
assert df["frame_id"].is_unique, "Duplicate frame_ids!"
assert all(df["final_state"].isin(["Pending", "Displayed", "Dropped", "Failed"]))

# Check XOR constraint
displayed = df[df["final_state"] == "Displayed"]
assert all(displayed["unity_display_ts"].notna())
assert all(displayed["unity_drop_ts"].isna())

dropped = df[df["final_state"] == "Dropped"]
assert all(dropped["unity_drop_ts"].notna())
assert all(dropped["unity_display_ts"].isna())
```

---

## Summary of Changes

| Component | Issue | Fix | Location |
|-----------|-------|-----|----------|
| **Response JSON** | Bbox in console but not in JSON | Add `detections[]` with `x1/y1/x2/y2` | `app/workers/udp_inference_worker.py` |
| **Unity DTO** | Field name mismatch | Align C# fields with JSON keys exactly | `PoseInferenceRunManager.cs` |
| **Coord Transform** | Bbox not displayed | Add 640×480 → overlay transformation | `DisplayFrame()` |
| **Mode=Both** | Only skeleton visible | Draw BOTH bboxes and skeleton | `DisplayFrame()` |
| **Polling 404** | Logged as final state | Treat 404 as "not ready", not error | `ListenForResponseHTTP()` |
| **Excel Schema** | Duplicate frame rows | One frame one row, validate before export | Excel export logic |
| **Final State** | Incorrect timing | Set final_state only in `TryDisplayNewestFrame()` | `TryDisplayNewestFrame()` |

---

**Status**: Patch complete, ready to apply
**Files to Modify**:
- `app/workers/udp_inference_worker.py` (server)
- `PoseInferenceRunManager.cs` (Unity)
- Excel export logic (Unity or server)

**Testing Priority**:
1. Verify JSON response has `detections[]` array
2. Verify Unity logs show `[PARSE VERIFY]` with correct bbox
3. Verify bboxes appear on screen
4. Verify Excel has one row per frame with correct final_state
