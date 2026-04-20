# Phase 1 - Remaining Implementation Tasks

**Current Status**: Server core infrastructure complete, need to wire up result caching and implement Unity client.

---

## Server-Side: Add Result Caching (1 task)

### File: `app/routes/infer_human.py`

**Find the return statement** (near end of `async def infer_human()`):
```python
# Current code returns response directly
return response_data
```

**Add before return**:
```python
# Store result in cache for UDP polling
from app.cache.result_cache import get_result_cache

result_cache = get_result_cache()
await result_cache.set(session_id, frame_id, response_data)

# Then return as before
return response_data
```

**Similar change needed in**: `app/routes/segmentation.py`

---

## Unity-Side: 5 Tasks

### Task 1: Add payload_hash to FrameTrace.cs

**File**: `Assets/PassthroughCameraApiSamples/Shared/Scripts/FrameTrace.cs`

**Add field**:
```csharp
// After existing fields
public string payload_hash;  // SHA256 hash (Base64-encoded)
```

---

### Task 2: Create UDPTransport.cs Utility

**File**: `Assets/PassthroughCameraApiSamples/Shared/Scripts/UDPTransport.cs` (NEW)

**Full implementation**: See `UDP_NON_BLOCKING_TRANSPORT_PROPOSAL.md` Section 2.2

**Key methods**:
- `SendFrameUDP(FrameTrace trace, byte[] jpegData)` - Pack and send UDP packet
- `ComputeSHA256(byte[] data)` - Hash computation
- Frame format: `[magic:4][session_id:16][frame_id:4][unity_send_ts:8][payload_length:4][hash:32][telemetry_length:2][telemetry:N][jpeg:M]`

---

### Task 3-5: Update All 3 Managers

**Files**:
1. `PoseInferenceRunManager.cs`
2. `SentisInferenceRunManager.cs`
3. `SegmentationInferenceRunManager.cs`

**Change `RunInference()` from**:
```csharp
private IEnumerator RunInference()
{
    // Capture
    Texture targetTexture = m_cameraAccess.GetTexture();

    // Send HTTP (blocking)
    yield return RunServerInference(targetTexture);  // OLD

    // Throttle
    yield return new WaitForSeconds(interval);
}
```

**To**:
```csharp
private IEnumerator RunInference()
{
    // 1. Capture
    Texture targetTexture = m_cameraAccess.GetTexture();

    // 2. Encode JPEG
    byte[] jpegData = EncodeToJPEG(targetTexture);

    // 3. Create trace with hash
    FrameTrace trace = new FrameTrace
    {
        session_id = m_sessionId,
        frame_id = ++m_frameId,
        unity_send_ts = UnixMilliseconds(),
        payload_hash = ComputeSHA256Base64(jpegData)  // NEW
    };

    // 4. Send UDP (non-blocking)
    SendFrameUDP(trace, jpegData);  // NEW

    // 5. Start async response listener
    StartCoroutine(ListenForResponseHTTP(trace.frame_id));  // NEW

    // 6. Throttle
    float interval = 1f / m_inferenceConfig.targetFPS;
    yield return new WaitForSeconds(interval);
}
```

**Add new methods**:
```csharp
private UdpClient m_udpClient;
private const int UDP_PORT = 8002;

private void SendFrameUDP(FrameTrace trace, byte[] jpegData)
{
    // Use UDPTransport utility
    UDPTransport.SendFrame(m_udpClient, serverIP, UDP_PORT, trace, jpegData, GetPrevFrameTelemetry());
}

private IEnumerator ListenForResponseHTTP(int expectedFrameId)
{
    string url = $"{baseUrl}/response/{m_sessionId}/{expectedFrameId}";
    float timeout = 5f;
    float elapsed = 0f;

    while (elapsed < timeout)
    {
        using (UnityWebRequest req = UnityWebRequest.Get(url))
        {
            yield return req.SendWebRequest();

            if (req.result == UnityWebRequest.Result.Success)
            {
                // Parse and process response (use existing ProcessResponse logic)
                ProcessResponse(expectedFrameId, req.downloadHandler.text);
                yield break;
            }
        }

        yield return new WaitForSeconds(0.1f);  // Poll every 100ms
        elapsed += 0.1f;
    }

    // Timeout
    MarkFrameAsFailed(expectedFrameId, "Response timeout");
}

private string ComputeSHA256Base64(byte[] data)
{
    using (var sha256 = System.Security.Cryptography.SHA256.Create())
    {
        byte[] hash = sha256.ComputeHash(data);
        return System.Convert.ToBase64String(hash);
    }
}
```

---

## Testing Checklist

### Server Tests
1. ✅ Start server → Check console logs for:
   - `[UDP INGEST] Listening on 0.0.0.0:8002`
   - `[RESULT CACHE] Initialized`
2. ⏳ Test `/response/stats` endpoint → Should return cache stats

### Integration Tests (After Unity Changes)
1. Unity sends frame → Server logs `[UDP INGEST] Frame X received`
2. Server logs show `server_receive_ts` immediately (clean timestamp)
3. Unity polls `/response/{session}/{frame}` → Gets 404, then 200
4. Excel logs show `queue_wait_ms` < 10ms (not 101ms)
5. Unity frame intervals: consistent ~100ms (targetFPS=10 → 1 frame per 100ms)

### Expected Results
| Metric | Before | After |
|--------|--------|-------|
| Actual FPS | 2.6 | 5.0 |
| Upload estimate | 276ms (noisy) | TBD (Phase 2) |
| Queue wait | 101ms | <5ms |
| Frames per 60s | 150 | 300 |

---

## Quick Start Commands

### Restart Server with UDP
```bash
cd C:\Repo\Github\vision_server
python -m uvicorn app.main:app --host 0.0.0.0 --port 8001 --workers 1
```

### Check UDP Port
```bash
netstat -an | findstr 8002
```

### Test Response Endpoint
```bash
curl http://localhost:8001/response/stats
```

---

## File Locations Summary

**Server (Complete)**:
- ✅ `app/transport/udp_ingest.py`
- ✅ `app/cache/result_cache.py`
- ✅ `app/routes/response.py`
- ✅ `app/main.py`

**Server (Need 2 line additions)**:
- ⏳ `app/routes/infer_human.py` - Add `result_cache.set()` before return
- ⏳ `app/routes/segmentation.py` - Add `result_cache.set()` before return

**Unity (Need implementation)**:
- ⏳ `Assets/Shared/Scripts/FrameTrace.cs` - Add 1 field
- ⏳ `Assets/Shared/Scripts/UDPTransport.cs` - New file (~200 lines)
- ⏳ `Assets/PoseEstimation/Scripts/PoseInferenceRunManager.cs` - Modify RunInference()
- ⏳ `Assets/MultiObjectDetection/SentisInference/Scripts/SentisInferenceRunManager.cs` - Modify RunInference()
- ⏳ `Assets/Segmentation/SegmentationInference/Scripts/SegmentationInferenceRunManager.cs` - Modify RunInference()

---

**All detailed specifications in**: `UDP_NON_BLOCKING_TRANSPORT_PROPOSAL.md`

**Last Updated**: 2026-04-16
