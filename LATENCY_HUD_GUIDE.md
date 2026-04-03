# Latency HUD Guide

## Overview

The Latency HUD (Head-Up Display) is a real-time performance monitoring system that displays inference latency and performance metrics in VR environments. The HUD follows the user's viewpoint and is fixed at the bottom center of the field of view.

## Features

- ✅ **Real-time Latency Monitoring**: Displays end-to-end (E2E) latency with breakdown by stage
- ✅ **FPS Counter**: 30-frame rolling average frame rate
- ✅ **Data Traffic Monitoring**: Upload/download data size tracking
- ✅ **Detection Statistics**: Object detection count and average confidence score
- ✅ **View Following**: HUD moves with head rotation, always visible at bottom of view

## Display Content

When the application is running, the HUD displays the following information:

```
FPS: 72.5
E2E: 245ms
 ├Upload: 45ms (18%)
 ├Server: 150ms (61%)
 ├Download: 35ms (14%)
 └Parse: 15ms (6%)
Objects: 3
Avg Conf: 0.87
Upload: 125.3KB
Download: 48.7KB
```

### Metrics Explanation

| Metric | Description | Unit |
|--------|-------------|------|
| **FPS** | Frame rate (30-frame rolling average) | frames/sec |
| **E2E** | End-to-end total latency (from request start to processing complete) | milliseconds |
| **Upload** | Time to upload image to server | ms (percentage) |
| **Server** | Server inference processing time | ms (percentage) |
| **Download** | Time to download response | ms (percentage) |
| **Parse** | JSON parsing time | ms (percentage) |
| **Objects** | Number of objects detected in current frame | count |
| **Avg Conf** | Average confidence score of detections | 0.0-1.0 |
| **Upload** | Upload data size | Bytes/KB/MB |
| **Download** | Download data size | Bytes/KB/MB |

## Component Architecture

### 1. InferenceHUD.cs

**Path**: `Assets/PassthroughCameraApiSamples/MultiObjectDetection/SentisInference/Scripts/InferenceHUD.cs`

**Purpose**: Manages HUD display logic and data updates

#### Serialized Fields

```csharp
[Header("UI References")]
[SerializeField] private TextMeshProUGUI m_metricsText;

[Header("FPS Settings")]
[SerializeField] private int m_fpsAverageSamples = 30;
```

| Field | Type | Description |
|-------|------|-------------|
| `m_metricsText` | TextMeshProUGUI | Text UI element for displaying metrics |
| `m_fpsAverageSamples` | int | Number of frames to sample for FPS calculation (default: 30) |

#### Private Variables

```csharp
private Queue<float> m_fpsHistory;      // FPS history (rolling window)
private float m_lastUpdateTime;          // Last update time

// Latest metrics data
private float m_e2eMs = 0f;              // End-to-end latency
private float m_uploadMs = 0f;           // Upload time
private float m_serverProcMs = 0f;       // Server processing time
private float m_downloadMs = 0f;         // Download time
private float m_parseMs = 0f;            // Parse time
private int m_uploadBytes = 0;           // Upload bytes
private int m_downloadBytes = 0;         // Download bytes
private int m_detectionCount = 0;        // Detection count
private float m_avgConfidence = 0f;      // Average confidence
```

#### Public Methods

```csharp
public void UpdateHUD(
    float e2eMs,
    float uploadMs,
    float serverProcMs,
    float downloadMs,
    float parseMs,
    int uploadBytes,
    int downloadBytes,
    int detectionCount,
    float avgConfidence)
```

**Description**: Updates all metrics data displayed in the HUD

**Parameters**:
- `e2eMs`: End-to-end latency (milliseconds)
- `uploadMs`: Upload time (milliseconds)
- `serverProcMs`: Server processing time (milliseconds)
- `downloadMs`: Download time (milliseconds)
- `parseMs`: JSON parsing time (milliseconds)
- `uploadBytes`: Upload data size (bytes)
- `downloadBytes`: Download data size (bytes)
- `detectionCount`: Number of detected objects
- `avgConfidence`: Average confidence score (0.0-1.0)

#### Core Logic

**FPS Calculation** (`Update()` method):
```csharp
float deltaTime = Time.time - m_lastUpdateTime;
if (deltaTime > 0f)
{
    float currentFPS = 1f / deltaTime;
    m_fpsHistory.Enqueue(currentFPS);

    // Keep only last N samples
    while (m_fpsHistory.Count > m_fpsAverageSamples)
    {
        m_fpsHistory.Dequeue();
    }
}

// Calculate average FPS
float avgFPS = m_fpsHistory.Count > 0 ? m_fpsHistory.Average() : 0f;
```

**Percentage Calculation** (`UpdateDisplay()` method):
```csharp
float uploadPct = m_e2eMs > 0 ? (m_uploadMs / m_e2eMs) * 100f : 0f;
float serverPct = m_e2eMs > 0 ? (m_serverProcMs / m_e2eMs) * 100f : 0f;
float downloadPct = m_e2eMs > 0 ? (m_downloadMs / m_e2eMs) * 100f : 0f;
float parsePct = m_e2eMs > 0 ? (m_parseMs / m_e2eMs) * 100f : 0f;
```

**Data Size Formatting** (`FormatBytes()` method):
```csharp
private string FormatBytes(int bytes)
{
    if (bytes < 1024)
        return $"{bytes}B";
    if (bytes < 1024 * 1024)
        return $"{bytes / 1024f:F1}KB";
    return $"{bytes / (1024f * 1024f):F2}MB";
}
```

---

### 2. SentisInferenceRunManager.cs Modifications

**Path**: `Assets/PassthroughCameraApiSamples/MultiObjectDetection/SentisInference/Scripts/SentisInferenceRunManager.cs`

#### New Field (Line 31)

```csharp
[Header("UI display references")]
[SerializeField] private SentisInferenceUiManager m_uiInference;
[SerializeField] private InferenceHUD m_inferenceHUD;  // Added this line
```

| Field | Type | Description |
|-------|------|-------------|
| `m_inferenceHUD` | InferenceHUD | Reference to HUD component |

#### Update Call (Lines 532-545)

Called after server inference completes:

```csharp
// Update real-time HUD overlay
if (m_inferenceHUD != null)
{
    m_inferenceHUD.UpdateHUD(
        e2eMs,
        uploadMs,
        serverProcMs,
        downloadMs,
        parseMs,
        uploadBytes,
        downloadBytes,
        detectionCount,
        avgConfidence
    );
}
```

---

## Scene Setup

### GameObject Hierarchy

```
[BuildingBlock] Camera Rig
├── TrackingSpace
│   ├── CenterEyeAnchor (MainCamera)
│   │   ├── LatencyHUD_Canvas  ← HUD Canvas
│   │   │   └── LatencyText     ← TextMeshPro UI
│   │   └── (other children...)
│   ├── LeftEyeAnchor
│   └── RightEyeAnchor
```

### LatencyHUD_Canvas Setup

**Location**: Attached to `CenterEyeAnchor` (center eye anchor)

#### Transform Properties

| Property | Value | Description |
|----------|-------|-------------|
| **Position** | (0, -0.15, 0.4) | Local space position |
| - X | 0 | Horizontally centered |
| - Y | -0.15 | Slightly below (bottom of view) |
| - Z | 0.4 | 40cm from eyes |
| **Rotation** | (0, 0, 0) | No rotation |
| **Scale** | (0.0005, 0.0005, 0.0005) | Appropriate size for VR view |

#### Canvas Component Settings

| Property | Value | Description |
|----------|-------|-------------|
| **Render Mode** | World Space | Canvas in 3D space |
| **Event Camera** | (auto-assigned) | Camera for UI interaction |

#### InferenceHUD Component Settings

| Property | Value | Description |
|----------|-------|-------------|
| **m_metricsText** | LatencyText (TextMeshProUGUI) | Text UI element reference |
| **m_fpsAverageSamples** | 30 | FPS averaging frame count |

### LatencyText Setup

#### RectTransform Properties

| Property | Value | Description |
|----------|-------|-------------|
| **Anchor Min** | (0.5, 1) | Top center anchor |
| **Anchor Max** | (0.5, 1) | Top center anchor |
| **Pivot** | (0.5, 1) | Expands from top center |
| **Anchored Position** | (0, 0) | Position relative to anchor |
| **Size Delta** | (800, 500) | Text area size |

#### TextMeshProUGUI Component Settings

| Property | Value | Description |
|----------|-------|-------------|
| **Text** | "Loading..." | Initial display text |
| **Font Size** | 28 | Font size suitable for VR reading |
| **Alignment** | Top Center (513) | Top center alignment |
| **Color** | (1, 1, 1, 1) | White color |
| **Enable Auto Sizing** | false | Fixed font size |
| **Wrapping** | Enabled | Auto line wrapping |

### SentisInferenceManagerPrefab References

In the `SentisInferenceRunManager` component of `SentisInferenceManagerPrefab` GameObject:

| Field | Reference Target | Instance ID |
|-------|-----------------|-------------|
| `m_inferenceHUD` | LatencyHUD_Canvas (InferenceHUD) | Scene-dependent |

---

## Usage

### Adding Latency HUD to a New Scene

#### 1. Create Canvas

```csharp
// In Unity Editor
GameObject canvas = new GameObject("LatencyHUD_Canvas");
Canvas c = canvas.AddComponent<Canvas>();
c.renderMode = RenderMode.WorldSpace;
canvas.AddComponent<CanvasScaler>();
canvas.AddComponent<GraphicRaycaster>();

// Set parent to camera anchor
canvas.transform.SetParent(Camera.main.transform);
canvas.transform.localPosition = new Vector3(0, -0.15f, 0.4f);
canvas.transform.localRotation = Quaternion.identity;
canvas.transform.localScale = new Vector3(0.0005f, 0.0005f, 0.0005f);
```

#### 2. Create Text Element

```csharp
GameObject textObj = new GameObject("LatencyText");
textObj.transform.SetParent(canvas.transform);

RectTransform rect = textObj.AddComponent<RectTransform>();
rect.anchorMin = new Vector2(0.5f, 1f);
rect.anchorMax = new Vector2(0.5f, 1f);
rect.pivot = new Vector2(0.5f, 1f);
rect.anchoredPosition = Vector2.zero;
rect.sizeDelta = new Vector2(800, 500);

TextMeshProUGUI text = textObj.AddComponent<TextMeshProUGUI>();
text.text = "Loading...";
text.fontSize = 28;
text.alignment = TextAlignmentOptions.Top;
text.color = Color.white;
```

#### 3. Add InferenceHUD Component

```csharp
InferenceHUD hud = canvas.AddComponent<InferenceHUD>();
// Set m_metricsText reference to text component in Inspector
```

#### 4. Connect to InferenceRunManager

In the `SentisInferenceRunManager` component Inspector:
- Drag and connect the `m_inferenceHUD` field to the `LatencyHUD_Canvas` GameObject

---

## Performance Considerations

### Update Frequency

- **FPS Calculation**: Updated every frame (`Update()`)
- **Latency Metrics**: Updated after each inference completes (~1-30 times per second, depending on inference speed)
- **UI Redraw**: Updated every frame (TextMeshPro handles automatically)

### Memory Usage

- **FPS History**: ~120 bytes (30 floats)
- **TextMeshPro Mesh**: ~2-4 KB (depending on character count)
- **Total**: < 10 KB

### Optimization Tips

1. **Lower FPS Sampling**: If less memory is needed
   ```csharp
   [SerializeField] private int m_fpsAverageSamples = 15; // Reduced from 30 to 15
   ```

2. **Reduce Update Frequency**: If HUD updates too frequently
   ```csharp
   private float m_updateInterval = 0.1f; // Update every 0.1 seconds
   private float m_lastDisplayUpdate = 0f;

   void Update()
   {
       if (Time.time - m_lastDisplayUpdate < m_updateInterval)
           return;

       m_lastDisplayUpdate = Time.time;
       UpdateDisplay();
   }
   ```

3. **Conditional Display**: Show/hide HUD on demand
   ```csharp
   public void ToggleHUD(bool visible)
   {
       m_metricsText.gameObject.SetActive(visible);
   }
   ```

---

## Troubleshooting

### HUD Not Displaying

**Checklist**:
1. ✓ `m_metricsText` reference is set
2. ✓ TextMeshPro font asset is assigned
3. ✓ Canvas is in front of Camera (Z > 0)
4. ✓ Canvas layer is correct (not occluded by other objects)
5. ✓ `SentisInferenceRunManager`'s `m_inferenceHUD` reference is set

**Diagnostic Logs**:
```bash
adb logcat -s Unity | findstr /C:"[HUD]"
```

Expected output:
```
[HUD] InferenceHUD started
[HUD] UpdateHUD called: e2e=245.0ms
[HUD] UpdateHUD stored: e2e=245.0ms, upload=45.0ms...
```

### Latency Data Shows 0

**Possible Causes**:
1. Server inference not enabled (`m_useServerInference = false`)
2. Server connection failed
3. `UpdateHUD()` not being called

**Check**:
```bash
adb logcat -s Unity | findstr /C:"[LATENCY]"
```

Expected output:
```
[LATENCY] e2eMs=245.1ms
[LATENCY] uploadMs=45.2ms
[LATENCY] serverProcMs=150.0ms
```

### FPS Display Incorrect

**Possible Causes**:
1. Sample buffer not filled (during startup)
2. `Update()` not being called
3. Frame rate calculation logic error

**Solution**:
```csharp
// Check if Update is executing
void Update()
{
    Debug.Log("[HUD] Update called");
    // ...
}
```

### HUD Position Not Following View

**Reason**: Canvas not properly set as camera child

**Solution**:
```csharp
// Ensure Canvas is child of CenterEyeAnchor
canvas.transform.SetParent(centerEyeAnchor.transform, false);
```

---

## Extended Features

### Add Latency History Graph

```csharp
public class LatencyGraph : MonoBehaviour
{
    private Queue<float> m_latencyHistory = new Queue<float>();
    private const int MAX_SAMPLES = 100;

    public void AddSample(float latencyMs)
    {
        m_latencyHistory.Enqueue(latencyMs);
        if (m_latencyHistory.Count > MAX_SAMPLES)
            m_latencyHistory.Dequeue();

        // Draw graph (using LineRenderer or UI Image)
        DrawGraph();
    }
}
```

### Add Color-Coded Warnings

```csharp
void UpdateDisplay()
{
    // Set color based on latency
    if (m_e2eMs > 500f)
        m_metricsText.color = Color.red;      // High latency
    else if (m_e2eMs > 300f)
        m_metricsText.color = Color.yellow;   // Medium latency
    else
        m_metricsText.color = Color.green;    // Low latency
}
```

### Add Statistical Summary

```csharp
public class LatencyStats
{
    public float MinLatency { get; private set; } = float.MaxValue;
    public float MaxLatency { get; private set; } = float.MinValue;
    public float AvgLatency { get; private set; }

    private List<float> m_samples = new List<float>();

    public void AddSample(float latency)
    {
        m_samples.Add(latency);
        MinLatency = Mathf.Min(MinLatency, latency);
        MaxLatency = Mathf.Max(MaxLatency, latency);
        AvgLatency = m_samples.Average();
    }

    public string GetSummary()
    {
        return $"Min: {MinLatency:F0}ms | Max: {MaxLatency:F0}ms | Avg: {AvgLatency:F0}ms";
    }
}
```

---

## HTTP Headers & Server Logging

### Overview

Unity sends performance metrics to the server via HTTP headers for logging and analysis. These headers correspond to the metrics displayed in the HUD and can be used by the server to log data to Excel/CSV files for offline analysis.

### HTTP Header Reference

The following headers are sent with each inference request (see `SentisInferenceRunManager.cs:390-401`):

| HTTP Header | Data Type | Description | HUD Metric | Example Value |
|-------------|-----------|-------------|------------|---------------|
| **X-Scene-Name** | string | Name of the Unity scene | (Info only) | "MultiObjectDetection" |
| **X-Frame-Id** | int | Sequential frame counter | (Info only) | 42 |
| **X-E2E-Ms** | float | End-to-end latency from previous frame | **E2E** | 245.1 |
| **X-Upload-Ms** | float | Upload time from previous frame | **Upload** (ms) | 45.2 |
| **X-Download-Ms** | float | Download time from previous frame | **Download** (ms) | 35.8 |
| **X-Parse-Ms** | float | JSON parsing time from previous frame | **Parse** (ms) | 15.3 |
| **X-Upload-Bytes** | int | Upload data size from previous frame | **Upload** (KB) | 128000 |
| **X-Download-Bytes** | int | Download data size from previous frame | **Download** (KB) | 49800 |

**Important Notes**:
- **N-1 Frame Delay**: Headers contain metrics from the **previous frame** (frame N-1), not the current frame
- **First Frame**: Values are `0` for the first frame (expected behavior)
- **Format**: Floats use "F1" format (1 decimal place), integers are whole numbers

### Code Location

Headers are set in `SentisInferenceRunManager.cs`:

```csharp
// Line 390-401
request.SetRequestHeader("X-Scene-Name", "MultiObjectDetection");
request.SetRequestHeader("X-Frame-Id", m_frameId.ToString());
request.SetRequestHeader("X-E2E-Ms", m_lastE2eMs.ToString("F1"));
request.SetRequestHeader("X-Upload-Ms", m_lastUploadMs.ToString("F1"));
request.SetRequestHeader("X-Download-Ms", m_lastDownloadMs.ToString("F1"));
request.SetRequestHeader("X-Parse-Ms", m_lastParseMs.ToString("F1"));
request.SetRequestHeader("X-Upload-Bytes", m_lastUploadBytes.ToString());
request.SetRequestHeader("X-Download-Bytes", m_lastDownloadBytes.ToString());
```

### Server-Side Logging (Excel/CSV)

The server can extract these headers and log them to Excel or CSV files for analysis:

**Example CSV Structure**:
```csv
Timestamp,SceneName,FrameId,E2E_Ms,Upload_Ms,Download_Ms,Parse_Ms,Upload_Bytes,Download_Bytes
2026-04-03 14:23:45,MultiObjectDetection,1,0.0,0.0,0.0,0.0,0,0
2026-04-03 14:23:45,MultiObjectDetection,2,245.1,45.2,35.8,15.3,128000,49800
2026-04-03 14:23:45,MultiObjectDetection,3,238.7,43.1,34.2,14.9,127500,49200
```

**Python Server Example** (FastAPI):
```python
from fastapi import Request
import csv
from datetime import datetime

@app.post("/infer")
async def infer(request: Request):
    # Extract headers
    scene_name = request.headers.get("X-Scene-Name", "Unknown")
    frame_id = int(request.headers.get("X-Frame-Id", 0))
    e2e_ms = float(request.headers.get("X-E2E-Ms", 0.0))
    upload_ms = float(request.headers.get("X-Upload-Ms", 0.0))
    download_ms = float(request.headers.get("X-Download-Ms", 0.0))
    parse_ms = float(request.headers.get("X-Parse-Ms", 0.0))
    upload_bytes = int(request.headers.get("X-Upload-Bytes", 0))
    download_bytes = int(request.headers.get("X-Download-Bytes", 0))

    # Log to CSV
    with open("metrics.csv", "a", newline="") as f:
        writer = csv.writer(f)
        writer.writerow([
            datetime.now().isoformat(),
            scene_name,
            frame_id,
            e2e_ms,
            upload_ms,
            download_ms,
            parse_ms,
            upload_bytes,
            download_bytes
        ])

    # ... rest of inference logic
```

### HUD to Header Mapping

This table shows how HUD display metrics correspond to HTTP headers:

| HUD Display | HTTP Header(s) | Calculation |
|-------------|---------------|-------------|
| **FPS: 72.5** | (Not sent) | Calculated client-side from frame delta time |
| **E2E: 245ms** | X-E2E-Ms | Direct mapping (previous frame) |
| **Upload: 45ms (18%)** | X-Upload-Ms | Direct mapping + percentage calculation |
| **Server: 150ms (61%)** | (Calculated) | E2E - Upload - Download - Parse |
| **Download: 35ms (14%)** | X-Download-Ms | Direct mapping + percentage calculation |
| **Parse: 15ms (6%)** | X-Parse-Ms | Direct mapping + percentage calculation |
| **Objects: 3** | (Not sent) | Calculated from inference response |
| **Avg Conf: 0.87** | (Not sent) | Calculated from inference response |
| **Upload: 125.3KB** | X-Upload-Bytes | Direct mapping (formatted to KB/MB) |
| **Download: 48.7KB** | X-Download-Bytes | Direct mapping (formatted to KB/MB) |

**Key Differences**:
- **Server processing time** is NOT sent as a header - it's calculated by the Unity client as: `E2E - Upload - Download - Parse`
- **FPS** is only calculated on the client side (not sent to server)
- **Detection metrics** (Objects, Avg Confidence) are derived from the server response, not sent as headers

### Usage for Analysis

Server-side logs enable offline analysis:

1. **Performance Trends**: Plot latency over time to identify degradation
2. **Bottleneck Identification**: Compare Upload/Server/Download ratios
3. **Network Issues**: Track Upload/Download times for WiFi problems
4. **Payload Size Analysis**: Monitor Upload/Download bytes for optimization
5. **Frame Correlation**: Use Frame-Id to correlate with client-side logs

---

## Related Documents

- [README.md](README.md) - Project overview
- [QUICK_START_GUIDE.md](QUICK_START_GUIDE.md) - Quick start guide
- [POSE_ESTIMATION_TECHNICAL_GUIDE.md](POSE_ESTIMATION_TECHNICAL_GUIDE.md) - Pose estimation technical guide

---

## Version History

| Version | Date | Changes |
|---------|------|---------|
| 1.0 | 2026-04-03 | Initial release - Basic latency monitoring features |

---

**Last Updated**: 2026-04-03
**Author**: Claude (Anthropic AI)
**Applicable Scene**: MultiObjectDetection
