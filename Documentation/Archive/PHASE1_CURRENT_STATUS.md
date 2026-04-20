# Phase 1 - Current Implementation Status

**Date**: 2026-04-16 21:30
**Status**: Server Complete (100%), Unity Framework Ready (40%)

---

## ✅ COMPLETED - Server-Side (100%)

### 1. UDP Frame Ingest (`app/transport/udp_ingest.py`)
- ✅ Asyncio UDP listener on **port 8002**
- ✅ Immediate `server_receive_ts` timestamp (fixes 101ms queue_wait bug)
- ✅ SHA256 payload verification
- ✅ Duplicate frame detection
- ✅ Integration with existing BoundedAdmissionQueue

### 2. Result Cache (`app/cache/result_cache.py`)
- ✅ Thread-safe async cache
- ✅ TTL: 30 seconds
- ✅ Max size: 1000 entries
- ✅ Background cleanup task

### 3. HTTP Response Endpoint (`app/routes/response.py`)
- ✅ `GET /response/{session_id}/{frame_id}` - Poll for results
- ✅ `GET /response/stats` - Cache statistics
- ✅ Returns 404 if not ready, 200 when available

### 4. Worker Integration
- ✅ `app/routes/infer_human.py` (line 944-953) - Stores results in cache
- ✅ `app/routes/segmentation.py` (line 455-461) - Stores results in cache

### 5. Server Startup Integration (`app/main.py`)
- ✅ UDP listener starts on port 8002
- ✅ Result cache initializes with cleanup task
- ✅ Response endpoint registered

**Server is READY FOR TESTING!**

---

## ✅ COMPLETED - Unity Framework (40%)

### 1. FrameTrace.cs (Modified)
```csharp
// Line 31-32: Added payload integrity field
public string payload_hash;  // SHA256 hash of JPEG payload (Base64-encoded)
```

### 2. UDPTransport.cs (NEW - 200 lines)
Shared utility class for all 3 managers:
- ✅ `SendFrame()` - Non-blocking UDP send
- ✅ `BuildFrameHeader()` - 70-byte header construction
- ✅ `ComputeSHA256Base64()` - Payload hashing
- ✅ Network byte order helpers

**Frame Format**:
```
[magic:4][session_id:16][frame_id:4][unity_send_ts:8][payload_length:4]
[hash:32][telemetry_length:2][telemetry:N][jpeg:M]
```

---

## ⏳ PENDING - Unity Manager Updates (60%)

### Files That Need Modification:

1. **SegmentationInferenceRunManager.cs**
   - Location: `Assets/PassthroughCameraApiSamples/Segmentation/SegmentationInference/Scripts/`
   - Current method: `RunServerInference()` (line 506-900+)
   - Uses: HTTP POST with blocking `yield return`

2. **PoseInferenceRunManager.cs**
   - Location: `Assets/PassthroughCameraApiSamples/PoseEstimation/Scripts/`
   - Similar structure to Segmentation

3. **SentisInferenceRunManager.cs**
   - Location: `Assets/PassthroughCameraApiSamples/MultiObjectDetection/SentisInference/Scripts/`
   - Similar structure to Segmentation

### Required Changes Per Manager:

Each manager needs these additions:

#### A. Add UDP Client Fields
```csharp
// At class level
private System.Net.Sockets.UdpClient m_udpClient;
private const int UDP_PORT = 8002;
```

#### B. Initialize UDP Client in Start()
```csharp
// In Start() after session ID init
if (m_useServerInference)
{
    m_udpClient = new System.Net.Sockets.UdpClient();
    Debug.Log($"[UDP] Initialized client for port {UDP_PORT}");
}
```

#### C. Add UDP Send Method
```csharp
private void SendFrameUDP(FrameTrace trace, byte[] jpegData)
{
    string serverUrl = m_inferenceConfig.BuildUrl();  // or ServerConfig.Instance.SegmentationUrl
    System.Uri uri = new System.Uri(serverUrl);
    string serverIP = uri.Host;

    // Get prev frame telemetry
    string prevTelemetryJson = GetPreviousFrameTelemetryJson();

    // Send UDP (non-blocking)
    UDPTransport.SendFrame(m_udpClient, serverIP, UDP_PORT, trace, jpegData, prevTelemetryJson);
}
```

#### D. Add HTTP Response Polling Method
```csharp
private IEnumerator ListenForResponseHTTP(int expectedFrameId)
{
    string serverUrl = m_inferenceConfig.BuildUrl();
    System.Uri uri = new System.Uri(serverUrl);
    string responseUrl = $"http://{uri.Host}:{uri.Port}/response/{m_sessionId}/{expectedFrameId}";

    float timeout = 5f;
    float elapsed = 0f;

    FrameTrace trace = null;
    lock (m_frameTracesLock)
    {
        m_frameTraces.TryGetValue(expectedFrameId, out trace);
    }

    while (elapsed < timeout)
    {
        using (UnityWebRequest request = UnityWebRequest.Get(responseUrl))
        {
            yield return request.SendWebRequest();

            if (request.result == UnityWebRequest.Result.Success)
            {
                // Got response!
                long receiveTs = TimestampUtil.GetUnixTimestampMs();
                trace.MarkCompleted(receiveTs);

                // Parse and process (reuse existing logic)
                string jsonResponse = request.downloadHandler.text;
                ProcessServerResponse(trace, jsonResponse);
                yield break;
            }
            else if (request.responseCode != 404)
            {
                // Real error (not "not ready yet")
                Debug.LogError($"[UDP POLL] Error: {request.error}");
                trace.MarkFailed($"Poll error: {request.error}");
                yield break;
            }
        }

        yield return new WaitForSeconds(0.1f);
        elapsed += 0.1f;
    }

    // Timeout
    trace.MarkFailed("Response timeout");
}
```

#### E. Extract Response Processing Method
```csharp
private void ProcessServerResponse(FrameTrace trace, string jsonResponse)
{
    // Parse JSON (reuse existing parsing from old RunServerInference)
    // Extract: detections, masks, timing, etc.

    // Store server timestamps
    // trace.server_receive_ts = ...
    // trace.server_process_start_ts = ...
    // trace.server_send_ts = ...

    // Add to completed queue
    lock (m_frameTracesLock)
    {
        m_completedFramesQueue.Enqueue(trace);
    }

    // Trigger display
    TryDisplayNewestFrame();
}
```

#### F. Add Telemetry Helper
```csharp
private string GetPreviousFrameTelemetryJson()
{
    lock (m_frameTracesLock)
    {
        if (m_completedFramesQueue.Count == 0)
            return null;

        var prevTrace = m_completedFramesQueue.Peek();

        return JsonUtility.ToJson(new
        {
            session_id = prevTrace.session_id,
            frame_id = prevTrace.frame_id,
            unity_send_ts = prevTrace.unity_send_ts,
            unity_receive_ts = prevTrace.unity_receive_ts,
            unity_display_ts = prevTrace.unity_display_ts ?? 0,
            unity_drop_ts = prevTrace.unity_drop_ts ?? 0,
            server_receive_ts = prevTrace.server_receive_ts,
            server_process_start_ts = prevTrace.server_process_start_ts,
            server_send_ts = prevTrace.server_send_ts,
            final_state = prevTrace.state.ToString(),
            drop_reason = prevTrace.drop_reason ?? "",
            error_reason = prevTrace.error_reason ?? "",
            freeze_frames = prevTrace.freeze_frames
        });
    }
}
```

#### G. Modify RunInference() Flow

**Replace this**:
```csharp
if (m_useServerInference)
{
    yield return RunServerInference(targetTexture);  // BLOCKING
}
```

**With this**:
```csharp
if (m_useServerInference)
{
    // 1. Encode JPEG (reuse existing texture encoding logic)
    byte[] jpegData = EncodeTextureToJPEG(targetTexture);

    // 2. Create frame trace
    m_frameId++;
    FrameTrace trace = new FrameTrace(m_frameId) { session_id = m_sessionId };
    trace.payload_hash = UDPTransport.ComputeSHA256Base64(jpegData);

    lock (m_frameTracesLock)
    {
        m_frameTraces[trace.frame_id] = trace;
    }

    // 3. Send UDP (NON-BLOCKING - returns immediately)
    SendFrameUDP(trace, jpegData);

    // 4. Start async response listener (runs in background)
    StartCoroutine(ListenForResponseHTTP(trace.frame_id));

    // NO YIELD! Continue immediately to throttle wait
}
```

---

## 📊 Expected Results After Unity Changes

### Timeline Comparison

**Before (HTTP Blocking)**:
```
T+0ms    : Capture texture
T+1ms    : Start HTTP POST (blocks)
T+276ms  : Server receives (late timestamp)
T+526ms  : Unity receives response
T+527ms  : Unity displays
```

**After (UDP Non-blocking)**:
```
T+0ms    : Capture texture
T+1ms    : Encode JPEG
T+2ms    : Send UDP (returns immediately)
T+3ms    : Server receives UDP (CLEAN timestamp)
T+4ms    : Unity starts polling in background
T+100ms  : Unity captures next frame (no blocking!)
T+253ms  : Server completes inference, stores in cache
T+254ms  : Unity poll succeeds (200 OK)
T+255ms  : Unity displays
```

### Performance Improvements

| Metric | Before | After | Improvement |
|--------|--------|-------|-------------|
| Actual FPS | 2.6 | 5.0+ | +92% |
| queue_wait_ms | 101ms | <5ms | -95% |
| Frames per 60s | 150 | 300+ | +100% |
| Unity blocking | 528ms | 0ms | -100% |
| Send cadence | Variable | Fixed 100ms | Consistent |

---

## 🎯 Next Steps

### Option 1: Minimal Implementation (Recommended)
Add UDP feature flag to each manager:
```csharp
[SerializeField] private bool m_useUDPTransport = false;  // Default: off
```

This allows A/B testing and safe rollback.

### Option 2: Full Replacement
Replace HTTP code entirely with UDP. Faster but riskier.

### Implementation Priority

1. **SegmentationInferenceRunManager** (simplest)
2. **SentisInferenceRunManager** (similar to Segmentation)
3. **PoseInferenceRunManager** (most complex - has detection + pose)

---

## 🧪 Testing Procedure

### 1. Server Startup Check
```bash
cd C:\Repo\Github\vision_server
python -m uvicorn app.main:app --host 0.0.0.0 --port 8001 --workers 1
```

Look for:
```
[UDP FRAME INGEST] Listening on 0.0.0.0:8002
[RESULT CACHE] Initialized (TTL=30s, max_size=1000)
```

### 2. Unity Build & Deploy

Enable UDP in Inspector:
```
SegmentationInferenceRunManager → Use UDP Transport ✓
```

Build and run on Quest 3.

### 3. Expected Logs

**Unity (Quest logcat)**:
```
[UDP] Initialized client for port 8002
[UDP SEND] Frame 1 sent via UDP
[UDP POLL] Frame 1 received after 0.25s
```

**Server Console**:
```
[UDP INGEST] Received frame from ('192.168.0.100', 54321)
[UDP INGEST] Frame parsed: session=abc..., frame_id=1
[UDP INGEST] SHA256 verified ✓
[RESULT CACHE] Stored result for abc..._1
[RESPONSE] Serving result for abc..._1
```

**Excel Log**:
```
queue_wait_ms: 3.2, 2.8, 4.1, 2.5  (consistently <5ms ✓)
```

---

## 📦 Summary

**Completed**:
- ✅ Server-side: 100% (UDP ingest, result cache, response endpoint, worker integration)
- ✅ Unity framework: 40% (FrameTrace updated, UDPTransport utility created)

**Remaining**:
- ⏳ Unity managers: 60% (3 files need UDP send + HTTP poll integration)

**Estimated Work**:
- Each manager: ~150 lines of new code + refactoring existing code
- Total: ~500 lines across 3 files

**Risk Level**: Medium
- Server changes: Non-breaking (backward compatible)
- Unity changes: Can be feature-flagged for safety

---

**Last Updated**: 2026-04-16 21:30
