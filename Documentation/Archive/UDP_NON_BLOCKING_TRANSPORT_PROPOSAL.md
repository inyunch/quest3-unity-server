# Implementation-Aware Proposal: Non-Blocking UDP Transport with Frame Identity Guarantee

**Date**: 2026-04-16
**Status**: Approved for Implementation
**Target**: Unity-PassthroughCameraApiSamples + vision_server

---

## 1. Current Problem Interpretation

### Identified Issues

**Unity Side:**
- **Blocking send**: `yield return request.SendWebRequest()` blocks until response completes
- **Actual vs Target FPS**: Configured 5 FPS, achieving only 2.6 FPS due to 386ms E2E latency
- **Upload time measurement noise**: HTTP request includes transport establishment, TLS handshake, request headers, response wait - cannot isolate pure upload time

**Server Side:**
- **Late receive timestamp**: Current `server_receive_ts = time.time()` happens inside FastAPI handler, after:
  - TCP accept
  - HTTP parsing
  - Request routing
  - Multipart/form-data parsing
- **Coupled processing**: Receive, queue, process all happen in one HTTP handler - no separation
- **Noisy queue_wait_ms**: Currently showing 101ms average, but should be <5ms with 1 worker

**Telemetry:**
- **N+1 delay pattern works** but frame identity relies on sequential delivery
- **No application-level frame verification** - assumes HTTP ensures integrity
- **Upload time estimation impossible** without clock sync

### Root Cause
The current HTTP-based synchronous request/response model couples transport, queueing, and processing into one blocking operation, preventing:
1. Fixed-cadence sending
2. Clean receive timestamps
3. Accurate upload time measurement
4. Independent queue management

---

## 2. Proposed Unity Non-Blocking Send Changes

### 2.1 Core Design: Fire-and-Forget UDP + Async HTTP Response Listener

**Keep existing code structure**, add parallel UDP send path:

```csharp
// EXISTING: PoseInferenceRunManager.cs line 126-133
while (true)
{
    while (m_uiMenuManager.IsPaused)
    {
        yield return null;
    }
    yield return RunInference();  // MODIFY THIS
}
```

**Proposed modification**:

```csharp
private IEnumerator RunInference()
{
    // 1. Capture frame (UNCHANGED)
    Texture targetTexture = m_cameraAccess.GetTexture();

    // 2. Encode JPEG (UNCHANGED)
    byte[] jpegData = EncodeToJPEG(targetTexture);

    // 3. Create FrameTrace (NEW: add payload hash)
    FrameTrace trace = new FrameTrace
    {
        session_id = m_sessionId,
        frame_id = ++m_frameId,
        unity_send_ts = UnixMilliseconds(),
        payload_hash = ComputeSHA256(jpegData)  // NEW
    };

    // 4. Send via UDP (NEW: non-blocking)
    SendFrameUDP(trace, jpegData);

    // 5. Poll for async response (NEW: separate coroutine)
    StartCoroutine(ListenForResponseHTTP(trace.frame_id));

    // 6. Throttle to targetFPS (UNCHANGED)
    float interval = 1f / m_inferenceConfig.targetFPS;
    yield return new WaitForSeconds(interval);
}
```

### 2.2 UDP Send Implementation

```csharp
private UdpClient m_udpClient;
private const int UDP_FRAME_PORT = 8002;  // New port for frame ingestion

private void SendFrameUDP(FrameTrace trace, byte[] jpegData)
{
    // Frame header (32 bytes)
    FrameHeader header = new FrameHeader
    {
        magic = 0xF2AE1234,  // Frame magic number
        session_id = trace.session_id.ToByteArray(),  // 16 bytes (GUID)
        frame_id = trace.frame_id,  // 4 bytes
        unity_send_ts = trace.unity_send_ts,  // 8 bytes
        payload_length = jpegData.Length,  // 4 bytes
        payload_hash = trace.payload_hash,  // 32 bytes (SHA256)

        // Telemetry from previous frame (N-1 delayed pattern)
        prev_frame_telemetry = SerializePrevFrameTelemetry()  // Variable length
    };

    // Serialize: [header][telemetry][jpeg_data]
    byte[] packet = CombineHeaderAndPayload(header, jpegData);

    // Send (non-blocking)
    m_udpClient.BeginSend(packet, packet.Length, m_serverEndpoint, null, null);

    Debug.Log($"[UDP SEND] Frame {trace.frame_id} sent, size={packet.Length}");
}
```

### 2.3 Async HTTP Response Listener

**Keep HTTP for responses** (reliable delivery, existing JSON parsing):

```csharp
private IEnumerator ListenForResponseHTTP(int expectedFrameId)
{
    // Poll HTTP endpoint: GET /response/{session_id}/{frame_id}
    string url = $"{m_baseUrl}/response/{m_sessionId}/{expectedFrameId}";

    // Timeout: 5 seconds (if no response, mark as Failed)
    float timeout = 5f;
    float elapsed = 0f;

    while (elapsed < timeout)
    {
        using (UnityWebRequest req = UnityWebRequest.Get(url))
        {
            yield return req.SendWebRequest();

            if (req.result == UnityWebRequest.Result.Success)
            {
                // Response received
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
```

### 2.4 Three-Mode Compatibility

**All three managers share the same base pattern**:

| File | Current Line | Change Required |
|------|-------------|-----------------|
| `PoseInferenceRunManager.cs` | 136-188 | Extract `RunInference()` → `SendFrameUDP()` + `ListenForResponseHTTP()` |
| `SentisInferenceRunManager.cs` | Similar | Same pattern |
| `SegmentationInferenceRunManager.cs` | Similar | Same pattern |

**Shared UDP client** can be moved to `InferenceConfig` or a new `UDPTransport.cs` utility class.

---

## 3. Proposed UDP Ingest Layer Design

### 3.1 Server Architecture Overview

```
┌─────────────────────────────────────────────────────────────┐
│ Unity (Quest 3)                                             │
│                                                             │
│  [Capture] → [Encode] → [UDP Send] ────┐                   │
│                            │            │                   │
│                            │            ↓                   │
│                            │     [HTTP Poll /response]      │
└────────────────────────────┼─────────────┬──────────────────┘
                             │             │
                         UDP Frame      HTTP Response
                             │             │
┌────────────────────────────┼─────────────┼──────────────────┐
│ Server                     ↓             ↑                   │
│                                                              │
│  [UDP Listener:8002] ──→ [Frame Parser] ──→ [Bounded Queue] │
│                              │                    │          │
│                              │                    ↓          │
│                        [Verify Hash]      [Worker Thread]   │
│                              │                    │          │
│                              ↓                    ↓          │
│                     [server_receive_ts]   [Inference]       │
│                                                   │          │
│                                                   ↓          │
│                                          [Result Cache]      │
│                                                   │          │
│                                                   ↓          │
│  [HTTP /response] ←────────────────────── [JSON Response]   │
│                                                              │
└──────────────────────────────────────────────────────────────┘
```

### 3.2 UDP Listener Implementation

**New file**: `app/transport/udp_ingest.py`

```python
import asyncio
import struct
import hashlib
from dataclasses import dataclass
from typing import Optional
import time

@dataclass
class FrameHeader:
    magic: int
    session_id: str
    frame_id: int
    unity_send_ts: float  # milliseconds
    payload_length: int
    payload_hash: bytes
    prev_telemetry: dict  # Serialized previous frame telemetry

class UDPFrameIngest:
    def __init__(self, port: int = 8002, max_frame_size: int = 5 * 1024 * 1024):
        self.port = port
        self.max_frame_size = max_frame_size
        self.frame_queue = None  # Will be set to bounded queue

    async def start(self):
        """Start UDP listener"""
        self.transport, self.protocol = await asyncio.get_event_loop().create_datagram_endpoint(
            lambda: UDPFrameProtocol(self),
            local_addr=('0.0.0.0', self.port)
        )
        print(f"[UDP INGEST] Listening on port {self.port}")

    def parse_frame(self, data: bytes) -> Optional[tuple]:
        """Parse incoming frame packet"""
        # 1. Record receive timestamp IMMEDIATELY
        server_receive_ts = time.time() * 1000  # milliseconds

        # 2. Parse header (fixed 32 bytes + variable telemetry)
        if len(data) < 64:
            print(f"[UDP INGEST] Packet too short: {len(data)} bytes")
            return None

        magic, = struct.unpack('!I', data[0:4])
        if magic != 0xF2AE1234:
            print(f"[UDP INGEST] Invalid magic: {magic:08x}")
            return None

        session_bytes = data[4:20]
        session_id = uuid.UUID(bytes=session_bytes)

        frame_id, = struct.unpack('!I', data[20:24])
        unity_send_ts, = struct.unpack('!Q', data[24:32])
        payload_length, = struct.unpack('!I', data[32:36])

        # Hash starts at offset 36, length 32 bytes
        expected_hash = data[36:68]

        # Telemetry length (2 bytes)
        telemetry_len, = struct.unpack('!H', data[68:70])
        telemetry_data = data[70:70+telemetry_len]

        # JPEG payload
        payload_start = 70 + telemetry_len
        jpeg_data = data[payload_start:]

        # 3. Verify payload integrity
        actual_hash = hashlib.sha256(jpeg_data).digest()
        if actual_hash != expected_hash:
            print(f"[UDP INGEST] Hash mismatch for frame {session_id}_{frame_id}")
            return None

        # 4. Verify payload length
        if len(jpeg_data) != payload_length:
            print(f"[UDP INGEST] Length mismatch: expected {payload_length}, got {len(jpeg_data)}")
            return None

        print(f"[UDP INGEST] Frame {session_id}_{frame_id} received, size={len(jpeg_data)}, queue_wait will be calculated from {server_receive_ts:.3f}")

        return (session_id, frame_id, unity_send_ts, server_receive_ts, telemetry_data, jpeg_data)
```

### 3.3 Integration with Existing Code

**Modify**: `app/main.py`

```python
from app.transport.udp_ingest import UDPFrameIngest

# Initialize UDP ingest
udp_ingest = UDPFrameIngest(port=8002)

@app.on_event("startup")
async def startup_udp():
    await udp_ingest.start()
    # Connect to bounded queue
    udp_ingest.frame_queue = get_bounded_queue()  # Existing queue from Phase 2
```

**Key integration points**:
1. UDP listener → Bounded Queue (existing)
2. Bounded Queue → Worker (existing)
3. Worker → Inference (existing: `app/routes/infer_human.py`)
4. Worker → Result Cache (NEW)
5. Result Cache → HTTP Response (NEW: `app/routes/response.py`)

---

## 4. Same-Frame Identity Guarantee

### 4.1 Unique Frame Key

**Primary Key**: `(session_id, frame_id)`

- `session_id`: GUID generated at Unity app start, persists for entire recording session
- `frame_id`: Sequential counter starting from 1, resets per session

**Enforcement across lifecycle**:

```
Unity Capture
  ↓ (session_id, frame_id) embedded in frame header
UDP Send
  ↓ (session_id, frame_id) parsed from header
Server Receive
  ↓ (session_id, frame_id) enqueued with frame
Bounded Queue
  ↓ (session_id, frame_id) passed to worker
Inference Worker
  ↓ (session_id, frame_id) stored in result cache
Result Cache
  ↓ (session_id, frame_id) key for HTTP response lookup
HTTP Response
  ↓ (session_id, frame_id) returned to Unity
Unity Display/Drop
  ↓ (session_id, frame_id) logged in telemetry
Excel Export
```

### 4.2 Payload Verification

**SHA256 hash** (32 bytes) included in frame header.

**Verification points**:
1. **UDP receive**: Hash mismatch → drop frame immediately, log corruption
2. **Queue enqueue**: Already verified, safe to queue
3. **Telemetry export**: Log `payload_verified=true/false`

**New FrameTrace field**:
```csharp
public string payload_hash;  // Base64-encoded SHA256
```

**New Excel column**:
```
payload_verified | true/false | Was payload hash verified on receive?
```

### 4.3 Duplicate Detection

**Problem**: UDP may deliver duplicates.

**Solution**: Server maintains a **frame deduplication cache**:

```python
# app/transport/udp_ingest.py
class UDPFrameIngest:
    def __init__(self):
        self.received_frames = {}  # Key: (session_id, frame_id), Value: receive_ts
        self.cache_ttl = 30  # seconds

    def is_duplicate(self, session_id, frame_id, receive_ts) -> bool:
        key = (session_id, frame_id)
        if key in self.received_frames:
            print(f"[UDP INGEST] Duplicate frame {key}, ignoring")
            return True
        self.received_frames[key] = receive_ts
        # Cleanup old entries
        self._cleanup_cache(receive_ts)
        return False
```

---

## 5. Definition of server_receive_ts

### 5.1 Recommended Definition

**server_receive_ts = Timestamp when UDP packet containing the complete frame is received and successfully parsed**

**Justification**:
- **One frame = one UDP datagram**: Max JPEG size ~500KB fits in single datagram (UDP max 65507 bytes, but Ethernet MTU 1500 bytes causes fragmentation at IP layer, handled transparently by OS)
- **No application-level reassembly**: OS reassembles IP fragments before delivering to application
- **Clear telemetry meaning**: "When did the server application first see this complete frame?"

### 5.2 Multi-Packet Fragmentation (If Needed)

**If frame size exceeds single datagram limit** (e.g., Segmentation with large masks):

**Option A**: Application-level chunking

```python
# Frame spans N packets, each with sequence number
# server_receive_ts = timestamp when LAST required packet received
```

**Option B**: Fallback to HTTP for large frames

```python
# If payload > 64KB, send via HTTP instead of UDP
# UDP only for small frames (<64KB)
```

**Recommendation**: Start with **Option B** (simpler), add Option A only if needed.

### 5.3 Additional Timestamp Fields

**Keep it simple for Phase 1**:
- `server_receive_ts`: When UDP packet received (one timestamp, clear definition)
- `server_process_start_ts`: When inference starts (existing)
- `server_send_ts`: When response ready (existing)

**Future enhancement** (if multi-packet reassembly added):
- `server_first_packet_ts`: First fragment received
- `server_frame_complete_ts`: Last fragment received
- Use `server_frame_complete_ts` as primary `server_receive_ts`

---

## 6. Clock Offset / Upload Time Estimation Method

### 6.1 Problem Statement

- Unity clock: `unity_send_ts` (Quest 3 monotonic time)
- Server clock: `server_receive_ts` (Linux server time)
- **Not synchronized**, offset unknown

**Current calculation** (incorrect):
```
upload_ms = server_receive_ts - unity_send_ts  // WRONG: assumes sync
```

### 6.2 Proposed Solution: NTP-Style Four-Timestamp RTT Estimation

**Collect four timestamps per frame**:

```
T1 = unity_send_ts          (Unity clock)
T2 = server_receive_ts      (Server clock)
T3 = server_send_ts         (Server clock)
T4 = unity_receive_ts       (Unity clock)
```

**Calculate**:
```
RTT = (T4 - T1) - (T3 - T2)  // Round-trip time (Unity clock)
clock_offset = ((T2 - T1) + (T3 - T4)) / 2  // Estimated offset
upload_estimate = (T2 - T1) - clock_offset   // One-way upload time
download_estimate = (T4 - T3) - clock_offset // One-way download time
```

**Key insight**: RTT calculation cancels clock offset!

### 6.3 Practical Implementation

**Unity side**:
```csharp
// Store T1 when sending
trace.unity_send_ts = UnixMilliseconds();  // T1

// Record T4 when response received
trace.unity_receive_ts = UnixMilliseconds();  // T4
```

**Server side** (already implemented):
```python
# T2: UDP receive timestamp
server_receive_ts = time.time() * 1000

# T3: Response ready timestamp
server_send_ts = time.time() * 1000
```

**New calculation** in `frame_state_manager.py`:

```python
def calculate_timing_with_clock_offset(
    unity_send_ts,      # T1
    server_receive_ts,  # T2
    server_send_ts,     # T3
    unity_receive_ts    # T4
) -> dict:
    """Calculate upload/download times accounting for clock offset"""

    # RTT (Unity clock, offset-independent)
    rtt_ms = (unity_receive_ts - unity_send_ts) - (server_send_ts - server_receive_ts)

    # Estimated clock offset (Server - Unity)
    offset_ms = ((server_receive_ts - unity_send_ts) + (server_send_ts - unity_receive_ts)) / 2

    # One-way times
    upload_ms = (server_receive_ts - unity_send_ts) - offset_ms
    download_ms = (unity_receive_ts - server_send_ts) + offset_ms

    # Validation: upload + download should ≈ rtt
    validation_error = abs((upload_ms + download_ms) - rtt_ms)

    return {
        'upload_ms': upload_ms,
        'download_ms': download_ms,
        'rtt_ms': rtt_ms,
        'clock_offset_ms': offset_ms,
        'validation_error_ms': validation_error
    }
```

### 6.4 Robust Clock Offset Filtering

**Problem**: Single-frame offset estimates are noisy (jitter, queueing delays).

**Solution**: Rolling min-RTT filter

```python
class ClockOffsetEstimator:
    def __init__(self, window_size=20):
        self.window = []  # (rtt, offset) tuples
        self.window_size = window_size

    def update(self, rtt, offset):
        self.window.append((rtt, offset))
        if len(self.window) > self.window_size:
            self.window.pop(0)

    def get_best_offset(self):
        """Return offset from sample with minimum RTT"""
        if not self.window:
            return 0
        min_rtt_sample = min(self.window, key=lambda x: x[0])
        return min_rtt_sample[1]
```

**Usage**:
```python
# Per-session offset estimator
offset_estimator = ClockOffsetEstimator()

# Update each frame
offset_estimator.update(rtt_ms, offset_ms)

# Use best estimate for upload calculation
best_offset = offset_estimator.get_best_offset()
upload_ms = (server_receive_ts - unity_send_ts) - best_offset
```

### 6.5 Expected Error Sources

| Source | Magnitude | Mitigation |
|--------|-----------|------------|
| Network jitter | ±10-50ms | Min-RTT filtering |
| Server queueing | 0-100ms | Use `server_receive_ts` not `server_process_start_ts` |
| Clock drift | ~1ms/minute | Re-estimate offset every 100 frames |
| UDP loss → HTTP fallback | N/A | Mark frame, exclude from offset estimation |

---

## 7. Integration with Bounded Queue = 3

### 7.1 Queue Architecture

```python
# app/queue/bounded_queue.py
from collections import deque
from dataclasses import dataclass
import threading

@dataclass
class QueuedFrame:
    session_id: str
    frame_id: int
    unity_send_ts: float
    server_receive_ts: float  # NEW: UDP receive timestamp
    prev_telemetry: dict
    jpeg_data: bytes
    enqueue_ts: float  # When added to queue

class BoundedFrameQueue:
    def __init__(self, max_size=3):
        self.queue = deque(maxlen=max_size)  # FIFO with auto-drop oldest
        self.lock = threading.Lock()
        self.drop_callback = None  # Called when frame dropped

    def enqueue(self, frame: QueuedFrame):
        with self.lock:
            if len(self.queue) == self.queue.maxlen:
                # Queue full, oldest frame will be dropped
                dropped = self.queue[0]  # Will be auto-removed by deque
                self._log_queue_drop(dropped)

            self.queue.append(frame)
            print(f"[QUEUE] Frame {frame.session_id}_{frame.frame_id} enqueued, queue_size={len(self.queue)}")

    def dequeue(self) -> QueuedFrame:
        with self.lock:
            if len(self.queue) == 0:
                return None
            frame = self.queue.popleft()

            # Calculate queue_wait_ms
            server_process_start_ts = time.time() * 1000
            queue_wait_ms = server_process_start_ts - frame.server_receive_ts
            frame.server_process_start_ts = server_process_start_ts
            frame.queue_wait_ms = queue_wait_ms

            print(f"[QUEUE] Frame {frame.session_id}_{frame.frame_id} dequeued, queue_wait={queue_wait_ms:.1f}ms")
            return frame

    def _log_queue_drop(self, frame: QueuedFrame):
        """Log queue-side drop event"""
        drop_data = {
            'session_id': frame.session_id,
            'frame_id': frame.frame_id,
            'drop_reason': 'QueueFullOldestDiscarded',
            'drop_ts': time.time() * 1000,
            'server_receive_ts': frame.server_receive_ts,
            'enqueue_ts': frame.enqueue_ts,
            'time_in_queue_ms': (time.time() * 1000) - frame.enqueue_ts
        }
        # Write to queue drop logger
        from app.debug.queue_drop_logger import log_queue_drop
        log_queue_drop(**drop_data)
```

### 7.2 UDP Ingest → Bounded Queue Connection

```python
# app/transport/udp_ingest.py
class UDPFrameIngest:
    def __init__(self, bounded_queue: BoundedFrameQueue):
        self.bounded_queue = bounded_queue

    def on_frame_received(self, session_id, frame_id, unity_send_ts,
                          server_receive_ts, telemetry, jpeg_data):
        """Called when valid frame received via UDP"""

        frame = QueuedFrame(
            session_id=session_id,
            frame_id=frame_id,
            unity_send_ts=unity_send_ts,
            server_receive_ts=server_receive_ts,  # Clean timestamp!
            prev_telemetry=telemetry,
            jpeg_data=jpeg_data,
            enqueue_ts=time.time() * 1000
        )

        # Enqueue (non-blocking, may drop oldest if full)
        self.bounded_queue.enqueue(frame)
```

### 7.3 Queue-Side vs Display-Side Drop Separation

**Queue-Side Drop** (before inference):
```python
# Logged in: app/debug/queue_drop_logger.py
{
    'drop_type': 'QueueFullOldestDiscarded',
    'drop_reason': 'Bounded queue max_size=3 reached',
    'final_state': 'DroppedBeforeProcessing',
    'server_receive_ts': 1776652800161,
    'drop_ts': 1776652800500,  # When dropped from queue
    'time_in_queue_ms': 339  # How long it waited before being dropped
}
```

**Display-Side Drop** (after inference):
```python
# Logged in: app/debug/inference_logger.py (via N+1 telemetry)
{
    'final_state': 'CompletedButSuperseded',
    'drop_reason': 'NewerFrameAlreadyDisplayed',
    'unity_display_ts': 0,  # Never displayed
    'unity_drop_ts': 1776652805000,  # When Unity decided not to display
    'server_proc_ms': 85.4,  # Inference DID complete
    'detection_count': 1  # Results ARE available
}
```

**Key distinction**:
- Queue-side: **No inference results**, frame never processed
- Display-side: **Inference completed**, results returned but not used

---

## 8. Telemetry / Exporter Compatibility

### 8.1 Minimal Changes to Existing Architecture

**Keep unchanged**:
- ✅ `FrameTrace.cs` structure (add 2 fields only)
- ✅ N+1 delayed telemetry pattern
- ✅ `frame_state_manager.py` logic
- ✅ `inference_logger.py` Excel export
- ✅ Final state taxonomy

**Add fields**:

**FrameTrace.cs**:
```csharp
public string payload_hash;      // NEW: SHA256 for verification
public float clock_offset_ms;    // NEW: Estimated offset
```

**Excel columns** (append to existing):
```
| payload_hash | clock_offset_ms | transport_type |
|--------------|-----------------|----------------|
| sha256...    | -123.4          | udp            |
```

### 8.2 Transport Adapter Pattern

**New abstraction**: `app/transport/transport_interface.py`

```python
from abc import ABC, abstractmethod

class TransportAdapter(ABC):
    @abstractmethod
    async def receive_frame(self) -> QueuedFrame:
        """Receive one frame from transport layer"""
        pass

class UDPTransport(TransportAdapter):
    async def receive_frame(self):
        # UDP-specific logic
        return awaitable_frame

class HTTPTransport(TransportAdapter):
    async def receive_frame(self):
        # HTTP fallback for large frames
        return awaitable_frame
```

**Worker thread** (existing) becomes transport-agnostic:

```python
# app/worker/inference_worker.py
async def process_frames():
    while True:
        frame = await bounded_queue.dequeue()  # Source-agnostic

        # Existing inference logic (UNCHANGED)
        result = await run_inference(frame.jpeg_data, frame.session_id, frame.frame_id)

        # Store in result cache
        result_cache.set((frame.session_id, frame.id), result)
```

### 8.3 Result Cache for HTTP Response Lookup

**New component**: `app/cache/result_cache.py`

```python
from typing import Dict, Tuple
import threading

class ResultCache:
    def __init__(self, ttl=30):
        self.cache = {}  # Key: (session_id, frame_id), Value: result dict
        self.lock = threading.Lock()
        self.ttl = ttl  # seconds

    def set(self, key: Tuple[str, int], result: dict):
        with self.lock:
            self.cache[key] = {
                'result': result,
                'timestamp': time.time()
            }

    def get(self, key: Tuple[str, int]) -> dict:
        with self.lock:
            if key in self.cache:
                return self.cache[key]['result']
            return None

    def cleanup(self):
        """Remove expired results"""
        with self.lock:
            now = time.time()
            expired = [k for k, v in self.cache.items() if now - v['timestamp'] > self.ttl]
            for k in expired:
                del self.cache[k]
```

**HTTP response endpoint** (NEW):

```python
# app/routes/response.py
from fastapi import APIRouter, HTTPException

router = APIRouter()

@router.get("/response/{session_id}/{frame_id}")
async def get_frame_response(session_id: str, frame_id: int):
    """Poll endpoint for frame inference result"""
    key = (session_id, frame_id)
    result = result_cache.get(key)

    if result is None:
        # Not ready yet, Unity will poll again
        raise HTTPException(status_code=404, detail="Result not ready")

    return result  # JSON response (same format as current /infer_human)
```

---

## 9. Three-Mode Impact Review

### 9.1 Shared Components (All Modes)

| Component | Change | PoseEstimation | MultiObjectDetection | Segmentation |
|-----------|--------|----------------|----------------------|--------------|
| UDP Send | Add `SendFrameUDP()` | ✓ | ✓ | ✓ |
| HTTP Poll | Add `ListenForResponseHTTP()` | ✓ | ✓ | ✓ |
| FrameTrace | Add 2 fields | ✓ | ✓ | ✓ |
| InferenceConfig | Add UDP endpoint | ✓ | ✓ | ✓ |
| UDP Ingest | New server component | ✓ | ✓ | ✓ |
| Bounded Queue | Already exists | ✓ | ✓ | ✓ |
| Result Cache | New server component | ✓ | ✓ | ✓ |

### 9.2 Mode-Specific Considerations

**PoseEstimation** (`/infer_human?mode=both`):
- **Payload size**: ~30-50KB JPEG → Single UDP datagram ✓
- **Response size**: ~10KB JSON (skeleton + detections) → HTTP poll ✓
- **Inference time**: 150-250ms → Queue size=3 sufficient ✓

**MultiObjectDetection** (`/infer_human?mode=detection`):
- **Payload size**: ~30-50KB JPEG → Single UDP datagram ✓
- **Response size**: ~5KB JSON (detections only) → HTTP poll ✓
- **Inference time**: 100-150ms → Queue size=3 sufficient ✓

**Segmentation** (`/segmentation`):
- **Payload size**: ~50-100KB JPEG + depth map → May need chunking ⚠️
- **Response size**: ~75KB PNG mask (downsampled) → HTTP poll ✓
- **Inference time**: 200-300ms → Queue size=3 may need tuning ⚠️

**Recommendation**:
- **Phase 1**: UDP for Pose/Detection, HTTP fallback for Segmentation if payload >64KB
- **Phase 2**: Add multi-packet reassembly if Segmentation needs UDP

### 9.3 Code Duplication Avoidance

**Create shared base class**:

```csharp
// Assets/Shared/Scripts/BaseInferenceManager.cs
public abstract class BaseInferenceManager : MonoBehaviour
{
    protected UdpClient m_udpClient;
    protected string m_sessionId;
    protected int m_frameId;

    protected void SendFrameUDP(FrameTrace trace, byte[] jpegData)
    {
        // Shared UDP send logic
    }

    protected IEnumerator ListenForResponseHTTP(int frameId)
    {
        // Shared HTTP poll logic
    }

    protected abstract string GetResponseEndpoint(int frameId);
    protected abstract void ProcessResponse(int frameId, string json);
}

// PoseInferenceRunManager : BaseInferenceManager
// SentisInferenceRunManager : BaseInferenceManager
// SegmentationInferenceRunManager : BaseInferenceManager
```

---

## 10. Risks and Constraints

### 10.1 UDP-Specific Risks

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| **Packet loss** | Medium (WiFi) | High (missing frames) | Accept loss, log in telemetry, Unity marks as Failed after timeout |
| **Out-of-order delivery** | Low (single UDP socket) | Low | Frame identity prevents correlation errors |
| **Duplicate packets** | Low | Low | Deduplication cache on server |
| **Large payload fragmentation** | Medium (Segmentation) | Medium | HTTP fallback for >64KB frames |
| **Firewall blocks UDP** | Medium | High | Fallback to HTTP transport mode |

### 10.2 Clock Sync Risks

| Risk | Impact | Mitigation |
|------|--------|------------|
| Clock drift | ±1-2ms/minute | Re-estimate offset every 100 frames |
| Jitter noise | ±10-50ms | Min-RTT filtering over 20-frame window |
| Queueing delays | Offset estimation bias | Use `server_receive_ts` not `server_process_start_ts` |

### 10.3 Implementation Risks

| Risk | Impact | Mitigation |
|------|--------|------------|
| Breaking existing telemetry | High | Phased migration, dual-path validation |
| UDP performance worse than HTTP | Medium | Measure before/after, keep HTTP fallback |
| Increased code complexity | Medium | Shared base classes, clear abstractions |
| Result cache memory growth | Low | TTL cleanup, bounded cache size |

---

## 11. Phased Migration Plan

### Phase 1: UDP Ingest + Clean Receive Timestamp (2-3 days)

**Goal**: Get clean `server_receive_ts` without breaking existing functionality.

**Changes**:
1. **Server**: Add UDP ingest listener (`app/transport/udp_ingest.py`)
2. **Server**: Add bounded queue integration (already exists from previous phase)
3. **Server**: Add result cache (`app/cache/result_cache.py`)
4. **Server**: Add HTTP response endpoint (`/response/{session_id}/{frame_id}`)
5. **Unity**: Add UDP send path (`SendFrameUDP()` in all 3 managers)
6. **Unity**: Add HTTP poll path (`ListenForResponseHTTP()` in all 3 managers)
7. **Unity**: Add payload hash computation

**Validation**:
- Server console shows: `[UDP INGEST] Frame received, size=X`
- Excel logs show: `server_receive_ts` immediately after UDP receive (not delayed by processing)
- Queue wait times drop to <5ms

**Rollback**: Keep HTTP path as fallback, add config flag `use_udp=true/false`

### Phase 2: Clock Offset Estimation (1-2 days)

**Goal**: Get accurate upload/download time estimates.

**Changes**:
1. **Unity**: Record `unity_receive_ts` (T4) in response handler
2. **Server**: Return `server_send_ts` (T3) in response JSON
3. **Server**: Implement `ClockOffsetEstimator` in `frame_state_manager.py`
4. **Server**: Update `calculate_timing_with_clock_offset()` in telemetry

**Validation**:
- Excel logs show: `clock_offset_ms`, `upload_ms`, `download_ms`
- Upload time drops from 276ms (noisy) to ~50-100ms (accurate)
- RTT validation: `upload_ms + download_ms ≈ rtt_ms`

### Phase 3: Non-Blocking Send (1 day)

**Goal**: Unity sends at fixed cadence without blocking.

**Changes**:
1. **Unity**: Remove `yield return asyncOp` from send path
2. **Unity**: Add throttle timer `WaitForSeconds(1f / targetFPS)`
3. **Unity**: Handle response timeout gracefully (mark as Failed)

**Validation**:
- Excel logs show: Actual FPS ≈ targetFPS (5 FPS target → 5 FPS actual)
- Frame intervals: Consistent 200ms (not variable 300-500ms)
- Frames logged per session: 300 frames in 60 seconds (was 150 frames)

### Phase 4: Multi-Mode Validation (2 days)

**Goal**: Ensure all 3 modes work with new transport.

**Test matrix**:
| Mode | UDP Send | HTTP Poll | Telemetry | Pass/Fail |
|------|----------|-----------|-----------|-----------|
| PoseEstimation | ✓ | ✓ | ✓ | TBD |
| MultiObjectDetection | ✓ | ✓ | ✓ | TBD |
| Segmentation | ✓ | ✓ | ✓ | TBD |

**Validation**:
- Run 2-minute test for each mode
- Verify: All sent frames logged, no hash mismatches, queue_wait <10ms

### Phase 5: Performance Tuning (1-2 days)

**Goals**:
- Optimize queue size (test 3, 5, 10)
- Tune clock offset filter window size
- Adjust HTTP poll interval
- Measure end-to-end improvement

**Metrics to compare** (before/after):

| Metric | Before (HTTP blocking) | Target (UDP non-blocking) |
|--------|------------------------|---------------------------|
| Actual FPS | 2.6 FPS | 5 FPS |
| Upload time estimate | 276ms (noisy) | 50-100ms (accurate) |
| Queue wait | 101ms (includes transport) | <5ms (processing only) |
| Frames logged per 60s | 150 | 300 |
| E2E latency | 386ms | 250ms |

---

## 12. Compatibility Checklist

### Preserved Existing Features
- ✅ N+1 delayed telemetry pattern (Unity still sends prev frame telemetry in headers)
- ✅ FrameTrace lifecycle (no breaking changes, only 2 new fields)
- ✅ frame_state_manager.py (minimal changes to timing calculation)
- ✅ inference_logger.py Excel export (append new columns)
- ✅ Final state taxonomy (add new states, don't break existing)
- ✅ All 3 inference modes (shared transport layer)
- ✅ Bounded queue drop logic (unchanged)

### New Components (Additive)
- ➕ UDP ingest listener (new server component)
- ➕ Result cache (new server component)
- ➕ HTTP response endpoint (new server route)
- ➕ Clock offset estimator (new server utility)
- ➕ UDP send path (new Unity method)
- ➕ HTTP poll path (new Unity coroutine)

### Modified Components
- 🔧 `RunInference()` in all 3 Unity managers (split into send + poll)
- 🔧 `frame_state_manager.py` timing calculation (add clock offset logic)
- 🔧 `inference_logger.py` (append 3 new columns)

### Removed Components
- ❌ None (migration is additive, HTTP path remains as fallback)

---

## Summary

This proposal adds **non-blocking UDP transport** to the existing architecture with:

1. **Unity non-blocking send**: Fire-and-forget UDP + async HTTP poll for responses
2. **Clean server timestamps**: `server_receive_ts` recorded immediately on UDP receive
3. **Same-frame identity**: `(session_id, frame_id)` enforced across full lifecycle + SHA256 verification
4. **Clock-offset-aware timing**: NTP-style 4-timestamp RTT estimation with min-RTT filtering
5. **Bounded queue integration**: UDP → Queue (max=3) → Worker → Result Cache → HTTP Response
6. **Telemetry compatibility**: Minimal changes (2 new FrameTrace fields, 3 new Excel columns)
7. **Three-mode support**: Shared transport layer for Pose/Detection/Segmentation
8. **Phased migration**: 5 phases with validation checkpoints and HTTP fallback

**Expected improvements**:
- Actual FPS: 2.6 → 5.0 FPS (matches target)
- Upload estimate: 276ms (noisy) → 50-100ms (accurate)
- Queue wait: 101ms → <5ms
- Frames logged: 150 → 300 per 60s

**Key design principles**:
- ✅ Additive changes, not replacements
- ✅ HTTP fallback preserved
- ✅ Existing telemetry/export pipeline intact
- ✅ Clear separation: queue-side drop vs display-side drop
- ✅ Application-level frame identity, not transport-dependent

---

**End of Proposal Document**
