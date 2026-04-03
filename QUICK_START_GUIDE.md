# Pose Estimation Quick Start Guide

## Prerequisites

### Hardware
- Meta Quest 3
- Development PC (Windows/Mac/Linux)
- USB-C cable (for ADB connection)
- WiFi network (Quest and PC on same network)

### Software
- Unity 2022.3+ with Android Build Support
- Meta XR SDK
- Python 3.8+ (for inference server)
- ADB (Android Debug Bridge)

---

## Part 1: Server Setup (5 minutes)

### Step 1: Install Dependencies

```bash
# Create virtual environment
python -m venv venv
source venv/bin/activate  # On Windows: venv\Scripts\activate

# Install packages
pip install fastapi uvicorn opencv-python torch torchvision ultralytics
```

### Step 2: Create Server Script

Create `server.py`:

```python
from fastapi import FastAPI, UploadFile
import cv2
import numpy as np
from ultralytics import YOLO

app = FastAPI()

# Load models
detector = YOLO('yolov8n.pt')  # Object detection
pose_model = YOLO('yolov8n-pose.pt')  # Pose estimation

COCO_KEYPOINT_NAMES = [
    "nose", "left_eye", "right_eye", "left_ear", "right_ear",
    "left_shoulder", "right_shoulder", "left_elbow", "right_elbow",
    "left_wrist", "right_wrist", "left_hip", "right_hip",
    "left_knee", "right_knee", "left_ankle", "right_ankle"
]

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

    h, w = img.shape[:2]

    # Run detection
    det_results = detector(img, verbose=False)
    detections = []
    for box in det_results[0].boxes:
        x1, y1, x2, y2 = box.xyxy[0].cpu().numpy()
        detections.append({
            "class_id": int(box.cls[0]),
            "class_name": detector.names[int(box.cls[0])],
            "confidence": float(box.conf[0]),
            "bbox": [x1/w, y1/h, x2/w, y2/h],
            "bbox_pixels": [int(x1), int(y1), int(x2), int(y2)]
        })

    # Run pose estimation
    pose_results = pose_model(img, verbose=False)
    persons = []

    for result in pose_results:
        if result.keypoints is None:
            continue

        for person_kps in result.keypoints:
            keypoints = []
            for i, kp in enumerate(person_kps.xy[0].cpu().numpy()):
                conf = float(person_kps.conf[0][i])
                keypoints.append({
                    "name": COCO_KEYPOINT_NAMES[i],
                    "x": float(kp[0] / w),
                    "y": float(kp[1] / h),
                    "score": conf
                })

            # Get bounding box from keypoints
            valid_kps = [kp for kp in person_kps.xy[0].cpu().numpy() if kp[0] > 0]
            if valid_kps:
                xs = [kp[0] for kp in valid_kps]
                ys = [kp[1] for kp in valid_kps]
                bbox = [min(xs)/w, min(ys)/h, max(xs)/w, max(ys)/h]
            else:
                bbox = [0, 0, 0, 0]

            persons.append({
                "keypoints": keypoints,
                "bbox": bbox
            })

    return {
        "detections": {
            "detections": detections,
            "num_detections": len(detections)
        },
        "skeleton": {
            "persons": persons
        },
        "input_image_width": w,
        "input_image_height": h
    }

@app.get("/")
async def health_check():
    return {"status": "ok", "message": "Pose estimation server running"}

if __name__ == "__main__":
    import uvicorn
    print("Starting server on http://0.0.0.0:8000")
    uvicorn.run(app, host="0.0.0.0", port=8000)
```

### Step 3: Run Server

```bash
python server.py
```

Expected output:
```
INFO:     Started server process
INFO:     Uvicorn running on http://0.0.0.0:8000
```

### Step 4: Test Server

```bash
# From another terminal
curl http://localhost:8000/

# Should return:
# {"status":"ok","message":"Pose estimation server running"}
```

---

## Part 2: Unity Setup (10 minutes)

### Step 1: Scene Setup

1. Open Unity project
2. Load scene: `Assets/PassthroughCameraApiSamples/PoseEstimation/PassthroughPoseEstimation.unity`

### Step 2: Configure Server URL

1. In Hierarchy, select: `SentisInferenceManagerPrefab`
2. In Inspector, find `PoseInferenceRunManager` component
3. Set **Server Url** to: `http://<YOUR-PC-IP>:8000/infer_human?mode=both`

**Find your PC IP:**
- Windows: `ipconfig` → look for IPv4 Address
- Mac/Linux: `ifconfig` → look for inet address
- Example: `http://192.168.1.100:8000/infer_human?mode=both`

### Step 3: Verify Component References

In `SentisInferenceManagerPrefab`, check **PoseInferenceRunManager**:
- ✓ Camera Access → PassthroughCameraAccessPrefab
- ✓ Ui Menu Manager → DetectionUiMenuPrefab
- ✓ Pose Manager → DetectionManagerPrefab
- ✓ Ui Pose → SentisInferenceManagerPrefab

In `SentisInferenceManagerPrefab`, check **PoseSkeletonUiManager**:
- ✓ Environment Raycast → EnvironmentRaycastPrefab
- ✓ Camera Access → PassthroughCameraAccessPrefab

In `DetectionManagerPrefab`, check **PoseEstimationManager**:
- ✓ Camera Access → PassthroughCameraAccessPrefab
- ✓ Ui Pose → SentisInferenceManagerPrefab

### Step 4: Build Settings

1. File → Build Settings
2. Verify **Platform**: Android
3. Click **Add Open Scenes** (if scene not in list)
4. Click **Build And Run**

---

## Part 3: Deployment & Testing (5 minutes)

### Step 1: Enable Developer Mode on Quest 3

1. Open Meta Quest app on phone
2. Go to **Menu** → **Devices** → Select your Quest 3
3. Go to **Developer Mode** → Enable
4. Put on headset, allow developer mode when prompted

### Step 2: Connect Quest to PC

```bash
# Check connection
adb devices

# Should show:
# List of devices attached
# 2G97C5ZH5100P2    device
```

### Step 3: Deploy App

In Unity:
1. Click **Build And Run**
2. Wait for build to complete (~2-3 minutes)
3. App will automatically launch on Quest

### Step 4: Verify Network Connection

On Quest, the app will test server connection at startup. Check logs:

```bash
adb logcat -s Unity | findstr /C:"SERVER TEST"
```

Expected:
```
[POSE SERVER TEST] Connecting to http://192.168.1.100:8000/
[POSE SERVER TEST] ✓ Connection OK! Response: {"status":"ok"...}
```

If connection fails:
- ✗ Check Quest and PC are on same WiFi
- ✗ Check firewall allows port 8000
- ✗ Verify IP address is correct

---

## Part 4: Using the App (2 minutes)

### Starting Pose Estimation

1. Put on Quest 3
2. Point camera at a person
3. Skeleton should appear after ~1 second

### Controls

- **Trigger**: Pause/Resume detection (TODO: Verify in code)
- **Menu**: Exit app

### What You Should See

- **Yellow spheres** at body joints (nose, shoulders, hips, knees, etc.)
- **Cyan lines** connecting joints to form skeleton
- Skeleton follows person's movement in real-time

### Troubleshooting Live

```bash
# Monitor all pose logs
adb logcat -s Unity | findstr /C:"POSE"

# Check parsing
adb logcat -s Unity | findstr /C:"POSE PARSE"

# Check rendering
adb logcat -s Unity | findstr /C:"POSE DRAW"

# Check errors only
adb logcat -s Unity:E
```

---

## Common Issues & Solutions

### Issue 1: "No skeleton visible"

**Symptoms**: App runs but no skeleton appears

**Diagnosis**:
```bash
adb logcat -s Unity | findstr /C:"POSE PARSE" /C:"POSE DRAW"
```

**Check**:
```
[POSE PARSE] persons count=1  ← Should be 1 when person detected
[POSE DRAW] DrawSkeleton called, persons=1  ← Rendering called
```

**Solutions**:
1. Ensure person is fully visible in camera view
2. Improve lighting (Quest needs good visibility)
3. Check server is processing frames:
   ```bash
   # On server terminal, should see requests
   INFO: 127.0.0.1:xxxxx - "POST /infer_human?mode=both HTTP/1.1" 200 OK
   ```

### Issue 2: "Connection timeout"

**Symptoms**:
```
[POSE SERVER] Inference failed: Timeout
```

**Solutions**:
1. Verify server is running:
   ```bash
   curl http://<SERVER-IP>:8000/
   ```

2. Check firewall:
   ```bash
   # Windows: Allow Python through firewall
   # Mac/Linux: Check iptables/ufw
   ```

3. Verify IP address:
   ```bash
   # In Unity, the URL should match your PC's actual IP
   # NOT localhost, NOT 127.0.0.1
   ```

### Issue 3: "Skeleton jumps around"

**Symptoms**: Skeleton position unstable

**Solutions**:
1. Move Quest around to build better environment mesh
2. Ensure room has good features (posters, furniture, not blank walls)
3. Check spatial anchor status:
   ```bash
   adb logcat -s Unity | findstr /C:"Anchor"
   ```

### Issue 4: "Low FPS / Stuttering"

**Symptoms**: Choppy visualization

**Solutions**:
1. Reduce JPEG quality in code (90% → 70%)
2. Lower camera resolution
3. Disable segmentation on server (already done in example)
4. Skip frames:
   ```csharp
   // In PoseInferenceRunManager
   if (frameCounter++ % 2 != 0) yield break;  // Process every 2nd frame
   ```

---

## Performance Tuning

### Server-Side

**GPU Acceleration**:
```python
# Add to server.py
import torch
device = 'cuda' if torch.cuda.is_available() else 'cpu'
detector = YOLO('yolov8n.pt').to(device)
pose_model = YOLO('yolov8n-pose.pt').to(device)
```

**Smaller Model** (faster, less accurate):
```python
# Nano model (fastest)
detector = YOLO('yolov8n.pt')
pose_model = YOLO('yolov8n-pose.pt')

# Small model (balanced)
detector = YOLO('yolov8s.pt')
pose_model = YOLO('yolov8s-pose.pt')

# Medium model (slower, more accurate)
detector = YOLO('yolov8m.pt')
pose_model = YOLO('yolov8m-pose.pt')
```

### Unity-Side

**Lower JPEG Quality**:
```csharp
// In PoseInferenceRunManager.cs
byte[] jpegBytes = tex2D.EncodeToJPG(70);  // Was 90
```

**Skip Frames**:
```csharp
private int frameSkip = 2;
private int frameCounter = 0;

if (frameCounter++ % frameSkip != 0)
{
    yield break;  // Skip this frame
}
```

**Reduce Resolution**:
```csharp
// Resize texture before encoding
Texture2D resized = ResizeTexture(original, 640, 480);  // Was 1280×720
```

---

## Next Steps

### Advanced Features

1. **Multi-Person Detection**
   - Server already returns multiple persons
   - Unity: Render different color per person

2. **Gesture Recognition**
   - Analyze keypoint angles
   - Detect poses (T-pose, waving, sitting, etc.)

3. **Interaction**
   - Raycast from wrist positions
   - Detect hand pointing at UI elements

4. **Recording**
   - Save skeleton data to JSON
   - Replay/analyze later

### Optimization

1. **Local Inference** (no server needed)
   - Use Unity Sentis for on-device inference
   - Trade-off: Lower accuracy but zero latency

2. **Edge Server**
   - Run server on Quest itself (Android)
   - Use Termux + Python

3. **Compression**
   - Use protobuf instead of JSON
   - Reduce response size 80%

---

## Appendix

### Useful ADB Commands

```bash
# Install APK manually
adb install -r YourApp.apk

# Uninstall
adb uninstall com.your.package

# View logs
adb logcat

# Filter Unity logs
adb logcat -s Unity

# Clear logcat buffer
adb logcat -c

# Take screenshot
adb shell screencap -p > screenshot.png

# Record video
adb shell screenrecord /sdcard/recording.mp4
# Ctrl+C to stop, then:
adb pull /sdcard/recording.mp4
```

### Server API Reference

**Endpoint**: `POST /infer_human`

**Parameters**:
- `mode`: Detection mode
  - `"detection"`: Objects only
  - `"pose"`: Skeleton only
  - `"both"`: Both (default)
- `include_segmentation`: Include mask (default: false)

**Example Request**:
```bash
curl -X POST http://localhost:8000/infer_human?mode=both \
  -F "image=@person.jpg"
```

**Response Structure**:
```json
{
  "detections": {
    "detections": [...],
    "num_detections": 1
  },
  "skeleton": {
    "persons": [
      {
        "keypoints": [
          {"name": "nose", "x": 0.5, "y": 0.3, "score": 0.95},
          ...
        ],
        "bbox": [0.2, 0.1, 0.8, 0.9]
      }
    ]
  },
  "input_image_width": 1280,
  "input_image_height": 720
}
```

---

## Resources

### Documentation
- [Technical Guide](POSE_ESTIMATION_TECHNICAL_GUIDE.md)
- [Coordinate Transformation Guide](COORDINATE_TRANSFORMATION_GUIDE.md)

### Meta XR SDK
- [Getting Started](https://developer.oculus.com/documentation/unity/unity-gs-overview/)
- [Passthrough API](https://developer.oculus.com/documentation/unity/unity-passthrough/)
- [Spatial Anchors](https://developer.oculus.com/documentation/unity/unity-spatial-anchors-overview/)

### YOLO
- [Ultralytics YOLOv8](https://github.com/ultralytics/ultralytics)
- [Pose Estimation](https://docs.ultralytics.com/tasks/pose/)

### Unity Networking
- [UnityWebRequest](https://docs.unity3d.com/ScriptReference/Networking.UnityWebRequest.html)

---

**Last Updated**: 2026-04-02
**Estimated Setup Time**: 20-25 minutes
