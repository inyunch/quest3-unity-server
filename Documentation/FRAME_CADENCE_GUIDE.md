# Frame Cadence (Inference Frequency) Guide - V3.0

This document explains how to control the **frequency of frame sending** (inference cadence) in the V3.0 UDP architecture.

---

## Quick Answer

**The frame sending frequency is controlled by the `targetFPS` setting in `InferenceConfig`.**

**File**: `Assets/PassthroughCameraApiSamples/Shared/Scripts/InferenceConfig.cs`
**Line**: 99

```csharp
[Header("FPS Configuration")]
[Tooltip("Target inference FPS for this mode. Lower FPS = less frequent inference, better performance.")]
[Range(1f, 30f)]
public float targetFPS = 10f;  // ← Controls frame frequency
```

**Formula**: `Interval (seconds) = 1 / targetFPS`

**Examples**:
- `targetFPS = 10` → Send frame every **100ms** (10 frames per second)
- `targetFPS = 5` → Send frame every **200ms** (5 frames per second)
- `targetFPS = 3` → Send frame every **333ms** (3 frames per second)
- `targetFPS = 1` → Send frame every **1000ms** (1 frame per second)

---

## How It Works (V3.0 Fixed Cadence System)

### 1. Configuration (Inspector)

**Location**: Select any inference manager GameObject in your scene

**Path**: Inspector → Inference Config → FPS Configuration → Target FPS

**Available in scenes**:
- Segmentation scene: `SegmentationInferenceManagerPrefab`
- PoseEstimation scene: `SentisInferenceManagerPrefab`
- MultiObjectDetection scene: `SentisInferenceManagerPrefab`

**Example Inspector Settings**:
```
Inference Config:
├─ Use Server Inference: ✓
├─ Use UDP Transport: ✓
├─ Mode: Both
├─ Target FPS: 10           ← Set this value
└─ JPEG Quality: 80
```

---

### 2. Interval Calculation (Code)

**File**: `InferenceConfig.cs`
**Method**: `GetInferenceInterval()`

```csharp
/// <summary>
/// Calculate the time interval between inference frames.
/// </summary>
/// <returns>Interval in seconds</returns>
public float GetInferenceInterval()
{
    if (targetFPS <= 0f)
    {
        Debug.LogWarning($"[InferenceConfig] Invalid targetFPS: {targetFPS}, defaulting to 10 FPS");
        return 0.1f;  // Fallback to 10 FPS
    }

    return 1f / targetFPS;  // ← Formula: 1 / FPS = interval in seconds
}
```

**Examples**:
```csharp
targetFPS = 10  → GetInferenceInterval() = 1 / 10 = 0.1 seconds = 100ms
targetFPS = 5   → GetInferenceInterval() = 1 / 5  = 0.2 seconds = 200ms
targetFPS = 3   → GetInferenceInterval() = 1 / 3  = 0.333 seconds = 333ms
targetFPS = 1   → GetInferenceInterval() = 1 / 1  = 1.0 seconds = 1000ms
```

---

### 3. Frame Sending Logic (Update Loop)

**File**: `SegmentationInferenceRunManager.cs` (same pattern in all inference managers)
**Lines**: 1004-1032

```csharp
private void Update()
{
    // V3.0: ALWAYS poll for UDP responses (even if camera not ready)
    if (m_useServerInference && m_useUDPTransport && m_transport != null)
    {
        while (m_transport.TryGetResponse(out FrameResponse response))
        {
            HandleV3Response(response);
        }
    }

    // PHASE 3: Fixed cadence inference triggering (UDP mode only)
    if (m_useServerInference && m_useUDPTransport && m_cameraReady)
    {
        // Check if paused
        if (m_uiMenuManager != null && m_uiMenuManager.IsPaused)
        {
            return;  // Don't send inference requests while paused
        }

        // Check if it's time for next inference
        float currentTime = Time.time;  // ← Unity's game time in seconds
        if (currentTime >= m_nextInferenceTime)  // ← Time check
        {
            // Calculate next inference time BEFORE starting inference (fixed cadence)
            float targetInterval = m_inferenceConfig.GetInferenceInterval();  // ← Get interval
            m_nextInferenceTime = currentTime + targetInterval;  // ← Schedule next frame

            // Start inference without blocking (fire and forget)
            StartCoroutine(RunInferenceNonBlocking());  // ← Send frame to server

            Debug.Log($"[V3 SEGMENTATION] Triggered inference at fixed cadence (interval={targetInterval * 1000f:F0}ms)");
        }

        // V3.0: Periodic telemetry cleanup
        if (Time.frameCount % 300 == 0)
        {
            m_telemetry.CleanupOldTraces();
        }
    }
}
```

---

### 4. Timeline Example

**Scenario**: `targetFPS = 10` (100ms interval)

```
Time (seconds)    Event
─────────────────────────────────────────────────────────────
0.000             Unity starts, m_nextInferenceTime = 0
0.000             Update() called: currentTime (0.000) >= m_nextInferenceTime (0)
                  ✓ Trigger inference
                  → m_nextInferenceTime = 0.000 + 0.1 = 0.100

0.016             Update() called: currentTime (0.016) < m_nextInferenceTime (0.100)
                  ✗ Skip (too soon)

0.033             Update() called: currentTime (0.033) < m_nextInferenceTime (0.100)
                  ✗ Skip

0.050             Update() called: currentTime (0.050) < m_nextInferenceTime (0.100)
                  ✗ Skip

... (more Update() calls, all skipped) ...

0.100             Update() called: currentTime (0.100) >= m_nextInferenceTime (0.100)
                  ✓ Trigger inference
                  → m_nextInferenceTime = 0.100 + 0.1 = 0.200

0.200             Update() called: currentTime (0.200) >= m_nextInferenceTime (0.200)
                  ✓ Trigger inference
                  → m_nextInferenceTime = 0.200 + 0.1 = 0.300

0.300             Update() called: currentTime (0.300) >= m_nextInferenceTime (0.300)
                  ✓ Trigger inference
                  → m_nextInferenceTime = 0.300 + 0.1 = 0.400
```

**Result**: Frames sent at **exactly 100ms intervals** (10 FPS), regardless of Unity's frame rate (60 FPS).

---

## Key Design Principles (V3.0)

### 1. **Fixed Cadence** (Not Frame-Based)

**V3.0 uses time-based cadence**, not frame-based:

❌ **OLD approach** (frame-based):
```csharp
// Send every N Unity frames (bad - FPS dependent)
if (Time.frameCount % 6 == 0)  // Every 6 frames at 60 FPS = ~10 Hz
{
    SendFrame();
}
```

✅ **V3.0 approach** (time-based):
```csharp
// Send at fixed time intervals (good - FPS independent)
if (Time.time >= m_nextInferenceTime)
{
    SendFrame();
    m_nextInferenceTime += GetInferenceInterval();
}
```

**Why time-based is better**:
- ✅ Consistent inference rate regardless of Unity FPS (60 FPS, 72 FPS, 90 FPS)
- ✅ Predictable server load
- ✅ Easier to reason about performance
- ✅ Works correctly if Unity FPS drops temporarily

---

### 2. **Non-Blocking (Fire and Forget)**

```csharp
// Start inference without blocking (fire and forget)
StartCoroutine(RunInferenceNonBlocking());
```

**What this means**:
- Unity's main thread does **NOT** wait for server response
- Frame is sent via UDP (instant, ~1ms)
- `Update()` continues immediately
- Response arrives later via background UDP listener
- Multiple frames can be "in flight" simultaneously

**Example with targetFPS = 10**:
```
t=0ms:    Send frame 1 (UDP instant)
t=100ms:  Send frame 2 (UDP instant)
t=150ms:  Receive response for frame 1 (via UDP listener)
t=200ms:  Send frame 3 (UDP instant)
t=250ms:  Receive response for frame 2 (via UDP listener)
t=300ms:  Send frame 4 (UDP instant)
t=350ms:  Receive response for frame 3 (via UDP listener)
```

**Benefits**:
- ✅ Unity never blocked (60 FPS maintained)
- ✅ Can send at 10 FPS even if server takes 250ms to respond
- ✅ Responses processed asynchronously when they arrive

---

### 3. **Independent of Camera Frame Rate**

Quest 3 camera may capture at **30 FPS or 60 FPS**, but inference can run at any rate:

```csharp
// Camera captures frames at 30 FPS (every 33ms)
// But we only send to server at 10 FPS (every 100ms)

Camera Frame 1 (t=0ms)    → Skip
Camera Frame 2 (t=33ms)   → Skip
Camera Frame 3 (t=66ms)   → Skip
Camera Frame 4 (t=100ms)  → ✓ Send to server (inference triggered)
Camera Frame 5 (t=133ms)  → Skip
Camera Frame 6 (t=166ms)  → Skip
Camera Frame 7 (t=200ms)  → ✓ Send to server (inference triggered)
```

**Why this matters**:
- ✅ Saves bandwidth (don't send every camera frame)
- ✅ Saves server compute (process fewer frames)
- ✅ Reduces Quest 3 battery usage
- ✅ Still get smooth AR experience (display latest result at 60 FPS)

---

## How to Configure targetFPS

### Option 1: Unity Inspector (Recommended)

**Steps**:
1. Open scene in Unity Editor (e.g., Segmentation scene)
2. Select inference manager GameObject in hierarchy
3. Inspector → Inference Config → Target FPS
4. Adjust slider (1-30 FPS)
5. Save scene (Ctrl+S)

**Visual**:
```
Inspector:
┌─────────────────────────────────────────┐
│ Segmentation Inference Run Manager     │
├─────────────────────────────────────────┤
│ Inference Config:                       │
│   ✓ Use Server Inference               │
│   ✓ Use UDP Transport                  │
│   Mode: Segmentation                    │
│   Target FPS: [====●====] 10           │ ← Drag slider
│   JPEG Quality: [======●==] 80          │
│   Downsample Factor: [●===] 1           │
└─────────────────────────────────────────┘
```

---

### Option 2: Code (Programmatic)

**Modify at runtime**:

```csharp
// In your inference manager script
public InferenceConfig m_inferenceConfig;

void Start()
{
    // Set targetFPS programmatically
    m_inferenceConfig.targetFPS = 5f;  // 5 FPS (200ms interval)

    Debug.Log($"Inference cadence set to {m_inferenceConfig.targetFPS} FPS " +
              $"({m_inferenceConfig.GetInferenceInterval() * 1000f:F0}ms interval)");
}
```

**Dynamic adjustment** (based on performance):

```csharp
void Update()
{
    // Reduce FPS if latency is high
    if (averageLatency > 500f)  // >500ms latency
    {
        m_inferenceConfig.targetFPS = Mathf.Max(3f, m_inferenceConfig.targetFPS - 1f);
        Debug.Log($"Latency high, reducing to {m_inferenceConfig.targetFPS} FPS");
    }

    // Increase FPS if latency is low
    else if (averageLatency < 200f)  // <200ms latency
    {
        m_inferenceConfig.targetFPS = Mathf.Min(10f, m_inferenceConfig.targetFPS + 1f);
        Debug.Log($"Latency good, increasing to {m_inferenceConfig.targetFPS} FPS");
    }
}
```

---

## Performance Recommendations

### targetFPS Selection Guide

| Mode | Recommended FPS | Interval | Reason |
|------|-----------------|----------|--------|
| **Multi-Object Detection** | 10-15 FPS | 66-100ms | Fast inference (~220ms), can handle higher rate |
| **Pose Estimation** | 5-8 FPS | 125-200ms | Medium inference (~290ms), moderate rate |
| **Both (Detection + Pose)** | 5 FPS | 200ms | Slower inference (~320ms), lower rate safer |
| **Segmentation** | 8-10 FPS | 100-125ms | Fast inference (~250ms), good balance |
| **Depth Estimation** | 3-5 FPS | 200-333ms | Slow inference (~350ms), large download |

**Rule of Thumb**:
```
targetFPS = 1 / (expected_latency_seconds × 2)

Example:
  Expected latency = 300ms = 0.3 seconds
  targetFPS = 1 / (0.3 × 2) = 1 / 0.6 = 1.67 FPS → Round to 2 FPS
```

**Why 2× latency?** Ensures new frame is sent **before** previous response arrives, maintaining pipeline.

---

### Performance vs Quality Tradeoff

**Higher targetFPS (e.g., 15 FPS)**:
- ✅ More responsive AR experience
- ✅ Smoother tracking
- ✅ Faster detection of new objects
- ❌ Higher server load
- ❌ More network bandwidth
- ❌ Higher Quest battery usage
- ❌ Risk of queue congestion if server can't keep up

**Lower targetFPS (e.g., 3 FPS)**:
- ✅ Lower server load
- ✅ Less network bandwidth
- ✅ Lower Quest battery usage
- ✅ No queue congestion
- ❌ Less responsive AR experience
- ❌ May miss fast-moving objects
- ❌ Stuttery tracking

**Recommended Starting Point**: **10 FPS** for most use cases

---

### Network Bandwidth Calculation

**Formula**:
```
Bandwidth (KB/s) = (JPEG size in KB) × targetFPS
```

**Example** (1280×720 image, JPEG quality 80, downsample 1×):
- JPEG size: ~20 KB
- targetFPS = 10
- **Upload bandwidth**: 20 KB × 10 = **200 KB/s** = **1.6 Mbps**

**WiFi capacity check**:
- Quest 3 WiFi 6: ~300 Mbps theoretical (actual: ~100-150 Mbps)
- 1.6 Mbps is only **1-2% of capacity** → Safe

**When to reduce targetFPS** (bandwidth issues):
- WiFi signal weak (< 50% strength)
- Other devices using WiFi heavily
- Large download responses (depth maps)

---

## Adaptive FPS (Advanced)

### Auto-Adjust Based on Latency

```csharp
// Add to inference manager script
private float m_averageLatency = 0f;
private int m_latencySampleCount = 0;
private const int MAX_SAMPLES = 10;

private void HandleV3Response(FrameResponse response)
{
    // Update latency average
    m_averageLatency = (m_averageLatency * m_latencySampleCount + response.latency_ms) / (m_latencySampleCount + 1);
    m_latencySampleCount = Mathf.Min(m_latencySampleCount + 1, MAX_SAMPLES);

    // Auto-adjust targetFPS every 10 frames
    if (response.frame_id % 10 == 0)
    {
        AdjustTargetFPS();
    }

    // ... rest of HandleV3Response
}

private void AdjustTargetFPS()
{
    // If latency too high, reduce FPS
    if (m_averageLatency > 400f)
    {
        m_inferenceConfig.targetFPS = Mathf.Max(3f, m_inferenceConfig.targetFPS - 1f);
        Debug.Log($"[ADAPTIVE FPS] Latency high ({m_averageLatency:F0}ms), reducing to {m_inferenceConfig.targetFPS} FPS");
    }
    // If latency good, increase FPS
    else if (m_averageLatency < 250f && m_inferenceConfig.targetFPS < 10f)
    {
        m_inferenceConfig.targetFPS = Mathf.Min(10f, m_inferenceConfig.targetFPS + 1f);
        Debug.Log($"[ADAPTIVE FPS] Latency good ({m_averageLatency:F0}ms), increasing to {m_inferenceConfig.targetFPS} FPS");
    }
}
```

---

### Auto-Adjust Based on Queue Wait

```csharp
private void HandleV3Response(FrameResponse response)
{
    // If queue wait is high, server is congested
    if (response.queue_wait_ms > 50f)
    {
        m_inferenceConfig.targetFPS = Mathf.Max(3f, m_inferenceConfig.targetFPS - 2f);
        Debug.LogWarning($"[ADAPTIVE FPS] Server congested (queue_wait={response.queue_wait_ms:F0}ms), " +
                        $"reducing to {m_inferenceConfig.targetFPS} FPS");
    }

    // ... rest of HandleV3Response
}
```

---

## Debugging Frame Cadence

### Verify Actual Send Rate

**Add logging to `Update()`**:

```csharp
private float m_lastSendTime = 0f;

private void Update()
{
    // ... existing code ...

    if (currentTime >= m_nextInferenceTime)
    {
        float actualInterval = currentTime - m_lastSendTime;
        float targetInterval = m_inferenceConfig.GetInferenceInterval();

        Debug.Log($"[CADENCE] Frame sent: " +
                  $"target={targetInterval * 1000f:F0}ms, " +
                  $"actual={actualInterval * 1000f:F0}ms, " +
                  $"drift={Mathf.Abs(actualInterval - targetInterval) * 1000f:F0}ms");

        m_lastSendTime = currentTime;
        m_nextInferenceTime = currentTime + targetInterval;

        StartCoroutine(RunInferenceNonBlocking());
    }
}
```

**Expected output** (targetFPS = 10):
```
[CADENCE] Frame sent: target=100ms, actual=100ms, drift=0ms
[CADENCE] Frame sent: target=100ms, actual=100ms, drift=0ms
[CADENCE] Frame sent: target=100ms, actual=100ms, drift=0ms
```

**If drift is high** (>10ms):
- Check if `Update()` is being called regularly
- Verify Unity FPS is stable (should be 60+ FPS)
- Check for frame drops (VR performance issues)

---

### Monitor Effective FPS

**Add to HUD display**:

```csharp
private int m_framesSentLast5Sec = 0;
private float m_last5SecTimestamp = 0f;

void Update()
{
    // Count frames sent
    if (currentTime >= m_nextInferenceTime)
    {
        m_framesSentLast5Sec++;
        // ... send frame ...
    }

    // Calculate actual FPS every 5 seconds
    if (currentTime - m_last5SecTimestamp >= 5f)
    {
        float actualFPS = m_framesSentLast5Sec / 5f;
        Debug.Log($"[CADENCE] Effective inference FPS: {actualFPS:F1} (target: {m_inferenceConfig.targetFPS})");

        m_framesSentLast5Sec = 0;
        m_last5SecTimestamp = currentTime;
    }
}
```

**Expected**: Actual FPS should match targetFPS within ±0.2 FPS

---

## Common Issues

### Issue 1: Frames Sent Too Frequently

**Symptom**: Logs show frames sent faster than expected

**Possible causes**:
- `targetFPS` set too high in Inspector
- `m_nextInferenceTime` not being updated correctly

**Fix**:
```csharp
// Verify targetFPS setting
Debug.Log($"Target FPS: {m_inferenceConfig.targetFPS}");
Debug.Log($"Interval: {m_inferenceConfig.GetInferenceInterval() * 1000f:F0}ms");

// Ensure m_nextInferenceTime is updated
if (currentTime >= m_nextInferenceTime)
{
    float targetInterval = m_inferenceConfig.GetInferenceInterval();
    m_nextInferenceTime = currentTime + targetInterval;  // ← Must have this!
    // ...
}
```

---

### Issue 2: Frames Sent Too Slowly

**Symptom**: Actual FPS much lower than targetFPS

**Possible causes**:
- Camera not ready (`m_cameraReady = false`)
- App paused (`m_uiMenuManager.IsPaused = true`)
- Unity FPS very low (< 30 FPS)

**Debug**:
```csharp
void Update()
{
    if (!m_cameraReady)
    {
        Debug.LogWarning("[CADENCE] Camera not ready, inference paused");
        return;
    }

    if (m_uiMenuManager != null && m_uiMenuManager.IsPaused)
    {
        Debug.LogWarning("[CADENCE] App paused, inference paused");
        return;
    }

    // ... rest of Update
}
```

---

### Issue 3: Inconsistent Cadence

**Symptom**: Frames sent at irregular intervals (sometimes 100ms, sometimes 150ms)

**Possible causes**:
- Unity FPS unstable (VR performance issues)
- `Time.time` drift (shouldn't happen in Unity)

**Fix**:
```csharp
// Use more stable timing
private float m_nextInferenceTime = 0f;

void Update()
{
    float currentTime = Time.time;  // Unity's game time (should be stable)

    if (currentTime >= m_nextInferenceTime)
    {
        // Use addition instead of recalculating from currentTime
        float targetInterval = m_inferenceConfig.GetInferenceInterval();
        m_nextInferenceTime += targetInterval;  // ← Accumulate (prevents drift)

        // Prevent runaway if behind schedule
        if (m_nextInferenceTime < currentTime - targetInterval)
        {
            m_nextInferenceTime = currentTime + targetInterval;
        }

        StartCoroutine(RunInferenceNonBlocking());
    }
}
```

---

## Summary

**How to control frame frequency**:
1. Set `targetFPS` in `InferenceConfig` (Inspector or code)
2. Interval is calculated as `1 / targetFPS` seconds
3. Unity's `Update()` checks if `Time.time >= m_nextInferenceTime`
4. When true, sends frame via UDP and schedules next send

**Key features**:
- ✅ Time-based (not frame-based) - works at any Unity FPS
- ✅ Non-blocking - UDP send is instant (~1ms)
- ✅ Independent of camera FPS - can send at lower rate than capture
- ✅ Configurable per scene - different modes can use different FPS

**Recommended values**:
- **Detection**: 10-15 FPS (fast inference)
- **Pose**: 5-8 FPS (medium inference)
- **Segmentation**: 8-10 FPS (fast inference)
- **Both/Depth**: 3-5 FPS (slow inference)

**Start with 10 FPS and adjust based on**:
- Server latency (reduce if >400ms)
- Network bandwidth (reduce if congested)
- Battery life (reduce for longer runtime)
- Responsiveness (increase for smoother AR)

---

**Last Updated**: 2026-04-21
**Version**: V3.0 Fixed Cadence Architecture
