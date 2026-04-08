# Unity Refactoring Progress - Depth Mode Integration

**Date**: 2026-04-07
**Status**: 🚧 In Progress (Parts A-B.2 Complete)

---

## Overview

Refactoring Unity PassthroughCameraApiSamples to support three inference modes with proper architecture:
1. Object Detection
2. Pose Estimation
3. **Depth Estimation** (NEW)

---

## Completed Work

### ✅ Part A: Server-Side Depth Mode

**Files Created**:
- `vision_server/app/inference_depth.py` - Real MiDaS depth model integration
- `vision_server/Documentation/DEPTH_MODE_IMPLEMENTATION.md`

**Files Modified**:
- `vision_server/app/routes/infer_human.py` - Added `mode=depth` support

**Result**: Server now accepts `mode=depth` and returns 320×240 depth maps using MiDaS_small model.

**Testing**: Ready for testing (server restart required)

---

### ✅ Part B.1: InferenceMode Enum and Configuration

**File Created**: `Assets/PassthroughCameraApiSamples/Shared/Scripts/InferenceConfig.cs`

**Key Features**:

1. **InferenceMode Enum**:
```csharp
public enum InferenceMode
{
    ObjectDetection,   // mode=detection
    PoseEstimation,    // mode=pose
    Both,              // mode=both
    DepthEstimation    // mode=depth (NEW)
}
```

2. **InferenceConfig Class**:
   - Replaces hardcoded server URLs
   - Dynamic URL building: `BuildUrl()` generates proper query parameters
   - Per-mode FPS configuration
   - Validation and logging utilities
   - Expected download size calculation

**Example Usage**:
```csharp
InferenceConfig config = new InferenceConfig();
config.mode = InferenceMode.DepthEstimation;
config.targetFPS = 5f;
config.jpegQuality = 80;

string url = config.BuildUrl();
// Result: "http://192.168.0.135:8001/infer_human?mode=depth&include_mask=false&include_depth=false"
```

**Benefits**:
- ✅ Single source of truth for server communication
- ✅ Type-safe mode selection (no string typos)
- ✅ Per-mode FPS targets (addresses Part C)
- ✅ Automatic validation and warnings

---

### ✅ Part B.2: SharedInferenceHUD - Accurate Metrics Display

**File Created**: `Assets/PassthroughCameraApiSamples/Shared/Scripts/SharedInferenceHUD.cs`

**Key Improvements**:

1. **FIXED FPS Calculation** (Critical Bug Fix):
   - **OLD**: Displayed Unity rendering FPS (60-72 Hz) - MISLEADING
   - **NEW**: Displays actual inference FPS = `1000 / E2E_ms`
   - Example: E2E = 200ms → Inference FPS = 5.0

2. **Mode Display**:
   - Shows current inference mode: "Object Detection", "Pose Estimation", "Depth Estimation"
   - Shows target FPS vs actual FPS

3. **Frame Statistics** (Part D Foundation):
   - Total frames processed
   - Dropped frame count and ratio
   - Frozen frame count and ratio
   - Session duration tracking
   - `ReportDroppedFrame()` and `ReportFrozenFrame()` APIs

4. **Enhanced Metrics**:
   - Bandwidth (upload/download sizes)
   - Latency breakdown with percentages
   - Keypoint confidence (for pose mode)
   - Session summary logging on destroy

**Example HUD Display**:
```
Depth Estimation
Inference FPS: 4.2 (target: 5.0)
E2E: 238ms
 ├Upload: 32ms (13%)
 ├Server: 95ms (40%)
 ├Download: 98ms (41%)
 └Parse: 13ms (6%)
Detections: 0
Upload: 85KB
Download: 305KB

Frame Stats (120s)
Total: 504
Dropped: 12 (2.4%)
Frozen: 3 (0.6%)
```

---

## In Progress

### 🚧 Part B.3: Refactor Existing Scripts

**Next Steps**:

1. **Modify SentisInferenceRunManager.cs**:
   - Replace hardcoded `m_serverUrl` with `InferenceConfig`
   - Replace local `m_jpegQuality` with `config.jpegQuality`
   - Implement FPS throttling based on `config.targetFPS`
   - Add drop frame detection

2. **Modify PoseInferenceRunManager.cs**:
   - Same refactoring as above
   - Add support for switching to depth mode
   - Integrate SharedInferenceHUD

3. **Create DepthInferenceRunManager.cs** (NEW):
   - New script specifically for depth mode
   - Parses depth response from server
   - Renders depth visualization
   - Uses SharedInferenceHUD

---

## Pending Work

### Part C: Per-Mode FPS Throttling Logic

**Status**: Foundation complete (InferenceConfig has `targetFPS`), logic pending

**Implementation**:
```csharp
// In RunInference()
float targetInterval = m_config.GetInferenceInterval();  // 1 / targetFPS
float timeSinceLastInference = Time.time - m_lastInferenceTime;

if (timeSinceLastInference < targetInterval)
{
    // Drop this frame - too soon since last inference
    m_sharedHUD.ReportDroppedFrame();
    yield break;
}

// Proceed with inference...
m_lastInferenceTime = Time.time;
```

**Files to Modify**:
- `SentisInferenceRunManager.cs`
- `PoseInferenceRunManager.cs`
- `DepthInferenceRunManager.cs` (new)

---

### Part D: Drop Frame and Freeze Frame Tracking

**Status**: HUD foundation complete, detection logic pending

**Drop Frame Definition**:
- Frame intentionally skipped due to FPS throttling
- Incremented when: `timeSinceLastInference < targetInterval`

**Freeze Frame Definition**:
- No fresh inference result available, displaying old data
- Incremented when: Server request in progress, but new frame needs to be rendered

**Implementation**:
```csharp
// Drop frame detection (Part C handles this)
if (timeSinceLastInference < targetInterval)
{
    m_sharedHUD.ReportDroppedFrame();
    return;
}

// Freeze frame detection
if (m_inferenceInProgress)
{
    // Still waiting for previous inference
    m_sharedHUD.ReportFrozenFrame();
    return;  // Keep displaying old visualization
}
```

**Files to Modify**:
- Same as Part C

---

### Part E: Integrate Depth Mode into Latency/Metrics System

**Status**: Pending

**Requirements**:
- Ensure Excel logging works for depth mode
- Add depth-specific metrics (depth map size, min/max depth values)
- Update `model_used` field to show "midas_depth" or similar

**Files to Modify**:
- Server-side: Already done ✅ (detectioncount=0 for depth mode)
- Unity-side: Add depth metrics to headers when `mode=depth`

---

### Part F: Create Basic Depth Visualization

**Status**: Pending

**Requirements**:
1. Parse depth response from server
2. Convert depth array to Unity Texture2D
3. Apply colormap (grayscale or inferno)
4. Render as overlay or separate display
5. Show depth value at center pixel

**Example Depth Response**:
```json
{
  "depth": {
    "height": 240,
    "width": 320,
    "downsample_factor": 4,
    "values": [[0.123, 0.456, ...], [...]]
  }
}
```

**Implementation Approach**:
```csharp
// Parse depth data
DepthData depthData = response.depth;
float[][] depthValues = depthData.values;

// Create texture
Texture2D depthTexture = new Texture2D(depthData.width, depthData.height, TextureFormat.RGB24, false);

// Apply colormap (grayscale or colored)
for (int y = 0; y < height; y++)
{
    for (int x = 0; x < width; x++)
    {
        float depthValue = depthValues[y][x];  // 0=near, 1=far
        Color color = ApplyColormap(depthValue);  // Inferno, viridis, or grayscale
        depthTexture.SetPixel(x, y, color);
    }
}

depthTexture.Apply();

// Render to UI RawImage or world-space quad
m_depthDisplay.texture = depthTexture;
```

**Files to Create**:
- `Assets/PassthroughCameraApiSamples/DepthEstimation/Scripts/DepthInferenceRunManager.cs`
- `Assets/PassthroughCameraApiSamples/DepthEstimation/Scripts/DepthVisualization.cs`

---

### Part G: Robustness and Mode Cleanup

**Status**: Pending

**Requirements**:
1. **Null Response Safety**:
   - Handle null depth, null detections, null skeleton
   - Don't crash if server returns error

2. **Mode Switch State Reset**:
   - Clear old visualizations when switching modes
   - Reset HUD statistics
   - Cancel in-progress requests

3. **Cleanup on Mode Change**:
   - Remove old bounding boxes (detection mode)
   - Remove old skeletons (pose mode)
   - Remove old depth overlay (depth mode)

**Implementation**:
```csharp
public void OnModeChanged(InferenceMode newMode)
{
    // Clear old visualizations
    m_detectionManager?.ClearAllDetections();
    m_poseManager?.ClearAllSkeletons();
    m_depthVisualization?.ClearDepthOverlay();

    // Reset HUD
    m_sharedHUD.ResetStatistics();
    m_sharedHUD.SetMode(newMode, m_config.targetFPS);

    // Cancel in-progress requests
    if (m_currentRequest != null)
    {
        m_currentRequest.Abort();
        m_currentRequest = null;
        m_inferenceInProgress = false;
    }
}
```

---

### Part H: Test Plan and Documentation

**Status**: Pending

**Test Plan Contents**:
1. Server startup verification
2. Test each mode individually (detection, pose, both, depth)
3. Verify FPS throttling works
4. Verify drop/freeze frame counting
5. Verify mode switching
6. Performance benchmarks for each mode
7. Network bandwidth measurements

**Documentation to Create**:
- `DEPTH_MODE_USER_GUIDE.md` - How to use depth mode in Unity
- `INFERENCE_MODES_COMPARISON.md` - Performance comparison table
- `TROUBLESHOOTING.md` - Common issues and solutions

---

## Files Created So Far

### Server Files
1. `vision_server/app/inference_depth.py` ✅
2. `vision_server/Documentation/DEPTH_MODE_IMPLEMENTATION.md` ✅

### Unity Shared Files
1. `Assets/PassthroughCameraApiSamples/Shared/Scripts/InferenceConfig.cs` ✅
2. `Assets/PassthroughCameraApiSamples/Shared/Scripts/SharedInferenceHUD.cs` ✅

### Documentation
1. `Documentation/UNITY_REFACTORING_PROGRESS.md` ✅ (this file)

---

## Files to Modify (Part B.3 - Next)

### Existing Unity Scripts to Refactor
1. `Assets/PassthroughCameraApiSamples/MultiObjectDetection/SentisInference/Scripts/SentisInferenceRunManager.cs`
   - Replace `m_serverUrl` with `InferenceConfig config`
   - Add FPS throttling
   - Integrate SharedInferenceHUD

2. `Assets/PassthroughCameraApiSamples/PoseEstimation/Scripts/PoseInferenceRunManager.cs`
   - Same refactoring as above
   - Add depth mode support

---

## Files to Create (Parts F-H)

### Unity Depth Mode (Part F)
1. `Assets/PassthroughCameraApiSamples/DepthEstimation/DepthEstimation.unity` - New scene
2. `Assets/PassthroughCameraApiSamples/DepthEstimation/Scripts/DepthInferenceRunManager.cs` - Main script
3. `Assets/PassthroughCameraApiSamples/DepthEstimation/Scripts/DepthVisualization.cs` - Rendering
4. `Assets/PassthroughCameraApiSamples/DepthEstimation/Scripts/DepthResponseModels.cs` - JSON models

### Documentation (Part H)
1. `Documentation/DEPTH_MODE_USER_GUIDE.md`
2. `Documentation/INFERENCE_MODES_COMPARISON.md`
3. `Documentation/TROUBLESHOOTING.md`
4. `Documentation/TEST_PLAN.md`

---

## Critical Design Decisions

### 1. FPS Calculation Fix
**Problem**: Original HUD showed Unity rendering FPS (60-72 Hz), NOT inference FPS
**Solution**: SharedInferenceHUD calculates `Inference FPS = 1000 / E2E_ms`
**Impact**: Users now see accurate inference rate (typically 2-10 FPS)

### 2. Shared vs Per-Mode Scripts
**Decision**: Create shared InferenceConfig and SharedInferenceHUD, but keep separate run managers
**Rationale**:
- Each mode has different response parsing needs
- Detection mode: bounding boxes
- Pose mode: keypoints/skeleton
- Depth mode: depth map texture
- Sharing config/HUD reduces duplication while allowing mode-specific logic

### 3. Depth Map Bandwidth
**Challenge**: 320×240 depth map = ~300KB per frame
**Solution**: Default to downsample_factor=4 and targetFPS=5
**Result**: ~1.5MB/second bandwidth (acceptable for 5GHz WiFi)

---

## Next Immediate Steps

1. **Refactor SentisInferenceRunManager.cs** (Part B.3):
   - [ ] Add `InferenceConfig m_config` field
   - [ ] Replace hardcoded URL with `m_config.BuildUrl()`
   - [ ] Add FPS throttling logic (Part C)
   - [ ] Integrate SharedInferenceHUD
   - [ ] Add drop frame detection

2. **Refactor PoseInferenceRunManager.cs** (Part B.3):
   - [ ] Same changes as above

3. **Create DepthInferenceRunManager.cs** (Part F):
   - [ ] Parse depth response
   - [ ] Create depth texture
   - [ ] Render visualization

---

**Last Updated**: 2026-04-07
**Next Review**: After Part B.3 completion
