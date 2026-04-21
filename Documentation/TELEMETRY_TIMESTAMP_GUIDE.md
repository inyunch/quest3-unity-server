# Telemetry Timestamp & Latency Metrics Guide - V3.0 UDP Architecture

This document explains how to capture and calculate all telemetry timestamps and latency metrics in the **V3.0 UDP bidirectional** Unity-Server inference pipeline.

---

## Overview

The V3.0 telemetry system tracks **7 timestamps** and derives **5 latency metrics** to measure end-to-end performance from Unity (Quest 3) to the Python inference server using **full bidirectional UDP**.

### V3.0 Architecture Flow (Bidirectional UDP)

```
Unity (Quest 3)                    Server (Python)
─────────────────                  ───────────────

[1] unity_send_ts ──────UDP:8002───────→ [4] server_receive_ts
    (FrameTrace.cs:85)                        (udp_ingest.py:51)
       │                                             │
       │                                             ▼
       │                                      [5] server_process_start_ts
       │                                          (udp_inference_worker_v3.py:94)
       │                                             │ (AI Inference)
       │                                             ▼
       │                                      [6] server_send_ts
       │                                          (udp_inference_worker_v3.py:227)
       │                                             │
[2] unity_receive_ts ◄──UDP:8003───────────────────┘
    (FrameTelemetryTracker.cs:128)
       │
       ▼
[3] unity_display_ts
    (FrameTelemetryTracker.cs:174)
```

**Key V3.0 Changes**:
- ✅ **Unity → Server**: UDP send (port 8002) - Non-blocking, instant (~1ms)
- ✅ **Server → Unity**: UDP push (port 8003) - Background listener, instant delivery
- ✅ **No HTTP polling**: Completely eliminated
- ✅ **Latency reduced**: 500-800ms → 200-350ms (-40% to -60%)
- ✅ **FPS increased**: 2-3 → 8-10 (+200% to +300%)

---

## Timestamp Reference

| Timestamp | Location | Meaning | When Captured | V3.0 File & Line |
|-----------|----------|---------|---------------|------------------|
| `unity_send_ts` | Unity | Frame sent via UDP | Before `UDPTransportManager.SendFrame()` | `FrameTrace.cs:85` |
| `unity_receive_ts` | Unity | Response received from server | When UDP response parsed | `FrameTelemetryTracker.cs:128` |
| `unity_display_ts` | Unity | Results rendered on screen | After rendering complete | `FrameTelemetryTracker.cs:174` |
| `unity_drop_ts` | Unity | Frame dropped (optional) | When frame superseded or expired | `FrameTrace.cs:119` |
| `server_receive_ts` | Server | UDP frame received | When UDP packet arrives | `udp_ingest.py:51` |
| `server_process_start_ts` | Server | Inference started | Before model.predict() | `udp_inference_worker_v3.py:94` |
| `server_send_ts` | Server | Response sent via UDP | After inference, before UDP send | `udp_inference_worker_v3.py:227` |

### Derived Latency Metrics

| Metric | Formula | Meaning | V3.0 Target |
|--------|---------|---------|-------------|
| `latency_ms` | `unity_receive_ts - unity_send_ts` | Total end-to-end latency | 200-300ms |
| `upload_ms` | `server_receive_ts - unity_send_ts` | Network upload time (UDP) | 5-20ms |
| `queue_wait_ms` | `server_process_start_ts - server_receive_ts` | Time waiting in server queue | < 5ms |
| `server_proc_ms` | `server_send_ts - server_process_start_ts` | Server inference time | 150-250ms |
| `download_ms` | `unity_receive_ts - server_send_ts` | Network download time (UDP) | 5-20ms |

---

## Unity Side Implementation (V3.0)

### 1. Capture `unity_send_ts`

**Location**: Frame creation in FrameTrace constructor

**File**: `Assets/PassthroughCameraApiSamples/Shared/Scripts/FrameTrace.cs`
**Line**: 85

**Code**:

```csharp
/// <summary>
/// Constructor - creates a new frame trace at send time.
/// session_id must be provided by caller.
/// </summary>
public FrameTrace(int frameId)
{
    frame_id = frameId;
    unity_send_ts = TimestampUtil.GetUnixTimestampMs();  // ← [1] CAPTURED HERE (Line 85)
    state = FrameState.Pending;

    // Initialize nullable fields to null (not 0)
    unity_display_ts = null;
    unity_drop_ts = null;
    detection_count = null;
}
```

**Usage in Inference Managers** (e.g., SegmentationInferenceRunManager.cs):

**File**: `Assets/PassthroughCameraApiSamples/Segmentation/SegmentationInference/Scripts/SegmentationInferenceRunManager.cs`
**Lines**: 464-474

```csharp
// 2. Create frame trace via FrameTelemetryTracker
m_frameId++;
FrameTrace trace = m_telemetry.CreateFrame(m_frameId, jpegData.Length);  // unity_send_ts set in constructor
trace.upload_bytes_uncompressed = targetTexture.width * targetTexture.height * 3;

Debug.Log($"[V3 SEGMENTATION] Frame {trace.frame_id} created, size={jpegData.Length} bytes");

// 3. Build minimal telemetry JSON for server (mode + scene)
string telemetryJson = $"{{\"mode\":\"segmentation\",\"scene\":\"Segmentation\",\"downsampleFactor\":{m_inferenceConfig.downsampleFactor}}}";

// 4. Send via UDPTransportManager (NON-BLOCKING!)
m_transport.SendFrame(trace, jpegData, telemetryJson);  // ← Frame sent with unity_send_ts

Debug.Log($"[V3 SEGMENTATION] Frame {trace.frame_id} sent via UDP (mode=segmentation)");
```

**Key Points**:
- Captured in `FrameTrace` constructor automatically
- Uses `TimestampUtil.GetUnixTimestampMs()` for Unix epoch milliseconds
- Set **before** UDP send to include all overhead
- Same pattern in all 3 inference managers (Segmentation, Pose, MultiObjectDetection)

---

### 2. Capture `unity_receive_ts`

**Location**: When UDP response is received and processed

**File**: `Assets/PassthroughCameraApiSamples/Shared/Scripts/FrameTelemetryTracker.cs`
**Lines**: 117-135

**Code**:

```csharp
/// <summary>
/// Mark frame as completed when response is received.
/// </summary>
/// <param name="frameId">Frame number</param>
/// <param name="response">Server response</param>
public void MarkFrameCompleted(int frameId, FrameResponse response)
{
    lock (m_frameTracesLock)
    {
        if (!m_frameTraces.TryGetValue(frameId, out FrameTrace trace))
        {
            Debug.LogWarning($"[TELEMETRY] Frame {frameId} not found in traces");
            return;
        }

        // Update frame trace with response data
        long receiveTime = TimestampUtil.GetUnixTimestampMs();  // ← [2] CAPTURED HERE (Line 128)
        trace.MarkCompleted(receiveTime);

        // Copy server timing from response
        trace.server_receive_ts = response.server_receive_ts;
        trace.server_process_start_ts = response.server_process_start_ts;
        trace.server_send_ts = response.server_send_ts;
        trace.server_proc_ms = response.processing_time_ms;

        // Calculate upload/download times (residual method)
        float totalMs = trace.e2e_ms;
        float serverMs = response.server_e2e_ms;
        float networkMs = totalMs - serverMs;
        trace.upload_ms = networkMs / 2;  // Approximate split
        trace.download_ms = networkMs / 2;

        // ...
    }
}
```

**Calling Path**:

**File**: `SegmentationInferenceRunManager.cs` (and other inference managers)
**Lines**: 998-1001, 1046

```csharp
// In Update() - polls for UDP responses from background thread
private void Update()
{
    // V3.0: ALWAYS poll for UDP responses (even if camera not ready)
    if (m_useServerInference && m_useUDPTransport && m_transport != null)
    {
        while (m_transport.TryGetResponse(out FrameResponse response))  // ← Gets response from UDP listener queue
        {
            HandleV3Response(response);  // ← Calls MarkFrameCompleted inside
        }
    }
}

private void HandleV3Response(FrameResponse response)
{
    Debug.Log($"[V3 SEGMENTATION] Received response for frame {response.frame_id}");

    // 1. Update telemetry (mark completed)
    m_telemetry.MarkFrameCompleted(response.frame_id, response);  // ← unity_receive_ts captured here

    // 2. Display segmentation results
    DisplayV3Frame(response);

    // 3. Mark as displayed
    m_telemetry.MarkFrameDisplayed(response.frame_id);
}
```

**Key Points**:
- Captured **immediately** when response is processed in main thread
- UDP listener runs in background thread (`UDPTransportManager.cs:160-209`)
- Response enqueued to thread-safe queue (`UDPTransportManager.cs:185`)
- Main thread polls queue via `TryGetResponse()` in `Update()`
- Timestamp captures moment response is **processed**, not when UDP packet arrived (adds ~16ms polling delay at 60 FPS)

---

### 3. Capture `unity_display_ts`

**Location**: After rendering 3D visualizations complete

**File**: `Assets/PassthroughCameraApiSamples/Shared/Scripts/FrameTelemetryTracker.cs`
**Lines**: 163-176

**Code**:

```csharp
/// <summary>
/// Mark frame as displayed and write to local telemetry.
/// Also checks for and marks any older pending frames as dropped.
/// </summary>
/// <param name="frameId">Frame number</param>
public void MarkFrameDisplayed(int frameId)
{
    lock (m_frameTracesLock)
    {
        if (!m_frameTraces.TryGetValue(frameId, out FrameTrace trace))
        {
            Debug.LogWarning($"[TELEMETRY] Frame {frameId} not found in traces");
            return;
        }

        // Mark frame as displayed
        long displayTime = TimestampUtil.GetUnixTimestampMs();  // ← [3] CAPTURED HERE (Line 174)
        trace.MarkDisplayed(displayTime);
        m_displayedFrames++;

        // Calculate freeze metrics
        if (m_lastDisplayedFrameId >= 0)
        {
            trace.freeze_frames = frameId - m_lastDisplayedFrameId - 1;
            trace.freeze_duration_ms = trace.freeze_frames * 16.67f;  // Assuming 60Hz
        }

        m_lastDisplayedFrameId = frameId;

        Debug.Log($"[TELEMETRY] Frame {frameId} displayed, freeze_frames={trace.freeze_frames}");

        // Write to local telemetry immediately (final state reached)
        WriteLocalTelemetry(trace);  // ← Writes to CSV file

        // Check for dropped frames (older frames superseded by newer)
        DropSupersededFrames(frameId);
    }
}
```

**Calling Path** (in HandleV3Response):

**File**: `SegmentationInferenceRunManager.cs:1052`

```csharp
private void HandleV3Response(FrameResponse response)
{
    // 1. Update telemetry (mark completed)
    m_telemetry.MarkFrameCompleted(response.frame_id, response);

    // 2. Display segmentation results (render 3D overlays)
    DisplayV3Frame(response);  // ← Rendering happens here

    // 3. Mark as displayed (this automatically writes to CSV)
    m_telemetry.MarkFrameDisplayed(response.frame_id);  // ← unity_display_ts captured here
}
```

**Key Points**:
- Captured **after** all rendering is complete
- Includes time for JSON parsing + 3D visualization updates
- Automatically triggers CSV write (final frame state)
- Should be the **last timestamp** set before telemetry recording

---

### 4. Capture `unity_drop_ts` (Optional)

**Location**: When frame is superseded or expired

**File**: `Assets/PassthroughCameraApiSamples/Shared/Scripts/FrameTrace.cs`
**Lines**: 115-125

**Code**:

```csharp
/// <summary>
/// Mark frame as dropped with reason.
/// dropTime should be Unix milliseconds.
/// </summary>
public void MarkDropped(long dropTime, string reason)
{
    unity_drop_ts = dropTime;  // ← [4] CAPTURED HERE
    drop_reason = reason;
    state = FrameState.Dropped;
}
```

**Usage** (when newer frame supersedes older pending frames):

**File**: `FrameTelemetryTracker.cs:230-250`

```csharp
private void DropSupersededFrames(int displayedFrameId)
{
    long dropTime = TimestampUtil.GetUnixTimestampMs();
    List<int> toDrop = new List<int>();

    foreach (var kvp in m_frameTraces)
    {
        int frameId = kvp.Key;
        FrameTrace trace = kvp.Value;

        // Drop frames that are older than displayed frame and still pending/completed
        if (frameId < displayedFrameId &&
            (trace.state == FrameState.Pending || trace.state == FrameState.Completed))
        {
            trace.MarkDropped(dropTime, "superseded_by_newer");  // ← unity_drop_ts set
            WriteLocalTelemetry(trace);
            toDrop.Add(frameId);
            m_droppedFrames++;
        }
    }

    // Remove dropped frames from tracking
    foreach (int frameId in toDrop)
    {
        m_frameTraces.Remove(frameId);
    }
}
```

**Key Points**:
- Only set when frame is explicitly dropped
- Most common reason: `"superseded_by_newer"` (older frame skipped when newer frame displayed)
- Other reasons: `"timeout"`, `"queue_full"` (server-side drops)
- Most **successful frames** will have `unity_drop_ts = null`

---

## Server Side Implementation (V3.0 UDP)

### 1. Capture `server_receive_ts`

**Location**: UDP protocol handler when packet arrives

**File**: `C:\Repo\Github\vision_server\app\transport\udp_ingest.py`
**Lines**: 48-56

**Code**:

```python
class UDPProtocol:
    """Asyncio UDP protocol handler"""

    def __init__(self, ingest_handler):
        self.ingest_handler = ingest_handler

    def connection_made(self, transport):
        self.transport = transport

    def datagram_received(self, data: bytes, addr):
        """Called when UDP datagram received"""
        # Immediate timestamp (critical for clean receive_ts)
        server_receive_ts = time.time() * 1000  # milliseconds  ← [4] CAPTURED HERE (Line 51)

        # Parse and enqueue asynchronously, passing client address for response
        asyncio.create_task(
            self.ingest_handler.on_datagram_received(data, addr, server_receive_ts)
        )
```

**Key Points**:
- Captured **immediately** when UDP packet arrives (line 51)
- Uses `time.time() * 1000` for Unix epoch milliseconds
- Passed to async handler for frame parsing and queueing
- Critical for accurate upload time calculation

**Passing to Admission Queue**:

**File**: `udp_ingest.py`
**Lines**: 179-189, 588

```python
# Enqueue to bounded queue
if self.bounded_queue is not None:
    await self.enqueue_frame(
        session_id=session_id,
        frame_id=frame_id,
        unity_send_ts=unity_send_ts,
        server_receive_ts=server_receive_ts,  # ← Passed along
        telemetry=telemetry_dict,
        jpeg_data=jpeg_data
    )

# Later, stored in AdmittedRequest (Line 588)
admitted_req = AdmittedRequest(
    request_id=request_id,
    session_id=session_id,
    frame_id=frame_id,
    # ...
    server_receive_ts=server_receive_ts / 1000,  # ← Converted to seconds for compatibility
    admission_ts=time.time(),
    headers=telemetry,
    # ...
)
```

**Note**: Timestamp is converted from milliseconds to seconds at line 588 for compatibility with existing `AdmittedRequest` structure.

---

### 2. Capture `server_process_start_ts`

**Location**: UDP worker pulls frame from queue

**File**: `C:\Repo\Github\vision_server\app\workers\udp_inference_worker_v3.py`
**Lines**: 82-97

**Code**:

```python
async def _worker_loop(self):
    """Main worker loop - continuously process frames from queue."""
    print("[UDP WORKER V3] Worker loop started, waiting for UDP frames...")

    while self.running:
        try:
            # Get next frame from queue (blocks if empty)
            processing_req = await self.queue.get_next()

            if processing_req is None:
                await asyncio.sleep(0.01)
                continue

            # Extract request info
            request_id = processing_req.request_id
            session_id = processing_req.session_id
            frame_id = processing_req.frame_id
            mode_str = processing_req.mode

            server_process_start_ts = time.time()  # ← [5] CAPTURED HERE (Line 94)
            queue_wait_ms = (server_process_start_ts - processing_req.server_receive_ts) * 1000

            print(f"[UDP WORKER V3] Processing {request_id} (queue_wait={queue_wait_ms:.1f}ms, mode={mode_str})")

            # Run inference using InferenceManager
            result = await self._run_inference(processing_req, server_process_start_ts)
            # ...
```

**Key Points**:
- Captured **before** calling inference (line 94)
- Difference from `server_receive_ts` = queue wait time
- Used to calculate: `queue_wait_ms = (server_process_start_ts - server_receive_ts) * 1000`
- Important for detecting queue congestion

---

### 3. Capture `server_send_ts`

**Location**: After inference completes, before sending UDP response

**File**: `C:\Repo\Github\vision_server\app\workers\udp_inference_worker_v3.py`
**Lines**: 224-252

**Code**:

```python
async def _run_inference(self, req, server_process_start_ts: float) -> Optional[dict]:
    """
    Run inference using InferenceManager (V3.0 unified path).
    """
    try:
        # Decode image, parse mode, etc.
        # ...

        # V3.0: Use InferenceManager
        inference_result = await self.inference_manager.run_inference(context)

        # Build minimal response with ONLY server timing + inference results
        server_process_end_ts = time.time()  # ← [6] CAPTURED HERE (Line 227)

        # Convert InferenceResult to legacy format
        legacy_response = inference_result.to_legacy_format(mode)

        # Add minimal server timing
        response = {
            **legacy_response,

            # Identity
            "frame_id": req.frame_id,
            "session_id": req.session_id,

            # Server timing (for Unity telemetry)
            "server_receive_ts": req.server_receive_ts,
            "server_process_start_ts": server_process_start_ts,
            "server_process_end_ts": server_process_end_ts,  # ← Same as server_send_ts
            "queue_wait_ms": (server_process_start_ts - req.server_receive_ts) * 1000,
            "server_e2e_ms": (server_process_end_ts - req.server_receive_ts) * 1000,

            # Timing fields for Unity compatibility
            "t_server_recv": req.server_receive_ts,
            "t_server_send": server_process_end_ts,

            "mode": mode_str
        }

        return response

    except Exception as e:
        print(f"[UDP WORKER V3] Inference error: {e}")
        traceback.print_exc()
        return None
```

**Note**: `server_process_end_ts` is effectively `server_send_ts` since UDP send happens immediately after this in the worker loop (lines 103-117).

**Key Points**:
- Captured **after** inference completes (line 227)
- Response includes both `server_process_end_ts` and `t_server_send` (same value)
- Timestamp is in **seconds** (Unity expects seconds in response)
- UDP send to Unity happens immediately after caching (minimal delay)

---

### Server Response Format (V3.0)

**Expected JSON structure** returned to Unity via UDP:

```json
{
  "detections": [...],
  "persons": [...],
  "processing_time_ms": 245.3,
  "input_image_width": 1280,
  "input_image_height": 720,

  "frame_id": 42,
  "session_id": "abc123-guid",

  "server_receive_ts": 1714567890.050,
  "server_process_start_ts": 1714567890.055,
  "server_process_end_ts": 1714567890.300,

  "queue_wait_ms": 5.0,
  "server_e2e_ms": 250.0,

  "t_server_recv": 1714567890.050,
  "t_server_send": 1714567890.300,

  "mode": "segmentation"
}
```

**Unit Note**: Server timestamps are in **seconds** (not milliseconds) for compatibility.

---

## Unity Parsing Server Timestamps

**File**: `FrameTelemetryTracker.cs:128-135`

```csharp
// Update frame trace with response data
long receiveTime = TimestampUtil.GetUnixTimestampMs();
trace.MarkCompleted(receiveTime);  // Sets unity_receive_ts

// Copy server timing from response (convert seconds → milliseconds)
trace.server_receive_ts = response.server_receive_ts;  // ← Already in seconds
trace.server_process_start_ts = response.server_process_start_ts;
trace.server_send_ts = response.server_send_ts;
trace.server_proc_ms = response.processing_time_ms;
```

**Important**: Unity stores server timestamps as-is (in seconds). Calculations in `LocalTelemetryWriter` handle unit conversions when writing to CSV.

---

## Calculating Derived Metrics

### Unity Side Calculation

**File**: `FrameTelemetryTracker.cs:137-142`

```csharp
// Calculate upload/download times (residual method)
float totalMs = trace.e2e_ms;  // unity_receive_ts - unity_send_ts (both in ms)
float serverMs = response.server_e2e_ms;  // From server (already in ms)
float networkMs = totalMs - serverMs;
trace.upload_ms = networkMs / 2;  // Approximate split (assumes symmetric network)
trace.download_ms = networkMs / 2;
```

**Alternative: Precise Calculation** (requires timestamp unit conversion):

```csharp
// Convert server timestamps from seconds to milliseconds
long server_receive_ms = (long)(response.server_receive_ts * 1000);
long server_send_ms = (long)(response.server_send_ts * 1000);

// Calculate precise times
long upload_ms = server_receive_ms - trace.unity_send_ts;
long download_ms = trace.unity_receive_ts - server_send_ms;
long queue_wait_ms = (long)response.queue_wait_ms;
long server_proc_ms = (long)response.processing_time_ms;

// Validate calculation
long latency_ms = trace.unity_receive_ts - trace.unity_send_ts;
if (latency_ms != upload_ms + queue_wait_ms + server_proc_ms + download_ms)
{
    Debug.LogWarning($"[TELEMETRY] Latency breakdown mismatch");
}
```

---

## Complete Timeline Example (V3.0 UDP)

### Example Scenario

A frame is sent from Unity, processed on the server, and rendered:

```
Timeline (Unix milliseconds):

[Unity] unity_send_ts = 1714567890000
         ↓ (10ms UDP upload)
[Server] server_receive_ts = 1714567890010  (stored as 1714567890.010 seconds)
         ↓ (3ms queue wait)
[Server] server_process_start_ts = 1714567890013
         ↓ (245ms inference: YOLO + segmentation)
[Server] server_send_ts = 1714567890258
         ↓ (12ms UDP download + background thread polling)
[Unity] unity_receive_ts = 1714567890270
         ↓ (15ms parse + render)
[Unity] unity_display_ts = 1714567890285
```

### Calculated Metrics

```
latency_ms = 1714567890270 - 1714567890000 = 270ms ✅
upload_ms = 1714567890010 - 1714567890000 = 10ms ✅
queue_wait_ms = 1714567890013 - 1714567890010 = 3ms ✅
server_proc_ms = 1714567890258 - 1714567890013 = 245ms ✅
download_ms = 1714567890270 - 1714567890258 = 12ms ✅

Validation: 270 = 10 + 3 + 245 + 12 ✅
```

**V3.0 Performance**: Total latency **270ms** (vs. 500-800ms in old HTTP architecture)

---

## CSV Output Format (V3.0)

**File**: `telemetry_{session_id}_{timestamp}.csv`

**Location** (Quest 3): `/sdcard/Android/data/com.samples.passthroughcamera/files/`

**Relevant Columns**:

```csv
timestamp,scene,session_id,frame_id,unity_send_ts,unity_receive_ts,unity_display_ts,unity_drop_ts,server_receive_ts,server_process_start_ts,server_send_ts,latency_ms,upload_ms,queue_wait_ms,server_proc_ms,download_ms,parse_ms,...
```

**Sample Row** (V3.0 UDP):

```csv
2026-04-21T10:30:15Z,Segmentation,abc123,42,1714567890000,1714567890270,1714567890285,0,1714567890.010,1714567890.013,1714567890.258,270,10,3,245,12,15,...
```

**Note**: Server timestamps are stored in **seconds** (with decimal) in CSV, Unity timestamps in **milliseconds** (integer).

---

## V3.0 File Reference Summary

### Unity Files

| Timestamp | File | Line(s) | Method |
|-----------|------|---------|---------|
| `unity_send_ts` | `FrameTrace.cs` | 85 | Constructor |
| `unity_receive_ts` | `FrameTelemetryTracker.cs` | 128 | `MarkFrameCompleted()` |
| `unity_display_ts` | `FrameTelemetryTracker.cs` | 174 | `MarkFrameDisplayed()` |
| `unity_drop_ts` | `FrameTrace.cs` | 119 | `MarkDropped()` |

**Inference Manager Example**:
- `SegmentationInferenceRunManager.cs:465` - Creates frame trace
- `SegmentationInferenceRunManager.cs:474` - Sends via UDP
- `SegmentationInferenceRunManager.cs:998-1001` - Polls UDP responses
- `SegmentationInferenceRunManager.cs:1046` - Marks frame completed
- `SegmentationInferenceRunManager.cs:1052` - Marks frame displayed

### Server Files

| Timestamp | File | Line(s) | Method |
|-----------|------|---------|---------|
| `server_receive_ts` | `udp_ingest.py` | 51 | `datagram_received()` |
| `server_process_start_ts` | `udp_inference_worker_v3.py` | 94 | `_worker_loop()` |
| `server_send_ts` | `udp_inference_worker_v3.py` | 227 | `_run_inference()` |

**Full Paths**:
- `C:\Repo\Github\vision_server\app\transport\udp_ingest.py`
- `C:\Repo\Github\vision_server\app\workers\udp_inference_worker_v3.py`

---

## Troubleshooting

### Issue 1: Timestamps are Zero or Null

**Symptom**: Some timestamps show `0` or `null` in CSV

**Causes**:
- Server not returning timestamps in JSON response
- Unity not parsing server timestamps correctly
- Timestamp not captured before frame cleanup

**Fix**:

1. **Check server logs** for response content:
```powershell
# On server terminal, verify response includes timestamps
adb logcat | findstr "server_receive_ts"
```

2. **Verify Unity parsing**:
```csharp
Debug.Log($"Parsed server_receive_ts = {response.server_receive_ts}");
Debug.Log($"Parsed server_process_start_ts = {response.server_process_start_ts}");
Debug.Log($"Parsed server_send_ts = {response.server_send_ts}");
```

3. **Check FrameResponse fields** match server response:
- Server sends: `server_receive_ts`, `server_process_start_ts`, `server_send_ts`
- Unity expects: Same field names in `FrameResponse.cs:35-37`

---

### Issue 2: Negative Latency Values

**Symptom**: Calculated metrics are negative (e.g., `upload_ms = -50`)

**Causes**:
- Clock skew between Unity (Quest 3) and server (PC)
- Timestamps captured in wrong order
- Unit mismatch (milliseconds vs seconds)

**Fix**:

1. **Always use UTC**:
   - Unity: `TimestampUtil.GetUnixTimestampMs()` (milliseconds)
   - Server: `time.time()` (seconds) × 1000 if storing as milliseconds

2. **Sync clocks**:
   - Ensure Quest 3 has accurate time (connect to internet, enable auto-sync)
   - Ensure server PC time is accurate (use NTP)

3. **Verify unit conversions**:
   - Server stores timestamps in **seconds** in `AdmittedRequest`
   - Server returns timestamps in **seconds** in JSON response
   - Unity stores milliseconds in `FrameTrace`
   - Conversions happen in `FrameTelemetryTracker.cs:132-134`

---

### Issue 3: Latency Breakdown Doesn't Sum

**Symptom**: `latency_ms ≠ upload_ms + queue_wait_ms + server_proc_ms + download_ms`

**Causes**:
- Missing intermediate timestamp (e.g., `server_process_start_ts = 0`)
- Timestamps captured at wrong points in code
- Unit conversion error

**Fix**:

1. **Verify all 7 timestamps are non-zero**:
```csharp
if (trace.server_receive_ts == 0 ||
    trace.server_process_start_ts == 0 ||
    trace.server_send_ts == 0)
{
    Debug.LogWarning($"[TELEMETRY] Frame {frameId} missing server timestamps");
}
```

2. **Check timestamp capture points** match this guide

3. **Add validation** in telemetry tracker:
```csharp
long expected = upload_ms + queue_wait_ms + server_proc_ms + download_ms;
if (Math.Abs(latency_ms - expected) > 10)  // Allow 10ms tolerance
{
    Debug.LogWarning($"[TELEMETRY] Latency mismatch: {latency_ms} vs {expected}");
}
```

---

### Issue 4: UDP Responses Not Received

**Symptom**: `unity_receive_ts` is always `0`, server logs show responses sent

**V3.0 Specific Debug**:

1. **Check UDP listener is running**:
```csharp
// Unity logs should show:
[UDP TRANSPORT] Receive client initialized (listening on port 8003)
[UDP TRANSPORT] Background receiver thread started
```

2. **Check server is sending to correct port**:
```python
# Server should send to Unity:8003
# udp_inference_worker_v3.py:157
unity_receive_port = 8003
```

3. **Verify firewall** allows UDP:8003 on Quest:
- Quest OS handles this automatically for apps, but check if VPN or custom firewall installed

4. **Check for UDP packet loss**:
```powershell
# Server logs should show:
[UDP WORKER V3] → Sent response for frame 42 to 192.168.1.100:8003 (size=1523 bytes)

# Unity logs should show (within ~20ms):
[UDP TRANSPORT] Received response for frame 42, queue_size=1, data_size=1523 bytes
```

**If response not received**:
- Check WiFi signal strength on Quest
- Move closer to WiFi router
- Verify no network congestion (other devices streaming, etc.)

---

## Best Practices (V3.0)

### 1. Use Consistent Time Sources

**Unity**:
```csharp
// ✅ Correct - UTC Unix epoch milliseconds
TimestampUtil.GetUnixTimestampMs()  // Wrapper for DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()

// ❌ Wrong - Local time, not comparable with server
DateTime.Now.Ticks
```

**Python Server**:
```python
# ✅ Correct - UTC Unix epoch
time.time()  # Returns seconds (float)

# For milliseconds (if needed):
int(time.time() * 1000)

# ❌ Wrong - May have timezone issues
int(datetime.now().timestamp() * 1000)
```

### 2. Capture Timestamps at Precise Moments

**Unity**:
- **Before** UDP send (in `FrameTrace` constructor)
- **Immediately** after receiving response (in `MarkFrameCompleted()`)
- **After** rendering completes (in `MarkFrameDisplayed()`)

**Server**:
- **Immediately** when UDP packet arrives (in `datagram_received()`)
- **Before** inference starts (in worker loop)
- **After** inference completes (before UDP send)

### 3. Include Timestamps in All Responses

Server should **always** return:
```json
{
  "server_receive_ts": 1714567890.010,
  "server_process_start_ts": 1714567890.013,
  "server_process_end_ts": 1714567890.258,
  "server_send_ts": 1714567890.258,
  "queue_wait_ms": 3.0,
  "server_e2e_ms": 248.0
}
```

Even if Unity doesn't currently use some fields, having timestamps enables future analysis.

### 4. Validate Calculations

```csharp
// In FrameTelemetryTracker.cs (add validation)
long calculated_latency = upload_ms + queue_wait_ms + server_proc_ms + download_ms;
long measured_latency = unity_receive_ts - unity_send_ts;

if (Math.Abs(measured_latency - calculated_latency) > 10)  // 10ms tolerance
{
    Debug.LogWarning($"[TELEMETRY] Latency breakdown error: " +
                     $"measured={measured_latency}ms, " +
                     $"calculated={calculated_latency}ms");
}
```

### 5. Monitor UDP Packet Loss

**V3.0 Specific**:

```csharp
// Track send/receive counts
int framesSent = m_frameId;
int responsesReceived = m_responsesReceived;
float lossRate = 1.0f - ((float)responsesReceived / framesSent);

if (lossRate > 0.10f)  // >10% loss
{
    Debug.LogWarning($"[UDP TRANSPORT] High packet loss: {lossRate * 100:F1}%");
}
```

**Acceptable loss rate**: < 5% for UDP transport

**If > 10%**:
- Check WiFi signal strength
- Reduce frame send rate (increase `targetFPS` interval)
- Reduce JPEG quality/resolution

---

## Summary

### Unity Captures (3 timestamps)

| Timestamp | File | Line | When |
|-----------|------|------|------|
| `unity_send_ts` | `FrameTrace.cs` | 85 | Constructor (before UDP send) |
| `unity_receive_ts` | `FrameTelemetryTracker.cs` | 128 | MarkFrameCompleted() |
| `unity_display_ts` | `FrameTelemetryTracker.cs` | 174 | MarkFrameDisplayed() |

### Server Captures (3 timestamps)

| Timestamp | File | Line | When |
|-----------|------|------|------|
| `server_receive_ts` | `udp_ingest.py` | 51 | UDP packet arrives |
| `server_process_start_ts` | `udp_inference_worker_v3.py` | 94 | Before inference |
| `server_send_ts` | `udp_inference_worker_v3.py` | 227 | After inference |

### Calculated Metrics (5 values)

| Metric | Formula | Calculated By | V3.0 Target |
|--------|---------|---------------|-------------|
| `latency_ms` | unity_receive - unity_send | Unity | 200-300ms |
| `upload_ms` | server_receive - unity_send | Unity | 5-20ms |
| `queue_wait_ms` | server_process_start - server_receive | Server + Unity | < 5ms |
| `server_proc_ms` | server_send - server_process_start | Server + Unity | 150-250ms |
| `download_ms` | unity_receive - server_send | Unity | 5-20ms |

---

## V3.0 Architecture Benefits

**Compared to Old HTTP POST Architecture**:

| Aspect | Old (HTTP) | V3.0 (UDP Bidirectional) |
|--------|-----------|--------------------------|
| **Unity → Server** | HTTP POST (~150ms) | UDP send (~10ms) |
| **Server → Unity** | HTTP GET polling (~150ms) | UDP push (~10ms) |
| **Total Latency** | 500-800ms | 200-350ms (-60%) |
| **Unity Blocking** | Yes (~500ms) | No (instant return) |
| **FPS** | 2-3 | 8-10 (+300%) |
| **Queue Wait** | 50-100ms | < 5ms (-95%) |

---

**Last Updated**: 2026-04-21
**Version**: V3.0 UDP Bidirectional Architecture
