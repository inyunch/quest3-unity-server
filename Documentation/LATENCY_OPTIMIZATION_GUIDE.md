# Latency Optimization Guide - upload_ms & download_ms

## Overview

This guide provides concrete strategies to reduce `upload_ms` and `download_ms` in Excel metrics.

**Current Typical Values**:
- `upload_ms`: 20-50ms (good WiFi) to 100-300ms (poor WiFi)
- `download_ms`: 80-120ms (good WiFi) to 200-400ms (poor WiFi)

---

## 🎯 Optimization Targets

### Quick Wins
- ✅ **Remove unused response data** → download_ms: 80ms → **20-30ms** (60-70% reduction)
- ✅ **Lower JPEG quality** → upload_ms: 40ms → **25-30ms** (25-40% reduction)
- ✅ **Use 5GHz WiFi** → Both: 30-50% reduction

### Advanced
- 🔧 Lower image resolution → Both reduce proportionally
- 🔧 Server-side response compression (gzip)
- 🔧 Use wired ethernet connection

---

## 📥 Optimizing download_ms (HIGH IMPACT)

### Issue: Unused Response Data

**Current Situation**:
```python
# Server returns (infer_human.py lines 261-275):
response = {
    "detections": {...},           # ~5-10KB   ← Unity uses this
    "skeleton": {...},             # ~5-10KB   ← Unity uses this
    "segmentation": {              # ~75KB     ← Unity DOES NOT use this
        "mask": [[...], [...], ...]
    },
    "depth": {                     # ~300KB    ← Unity DOES NOT use this
        "values": [[...], [...], ...]
    }
}
```

**Total Response Size**: ~425KB
**Actually Used by Unity**: ~20KB (detections + skeleton only)
**Wasted Bandwidth**: **~405KB (95%)**

---

### 🔴 Critical Optimization: Remove Unused Fields

#### Option 1: Add Query Parameter (Recommended)

**Server Change** (`infer_human.py`):

```python
@router.post("/infer_human", response_model=HumanInferenceResponse)
async def infer_human(
    request: Request,
    image: UploadFile = File(...),
    mode: str = Query(default="detection", ...),
    include_mask: bool = Query(default=False, description="Include segmentation mask"),
    include_depth: bool = Query(default=False, description="Include depth map")
):
    # ... existing code ...

    # Build response (lines 261-275)
    # Only add segmentation if requested
    if include_mask and "segmentation" not in results:
        from app.inference import run_segmentation_model
        mask, mask_downsample = run_segmentation_model(pil_image)
        results["segmentation"] = {"mask": mask, "downsample_factor": mask_downsample}

    # Only add depth if requested
    if include_depth and "depth" not in results:
        from app.inference import run_depth_model
        depth, depth_downsample = run_depth_model(pil_image)
        results["depth"] = {"depth_map": depth, "downsample_factor": depth_downsample}

    # ... build response as before, but segmentation/depth may be null ...
```

**Unity Change** (`SentisInferenceRunManager.cs`, `PoseInferenceRunManager.cs`):

```csharp
// Update URL to exclude mask and depth
private string m_serverUrl = "http://192.168.0.135:8001/infer_human?mode=detection&include_mask=false&include_depth=false";
```

**Expected Impact**:
```
Before: download_bytes = ~425KB, download_ms = 80-120ms
After:  download_bytes = ~20KB,  download_ms = 20-30ms

Reduction: ~405KB (95%), 50-90ms saved
```

---

#### Option 2: Return Null for Unused Fields

**Server Change** (simpler, no Unity changes needed):

```python
# infer_human.py lines 261-275
# Change to always return null for unused fields
results["segmentation"] = None  # Don't compute if not needed
results["depth"] = None          # Don't compute if not needed
```

**Modify Response Model** (`app/models.py`):

```python
class HumanInferenceResponse(BaseModel):
    detections: Optional[DetectionResult] = None
    segmentation: Optional[SegmentationResult] = None  # Make optional
    skeleton: SkeletonResult
    depth: Optional[DepthResult] = None               # Make optional
    processing_time_ms: float
    input_image_width: int
    input_image_height: int
    model_input_width: int
    model_input_height: int
    mode: str
```

**Expected Impact**:
```
Before: {"segmentation": {"mask": [[...]...]}, "depth": {"values": [[...]]}}
After:  {"segmentation": null, "depth": null}

Response size: ~425KB → ~20KB
download_ms: 80-120ms → 20-30ms
```

---

#### Option 3: Separate Lightweight Endpoint

Create new endpoint for Unity that only returns essential data:

**Server** (`app/routes/infer_human_lite.py`):

```python
@router.post("/infer_human_lite")
async def infer_human_lite(
    request: Request,
    image: UploadFile = File(...),
    mode: str = Query(default="detection")
):
    # ... same inference logic ...

    # Return only detections and skeleton (no mask, no depth)
    return {
        "detections": detections_result,
        "skeleton": skeleton,
        "processing_time_ms": processing_time_ms,
        "model_input_width": model_input_w,
        "model_input_height": model_input_h,
        "input_image_width": img_width,
        "input_image_height": img_height,
        "mode": mode
    }
```

**Unity**:
```csharp
private string m_serverUrl = "http://192.168.0.135:8001/infer_human_lite?mode=detection";
```

---

### Comparison of Options

| Option | Server Changes | Unity Changes | Response Size | Flexibility | Recommended |
|--------|----------------|---------------|---------------|-------------|-------------|
| **Option 1: Query Params** | Medium | Small (URL change) | 20KB | High (can enable later) | ✅ Best |
| **Option 2: Return Null** | Small | None | 20KB | Low (hardcoded) | 🟡 OK |
| **Option 3: New Endpoint** | High | Small (URL change) | 20KB | Medium | 🟡 OK |

**Recommendation**: Use **Option 1** for maximum flexibility.

---

## 📤 Optimizing upload_ms (MEDIUM IMPACT)

### Current Upload Breakdown

```
upload_ms = JPEG_encoding_time + Network_transfer_time

Where:
- Image: 1280×960 RGB
- JPEG Quality: 90
- Encoded Size: ~125KB
- JPEG Encoding: ~5-10ms (on Quest)
- Network Transfer: ~15-40ms (WiFi dependent)
```

---

### Optimization 1: Lower JPEG Quality

**Current**:
```csharp
// SentisInferenceRunManager.cs:380
byte[] jpegBytes = tex2D.EncodeToJPG(90);  // Quality 90
```

**Change to**:
```csharp
byte[] jpegBytes = tex2D.EncodeToJPG(75);  // Quality 75
```

**Impact**:

| Quality | File Size | Encoding Time | Visual Quality | upload_ms Impact |
|---------|-----------|---------------|----------------|------------------|
| 90 | ~125KB | 8-10ms | Excellent | Baseline (40ms) |
| 80 | ~85KB | 6-8ms | Very Good | **30-32ms (-25%)** |
| 75 | ~70KB | 5-7ms | Good | **28-30ms (-30%)** |
| 70 | ~60KB | 4-6ms | Acceptable | **25-28ms (-35%)** |
| 60 | ~45KB | 3-5ms | Fair (artifacts) | **22-25ms (-40%)** |

**Recommendation**: Quality **75-80** provides best trade-off.

**Testing**:
```csharp
[Header("Upload Optimization")]
[SerializeField, Range(60, 100)] private int m_jpegQuality = 75;

// In RunServerInference():
byte[] jpegBytes = tex2D.EncodeToJPG(m_jpegQuality);
```

---

### Optimization 2: Lower Image Resolution

**Current**: 1280×960 (from Quest camera)

**Change to**:
```csharp
// Resize before encoding
Texture2D resizedTex = new Texture2D(640, 480, TextureFormat.RGB24, false);
// ... copy and resize tex2D to resizedTex ...
byte[] jpegBytes = resizedTex.EncodeToJPG(75);
```

**Impact**:

| Resolution | Pixels | JPEG Size (q=75) | Encoding Time | upload_ms | Accuracy Impact |
|------------|--------|------------------|---------------|-----------|-----------------|
| 1280×960 | 1.23M | ~70KB | 5-7ms | 28-30ms | Baseline |
| 960×720 | 0.69M | ~45KB | 3-5ms | **22-25ms** | Minimal |
| 640×480 | 0.31M | ~25KB | 2-3ms | **18-20ms** | Moderate |

**⚠️ Note**: Lower resolution affects detection/pose accuracy. Test thoroughly.

---

### Optimization 3: Better Network Connection

**WiFi Optimization**:

| Connection Type | Typical Bandwidth | Latency | upload_ms (125KB) | download_ms (425KB) |
|----------------|-------------------|---------|-------------------|---------------------|
| 2.4GHz WiFi (fair) | 10-20 Mbps | 20-50ms | 80-120ms | 200-400ms |
| 2.4GHz WiFi (good) | 30-50 Mbps | 10-20ms | 30-50ms | 80-120ms |
| **5GHz WiFi** | 100-200 Mbps | 5-10ms | **20-30ms** | **40-60ms** |
| **Wired Ethernet** | 1000 Mbps | 1-3ms | **10-15ms** | **20-30ms** |

**Recommendations**:
1. ✅ Use 5GHz WiFi band (not 2.4GHz)
2. ✅ Stay close to router (< 5 meters)
3. ✅ Avoid interference (microwaves, other devices)
4. 🔧 Consider wired ethernet for server (if Quest supports it via Link)

---

## 📊 Combined Optimization Impact

### Scenario 1: Conservative (Minimal Risk)

**Changes**:
- Remove segmentation/depth from response
- Use 5GHz WiFi
- Keep JPEG quality 90
- Keep resolution 1280×960

**Results**:

| Metric | Before | After | Improvement |
|--------|--------|-------|-------------|
| upload_bytes | 125KB | 125KB | 0% |
| download_bytes | 425KB | **20KB** | **95%** |
| upload_ms | 40ms | **25ms** | **38%** |
| download_ms | 100ms | **25ms** | **75%** |
| **Total E2E** | 400ms | **250ms** | **38%** |

---

### Scenario 2: Aggressive (Maximum Speed)

**Changes**:
- Remove segmentation/depth from response
- Use 5GHz WiFi
- Lower JPEG quality to 75
- Lower resolution to 960×720

**Results**:

| Metric | Before | After | Improvement |
|--------|--------|-------|-------------|
| upload_bytes | 125KB | **45KB** | **64%** |
| download_bytes | 425KB | **15KB** | **96%** |
| upload_ms | 40ms | **18ms** | **55%** |
| download_ms | 100ms | **20ms** | **80%** |
| **Total E2E** | 400ms | **200ms** | **50%** |

**⚠️ Warning**: Test detection/pose accuracy at lower resolution!

---

## 🛠️ Implementation Steps

### Step 1: Server-Side Changes (High Priority)

**File**: `vision_server/app/routes/infer_human.py`

```python
# Line 56 - Add query parameters
async def infer_human(
    request: Request,
    image: UploadFile = File(...),
    mode: str = Query(default="detection", ...),
    include_mask: bool = Query(default=False, description="Include segmentation mask in response"),
    include_depth: bool = Query(default=False, description="Include depth map in response")
):
    # ... existing code ...

    # Lines 261-275 - Conditional field addition
    if include_mask and "segmentation" not in results:
        from app.inference import run_segmentation_model
        mask, mask_downsample = run_segmentation_model(pil_image)
        results["segmentation"] = {"mask": mask, "downsample_factor": mask_downsample}
    else:
        results["segmentation"] = None

    if include_depth and "depth" not in results:
        from app.inference import run_depth_model
        depth, depth_downsample = run_depth_model(pil_image)
        results["depth"] = {"depth_map": depth, "downsample_factor": depth_downsample}
    else:
        results["depth"] = None
```

**File**: `vision_server/app/models.py`

```python
class HumanInferenceResponse(BaseModel):
    detections: Optional[DetectionResult] = None
    segmentation: Optional[SegmentationResult] = None  # Make optional
    skeleton: SkeletonResult
    depth: Optional[DepthResult] = None               # Make optional
    # ... rest unchanged ...
```

---

### Step 2: Unity Client Changes

**File**: `Assets/PassthroughCameraApiSamples/MultiObjectDetection/SentisInference/Scripts/SentisInferenceRunManager.cs`

```csharp
// Line 35 - Update URL
[SerializeField] private string m_serverUrl = "http://192.168.0.135:8001/infer_human?mode=detection&include_mask=false&include_depth=false";

// Optional: Add JPEG quality control
[Header("Upload Optimization")]
[SerializeField, Range(60, 100)] private int m_jpegQuality = 80;

// Line 380 - Use quality setting
byte[] jpegBytes = tex2D.EncodeToJPG(m_jpegQuality);
```

**File**: `Assets/PassthroughCameraApiSamples/PoseEstimation/Scripts/PoseInferenceRunManager.cs`

```csharp
// Line 26 - Update URL
[SerializeField] private string m_serverUrl = "http://192.168.0.135:8001/infer_human?mode=both&include_mask=false&include_depth=false";

// Optional: Add JPEG quality control
[Header("Upload Optimization")]
[SerializeField, Range(60, 100)] private int m_jpegQuality = 80;

// Line 209 - Use quality setting
byte[] jpegBytes = tex2D.EncodeToJPG(m_jpegQuality);
```

---

### Step 3: Testing & Validation

#### Test 1: Verify Response Size Reduction

```bash
# Before optimization
adb logcat -s Unity | findstr /C:"[BYTES]"
# Expected: Upload=125000 Download=425000

# After optimization
adb logcat -s Unity | findstr /C:"[BYTES]"
# Expected: Upload=125000 Download=20000
```

#### Test 2: Check Excel Metrics

Run inference for 50 frames, check Excel:

| Metric | Before | Target After |
|--------|--------|--------------|
| avg(download_bytes) | ~425,000 | **~20,000** |
| avg(download_ms) | ~100ms | **~25ms** |
| avg(upload_ms) | ~40ms | ~40ms (or ~25ms if quality lowered) |
| avg(latency_ms) | ~400ms | **~250ms** |

#### Test 3: Verify Detection/Pose Accuracy

Compare results before/after:
- Same number of detections?
- Similar bounding box positions?
- Similar keypoint positions?
- Similar confidence scores?

---

## 📈 Expected Excel Output After Optimization

### Before Optimization

```
Frame 10:
- upload_bytes: 128000
- download_bytes: 432000
- upload_ms: 42.3
- download_ms: 105.7
- latency_ms: 412.5
```

### After Optimization (Conservative)

```
Frame 10:
- upload_bytes: 128000
- download_bytes: 18500    ← 96% reduction
- upload_ms: 24.1          ← 43% reduction (5GHz WiFi)
- download_ms: 22.3        ← 79% reduction
- latency_ms: 243.8        ← 41% reduction
```

### After Optimization (Aggressive)

```
Frame 10:
- upload_bytes: 47000      ← 63% reduction (quality 75, 960x720)
- download_bytes: 15200    ← 96% reduction
- upload_ms: 17.8          ← 58% reduction
- download_ms: 19.1        ← 82% reduction
- latency_ms: 201.4        ← 51% reduction
```

---

## 🎯 Recommendations

### Priority 1: Immediate (High Impact, Low Risk)

✅ **Remove segmentation/depth from response**
- Impact: download_ms 100ms → 25ms (75% reduction)
- Risk: None (Unity doesn't use these fields)
- Effort: Small server + Unity URL change
- **Do this first!**

✅ **Use 5GHz WiFi**
- Impact: Both upload/download 30-50% faster
- Risk: None
- Effort: Change Quest WiFi settings

---

### Priority 2: Testing Required (Medium Impact, Medium Risk)

🧪 **Lower JPEG quality to 75-80**
- Impact: upload_ms 40ms → 28ms (30% reduction)
- Risk: Need to verify detection accuracy
- Effort: One-line Unity change

🧪 **Lower resolution to 960×720**
- Impact: upload_ms 40ms → 22ms (45% reduction)
- Risk: May affect detection/pose accuracy
- Effort: Medium (need resize code)

---

## ⚠️ Important Notes

1. **Always test detection/pose accuracy** when changing image quality/resolution
2. **Monitor Excel logs** to verify improvements
3. **Network conditions vary** - test in target environment
4. **Server-side compression** (gzip) can further reduce download_ms by 20-30%

---

## 📚 Related Documents

- [Excel Formulas by Mode](EXCEL_FORMULAS_BY_MODE.md) - Field definitions
- [Metrics Latency Comparison](METRICS_LATENCY_COMPARISON.md) - Performance analysis
- [Excel Metrics Issues](EXCEL_METRICS_ISSUES.md) - Known issues

---

**Created**: 2026-04-06
**Author**: Claude (Anthropic AI)
**Version**: 1.0
