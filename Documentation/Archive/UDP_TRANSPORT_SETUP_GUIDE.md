# UDP Non-Blocking Transport - Complete Setup Guide

**Status**: Phase 3 Complete - UDP Transport Ready for Testing

---

## Table of Contents

1. [Overview](#overview)
2. [Architecture](#architecture)
3. [Server Setup](#server-setup)
4. [Unity Setup](#unity-setup)
5. [Testing & Validation](#testing--validation)
6. [Troubleshooting](#troubleshooting)

---

## Overview

### What is UDP Non-Blocking Transport?

The UDP transport replaces the blocking HTTP POST flow with a non-blocking UDP + HTTP polling architecture:

**Before (HTTP POST - Blocking)**:
```
Unity → Send POST with JPEG → Wait for response → Process result
        ^^^^^^^^^^^^^^^^^^^^^^
        Blocks Unity main thread (528ms!)
```

**After (UDP + HTTP Polling - Non-Blocking)**:
```
Unity → Send UDP frame (instant) → Continue rendering
        ↓ (background)
        Poll HTTP for result → Process when ready
```

### Performance Improvements

| Metric | Before (HTTP) | After (UDP) | Improvement |
|--------|---------------|-------------|-------------|
| FPS | 2.6 | 5.0+ | +92% |
| Unity blocking | 528ms | 0ms | -100% |
| queue_wait_ms | 101ms | <5ms | -95% |
| Frames/60s | 150 | 300+ | +100% |

---

## Architecture

### Complete Flow Diagram

```
┌─────────────────────────────────────────────────────────────────┐
│ Unity (Quest 3)                                                 │
├─────────────────────────────────────────────────────────────────┤
│                                                                 │
│  Update() {                                                     │
│      if (Time.time >= m_nextInferenceTime) {                   │
│          // Fixed cadence triggering (e.g., every 100ms)       │
│          StartCoroutine(RunInferenceNonBlocking());            │
│      }                                                          │
│  }                                                              │
│                                                                 │
│  RunInferenceNonBlocking() {                                   │
│      1. Encode texture to JPEG                                 │
│      2. Create FrameTrace with hash                            │
│      3. Send UDP frame (instant return)                        │
│      4. Start HTTP polling coroutine (background)              │
│      5. Return immediately (NO blocking!)                      │
│  }                                                              │
│                                                                 │
└───────────┬─────────────────────────────────────────────────────┘
            │ UDP Frame (JPEG + metadata)
            │ Port 8002
            ↓
┌─────────────────────────────────────────────────────────────────┐
│ Server (Python FastAPI)                                         │
├─────────────────────────────────────────────────────────────────┤
│                                                                 │
│  [UDP Listener] (Port 8002)                                    │
│      ↓                                                          │
│  Parse frame, validate hash                                    │
│      ↓                                                          │
│  Add to BoundedAdmissionQueue (max 3 pending)                  │
│                                                                 │
│  [UDP Inference Worker] ← NEW!                                 │
│      ↓                                                          │
│  Pull frame from queue                                         │
│      ↓                                                          │
│  Run AI inference (YOLO, Pose, Depth, Segmentation)           │
│      ↓                                                          │
│  Store result in ResultCache (30s TTL)                         │
│      ↓                                                          │
│  Mark frame complete                                           │
│                                                                 │
│  [HTTP Response Endpoint] (Port 8001)                          │
│      GET /response/{session_id}_{frame_id}                     │
│      Returns cached result or 404 if not ready                 │
│                                                                 │
└───────────┬─────────────────────────────────────────────────────┘
            │ HTTP Response (JSON)
            │ Port 8001
            ↓
┌─────────────────────────────────────────────────────────────────┐
│ Unity - HTTP Polling Coroutine (Background)                    │
├─────────────────────────────────────────────────────────────────┤
│                                                                 │
│  ListenForResponseHTTP(frame_id) {                             │
│      while (!result_ready && !timeout) {                       │
│          response = GET /response/{session}_{frame}            │
│          if (response.status == 200) {                         │
│              ProcessServerResponse(response);                  │
│              break;                                            │
│          }                                                      │
│          yield return WaitForSeconds(0.05); // 50ms poll       │
│      }                                                          │
│  }                                                              │
│                                                                 │
└─────────────────────────────────────────────────────────────────┘
```

### Key Components

**Unity Side (Phase 3 Complete)**:
- `UDPTransport.cs` - UDP client utility
- `FrameTrace.cs` - Frame metadata with hash
- `*InferenceRunManager.cs` - Fixed cadence triggering in Update()
  - `RunInferenceNonBlocking()` - Non-blocking send + async poll
  - `ListenForResponseHTTP()` - Background polling coroutine

**Server Side (Phase 3 Complete)**:
- `app/transport/udp_ingest.py` - UDP listener (port 8002)
- `app/workers/udp_inference_worker.py` - **NEW!** Background inference worker
- `app/cache/result_cache.py` - Result cache (30s TTL)
- `app/routes/response.py` - HTTP polling endpoint (port 8001)

---

## Server Setup

### Prerequisites

- Python 3.8+
- vision_server repository cloned to `C:\Repo\Github\vision_server`
- Dependencies installed (see requirements.txt)

### Step 1: Verify UDP Worker Implementation

Ensure these files exist in the server:

```
C:\Repo\Github\vision_server\
├── app\
│   ├── main.py                              # UDP worker startup added
│   ├── workers\
│   │   ├── __init__.py                      # NEW
│   │   └── udp_inference_worker.py          # NEW - Background worker
│   ├── transport\
│   │   └── udp_ingest.py                    # Phase 1 - UDP listener
│   ├── cache\
│   │   └── result_cache.py                  # Phase 1 - Result cache
│   └── routes\
│       └── response.py                      # Phase 1 - HTTP polling endpoint
```

### Step 2: Start the Server

**Single Worker Mode (Recommended for Development)**:

```bash
cd C:\Repo\Github\vision_server
python -m uvicorn app.main:app --host 0.0.0.0 --port 8001 --workers 1
```

**Expected Output**:

```
[YOLO] Model ready: yolov8n (80 classes, person at index 0)
[POSE] Keypoint R-CNN loaded successfully!
[SEGMENTATION] YOLO11n-seg model loaded successfully

============================================================
BOUNDED ADMISSION QUEUE - Initialized
============================================================
  Max pending frames: 3
  Drop policy: FIFO (oldest pending dropped when full)
============================================================

============================================================
UDP FRAME INGEST - Started
============================================================
  Listening on: 0.0.0.0:8002
  Max frame size: 512.0 KB
  Deduplication TTL: 5s
============================================================

============================================================
RESULT CACHE - Initialized
============================================================
  TTL: 30s
  Max size: 1000
  Cleanup interval: 10s
============================================================

============================================================
UDP INFERENCE WORKER - Started
============================================================
  Worker will process UDP frames from admission queue
  Inference results stored in result cache
  Unity polls via GET /response/{session_id}_{frame_id}
============================================================

INFO:     Uvicorn running on http://0.0.0.0:8001
[UDP WORKER] Worker loop started, waiting for UDP frames...
```

### Step 3: Verify Ports

```bash
# Check UDP listener
netstat -an | findstr 8002
# Should show: UDP    0.0.0.0:8002           *:*

# Check HTTP server
netstat -an | findstr 8001
# Should show: TCP    0.0.0.0:8001           LISTENING
```

### Step 4: Test Server Health

```bash
curl http://localhost:8001/
# Should return: {"status":"ok","service":"Human Inference API",...}

curl http://localhost:8001/response/stats
# Should return: {"total_set":0,"hits":0,"misses":0,...}
```

---

## Unity Setup

### Prerequisites

- Unity 6000.0.38f1+ (or Unity 2022.3+)
- Quest 3 device connected via USB (adb enabled)
- Assets/PassthroughCameraApiSamples project open

### Step 1: Verify Unity Files

Ensure Phase 3 implementation is complete in these files:

```
Assets/PassthroughCameraApiSamples/
├── Shared/
│   ├── Scripts/
│   │   ├── UDPTransport.cs                  # Phase 1
│   │   ├── FrameTrace.cs                    # Phase 1 (with payload_hash)
│   │   └── InferenceConfig.cs               # Configuration
│   └── Resources/
│       └── ServerConfig.asset                # Server IP config
├── Segmentation/
│   └── SegmentationInference/
│       └── Scripts/
│           └── SegmentationInferenceRunManager.cs  # Phase 3 complete
├── PoseEstimation/
│   └── Scripts/
│       └── PoseInferenceRunManager.cs        # Phase 3 complete
└── MultiObjectDetection/
    └── SentisInference/
        └── Scripts/
            └── SentisInferenceRunManager.cs  # Phase 3 complete
```

### Step 2: Configure Server IP

**Option 1: Using Editor Tool (Recommended)**:

1. Open Unity Editor
2. Navigate to **Tools → Passthrough Camera → Server Config Editor**
3. Enter your server IP: `192.168.0.135` (or your PC's IP)
4. Port: `8001`
5. Click **Save Configuration**

**Option 2: Using Inspector**:

1. Select: `Assets/Resources/ServerConfig.asset`
2. Set **Server IP**: `192.168.0.135`
3. Set **Server Port**: `8001`
4. Save (Ctrl+S)

### Step 3: Enable UDP Transport

For each scene you want to test:

1. **Segmentation Scene**: `Assets/PassthroughCameraApiSamples/Segmentation/Segmentation.unity`
   - Select **SegmentationInferenceManager** in hierarchy
   - Check ✓ **Use UDP Transport** in Inspector

2. **Pose Estimation Scene**: `Assets/PassthroughCameraApiSamples/PoseEstimation/PassthroughPoseEstimation.unity`
   - Select **SentisInferenceManagerPrefab** in hierarchy
   - Check ✓ **Use UDP Transport** in Inspector

3. **MultiObjectDetection Scene**: `Assets/PassthroughCameraApiSamples/MultiObjectDetection/MultiObjectDetection.unity`
   - Select **SentisInferenceManagerPrefab** in hierarchy
   - Check ✓ **Use UDP Transport** in Inspector

### Step 4: Configure Inference Settings

In Inspector for the InferenceRunManager:

```
Inference Config:
├─ Use Server Inference: ✓
├─ Use UDP Transport: ✓           ← Enable UDP
├─ Mode: Both (or Detection/Pose/Depth)
├─ Target FPS: 10                  ← Fixed cadence (100ms intervals)
└─ Use Server Config: ✓
```

### Step 5: Build & Deploy

1. **File → Build Settings**
2. **Platform**: Android
3. **Texture Compression**: ASTC
4. **Build And Run** (Quest 3 connected via USB)

---

## Testing & Validation

### Unity Logs (Expected Output)

**Connect via adb**:
```bash
adb logcat -s Unity | findstr "PHASE\|UDP"
```

**Expected logs**:

```
[PHASE 3] Start() complete - inference now driven by Update() at fixed cadence
[PHASE 3] Triggered inference at fixed cadence (interval=100ms, next=12.34)
[PHASE 3] Frame 1 created, hash=a1b2c3d4..., size=9238 bytes
[UDP SEND] Frame sessionid_1 sent, size=9308 bytes
[UDP SEND] Frame 1 sent to 192.168.0.135:8002
[UDP POLL] Starting polling for frame 1
[PHASE 3] Frame 1 sent via UDP, listener started

[UDP POLL] Attempt 1/100 for frame 1
[UDP POLL] Frame 1 received after 0.25s
[UDP PROCESS] Processing server response for frame 1
[UDP SUCCESS] Frame 1 completed successfully
```

### Server Logs (Expected Output)

```
[UDP INGEST] Frame parsed: session=abc123, frame_id=1, size=9238
[UDP INGEST] SHA256 verified: a1b2c3d4... ✓
[UDP WORKER] Processing abc123_1 (queue_wait=2.3ms, mode=both)
[UDP WORKER] Decoding image: 1280x720, mode=both
[UDP WORKER mode=detection] YOLO detected 1 person(s)
[UDP WORKER mode=both] Pose on 1 crops, detected 1 person(s)
[UDP WORKER] Inference complete: 245.8ms
[UDP WORKER] Completed abc123_1 (processing=246.1ms, total=248.4ms)
[RESULT CACHE] Stored result for abc123_1, cache_size=1/1000
```

### Verify Performance Metrics

Check Excel logs or telemetry output:

**Expected improvements**:
- `queue_wait_ms`: **< 5ms** (was 101ms)
- `frame_interval_ms`: **~100ms** (consistent fixed cadence)
- `server_processing_ms`: **200-300ms** (unchanged, inference time)
- `e2e_ms`: **250-350ms** (UDP send + inference + poll)

---

## Troubleshooting

### Issue 1: Unity Logs Show No UDP Activity

**Symptom**:
```
No logs with [PHASE 3] or [UDP] prefix
```

**Diagnosis**:
- UDP transport not enabled in Inspector
- InferenceRunManager not using Phase 3 code

**Solution**:
1. Check **Use UDP Transport** checkbox in Inspector
2. Verify `m_useUDPTransport` field exists in script (Phase 3 implementation)
3. Rebuild and redeploy to Quest 3

---

### Issue 2: Server Shows "Worker loop started" but No Processing

**Symptom**:
```
[UDP WORKER] Worker loop started, waiting for UDP frames...
(no further logs)
```

**Diagnosis**:
- Unity not sending UDP frames
- Firewall blocking port 8002
- Wrong server IP configured in Unity

**Solution**:
1. Verify Unity logs show `[UDP SEND]` messages
2. Check firewall allows UDP port 8002:
   ```bash
   # Windows: Firewall → Advanced Settings → Inbound Rules
   # Allow UDP port 8002
   ```
3. Verify server IP in Unity matches PC IP:
   ```bash
   ipconfig  # Get PC IP
   # Should match ServerConfig.asset in Unity
   ```

---

### Issue 3: HTTP Polling Always Returns 404

**Symptom**:
```
[UDP POLL] Attempt 1/100 for frame 1
[UDP POLL] Attempt 2/100 for frame 1
...
[UDP POLL] Frame 1 timed out after 5.0s
```

**Diagnosis**:
- UDP worker not processing frames
- Inference failing
- Result cache not storing results

**Solution**:
1. Check server logs for `[UDP WORKER]` processing messages
2. Check for inference errors in server logs
3. Verify result cache initialized:
   ```bash
   curl http://192.168.0.135:8001/response/stats
   # Should show: total_set > 0 after frames sent
   ```

---

### Issue 4: Performance Not Improved

**Symptom**:
```
FPS still ~2.6, queue_wait_ms still ~101ms
```

**Diagnosis**:
- Still using HTTP POST mode (UDP not enabled)
- Incorrect mode configuration

**Solution**:
1. Verify `m_useUDPTransport == true` in Unity Inspector
2. Check Unity logs for `[PHASE 3]` prefix (confirms Phase 3 code running)
3. Ensure server UDP worker is running (check logs for worker startup message)

---

### Issue 5: Frames Getting Dropped

**Symptom**:
```
[ADMISSION] Frame abc123_25 dropped (queue full)
```

**Diagnosis**:
- Inference too slow for frame rate
- Queue size too small (max 3 pending)

**Solution**:
1. Reduce Unity target FPS:
   ```
   Inspector → Inference Config → Target FPS: 5 (instead of 10)
   ```
2. Check server performance:
   - CPU inference is slow (200-1300ms)
   - Consider using GPU (CUDA) for faster inference (10-50ms)

---

## Comparing HTTP vs UDP Modes

### HTTP POST Mode (Original)

**Enable**:
- Uncheck **Use UDP Transport** in Inspector
- Keep **Use Server Inference** checked

**Flow**:
```
Unity → POST /infer_human → Wait → Process response
```

**Logs**:
```
[SERVER POST] Uploading to http://...
[SERVER RECV] Response received, length=...
[SERVER PROCESS] Processing server response...
```

### UDP Transport Mode (New)

**Enable**:
- Check **Use UDP Transport** in Inspector
- Keep **Use Server Inference** checked

**Flow**:
```
Unity → UDP frame → Continue → Poll → Process when ready
```

**Logs**:
```
[PHASE 3] Triggered inference at fixed cadence
[UDP SEND] Frame X sent to ...
[UDP POLL] Starting polling for frame X
[UDP POLL] Frame X received after 0.25s
```

---

## Performance Testing Checklist

- [ ] Server UDP worker started successfully
- [ ] Unity UDP transport enabled in Inspector
- [ ] Unity logs show `[PHASE 3]` and `[UDP SEND]` messages
- [ ] Server logs show `[UDP WORKER]` processing messages
- [ ] HTTP polling returns 200 (not 404) within ~250ms
- [ ] `queue_wait_ms` < 5ms in Excel logs
- [ ] Frame intervals ~100ms (consistent fixed cadence)
- [ ] FPS improved (2.6 → 5.0+)
- [ ] No Unity main thread blocking

---

## Next Steps

### Phase 4: Multi-Mode Validation (Optional)

Test all inference modes with UDP transport:
- Detection only (`mode=detection`)
- Pose only (`mode=pose`)
- Both (`mode=both`)
- Depth (`mode=depth`)

### Phase 5: Performance Tuning (Optional)

Optimize for Quest 3:
- Adjust poll interval (default 50ms)
- Tune timeout values (default 5s)
- Optimize JPEG quality vs size
- GPU inference (CUDA) for faster processing

---

## Summary

**Phase 3 Complete** ✓

**Server Changes**:
- ✅ `app/workers/udp_inference_worker.py` created
- ✅ `app/main.py` starts UDP worker on startup
- ✅ Worker pulls frames from admission queue
- ✅ Worker runs inference and caches results

**Unity Changes** (Already Complete from Previous Phase):
- ✅ Fixed cadence triggering in Update()
- ✅ Non-blocking `RunInferenceNonBlocking()`
- ✅ Background HTTP polling coroutine
- ✅ Feature flag: `m_useUDPTransport`

**Expected Results**:
- 🚀 FPS: 2.6 → 5.0+ (+92%)
- ⚡ queue_wait: 101ms → <5ms (-95%)
- 📈 Consistent 100ms frame cadence
- ✅ Zero Unity main thread blocking

**Testing Status**:
- Server: Ready for testing (UDP worker implemented)
- Unity: Ready for testing (Phase 3 complete)
- Integration: Deploy to Quest 3 and validate

---

**Last Updated**: 2026-04-16 23:00
**Version**: Phase 3 Complete
