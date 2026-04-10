# ROI Depth Estimation Guide

## Overview

ROI (Region of Interest) Depth Estimation is an optimized depth rendering approach that combines object detection with depth estimation. Instead of rendering depth for the entire scene, it only visualizes depth points within detected object bounding boxes.

**Key Benefits**:
- 3-5x performance improvement over full-frame depth rendering
- Reduces rendering from thousands of points to hundreds
- Maintains high-quality depth visualization for objects of interest
- Lower bandwidth usage (4x downsampled depth map)

## Architecture

### Two-Stage Approach

1. **Server-Side Processing** (`/infer_roi_depth` endpoint):
   - Stage 1: YOLO object detection (YOLOv8n)
   - Stage 2: MiDaS depth estimation (full frame)
   - Returns: Combined detections + depth map

2. **Client-Side Filtering** (Unity):
   - Receives full depth map + detections
   - Filters depth points to ROI (detection bounding boxes)
   - Renders only depth within detected objects

### Why This Approach?

- **Server**: Runs full-frame depth estimation (GPU-accelerated)
- **Client**: Lightweight ROI filtering (no heavy compute)
- **Flexibility**: Client can adjust ROI padding/filtering without server changes
- **Bandwidth**: Downsampled depth (4x) keeps network transfer low

## Server Implementation

### Endpoint

**URL**: `http://192.168.0.135:8001/infer_roi_depth`

**Method**: POST (multipart/form-data)

**Request Parameters**:
```
image: UploadFile (JPEG/PNG)
min_confidence: float (default 0.5) - Detection confidence threshold
```

**Response Format**:
```json
{
  "detections": {
    "detections": [
      {
        "class_id": 0,
        "class_name": "person",
        "confidence": 0.87,
        "bbox": [0.2, 0.3, 0.6, 0.8],  // normalized [x1, y1, x2, y2]
        "bbox_pixels": [256, 384, 768, 1024]
      }
    ],
    "num_detections": 1
  },
  "depth": {
    "width": 320,
    "height": 240,
    "downsample_factor": 4,
    "values": [[0.12, 0.15, ...], [...]]  // 2D array [height][width]
  },
  "input_image_width": 1280,
  "input_image_height": 960,
  "processing_time_ms": 245.3
}
```

### Server Code

**File**: `vision_server/app/routes/roi_depth.py`

**Key Implementation**:
```python
@router.post("/infer_roi_depth")
async def infer_roi_depth(
    request: Request,
    image: UploadFile = File(...),
    min_confidence: float = Query(default=0.5)
):
    # 1. Run YOLO detection
    if YOLO_AVAILABLE:
        results = run_all_models_with_yolo(
            pil_image,
            conf_threshold=min_confidence,
            label_filter=None  # Detect all objects
        )
        detections_list = results.get("detections", [])

    # 2. Run MiDaS depth estimation
    if DEPTH_AVAILABLE:
        depth_map, depth_downsample = run_depth_estimation(
            pil_image,
            downsample_factor=4  # 4x downsample for bandwidth
        )

    # 3. Convert numpy types to Python native types
    detections_response = [
        {
            "class_id": int(d["class_id"]),
            "class_name": str(d["class_name"]),
            "confidence": float(d["confidence"]),
            "bbox": [float(x) for x in d["bbox"]],
            "bbox_pixels": [int(x) for x in d["bbox_pixels"]]
        }
        for d in detections_list
    ]

    # 4. Build response
    return {
        "detections": {...},
        "depth": {
            "width": int(depth_w),
            "height": int(depth_h),
            "values": depth_map.astype(np.float32).tolist()
        }
    }
```

**Dependencies**:
- `app.inference_yolo.run_all_models_with_yolo` - YOLO detection
- `app.inference_depth.run_depth_estimation` - MiDaS depth

## Unity Implementation

### Scene Components

**Scene**: `Assets/PassthroughCameraApiSamples/DepthEstimation/DepthEstimation.unity`

**Key GameObjects**:
1. **DepthManager** - Scene management and spatial anchoring
2. **DepthVisualization** - 3D depth point cloud rendering
3. **InferenceRunManager** - HTTP client and inference orchestration

### Scripts

#### 1. DepthInferenceRunManager.cs

**Responsibility**: HTTP client for ROI depth endpoint

**Key Code**:
```csharp
private IEnumerator RunServerInference(Texture texture)
{
    // Encode as JPEG
    byte[] jpegBytes = tex2D.EncodeToJPG(m_inferenceConfig.jpegQuality);

    // Build request
    string serverUrl = "http://192.168.0.135:8001/infer_roi_depth";
    UnityWebRequest request = UnityWebRequest.Post(serverUrl, formData);

    // Add headers
    request.SetRequestHeader("X-Scene-Name", "DepthEstimation");
    request.SetRequestHeader("X-Frame-Id", m_frameId.ToString());
    request.SetRequestHeader("X-Min-Confidence", m_minDetectionConfidence.ToString("F2"));

    // Send request
    yield return request.SendWebRequest();

    // Parse response
    DepthROIResponse response = JsonConvert.DeserializeObject<DepthROIResponse>(jsonResponse);

    // Filter detections by confidence
    List<DetectionData> validDetections = new List<DetectionData>();
    foreach (var det in response.detections.detections)
    {
        if (det.confidence >= m_minDetectionConfidence)
            validDetections.Add(det);
    }

    // Visualize with ROI filtering
    m_depthVisualizationManager.DrawDepthMapROI(
        response.depth,
        validDetections.ToArray(),
        cameraPose
    );
}
```

#### 2. DepthVisualizationManager.cs

**Responsibility**: 3D depth point cloud rendering with ROI filtering

**Key Code**:
```csharp
public void DrawDepthMapROI(
    DepthInferenceRunManager.DepthData depth,
    DepthInferenceRunManager.DetectionData[] detections,
    Pose cameraPose)
{
    ClearDepthPoints();  // Return points to pool

    if (!m_useROI || detections == null || detections.Length == 0)
    {
        // Fallback: render full depth map
        DrawFullDepthMap(depth, cameraPose);
        return;
    }

    int pointsRendered = 0;

    // For each detection, render depth within ROI
    foreach (var detection in detections)
    {
        // Calculate ROI with padding
        int x1 = Mathf.Max(0, (int)((detection.bbox[0] - m_roiPadding) * depth.width));
        int y1 = Mathf.Max(0, (int)((detection.bbox[1] - m_roiPadding) * depth.height));
        int x2 = Mathf.Min(depth.width, (int)((detection.bbox[2] + m_roiPadding) * depth.width));
        int y2 = Mathf.Min(depth.height, (int)((detection.bbox[3] + m_roiPadding) * depth.height));

        // Sample depth within ROI
        for (int y = y1; y < y2; y += m_samplingRate)
        {
            for (int x = x1; x < x2; x += m_samplingRate)
            {
                if (pointsRendered >= m_maxPointsPerFrame)
                    break;

                float depthValue = depth.values[y][x];

                // Skip invalid depths
                if (depthValue < 0.01f || depthValue > 0.99f)
                    continue;

                // Get 3D world position via raycasting
                Vector3 worldPos = GetWorldPositionFromDepth(
                    x, y, depthValue, depth, cameraPose
                );

                // Get depth point from pool
                GameObject point = GetDepthPoint();
                point.transform.position = worldPos;
                point.transform.localScale = Vector3.one * m_pointSize;

                // Color by depth (cyan gradient)
                var renderer = point.GetComponent<Renderer>();
                renderer.material.color = GetDepthColor(depthValue);

                m_activeDepthPoints.Add(point);
                pointsRendered++;
            }
        }
    }

    Debug.Log($"[DEPTH VIZ] Rendered {pointsRendered} points in {detections.Length} ROIs");
}

private Vector3 GetWorldPositionFromDepth(
    int x, int y, float depthValue,
    DepthInferenceRunManager.DepthData depth,
    Pose cameraPose)
{
    // Convert depth pixel to normalized screen coordinates
    float screenX = (float)x / depth.width;
    float screenY = 1.0f - ((float)y / depth.height);  // Flip Y

    // Raycast from camera to world
    Ray ray = m_passthroughCamera.ViewportPointToRay(new Vector3(screenX, screenY, 0));

    // Use depth value to calculate distance
    // Normalize depth: 0=near, 1=far
    float distance = Mathf.Lerp(m_minDepth, m_maxDepth, depthValue);

    // Calculate world position
    Vector3 worldPos = ray.origin + ray.direction * distance;

    return worldPos;
}

private Color GetDepthColor(float depthValue)
{
    // Cyan gradient: near=bright cyan, far=dark cyan
    float intensity = 1.0f - depthValue;
    return new Color(0, intensity, intensity, 1);
}
```

**Object Pooling**:
```csharp
private GameObject GetDepthPoint()
{
    if (m_depthPointPool.Count > 0)
    {
        var point = m_depthPointPool.Dequeue();
        point.SetActive(true);
        return point;
    }

    // Pool exhausted, instantiate new
    return Instantiate(m_depthPointPrefab, transform);
}

public void ClearDepthPoints()
{
    foreach (var point in m_activeDepthPoints)
    {
        point.SetActive(false);
        m_depthPointPool.Enqueue(point);
    }
    m_activeDepthPoints.Clear();
}
```

#### 3. DepthEstimationManager.cs

**Responsibility**: Scene management and spatial anchoring

**Key Code**:
```csharp
private void EraseSpatialAnchor()
{
    if (m_spatialAnchor != null)
    {
        m_spatialAnchor.EraseAnchorAsync();
        DestroyImmediate(m_spatialAnchor);
        m_spatialAnchor = null;
        m_depthVisualization.ClearDepthPoints();
    }
}

private void OnApplicationPause(bool pause)
{
    if (pause)
    {
        m_depthVisualization.ClearDepthPoints();
    }
}
```

## Configuration

### Server Configuration

**File**: `vision_server/seg_server/depth_config.py`

```python
MODEL_TYPE = "MiDaS_small"  # Small model for Quest 3 (faster)
TRANSFORM_TYPE = "small_transform"
DOWNSAMPLE_FACTOR = 4  # 4x downsample for bandwidth
```

### Unity Configuration

**Inspector Settings** (DepthInferenceRunManager):

```csharp
[Header("Server Inference - ROI Depth")]
[SerializeField] private InferenceConfig m_inferenceConfig = new InferenceConfig
{
    mode = InferenceMode.DepthEstimation,
    targetFPS = 5f,  // Lower FPS for depth (compute-heavy)
    jpegQuality = 80,
    includeDepth = true
};

[Header("ROI Settings")]
[SerializeField] private bool m_useROI = true;  // Enable ROI filtering
[SerializeField] private float m_minDetectionConfidence = 0.5f;
```

**Inspector Settings** (DepthVisualizationManager):

```csharp
[Header("Depth Visualization")]
[SerializeField] private GameObject m_depthPointPrefab;  // Cyan sphere
[SerializeField] private Camera m_passthroughCamera;
[SerializeField] private int m_maxPointsPerFrame = 500;  // Performance limit
[SerializeField] private int m_samplingRate = 2;  // Skip pixels (1=all, 2=half)

[Header("ROI Settings")]
[SerializeField] private bool m_useROI = true;
[SerializeField] private float m_roiPadding = 0.1f;  // 10% padding around bbox

[Header("Depth Range")]
[SerializeField] private float m_minDepth = 0.5f;  // meters
[SerializeField] private float m_maxDepth = 5.0f;  // meters

[Header("Visual Settings")]
[SerializeField] private float m_pointSize = 0.015f;  // Sphere scale
```

## Performance Metrics

### Typical Latency (Quest 3 → Local PC)

| Stage | Time (ms) | Percentage |
|-------|-----------|------------|
| Upload | 15-25 | 8% |
| Server Processing | 200-300 | 70% |
| - YOLO Detection | 50-80 | - |
| - MiDaS Depth | 150-220 | - |
| Download | 30-50 | 15% |
| Parse JSON | 5-10 | 2% |
| ROI Filtering | 2-5 | 1% |
| Rendering | 5-10 | 2% |
| **Total E2E** | **280-400** | **100%** |

### Bandwidth Usage

| Component | Size | Notes |
|-----------|------|-------|
| Upload (JPEG 80%) | 80-150 KB | 1280x960 camera frame |
| Download (JSON) | 50-120 KB | Compressed (gzip) |
| - Detections | 1-5 KB | ~1-10 objects |
| - Depth Map | 50-115 KB | 320x240 float array |
| **Total per Frame** | **130-270 KB** | **@ 5 FPS = 0.65-1.35 MB/s** |

### Rendering Performance

| Mode | Points Rendered | FPS Impact |
|------|-----------------|------------|
| Full Depth (no ROI) | 2000-5000 | High (20-30 FPS) |
| **ROI Depth (1 object)** | **200-500** | **Low (60-72 FPS)** |
| ROI Depth (3 objects) | 600-1500 | Medium (40-60 FPS) |

## Troubleshooting

### Issue: Blue background blocking passthrough

**Symptoms**: Solid blue screen instead of camera passthrough

**Cause**: Camera background color not set to black

**Fix**: In Unity scene, select Main Camera → Background → Color = (0, 0, 0, 0)

```yaml
# DepthEstimation.unity
Camera:
  m_BackGroundColor: {r: 0, g: 0, b: 0, a: 0}
```

### Issue: No depth points visible

**Symptoms**: Server returns depth, but Unity doesn't render anything

**Possible Causes**:

1. **Spatial anchor not tracked**
   - Check: `m_spatialAnchor.IsTracked`
   - Fix: Wait for anchor to track, or reset anchor

2. **All detections filtered by confidence**
   - Check: `validDetections.Count == 0`
   - Fix: Lower `m_minDetectionConfidence` (default 0.5 → 0.3)

3. **Depth values out of range**
   - Check: `depthValue < 0.01f || depthValue > 0.99f`
   - Fix: Adjust `m_minDepth` / `m_maxDepth` range

4. **Max points limit reached**
   - Check: `pointsRendered >= m_maxPointsPerFrame`
   - Fix: Increase `m_maxPointsPerFrame` (500 → 1000)

### Issue: Server JSON serialization error

**Symptoms**:
```
ValueError: [TypeError("'numpy.float32' object is not iterable")]
```

**Cause**: FastAPI can't serialize numpy types

**Fix**: Ensure all numpy types converted to Python native types in `roi_depth.py`:

```python
# WRONG:
"confidence": d["confidence"]  # numpy.float32

# CORRECT:
"confidence": float(d["confidence"])  # Python float

# WRONG:
"values": depth_map  # numpy.ndarray

# CORRECT:
"values": depth_map.astype(np.float32).tolist()  # Python list
```

### Issue: MiDaS model loading failure

**Symptoms**:
```
Compose.__call__() missing 1 required positional argument: 'img'
```

**Cause**: MiDaS transform API changed in newer versions

**Fix**: In `inference_depth.py`, store transform class (not instance):

```python
# WRONG:
DEPTH_TRANSFORM = getattr(midas_transforms, TRANSFORM_TYPE)()  # Don't instantiate

# CORRECT:
DEPTH_TRANSFORM = getattr(midas_transforms, TRANSFORM_TYPE)  # Store class

# Then in run_depth_estimation:
if callable(DEPTH_TRANSFORM):
    transform_instance = DEPTH_TRANSFORM()  # Instantiate here
    input_batch = transform_instance(img_np).to(DEVICE)
```

### Issue: Connection timeout to server

**Symptoms**: Unity can't connect to `http://192.168.0.135:8001`

**Checklist**:
1. Server running? Check terminal for FastAPI startup logs
2. Correct IP? Run `ipconfig` on server PC
3. Firewall blocking? Allow port 8001 in Windows Firewall
4. Same network? Quest 3 and PC on same WiFi
5. Test connection: `curl http://192.168.0.135:8001/` from PC

### Issue: Poor depth quality

**Symptoms**: Depth map looks noisy or incorrect

**Possible Causes**:

1. **Low lighting conditions**
   - MiDaS performs worse in dark environments
   - Fix: Increase room lighting

2. **Textureless surfaces**
   - Monocular depth struggles with blank walls
   - Fix: Add visual features to environment

3. **Fast camera motion**
   - 5 FPS creates motion blur with fast movement
   - Fix: Move camera slower during depth capture

4. **Downsampling too aggressive**
   - 4x downsample loses detail
   - Fix: Reduce to 2x (warning: increases bandwidth 4x)

## Code Examples

### Custom ROI Padding

Adjust ROI padding per detection class:

```csharp
// In DepthVisualizationManager.cs
private float GetROIPadding(string className)
{
    switch (className)
    {
        case "person":
            return 0.15f;  // More padding for people
        case "chair":
            return 0.05f;  // Less padding for furniture
        default:
            return 0.1f;
    }
}

// In DrawDepthMapROI:
float padding = GetROIPadding(detection.class_name);
int x1 = Mathf.Max(0, (int)((detection.bbox[0] - padding) * depth.width));
```

### Depth-Based Point Sizing

Make nearby points larger:

```csharp
// In DrawDepthMapROI, after getting worldPos:
float sizeMultiplier = Mathf.Lerp(2.0f, 0.5f, depthValue);  // Near=2x, Far=0.5x
point.transform.localScale = Vector3.one * m_pointSize * sizeMultiplier;
```

### Confidence-Based Coloring

Color points by detection confidence:

```csharp
// In DrawDepthMapROI:
Color baseColor = GetDepthColor(depthValue);
float alpha = Mathf.Lerp(0.5f, 1.0f, detection.confidence);  // Low conf = transparent
renderer.material.color = new Color(baseColor.r, baseColor.g, baseColor.b, alpha);
```

## Related Documentation

- [README.md](README.md) - Main documentation index
- [LATENCY_HUD.md](LATENCY_HUD.md) - Performance metrics HUD system
- Server Setup: `vision_server/README.md`
- Unity Samples: `Assets/PassthroughCameraApiSamples/README.md`

## References

- [MiDaS: Monocular Depth Estimation](https://github.com/isl-org/MiDaS)
- [YOLOv8 Documentation](https://docs.ultralytics.com/)
- [Meta Quest 3 Passthrough API](https://developer.oculus.com/documentation/unity/unity-passthrough/)
- [OVRSpatialAnchor Documentation](https://developer.oculus.com/documentation/unity/unity-spatial-anchors-overview/)
