# Segmentation Mode - Excel Logging Implementation Complete

## Summary

Successfully implemented complete Excel latency logging for Segmentation mode, matching the same comprehensive metrics tracking as Object Detection and Pose Estimation modes.

## Implementation Details

### Server-Side Changes

**File**: `C:\Repo\Github\vision_server\app\routes\segmentation.py`

1. **Added Excel Logger Import** (lines 17-19):
   ```python
   import sys
   sys.path.insert(0, os.path.join(os.path.dirname(__file__), '../..'))
   from debug.inference_logger import log_async
   ```

2. **Added Request Parameter** (line 47):
   ```python
   async def segmentation_detection(request: Request, image: UploadFile = File(...)):
   ```

3. **Added Complete Excel Logging** (lines 213-296):
   - Extracts all 24 Excel column values from request headers
   - Calculates detection metrics (count, avg confidence)
   - Computes percentage breakdowns (server_pct, upload_pct, download_pct)
   - Calls `log_async()` with all parameters
   - Non-blocking async logging (doesn't slow down response)

### Unity-Side Verification

**File**: `Assets/PassthroughCameraApiSamples/Segmentation/SegmentationInference/Scripts/SegmentationInferenceRunManager.cs`

**Already Implemented** ✅:
- Lines 543-556: Sends all required HTTP headers
  - `X-Scene-Name`: "Segmentation"
  - `X-Frame-Id`: Frame counter
  - `X-E2E-Ms`, `X-Upload-Ms`, `X-Download-Ms`, `X-Parse-Ms`: Timing data (Frame N-1)
  - `X-Upload-Bytes`, `X-Download-Bytes`, `X-Download-Bytes-Compressed`: Data sizes
  - `X-Target-FPS`: From config (e.g., 10.0)
  - `X-Dropped-Frames`: FPS throttling counter
  - `X-Freeze-Frames`: Inference overlap counter
  - `X-Freeze-Ratio`: freeze_frames / total_frames

**Frame Tracking** ✅:
- Line 224: `m_droppedFrames++` when frame arrives too soon
- Line 236: `m_frozenFrames++` when previous inference still running
- Line 333: `m_totalFrames++` when inference completes
- Line 552: `freezeRatio = m_frozenFrames / m_totalFrames`

### Documentation

**File**: `Documentation/EXCEL_LOGGING.md`

Created comprehensive documentation covering:
- All 24 Excel columns with descriptions
- Three inference modes: Object Detection, Pose Estimation, **Segmentation**
- Frame tracking formulas (dropped_frames, freeze_frames, freeze_ratio)
- Mode comparison table
- Sample Excel row example
- Known issues (frame misalignment)
- Troubleshooting guide

## Excel Columns Logged

### Segmentation Mode Specifics

| Column | Value for Segmentation | Notes |
|--------|------------------------|-------|
| **scene** | "Segmentation" | From Unity scene |
| **model_used** | "yolo11n-seg" | YOLO11n segmentation model |
| **detection_count** | # of persons with masks | From YOLO-seg bboxes |
| **avg_confidence** | Average person confidence | From detection results |
| **keypoint_avg_conf** | 0.0 | N/A for segmentation (no keypoints) |
| **server_proc_ms** | 60-120ms typical | YOLO-seg + mask extraction time |
| **download_bytes** | 50-100KB typical | Bboxes + base64 PNG masks |

### All 24 Columns

1. timestamp
2. scene
3. frame_id
4. latency_ms
5. server_proc_ms
6. upload_ms
7. download_ms
8. parse_ms
9. upload_bytes
10. download_bytes
11. download_bytes_compressed
12. server_pct
13. upload_pct
14. download_pct
15. detection_count
16. avg_confidence
17. keypoint_avg_conf
18. image_width
19. image_height
20. model_used
21. target_fps
22. dropped_frames
23. freeze_frames
24. freeze_ratio

## Frame Tracking Formulas (Verified)

### Dropped Frames
**Formula**: Increment when `timeSinceLastInference < (1.0 / targetFPS)`

**Example**:
- Target FPS: 10 → Interval: 100ms
- Frame at 50ms → **DROPPED**
- Frame at 110ms → Processed

**Code**: `SegmentationInferenceRunManager.cs:221-224`

### Freeze Frames
**Formula**: Increment when `m_inferenceInProgress == true`

**Meaning**: Previous server inference hasn't completed yet

**Code**: `SegmentationInferenceRunManager.cs:233-236`

### Freeze Ratio
**Formula**: `freeze_frames / total_frames`

**Note**: `total_frames` only counts successful completions (not dropped frames)

**Code**: `SegmentationInferenceRunManager.cs:552`

## Logging Behavior

### When Logs Are Created

**Rule**: Only frames with `detection_count > 0` are logged to Excel

**Location**: `debug/logs/inference_log_YYYY-MM-DD.xlsx`

**Example**:
```
2026-04-11:
- Frame 1: 0 persons → NOT logged
- Frame 2: 2 persons → LOGGED ✅
- Frame 3: 1 person → LOGGED ✅
- Frame 4: 0 persons → NOT logged
```

### Log File Rotation

Daily Excel files:
```
vision_server/debug/logs/
├── inference_log_2026-04-10.xlsx
├── inference_log_2026-04-11.xlsx  ← Today
└── inference_log_2026-04-12.xlsx
```

## Testing

### Verified

1. ✅ Python imports successfully (`segmentation.py` loads without errors)
2. ✅ Server reloaded without errors
3. ✅ All headers already sent by Unity client
4. ✅ Frame tracking formulas match documentation
5. ✅ Same implementation pattern as `infer_human.py`

### To Test on Quest 3

1. Run Segmentation scene
2. Detect some persons
3. Check Excel log:
   ```
   C:\Repo\Github\vision_server\debug\logs\inference_log_2026-04-11.xlsx
   ```

4. Verify columns:
   - `scene` = "Segmentation"
   - `model_used` = "yolo11n-seg"
   - `detection_count` > 0
   - `keypoint_avg_conf` = 0.0
   - All timing columns populated

## Known Issues (Documented)

### Frame Misalignment
**Problem**: Timing data (latency_ms, upload_ms, etc.) are from Frame N-1, but server_proc_ms and detection_count are from Frame N.

**Why**: Unity sends previous frame's timing as headers to avoid blocking.

**Impact**: Cannot directly correlate Frame N's detections with Frame N's E2E latency.

### server_pct Calculation
**Problem**: Uses Frame N's `server_proc_ms` divided by Frame N-1's `latency_ms`.

**Formula**: `(Frame N server time) / (Frame N-1 E2E time) × 100`

**Impact**: Percentage value is not meaningful (mixing different frames).

---

## Files Modified

### Server (Python)
- ✅ `C:\Repo\Github\vision_server\app\routes\segmentation.py` (+95 lines)

### Unity (C#)
- ✅ (No changes needed - already implemented)

### Documentation
- ✅ `Documentation/EXCEL_LOGGING.md` (new file)
- ✅ `SEGMENTATION_EXCEL_LOGGING_COMPLETE.md` (this file)

---

**Date**: 2026-04-11
**Status**: ✅ Implementation Complete
**Ready for Testing**: Yes
**Next Steps**: Test on Quest 3 and verify Excel logs are generated correctly
