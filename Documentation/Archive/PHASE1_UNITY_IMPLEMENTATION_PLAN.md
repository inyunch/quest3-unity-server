# Phase 1 - Unity Implementation Plan

**Date**: 2026-04-16
**Status**: Server-side Complete, Unity-side In Progress

---

## 📋 Summary

需要更新3個inference manager以使用UDP non-blocking transport:
1. SegmentationInferenceRunManager.cs
2. PoseInferenceRunManager.cs
3. SentisInferenceRunManager.cs

---

## ✅ Already Completed

1. **FrameTrace.cs** - Added `payload_hash` field (line 32)
2. **UDPTransport.cs** - Created shared utility class with:
   - `SendFrame()` - Non-blocking UDP send
   - `ComputeSHA256Base64()` - Payload hashing
   - `BuildFrameHeader()` - 70-byte header construction

---

## 🔧 Changes Needed Per Manager

Each of the 3 managers needs the following changes:

### 1. Add UDP Client Field

```csharp
// At class level with other private fields
private System.Net.Sockets.UdpClient m_udpClient;
private const int UDP_PORT = 8002;
```

### 2. Initialize UDP Client in Start()

```csharp
// In Start() method, after session ID initialization
if (m_useServerInference)
{
    m_udpClient = new System.Net.Sockets.UdpClient();
    Debug.Log($"[UDP] Initialized UDP client for port {UDP_PORT}");
}
```

### 3. Modify RunInference() Flow

**Current Flow (HTTP Blocking)**:
```
1. Capture texture
2. Call RunServerInference(texture)  ← BLOCKS until response
3. Wait for interval
```

**New Flow (UDP Non-blocking)**:
```
1. Capture texture
2. Encode JPEG
3. Create FrameTrace with hash
4. SendFrameUDP() ← Returns immediately
5. StartCoroutine(ListenForResponseHTTP())  ← Async polling
6. Wait for interval
```

### 4. Replace RunServerInference() Logic

**Old**: HTTP POST with blocking `yield return request.SendWebRequest()`

**New**: Split into 2 methods:

#### Method A: SendFrameUDP() - Immediate send

```csharp
private void SendFrameUDP(FrameTrace trace, byte[] jpegData)
{
    // Get server IP from config
    string serverBaseUrl = m_inferenceConfig.BuildUrl();
    string serverIP = ExtractIPFromUrl(serverBaseUrl);  // Parse "http://192.168.0.135:8001" → "192.168.0.135"

    // Get previous frame telemetry (if any)
    string prevTelemetryJson = GetPreviousFrameTelemetryJson();

    // Send via UDP (non-blocking)
    UDPTransport.SendFrame(m_udpClient, serverIP, UDP_PORT, trace, jpegData, prevTelemetryJson);

    Debug.Log($"[UDP SEND] Frame {trace.frame_id} sent via UDP");
}

private string ExtractIPFromUrl(string url)
{
    // Parse "http://192.168.0.135:8001/segmentation" → "192.168.0.135"
    var uri = new System.Uri(url);
    return uri.Host;
}
```

#### Method B: ListenForResponseHTTP() - Async polling

```csharp
private IEnumerator ListenForResponseHTTP(int expectedFrameId)
{
    // Build response polling URL
    string serverBaseUrl = m_inferenceConfig.BuildUrl();
    string responseUrl = serverBaseUrl.Replace("/segmentation", $"/response/{m_sessionId}/{expectedFrameId}");

    float timeout = 5f;
    float elapsed = 0f;
    float pollInterval = 0.1f;  // Poll every 100ms

    FrameTrace trace = null;
    lock (m_frameTracesLock)
    {
        if (!m_frameTraces.TryGetValue(expectedFrameId, out trace))
        {
            Debug.LogWarning($"[UDP POLL] Frame {expectedFrameId} not found in traces!");
            yield break;
        }
    }

    while (elapsed < timeout)
    {
        using (UnityWebRequest request = UnityWebRequest.Get(responseUrl))
        {
            yield return request.SendWebRequest();

            if (request.result == UnityWebRequest.Result.Success)
            {
                // Got response! Process it
                long receiveTs = TimestampUtil.GetUnixTimestampMs();
                trace.MarkCompleted(receiveTs);

                // Parse JSON response (reuse existing parsing logic)
                string jsonResponse = request.downloadHandler.text;
                ProcessServerResponse(trace, jsonResponse);

                Debug.Log($"[UDP POLL] Frame {expectedFrameId} received after {elapsed:F2}s");
                yield break;
            }
            else if (request.responseCode == 404)
            {
                // Not ready yet, continue polling
                // (404 is expected while server is processing)
            }
            else
            {
                // Actual error
                Debug.LogError($"[UDP POLL] Error polling frame {expectedFrameId}: {request.error}");
                trace.MarkFailed($"Poll error: {request.error}");
                yield break;
            }
        }

        yield return new WaitForSeconds(pollInterval);
        elapsed += pollInterval;
    }

    // Timeout
    Debug.LogWarning($"[UDP POLL] Timeout waiting for frame {expectedFrameId}");
    trace.MarkFailed("Response timeout");
}
```

### 5. Extract ProcessServerResponse() Method

Move response parsing logic into a shared method:

```csharp
private void ProcessServerResponse(FrameTrace trace, string jsonResponse)
{
    // Parse JSON and extract detections
    // (reuse existing parsing logic from old RunServerInference)

    // Store timing metrics
    trace.server_proc_ms = /* from response */;
    trace.server_receive_ts = /* from response */;
    trace.server_process_start_ts = /* from response */;
    trace.server_send_ts = /* from response */;

    // Calculate upload/download times
    // (existing logic)

    // Add to completed queue
    lock (m_frameTracesLock)
    {
        m_completedFramesQueue.Enqueue(trace);
    }

    // Trigger display attempt
    TryDisplayNewestFrame();
}
```

### 6. Update RunInference() to Use New Flow

**Replace this**:
```csharp
if (m_useServerInference)
{
    yield return RunServerInference(targetTexture);
}
```

**With this**:
```csharp
if (m_useServerInference)
{
    // 1. Encode JPEG
    byte[] jpegData = EncodeTextureToJPEG(targetTexture, m_inferenceConfig.jpegQuality);

    // 2. Create frame trace
    m_frameId++;
    var trace = new FrameTrace(m_frameId)
    {
        session_id = m_sessionId
    };

    // Compute hash
    trace.payload_hash = UDPTransport.ComputeSHA256Base64(jpegData);

    // Store trace
    lock (m_frameTracesLock)
    {
        m_frameTraces[trace.frame_id] = trace;
    }

    // 3. Send UDP (non-blocking)
    SendFrameUDP(trace, jpegData);

    // 4. Start async response listener
    StartCoroutine(ListenForResponseHTTP(trace.frame_id));
}
```

### 7. Add Helper Method: EncodeTextureToJPEG()

```csharp
private byte[] EncodeTextureToJPEG(Texture texture, int quality)
{
    // Convert to Texture2D
    RenderTexture rt = texture as RenderTexture;
    Texture2D tempTex = new Texture2D(texture.width, texture.height, TextureFormat.RGB24, false);

    RenderTexture.active = rt;
    tempTex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
    tempTex.Apply();
    RenderTexture.active = null;

    // Encode to JPEG
    byte[] jpegData = tempTex.EncodeToJPG(quality);

    Destroy(tempTex);
    return jpegData;
}
```

### 8. Add Helper Method: GetPreviousFrameTelemetryJson()

```csharp
private string GetPreviousFrameTelemetryJson()
{
    lock (m_frameTracesLock)
    {
        if (m_completedFramesQueue.Count == 0)
            return null;

        var prevTrace = m_completedFramesQueue.Peek();

        // Build JSON with delayed telemetry
        var telemetry = new Dictionary<string, object>
        {
            { "session_id", prevTrace.session_id },
            { "frame_id", prevTrace.frame_id },
            { "unity_send_ts", prevTrace.unity_send_ts },
            { "unity_receive_ts", prevTrace.unity_receive_ts },
            { "unity_display_ts", prevTrace.unity_display_ts ?? 0 },
            { "unity_drop_ts", prevTrace.unity_drop_ts ?? 0 },
            { "server_receive_ts", prevTrace.server_receive_ts },
            { "server_process_start_ts", prevTrace.server_process_start_ts },
            { "server_send_ts", prevTrace.server_send_ts },
            { "final_state", prevTrace.state.ToString() },
            { "drop_reason", prevTrace.drop_reason ?? "" },
            { "error_reason", prevTrace.error_reason ?? "" },
            { "freeze_frames", prevTrace.freeze_frames }
        };

        return JsonUtility.ToJson(telemetry);
    }
}
```

---

## 📊 Expected Behavior Changes

### Before (HTTP Blocking)

Timeline for 1 frame:
```
T+0ms    : Unity captures texture
T+1ms    : Unity starts HTTP POST
T+2ms    : Unity blocks (yield return)
T+276ms  : Server receives (late timestamp)
T+277ms  : Server starts processing (queue_wait=101ms incorrect)
T+527ms  : Server completes (250ms inference)
T+528ms  : Unity receives response
T+529ms  : Unity displays
```

**Issues**:
- Unity blocks for 528ms
- Can't send next frame until response received
- Max FPS = 1000/528 = 1.9 FPS
- Server timestamps are late (include HTTP overhead)

### After (UDP Non-blocking)

Timeline for multiple frames:
```
Frame 1:
T+0ms    : Unity captures, encodes, sends UDP
T+1ms    : Server receives UDP (CLEAN timestamp)
T+2ms    : Server enqueues (queue_wait=1ms ✓)
T+5ms    : Unity starts polling /response/session/1
T+105ms  : Unity polls (404 - not ready)
T+205ms  : Unity polls (404 - not ready)
T+252ms  : Server completes inference, stores in cache
T+305ms  : Unity polls (200 - success!)

Frame 2: (sent at T+100ms - no blocking!)
T+100ms  : Unity captures, encodes, sends UDP
T+101ms  : Server receives UDP
T+102ms  : Server enqueues (queue_wait=1ms ✓)
...
```

**Improvements**:
- ✅ Unity never blocks
- ✅ Fixed-cadence sending (targetFPS=10 → 1 frame per 100ms)
- ✅ Clean server timestamps (no HTTP overhead)
- ✅ Accurate queue_wait_ms (<5ms)
- ✅ Multiple frames in-flight simultaneously

---

## 🔄 Migration Strategy

### Option A: Feature Flag (Recommended)

Add a toggle to switch between old HTTP and new UDP:

```csharp
[Header("UDP Transport (Phase 1)")]
[SerializeField] private bool m_useUDPTransport = false;  // Default: off for safety

// In RunInference():
if (m_useServerInference)
{
    if (m_useUDPTransport)
    {
        // NEW: UDP non-blocking
        SendFrameUDP(trace, jpegData);
        StartCoroutine(ListenForResponseHTTP(trace.frame_id));
    }
    else
    {
        // OLD: HTTP blocking
        yield return RunServerInference(targetTexture);
    }
}
```

**Benefits**:
- Can A/B test performance
- Safe rollback if issues found
- No breaking changes

### Option B: Direct Replacement

Replace HTTP code entirely with UDP:
- Faster to implement
- Cleaner codebase
- Requires confidence in UDP implementation

---

## ⚠️ Potential Issues

### Issue 1: URL Parsing

The `BuildUrl()` method returns full endpoint URLs:
- PoseEstimation: `http://192.168.0.135:8001/infer_human?mode=both`
- Segmentation: `http://192.168.0.135:8001/segmentation`

We need to extract just the IP for UDP send, and build response URL correctly.

### Issue 2: ServerConfig Integration

All 3 managers use `m_inferenceConfig.BuildUrl()` which may use `ServerConfig.Instance`.

Need to ensure UDP client gets correct IP from config.

### Issue 3: Existing Telemetry Headers

Current code sends telemetry via HTTP headers. UDP sends it in packet payload.

Need to ensure server receives and processes it correctly (already handled in server-side implementation).

### Issue 4: Unity Compilation

After adding UDP client, Unity needs to recompile. May take 1-2 minutes.

---

## 🧪 Testing Checklist

After implementation:

1. **Unity Console Logs**:
   - `[UDP] Initialized UDP client for port 8002`
   - `[UDP SEND] Frame X sent via UDP`
   - `[UDP POLL] Frame X received after Ys`

2. **Server Console Logs**:
   - `[UDP INGEST] Received frame from (IP, port)`
   - `[UDP INGEST] Frame parsed: session=..., frame_id=X`
   - `[UDP INGEST] SHA256 verified ✓`
   - `[RESULT CACHE] Stored result for...`
   - `[RESPONSE] Serving result for...`

3. **Excel Logs**:
   - `queue_wait_ms` < 5ms (not 101ms)
   - `upload_ms` values more accurate
   - Frame intervals consistent ~100ms (targetFPS=10)

4. **Performance Metrics**:
   - Actual FPS increases from 2.6 to 5.0+
   - Frames per 60s: 150 → 300+

---

## 📝 Implementation Order

Suggested order (easiest to hardest):

1. **SegmentationInferenceRunManager.cs** - Simplest structure
2. **SentisInferenceRunManager.cs** - Similar to Segmentation
3. **PoseInferenceRunManager.cs** - Most complex (has both detection + pose)

---

**Next Step**: Implement changes in SegmentationInferenceRunManager.cs first as proof-of-concept.

**Last Updated**: 2026-04-16 21:00
