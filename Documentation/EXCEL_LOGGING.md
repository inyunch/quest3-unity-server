# Excel Metrics Logging - Complete Guide

## Overview

The vision server automatically logs inference metrics to Excel files for performance analysis and latency optimization. This document covers all three inference modes: **Object Detection**, **Pose Estimation**, and **Segmentation**.

**Excel Columns**: 24 total
**Code Locations**:
- Server Logger: `vision_server/debug/inference_logger.py`
- Object Detection: `vision_server/app/routes/infer_human.py`
- Segmentation: `vision_server/app/routes/segmentation.py`

**Log Location**: `vision_server/debug/logs/inference_log_YYYY-MM-DD.xlsx`

---

## Supported Modes

| Mode | Unity Scene | Server Endpoint | Model Used | Logged? |
|------|-------------|-----------------|------------|---------|
| **Object Detection** | MultiObjectDetection | `/infer_human?mode=detection` | YOLOv8n | ✅ Yes |
| **Pose Estimation** | PoseEstimation | `/infer_human?mode=both` | YOLOv8n + RTMPose | ✅ Yes |
| **Segmentation** | Segmentation | `/segmentation` | YOLO11n-seg | ✅ Yes |

---

## Excel Column Definitions

### Timing Columns

| Column | Type | Description | Source | Unit |
|--------|------|-------------|--------|------|
| **timestamp** | string | Server timestamp when frame was processed | Server | `YYYY-MM-DD HH:MM:SS.mmm` |
| **latency_ms** | float | End-to-end latency (Unity → Server → Unity) | Unity (Frame N-1) | milliseconds |
| **server_proc_ms** | float | Server-side inference + processing time | Server (Frame N) | milliseconds |
| **upload_ms** | float | Image upload time (client-side) | Unity (Frame N-1) | milliseconds |
| **download_ms** | float | Response download time (client-side) | Unity (Frame N-1) | milliseconds |
| **parse_ms** | float | JSON parsing time (client-side) | Unity (Frame N-1) | milliseconds |

### Data Size Columns

| Column | Type | Description | Unit |
|--------|------|-------------|------|
| **upload_bytes** | int | JPEG image upload size | bytes |
| **download_bytes** | int | JSON response size (uncompressed) | bytes |
| **download_bytes_compressed** | int | Network transfer size (compressed) | bytes |

### Percentage Breakdowns

| Column | Type | Formula | Note |
|--------|------|---------|------|
| **server_pct** | float | `(server_proc_ms / latency_ms) × 100` | ⚠️ Uses mixed frames (N / N-1) |
| **upload_pct** | float | `(upload_ms / latency_ms) × 100` | ✅ Consistent (N-1 / N-1) |
| **download_pct** | float | `(download_ms / latency_ms) × 100` | ✅ Consistent (N-1 / N-1) |

### Detection Metrics

| Column | Type | Description | Mode Differences |
|--------|------|-------------|------------------|
| **detection_count** | int | Number of persons detected | Object Detection: bbox count<br>Pose: persons with valid keypoints<br>Segmentation: bbox count |
| **avg_confidence** | float | Average detection confidence [0-1] | Same for all modes |
| **keypoint_avg_conf** | float | Average keypoint confidence [0-1] | Object Detection: 0.0<br>Pose: calculated<br>Segmentation: 0.0 |

### Metadata

| Column | Type | Description | Example Values |
|--------|------|-------------|----------------|
| **scene** | string | Unity scene name | `"MultiObjectDetection"`, `"PoseEstimation"`, `"Segmentation"` |
| **frame_id** | int | Frame sequence number from Unity | 1, 2, 3, ... |
| **image_width** | int | Input image width (pixels) | 1280 |
| **image_height** | int | Input image height (pixels) | 960 |
| **model_used** | string | Model identifier | `"yolo+pose_detection"`, `"yolo+pose_both"`, `"yolo11n-seg"` |

### Performance Metrics

| Column | Type | Formula | Description |
|--------|------|---------|-------------|
| **target_fps** | float | From Unity config | Target inference rate (e.g., 5.0, 10.0) |
| **dropped_frames** | int | Cumulative count | Frames skipped due to FPS throttling |
| **freeze_frames** | int | Cumulative count | Frames where previous inference still running |
| **freeze_ratio** | float | `freeze_frames / total_frames` | Percentage of frames that froze [0-1] |

---

## Frame Tracking Formulas

### Dropped Frames

**Definition**: Frames that were intentionally skipped because they arrived too soon after the last inference (FPS throttling).

**Calculation** (Unity):
```csharp
float timeSinceLastInference = Time.realtimeSinceStartup - m_lastInferenceTime;
float targetInterval = 1.0f / m_inferenceConfig.targetFPS;

if (timeSinceLastInference < targetInterval)
{
    m_droppedFrames++;  // Increment counter
}
```

**Example**:
- Target FPS: 10 → Target interval: 100ms
- Frame arrives 50ms after last inference → **DROP** (too soon)
- Frame arrives 110ms after last inference → **PROCESS**

**Code Location**:
- `SentisInferenceRunManager.cs:180`
- `SegmentationInferenceRunManager.cs:224`

---

### Freeze Frames

**Definition**: Frames where a new inference could not start because the previous inference was still in progress (server overload).

**Calculation** (Unity):
```csharp
if (m_inferenceInProgress)
{
    m_frozenFrames++;  // Increment counter
    // Keep showing old visualization
}
else
{
    // Start new inference
    m_inferenceInProgress = true;
}
```

**Example**:
- Frame 1: Start inference, set `m_inferenceInProgress = true`
- Frame 2: Arrives before Frame 1 completes → **FREEZE** (increment counter, skip)
- Frame 1: Completes, set `m_inferenceInProgress = false`
- Frame 3: Can now process normally

**Code Location**:
- `SentisInferenceRunManager.cs:192`
- `SegmentationInferenceRunManager.cs:236`

---

### Freeze Ratio

**Definition**: Percentage of total successful frames that were frozen (0.0 = no freezes, 1.0 = all frames frozen).

**Calculation** (Unity):
```csharp
// Increment total frames only when inference completes
if (m_useServerInference)
{
    m_inferenceInProgress = false;
    m_totalFrames++;  // Count successful completions
}

// Calculate ratio
float freezeRatio = m_totalFrames > 0 ? (float)m_frozenFrames / m_totalFrames : 0f;
```

**Example**:
- 100 total frames processed
- 15 freeze frames occurred
- `freeze_ratio = 15 / 100 = 0.15` (15% of frames froze)

**Code Location**:
- `SentisInferenceRunManager.cs:287, 502`
- `SegmentationInferenceRunManager.cs:333, 552`

**Note**: `m_totalFrames` only counts **successful completions**, not all attempted frames. Dropped frames are not included in the total.

---

## Mode Comparison

### Object Detection Mode

```python
# Server endpoint: /infer_human?mode=detection
# Unity: MultiObjectDetection scene
# Model: YOLOv8n only

scene = "MultiObjectDetection"
model_used = "yolo+pose_detection"
detection_count = len(detections)  # YOLO bounding boxes
avg_confidence = mean([d.confidence for d in detections])
keypoint_avg_conf = 0.0  # No keypoints in this mode
```

**Typical Performance**:
- `server_proc_ms`: 50-150ms
- `download_bytes`: ~50KB (bounding boxes only)

---

### Pose Estimation Mode

```python
# Server endpoint: /infer_human?mode=both
# Unity: PoseEstimation scene
# Model: YOLOv8n + RTMPose

scene = "PoseEstimation"
model_used = "yolo+pose_both"
detection_count = sum(1 for p in skeleton.persons if p is not None)
avg_confidence = mean([d.confidence for d in detections])
keypoint_avg_conf = mean([kp.score for p in persons for kp in p.keypoints])
```

**Typical Performance**:
- `server_proc_ms`: 150-300ms (includes pose estimation)
- `download_bytes`: ~435KB (detections + keypoints + depth)
- `parse_ms`: 15-50ms (complex JSON parsing)

---

### Segmentation Mode

```python
# Server endpoint: /segmentation
# Unity: Segmentation scene
# Model: YOLO11n-seg (segmentation model)

scene = "Segmentation"
model_used = "yolo11n-seg"
detection_count = len(detections)  # Person bounding boxes with masks
avg_confidence = mean([d.confidence for d in detections])
keypoint_avg_conf = 0.0  # No keypoints in segmentation mode
```

**Typical Performance**:
- `server_proc_ms`: 60-120ms (YOLO-seg inference + mask extraction)
- `download_bytes`: ~50-100KB (bounding boxes + base64 PNG masks)
- **Mask Data**: Each detection includes:
  - `mask_png_base64`: Base64-encoded RGBA PNG
  - `mask_width`: Cropped mask width
  - `mask_height`: Cropped mask height

**Key Differences from Object Detection**:
- Uses YOLO11n-seg model (not YOLOv8n)
- Returns pixel-level segmentation masks (not just bboxes)
- Masks are cropped to bounding box regions
- Masks encoded as RGBA PNG with alpha channel

---

## Known Issues

### ⚠️ Issue 1: Frame Misalignment

**Problem**: Timing data (columns 4-8) and percentages (11-13) are from **Frame N-1**, but detection data (14-16) is from **Frame N**.

**Impact**:
```
Excel Row for frame_id=42:
├─ latency_ms (Frame 41)
├─ server_proc_ms (Frame 42)  ❌ MISMATCH
├─ upload_ms (Frame 41)
├─ download_ms (Frame 41)
├─ detection_count (Frame 42)  ❌ MISMATCH
```

**Why**: Unity sends timing data from the previous frame as HTTP headers in the current request (to avoid blocking).

---

### ⚠️ Issue 2: server_pct Uses Mixed Frames

**Problem**:
```python
server_pct = (processing_time_ms / e2e_ms) * 100.0
            ↑ Frame N          ↑ Frame N-1
```

**Impact**: The percentage is meaningless because numerator and denominator are from different frames.

---

## Logging Behavior

### When Logs Are Created

**Rule**: Only frames with `detection_count > 0` are logged.

**Code** (`inference_logger.py:106-108`):
```python
if detection_count == 0:
    return  # Skip logging
```

**Impact**:
- Empty frames (no persons) → **Not logged**
- Frames with 1+ persons → **Logged to Excel**

### Log File Rotation

**Daily Files**: A new Excel file is created each day.

**Format**: `inference_log_YYYY-MM-DD.xlsx`

**Example**:
```
debug/logs/
├── inference_log_2026-04-10.xlsx  (yesterday's logs)
└── inference_log_2026-04-11.xlsx  (today's logs)
```

---

## Usage Example

### Running Segmentation Mode

1. **Unity**: Configure `SegmentationInferenceRunManager`
   ```csharp
   m_inferenceConfig.targetFPS = 10f;
   m_inferenceConfig.jpegQuality = 80;
   ```

2. **Server**: Start vision server
   ```bash
   cd C:\Repo\Github\vision_server
   python -m uvicorn app.main:app --host 0.0.0.0 --port 8001
   ```

3. **Run**: Play Segmentation scene on Quest 3

4. **Check Logs**:
   ```
   C:\Repo\Github\vision_server\debug\logs\inference_log_2026-04-11.xlsx
   ```

### Sample Excel Row (Segmentation)

| Column | Value | Notes |
|--------|-------|-------|
| timestamp | 2026-04-11 14:23:45.123 | Server timestamp |
| scene | Segmentation | From Unity scene |
| frame_id | 42 | Frame number |
| latency_ms | 187.3 | E2E latency (Frame 41) |
| server_proc_ms | 85.2 | Server time (Frame 42) |
| upload_ms | 35.0 | Upload time (Frame 41) |
| download_ms | 50.2 | Download time (Frame 41) |
| parse_ms | 7.0 | Parse time (Frame 41) |
| upload_bytes | 125000 | ~125KB JPEG |
| download_bytes | 68500 | ~68KB JSON |
| server_pct | 45.5 | ⚠️ Mixed frames |
| upload_pct | 18.7 | ✅ Consistent |
| download_pct | 26.8 | ✅ Consistent |
| detection_count | 2 | 2 persons detected |
| avg_confidence | 0.78 | High confidence |
| keypoint_avg_conf | 0.0 | N/A for segmentation |
| image_width | 1280 | Quest camera |
| image_height | 960 | Quest camera |
| model_used | yolo11n-seg | Segmentation model |
| target_fps | 10.0 | From config |
| dropped_frames | 5 | Cumulative |
| freeze_frames | 2 | Cumulative |
| freeze_ratio | 0.0476 | 4.76% |

---

## Troubleshooting

### No logs being created?

**Check**:
1. Are persons being detected? (`detection_count > 0`)
2. Is the server running? (check console for "[LOGGER] Logged frame...")
3. File permissions on `debug/logs/` folder

### Unexpected freeze_ratio values?

**Check**:
1. Server response time (should be < 1/targetFPS)
2. Network latency between Quest and server
3. Server CPU usage (may need GPU acceleration)

**Example**:
- Target FPS: 10 (100ms interval)
- Server takes 150ms → Every other frame will freeze
- Expected `freeze_ratio`: ~0.5 (50%)

---

**Created**: 2026-04-11
**Author**: Claude (Anthropic AI)
**Updated**: Added Segmentation mode support
