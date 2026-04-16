# Unity Frame Tracking Implementation Guide

This guide provides complete C# implementation for frame tracking, display-side drop detection, and telemetry integration with the bounded queue server.

---

## Overview

The Unity side must:
1. **Send frames** without waiting for responses (fire-and-forget)
2. **Track frame lifecycle** (send → receive → display/drop)
3. **Detect display-side drops** (when newer frame supersedes older)
4. **Send telemetry** to server using delayed N+1 pattern

---

## Core Data Structures

### FrameTelemetry Class

```csharp
[System.Serializable]
private class FrameTelemetry
{
    // Identity
    public int frameId;
    public string sessionId;

    // Unity timestamps (Time.realtimeSinceStartup in seconds)
    public float unitySendTs;
    public float unityReceiveTs;
    public float unityDisplayTs;
    public float unityDropTs;

    // Server timestamps (from response, Unix epoch seconds)
    public double serverReceiveTs;
    public double serverProcessStartTs;
    public double serverSendTs;

    // State
    public string finalState = "Pending";  // "Pending", "Displayed", "Dropped", "Failed"
    public string dropReason = "";
    public string errorReason = "";

    // Tracking flags
    public bool isCompleted = false;  // Response received
    public bool isDisplayed = false;  // Actually shown in Unity

    // Convert Unity timestamps to Unix milliseconds for server
    public double UnityToUnixMs(float unityTime)
    {
        // Unity Time.realtimeSinceStartup is in seconds since app start
        // Convert to Unix milliseconds: (current Unix time - app start time) + unityTime
        return (DateTime.UtcNow.Subtract(new DateTime(1970, 1, 1)).TotalMilliseconds)
               - (Time.realtimeSinceStartup * 1000.0)
               + (unityTime * 1000.0);
    }
}
```

### InferenceResponse Structure

```csharp
[System.Serializable]
public class InferenceResponse
{
    public int frameId;
    public float receiveTimestamp;

    // Server timestamps (Unix epoch seconds)
    public double t_server_recv;
    public double server_process_start_ts;
    public double t_server_send;

    // Timing metrics
    public float processing_time_ms;
    public float queue_wait_ms;

    // Detection/pose data
    public DetectionResult detections;
    public SkeletonResult skeleton;
    // ... other fields ...
}
```

---

## Implementation

### 1. Member Variables

```csharp
// Frame tracking
private Dictionary<int, FrameTelemetry> m_frameTracking = new Dictionary<int, FrameTelemetry>();
private Dictionary<int, InferenceResponse> m_completedFrames = new Dictionary<int, InferenceResponse>();
private int m_lastDisplayedFrameId = -1;
private int m_frameCounter = 0;

// Session tracking
private string m_sessionId;
private double m_sessionStartTimeUnixMs;

void Start()
{
    // Generate session ID (GUID for global uniqueness)
    m_sessionId = System.Guid.NewGuid().ToString();

    // Record session start time in Unix milliseconds
    m_sessionStartTimeUnixMs = DateTime.UtcNow.Subtract(new DateTime(1970, 1, 1)).TotalMilliseconds;

    Debug.Log($"[SESSION] Started: {m_sessionId}");
}
```

### 2. Send Request with Telemetry Headers

```csharp
private IEnumerator SendInferenceRequest()
{
    // Capture frame
    byte[] imageData = CaptureFrame();  // Your existing capture logic

    int frameId = m_frameCounter++;

    // Record send timestamp IMMEDIATELY
    float unitySendTs = Time.realtimeSinceStartup;

    // Create telemetry tracking
    var telemetry = new FrameTelemetry
    {
        frameId = frameId,
        sessionId = m_sessionId,
        unitySendTs = unitySendTs,
        finalState = "Pending"
    };

    m_frameTracking[frameId] = telemetry;

    // Build URL
    string url = $"{m_baseUrl}/infer_human?mode=both";

    // Create request
    using (UnityWebRequest request = new UnityWebRequest(url, "POST"))
    {
        // Setup multipart form data
        List<IMultipartFormSection> formData = new List<IMultipartFormSection>();
        formData.Add(new MultipartFormFileSection("image", imageData, "frame.jpg", "image/jpeg"));

        byte[] boundary = UnityWebRequest.GenerateBoundary();
        byte[] formSections = UnityWebRequest.SerializeFormSections(formData, boundary);

        request.uploadHandler = new UploadHandlerRaw(formSections);
        request.downloadHandler = new DownloadHandlerBuffer();
        request.SetRequestHeader("Content-Type", $"multipart/form-data; boundary={Encoding.UTF8.GetString(boundary)}");

        // ====================================================================
        // CURRENT FRAME HEADERS (Frame N)
        // ====================================================================
        request.SetRequestHeader("X-Session-Id", m_sessionId);
        request.SetRequestHeader("X-Frame-Id", frameId.ToString());
        request.SetRequestHeader("X-Scene-Name", "PoseEstimation");

        // Send Unity timestamp (will be converted to Unix ms by server if needed)
        request.SetRequestHeader("X-Unity-Send-Ts", telemetry.UnityToUnixMs(unitySendTs).ToString("F3"));

        // Image dimensions (for queue-drop logging on server)
        request.SetRequestHeader("X-Image-Width", imageWidth.ToString());
        request.SetRequestHeader("X-Image-Height", imageHeight.ToString());

        // ====================================================================
        // PREVIOUS FRAME TELEMETRY (Frame N-1) - Delayed N+1 Pattern
        // ====================================================================
        if (frameId > 0)
        {
            int prevFrameId = frameId - 1;
            if (m_frameTracking.ContainsKey(prevFrameId))
            {
                var prevTelem = m_frameTracking[prevFrameId];

                // Only send if final state is determined (not Pending)
                if (prevTelem.finalState != "Pending")
                {
                    request.SetRequestHeader("X-Prev-Session-Id", prevTelem.sessionId);
                    request.SetRequestHeader("X-Prev-Frame-Id", prevTelem.frameId.ToString());

                    // Unity timestamps (converted to Unix milliseconds)
                    request.SetRequestHeader("X-Prev-Unity-Send-Ts",
                        prevTelem.UnityToUnixMs(prevTelem.unitySendTs).ToString("F3"));
                    request.SetRequestHeader("X-Prev-Unity-Receive-Ts",
                        prevTelem.UnityToUnixMs(prevTelem.unityReceiveTs).ToString("F3"));
                    request.SetRequestHeader("X-Prev-Unity-Display-Ts",
                        prevTelem.unityDisplayTs > 0 ? prevTelem.UnityToUnixMs(prevTelem.unityDisplayTs).ToString("F3") : "0");
                    request.SetRequestHeader("X-Prev-Unity-Drop-Ts",
                        prevTelem.unityDropTs > 0 ? prevTelem.UnityToUnixMs(prevTelem.unityDropTs).ToString("F3") : "0");

                    // Server timestamps (already in Unix seconds, convert to ms)
                    request.SetRequestHeader("X-Prev-Server-Receive-Ts",
                        (prevTelem.serverReceiveTs * 1000.0).ToString("F3"));
                    request.SetRequestHeader("X-Prev-Server-Process-Start-Ts",
                        (prevTelem.serverProcessStartTs * 1000.0).ToString("F3"));
                    request.SetRequestHeader("X-Prev-Server-Send-Ts",
                        (prevTelem.serverSendTs * 1000.0).ToString("F3"));

                    // State
                    request.SetRequestHeader("X-Prev-Final-State", prevTelem.finalState);
                    request.SetRequestHeader("X-Prev-Drop-Reason", prevTelem.dropReason);
                    request.SetRequestHeader("X-Prev-Error-Reason", prevTelem.errorReason);

                    // Clean up old telemetry (frames older than N-2)
                    if (frameId > 2)
                    {
                        m_frameTracking.Remove(frameId - 2);
                    }
                }
            }
        }

        // Send request (fire-and-forget, don't wait for previous responses)
        yield return request.SendWebRequest();

        // Record receive timestamp IMMEDIATELY
        float unityReceiveTs = Time.realtimeSinceStartup;

        // Handle response
        if (request.result == UnityWebRequest.Result.Success)
        {
            HandleSuccessResponse(frameId, unityReceiveTs, request.downloadHandler.data);
        }
        else
        {
            HandleFailedResponse(frameId, request.error);
        }
    }
}
```

### 3. Handle Success Response

```csharp
private void HandleSuccessResponse(int frameId, float unityReceiveTs, byte[] responseData)
{
    // Update telemetry
    if (!m_frameTracking.ContainsKey(frameId))
    {
        Debug.LogWarning($"[TELEMETRY] Frame {frameId} not found in tracking!");
        return;
    }

    var telemetry = m_frameTracking[frameId];
    telemetry.unityReceiveTs = unityReceiveTs;
    telemetry.isCompleted = true;

    // Parse response
    string responseText = System.Text.Encoding.UTF8.GetString(responseData);
    InferenceResponse response = JsonUtility.FromJson<InferenceResponse>(responseText);

    // Store server timestamps
    telemetry.serverReceiveTs = response.t_server_recv;
    telemetry.serverProcessStartTs = response.server_process_start_ts;
    telemetry.serverSendTs = response.t_server_send;

    // Store completed frame for display decision
    m_completedFrames[frameId] = response;

    // ========================================================================
    // DISPLAY DECISION: Show only the LATEST completed frame
    // ========================================================================
    int latestCompletedFrameId = m_completedFrames.Keys.Max();

    if (frameId == latestCompletedFrameId)
    {
        // DISPLAY this frame
        DisplayFrame(frameId, response);

        // Mark as displayed
        telemetry.unityDisplayTs = Time.realtimeSinceStartup;
        telemetry.finalState = "Displayed";
        telemetry.isDisplayed = true;

        m_lastDisplayedFrameId = frameId;

        Debug.Log($"[DISPLAY] Frame {frameId} displayed (E2E={(unityReceiveTs - telemetry.unitySendTs)*1000:F1}ms)");

        // Check for display-drops: frames completed before this but never displayed
        CheckForDisplayDrops(frameId);
    }
    else
    {
        // This frame completed but is not the latest
        // Keep it in completedFrames, decision pending
        Debug.Log($"[RECEIVE] Frame {frameId} completed but not latest (latest={latestCompletedFrameId})");
    }
}

private void CheckForDisplayDrops(int currentDisplayedFrameId)
{
    float now = Time.realtimeSinceStartup;

    foreach (var kvp in m_completedFrames.ToList())
    {
        int oldFrameId = kvp.Key;

        // If older frame was completed but never displayed
        if (oldFrameId < currentDisplayedFrameId &&
            m_frameTracking.ContainsKey(oldFrameId) &&
            !m_frameTracking[oldFrameId].isDisplayed)
        {
            var oldTelem = m_frameTracking[oldFrameId];

            // Mark as display-dropped
            oldTelem.unityDropTs = now;
            oldTelem.finalState = "Dropped";
            oldTelem.dropReason = "SupersededByNewerCompletedFrame";

            Debug.Log($"[DISPLAY DROP] Frame {oldFrameId} superseded by {currentDisplayedFrameId}");

            // Remove from completed frames
            m_completedFrames.Remove(oldFrameId);
        }
    }
}
```

### 4. Handle Failed Response

```csharp
private void HandleFailedResponse(int frameId, string error)
{
    if (!m_frameTracking.ContainsKey(frameId))
    {
        return;
    }

    var telemetry = m_frameTracking[frameId];
    telemetry.finalState = "Failed";
    telemetry.errorReason = error;

    Debug.LogError($"[FAILED] Frame {frameId}: {error}");
}
```

### 5. Display Frame (Your Existing Logic)

```csharp
private void DisplayFrame(int frameId, InferenceResponse response)
{
    // Update your existing display logic:
    // - Draw bounding boxes
    // - Draw skeleton keypoints
    // - Update UI

    // Example:
    if (response.detections != null)
    {
        foreach (var detection in response.detections.detections)
        {
            DrawBoundingBox(detection.bbox);
        }
    }

    if (response.skeleton != null)
    {
        foreach (var person in response.skeleton.persons)
        {
            DrawSkeleton(person.keypoints);
        }
    }
}
```

---

## Key Implementation Points

### ✅ Fire-and-Forget Sending
- Don't wait for Frame N-1 response before sending Frame N
- Use coroutines that run independently
- Multiple requests can be in flight simultaneously

### ✅ Display Decision Logic
```csharp
// ALWAYS display the latest completed frame
int latestCompleted = m_completedFrames.Keys.Max();
if (currentFrameId == latestCompleted)
{
    DisplayFrame(currentFrameId, response);
}
```

### ✅ Timestamp Conversion
```csharp
// Unity time (seconds since app start) → Unix milliseconds
double UnityToUnixMs(float unityTime)
{
    double nowUnixMs = DateTime.UtcNow.Subtract(new DateTime(1970, 1, 1)).TotalMilliseconds;
    double appStartUnixMs = nowUnixMs - (Time.realtimeSinceStartup * 1000.0);
    return appStartUnixMs + (unityTime * 1000.0);
}
```

### ✅ Delayed N+1 Telemetry Pattern
- Frame N's **final state** is only known after Frame N+1 starts
- Send Frame N-1's complete telemetry in Frame N's request headers
- Server logs Frame N-1 when it receives Frame N

---

## Testing Checklist

### Verify Display Logic
```csharp
// Test case: Frame 10, 11, 12 complete out of order
// Expected: Frame 12 displayed, Frame 10 and 11 dropped
```

### Verify Telemetry Headers
```csharp
// Check that X-Prev-* headers are sent correctly
// Check that timestamps are in Unix milliseconds
```

### Verify State Transitions
```csharp
// Pending → Displayed (normal case)
// Pending → Dropped (superseded)
// Pending → Failed (network error)
```

---

## Performance Considerations

### Memory Management
```csharp
// Clean up old telemetry to avoid memory growth
if (frameId > 2)
{
    m_frameTracking.Remove(frameId - 2);
    m_completedFrames.Remove(frameId - 2);
}
```

### Frame Rate Control
```csharp
// Use target FPS to control send rate
private float m_timeSinceLastSend = 0f;
private float m_targetInterval;

void Update()
{
    m_timeSinceLastSend += Time.deltaTime;
    m_targetInterval = 1.0f / m_targetFPS;

    if (m_timeSinceLastSend >= m_targetInterval)
    {
        StartCoroutine(SendInferenceRequest());
        m_timeSinceLastSend = 0f;
    }
}
```

---

## Debugging Tips

### Log Frame Lifecycle
```csharp
Debug.Log($"[SEND] Frame {frameId} sent at {unitySendTs:F3}s");
Debug.Log($"[RECEIVE] Frame {frameId} received at {unityReceiveTs:F3}s (E2E={(unityReceiveTs-unitySendTs)*1000:F1}ms)");
Debug.Log($"[DISPLAY] Frame {frameId} displayed");
Debug.Log($"[DROP] Frame {frameId} dropped (reason={dropReason})");
```

### Monitor Queue State
```csharp
Debug.Log($"[QUEUE] Pending={m_completedFrames.Count}, LastDisplayed={m_lastDisplayedFrameId}");
```

---

## Integration with Existing Scenes

### PoseEstimation Scene
- Already has `PoseInferenceRunManager.cs`
- Apply frame tracking to existing coroutine

### Segmentation Scene
- Already has `SegmentationInferenceManager.cs`
- Apply same pattern, use `/segmentation` endpoint

### MultiObjectDetection Scene
- Similar to PoseEstimation
- Use `mode=detection` parameter

---

This implementation provides complete frame tracking, display-side drop detection, and telemetry integration with the bounded queue server. All three scenes should follow this pattern for consistent behavior.
