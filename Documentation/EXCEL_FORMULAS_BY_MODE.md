# Excel Metrics - Formulas & Mode Comparison

## Overview

This document provides the exact calculation formula for each Excel column and compares how they differ between Object Detection and Pose Estimation modes.

**Excel Columns**: 19 total
**Code Location**: `vision_server/app/routes/infer_human.py` (lines 380-469)
**Logger**: `vision_server/debug/inference_logger.py` (lines 52-124)

---

## Excel Column Formulas

### Column 1: timestamp

| Property | Value |
|----------|-------|
| **Type** | `string` |
| **Formula** | `datetime.now().strftime("%Y-%m-%d %H:%M:%S.%f")[:-3]` |
| **Object Detection** | Same |
| **Pose Estimation** | Same |
| **Code** | `inference_logger.py:105` |

**Calculation**:
```python
datetime.now().strftime("%Y-%m-%d %H:%M:%S.%f")[:-3]
# Example: "2026-04-06 14:23:45.123"
# [:-3] truncates to milliseconds (removes last 3 microsecond digits)
```

✅ **Mode Difference**: None

---

### Column 2: scene

| Property | Value |
|----------|-------|
| **Type** | `string` |
| **Formula** | `request.headers.get("X-Scene-Name", "Unknown")` |
| **Object Detection** | `"MultiObjectDetection"` |
| **Pose Estimation** | `"PoseEstimation"` |
| **Code** | `infer_human.py:383` |

**Calculation**:
```python
scene = request.headers.get("X-Scene-Name", "Unknown")
```

⚠️ **Mode Difference**: **Different values** based on Unity scene

| Mode | Scene Name | Unity Source |
|------|------------|--------------|
| Object Detection | `"MultiObjectDetection"` | `SentisInferenceRunManager.cs:391` |
| Pose Estimation | `"PoseEstimation"` | `PoseInferenceRunManager.cs:220` |

---

### Column 3: frame_id

| Property | Value |
|----------|-------|
| **Type** | `int` |
| **Formula** | `int(request.headers.get("X-Frame-Id", "-1"))` |
| **Object Detection** | Same |
| **Pose Estimation** | Same |
| **Code** | `infer_human.py:384-388` |

**Calculation**:
```python
frame_id_str = request.headers.get("X-Frame-Id", "-1")
try:
    frame_id = int(frame_id_str)
except (ValueError, TypeError):
    frame_id = -1
```

✅ **Mode Difference**: None

**⚠️ Note**: This is Frame N, but timing data in columns 4-8 are from Frame N-1 (see Issues doc).

---

### Column 4: latency_ms

| Property | Value |
|----------|-------|
| **Type** | `float` |
| **Formula** | `e2e_ms if e2e_ms > 0 else processing_time_ms` |
| **Object Detection** | Same |
| **Pose Estimation** | Same |
| **Code** | `infer_human.py:420, 450` |

**Calculation**:
```python
# Read from Unity header (Frame N-1 data)
e2e_ms = float(request.headers.get("X-E2E-Ms", "0"))

# Use client E2E if available, otherwise use server processing time
latency_ms = e2e_ms if e2e_ms > 0 else processing_time_ms
```

**Logged Value**:
```python
log_async(
    latency_ms=e2e_ms if e2e_ms > 0 else processing_time_ms,
    ...
)
```

✅ **Mode Difference**: None

**⚠️ Critical Issue**:
- This is **Frame N-1's E2E latency**, not Frame N
- First frame (frame_id=1) will have `latency_ms = processing_time_ms` (no prior frame)

---

### Column 5: server_proc_ms

| Property | Value |
|----------|-------|
| **Type** | `float` |
| **Formula** | `(time.time() - start_time) * 1000.0` |
| **Object Detection** | Same formula, different value |
| **Pose Estimation** | Same formula, different value |
| **Code** | `infer_human.py:85, 359` |

**Calculation**:
```python
# Start timing (line 85)
start_time = time.time()

# ... run inference (lines 103-281) ...

# Calculate processing time (line 359)
processing_time_ms = (time.time() - start_time) * 1000.0
```

**Logged Value**:
```python
log_async(
    server_proc_ms=processing_time_ms,  # Frame N data
    ...
)
```

⚠️ **Mode Difference**: **Different processing times**

| Mode | What It Includes | Typical Range |
|------|------------------|---------------|
| Object Detection | Image decode + YOLOv8 + JSON serialize | 50-150ms |
| Pose Estimation | Image decode + YOLOv8 + RTMPose + Segmentation + JSON serialize | 150-300ms |

**⚠️ Critical Issue**: This is **Frame N's server time**, but `latency_ms` is Frame N-1's!

---

### Column 6: upload_ms

| Property | Value |
|----------|-------|
| **Type** | `float` |
| **Formula** | `float(request.headers.get("X-Upload-Ms", "0"))` |
| **Object Detection** | Same |
| **Pose Estimation** | Same |
| **Code** | `infer_human.py:421` |

**Calculation**:
```python
# Read from Unity header (Frame N-1 data)
upload_ms = float(request.headers.get("X-Upload-Ms", "0"))
```

**Unity Calculation** (Frame N-1):
```csharp
// SentisInferenceRunManager.cs:406-420
uploadStartTime = Time.realtimeSinceStartup;
// ... SendWebRequest() ...
while (!asyncOp.isDone && request.uploadProgress < 1.0f)
    yield return null;
uploadDoneTime = Time.realtimeSinceStartup;
uploadMs = (uploadDoneTime - uploadStartTime) * 1000f;
```

✅ **Mode Difference**: None in calculation method

**⚠️ Note**: This is **Frame N-1's upload time**

---

### Column 7: download_ms

| Property | Value |
|----------|-------|
| **Type** | `float` |
| **Formula** | `float(request.headers.get("X-Download-Ms", "0"))` |
| **Object Detection** | Same formula, different value |
| **Pose Estimation** | Same formula, different value |
| **Code** | `infer_human.py:422` |

**Calculation**:
```python
# Read from Unity header (Frame N-1 data)
download_ms = float(request.headers.get("X-Download-Ms", "0"))
```

**Unity Calculation** (Frame N-1):
```csharp
// SentisInferenceRunManager.cs:457
downloadMs = e2eMs - uploadMs - serverProcMs - parseMs;
```

⚠️ **Mode Difference**: **Different values** due to response size

| Mode | Response Size | Typical download_ms |
|------|---------------|---------------------|
| Object Detection | ~50KB | 20-50ms |
| Pose Estimation | ~500KB-2MB | 100-400ms |

**⚠️ Note**: This is **Frame N-1's download time**

---

### Column 8: parse_ms

| Property | Value |
|----------|-------|
| **Type** | `float` |
| **Formula** | `float(request.headers.get("X-Parse-Ms", "0"))` |
| **Object Detection** | Same formula, different method |
| **Pose Estimation** | Same formula, different method |
| **Code** | `infer_human.py:423` |

**Calculation**:
```python
# Read from Unity header (Frame N-1 data)
parse_ms = float(request.headers.get("X-Parse-Ms", "0"))
```

**Unity Calculation** (Frame N-1):

#### Object Detection:
```csharp
// SentisInferenceRunManager.cs:437-451
parseStartTime = Time.realtimeSinceStartup;
ServerResponse response = JsonUtility.FromJson<ServerResponse>(jsonResponse);
parseMs = (Time.realtimeSinceStartup - parseStartTime) * 1000f;
```

#### Pose Estimation:
```csharp
// PoseInferenceRunManager.cs:264-333
parseStartTime = Time.realtimeSinceStartup;

// Extract fields manually
string skeletonJson = ExtractJsonField(jsonResponse, "skeleton");
response.skeleton = JsonConvert.DeserializeObject<SkeletonData>(skeletonJson);

string detectionsJson = ExtractJsonField(jsonResponse, "detections");
response.detections = JsonConvert.DeserializeObject<DetectionResultData>(detectionsJson);

string procTimeStr = ExtractSimpleJsonValue(jsonResponse, "processing_time_ms");

parseMs = (Time.realtimeSinceStartup - parseStartTime) * 1000f;
```

⚠️ **Mode Difference**: **Different parsing complexity**

| Mode | Parsing Method | Typical parse_ms |
|------|----------------|------------------|
| Object Detection | Simple `JsonUtility.FromJson` | 5-15ms |
| Pose Estimation | Custom field extraction + `JsonConvert.DeserializeObject` | 15-50ms |

**⚠️ Note**: This is **Frame N-1's parse time**

---

### Column 9: upload_bytes

| Property | Value |
|----------|-------|
| **Type** | `int` |
| **Formula** | `int(request.headers.get("X-Upload-Bytes", "0"))` |
| **Object Detection** | Same |
| **Pose Estimation** | Same |
| **Code** | `infer_human.py:424` |

**Calculation**:
```python
# Read from Unity header (Frame N-1 data)
upload_bytes = int(request.headers.get("X-Upload-Bytes", "0"))
```

**Unity Calculation** (Frame N-1):
```csharp
// SentisInferenceRunManager.cs:380-381
byte[] jpegBytes = tex2D.EncodeToJPG(90);
int uploadBytes = jpegBytes.Length;
```

✅ **Mode Difference**: None (same JPEG quality 90, same resolution)

**Typical Value**: 100,000-150,000 bytes (~125KB for 1280×960 JPEG)

**⚠️ Note**: This is **Frame N-1's upload size**

---

### Column 10: download_bytes

| Property | Value |
|----------|-------|
| **Type** | `int` |
| **Formula** | `int(request.headers.get("X-Download-Bytes", "0"))` |
| **Object Detection** | Same formula, different value |
| **Pose Estimation** | Same formula, different value |
| **Code** | `infer_human.py:425` |

**Calculation**:
```python
# Read from Unity header (Frame N-1 data)
download_bytes = int(request.headers.get("X-Download-Bytes", "0"))
```

**Unity Calculation** (Frame N-1):
```csharp
// SentisInferenceRunManager.cs:455
int downloadBytes = (int)request.downloadedBytes;
```

✅ **Mode Difference**: **Minimal difference** (both modes return segmentation + depth)

| Mode | Response Content | Typical download_bytes |
|------|------------------|------------------------|
| Object Detection | Detections + segmentation(75KB) + depth(300KB) + dummy skeleton | 425,000-435,000 (~425KB) |
| Pose Estimation | Detections + segmentation(75KB) + depth(300KB) + real skeleton | 430,000-440,000 (~435KB) |

**Note**: Both modes return the **same response structure** with segmentation mask and depth map (lines 261-275 in `infer_human.py`). The only difference is skeleton data size (~5-10KB).

**⚠️ Note**: This is **Frame N-1's download size**

---

### Column 11: server_pct

| Property | Value |
|----------|-------|
| **Type** | `float` |
| **Formula** | `(processing_time_ms / e2e_ms) * 100.0` if e2e_ms > 0 else 100.0 |
| **Object Detection** | Same formula |
| **Pose Estimation** | Same formula |
| **Code** | `infer_human.py:436-444` |

**Calculation**:
```python
if e2e_ms > 0:
    server_pct = (processing_time_ms / e2e_ms) * 100.0
else:
    # No client timing available, use 100% for server
    server_pct = 100.0
```

✅ **Mode Difference**: None in formula

❌ **Critical Issue**: **Uses data from different frames!**

```python
server_pct = (processing_time_ms / e2e_ms) * 100.0
            ↑ Frame N          ↑ Frame N-1
```

**This percentage is meaningless** because:
- Numerator: Frame N's server processing time
- Denominator: Frame N-1's E2E latency

**Example**:
```
Frame 2 processing:
- processing_time_ms = 200ms (Frame 2)
- e2e_ms = 245ms (Frame 1, from header)
- server_pct = (200/245)*100 = 81.6%

What does this mean? Nothing useful!
```

---

### Column 12: upload_pct

| Property | Value |
|----------|-------|
| **Type** | `float` |
| **Formula** | `(upload_ms / e2e_ms) * 100.0` if e2e_ms > 0 else 0.0 |
| **Object Detection** | Same |
| **Pose Estimation** | Same |
| **Code** | `infer_human.py:436-444` |

**Calculation**:
```python
if e2e_ms > 0:
    upload_pct = (upload_ms / e2e_ms) * 100.0
else:
    upload_pct = 0.0
```

✅ **Mode Difference**: None in formula

✅ **Frame Alignment**: Correct (both Frame N-1 data)

```python
upload_pct = (upload_ms / e2e_ms) * 100.0
            ↑ Frame N-1    ↑ Frame N-1
```

**Example**:
```
Frame 1 header in Frame 2 request:
- upload_ms = 45.2ms (Frame 1)
- e2e_ms = 245.1ms (Frame 1)
- upload_pct = (45.2/245.1)*100 = 18.4%
✅ Correct - both from same frame
```

---

### Column 13: download_pct

| Property | Value |
|----------|-------|
| **Type** | `float` |
| **Formula** | `(download_ms / e2e_ms) * 100.0` if e2e_ms > 0 else 0.0 |
| **Object Detection** | Same |
| **Pose Estimation** | Same |
| **Code** | `infer_human.py:436-444` |

**Calculation**:
```python
if e2e_ms > 0:
    download_pct = (download_ms / e2e_ms) * 100.0
else:
    download_pct = 0.0
```

✅ **Mode Difference**: None in formula

✅ **Frame Alignment**: Correct (both Frame N-1 data)

```python
download_pct = (download_ms / e2e_ms) * 100.0
              ↑ Frame N-1      ↑ Frame N-1
```

---

### Column 14: detection_count

| Property | Value |
|----------|-------|
| **Type** | `int` |
| **Formula** | Depends on mode |
| **Object Detection** | `len(detections_result.detections)` |
| **Pose Estimation** | `sum(1 for p in skeleton.persons if p is not None)` |
| **Code** | `infer_human.py:407-416` |

**Calculation**:

#### Object Detection Mode:
```python
detection_count = 0
if mode == "detection":
    if detections_result and detections_result.detections:
        detection_count = len(detections_result.detections)
```

#### Pose Estimation Mode:
```python
detection_count = 0
if mode in ["pose", "both"]:
    if skeleton and skeleton.persons:
        detection_count = sum(1 for p in skeleton.persons if p is not None)
```

❌ **Mode Difference**: **Completely different sources!**

| Mode | Source | What It Counts | Frame |
|------|--------|----------------|-------|
| Object Detection | `detections_result.detections` | Number of person bounding boxes from YOLO | N |
| Pose Estimation | `skeleton.persons` | Number of non-None persons with keypoints | N |

**⚠️ Issue**: In `mode="both"`:
- YOLO might detect 3 persons
- But pose estimation might fail for 1 person
- `skeleton.persons = [pose1, None, pose3]`
- `detection_count = 2` (counts non-None)
- But `avg_confidence` uses all 3 detections!

**Example**:
```
Frame with 3 person detections:
├─ detections_result.detections: [person1, person2, person3]
└─ skeleton.persons: [pose1, None, pose3]  # person2 pose failed

Excel logs:
├─ detection_count = 2  (from skeleton, Frame N)
└─ avg_confidence = (conf1+conf2+conf3)/3  (from detections, Frame N)

❌ Counting 2 but averaging 3!
```

---

### Column 15: avg_confidence

| Property | Value |
|----------|-------|
| **Type** | `float` (rounded to 4 decimals) |
| **Formula** | `sum(confidences) / len(confidences)` |
| **Object Detection** | Same |
| **Pose Estimation** | Same |
| **Code** | `infer_human.py:390-394, inference_logger.py:119` |

**Calculation**:
```python
avg_confidence = 0.0
if detections_result and detections_result.detections:
    confidences = [d.confidence for d in detections_result.detections]
    avg_confidence = sum(confidences) / len(confidences) if confidences else 0.0
```

**Logged Value**:
```python
# inference_logger.py:119
round(avg_confidence, 4)  # Round to 4 decimal places
```

✅ **Mode Difference**: None in formula (both use `detections_result`)

⚠️ **Source**: Always from `detections_result.detections` (YOLO output, Frame N)

**⚠️ Issue**: In Pose mode, this uses YOLO detections, but `detection_count` uses skeleton data → mismatch!

---

### Column 16: keypoint_avg_conf

| Property | Value |
|----------|-------|
| **Type** | `float` (rounded to 4 decimals) |
| **Formula** | `sum(all_keypoint_scores) / len(all_keypoint_scores)` |
| **Object Detection** | Should be 0.0, but might not be |
| **Pose Estimation** | Calculated from skeleton |
| **Code** | `infer_human.py:396-405, inference_logger.py:120` |

**Calculation**:
```python
keypoint_avg_conf = 0.0
if skeleton and skeleton.persons:  # ⚠️ No mode check!
    all_keypoint_scores = []
    for person in skeleton.persons:
        if person is not None and person.keypoints:
            scores = [kp.score for kp in person.keypoints if kp.score > 0]
            all_keypoint_scores.extend(scores)
    if all_keypoint_scores:
        keypoint_avg_conf = sum(all_keypoint_scores) / len(all_keypoint_scores)
```

**Logged Value**:
```python
# inference_logger.py:120
round(keypoint_avg_conf, 4)  # Round to 4 decimal places
```

❌ **Mode Difference**: **Should differ, but code doesn't check mode!**

| Mode | Expected Behavior | Actual Behavior |
|------|-------------------|-----------------|
| Object Detection | Always `0.0` (no pose data) | Might be non-zero if dummy skeleton has keypoints! |
| Pose Estimation | Calculate from keypoints | Calculated correctly |

**⚠️ Critical Issue**: Missing mode check!

**Should be**:
```python
keypoint_avg_conf = 0.0
if mode in ["pose", "both"]:  # ✅ Add mode check
    if skeleton and skeleton.persons:
        # ... calculate ...
```

---

### Column 17: image_width

| Property | Value |
|----------|-------|
| **Type** | `int` |
| **Formula** | `pil_image.size[0]` |
| **Object Detection** | Same |
| **Pose Estimation** | Same |
| **Code** | `infer_human.py:91, 463` |

**Calculation**:
```python
# infer_human.py:91
pil_image = Image.open(io.BytesIO(contents)).convert("RGB")
img_width, img_height = pil_image.size

# infer_human.py:463
log_async(
    image_width=img_width,
    ...
)
```

✅ **Mode Difference**: None

**Typical Value**: 1280 (from Quest camera)

---

### Column 18: image_height

| Property | Value |
|----------|-------|
| **Type** | `int` |
| **Formula** | `pil_image.size[1]` |
| **Object Detection** | Same |
| **Pose Estimation** | Same |
| **Code** | `infer_human.py:91, 464` |

**Calculation**:
```python
pil_image = Image.open(io.BytesIO(contents)).convert("RGB")
img_width, img_height = pil_image.size

log_async(
    image_height=img_height,
    ...
)
```

✅ **Mode Difference**: None

**Typical Value**: 960 (from Quest camera)

---

### Column 19: model_used

| Property | Value |
|----------|-------|
| **Type** | `string` |
| **Formula** | `f"yolo+pose_{mode}"` |
| **Object Detection** | `"yolo+pose_detection"` |
| **Pose Estimation** | `"yolo+pose_both"` or `"yolo+pose_pose"` |
| **Code** | `infer_human.py:465` |

**Calculation**:
```python
model_used = f"yolo+pose_{mode}"
```

⚠️ **Mode Difference**: **Different strings**

| Mode Value | model_used String |
|------------|-------------------|
| `"detection"` | `"yolo+pose_detection"` |
| `"pose"` | `"yolo+pose_pose"` |
| `"both"` | `"yolo+pose_both"` |

**Note**: String format is `yolo+pose_{mode}`, where mode is the query parameter from Unity.

---

## Summary Table: Mode Differences

| Column # | Column Name | Object Detection | Pose Estimation | Difference? |
|----------|-------------|------------------|-----------------|-------------|
| 1 | timestamp | Server timestamp | Server timestamp | ✅ Same |
| 2 | scene | "MultiObjectDetection" | "PoseEstimation" | ⚠️ Different value |
| 3 | frame_id | From header | From header | ✅ Same |
| 4 | latency_ms | From header (N-1) | From header (N-1) | ✅ Same |
| 5 | server_proc_ms | YOLOv8 only (N) | YOLOv8+RTMPose (N) | ⚠️ Different value |
| 6 | upload_ms | From header (N-1) | From header (N-1) | ✅ Same |
| 7 | download_ms | From header (N-1) | From header (N-1) | ✅ Similar (both ~80-120ms) |
| 8 | parse_ms | Simple parsing (N-1) | Complex parsing (N-1) | ⚠️ Different value |
| 9 | upload_bytes | From header (N-1) | From header (N-1) | ✅ Same |
| 10 | download_bytes | ~425KB response (N-1) | ~435KB response (N-1) | ✅ Minimal difference (~10KB) |
| 11 | server_pct | ❌ Frame N / N-1 | ❌ Frame N / N-1 | ✅ Same (both wrong) |
| 12 | upload_pct | Frame N-1 / N-1 | Frame N-1 / N-1 | ✅ Same |
| 13 | download_pct | Frame N-1 / N-1 | Frame N-1 / N-1 | ✅ Same |
| 14 | detection_count | From detections (N) | From skeleton (N) | ❌ Different source |
| 15 | avg_confidence | From detections (N) | From detections (N) | ✅ Same |
| 16 | keypoint_avg_conf | Should be 0.0 | From skeleton (N) | ❌ Should differ |
| 17 | image_width | From image | From image | ✅ Same |
| 18 | image_height | From image | From image | ✅ Same |
| 19 | model_used | "yolo+pose_detection" | "yolo+pose_both" | ⚠️ Different value |

---

## Critical Issues Summary

### ❌ Issue 1: Frame Misalignment (Affects ALL modes)

**Columns Affected**: 4, 6-13

**Problem**: Timing data (columns 4, 6-8) and percentages (11-13) are from **Frame N-1**, but detection data (14-16) is from **Frame N**.

**Formula Impact**:
```
Excel Row for frame_id=42:
├─ latency_ms (Frame 41)
├─ server_proc_ms (Frame 42)  ❌ MISMATCH
├─ upload_ms (Frame 41)
├─ download_ms (Frame 41)
├─ parse_ms (Frame 41)
├─ detection_count (Frame 42)  ❌ MISMATCH
└─ avg_confidence (Frame 42)   ❌ MISMATCH
```

---

### ❌ Issue 2: server_pct Uses Mixed Frames (Affects ALL modes)

**Column Affected**: 11

**Problem**:
```python
server_pct = (processing_time_ms / e2e_ms) * 100.0
            ↑ Frame N          ↑ Frame N-1
```

**Impact**: Percentage is meaningless because numerator and denominator are from different frames.

---

### ❌ Issue 3: detection_count vs avg_confidence Mismatch (Affects POSE mode only)

**Columns Affected**: 14, 15

**Problem**: In Pose mode:
- `detection_count` uses `skeleton.persons` (might have None entries)
- `avg_confidence` uses `detections_result.detections` (all YOLO detections)

**Example**:
```
3 YOLO detections, 1 pose failed:
├─ detections: [person1, person2, person3]
├─ skeleton: [pose1, None, pose3]
├─ detection_count = 2 (from skeleton)
└─ avg_confidence = average of 3 (from detections)
❌ INCONSISTENT
```

---

### ⚠️ Issue 4: keypoint_avg_conf Missing Mode Check (Affects DETECTION mode)

**Column Affected**: 16

**Problem**: No mode check before calculating keypoint confidence.

**Current Code**:
```python
if skeleton and skeleton.persons:  # ⚠️ No mode check
    # Calculate keypoint_avg_conf
```

**Should Be**:
```python
if mode in ["pose", "both"] and skeleton and skeleton.persons:
    # Calculate keypoint_avg_conf
```

**Impact**: Object Detection mode might log non-zero `keypoint_avg_conf` if dummy skeleton has keypoints.

---

## Recommendations

### Fix Priority

1. **🔴 HIGH**: Fix frame alignment (Issue 1)
2. **🔴 HIGH**: Fix server_pct calculation (Issue 2)
3. **🟡 MEDIUM**: Fix detection_count source inconsistency (Issue 3)
4. **🟢 LOW**: Add mode check for keypoint_avg_conf (Issue 4)

### Verification Tests

#### Test 1: Check Object Detection keypoint_avg_conf
```bash
# Run Object Detection mode
# Check Excel: keypoint_avg_conf column
# Expected: All rows = 0.0
# If non-zero: Issue 4 confirmed
```

#### Test 2: Check Pose Mode detection_count vs avg_confidence
```bash
# Run Pose mode with 3 persons where 1 pose fails
# Check Excel:
# - detection_count = 2 or 3?
# - avg_confidence = average of how many?
# If mismatch: Issue 3 confirmed
```

#### Test 3: Check Frame Alignment
```bash
# Run with distinct server processing times per frame
# Frame 1: 150ms, Frame 2: 200ms, Frame 3: 180ms
# Check Excel:
# - Row 1: server_proc_ms=150, latency_ms=0
# - Row 2: server_proc_ms=200, latency_ms=??? (should be Frame 1's E2E)
# - Row 3: server_proc_ms=180, latency_ms=??? (should be Frame 2's E2E)
# If server_proc and latency don't align: Issue 1 confirmed
```

---

**Created**: 2026-04-06
**Author**: Claude (Anthropic AI)
