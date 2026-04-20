# Phase 1 Implementation Status

**Date**: 2026-04-16
**Status**: Server-side Complete, Unity-side Pending

---

## ✅ Completed (Server-Side)

### 1. UDP Frame Ingest (`app/transport/udp_ingest.py`)
- Asyncio UDP datagram handler
- Immediate `server_receive_ts` timestamp on packet arrival
- SHA256 payload hash verification
- Duplicate frame detection with TTL cache
- Integrated with existing `BoundedAdmissionQueue`

**Key Features**:
- Frame format: `[Header: 70 bytes][Telemetry: variable][JPEG: variable]`
- Header includes: magic, session_id, frame_id, timestamps, hash
- Stats tracking: total_received, valid_frames, hash_mismatches, duplicates

### 2. Bounded Queue (Already Exists in `app/request_admission.py`)
- Max 3 pending frames
- FIFO drop policy (drops oldest pending when full)
- Separate tracking for pending vs processing frames
- Queue-side drop logging

**Integration**: UDP ingest creates `AdmittedRequest` objects compatible with existing bounded queue.

### 3. Result Cache (`app/cache/result_cache.py`)
- Thread-safe async cache
- Key: `(session_id, frame_id)`
- TTL-based expiration (default 30s)
- Memory-bounded (max 1000 entries)
- Background cleanup task (every 10s)

**Stats**: Tracks hits, misses, expired, evicted, hit_rate

### 4. HTTP Response Endpoint (`app/routes/response.py`)
- `GET /response/{session_id}/{frame_id}`
- Returns 404 if result not ready (Unity polls again)
- Returns 200 with JSON when available
- Additional endpoint: `GET /response/stats` for cache statistics

### 5. Main Integration (`app/main.py`)
- Added UDP listener startup in `@app.on_event("startup")`
- Registered response router
- Started result cache cleanup task
- UDP listening on port 8002
- HTTP responses on port 8001

**Startup Sequence**:
1. GPU warmup (if available)
2. Initialize bounded admission queue
3. Start UDP frame ingest (port 8002)
4. Start result cache cleanup task
5. Register response endpoint router

### 6. Update Workers to Use Result Cache ✅ COMPLETED

Both inference workers now store results in the result cache before returning responses.

**Changes Made**:
- `app/routes/infer_human.py`: Added `result_cache.set()` call at line 944-953
- `app/routes/segmentation.py`: Added `result_cache.set()` call at line 455-461

**Implementation**:
```python
# Store result in cache for UDP polling
from app.cache.result_cache import get_result_cache

result_cache = get_result_cache()

# Convert response to dict for caching (infer_human.py)
response_dict = response.model_dump()
await result_cache.set(session_id, frame_id, response_dict)

# OR: Store response dict directly (segmentation.py)
await result_cache.set(session_id, frame_id, response)
```

---

## 🔄 Pending (Unity-Side)

### 7. Add payload_hash Field to FrameTrace.cs

**File**: `Assets/PassthroughCameraApiSamples/Shared/Scripts/FrameTrace.cs`

**Add**:
```csharp
public string payload_hash;  // SHA256 hash of JPEG payload (Base64-encoded)
```

### 8. Create Shared UDPTransport Utility Class

**File**: `Assets/PassthroughCameraApiSamples/Shared/Scripts/UDPTransport.cs` (NEW)

**Purpose**: Shared UDP send logic for all 3 inference managers

**Key Methods**:
- `void Initialize(string serverIP, int port)` - Setup UDP client
- `void SendFrame(FrameTrace trace, byte[] jpegData)` - Encode header + send UDP
- `byte[] ComputeSHA256(byte[] data)` - Hash computation
- `byte[] SerializeFrameHeader(FrameTrace trace)` - Pack header struct

### 9. Add SendFrameUDP() to All 3 Managers

**Files**:
- `Assets/PassthroughCameraApiSamples/PoseEstimation/Scripts/PoseInferenceRunManager.cs`
- `Assets/PassthroughCameraApiSamples/MultiObjectDetection/SentisInference/Scripts/SentisInferenceRunManager.cs`
- `Assets/PassthroughCameraApiSamples/Segmentation/SegmentationInference/Scripts/SegmentationInferenceRunManager.cs`

**Changes**:
- Modify `RunInference()` to call `SendFrameUDP()` instead of blocking HTTP send
- Frame header includes: magic, session_id, frame_id, unity_send_ts, payload_length, payload_hash
- Telemetry serialized as JSON and included in packet

### 10. Add ListenForResponseHTTP() to All 3 Managers

**Purpose**: Async HTTP polling for inference results

**Implementation**:
```csharp
private IEnumerator ListenForResponseHTTP(int expectedFrameId)
{
    string url = $"{baseUrl}/response/{sessionId}/{expectedFrameId}";
    float timeout = 5f;
    float elapsed = 0f;

    while (elapsed < timeout)
    {
        using (UnityWebRequest req = UnityWebRequest.Get(url))
        {
            yield return req.SendWebRequest();

            if (req.result == UnityWebRequest.Result.Success)
            {
                ProcessResponse(expectedFrameId, req.downloadHandler.text);
                yield break;
            }
        }

        yield return new WaitForSeconds(0.1f);  // Poll every 100ms
        elapsed += 0.1f;
    }

    // Timeout - mark frame as Failed
    MarkFrameAsFailed(expectedFrameId, "Response timeout");
}
```

---

## 📊 Testing Plan

### Phase 1 Validation

**Server-Side Tests**:
1. Start server and verify UDP listener starts on port 8002
2. Verify result cache initializes with TTL=30s, max_size=1000
3. Check `/response/stats` endpoint returns cache statistics

**Integration Tests** (once Unity changes complete):
1. Unity sends UDP frame → Server logs `[UDP INGEST] Frame received`
2. Server logs `server_receive_ts` immediately (before queue wait)
3. Excel logs show `queue_wait_ms` < 5ms (not 101ms)
4. Unity polls `/response/{session_id}/{frame_id}` → Gets result
5. Frame intervals in Unity logs: consistent ~100ms (not variable 300-500ms)

**Expected Improvements**:
| Metric | Before (HTTP blocking) | After (UDP non-blocking) |
|--------|------------------------|--------------------------|
| Actual FPS | 2.6 FPS | 5.0 FPS |
| Upload time estimate | 276ms (noisy) | 50-100ms (accurate) |
| Queue wait | 101ms | <5ms |
| Frames logged per 60s | 150 | 300 |

---

## 📝 Next Steps

### Immediate (Complete Phase 1)
1. ✅ Server UDP ingest - DONE
2. ✅ Server result cache - DONE
3. ✅ Server response endpoint - DONE
4. ✅ Server main.py integration - DONE
5. ✅ Update workers to use result cache - DONE
6. ⏳ Unity FrameTrace.cs modifications - PENDING
7. ⏳ Unity UDPTransport utility - PENDING
8. ⏳ Unity managers SendFrameUDP() - PENDING
9. ⏳ Unity managers ListenForResponseHTTP() - PENDING
10. ⏳ Test and validate - PENDING

### Future (Phase 2+)
- Clock offset estimation (NTP-style 4-timestamp)
- Non-blocking send (remove `yield return` from send path)
- Multi-mode validation (Pose, Detection, Segmentation)
- Performance tuning (queue size, poll interval)

---

## 📂 Files Created/Modified

### Server-Side (Completed)
- ✅ `app/transport/__init__.py` (NEW)
- ✅ `app/transport/udp_ingest.py` (NEW)
- ✅ `app/cache/__init__.py` (NEW)
- ✅ `app/cache/result_cache.py` (NEW)
- ✅ `app/routes/response.py` (NEW)
- ✅ `app/main.py` (MODIFIED - added UDP startup)

### Server-Side (Completed)
- ✅ `app/routes/infer_human.py` (MODIFIED - added result_cache.set() at line 944-953)
- ✅ `app/routes/segmentation.py` (MODIFIED - added result_cache.set() at line 455-461)

### Unity-Side (Pending)
- ⏳ `Assets/Shared/Scripts/FrameTrace.cs` (MODIFY - add payload_hash)
- ⏳ `Assets/Shared/Scripts/UDPTransport.cs` (NEW)
- ⏳ `Assets/PoseEstimation/Scripts/PoseInferenceRunManager.cs` (MODIFY)
- ⏳ `Assets/MultiObjectDetection/SentisInference/Scripts/SentisInferenceRunManager.cs` (MODIFY)
- ⏳ `Assets/Segmentation/SegmentationInference/Scripts/SegmentationInferenceRunManager.cs` (MODIFY)

---

**Last Updated**: 2026-04-16 20:15

---

## 🎉 Server-Side Implementation Complete!

All server-side infrastructure for Phase 1 is now complete:
- ✅ UDP frame ingest (port 8002)
- ✅ Result cache (30s TTL, 1000 entries)
- ✅ HTTP response polling endpoint (/response/{session}/{frame})
- ✅ Workers store results in cache
- ✅ Integrated in main.py startup

**Next Step**: Implement Unity-side changes (5 tasks remaining)
