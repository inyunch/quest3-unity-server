# Excel Metrics Calculation - Issues Found

## Critical Issues

### 🔴 Issue 1: Frame Misalignment (Data Mixing)

**Problem**: Excel rows mix data from **different frames**.

#### What happens during Frame N processing:

```
Unity sends:
  - Image: Frame N
  - HTTP Headers: Frame N-1 timing data (X-E2E-Ms, X-Upload-Ms, etc.)

Server logs to Excel:
  - frame_id: N
  - latency_ms, upload_ms, download_ms, parse_ms: Frame N-1 data (from headers)
  - server_proc_ms: Frame N data (current processing)
  - detection_count, avg_confidence: Frame N data (current results)
```

#### Example Excel Row Breakdown:

| Column | Frame | Value | Source |
|--------|-------|-------|--------|
| frame_id | **N** | 42 | Current request |
| latency_ms | **N-1** | 245.1 | X-E2E-Ms header (previous frame) |
| server_proc_ms | **N** | 234.5 | Current inference |
| upload_ms | **N-1** | 45.2 | X-Upload-Ms header |
| download_ms | **N-1** | 192.3 | X-Download-Ms header |
| detection_count | **N** | 3 | Current frame detections |
| avg_confidence | **N** | 0.85 | Current frame results |

**Impact**:
- Cannot correlate timing with detection results in the same row
- Frame 1 has `latency_ms=0` (no previous frame data)
- All percentages are calculated using mismatched frame data

**Code Location**: `app/routes/infer_human.py:447-466`

---

### 🔴 Issue 2: Percentage Calculation Using Wrong Frames

**Problem**: Percentages are calculated using data from **different frames**.

#### Current Code:
```python
# infer_human.py:436-439
if e2e_ms > 0:
    server_pct = (processing_time_ms / e2e_ms) * 100.0
    upload_pct = (upload_ms / e2e_ms) * 100.0
    download_pct = (download_ms / e2e_ms) * 100.0
```

Where:
- `processing_time_ms` = Frame N (current)
- `e2e_ms` = Frame N-1 (from header)
- `upload_ms` = Frame N-1 (from header)
- `download_ms` = Frame N-1 (from header)

#### Example:

**Frame 1**:
- Server processes: 150ms
- Excel logs: `server_proc_ms=150`, `latency_ms=0`, `server_pct=100%`

**Frame 2**:
- Server processes: 200ms (Frame 2)
- Headers contain: e2e_ms=245ms (Frame 1)
- Excel logs:
  - `server_proc_ms=200ms` (Frame 2)
  - `latency_ms=245ms` (Frame 1)
  - `server_pct = (200/245)*100 = 81.6%` ❌ **WRONG!**

**The percentage is meaningless** because:
- Numerator: Frame 2's server time
- Denominator: Frame 1's E2E time

**Correct calculation** should be:
```
Frame 2's server_pct = Frame 2's server_proc_ms / Frame 2's e2e_ms
```

But we don't have Frame 2's E2E until Frame 3 arrives!

---

### 🟡 Issue 3: keypoint_avg_conf Calculation in Detection Mode

**Problem**: In Object Detection mode, `keypoint_avg_conf` might be non-zero if dummy skeleton data contains keypoints.

#### Current Code:
```python
# infer_human.py:396-405
keypoint_avg_conf = 0.0
if skeleton and skeleton.persons:  # ❌ No mode check!
    all_keypoint_scores = []
    for person in skeleton.persons:
        if person is not None and person.keypoints:
            scores = [kp.score for kp in person.keypoints if kp.score > 0]
            all_keypoint_scores.extend(scores)
    if all_keypoint_scores:
        keypoint_avg_conf = sum(all_keypoint_scores) / len(all_keypoint_scores)
```

**Issue**:
- In `mode="detection"`, skeleton comes from dummy data (`run_skeleton_model()`)
- If dummy skeleton has keypoints with score > 0, this will calculate a non-zero `keypoint_avg_conf`
- Object Detection Excel rows should **always** have `keypoint_avg_conf=0.0`

**Expected Behavior**:
```python
keypoint_avg_conf = 0.0
if mode in ["pose", "both"] and skeleton and skeleton.persons:  # ✅ Add mode check
    # ... calculate keypoint confidence
```

**Verification Needed**: Check if dummy skeleton actually returns keypoints with scores.

---

### 🟢 Issue 4: detection_count Logic (Minor - Needs Clarification)

**Current Code**:
```python
# infer_human.py:407-416
detection_count = 0
if mode in ["pose", "both"]:
    # Count persons with valid keypoints
    if skeleton and skeleton.persons:
        detection_count = sum(1 for p in skeleton.persons if p is not None)
else:  # mode == "detection"
    # Count all detections
    if detections_result and detections_result.detections:
        detection_count = len(detections_result.detections)
```

**Analysis**:

#### Object Detection Mode:
- Source: `detections_result.detections`
- Content: Filtered to **persons only** (after bbox filtering in lines 128-168)
- Count: Number of person detections

#### Pose Estimation Mode:
- Source: `skeleton.persons`
- Content: Persons with keypoints (some may be None if pose failed)
- Count: Number of **non-None** persons

**Potential Inconsistency**:
- In `mode="both"`:
  - `detections_result` has N person bboxes
  - `skeleton.persons` might have M persons (where M ≤ N, because some pose estimations failed)
  - Excel logs `detection_count = M` (from skeleton)
  - But `avg_confidence` is calculated from `detections_result` (N items)

**Example**:
```
Frame with 3 person detections:
- detections_result.detections: [person1, person2, person3]
- skeleton.persons: [person1_pose, None, person3_pose]  # person2 pose failed

Excel logs:
- detection_count = 2 (non-None persons from skeleton)
- avg_confidence = (conf1 + conf2 + conf3) / 3 = average of 3 detections
```

**This is inconsistent** - counting 2 persons but averaging 3 confidences.

**Recommendation**: Clarify what `detection_count` should represent:
1. **Option A**: Number of person detections (YOLO output) → use `detections_result`
2. **Option B**: Number of persons with valid pose → use `skeleton.persons`

---

## Recommendations

### Fix 1: Align Frame Data (High Priority)

**Option A**: Log Frame N-1's complete data when processing Frame N
```python
log_async(
    scene=scene,
    frame_id=frame_id - 1,  # Log previous frame
    latency_ms=e2e_ms,
    server_proc_ms=... # Need to store previous frame's server time
    # ... all Frame N-1 data
)
```

**Problem**: Requires storing Frame N-1's detection results and server_proc_ms.

**Option B**: Change Unity to send current frame's metadata in response, log on next request
- Server returns `frame_id` in JSON response
- Unity stores Frame N's results
- Frame N+1 request includes Frame N's complete metrics + detection results
- Server logs complete Frame N data

**Option C**: Accept the misalignment and document it clearly
- Update documentation to explain data mixing
- Add warning that percentages are approximate
- Frame 1 will always have incomplete data

---

### Fix 2: Correct Percentage Calculation

If keeping current design, percentages should use **same frame** data:

```python
# Only calculate percentages for Frame N-1 (from headers)
if e2e_ms > 0:
    # These are all Frame N-1 values from headers
    upload_pct = (upload_ms / e2e_ms) * 100.0
    download_pct = (download_ms / e2e_ms) * 100.0
    parse_pct = (parse_ms / e2e_ms) * 100.0
    # Calculate server_pct by subtraction (Frame N-1's server time)
    server_ms_n1 = e2e_ms - upload_ms - download_ms - parse_ms
    server_pct = (server_ms_n1 / e2e_ms) * 100.0
else:
    # First frame - no percentage data
    server_pct = 0.0
    upload_pct = 0.0
    download_pct = 0.0

# Log Frame N-1's server_proc_ms separately
# (requires storing previous frame's processing_time_ms)
```

**Problem**: Requires calculating Frame N-1's server time by subtraction, or storing previous frame's `processing_time_ms`.

---

### Fix 3: Add Mode Check for keypoint_avg_conf

```python
keypoint_avg_conf = 0.0
if mode in ["pose", "both"]:  # ✅ Only calculate in pose modes
    if skeleton and skeleton.persons:
        all_keypoint_scores = []
        for person in skeleton.persons:
            if person is not None and person.keypoints:
                scores = [kp.score for kp in person.keypoints if kp.score > 0]
                all_keypoint_scores.extend(scores)
        if all_keypoint_scores:
            keypoint_avg_conf = sum(all_keypoint_scores) / len(all_keypoint_scores)
```

---

### Fix 4: Clarify detection_count Meaning

**Recommended**: Use consistent source for both modes

```python
detection_count = 0
if mode in ["pose", "both"]:
    # Use detections as source of truth (YOLO output)
    if detections_result and detections_result.detections:
        detection_count = len(detections_result.detections)
else:  # mode == "detection"
    if detections_result and detections_result.detections:
        detection_count = len(detections_result.detections)
```

OR add separate column for pose count:

```python
detection_count = len(detections_result.detections) if detections_result else 0
pose_count = sum(1 for p in skeleton.persons if p is not None) if skeleton else 0

# Add 'pose_count' to Excel columns
```

---

## Current Behavior Summary

### Object Detection Mode (mode="detection")

| Metric | Calculation | Frame | Correct? |
|--------|-------------|-------|----------|
| frame_id | From header | N | ✅ |
| latency_ms | X-E2E-Ms header | N-1 | ⚠️ Misaligned |
| server_proc_ms | Current processing | N | ⚠️ Misaligned with latency |
| upload/download/parse_ms | From headers | N-1 | ⚠️ Misaligned |
| server_pct | `processing_time_ms / e2e_ms` | N / N-1 | ❌ Wrong frames |
| detection_count | `len(detections)` | N | ✅ |
| avg_confidence | Average detection conf | N | ✅ |
| keypoint_avg_conf | From skeleton | N | ⚠️ Should always be 0.0 |

---

### Pose Estimation Mode (mode="both")

| Metric | Calculation | Frame | Correct? |
|--------|-------------|-------|----------|
| frame_id | From header | N | ✅ |
| latency_ms | X-E2E-Ms header | N-1 | ⚠️ Misaligned |
| server_proc_ms | Current processing | N | ⚠️ Misaligned with latency |
| upload/download/parse_ms | From headers | N-1 | ⚠️ Misaligned |
| server_pct | `processing_time_ms / e2e_ms` | N / N-1 | ❌ Wrong frames |
| detection_count | `sum(p is not None)` from skeleton | N | ⚠️ Inconsistent with avg_confidence |
| avg_confidence | Average detection conf | N | ✅ But source mismatch |
| keypoint_avg_conf | Average keypoint scores | N | ✅ |

---

## Testing Recommendations

### Test 1: Verify Frame Alignment

Create a test sequence:
```
Frame 1: 1 detection, server=150ms
Frame 2: 2 detections, server=200ms
Frame 3: 3 detections, server=180ms
```

Check Excel:
```
Row 1 (Frame 1): detection_count=1, server_proc_ms=150, latency_ms=0
Row 2 (Frame 2): detection_count=2, server_proc_ms=200, latency_ms=??? (should be Frame 1's E2E)
Row 3 (Frame 3): detection_count=3, server_proc_ms=180, latency_ms=??? (should be Frame 2's E2E)
```

### Test 2: Verify keypoint_avg_conf in Detection Mode

Run Object Detection mode, check Excel:
```
Expected: keypoint_avg_conf = 0.0 for ALL rows
Actual: ??? (check if dummy skeleton causes non-zero values)
```

### Test 3: Verify detection_count vs avg_confidence Consistency

Run Pose mode with 3 person detections where 1 pose fails:
```
Expected behavior (Option A):
- detection_count = 3 (from YOLO)
- avg_confidence = average of 3 detection confidences
- pose_count = 2 (if we add this column)

Current behavior:
- detection_count = 2 (from skeleton.persons)
- avg_confidence = average of 3 detection confidences
❌ MISMATCH
```

---

## Conclusion

The Excel logging has **significant frame alignment issues** that make the data difficult to interpret:

1. ❌ **Critical**: Frame misalignment - timing data is from Frame N-1, detection data from Frame N
2. ❌ **Critical**: Percentage calculations use data from different frames
3. ⚠️ **Moderate**: keypoint_avg_conf might be non-zero in detection mode
4. ⚠️ **Minor**: detection_count and avg_confidence use different sources in pose mode

**Recommendation**: Implement **Fix 1 Option B** to properly align all metrics for each frame.

---

**Created**: 2026-04-06
**Author**: Claude (Anthropic AI)
