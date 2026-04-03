# Coordinate Transformation Deep Dive

## Overview

This document provides detailed examples and visualizations for transforming 2D pose keypoints from the server into 3D positions in VR space.

---

## Coordinate Systems Summary

| System | Origin | Range | Units | Y-Axis Direction |
|--------|--------|-------|-------|------------------|
| **Image Space** | Top-Left | 0 to W×H | Pixels | Down ↓ |
| **Normalized** | Top-Left | 0.0 to 1.0 | Fraction | Down ↓ |
| **Viewport** | Bottom-Left | 0.0 to 1.0 | Fraction | Up ↑ |
| **World Space** | Scene Origin | -∞ to +∞ | Meters | Up ↑ |

---

## Example 1: Nose Keypoint Transformation

### Server Response
```json
{
  "skeleton": {
    "persons": [{
      "keypoints": [
        {
          "name": "nose",
          "x": 0.678,
          "y": 0.450,
          "score": 0.95
        }
      ]
    }]
  }
}
```

### Visualization

```
Server Image Space (1280×720 pixels)
┌─────────────────────────────────┐
│ (0,0)                           │  ← Y = 0 at TOP
│                                 │
│                                 │
│              * nose             │  ← (867, 324) pixels
│            (x=867, y=324)       │     = (0.678, 0.450) normalized
│                                 │
│                                 │
└─────────────────────────────────┘
                           (1280,720)

Unity Viewport Space (0-1 normalized)
┌─────────────────────────────────┐
│                                 │
│                                 │
│              * nose             │  ← (0.678, 0.550) viewport
│            (x=0.678, y=0.550)   │     = (0.678, 1.0-0.450)
│                                 │
│                                 │
│ (0,0)                           │  ← Y = 0 at BOTTOM
└─────────────────────────────────┘
                             (1,1)
```

### Code

```csharp
// Step 1: Receive from server (normalized, Y-down)
float serverX = 0.678f;
float serverY = 0.450f;

// Step 2: Convert to Unity viewport (Y-up)
Vector2 viewportPoint = new Vector2(
    serverX,           // 0.678 (unchanged)
    1.0f - serverY     // 1.0 - 0.450 = 0.550 (flipped!)
);

// Step 3: Get camera state
Pose cameraPose = m_cameraAccess.GetCameraPose();
// Example values:
// position = (0.1, 1.6, -0.2)  // Head position in world
// rotation = Quaternion(0, 0.1, 0, 0.995)  // Looking forward

// Step 4: Create ray
Ray ray = m_cameraAccess.ViewportPointToRay(viewportPoint, cameraPose);
// Result:
// ray.origin = (0.1, 1.6, -0.2)  // Same as camera position
// ray.direction = (0.356, -0.089, 0.930).normalized

// Step 5: Raycast to environment
Vector3? worldPos = m_environmentRaycast.Raycast(ray);
// Result: (1.2, 1.5, 2.3)  // Hit point on wall 2.4m away

// Step 6: Create 3D joint
GameObject joint = CreateJoint(worldPos.Value);
```

---

## Example 2: Full Body Skeleton

### Server Data
```json
{
  "keypoints": [
    {"name": "nose",           "x": 0.500, "y": 0.300, "score": 0.95},
    {"name": "left_shoulder",  "x": 0.450, "y": 0.400, "score": 0.92},
    {"name": "right_shoulder", "x": 0.550, "y": 0.400, "score": 0.93},
    {"name": "left_hip",       "x": 0.450, "y": 0.600, "score": 0.88},
    {"name": "right_hip",      "x": 0.550, "y": 0.600, "score": 0.89},
    {"name": "left_knee",      "x": 0.440, "y": 0.750, "score": 0.85},
    {"name": "right_knee",     "x": 0.560, "y": 0.750, "score": 0.86}
  ]
}
```

### Transformation Table

| Keypoint | Server (x,y) | Viewport (x,y) | Ray Direction | World Pos (m) |
|----------|-------------|----------------|---------------|---------------|
| nose | (0.500, 0.300) | (0.500, 0.700) | (0.000, 0.200, 0.980) | (0.0, 1.8, 2.5) |
| L shoulder | (0.450, 0.400) | (0.450, 0.600) | (-0.102, 0.045, 0.994) | (-0.3, 1.6, 2.6) |
| R shoulder | (0.550, 0.400) | (0.550, 0.600) | (0.102, 0.045, 0.994) | (0.3, 1.6, 2.6) |
| L hip | (0.450, 0.600) | (0.450, 0.400) | (-0.102, -0.155, 0.983) | (-0.3, 1.2, 2.7) |
| R hip | (0.550, 0.600) | (0.550, 0.400) | (0.102, -0.155, 0.983) | (0.3, 1.2, 2.7) |
| L knee | (0.440, 0.750) | (0.440, 0.250) | (-0.122, -0.355, 0.927) | (-0.4, 0.8, 2.9) |
| R knee | (0.560, 0.750) | (0.560, 0.250) | (0.122, -0.355, 0.927) | (0.4, 0.8, 2.9) |

### 3D Visualization

```
Side View (X-Z plane):

         nose (0.0, 1.8, 2.5)
           *
          / \
         /   \
  L.S  *     * R.S  (±0.3, 1.6, 2.6)
       |     |
       |     |
  L.H  *     * R.H  (±0.3, 1.2, 2.7)
       |     |
       |     |
  L.K  *     * R.K  (±0.4, 0.8, 2.9)

Camera at (0.1, 1.6, -0.2)
```

---

## Camera Intrinsics Explained

### What are Intrinsics?

Camera intrinsics describe the internal properties of the camera lens:

```
Camera Intrinsic Matrix (K):
┌                    ┐
│  fx   0   cx       │
│  0    fy  cy       │
│  0    0   1        │
└                    ┘

fx, fy = Focal lengths (pixels)
cx, cy = Principal point (pixels)
```

### Quest 3 Example Values

```csharp
Vector2 focalLength = m_cameraAccess.CurrentFocalLength;
// focalLength = (638.5, 638.5) pixels

Vector2 principalPoint = m_cameraAccess.CurrentPrincipalPoint;
// principalPoint = (640.0, 360.0) pixels (center of 1280×720)

Vector2 resolution = m_cameraAccess.CurrentResolution;
// resolution = (1280, 720) pixels
```

### How They Work

#### Field of View (FOV)

```
FOV = 2 * arctan(width / (2 * fx))

For Quest 3:
FOV_horizontal = 2 * arctan(1280 / (2 * 638.5))
               = 2 * arctan(1.002)
               = 2 * 45.0°
               = 90°
```

#### Viewport to Camera Space

```csharp
// Viewport point (0.678, 0.550)
float pixelX = 0.678 * 1280 = 867.8
float pixelY = 0.550 * 720 = 396.0

// Compute direction in camera space
float dirX = (pixelX - cx) / fx
           = (867.8 - 640.0) / 638.5
           = 0.357

float dirY = (pixelY - cy) / fy
           = (396.0 - 360.0) / 638.5
           = 0.056

float dirZ = 1.0  // Forward

// Normalize
Vector3 direction = normalize(0.357, 0.056, 1.0)
                 = (0.335, 0.053, 0.941)
```

---

## Raycasting Deep Dive

### What is Raycasting?

Raycasting shoots a virtual ray from the camera and finds where it hits the physical environment.

```
Camera Position: (0.0, 1.6, 0.0)
Ray Direction:   (0.335, 0.053, 0.941)

┌─────────────────────────────────┐
│                                 │
│  Camera (0,1.6,0)              │
│     *                          │
│      \                         │
│       \  Ray                   │
│        \                       │
│         \                      │
│          \                     │
│           *  Hit (1.2,1.5,2.3) │
│          /│                    │
│         / │  Wall              │
│        /  │                    │
└───────────────────────────────┘
```

### OVRPlugin.Raycast

```csharp
bool OVRPlugin.Raycast(
    Vector3 origin,      // Ray start point
    Vector3 direction,   // Ray direction (normalized)
    out RaycastResult result
)

// Result contains:
result.Point      // 3D position where ray hit
result.Normal     // Surface normal at hit point
result.Distance   // Distance from origin to hit
```

### Fallback Strategy

If raycast fails (no environment detected):

```csharp
Vector3? worldPos = m_environmentRaycast.Raycast(ray);

if (!worldPos.HasValue)
{
    // Fallback: Project to fixed distance
    float defaultDistance = 2.0f;  // 2 meters
    worldPos = ray.origin + ray.direction * defaultDistance;
}
```

---

## Common Coordinate System Errors

### Error 1: Forgetting Y-Flip

```csharp
// ❌ WRONG - No Y-flip
Vector2 viewportPoint = new Vector2(kp.x, kp.y);

// ✅ CORRECT - Flip Y
Vector2 viewportPoint = new Vector2(kp.x, 1.0f - kp.y);
```

**Result of error**: Skeleton appears upside-down

### Error 2: Using Pixel Coordinates in Viewport

```csharp
// ❌ WRONG - Pixel coordinates
Vector2 viewportPoint = new Vector2(867, 324);

// ✅ CORRECT - Normalized 0-1
Vector2 viewportPoint = new Vector2(0.678f, 0.550f);
```

**Result of error**: Ray points far outside camera view

### Error 3: Not Normalizing Ray Direction

```csharp
// ❌ WRONG - Direction not normalized
Vector3 direction = new Vector3(dirX, dirY, dirZ);

// ✅ CORRECT - Normalize direction
Vector3 direction = new Vector3(dirX, dirY, dirZ).normalized;
```

**Result of error**: Incorrect raycasting distances

---

## Debugging Coordinate Transforms

### Visual Debug Helpers

```csharp
// Draw ray in scene view
void DebugDrawRay(Ray ray, Color color, float duration = 1.0f)
{
    Debug.DrawRay(
        ray.origin,
        ray.direction * 5.0f,  // 5 meter length
        color,
        duration
    );
}

// Usage:
Ray ray = m_cameraAccess.ViewportPointToRay(viewportPoint, cameraPose);
DebugDrawRay(ray, Color.red, 2.0f);
```

### Log Coordinate Transformations

```csharp
void DebugLogTransform(Keypoint kp, Vector3? worldPos)
{
    Debug.Log($"[COORD] {kp.name}:");
    Debug.Log($"  Server: ({kp.x:F3}, {kp.y:F3})");

    Vector2 viewport = new Vector2(kp.x, 1.0f - kp.y);
    Debug.Log($"  Viewport: ({viewport.x:F3}, {viewport.y:F3})");

    if (worldPos.HasValue)
    {
        Debug.Log($"  World: {worldPos.Value}");

        float distance = Vector3.Distance(
            m_cameraAccess.GetCameraPose().position,
            worldPos.Value
        );
        Debug.Log($"  Distance from camera: {distance:F2}m");
    }
    else
    {
        Debug.LogWarning($"  World: RAYCAST FAILED");
    }
}
```

### Expected Output

```
[COORD] nose:
  Server: (0.678, 0.450)
  Viewport: (0.678, 0.550)
  World: (1.2, 1.5, 2.3)
  Distance from camera: 2.41m

[COORD] left_shoulder:
  Server: (0.450, 0.400)
  Viewport: (0.450, 0.600)
  World: (-0.3, 1.6, 2.6)
  Distance from camera: 2.64m
```

---

## Performance Considerations

### Raycasting Cost

```
Single raycast:      ~0.1-0.3ms
17 keypoints:        ~1.7-5.1ms
Per frame (30 FPS):  ~51-153ms overhead if sequential
```

### Optimization: Batch Raycasting

```csharp
// Instead of 17 individual raycasts:
foreach (var kp in keypoints)
{
    var ray = CreateRay(kp);
    var worldPos = Raycast(ray);  // 0.3ms each
}

// Batch raycast (if available):
Ray[] rays = keypoints.Select(kp => CreateRay(kp)).ToArray();
Vector3[] results = BatchRaycast(rays);  // ~1.5ms total
```

### Optimization: Spatial Coherence

```csharp
// Keypoints are usually close together
// Use previous frame's distance as hint

float estimatedDistance = previousFrame.noseDistance;

Vector3 worldPos = ray.origin + ray.direction * estimatedDistance;

// Only raycast if estimate seems wrong
if (Vector3.Distance(worldPos, previousFrame.nosePos) > 0.5f)
{
    worldPos = Raycast(ray).Value;
}
```

---

## Math Reference

### Quaternion Rotation

```csharp
// Rotate vector from camera space to world space
Vector3 worldDirection = cameraPose.rotation * cameraSpaceDirection;

// Example:
Quaternion rotation = Quaternion.Euler(0, 45, 0);  // 45° around Y
Vector3 forward = new Vector3(0, 0, 1);
Vector3 rotated = rotation * forward;
// rotated = (0.707, 0, 0.707)  // Now points 45° right
```

### Normalized to Pixel

```csharp
// Convert normalized (0-1) to pixel coordinates
int pixelX = (int)(normalizedX * imageWidth);
int pixelY = (int)(normalizedY * imageHeight);

// Convert pixel to normalized
float normalizedX = (float)pixelX / imageWidth;
float normalizedY = (float)pixelY / imageHeight;
```

### Field of View

```csharp
// Calculate FOV from focal length
float fovRadians = 2.0f * Mathf.Atan(resolution.x / (2.0f * focalLength.x));
float fovDegrees = fovRadians * Mathf.Rad2Deg;

// Quest 3:
// fovDegrees = 2.0 * Atan(1280 / (2 * 638.5)) * 57.3
//            = 2.0 * Atan(1.002) * 57.3
//            = 2.0 * 45.0
//            = 90.0°
```

---

## Testing & Validation

### Unit Test: Y-Flip

```csharp
[Test]
public void TestYFlip()
{
    // Server: y=0 at top
    float serverY = 0.0f;

    // Viewport: y=0 at bottom
    float viewportY = 1.0f - serverY;

    Assert.AreEqual(1.0f, viewportY);

    // Server: y=1 at bottom
    serverY = 1.0f;
    viewportY = 1.0f - serverY;

    Assert.AreEqual(0.0f, viewportY);
}
```

### Unit Test: Normalized to Pixel

```csharp
[Test]
public void TestNormalizedToPixel()
{
    float normalizedX = 0.678f;
    int width = 1280;

    int pixelX = (int)(normalizedX * width);

    Assert.AreEqual(867, pixelX);  // 0.678 * 1280 = 867.84
}
```

### Integration Test: Full Pipeline

```csharp
[Test]
public void TestFullPipeline()
{
    // Mock server response
    Keypoint nose = new Keypoint {
        x = 0.5f, y = 0.5f, score = 0.95f
    };

    // Mock camera at origin, looking forward
    Pose cameraPose = new Pose(
        Vector3.zero,
        Quaternion.identity
    );

    // Convert to viewport (should be centered)
    Vector2 viewport = new Vector2(nose.x, 1.0f - nose.y);
    Assert.AreEqual(0.5f, viewport.x);
    Assert.AreEqual(0.5f, viewport.y);

    // Create ray (should point straight ahead)
    Ray ray = ViewportPointToRay(viewport, cameraPose);
    Assert.AreEqual(Vector3.zero, ray.origin);
    Assert.AreEqual(Vector3.forward, ray.direction, 0.01f);
}
```

---

## FAQ

### Q: Why is Y-axis flipped between server and Unity?

**A**: Convention difference:
- **Computer Vision** (OpenCV, PIL): Origin at top-left, Y increases downward
- **Graphics/Games** (Unity, OpenGL): Origin at bottom-left, Y increases upward

### Q: Can I skip raycasting and use fixed distance?

**A**: Yes, for testing:
```csharp
Vector3 worldPos = ray.origin + ray.direction * 2.0f;  // 2 meters
```

But you lose:
- Correct depth (everything at same distance)
- Occlusion (skeleton doesn't go behind walls)
- Natural placement (skeleton floats)

### Q: What if Quest doesn't have environment mesh?

**A**:
1. Wait for user to look around (Quest builds mesh over time)
2. Use fixed distance fallback
3. Prompt user to enable scene understanding in settings

### Q: How accurate is the coordinate transformation?

**A**: Typical errors:
- JSON parsing precision: ±0.001 normalized units
- Viewport conversion: Exact (mathematical)
- Raycasting: ±2-5cm depending on environment
- **Total end-to-end**: ±3-8cm in world space

---

**Document Version**: 1.0
**Last Updated**: 2026-04-02
