# Optimization Changes Summary

## Overview

All optimization changes have been successfully implemented to reduce `upload_ms` and `download_ms` in Excel metrics.

**Date**: 2026-04-06
**Goal**: Reduce latency by removing unused response data and optimizing upload size

---

## Changes Made

### ✅ Server-Side Changes

#### 1. Modified `vision_server/app/routes/infer_human.py`

**Added Query Parameters** (Lines 64-71):
```python
include_mask: bool = Query(
    default=False,
    description="Include segmentation mask in response (adds ~75KB)"
),
include_depth: bool = Query(
    default=False,
    description="Include depth map in response (adds ~300KB)"
)
```

**Modified Response Logic** (Lines 270-289):
```python
# Only add segmentation if requested (optimization: saves ~75KB)
if include_mask and "segmentation" not in results:
    from app.inference import run_segmentation_model
    mask, mask_downsample = run_segmentation_model(pil_image)
    results["segmentation"] = {"mask": mask, "downsample_factor": mask_downsample}
elif not include_mask:
    results["segmentation"] = None

# Only add depth if requested (optimization: saves ~300KB)
if include_depth and "depth" not in results:
    from app.inference import run_depth_model
    depth, depth_downsample = run_depth_model(pil_image)
    results["depth"] = {"depth_map": depth, "downsample_factor": depth_downsample}
elif not include_depth:
    results["depth"] = None
```

**Modified Response Building** (Lines 317-368):
```python
# Segmentation (optional - only if requested)
segmentation = None
if results.get("segmentation") is not None:
    # ... build segmentation ...

# Depth (optional - only if requested)
depth = None
if results.get("depth") is not None:
    # ... build depth ...
```

---

#### 2. Modified `vision_server/app/models.py`

**Made Fields Optional** (Lines 91-99):
```python
segmentation: Optional[SegmentationResult] = Field(
    None,
    description="Human segmentation mask (optional, controlled by include_mask parameter)"
)
skeleton: SkeletonResult
depth: Optional[DepthResult] = Field(
    None,
    description="Monocular depth estimation (optional, controlled by include_depth parameter)"
)
```

---

### ✅ Unity Client-Side Changes

#### 3. Modified Object Detection Script

**File**: `Assets/PassthroughCameraApiSamples/MultiObjectDetection/SentisInference/Scripts/SentisInferenceRunManager.cs`

**Updated URL** (Line 35):
```csharp
[SerializeField] private string m_serverUrl = "http://192.168.0.135:8001/infer_human?mode=detection&include_mask=false&include_depth=false";
```

**Added JPEG Quality Control** (Lines 37-38):
```csharp
[Header("Upload Optimization")]
[SerializeField, Range(60, 100)] private int m_jpegQuality = 80;
```

**Modified JPEG Encoding** (Line 384):
```csharp
byte[] jpegBytes = tex2D.EncodeToJPG(m_jpegQuality);
```

---

#### 4. Modified Pose Estimation Script

**File**: `Assets/PassthroughCameraApiSamples/PoseEstimation/Scripts/PoseInferenceRunManager.cs`

**Updated URL** (Line 26):
```csharp
[SerializeField] private string m_serverUrl = "http://192.168.0.135:8001/infer_human?mode=both&include_mask=false&include_depth=false";
```

**Added JPEG Quality Control** (Lines 29-30):
```csharp
[Header("Upload Optimization")]
[SerializeField, Range(60, 100)] private int m_jpegQuality = 80;
```

**Modified JPEG Encoding** (Line 213):
```csharp
byte[] jpegBytes = tex2D.EncodeToJPG(m_jpegQuality);
```

---

## Expected Impact

### Before Optimization

| Metric | Object Detection | Pose Estimation |
|--------|------------------|-----------------|
| upload_bytes | ~125KB | ~125KB |
| download_bytes | **~425KB** | **~435KB** |
| upload_ms | 40ms | 40ms |
| download_ms | **100ms** | **100ms** |
| E2E latency | 300ms | 450ms |

### After Optimization

| Metric | Object Detection | Pose Estimation | Improvement |
|--------|------------------|-----------------|-------------|
| upload_bytes | ~85KB | ~85KB | **-32%** (quality 80) |
| download_bytes | **~20KB** | **~20KB** | **-95%** |
| upload_ms | 30ms | 30ms | **-25%** |
| download_ms | **25ms** | **25ms** | **-75%** |
| E2E latency | 220ms | 290ms | **-27% to -36%** |

---

## Testing Instructions

### Step 1: Restart Server

```bash
cd C:\Repo\Github\vision_server
python -m app.main
```

Verify startup logs show:
```
INFO:     Application startup complete.
```

### Step 2: Build and Deploy Unity

1. Open Unity project
2. Verify Inspector settings:
   - Object Detection: `m_serverUrl` includes `&include_mask=false&include_depth=false`
   - Object Detection: `m_jpegQuality` = 80
   - Pose Estimation: `m_serverUrl` includes `&include_mask=false&include_depth=false`
   - Pose Estimation: `m_jpegQuality` = 80
3. Build and deploy to Quest

### Step 3: Run Inference and Monitor Logs

#### Unity Logs (on Quest):
```bash
adb logcat -s Unity | findstr /C:"[BYTES]"
```

**Expected Output**:
```
[BYTES] Upload=85000 Download=20000  (previously: Upload=125000 Download=425000)
```

#### Server Logs:
```bash
cd C:\Repo\Github\vision_server
# Monitor console output
```

**Look for**:
```
[API] Received image: 1280x960, size=85000 bytes  (previously: 125000)
[LOGGER] Logged frame X scene=MultiObjectDetection detections=3
```

### Step 4: Check Excel Metrics

Open: `vision_server/debug/logs/inference_log_2026-04-06.xlsx`

**Compare Before/After**:

| Frame | upload_bytes | download_bytes | upload_ms | download_ms | latency_ms |
|-------|--------------|----------------|-----------|-------------|------------|
| Before | 125000 | 425000 | 40 | 100 | 300 |
| After  | **85000** | **20000** | **30** | **25** | **220** |

### Step 5: Verify Detection Accuracy

**Important**: Check that detection quality is still acceptable with JPEG quality 80.

**Visual Checks**:
- Bounding boxes still accurate?
- Keypoints still visible and correct?
- Confidence scores similar to before?

**If quality is poor**:
- Increase `m_jpegQuality` to 85 or 90 in Unity Inspector
- Rebuild and test again

---

## Reverting Changes (If Needed)

### To Re-enable Mask and Depth:

**Unity**:
```csharp
// Change URL to:
m_serverUrl = "http://192.168.0.135:8001/infer_human?mode=detection&include_mask=true&include_depth=true";
```

### To Reset JPEG Quality:

**Unity**:
```csharp
m_jpegQuality = 90;  // Back to original
```

---

## Troubleshooting

### Issue 1: Server Returns Error 422

**Symptom**: Unity logs show "Response code: 422"

**Cause**: Server doesn't recognize new query parameters (old server version running)

**Fix**: Restart server to load updated code

---

### Issue 2: download_bytes Still Large

**Symptom**: Excel shows download_bytes = 425000 (unchanged)

**Possible Causes**:
1. Unity URL doesn't include `&include_mask=false&include_depth=false`
2. Server cached old response
3. Old Unity build still deployed

**Fix**:
1. Check Unity Inspector → verify URL
2. Restart server
3. Rebuild and redeploy Unity app

---

### Issue 3: Detection Accuracy Degraded

**Symptom**: Bounding boxes less accurate, lower confidence scores

**Cause**: JPEG quality 80 may be too low for your use case

**Fix**: Increase `m_jpegQuality` to 85-90

---

### Issue 4: Response is Null for Segmentation/Depth

**Symptom**: Unity logs show warnings about null segmentation/depth

**Cause**: This is expected! Unity doesn't use these fields.

**Fix**: None needed - this is the optimization working correctly.

---

## Performance Monitoring

### Key Metrics to Watch in Excel

| Metric | Target After Optimization | Red Flag If |
|--------|---------------------------|-------------|
| download_bytes | ~20,000 | > 50,000 |
| download_ms | 20-30ms | > 50ms |
| upload_bytes | ~85,000 | > 130,000 |
| upload_ms | 25-35ms | > 50ms |
| latency_ms | 200-250ms (Object Detection) | > 350ms |
| latency_ms | 280-350ms (Pose Estimation) | > 500ms |

### Calculate Actual Savings

After collecting 50+ frames in Excel:

```python
import pandas as pd

df = pd.read_excel("inference_log_2026-04-06.xlsx")

# Calculate averages
avg_download_bytes = df['download_bytes'].mean()
avg_download_ms = df['download_ms'].mean()
avg_upload_bytes = df['upload_bytes'].mean()
avg_upload_ms = df['upload_ms'].mean()
avg_latency = df['latency_ms'].mean()

print(f"Average download_bytes: {avg_download_bytes:.0f} bytes (~{avg_download_bytes/1024:.1f}KB)")
print(f"Average download_ms: {avg_download_ms:.1f}ms")
print(f"Average upload_bytes: {avg_upload_bytes:.0f} bytes (~{avg_upload_bytes/1024:.1f}KB)")
print(f"Average upload_ms: {avg_upload_ms:.1f}ms")
print(f"Average latency: {avg_latency:.1f}ms")
```

**Expected Output**:
```
Average download_bytes: 20500 bytes (~20.0KB)  ← Was 425KB
Average download_ms: 25.3ms                    ← Was 100ms
Average upload_bytes: 86200 bytes (~84.2KB)    ← Was 125KB
Average upload_ms: 30.1ms                      ← Was 40ms
Average latency: 223.8ms                       ← Was 300ms
```

---

## Additional Optimizations (Optional)

### 1. Use 5GHz WiFi

**Impact**: Additional 30-50% reduction in upload_ms and download_ms

**Steps**:
1. Connect Quest to 5GHz WiFi network
2. Ensure server PC also on same 5GHz network
3. Test again

**Expected**:
- upload_ms: 30ms → **20ms**
- download_ms: 25ms → **18ms**

### 2. Further Lower JPEG Quality (Risky)

**Only if detection accuracy is still good at quality 80**:

**Unity**:
```csharp
m_jpegQuality = 75;  // Further reduction
```

**Expected**:
- upload_bytes: 85KB → **70KB**
- upload_ms: 30ms → **25ms**

**⚠️ Warning**: Test thoroughly! May affect detection/pose accuracy.

---

## Success Criteria

✅ Optimization is successful if:

1. **download_bytes** reduced from ~425KB to **~20KB** (95% reduction)
2. **download_ms** reduced from ~100ms to **~25ms** (75% reduction)
3. **upload_bytes** reduced from ~125KB to **~85KB** (32% reduction)
4. **upload_ms** reduced from ~40ms to **~30ms** (25% reduction)
5. **Detection accuracy** remains acceptable (visual inspection)
6. **No errors** in Unity or server logs

---

## Files Modified

### Server Files
- `vision_server/app/routes/infer_human.py` (Lines 64-71, 270-289, 317-368)
- `vision_server/app/models.py` (Lines 91-99)

### Unity Files
- `Assets/PassthroughCameraApiSamples/MultiObjectDetection/SentisInference/Scripts/SentisInferenceRunManager.cs` (Lines 35, 37-38, 384)
- `Assets/PassthroughCameraApiSamples/PoseEstimation/Scripts/PoseInferenceRunManager.cs` (Lines 26, 29-30, 213)

---

## Related Documents

- [Latency Optimization Guide](LATENCY_OPTIMIZATION_GUIDE.md) - Detailed optimization strategies
- [Excel Formulas by Mode](EXCEL_FORMULAS_BY_MODE.md) - Field definitions and calculations
- [Metrics Latency Comparison](METRICS_LATENCY_COMPARISON.md) - Performance analysis

---

**Implementation Date**: 2026-04-06
**Status**: ✅ Complete - Ready for Testing
**Next Steps**: Deploy and monitor Excel metrics
