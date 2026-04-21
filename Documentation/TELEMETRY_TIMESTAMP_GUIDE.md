# Telemetry Timestamp & Latency Metrics Guide

This document explains how to capture and calculate all telemetry timestamps and latency metrics in the Unity-Server inference pipeline.

---

## Overview

The telemetry system tracks **7 timestamps** and derives **5 latency metrics** to measure end-to-end performance from Unity (Quest 3) to the Python inference server.

### Architecture Flow

```
Unity (Quest 3)                    Server (Python)
─────────────────                  ───────────────

[1] unity_send_ts ──────UDP────────→ [4] server_receive_ts
       │                                    │
       │                                    ▼
       │                             [5] server_process_start_ts
       │                                    │ (AI Inference)
       │                                    ▼
       │                             [6] server_send_ts
       │                                    │
[2] unity_receive_ts ◄──HTTP GET───────────┘
       │
       ▼
[3] unity_display_ts
```

---

## Timestamp Reference

| Timestamp | Location | Meaning | When Captured |
|-----------|----------|---------|---------------|
| `unity_send_ts` | Unity | Frame sent via UDP | Before `UDPTransportManager.SendFrame()` |
| `unity_receive_ts` | Unity | Response received from server | After successful HTTP GET poll |
| `unity_display_ts` | Unity | Results rendered on screen | After `HandleV3Response()` completes |
| `unity_drop_ts` | Unity | Frame dropped (optional) | When frame expires or is rejected |
| `server_receive_ts` | Server | UDP frame received | When UDP listener receives packet |
| `server_process_start_ts` | Server | Inference started | Before model.predict() |
| `server_send_ts` | Server | Response cached | After result stored in cache |

### Derived Latency Metrics

| Metric | Formula | Meaning |
|--------|---------|---------|
| `latency_ms` | `unity_receive_ts - unity_send_ts` | Total end-to-end latency |
| `upload_ms` | `server_receive_ts - unity_send_ts` | Network upload time |
| `queue_wait_ms` | `server_process_start_ts - server_receive_ts` | Time waiting in server queue |
| `server_proc_ms` | `server_send_ts - server_process_start_ts` | Server inference time |
| `download_ms` | `unity_receive_ts - server_send_ts` | Network download time |

---

## Unity Side Implementation

### 1. Capture `unity_send_ts`

**Location**: Before sending UDP frame

**Code** (in `SegmentationInferenceRunManager.cs`, `PoseInferenceRunManager.cs`, etc.):

```csharp
// Create frame trace with send timestamp
FrameTrace trace = new FrameTrace
{
    sessionId = m_sessionId,
    frameId = m_frameId,
    unitySendTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), // [1] unity_send_ts
    imageWidth = currentTexture.width,
    imageHeight = currentTexture.height
};

// Send frame via UDP
m_transport.SendFrame(trace, jpegData, telemetryJson);
```

**Key Points**:
- Use `DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()` for consistent Unix epoch timestamps
- Capture **before** `SendFrame()` to include serialization time
- Store in `FrameTrace` object

### 2. Capture `unity_receive_ts`

**Location**: When response is polled successfully

**Code** (in inference managers):

```csharp
private void Update()
{
    if (m_useServerInference && m_useUDPTransport && m_transport != null)
    {
        while (m_transport.TryGetResponse(out FrameResponse response))
        {
            // [2] unity_receive_ts is already set by UDPTransportManager
            // response.trace.unityReceiveTimestamp contains the value

            HandleV3Response(response);
        }
    }
}
```

**Automatic Capture** (in `UDPTransportManager.cs`):

```csharp
private IEnumerator PollForResponse(FrameTrace trace)
{
    // ... HTTP GET polling logic ...

    if (request.result == UnityWebRequest.Result.Success)
    {
        // Capture receive timestamp
        trace.unityReceiveTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); // [2]

        FrameResponse response = new FrameResponse
        {
            trace = trace,
            rawJson = request.downloadHandler.text
        };

        m_responseQueue.Enqueue(response);
    }
}
```

**Key Points**:
- Captured automatically in `UDPTransportManager.PollForResponse()`
- Set **immediately after** successful HTTP GET
- Includes parsing time (JSON deserialization happens later)

### 3. Capture `unity_display_ts`

**Location**: After rendering results

**Code** (in inference managers):

```csharp
private void HandleV3Response(FrameResponse response)
{
    // Parse JSON
    var jsonData = ParseServerResponse(response.rawJson);

    // Update visualizations (3D boxes, skeletons, etc.)
    UpdateVisualizations(jsonData);

    // [3] Capture display timestamp
    response.trace.unityDisplayTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    // Record telemetry
    m_telemetryTracker?.RecordSuccess(response);
}
```

**Key Points**:
- Capture **after** all rendering is complete
- Includes time for JSON parsing + visualization updates
- Should be the last timestamp set before telemetry recording

### 4. Capture `unity_drop_ts` (Optional)

**Location**: When frame is dropped

**Code** (in inference managers):

```csharp
private void OnFrameDropped(FrameTrace trace, string reason)
{
    trace.unityDropTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); // [4]
    trace.dropReason = reason;

    m_telemetryTracker?.RecordDrop(trace, reason);
}
```

**Key Points**:
- Only set when frame is explicitly dropped
- Include `dropReason` (e.g., "timeout", "queue_full", "stale_frame")
- Most successful frames will have `0` for this field

---

## Server Side Implementation

### 1. Capture `server_receive_ts`

**Location**: UDP listener receives packet

**Code** (in `app/udp/listener.py` or similar):

```python
async def handle_udp_frame(self, data: bytes, addr):
    """Handle incoming UDP frame from Unity."""

    # [4] Capture receive timestamp
    receive_ts = int(time.time() * 1000)

    try:
        # Deserialize UDP packet
        frame = deserialize_udp_frame(data)

        # Add server receive timestamp
        frame.server_receive_ts = receive_ts

        # Add to admission queue
        await self.admission_queue.enqueue(frame)

    except Exception as e:
        print(f"[UDP] Error handling frame: {e}")
```

**Key Points**:
- Use `int(time.time() * 1000)` for Unix epoch milliseconds (matches Unity)
- Capture **immediately** when packet arrives
- Before deserialization to include parsing overhead

### 2. Capture `server_process_start_ts`

**Location**: UDP worker pulls frame from queue

**Code** (in `app/udp/worker.py` or inference pipeline):

```python
async def process_frame(self, frame: UDPFrame):
    """Process a frame from the admission queue."""

    # [5] Capture processing start timestamp
    process_start_ts = int(time.time() * 1000)
    frame.server_process_start_ts = process_start_ts

    # Calculate queue wait time
    queue_wait_ms = process_start_ts - frame.server_receive_ts
    print(f"[UDP WORKER] Frame {frame.frame_id} waited {queue_wait_ms}ms in queue")

    # Run inference
    result = await self.run_inference(frame)

    return result
```

**Key Points**:
- Capture **before** calling inference model
- Difference from `server_receive_ts` = queue wait time
- Important for detecting queue congestion

### 3. Capture `server_send_ts`

**Location**: After inference, before caching result

**Code** (in UDP worker or inference pipeline):

```python
async def run_inference(self, frame: UDPFrame) -> InferenceResult:
    """Run AI inference on the frame."""

    # Run model inference
    if frame.mode == "segmentation":
        result = await self.segmentation_model.predict(frame.image)
    elif frame.mode == "both":
        result = await self.pose_detection_model.predict(frame.image)
    else:
        result = await self.detection_model.predict(frame.image)

    # [6] Capture send timestamp
    send_ts = int(time.time() * 1000)

    # Calculate server processing time
    server_proc_ms = send_ts - frame.server_process_start_ts

    # Add timestamps to result
    result_data = {
        "detections": result.detections,
        "persons": result.persons,
        "processing_time_ms": result.processing_time_ms,

        # Server timestamps
        "server_receive_ts": frame.server_receive_ts,
        "server_process_start_ts": frame.server_process_start_ts,
        "server_send_ts": send_ts,

        # Calculated metrics
        "queue_wait_ms": frame.server_process_start_ts - frame.server_receive_ts,
        "server_proc_ms": server_proc_ms
    }

    # Cache result for Unity to poll via HTTP GET
    await self.result_cache.set(f"{frame.session_id}_{frame.frame_id}", result_data)

    return result_data
```

**Key Points**:
- Capture **after** inference completes
- **Before** or **immediately after** caching (minimal difference)
- Include in HTTP response so Unity can calculate download time

### Server Response Format

**Expected JSON structure** returned to Unity:

```json
{
  "detections": [...],
  "persons": [...],
  "processing_time_ms": 245.3,

  "server_receive_ts": 1714567890123,
  "server_process_start_ts": 1714567890128,
  "server_send_ts": 1714567890373,

  "queue_wait_ms": 5,
  "server_proc_ms": 245
}
```

---

## Unity Parsing Server Timestamps

**Code** (in inference managers):

```csharp
private void HandleV3Response(FrameResponse response)
{
    var jsonData = JsonUtility.FromJson<ServerResponse>(response.rawJson);

    // Extract server timestamps from response
    if (jsonData.server_receive_ts > 0)
    {
        response.trace.serverReceiveTimestamp = jsonData.server_receive_ts;
    }

    if (jsonData.server_process_start_ts > 0)
    {
        response.trace.serverProcessStartTimestamp = jsonData.server_process_start_ts;
    }

    if (jsonData.server_send_ts > 0)
    {
        response.trace.serverSendTimestamp = jsonData.server_send_ts;
    }

    // Capture Unity display timestamp
    response.trace.unityDisplayTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    // Record telemetry (calculations happen in FrameTelemetryTracker)
    m_telemetryTracker?.RecordSuccess(response);
}

[Serializable]
public class ServerResponse
{
    public Detection[] detections;
    public Person[] persons;
    public float processing_time_ms;

    // Server timestamps
    public long server_receive_ts;
    public long server_process_start_ts;
    public long server_send_ts;

    // Server-calculated metrics (optional, Unity recalculates anyway)
    public float queue_wait_ms;
    public float server_proc_ms;
}
```

---

## Calculating Derived Metrics

### Unity Side (FrameTelemetryTracker.cs)

```csharp
public class FrameTelemetryTracker
{
    public void RecordSuccess(FrameResponse response)
    {
        FrameTrace trace = response.trace;

        // Calculate latency metrics
        long latency_ms = trace.unityReceiveTimestamp - trace.unitySendTimestamp;
        long upload_ms = trace.serverReceiveTimestamp - trace.unitySendTimestamp;
        long queue_wait_ms = trace.serverProcessStartTimestamp - trace.serverReceiveTimestamp;
        long server_proc_ms = trace.serverSendTimestamp - trace.serverProcessStartTimestamp;
        long download_ms = trace.unityReceiveTimestamp - trace.serverSendTimestamp;

        // Validate calculation
        if (latency_ms != upload_ms + queue_wait_ms + server_proc_ms + download_ms)
        {
            Debug.LogWarning($"[TELEMETRY] Latency breakdown mismatch: {latency_ms} != {upload_ms + queue_wait_ms + server_proc_ms + download_ms}");
        }

        // Log to CSV
        m_telemetryWriter.WriteFrame(trace, latency_ms, upload_ms, queue_wait_ms, server_proc_ms, download_ms);
    }
}
```

### Server Side (Optional Pre-calculation)

```python
def calculate_metrics(frame: UDPFrame, send_ts: int) -> dict:
    """Calculate latency metrics on server side."""

    metrics = {
        "upload_ms": frame.server_receive_ts - frame.unity_send_ts,
        "queue_wait_ms": frame.server_process_start_ts - frame.server_receive_ts,
        "server_proc_ms": send_ts - frame.server_process_start_ts
    }

    return metrics
```

**Note**: Server can pre-calculate `upload_ms`, `queue_wait_ms`, and `server_proc_ms`, but Unity **must** calculate `download_ms` and total `latency_ms` since server doesn't know when Unity receives the response.

---

## Complete Timeline Example

### Example Scenario

A frame is sent from Unity, processed on the server, and rendered:

```
[Unity] unity_send_ts = 1000
         ↓ (50ms upload)
[Server] server_receive_ts = 1050
         ↓ (5ms queue wait)
[Server] server_process_start_ts = 1055
         ↓ (245ms inference)
[Server] server_send_ts = 1300
         ↓ (80ms download + HTTP polling)
[Unity] unity_receive_ts = 1380
         ↓ (20ms parse + render)
[Unity] unity_display_ts = 1400
```

### Calculated Metrics

```
latency_ms = 1380 - 1000 = 380ms
upload_ms = 1050 - 1000 = 50ms
queue_wait_ms = 1055 - 1050 = 5ms
server_proc_ms = 1300 - 1055 = 245ms
download_ms = 1380 - 1300 = 80ms

Validation: 380 = 50 + 5 + 245 + 80 ✅
```

---

## CSV Output Format

**File**: `telemetry_{session_id}_{timestamp}.csv`

**Location** (Quest 3): `/sdcard/Android/data/com.samples.passthroughcamera/files/`

**Sample Row**:

```csv
timestamp,scene,session_id,frame_id,unity_send_ts,unity_receive_ts,unity_display_ts,unity_drop_ts,server_receive_ts,server_process_start_ts,server_send_ts,latency_ms,upload_ms,queue_wait_ms,server_proc_ms,download_ms,parse_ms,...
2026-04-21T10:30:15Z,Segmentation,abc123,42,1714567890000,1714567890380,1714567890400,0,1714567890050,1714567890055,1714567890300,380,50,5,245,80,20,...
```

---

## Troubleshooting

### Issue 1: Timestamps are Zero

**Symptom**: Some timestamps show `0` in CSV

**Causes**:
- Server not returning timestamps in JSON response
- Unity not parsing server timestamps correctly
- Timestamp not captured before object is destroyed

**Fix**:
1. Check server logs for `server_receive_ts`, `server_process_start_ts`, `server_send_ts` in response
2. Verify Unity `ServerResponse` class has matching field names
3. Add debug logs: `Debug.Log($"Parsed server_receive_ts = {jsonData.server_receive_ts}")`

### Issue 2: Negative Latency Values

**Symptom**: Calculated metrics are negative (e.g., `upload_ms = -50`)

**Causes**:
- Clock skew between Unity (Quest 3) and server (PC)
- Timestamps captured in wrong order
- Timezone mismatch (Unity uses local time instead of UTC)

**Fix**:
1. **Always use UTC**: `DateTimeOffset.UtcNow` in Unity, `time.time()` in Python
2. **Sync clocks**: Ensure Quest 3 and PC have accurate time (use NTP)
3. **Log raw timestamps**: Debug both Unity and server timestamps to verify ordering

### Issue 3: Latency Breakdown Doesn't Sum

**Symptom**: `latency_ms ≠ upload_ms + queue_wait_ms + server_proc_ms + download_ms`

**Causes**:
- Missing intermediate timestamp (e.g., `server_process_start_ts = 0`)
- Timestamps captured at wrong points in code
- Calculation error in formula

**Fix**:
1. Verify all 7 timestamps are non-zero
2. Check timestamp capture points match this guide
3. Add validation in `FrameTelemetryTracker.RecordSuccess()`

---

## Best Practices

### 1. Use Consistent Time Sources

**Unity**:
```csharp
// ✅ Correct - UTC Unix epoch
DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()

// ❌ Wrong - Local time, not comparable with server
DateTime.Now.Ticks
```

**Python**:
```python
# ✅ Correct - UTC Unix epoch
int(time.time() * 1000)

# ❌ Wrong - May have timezone issues
int(datetime.now().timestamp() * 1000)
```

### 2. Capture Timestamps at Precise Moments

- **Before** I/O operations (SendFrame, HTTP GET)
- **After** processing completes (inference, rendering)
- **Immediately** when events occur (UDP receive, queue pull)

### 3. Include Timestamps in All Responses

Server should **always** return:
```json
{
  "server_receive_ts": 1234567890,
  "server_process_start_ts": 1234567895,
  "server_send_ts": 1234567900
}
```

Even if Unity doesn't currently use them, having timestamps enables future analysis.

### 4. Validate Calculations

```csharp
// Add validation in telemetry tracker
long expected = upload_ms + queue_wait_ms + server_proc_ms + download_ms;
if (Math.Abs(latency_ms - expected) > 5) // Allow 5ms tolerance
{
    Debug.LogWarning($"[TELEMETRY] Latency mismatch: {latency_ms} vs {expected}");
}
```

---

## Summary

### Unity Captures (3 timestamps)

| Timestamp | When | Code Location |
|-----------|------|---------------|
| `unity_send_ts` | Before UDP send | Inference managers |
| `unity_receive_ts` | After HTTP GET | `UDPTransportManager` |
| `unity_display_ts` | After rendering | Inference managers |

### Server Captures (3 timestamps)

| Timestamp | When | Code Location |
|-----------|------|---------------|
| `server_receive_ts` | UDP packet arrives | UDP listener |
| `server_process_start_ts` | Before inference | UDP worker |
| `server_send_ts` | After inference | UDP worker |

### Calculated Metrics (5 values)

| Metric | Formula | Calculated By |
|--------|---------|---------------|
| `latency_ms` | unity_receive - unity_send | Unity |
| `upload_ms` | server_receive - unity_send | Unity or Server |
| `queue_wait_ms` | server_process_start - server_receive | Unity or Server |
| `server_proc_ms` | server_send - server_process_start | Unity or Server |
| `download_ms` | unity_receive - server_send | Unity only |

---

**Last Updated**: 2026-04-21
**Version**: 1.0
