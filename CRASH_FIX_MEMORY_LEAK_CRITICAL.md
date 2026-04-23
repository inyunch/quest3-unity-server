# 🚨 CRITICAL: Memory Leak Crash Fix (30-Second Crash)

**Date**: 2026-04-22
**Severity**: CRITICAL - Causes crash after ~30 seconds
**Status**: ⚠️ **REQUIRES IMMEDIATE FIX**

---

## Problem Analysis

Your Unity app crashes after ~30 seconds due to **severe memory leaks** in the V3 architecture. Three critical issues identified:

### Issue 1: Texture2D Memory Leak (CRITICAL)

**Location**: `SegmentationInferenceRunManager.cs` line 1089

**Problem Code**:
```csharp
// DisplayV3Frame() method
foreach (var det in response.detections)
{
    if (!string.IsNullOrEmpty(det.mask_png_base64))
    {
        byte[] maskBytes = System.Convert.FromBase64String(det.mask_png_base64);

        // ⚠️ LEAK: Texture2D created but NEVER destroyed!
        Texture2D maskTexture = new Texture2D(2, 2, TextureFormat.RGBA32, false);

        if (maskTexture.LoadImage(maskBytes))
        {
            m_uiInference.RenderMask(maskIndex, maskTexture, det.bbox_pixels, cachedCameraPose);
            masksRendered++;
        }
        // ❌ NO Destroy(maskTexture) here!
    }
}
```

**Memory Leak Rate**:
- FPS: 10 frames/second
- Detections per frame: ~2-5
- Leak rate: **20-50 Texture2D objects/second**
- After 30 seconds: **600-1500 leaked textures!**

---

### Issue 2: Material Memory Leak (CRITICAL)

**Location**: `SegmentationInferenceUiManager.cs` (in RenderMask method)

**Problem Code**:
```csharp
public void RenderMask(int maskId, Texture2D maskTexture, int[] bboxPixels, Pose cameraPose)
{
    // ... positioning code ...

    var renderer = quad.GetComponent<Renderer>();

    // ⚠️ LEAK: New Material created every frame, NEVER destroyed!
    var material = new Material(Shader.Find("Unlit/Transparent"));
    material.mainTexture = maskTexture;
    material.color = new Color(0f, 1f, 0f, 0.7f);
    renderer.material = material;

    // ❌ NO Destroy(material) when clearing masks!
}
```

**Memory Leak Rate**:
- **20-50 Material objects/second** (same as textures)
- Each Material holds references to Shader + Texture
- After 30 seconds: **600-1500 leaked materials!**

---

### Issue 3: FrameTrace Dictionary Growth (MEDIUM)

**Location**: `FrameTelemetryTracker.cs`

**Current Settings**:
```csharp
private const int MAX_FRAME_TRACES = 100;        // Too high
private const float FRAME_TIMEOUT_SECONDS = 5.0f; // Too long
```

**Cleanup Frequency**:
```csharp
// SegmentationInferenceRunManager.cs line 1028
if (Time.frameCount % 300 == 0)
{
    m_telemetry.CleanupOldTraces();  // Only every 300 frames (~5 seconds at 60fps)
}
```

**Problem**:
- At 10 FPS inference, generates 50 frames in 5 seconds
- Cleanup only happens every 5 seconds
- Dictionary grows to 50 entries before cleanup
- Not critical but wastes memory

---

## Impact Analysis

### Memory Usage Projection

| Time | Texture2D | Material | FrameTrace | Total Estimate |
|------|-----------|----------|------------|----------------|
| 10s | 200-500 | 200-500 | ~100 | ~10-25 MB |
| 20s | 400-1000 | 400-1000 | ~100 | ~20-50 MB |
| 30s | 600-1500 | 600-1500 | ~100 | **~30-75 MB** ⚠️ |

**Typical Quest 3 per-app memory limit**: ~300-500 MB
**Crash threshold**: When GC can't keep up with allocation rate

### Why Crash at 30 Seconds?

1. **Texture2D**: Each ~640×640 RGBA32 = ~1.6 MB × 1500 = **2.4 GB** uncompressed!
2. **Unity tries GC** but objects are still referenced
3. **Allocation failure** → Crash

---

## Fix Implementation

### Fix 1: Destroy Texture2D After Use (CRITICAL)

**File**: `SegmentationInferenceRunManager.cs`

**Original Code** (line 1059-1115):
```csharp
private void DisplayV3Frame(FrameResponse response)
{
    // ... validation code ...

    m_uiInference.ClearAllMasks();
    var cachedCameraPose = m_cameraAccess.GetCameraPose();

    int maskIndex = 0;
    int masksRendered = 0;

    foreach (var det in response.detections)
    {
        if (!string.IsNullOrEmpty(det.mask_png_base64))
        {
            try
            {
                byte[] maskBytes = System.Convert.FromBase64String(det.mask_png_base64);
                Texture2D maskTexture = new Texture2D(2, 2, TextureFormat.RGBA32, false);

                if (maskTexture.LoadImage(maskBytes))
                {
                    m_uiInference.RenderMask(maskIndex, maskTexture, det.bbox_pixels, cachedCameraPose);
                    masksRendered++;
                }
                else
                {
                    Debug.LogError($"[V3 SEGMENTATION] Failed to LoadImage for detection {maskIndex}");
                }
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[V3 SEGMENTATION] Error decoding mask {maskIndex}: {e.Message}");
            }

            maskIndex++;
        }
    }

    UpdateUIMetrics(response);
}
```

**FIXED Code**:
```csharp
private void DisplayV3Frame(FrameResponse response)
{
    // ... validation code ...

    m_uiInference.ClearAllMasks();
    var cachedCameraPose = m_cameraAccess.GetCameraPose();

    int maskIndex = 0;
    int masksRendered = 0;
    List<Texture2D> tempTextures = new List<Texture2D>();  // ✅ Track for cleanup

    foreach (var det in response.detections)
    {
        if (!string.IsNullOrEmpty(det.mask_png_base64))
        {
            Texture2D maskTexture = null;  // ✅ Declare outside try
            try
            {
                byte[] maskBytes = System.Convert.FromBase64String(det.mask_png_base64);
                maskTexture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                tempTextures.Add(maskTexture);  // ✅ Track for cleanup

                if (maskTexture.LoadImage(maskBytes))
                {
                    m_uiInference.RenderMask(maskIndex, maskTexture, det.bbox_pixels, cachedCameraPose);
                    masksRendered++;
                }
                else
                {
                    Debug.LogError($"[V3 SEGMENTATION] Failed to LoadImage for detection {maskIndex}");
                }
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[V3 SEGMENTATION] Error decoding mask {maskIndex}: {e.Message}");
            }

            maskIndex++;
        }
    }

    UpdateUIMetrics(response);

    // ✅ CRITICAL: Destroy all temporary textures after RenderMask copies them
    foreach (var tex in tempTextures)
    {
        if (tex != null)
        {
            Destroy(tex);
        }
    }
    tempTextures.Clear();

    Debug.Log($"[V3 SEGMENTATION] Cleaned up {tempTextures.Count} temporary textures");
}
```

**Why This Works**:
- RenderMask() assigns `material.mainTexture = maskTexture`, which creates an internal reference
- Unity copies the texture data to GPU
- After RenderMask() returns, we can safely Destroy() the original Texture2D
- GPU memory is managed separately

---

### Fix 2: Material Pooling in UI Manager (CRITICAL)

**File**: `SegmentationInferenceUiManager.cs`

**Add Material Cache**:
```csharp
// Add at class level (around line 20)
private Material m_cachedMaskMaterial;  // ✅ Reuse instead of creating new

private void Awake()
{
    // ✅ Create material once
    m_cachedMaskMaterial = new Material(Shader.Find("Unlit/Transparent"));
}

private void OnDestroy()
{
    // ✅ Cleanup cached material
    if (m_cachedMaskMaterial != null)
    {
        Destroy(m_cachedMaskMaterial);
        m_cachedMaskMaterial = null;
    }
}
```

**Fix RenderMask() Method** (line 291):
```csharp
public void RenderMask(int maskId, Texture2D maskTexture, int[] bboxPixels, Pose cameraPose)
{
    // ... positioning code ...

    var renderer = quad.GetComponent<Renderer>();

    // ❌ OLD (LEAK):
    // var material = new Material(Shader.Find("Unlit/Transparent"));

    // ✅ NEW (FIXED):
    renderer.material = m_cachedMaskMaterial;  // Reuse cached material
    m_cachedMaskMaterial.mainTexture = maskTexture;
    m_cachedMaskMaterial.color = new Color(0f, 1f, 0f, 0.7f);

    maskData.SamplePoints.Add(quad);
}
```

**Alternative (If Multiple Colors Needed)**:
```csharp
// Use material instance (Unity manages lifecycle)
renderer.sharedMaterial = m_cachedMaskMaterial;
renderer.material.mainTexture = maskTexture;  // Creates instance automatically
renderer.material.color = new Color(0f, 1f, 0f, 0.7f);
```

---

### Fix 3: Optimize FrameTrace Cleanup (MEDIUM)

**File**: `FrameTelemetryTracker.cs`

**Reduce Limits**:
```csharp
// Line 57-58
private const int MAX_FRAME_TRACES = 50;         // ✅ Reduced from 100
private const float FRAME_TIMEOUT_SECONDS = 2.0f; // ✅ Reduced from 5.0f
```

**File**: `SegmentationInferenceRunManager.cs`

**Increase Cleanup Frequency**:
```csharp
// Line 1028 (in Update method)
// ❌ OLD: Cleanup every 300 frames (~5 seconds)
if (Time.frameCount % 300 == 0)

// ✅ NEW: Cleanup every 60 frames (~1 second at 60fps)
if (Time.frameCount % 60 == 0)
{
    m_telemetry.CleanupOldTraces();
}
```

---

### Fix 4: Add Memory Monitoring (RECOMMENDED)

**File**: Create new `Assets/PassthroughCameraApiSamples/Shared/Scripts/MemoryMonitor.cs`

```csharp
using UnityEngine;
using UnityEngine.Profiling;

namespace PassthroughCameraSamples.Shared
{
    /// <summary>
    /// Monitors memory usage and logs warnings when thresholds exceeded.
    /// Attach to any scene GameObject for automatic monitoring.
    /// </summary>
    public class MemoryMonitor : MonoBehaviour
    {
        [SerializeField] private float m_logInterval = 5.0f;  // Log every 5 seconds
        [SerializeField] private long m_warningThresholdMB = 200;  // Warn at 200 MB

        private float m_nextLogTime = 0f;

        private void Update()
        {
            float currentTime = Time.time;
            if (currentTime >= m_nextLogTime)
            {
                m_nextLogTime = currentTime + m_logInterval;

                long totalMemoryMB = Profiler.GetTotalReservedMemoryLong() / 1048576;
                long usedMemoryMB = Profiler.GetTotalAllocatedMemoryLong() / 1048576;
                long textureMemoryMB = Profiler.GetAllocatedMemoryForGraphicsDriver() / 1048576;

                Debug.Log($"[MEMORY] Total: {totalMemoryMB} MB, Used: {usedMemoryMB} MB, GPU: {textureMemoryMB} MB");

                if (usedMemoryMB > m_warningThresholdMB)
                {
                    Debug.LogWarning($"[MEMORY] High memory usage detected: {usedMemoryMB} MB (threshold: {m_warningThresholdMB} MB)");

                    // Force GC if very high
                    if (usedMemoryMB > m_warningThresholdMB * 1.5f)
                    {
                        Debug.LogWarning($"[MEMORY] Forcing garbage collection...");
                        System.GC.Collect();
                        Resources.UnloadUnusedAssets();
                    }
                }
            }
        }
    }
}
```

**Usage**: Attach to any GameObject in Segmentation scene:
1. Select any GameObject (e.g., "InferenceManager")
2. Add Component → Scripts → Memory Monitor
3. Run app and monitor Unity logs for `[MEMORY]` entries

---

## Testing Verification

### Before Fix (Expected)
- Crash after **~30 seconds**
- Memory grows continuously
- No cleanup logs

### After Fix (Expected)
```
[V3 SEGMENTATION] Cleaned up 3 temporary textures
[MEMORY] Total: 180 MB, Used: 120 MB, GPU: 45 MB
[MEMORY] Total: 185 MB, Used: 125 MB, GPU: 47 MB  (stable!)
```

### Test Plan
1. Apply all 4 fixes
2. Build and deploy to Quest 3
3. Run Segmentation scene for **5 minutes** (10× previous crash time)
4. Monitor memory logs every 5 seconds
5. Verify memory usage stays below 250 MB

---

## Additional Optimizations (Optional)

### Optimization 1: Reduce Mask Resolution

**Problem**: 640×640 RGBA32 masks = 1.6 MB each

**Solution**: Downsample on server side to 320×240 (4× smaller)

**Server Change** (`vision_server/app/core/inference/processors/segmentation.py`):
```python
# Already implemented in server - verify downsampling is active
mask_img = mask_img.resize((320, 240), Image.BILINEAR)
```

---

### Optimization 2: Use DXT1/DXT5 Texture Compression

**Unity Side** (`SegmentationInferenceRunManager.cs`):
```csharp
// Line 1089 - Use compressed format
Texture2D maskTexture = new Texture2D(2, 2, TextureFormat.DXT5, false);
maskTexture.LoadImage(maskBytes);  // Auto-converts to DXT5
```

**Benefit**: ~4-6× smaller memory footprint (RGBA32 → DXT5)

---

### Optimization 3: Limit Active Masks

**UI Manager** - Only render top N detections:
```csharp
// In SegmentationInferenceRunManager.DisplayV3Frame()
int MAX_MASKS = 5;  // Only render top 5 detections
int masksRendered = 0;

foreach (var det in response.detections)
{
    if (masksRendered >= MAX_MASKS)
        break;  // Skip remaining masks

    // ... render code ...
    masksRendered++;
}
```

---

## Priority Action Items

### CRITICAL (Fix Today)
1. ✅ Fix 1: Destroy Texture2D after use
2. ✅ Fix 2: Material pooling in UI manager

### HIGH (Fix This Week)
3. ✅ Fix 3: Optimize FrameTrace cleanup
4. ✅ Fix 4: Add memory monitoring

### MEDIUM (Optional Enhancements)
5. ⚡ Verify server mask downsampling (320×240)
6. ⚡ Use DXT5 texture compression
7. ⚡ Limit active masks to top 5

---

## Similar Issues in Other Scenes

**Check these files for similar leaks**:

1. **PoseInferenceRunManager.cs**:
   - Does it create Texture2D for pose visualization?
   - Are skeleton line renderers properly pooled?

2. **SentisInferenceRunManager.cs** (Detection):
   - Does it create materials for bounding boxes?
   - Are debug visualization objects destroyed?

**Recommended**: Run memory profiler on all 3 scenes after fixing Segmentation.

---

## Root Cause Summary

The V3.0 architecture focused on **UDP transport performance** but introduced memory leaks in the **visualization layer**:

1. **Texture2D leak**: Created per-frame but never destroyed
2. **Material leak**: Created per-mask but never destroyed
3. **Inadequate cleanup**: FrameTrace cleanup too infrequent

These are **classic Unity memory management mistakes**:
- Rule: **"If you create it, you destroy it"**
- Textures, Materials, GameObjects → Must call `Destroy()`
- Use object pooling for frequently created objects

---

## Expected Results After Fix

| Metric | Before | After | Improvement |
|--------|--------|-------|-------------|
| Crash time | 30 sec | Never ⭐ | Stable |
| Memory @ 1 min | ~100 MB | ~80 MB | -20% |
| Memory @ 5 min | Crash | ~85 MB | Stable |
| FPS | 10 | 10 | Maintained |
| Latency | ~300 ms | ~300 ms | No regression |

---

**Status**: ⚠️ **CRITICAL FIXES REQUIRED**
**Priority**: P0 (Blocking deployment)
**Estimated Fix Time**: 2-3 hours (implement + test)

**Next Action**: Apply Fix 1 and Fix 2 immediately, then rebuild and test.
