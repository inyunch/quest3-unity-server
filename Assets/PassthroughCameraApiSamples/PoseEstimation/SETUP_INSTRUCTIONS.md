# PoseEstimation Scene Setup Instructions

## Overview
The PoseEstimation scene uses your Python server's pose estimation model to detect people and render their 17-keypoint COCO skeletons in AR on Quest 3.

## Scene Files Created
- `PassthroughPoseEstimation.unity` - Main scene (duplicated from MultiObjectDetection)
- `Scripts/PoseInferenceRunManager.cs` - Handles server communication
- `Scripts/PoseSkeletonUiManager.cs` - Renders skeleton joints and bones
- `Scripts/PoseEstimationManager.cs` - Manages spatial anchors

## Manual Setup Required in Unity Editor

### Step 1: Open the Scene
1. In Unity, navigate to `Assets/PassthroughCameraApiSamples/PoseEstimation/`
2. Double-click `PassthroughPoseEstimation.unity` to open it

### Step 2: Update Scene GameObjects

The scene currently has MultiObjectDetection components. You need to replace them:

#### A) Find "SentisInferenceManagerPrefab" GameObject
1. Select it in Hierarchy
2. **Remove Component**: `SentisInferenceRunManager`
3. **Add Component**: `PoseInferenceRunManager` (from PoseEstimation namespace)
4. **Remove Component**: `SentisInferenceUiManager`
5. **Add Component**: `PoseSkeletonUiManager` (from PoseEstimation namespace)

#### B) Configure PoseInferenceRunManager
In the Inspector for PoseInferenceRunManager:
- **Camera Access**: Drag the PassthroughCameraAccess component
- **Ui Menu Manager**: Drag the DetectionUiMenuManager component
- **Pose Manager**: Drag the PoseEstimationManager component (see step D)
- **Ui Pose**: Drag the PoseSkeletonUiManager component (same GameObject)
- **Server Url**: Should be `http://192.168.0.135:8000/infer_human?mode=both`
- **Min Keypoint Score**: 0.3 (default)

#### C) Configure PoseSkeletonUiManager
In the Inspector for PoseSkeletonUiManager:
- **Environment Raycast**: Drag the EnvironmentRayCastSampleManager from the scene
- **Camera Access**: Drag the PassthroughCameraAccess component
- **Joint Prefab**: Leave empty (will auto-create spheres)
- **Bone Prefab**: Leave empty (will auto-create LineRenderers)
- **Joint Size**: 0.03
- **Bone Width**: 0.015
- **Colors**:
  - Head Color: Yellow (RGB: 1, 1, 0)
  - Torso Color: Blue (RGB: 0, 0, 1)
  - Arms Color: Green (RGB: 0, 1, 0)
  - Legs Color: Red (RGB: 1, 0, 0)
  - Bone Color: White (RGB: 1, 1, 1)

#### D) Update DetectionManagerPrefab GameObject
1. Select "DetectionManagerPrefab" in Hierarchy
2. **Remove Component**: `DetectionManager`
3. **Add Component**: `PoseEstimationManager` (from PoseEstimation namespace)

#### E) Configure PoseEstimationManager
In the Inspector for PoseEstimationManager:
- **Camera Access**: Drag the PassthroughCameraAccess component
- **Ui Pose**: Drag the PoseSkeletonUiManager component

### Step 3: Add Scene to Build Settings
1. Go to `File → Build Settings`
2. Click **"Add Open Scenes"** button
3. The scene "PassthroughPoseEstimation" should now appear in the list
4. **Important**: Make sure it's listed AFTER StartScene but it can be in any order with other scenes

### Step 4: Verify StartScene Will Show the Button
The StartMenu.cs automatically discovers all scenes with "Passthrough" in the name.
Since this scene is named "PassthroughPoseEstimation", it will automatically appear
in the left pane as "PassthroughPoseEstimation" button.

### Step 5: Build and Deploy
1. Connect Quest 3 via USB
2. `File → Build Settings → Build`
3. Install the APK to Quest 3
4. Make sure your Python server is running:
   ```
   uvicorn app.main:app --host 0.0.0.0 --port 8000
   ```

### Step 6: Test on Quest 3
1. Launch the app on Quest 3
2. From StartScene, select "PassthroughPoseEstimation"
3. Point camera at a person
4. You should see:
   - 17 colored spheres (joints) at body keypoints
   - White lines (bones) connecting the joints
   - Skeleton overlay on passthrough camera

## Skeleton Rendering Details

### 17 COCO Keypoints (in order):
0. nose
1. left_eye
2. right_eye
3. left_ear
4. right_ear
5. left_shoulder
6. right_shoulder
7. left_elbow
8. right_elbow
9. left_wrist
10. right_wrist
11. left_hip
12. right_hip
13. left_knee
14. right_knee
15. left_ankle
16. right_ankle

### Color Coding:
- **Yellow** (0-4): Head (nose, eyes, ears)
- **Blue** (5-6, 11-12): Torso (shoulders, hips)
- **Green** (7-10): Arms (elbows, wrists)
- **Red** (13-16): Legs (knees, ankles)
- **White**: Bones (lines connecting joints)

### Bone Connections:
The skeleton follows standard COCO structure with 18 bone connections:
- Head: nose to eyes, eyes to ears
- Shoulders: left shoulder to right shoulder
- Arms: shoulders to elbows to wrists
- Torso: shoulders to hips, left hip to right hip
- Legs: hips to knees to ankles

## Troubleshooting

### No skeleton appears:
- Check server is running (`http://192.168.0.135:8000/health`)
- Check logs: `adb logcat -s Unity | findstr "POSE"`
- Verify "Use Server Inference" is enabled (if applicable)

### Skeleton jitters or jumps:
- This is normal - pose estimation can be jittery
- Ensure good lighting for better keypoint confidence

### Joints missing:
- Joints with confidence < 0.3 are hidden
- Check `Min Keypoint Score` setting (lower = more joints shown)

### Server not responding:
- Verify Quest 3 and server on same WiFi
- Check AndroidManifest.xml has INTERNET permission
- Check Player Settings: "Allow downloads over HTTP" = "Always allowed"

## Server Endpoint

The scene uses: `POST http://192.168.0.135:8000/infer_human?mode=both`

This returns both:
- **detections**: Person bounding boxes
- **skeleton**: 17 keypoints per detected person

If you need to change the server IP, update it in:
- PoseInferenceRunManager component → Server Url field

## Notes

- The scene reuses EnvironmentRaycastPrefab from MultiObjectDetection (no changes needed)
- PassthroughCameraAccess is unchanged (same camera pipeline)
- Spatial anchors ensure skeleton stays locked to world space
- Object pooling is used for performance (joints and bones are reused)

## Next Steps

After setup is complete, you can:
1. Adjust joint/bone sizes for visibility
2. Change colors to your preference
3. Modify `m_minKeypointScore` to filter low-confidence joints
4. Add additional features (e.g., person ID labels, tracking history)
