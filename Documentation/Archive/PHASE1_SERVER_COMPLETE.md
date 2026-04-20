# Phase 1 - Server-Side Implementation COMPLETE ✅

**Date**: 2026-04-16
**Status**: All server components ready for testing

---

## 🎉 What's Been Built

### 1. UDP Frame Ingest (Port 8002)

**File**: `C:\Repo\Github\vision_server\app\transport\udp_ingest.py`

- Asyncio UDP datagram handler
- **Immediate timestamping** on packet arrival (fixes 101ms queue_wait bug)
- SHA256 payload verification
- Duplicate frame detection (TTL-based cache)
- Integrates with existing BoundedAdmissionQueue

**Key Features**:
- Frame format: `[Header: 70 bytes][Telemetry: variable][JPEG: variable]`
- Records `server_receive_ts` immediately (before any processing)
- Creates `AdmittedRequest` objects compatible with existing queue
- Stats tracking: total_received, valid_frames, hash_mismatches, duplicates

### 2. Result Cache

**File**: `C:\Repo\Github\vision_server\app\cache\result_cache.py`

- Thread-safe async cache
- Key: `(session_id, frame_id)`
- TTL: 30 seconds (configurable)
- Max size: 1000 entries
- Background cleanup task (runs every 10s)

**Stats Tracked**:
- total_set, total_get
- hits, misses
- expired, evicted
- hit_rate

### 3. HTTP Response Endpoint

**File**: `C:\Repo\Github\vision_server\app\routes\response.py`

**Endpoints**:
- `GET /response/{session_id}/{frame_id}` - Poll for inference result
  - Returns 404 if not ready
  - Returns 200 with JSON when available
- `GET /response/stats` - Cache statistics

### 4. Worker Integration

**Files Modified**:
- `app/routes/infer_human.py` (line 944-953)
- `app/routes/segmentation.py` (line 455-461)

Both workers now call `result_cache.set()` after inference completes, storing results for Unity to poll via HTTP.

### 5. Main App Integration

**File**: `C:\Repo\Github\vision_server\app\main.py`

**Startup Sequence** (added to `@app.on_event("startup")`):
1. GPU warmup
2. Initialize bounded admission queue
3. **Start UDP frame ingest** (port 8002)
4. **Start result cache cleanup task**
5. Register response endpoint router

---

## 📊 Expected Behavior

### Server Logs on Startup

```
==============================================================
UDP FRAME INGEST - Started
==============================================================
  Listening on: 0.0.0.0:8002
  Max frame size: 2048.0 KB
  Deduplication TTL: 60s
==============================================================

==============================================================
RESULT CACHE - Initialized
==============================================================
  TTL: 30.0s
  Max size: 1000
  Cleanup interval: 10s
==============================================================
```

### Server Logs on Frame Receipt (UDP)

```
[UDP INGEST] Received 125678 bytes from ('192.168.0.100', 54321)
[UDP INGEST] Frame parsed: session=abc123de_1, frame_id=42
[UDP INGEST] SHA256 verified ✓
[UDP INGEST] Enqueued frame abc123de_1 to bounded queue
```

### Server Logs on Inference Complete

```
[PROCESSING START] abc123de_1 (queue_wait=2.3ms)
[API] Processing image: 1280x960
[API mode=both] YOLO detected 1 person(s)
[API mode=both] Pose estimation complete: 1 person(s)
[RESULT CACHE] Stored result for abc123de_1, cache_size=5/1000
[PROCESSING COMPLETE] abc123de_1 (total=245.7ms)
```

### Server Logs on Unity Poll

```
[RESPONSE] Serving result for abc123de_1
```

---

## 🧪 How to Test (Before Unity Changes)

### 1. Start Server

```bash
cd C:\Repo\Github\vision_server
python -m uvicorn app.main:app --host 0.0.0.0 --port 8001 --workers 1
```

### 2. Verify UDP Listener

```bash
netstat -an | findstr 8002
```

**Expected output**:
```
UDP    0.0.0.0:8002           *:*
```

### 3. Check Cache Stats

```bash
curl http://localhost:8001/response/stats
```

**Expected output**:
```json
{
  "total_set": 0,
  "total_get": 0,
  "hits": 0,
  "misses": 0,
  "expired": 0,
  "evicted": 0,
  "cache_size": 0,
  "hit_rate": 0.0
}
```

### 4. Test Response Endpoint (Simulate Missing Frame)

```bash
curl http://localhost:8001/response/test-session/999
```

**Expected output** (404):
```json
{
  "detail": "Result not ready for session=test-ses, frame=999"
}
```

---

## 🔄 What Happens When Unity Sends UDP Frames

**Timeline** (expected after Unity implementation):

```
T+0ms   : Unity sends UDP packet to port 8002
T+1ms   : Server receives packet, records server_receive_ts IMMEDIATELY
T+2ms   : SHA256 hash verified, frame enqueued
T+3ms   : Frame admitted to bounded queue (max 3 pending)
T+5ms   : Worker picks up frame (queue_wait = 5ms - 0ms = 5ms) ✓ CLEAN!
T+255ms : Inference completes, result stored in cache
T+256ms : Unity polls /response/{session}/{frame}
T+257ms : Server returns 200 with JSON result
```

**Before (HTTP blocking)**:
- `server_receive_ts` recorded after HTTP parsing (late timestamp)
- `queue_wait_ms` = 101ms (incorrect - includes HTTP overhead)

**After (UDP non-blocking)**:
- `server_receive_ts` recorded on packet arrival (clean timestamp)
- `queue_wait_ms` = 3-5ms (correct - pure queue wait time)

---

## 📁 Files Created/Modified Summary

### Created
- `app/transport/__init__.py`
- `app/transport/udp_ingest.py` (~400 lines)
- `app/cache/__init__.py`
- `app/cache/result_cache.py` (~170 lines)
- `app/routes/response.py` (~65 lines)

### Modified
- `app/main.py` (lines 213-237: UDP startup integration)
- `app/routes/infer_human.py` (lines 944-953: result cache storage)
- `app/routes/segmentation.py` (lines 455-461: result cache storage)

---

## ⚠️ Important Notes

### Compatibility

**The server is BACKWARD COMPATIBLE**:
- Old HTTP-based Unity clients continue to work (returns response immediately)
- New UDP-based Unity clients can poll via `/response/` endpoint
- Both can coexist during migration

### No Breaking Changes

- Existing HTTP endpoints unchanged
- Existing telemetry/logging unchanged
- Existing bounded queue logic unchanged
- Only ADDED new UDP listener and result cache

### Performance Impact

**Minimal overhead**:
- UDP listener runs in asyncio (non-blocking)
- Result cache uses async locks (no blocking)
- Cache cleanup runs in background (every 10s)
- Memory bounded (max 1000 entries, 30s TTL)

---

## 🚀 Next Steps

### Ready for Unity Implementation

Now that server-side is complete, the Unity client needs 5 changes:

1. **FrameTrace.cs**: Add `payload_hash` field
2. **UDPTransport.cs**: Create shared UDP send utility
3. **PoseInferenceRunManager.cs**: Add `SendFrameUDP()` and `ListenForResponseHTTP()`
4. **SentisInferenceRunManager.cs**: Same as above
5. **SegmentationInferenceRunManager.cs**: Same as above

See: `PHASE1_REMAINING_TASKS.md` for detailed Unity implementation steps.

---

## 🐛 Troubleshooting

### "UDP listener not starting"

**Check**:
```bash
# Windows: Check if port 8002 is already in use
netstat -ano | findstr 8002

# Kill process if needed
taskkill /PID <PID> /F
```

### "Result cache not working"

**Check server logs for**:
```
[RESULT CACHE] Initialized  ← Should appear on startup
[RESULT CACHE] Stored result for... ← Should appear after inference
```

### "404 when polling /response/{session}/{frame}"

**Possible reasons**:
1. Frame not yet processed (keep polling)
2. Frame expired (30s TTL)
3. Session/frame ID mismatch
4. Worker didn't call `result_cache.set()` (check logs)

---

**Last Updated**: 2026-04-16 20:20
**Status**: ✅ Server-side READY FOR TESTING
