# Pose Estimation & Object Detection - Technical Guide

## Table of Contents
1. [System Overview](#system-overview)
2. [Architecture](#architecture)
3. [Server-Side Pipeline](#server-side-pipeline)
4. [Unity Client Pipeline](#unity-client-pipeline)
5. [Coordinate Transformation System](#coordinate-transformation-system)
6. [Meta XR API Integration](#meta-xr-api-integration)
7. [Data Flow](#data-flow)
8. [Performance Optimization](#performance-optimization)
9. [Troubleshooting](#troubleshooting)

---

## System Overview

This system performs real-time human pose estimation and object detection on Meta Quest 3 using:
- **Server**: Python-based inference server (FastAPI + YOLO/Pose models)
- **Client**: Unity application with Meta XR SDK
- **Communication**: HTTP multipart/form-data for image upload + JSON response

### Key Features
- Real-time camera frame capture from Quest 3 passthrough camera
- Server-side inference for pose estimation (17 COCO keypoints)
- Object detection (80 COCO classes)
- 3D skeleton visualization in VR space using environment raycasting
- Spatial anchor persistence for stable skeleton placement

---

## Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│                        Meta Quest 3                              │
│  ┌────────────────────────────────────────────────────────────┐ │
│  │  Unity Application                                          │ │
│  │  ┌──────────────┐  ┌──────────────┐  ┌─────────────────┐  │ │
│  │  │ Passthrough  │→ │   Camera     │→ │  HTTP Client    │  │ │
│  │  │   Camera     │  │   Access     │  │  (UnityWeb      │  │ │
│  │  │   API        │  │              │  │   Request)      │  │ │
│  │  └──────────────┘  └──────────────┘  └────────┬────────┘  │ │
│  │                                                 │           │ │
│  │  ┌──────────────────────────────────────────────┼──────┐  │ │
│  │  │  Pose Skeleton UI Manager                    ▼      │  │ │
│  │  │  • Environment Raycasting    ◄────── JSON Parser    │  │ │
│  │  │  • 3D Joint Rendering                               │  │ │
│  │  │  • Bone LineRenderer                                │  │ │
│  │  │  • Spatial Anchor Tracking                          │  │ │
│  │  └─────────────────────────────────────────────────────┘  │ │
│  └────────────────────────────────────────────────────────────┘ │
└─────────────────────────┬───────────────────────────────────────┘
                          │ HTTP POST
                          │ (JPEG image)
                          ▼
┌─────────────────────────────────────────────────────────────────┐
│                    Python Inference Server                       │
│  ┌────────────────────────────────────────────────────────────┐ │
│  │  FastAPI Endpoint: POST /infer_human?mode=both             │ │
│  │  ┌──────────────┐  ┌──────────────┐  ┌─────────────────┐  │ │
│  │  │  Image       │→ │   YOLO       │→ │  Pose Model     │  │ │
│  │  │  Decode      │  │  Detection   │  │  (17 keypoints) │  │ │
│  │  └──────────────┘  └──────────────┘  └─────────────────┘  │ │
│  │                                                 │           │ │
│  │                                                 ▼           │ │
│  │  ┌──────────────────────────────────────────────────────┐  │ │
│  │  │  JSON Response:                                      │  │ │
│  │  │  {                                                   │  │ │
│  │  │    "detections": {...},                             │  │ │
│  │  │    "skeleton": {                                    │  │ │
│  │  │      "persons": [{                                  │  │ │
│  │  │        "keypoints": [{"name","x","y","score"}],    │  │ │
│  │  │        "bbox": [x1,y1,x2,y2]                       │  │ │
│  │  │      }]                                             │  │ │
│  │  │    }                                                │  │ │
│  │  │  }                                                  │  │ │
│  │  └──────────────────────────────────────────────────────┘  │ │
│  └────────────────────────────────────────────────────────────┘ │
└─────────────────────────────────────────────────────────────────┘
```

---

## Server-Side Pipeline

### Endpoint
```
POST http://<server-ip>:8000/infer_human?mode=both
Content-Type: multipart/form-data
```

### Input
- **Field**: `image`
- **Format**: JPEG (90% quality)
- **Resolution**: Original camera frame (e.g., 640×480, 1280×720)

### Processing Steps

1. **Image Decoding**
   ```python
   image = cv2.imdecode(np.frombuffer(await image.read(), np.uint8), cv2.IMREAD_COLOR)
   ```

2. **Object Detection (YOLO)**
   ```python
   detection_results = detector(image)
   detections = {
       "detections": [
           {
               "class_id": int,
               "class_name": str,
               "confidence": float,
               "bbox": [x1_norm, y1_norm, x2_norm, y2_norm],
               "bbox_pixels": [x1, y1, x2, y2]
           }
       ],
       "num_detections": int
   }
   ```

3. **Pose Estimation**
   ```python
   pose_results = pose_model(image)
   skeleton = {
       "persons": [
           {
               "keypoints": [
                   {
                       "name": "nose",      # COCO keypoint name
                       "x": 0.0-1.0,        # Normalized x coordinate
                       "y": 0.0-1.0,        # Normalized y coordinate
                       "score": 0.0-1.0     # Confidence score
                   },
                   # ... 17 keypoints total (COCO format)
               ],
               "bbox": [x1_norm, y1_norm, x2_norm, y2_norm]
           }
       ]
   }
   ```

### COCO 17 Keypoint Order
```
0:  nose
1:  left_eye
2:  right_eye
3:  left_ear
4:  right_ear
5:  left_shoulder
6:  right_shoulder
7:  left_elbow
8:  right_elbow
9:  left_wrist
10: right_wrist
11: left_hip
12: right_hip
13: left_knee
14: right_knee
15: left_ankle
16: right_ankle
```

### Response Format
```json
{
  "detections": {
    "detections": [...],
    "num_detections": 1
  },
  "skeleton": {
    "persons": [
      {
        "keypoints": [...],
        "bbox": [...]
      }
    ]
  },
  "segmentation": {
    "mask": [[...]]  // Optional - large 2D array
  },
  "model_input_width": 640,
  "model_input_height": 640,
  "input_image_width": 1280,
  "input_image_height": 720,
  "processing_time_ms": 45.2
}
```

### Performance Optimization Tips

**Server-Side:**
1. **Remove Segmentation for Pose Estimation**
   ```python
   # Add parameter to control segmentation inclusion
   @app.post("/infer_human")
   async def infer_human(
       image: UploadFile,
       mode: str = "both",
       include_segmentation: bool = False  # Set to False for pose
   ):
       response = {
           "detections": detections_data,
           "skeleton": skeleton_data,
       }

       if include_segmentation:
           response["segmentation"] = segmentation_data

       return response
   ```

2. **Field Ordering** - Put small fields first:
   ```python
   # Good: Small fields before large arrays
   response = {
       "detections": {...},      # ~500 bytes
       "skeleton": {...},        # ~2KB
       "segmentation": {...}     # ~150KB
   }
   ```

---

## Unity Client Pipeline

### Main Components

#### 1. PoseInferenceRunManager
**Purpose**: Orchestrates frame capture, server communication, and response handling

**Key Methods**:
```csharp
private IEnumerator RunInference()
{
    // 1. Get current camera frame
    Texture targetTexture = m_cameraAccess.GetTexture();
    Pose cameraPose = m_cameraAccess.GetCameraPose();

    // 2. Send to server
    yield return RunServerInference(targetTexture);

    // 3. Parse response
    // 4. Render skeleton
}
```

#### 2. PoseSkeletonUiManager
**Purpose**: Renders 3D skeleton in VR space

**Key Responsibilities**:
- Convert 2D keypoints to 3D world positions
- Create joint spheres and bone lines
- Manage object pooling for performance

#### 3. PoseEstimationManager
**Purpose**: Manages spatial anchor for stable skeleton placement

**Key Features**:
- Creates spatial anchor at skeleton location
- Persists anchor across sessions
- Handles tracking loss/recovery

### Frame Capture & Upload

```csharp
// 1. Capture frame
Texture2D tex2D = m_cameraAccess.GetTexture() as Texture2D;

// 2. Encode as JPEG
byte[] jpegBytes = tex2D.EncodeToJPG(90);

// 3. Create multipart form
List<IMultipartFormSection> formData = new List<IMultipartFormSection>();
formData.Add(new MultipartFormFileSection("image", jpegBytes, "frame.jpg", "image/jpeg"));

// 4. Send HTTP POST
UnityWebRequest request = UnityWebRequest.Post(m_serverUrl, formData);
yield return request.SendWebRequest();
```

### JSON Parsing Strategy

**Problem**: Full JSON response can be 150KB+ due to segmentation mask
**Solution**: Extract only needed fields using string parsing

```csharp
// Extract skeleton field without parsing entire JSON
string skeletonJson = ExtractJsonField(jsonResponse, "skeleton");
var skeleton = JsonConvert.DeserializeObject<SkeletonData>(skeletonJson);

// Extract detections field
string detectionsJson = ExtractJsonField(jsonResponse, "detections");
var detections = JsonConvert.DeserializeObject<DetectionResultData>(detectionsJson);
```

**ExtractJsonField Algorithm**:
1. Find field name in JSON string
2. Determine if value is object `{...}` or array `[...]`
3. Track nested bracket depth
4. Handle escaped characters and strings
5. Extract substring when depth returns to 0

### C# Data Classes

```csharp
[Serializable]
public class SkeletonData
{
    public List<PersonSkeleton> persons;  // Array of detected persons
}

[Serializable]
public class PersonSkeleton
{
    public List<Keypoint> keypoints;  // 17 COCO keypoints
    public float[] bbox;              // Bounding box [x1, y1, x2, y2]
}

[Serializable]
public class Keypoint
{
    public string name;   // "nose", "left_eye", etc.
    public float x;       // Normalized 0-1
    public float y;       // Normalized 0-1
    public float score;   // Confidence 0-1
}
```

---

## Coordinate Transformation System

### Overview
The system transforms 2D image coordinates to 3D world positions through multiple coordinate spaces:

```
Image Space → Normalized Space → Viewport Space → Ray Space → World Space
  (pixels)      (0-1)              (0-1)          (direction)   (meters)
```

### Step-by-Step Transformation

#### 1. Server: Image Space to Normalized Coordinates

**Input**: Keypoint pixel coordinates from pose model
```python
# Model output (pixels)
keypoint_x_pixels = 867
keypoint_y_pixels = 324

# Image dimensions
image_width = 1280
image_height = 720

# Normalize to 0-1 range
x_normalized = keypoint_x_pixels / image_width   # 0.678
y_normalized = keypoint_y_pixels / image_height  # 0.450
```

**Output**: JSON with normalized coordinates
```json
{
  "name": "nose",
  "x": 0.678,
  "y": 0.450,
  "score": 0.95
}
```

#### 2. Unity: Normalized to Viewport Coordinates

**Important**: Unity's viewport Y-axis is inverted compared to image coordinates

```csharp
// Server sends: y=0 at TOP of image
// Unity viewport: y=0 at BOTTOM of screen

Vector2 normalizedPos = new Vector2(
    kp.x,           // x: 0.678 (left to right)
    1.0f - kp.y     // y: 1.0 - 0.450 = 0.550 (bottom to top)
);
```

#### 3. Unity: Viewport to Ray

Use **Meta XR PassthroughCameraAccess API**:

```csharp
public Ray ViewportPointToRay(Vector2 viewportPoint, Pose cameraPose)
{
    // Get camera intrinsics
    Vector2 focalLength = CurrentFocalLength;      // (fx, fy) in pixels
    Vector2 principalPoint = CurrentPrincipalPoint; // (cx, cy) in pixels
    Vector2 resolution = CurrentResolution;         // (width, height)

    // Convert viewport (0-1) to pixel coordinates
    float pixelX = viewportPoint.x * resolution.x;
    float pixelY = viewportPoint.y * resolution.y;

    // Compute normalized direction in camera space
    Vector3 directionInCameraSpace = new Vector3(
        (pixelX - principalPoint.x) / focalLength.x,  // X direction
        (pixelY - principalPoint.y) / focalLength.y,  // Y direction
        1.0f                                          // Forward (Z)
    ).normalized;

    // Transform to world space
    Vector3 worldDirection = cameraPose.rotation * directionInCameraSpace;

    return new Ray(cameraPose.position, worldDirection);
}
```

**Camera Intrinsics Explained**:
- **Focal Length** (fx, fy): Controls field of view
  - Higher values = narrower FOV
  - Typically ~640 pixels for Quest 3

- **Principal Point** (cx, cy): Optical center of the image
  - Usually near center: (width/2, height/2)
  - Accounts for lens distortion offset

#### 4. Unity: Ray to World Position (Environment Raycasting)

```csharp
public Vector3? Raycast(Ray ray)
{
    // Use Meta's scene understanding to find environment intersection
    if (OVRPlugin.Raycast(
        ray.origin,
        ray.direction,
        out OVRPlugin.RaycastResult result))
    {
        return result.Point;  // World position where ray hits environment
    }

    // Fallback: Project to fixed distance
    return ray.origin + ray.direction * 2.0f;  // 2 meters ahead
}
```

### Complete Pipeline Example

```csharp
// INPUT: Keypoint from server
Keypoint nose = new Keypoint {
    name = "nose",
    x = 0.678f,      // Normalized image x
    y = 0.450f,      // Normalized image y
    score = 0.95f
};

// STEP 1: Get camera pose
Pose cameraPose = m_cameraAccess.GetCameraPose();
// cameraPose.position = (0.1, 1.6, -0.2) meters in world space
// cameraPose.rotation = Quaternion representing head orientation

// STEP 2: Convert to viewport (flip Y)
Vector2 viewportPoint = new Vector2(
    nose.x,           // 0.678
    1.0f - nose.y     // 0.550
);

// STEP 3: Create ray from camera through viewport point
Ray ray = m_cameraAccess.ViewportPointToRay(viewportPoint, cameraPose);
// ray.origin = (0.1, 1.6, -0.2)
// ray.direction = normalized direction vector

// STEP 4: Raycast to find world position
Vector3? worldPos = m_environmentRaycast.Raycast(ray);
// worldPos = (1.2, 1.5, 2.3) meters - actual 3D position in room

// STEP 5: Create visual joint at world position
GameObject joint = GameObject.CreatePrimitive(PrimitiveType.Sphere);
joint.transform.position = worldPos.Value;
joint.transform.localScale = Vector3.one * 0.03f;  // 3cm sphere
```

---

## Meta XR API Integration

### PassthroughCameraAccess

**Purpose**: Access Quest 3 passthrough camera frames

```csharp
public class PassthroughCameraAccess : MonoBehaviour
{
    // Check if camera is ready
    public bool IsPlaying { get; }

    // Get current frame as texture
    public Texture GetTexture();

    // Get camera pose (position + rotation)
    public Pose GetCameraPose();

    // Get camera intrinsics
    public Vector2 CurrentFocalLength { get; }
    public Vector2 CurrentPrincipalPoint { get; }
    public Vector2 CurrentResolution { get; }

    // Convert viewport point to world ray
    public Ray ViewportPointToRay(Vector2 viewportPoint, Pose cameraPose);
}
```

### OVRSpatialAnchor

**Purpose**: Persist skeleton position in physical space

```csharp
// Create anchor at skeleton location
m_spatialAnchor = gameObject.AddComponent<OVRSpatialAnchor>();

// Wait for localization
while (!m_spatialAnchor.Localized)
{
    yield return null;
}

// Save anchor for future sessions
var result = await m_spatialAnchor.SaveAnchorAsync();

// Later: Restore anchor tracking if lost
var unboundAnchors = new List<OVRSpatialAnchor.UnboundAnchor>();
await OVRSpatialAnchor.LoadUnboundAnchorsAsync(
    new[] { m_spatialAnchor.Uuid },
    unboundAnchors
);
```

**Benefits**:
- Skeleton stays at same physical location
- Survives tracking loss
- Persists across app restarts

### EnvironmentRaycastManager

**Purpose**: Find physical surfaces for skeleton placement

```csharp
public class EnvironmentRayCastSampleManager : MonoBehaviour
{
    public Vector3? Raycast(Ray ray)
    {
        // Use OVRPlugin scene understanding
        if (OVRPlugin.Raycast(
            ray.origin,
            ray.direction,
            out OVRPlugin.RaycastResult result))
        {
            return result.Point;
        }

        return null;
    }
}
```

---

## Data Flow

### Frame Processing Loop

```
1. Unity captures camera frame (60 FPS)
   ↓
2. Check if UI is paused
   ↓ (if not paused)
3. Encode frame as JPEG
   ↓
4. HTTP POST to server
   ↓
5. Server processes (40-60ms)
   ↓
6. JSON response (2-150KB)
   ↓
7. Extract skeleton field
   ↓
8. Parse JSON to C# objects
   ↓
9. For each keypoint:
   - Convert to viewport coords
   - Create ray through camera
   - Raycast to environment
   - Create 3D joint sphere
   ↓
10. Draw bones between joints
    ↓
11. Update spatial anchor
    ↓
12. Repeat from step 1
```

### Timing Analysis

| Stage | Typical Duration |
|-------|-----------------|
| Frame capture | <1ms |
| JPEG encoding | 5-10ms |
| Network upload | 10-30ms |
| Server inference | 40-60ms |
| Network download | 5-15ms |
| JSON parsing | 2-5ms |
| Raycasting (17 points) | 5-10ms |
| Rendering | 1-2ms |
| **Total** | **68-123ms** |
| **Effective FPS** | **8-15 FPS** |

### Optimization Opportunities

1. **Reduce Upload Size**
   - Lower JPEG quality: 90% → 70%
   - Reduce resolution: 1280×720 → 640×480
   - Expected savings: 50-70% bandwidth

2. **Reduce Download Size**
   - Disable segmentation mask
   - Response: 150KB → 2KB (75x smaller!)

3. **Batch Processing**
   - Skip frames: Process every 2nd or 3rd frame
   - Interpolate skeleton between updates

---

## Performance Optimization

### Server Recommendations

1. **GPU Acceleration**
   ```python
   # Use CUDA if available
   device = "cuda" if torch.cuda.is_available() else "cpu"
   model = model.to(device)
   ```

2. **Model Quantization**
   ```python
   # Use INT8 quantization for faster inference
   model = torch.quantization.quantize_dynamic(
       model, {torch.nn.Linear}, dtype=torch.qint8
   )
   ```

3. **Batch Processing** (if multiple clients)
   ```python
   # Process multiple images in single batch
   images_batch = torch.stack(images)
   results = model(images_batch)
   ```

### Unity Recommendations

1. **Object Pooling** (Already implemented)
   ```csharp
   // Reuse GameObjects instead of Destroy/Instantiate
   private List<GameObject> m_jointPool = new List<GameObject>();
   ```

2. **LOD for Distant Skeletons**
   ```csharp
   float distance = Vector3.Distance(camera.position, skeleton.position);
   if (distance > 5.0f)
   {
       // Use simpler rendering (fewer bones, smaller spheres)
       jointSize *= 0.5f;
       skipBones = true;
   }
   ```

3. **Async/Await for HTTP**
   ```csharp
   // Don't block main thread during network calls
   yield return request.SendWebRequest();  // ✓ Correct
   // NOT: request.SendWebRequest().Wait(); // ✗ Blocks
   ```

---

## Troubleshooting

### Common Issues

#### 1. "Skeleton not visible in VR"

**Symptoms**: Logs show successful parsing but no visual skeleton

**Diagnosis**:
```bash
adb logcat -s Unity | findstr /C:"POSE PARSE" /C:"POSE DRAW"
```

**Check**:
- [ ] `[POSE PARSE] persons count=1` (should be > 0)
- [ ] `[POSE DRAW] DrawSkeleton called, persons=1`
- [ ] `[POSE UI] FALLBACK: Creating primitive sphere`

**Possible Causes**:
1. **Raycast fails** → Skeleton placed at wrong position
   ```csharp
   // Solution: Add fallback distance
   Vector3 worldPos = raycastResult ?? (ray.origin + ray.direction * 2.0f);
   ```

2. **Joints too small** → Increase size
   ```csharp
   m_jointSize = 0.05f;  // Increase from 0.03f
   ```

3. **Material invisible** → Check shader
   ```csharp
   // Use unlit shader for VR
   Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
   ```

#### 2. "JSON parsing fails"

**Symptoms**:
```
Newtonsoft deserialization failed: Unexpected character
```

**Solution**: Response too large (segmentation mask)
```csharp
// Already implemented: Extract skeleton field only
string skeletonJson = ExtractJsonField(jsonResponse, "skeleton");
```

**Server fix**:
```python
# Don't send segmentation for pose estimation
if mode == "pose":
    response = {
        "detections": detections_data,
        "skeleton": skeleton_data
        # NO segmentation
    }
```

#### 3. "Spatial anchor not tracking"

**Symptoms**: Skeleton jumps around or disappears

**Check**:
```csharp
Debug.Log($"Anchor tracked: {m_spatialAnchor.IsTracked}");
Debug.Log($"Anchor localized: {m_spatialAnchor.Localized}");
```

**Solutions**:
1. Ensure good lighting conditions
2. Move around to help Quest build scene understanding
3. Look at feature-rich areas (textured surfaces, not blank walls)

#### 4. "Network timeout"

**Symptoms**: Request fails after 2 minutes

**Diagnosis**:
```csharp
Debug.Log($"Request result: {request.result}");
Debug.Log($"Response code: {request.responseCode}");
```

**Solutions**:
1. Check server is running: `curl http://192.168.0.135:8000/`
2. Check Quest is on same network
3. Increase timeout:
   ```csharp
   request.timeout = 10;  // 10 seconds
   ```

#### 5. "Low FPS / Stuttering"

**Check frame processing time**:
```csharp
float startTime = Time.realtimeSinceStartup;
yield return RunInference();
float duration = Time.realtimeSinceStartup - startTime;
Debug.Log($"Frame processing took {duration*1000:F1}ms");
```

**Solutions**:
1. Skip frames:
   ```csharp
   private int frameCounter = 0;
   if (frameCounter++ % 2 == 0)  // Every 2nd frame
       yield return RunInference();
   ```

2. Lower image resolution:
   ```csharp
   Texture2D downsized = ResizeTexture(original, 640, 480);
   ```

---

## Debugging Commands

### ADB Logcat Filters

```bash
# Monitor pose parsing
adb logcat -s Unity | findstr /C:"POSE PARSE" /C:"POSE DRAW"

# Monitor network
adb logcat -s Unity | findstr /C:"POSE SEND" /C:"POSE RECV"

# Monitor errors only
adb logcat -s Unity:E

# Full pose pipeline
adb logcat -s Unity | findstr /C:"POSE"
```

### Unity Console Logs

Enable all pose-related logs:
```csharp
[POSE MGR]    - PoseEstimationManager lifecycle
[POSE INF]    - PoseInferenceRunManager lifecycle
[POSE UI]     - PoseSkeletonUiManager rendering
[POSE REF]    - Component reference checks
[POSE SEND]   - HTTP request sent
[POSE RECV]   - HTTP response received
[POSE JSON]   - JSON extraction/parsing
[POSE PARSE]  - Parsed data validation
[POSE DRAW]   - Drawing operations
[SKELETON RAW] - Raw skeleton JSON
```

---

## Advanced Topics

### Custom Keypoint Filtering

```csharp
// Only render high-confidence keypoints
if (kp.score < m_minKeypointScore)
{
    continue;  // Skip low-confidence keypoints
}
```

### Skeleton Smoothing

```csharp
// Exponential moving average for smoother movement
Vector3 smoothedPos = Vector3.Lerp(
    previousPos,
    newPos,
    Time.deltaTime * smoothingSpeed
);
```

### Multi-Person Support

```csharp
// Render multiple skeletons
foreach (var person in response.skeleton.persons)
{
    Color personColor = GetColorForPerson(person.personId);
    DrawSkeleton(person, personColor);
}
```

---

## References

### Meta XR Documentation
- [Passthrough API](https://developer.oculus.com/documentation/unity/unity-passthrough/)
- [Spatial Anchors](https://developer.oculus.com/documentation/unity/unity-spatial-anchors-overview/)
- [Scene Understanding](https://developer.oculus.com/documentation/unity/unity-scene-overview/)

### COCO Dataset
- [Keypoint Format](https://cocodataset.org/#keypoints-2020)
- [17 Keypoint Specification](https://github.com/cocodataset/cocoapi/blob/master/PythonAPI/pycocotools/cocoeval.py)

### Unity Networking
- [UnityWebRequest](https://docs.unity3d.com/ScriptReference/Networking.UnityWebRequest.html)
- [Multipart Form Data](https://docs.unity3d.com/ScriptReference/Networking.MultipartFormFileSection.html)

---

## Appendix: Server Setup Example

### Python Server (FastAPI)

```python
from fastapi import FastAPI, UploadFile
import cv2
import numpy as np

app = FastAPI()

@app.post("/infer_human")
async def infer_human(
    image: UploadFile,
    mode: str = "both",
    include_segmentation: bool = False
):
    # Decode image
    contents = await image.read()
    nparr = np.frombuffer(contents, np.uint8)
    img = cv2.imdecode(nparr, cv2.IMREAD_COLOR)

    # Run detection
    detection_results = detector(img)

    # Run pose estimation
    pose_results = pose_model(img)

    # Build response
    response = {
        "detections": format_detections(detection_results),
        "skeleton": format_skeleton(pose_results),
        "input_image_width": img.shape[1],
        "input_image_height": img.shape[0]
    }

    if include_segmentation:
        response["segmentation"] = segment(img)

    return response

def format_skeleton(results):
    """Convert pose model output to JSON format"""
    persons = []

    for detection in results:
        keypoints = []
        for i, kp in enumerate(detection.keypoints):
            keypoints.append({
                "name": COCO_KEYPOINT_NAMES[i],
                "x": float(kp.x / img_width),
                "y": float(kp.y / img_height),
                "score": float(kp.confidence)
            })

        persons.append({
            "keypoints": keypoints,
            "bbox": detection.bbox.tolist()
        })

    return {"persons": persons}

# Run server
if __name__ == "__main__":
    import uvicorn
    uvicorn.run(app, host="0.0.0.0", port=8000)
```

---

**Document Version**: 1.0
**Last Updated**: 2026-04-02
**Status**: Production Ready
