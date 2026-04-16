# UDP Migration Plan for Real-Time Inference

This document provides a comprehensive plan for migrating from HTTP to UDP for Unity-to-Server communication.

---

## Executive Summary

### Current Architecture (HTTP)
- **Protocol**: HTTP POST with UnityWebRequest
- **Reliability**: TCP ensures delivery, retransmission on loss
- **Overhead**: HTTP headers (~200 bytes), TCP handshake, connection management
- **Latency**: ~50-150ms upload, affected by server backlog
- **Issue**: Bounded queue helps, but HTTP overhead remains

### Proposed Architecture (UDP)
- **Protocol**: UDP datagram with custom framing
- **Reliability**: **Application-level** acknowledgment (optional)
- **Overhead**: UDP header (8 bytes) + custom header (~50 bytes)
- **Latency**: **10-30ms** upload (theoretical), no connection overhead
- **Benefit**: Lower latency, natural frame dropping at transport level

---

## Migration Strategy

### Phase 1: Hybrid Architecture (Recommended)

Keep both protocols during transition:

```
Unity Side:
- UDP: Send inference requests (frames)
- HTTP: Receive responses (detections/poses)

Server Side:
- UDP Socket: Receive frames
- HTTP Response: Send results back via long-polling or WebSocket
```

**Rationale**:
- UDP upload is **unidirectional** (Unity → Server)
- HTTP download **already works** well (Server → Unity)
- Avoid complexity of bidirectional UDP NAT traversal

### Phase 2: Full UDP (Optional Future)

Implement bidirectional UDP:

```
Unity ←─ UDP ─→ Server
  Request: Frame data
  Response: Detection/pose results
```

---

## Detailed Implementation

## 1. Unity Side: UDP Send Implementation

### C# UDP Client

```csharp
using System.Net;
using System.Net.Sockets;
using System.IO;

public class UdpInferenceSender : MonoBehaviour
{
    // Configuration
    [SerializeField] private string m_serverIP = "192.168.0.135";
    [SerializeField] private int m_serverPort = 8002;  // NEW UDP port

    // UDP client
    private UdpClient m_udpClient;
    private IPEndPoint m_serverEndpoint;

    // Session tracking
    private string m_sessionId;
    private int m_frameCounter = 0;

    void Start()
    {
        // Initialize UDP client
        m_udpClient = new UdpClient();
        m_serverEndpoint = new IPEndPoint(IPAddress.Parse(m_serverIP), m_serverPort);

        // Generate session ID
        m_sessionId = System.Guid.NewGuid().ToString();

        Debug.Log($"[UDP] Initialized: {m_serverIP}:{m_serverPort}");
    }

    void OnDestroy()
    {
        m_udpClient?.Close();
    }

    // Send frame via UDP
    public void SendFrame(byte[] jpegData, int width, int height)
    {
        int frameId = m_frameCounter++;
        float sendTs = Time.realtimeSinceStartup;

        try
        {
            // Build UDP packet with custom header
            byte[] packet = BuildPacket(frameId, jpegData, width, height);

            // Send datagram (non-blocking, fire-and-forget)
            m_udpClient.Send(packet, packet.Length, m_serverEndpoint);

            Debug.Log($"[UDP SEND] Frame {frameId}, size={packet.Length} bytes");
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[UDP ERROR] Frame {frameId}: {e.Message}");
        }
    }

    private byte[] BuildPacket(int frameId, byte[] jpegData, int width, int height)
    {
        using (MemoryStream ms = new MemoryStream())
        using (BinaryWriter writer = new BinaryWriter(ms))
        {
            // ========================================================
            // PACKET HEADER (50 bytes fixed)
            // ========================================================
            // Magic number for validation (4 bytes)
            writer.Write((uint)0xCAFEBABE);

            // Protocol version (2 bytes)
            writer.Write((ushort)1);

            // Packet type (1 byte): 0x01 = Inference Request
            writer.Write((byte)0x01);

            // Flags (1 byte): Reserved
            writer.Write((byte)0);

            // Session ID (16 bytes, GUID as bytes)
            writer.Write(System.Guid.Parse(m_sessionId).ToByteArray());

            // Frame ID (4 bytes)
            writer.Write((uint)frameId);

            // Timestamp (8 bytes, Unix milliseconds)
            double unixMs = (System.DateTime.UtcNow - new System.DateTime(1970, 1, 1)).TotalMilliseconds;
            writer.Write((long)unixMs);

            // Image dimensions (4 bytes each)
            writer.Write((uint)width);
            writer.Write((uint)height);

            // Payload length (4 bytes)
            writer.Write((uint)jpegData.Length);

            // ========================================================
            // PAYLOAD (variable length)
            // ========================================================
            writer.Write(jpegData);

            return ms.ToArray();
        }
    }
}
```

### Packet Format

```
+-------------------+------------------+--------------------------------+
| Field             | Size (bytes)     | Description                    |
+-------------------+------------------+--------------------------------+
| Magic Number      | 4                | 0xCAFEBABE (validation)        |
| Protocol Version  | 2                | 0x0001 (v1)                    |
| Packet Type       | 1                | 0x01 (inference request)       |
| Flags             | 1                | Reserved                       |
| Session ID        | 16               | GUID bytes                     |
| Frame ID          | 4                | Unique frame number            |
| Timestamp         | 8                | Unix milliseconds (int64)      |
| Image Width       | 4                | Pixel width (uint32)           |
| Image Height      | 4                | Pixel height (uint32)          |
| Payload Length    | 4                | JPEG data size (uint32)        |
+-------------------+------------------+--------------------------------+
| JPEG Data         | Variable         | Compressed image               |
+-------------------+------------------+--------------------------------+
Total Header: 50 bytes
```

---

## 2. Server Side: UDP Receive Implementation

### Python UDP Server

```python
"""
UDP inference receiver with bounded queue integration.
"""

import asyncio
import struct
import time
from dataclasses import dataclass

@dataclass
class UdpInferenceRequest:
    """Parsed UDP inference request."""
    session_id: str
    frame_id: int
    timestamp_ms: int
    image_width: int
    image_height: int
    jpeg_data: bytes
    server_receive_ts: float  # When server received

class UdpInferenceServer:
    """
    UDP server that receives inference requests and feeds them to bounded queue.
    """

    def __init__(self, host='0.0.0.0', port=8002):
        self.host = host
        self.port = port
        self.transport = None
        self.protocol = None

    async def start(self):
        """Start UDP server."""
        loop = asyncio.get_event_loop()

        # Create datagram endpoint
        self.transport, self.protocol = await loop.create_datagram_endpoint(
            lambda: UdpInferenceProtocol(self.on_request_received),
            local_addr=(self.host, self.port)
        )

        print(f"[UDP SERVER] Listening on {self.host}:{self.port}")

    async def on_request_received(self, request: UdpInferenceRequest):
        """
        Handle received UDP inference request.
        Feed to bounded admission queue.
        """
        from app.request_admission import admit_request, AdmittedRequest, get_admission_queue
        from debug.queue_drop_logger import log_queue_drop_immediately

        request_id = f"{request.session_id}_{request.frame_id}"

        print(f"[UDP RECV] {request_id}, size={len(request.jpeg_data)} bytes")

        try:
            # Create AdmittedRequest (skip FastAPI UploadFile, already have bytes)
            from PIL import Image
            import io

            # Decode image to get dimensions (fast validation)
            pil_image = Image.open(io.BytesIO(request.jpeg_data))
            img_width, img_height = pil_image.size

            # Build admitted request
            admitted_req = AdmittedRequest(
                request_id=request_id,
                session_id=request.session_id,
                frame_id=request.frame_id,
                scene="UdpInference",
                mode="both",  # or read from packet flags
                image_bytes=request.jpeg_data,
                img_width=img_width,
                img_height=img_height,
                server_receive_ts=request.server_receive_ts,
                admission_ts=time.time(),
                headers={
                    'X-Image-Width': str(img_width),
                    'X-Image-Height': str(img_height),
                    'X-Timestamp-Ms': str(request.timestamp_ms)
                },
                include_mask=False,
                include_depth=False
            )

            # Admit to bounded queue
            queue = get_admission_queue()
            admitted, dropped = await queue.admit(admitted_req)

            # If a frame was dropped, log it IMMEDIATELY
            if dropped:
                await log_queue_drop_immediately(dropped)

            # Processing happens in background worker pool
            # Response will be sent via HTTP long-polling or WebSocket

        except Exception as e:
            print(f"[UDP ERROR] Failed to process {request_id}: {e}")


class UdpInferenceProtocol(asyncio.DatagramProtocol):
    """
    Asyncio protocol for receiving UDP datagrams.
    """

    def __init__(self, callback):
        self.callback = callback
        super().__init__()

    def datagram_received(self, data, addr):
        """Called when a datagram is received."""
        try:
            # Record receive timestamp IMMEDIATELY
            server_receive_ts = time.time()

            # Parse packet
            request = self.parse_packet(data, server_receive_ts)

            # Process asynchronously
            asyncio.create_task(self.callback(request))

        except Exception as e:
            print(f"[UDP] Failed to parse packet from {addr}: {e}")

    def parse_packet(self, data: bytes, server_receive_ts: float) -> UdpInferenceRequest:
        """
        Parse UDP packet into UdpInferenceRequest.

        Packet format:
        - Magic number (4 bytes): 0xCAFEBABE
        - Protocol version (2 bytes): 0x0001
        - Packet type (1 byte): 0x01
        - Flags (1 byte)
        - Session ID (16 bytes)
        - Frame ID (4 bytes)
        - Timestamp (8 bytes)
        - Image width (4 bytes)
        - Image height (4 bytes)
        - Payload length (4 bytes)
        - JPEG data (variable)
        """
        # Validate minimum size
        if len(data) < 50:
            raise ValueError(f"Packet too small: {len(data)} bytes")

        # Unpack header (50 bytes)
        header = struct.unpack('!I H B B 16s I Q I I I', data[:50])

        magic, version, ptype, flags, session_bytes, frame_id, timestamp_ms, width, height, payload_len = header

        # Validate magic number
        if magic != 0xCAFEBABE:
            raise ValueError(f"Invalid magic number: 0x{magic:08X}")

        # Validate version
        if version != 1:
            raise ValueError(f"Unsupported version: {version}")

        # Validate packet type
        if ptype != 0x01:
            raise ValueError(f"Invalid packet type: 0x{ptype:02X}")

        # Parse session ID (GUID)
        import uuid
        session_id = str(uuid.UUID(bytes=session_bytes))

        # Extract JPEG data
        jpeg_data = data[50:50+payload_len]

        if len(jpeg_data) != payload_len:
            raise ValueError(f"Payload size mismatch: expected {payload_len}, got {len(jpeg_data)}")

        return UdpInferenceRequest(
            session_id=session_id,
            frame_id=frame_id,
            timestamp_ms=timestamp_ms,
            image_width=width,
            image_height=height,
            jpeg_data=jpeg_data,
            server_receive_ts=server_receive_ts
        )
```

### Integration with Main App

```python
# app/main.py

from app.udp_server import UdpInferenceServer

@app.on_event("startup")
async def startup():
    # ... existing warmup code ...

    # Start UDP server
    udp_server = UdpInferenceServer(host='0.0.0.0', port=8002)
    await udp_server.start()

    print("[UDP] Server started on port 8002")
```

---

## 3. Response Delivery Options

### Option A: HTTP Long-Polling (Recommended)

Unity polls for results using frame ID:

```csharp
// Unity side
IEnumerator PollForResult(int frameId)
{
    string url = $"{m_baseUrl}/get_result/{m_sessionId}/{frameId}";

    using (UnityWebRequest request = UnityWebRequest.Get(url))
    {
        // Set timeout for long-polling
        request.timeout = 5;  // 5 seconds

        yield return request.SendWebRequest();

        if (request.result == UnityWebRequest.Result.Success)
        {
            // Process result
            HandleResponse(frameId, request.downloadHandler.data);
        }
    }
}
```

```python
# Server side (FastAPI)
@app.get("/get_result/{session_id}/{frame_id}")
async def get_result(session_id: str, frame_id: int):
    """
    Long-polling endpoint: waits for result to be ready.
    Returns immediately if result is available, otherwise waits up to 5s.
    """
    from app.result_store import get_result_store

    store = get_result_store()

    # Wait for result (with timeout)
    result = await store.wait_for_result(session_id, frame_id, timeout=5.0)

    if result:
        return result
    else:
        return JSONResponse(status_code=204, content={"status": "not_ready"})
```

### Option B: WebSocket (Lower Latency)

Persistent connection for responses:

```csharp
// Unity side (using WebSocket library)
WebSocket ws = new WebSocket($"ws://{m_serverIP}:8001/ws/{m_sessionId}");

ws.OnMessage += (sender, e) =>
{
    // Parse result
    InferenceResponse response = JsonUtility.FromJson<InferenceResponse>(e.Data);
    HandleResponse(response.frameId, response);
};

ws.Connect();
```

```python
# Server side (FastAPI WebSocket)
@app.websocket("/ws/{session_id}")
async def websocket_endpoint(websocket: WebSocket, session_id: str):
    await websocket.accept()

    try:
        while True:
            # Wait for next result
            result = await result_queue.get(session_id)

            # Send to Unity
            await websocket.send_json(result)
    except:
        await websocket.close()
```

---

## 4. Latency Breakdown Comparison

### HTTP (Current)
```
Total E2E: ~200-300ms
├─ Upload:   50-100ms  (TCP handshake + headers + JPEG)
├─ Queue:    10-50ms   (bounded queue wait)
├─ Inference: 50-100ms (GPU processing)
└─ Download:  50-100ms (TCP + HTTP headers + JSON)
```

### UDP Upload + HTTP Download (Hybrid)
```
Total E2E: ~150-250ms (-50ms improvement)
├─ Upload:   10-30ms   (UDP datagram, no handshake)
├─ Queue:    10-50ms   (bounded queue wait)
├─ Inference: 50-100ms (GPU processing)
└─ Download:  50-100ms (HTTP response)
```

### UDP Bidirectional (Full)
```
Total E2E: ~100-200ms (-100ms improvement)
├─ Upload:   10-30ms   (UDP datagram)
├─ Queue:    10-50ms   (bounded queue wait)
├─ Inference: 50-100ms (GPU processing)
└─ Download:  10-30ms  (UDP datagram response)
```

---

## 5. Reliability Considerations

### Packet Loss Handling

**Strategy 1: Fire-and-Forget (Recommended for VR)**
```
- Send frame, don't wait for ACK
- If lost, next frame compensates
- Acceptable for 60 FPS inference (10-20% loss OK)
```

**Strategy 2: Selective ACK**
```
- Server sends UDP ACK for each frame
- Unity resends if no ACK within 100ms
- Adds latency but improves reliability
```

### Duplicate Detection

```python
# Server side
seen_frames = {}  # {(session_id, frame_id): timestamp}

def is_duplicate(session_id, frame_id):
    key = (session_id, frame_id)
    if key in seen_frames:
        return True
    seen_frames[key] = time.time()
    return False
```

### Out-of-Order Handling

```python
# Bounded queue already handles this:
# - Older frames are dropped when queue is full
# - Display decision in Unity handles out-of-order results
```

---

## 6. Migration Roadmap

### Stage 1: UDP Send Only (1-2 weeks)
- [ ] Implement Unity UDP sender
- [ ] Implement Python UDP receiver
- [ ] Test with bounded queue integration
- [ ] Keep HTTP response for backward compatibility

### Stage 2: HTTP Long-Polling Response (1 week)
- [ ] Implement result store
- [ ] Add long-polling endpoint
- [ ] Unity polls after UDP send
- [ ] Measure latency improvement

### Stage 3: WebSocket Response (Optional, 1 week)
- [ ] Implement WebSocket endpoint
- [ ] Unity WebSocket client
- [ ] Persistent connection management

### Stage 4: Full UDP (Optional, 2-3 weeks)
- [ ] UDP response datagrams
- [ ] NAT traversal (STUN/TURN if needed)
- [ ] Reliability layer (optional ACK)

---

## 7. Firewall and Network Configuration

### Server Firewall (Windows)

```powershell
# Allow inbound UDP on port 8002
New-NetFirewallRule -DisplayName "Vision Server UDP" `
    -Direction Inbound `
    -Protocol UDP `
    -LocalPort 8002 `
    -Action Allow
```

### Quest 3 Network

- Quest 3 can send UDP (no restrictions)
- NAT traversal NOT needed for outbound UDP
- Server must have static IP or DDNS

---

## 8. Testing Plan

### Unit Tests

```python
# Test UDP packet parsing
def test_parse_packet():
    # Build test packet
    packet = build_test_packet(session_id="test", frame_id=42)

    # Parse
    request = parse_packet(packet)

    assert request.frame_id == 42
    assert len(request.jpeg_data) > 0
```

### Integration Tests

```csharp
// Unity test: Send 100 frames, measure delivery rate
void TestUdpReliability()
{
    int sent = 0;
    int received = 0;

    for (int i = 0; i < 100; i++)
    {
        SendFrame(testJpeg, 1280, 960);
        sent++;
    }

    // Wait for responses
    yield return new WaitForSeconds(10);

    float lossRate = 1.0f - (received / (float)sent);
    Debug.Log($"Loss rate: {lossRate * 100:F1}%");
}
```

---

## 9. Rollback Plan

If UDP migration fails:

1. **Keep HTTP endpoint active** during migration
2. **Feature flag** in Unity to switch UDP on/off
3. **Metrics comparison**: HTTP vs UDP latency
4. **Gradual rollout**: 10% → 50% → 100%

```csharp
// Feature flag
[SerializeField] private bool m_useUdp = false;

void SendFrame()
{
    if (m_useUdp)
        SendViaUdp();
    else
        SendViaHttp();
}
```

---

## 10. Expected Performance Gains

### Latency Reduction

| Metric | HTTP (Current) | UDP Hybrid | UDP Full | Improvement |
|--------|----------------|------------|----------|-------------|
| Upload | 50-100ms | 10-30ms | 10-30ms | **-70ms** |
| Download | 50-100ms | 50-100ms | 10-30ms | **-70ms** |
| **Total E2E** | **200-300ms** | **150-250ms** | **100-200ms** | **-100ms** |

### Throughput

- HTTP: ~10-15 FPS (bounded by queue + overhead)
- UDP: ~20-30 FPS (lower overhead, faster admission)

---

## Recommendation

**Start with Stage 1 (UDP Send + HTTP Response)**

Why:
- ✅ Immediate latency benefit (-50ms upload)
- ✅ Minimal risk (HTTP response still works)
- ✅ Reuses existing bounded queue infrastructure
- ✅ No NAT traversal complexity
- ✅ Can measure improvement before full migration

**Defer Stage 4 (Full UDP) unless further optimization needed**

Why:
- ⚠️ Adds complexity (reliability, NAT traversal)
- ⚠️ Diminishing returns (additional -50ms)
- ⚠️ HTTP response already fast enough for VR

---

This plan provides a clear, incremental path to UDP migration with minimal risk and measurable improvements at each stage.
