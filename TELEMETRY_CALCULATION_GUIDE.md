# Telemetry Metrics Calculation Guide

**Date**: 2026-04-15
**Purpose**: Complete documentation of how each Excel column is calculated and validated

---

## Table of Contents

1. [Architecture Overview](#architecture-overview)
2. [Field Calculation Methods](#field-calculation-methods)
3. [Data Flow Diagram](#data-flow-diagram)
4. [Validation Rules](#validation-rules)
5. [Known Issues and Solutions](#known-issues-and-solutions)

---

## Architecture Overview

### Parallel Processing Model

```
Unity (60 FPS)           Server (Async)           Excel Logger
    |                         |                         |
    |-- Frame N sent -------->|                         |
    |   (unity_send_ts)       |                         |
    |                         |-- Processing N          |
    |                         |   (server_proc_ms)      |
    |<- Frame N response -----|                         |
    |   (unity_receive_ts)    |                         |
    |                         |                         |
    |-- Frame N+1 sent ------->|                         |
    |   + Frame N headers ---->|                         |
    |   (delayed telemetry)   |                         |
    |                         |                         |
    |                         |-- Log Frame N --------->|
    |                         |   (complete lifecycle)  |
```

### Delayed Telemetry Pattern

**Key Concept**: Frame N+1's HTTP request contains Frame N's final lifecycle data via `X-Prev-*` headers.

**Why?**: Unity doesn't know if Frame N will be displayed or dropped until Frame N+1 arrives.

---

## Field Calculation Methods

### 1. Identity Fields (PRIMARY KEY)

#### `timestamp`
- **Source**: Server (Python)
- **Calculation**: `datetime.now().strftime("%Y-%m-%d %H:%M:%S.%f")[:-3]`
- **When**: At Excel log write time
- **Purpose**: Log write time (not inference time)
- **Format**: `2026-04-15 17:13:56.143`

#### `scene`
- **Source**: HTTP header `X-Scene`
- **Calculation**: Direct passthrough
- **Values**: `"PoseEstimation"`, `"MultiObjectDetection"`, `"Segmentation"`
- **File**: Unity sends via header in all modes

#### `session_id`
- **Source**: Unity (C#)
- **Calculation**: `System.Guid.NewGuid().ToString()` at Start()
- **When**: Once per Unity session (app start or scene load)
- **Purpose**: Distinguish multiple runs of same scene
- **Format**: GUID string (e.g., `"a3f8b2c1-4d5e-6f7g-8h9i-0j1k2l3m4n5o"`)
- **File**:
  - MultiObjectDetection: `SentisInferenceRunManager.cs:109`
  - PoseEstimation: `PoseInferenceRunManager.cs:86`
  - Segmentation: `SegmentationInferenceRunManager.cs:106`

#### `frame_id`
- **Source**: Unity (C#)
- **Calculation**: `m_frameId++` (sequential counter)
- **When**: Each time SendImage() is called
- **Range**: 0 to N (resets each session)
- **File**: Same as session_id files

---

### 2. Unity Timestamps (Client-Side)

**All Unity timestamps use**: `TimestampUtil.GetUnixTimestampMs()` (Unix milliseconds since epoch)

#### `unity_send_ts`
- **Calculation**:
  ```csharp
  // FrameTrace.cs:61
  unity_send_ts = TimestampUtil.GetUnixTimestampMs();
  ```
- **When**: Immediately before HTTP request sent
- **Measured by**: Unity (client)
- **Excel Format**: `2026-04-15 17:13:56.143` (converted from Unix ms)

#### `unity_receive_ts`
- **Calculation**:
  ```csharp
  // InferenceRunManager.cs (all 3 modes)
  long receiveTime = TimestampUtil.GetUnixTimestampMs();
  trace.MarkCompleted(receiveTime);

  // FrameTrace.cs:76
  unity_receive_ts = receiveTime;
  e2e_ms = unity_receive_ts - unity_send_ts;
  ```
- **When**: When HTTP response received (success or error)
- **Measured by**: Unity (client)

#### `unity_display_ts`
- **Calculation**:
  ```csharp
  // InferenceRunManager.cs:TryDisplayNewestFrame()
  long currentTimestamp = TimestampUtil.GetUnixTimestampMs();
  newest.MarkDisplayed(currentTimestamp);

  // FrameTrace.cs:87
  unity_display_ts = displayTime;
  state = FrameState.Displayed;
  ```
- **When**: When frame is actually displayed to user
- **Measured by**: Unity (client)
- **Null if**: Frame was dropped or failed

#### `unity_drop_ts`
- **Calculation**:
  ```csharp
  // InferenceRunManager.cs:TryDisplayNewestFrame()
  long currentTimestamp = TimestampUtil.GetUnixTimestampMs();
  olderFrame.MarkDropped(currentTimestamp, $"superseded_by_newer_{newest.frame_id}");

  // FrameTrace.cs:97
  unity_drop_ts = dropTime;
  drop_reason = reason;
  state = FrameState.Dropped;
  ```
- **When**: When frame response received but never displayed (superseded by newer)
- **Measured by**: Unity (client)
- **Null if**: Frame was displayed or failed

---

### 3. Server Timestamps (Server-Side)

**All server timestamps use**: `time.time() * 1000` (Python Unix milliseconds)

#### `server_receive_ts`
- **Calculation**:
  ```python
  # infer_human.py / segmentation.py
  start_time = time.time() * 1000  # Current frame's server recv time

  # Sent back in response
  "t_server_recv": start_time

  # Unity receives and stores
  trace.server_receive_ts = response.t_server_recv;

  # Next frame (N+1) sends as header
  request.SetRequestHeader("X-Prev-Server-Receive-Ts", traceToSend.server_receive_ts.ToString());

  # Server extracts from header
  prev_server_receive_ts = float(request.headers.get("X-Prev-Server-Receive-Ts", "0"))
  ```
- **When**: When server receives HTTP request
- **Measured by**: Server (Python)
- **Excel Format**: `2026-04-15 17:13:56.143` (converted from Unix ms)

#### `server_send_ts`
- **Calculation**:
  ```python
  # infer_human.py / segmentation.py
  t_postprocess_end = time.time() * 1000  # After all processing done

  # Sent back in response
  "t_server_send": t_postprocess_end

  # Unity receives and stores
  trace.server_send_ts = response.t_server_send;

  # Next frame (N+1) sends as header
  request.SetRequestHeader("X-Prev-Server-Send-Ts", traceToSend.server_send_ts.ToString());

  # Server extracts from header
  prev_server_send_ts = float(request.headers.get("X-Prev-Server-Send-Ts", "0"))
  ```
- **When**: When server sends HTTP response
- **Measured by**: Server (Python)

---

### 4. Timing Metrics (Derived)

#### `latency_ms` (E2E Latency)
- **Calculation**:
  ```csharp
  // FrameTrace.cs:77
  e2e_ms = unity_receive_ts - unity_send_ts;
  ```
- **Measured by**: Unity (client-side calculation)
- **Components**: Upload + Server Processing + Download
- **Range**: Typically 50-500ms
- **Formula**: `E2E = unity_receive_ts - unity_send_ts`

#### `server_proc_ms` (Server Processing Time)
- **Calculation**:
  ```python
  # infer_human.py / segmentation.py
  processing_time_ms = (t_postprocess_end - start_time)

  # Sent in response
  "processing_time_ms": processing_time_ms

  # Unity stores
  trace.server_proc_ms = response.processing_time_ms;
  ```
- **Measured by**: Server (Python)
- **Components**: Model inference + JSON serialization
- **Range**: Typically 20-200ms
- **Formula**: `Server = server_send_ts - server_receive_ts`

#### `upload_ms` (Upload Time)
- **Calculation**:
  ```python
  # Server estimates based on total E2E and known server time
  # infer_human.py / segmentation.py

  # Client sends E2E time via header
  e2e_ms = float(request.headers.get("X-E2E-Ms", "0"))

  # Server knows its own processing time
  server_proc_ms = processing_time_ms

  # Estimate upload as portion of network time
  network_ms = e2e_ms - server_proc_ms
  upload_ms = network_ms * (upload_bytes / (upload_bytes + download_bytes))
  ```
- **Measured by**: Server (estimated)
- **Method**: Proportional to upload bytes
- **Range**: Typically 5-100ms
- **Formula**: `Upload ≈ Network_Time × (Upload_Bytes / Total_Bytes)`

#### `download_ms` (Download Time)
- **Calculation**:
  ```python
  # Server estimates based on total E2E and known server time
  network_ms = e2e_ms - server_proc_ms
  download_ms = network_ms * (download_bytes / (upload_bytes + download_bytes))
  ```
- **Measured by**: Server (estimated)
- **Method**: Proportional to download bytes
- **Range**: Typically 5-100ms
- **Formula**: `Download ≈ Network_Time × (Download_Bytes / Total_Bytes)`

#### `parse_ms` (JSON Parse Time)
- **Calculation**:
  ```csharp
  // InferenceRunManager.cs (all 3 modes)
  float parseStartTime = Time.realtimeSinceStartup;
  ServerResponse response = JsonUtility.FromJson<ServerResponse>(jsonResponse);
  float parseEndTime = Time.realtimeSinceStartup;
  float parseTimeMs = (parseEndTime - parseStartTime) * 1000f;
  trace.parse_ms = parseTimeMs;
  ```
- **Measured by**: Unity (client)
- **Method**: Direct measurement with Unity's Time.realtimeSinceStartup
- **Range**: Typically 0.1-5ms
- **Formula**: `Parse = (parseEndTime - parseStartTime) × 1000`

---

### 5. Percentage Metrics (Breakdown)

#### `server_pct` (Server Processing Percentage)
- **Calculation**:
  ```python
  # frame_state_manager.py
  if e2e_ms > 0:
      server_pct = (server_proc_ms / e2e_ms) * 100.0
  else:
      server_pct = 100.0
  ```
- **Purpose**: Show what % of E2E time was server processing
- **Range**: 10-80% (higher = server bottleneck)
- **Formula**: `(server_proc_ms / latency_ms) × 100`

#### `upload_pct` (Upload Percentage)
- **Calculation**:
  ```python
  # frame_state_manager.py
  if e2e_ms > 0:
      upload_pct = (upload_ms / e2e_ms) * 100.0
  else:
      upload_pct = 0.0
  ```
- **Purpose**: Show what % of E2E time was upload
- **Range**: 5-40% (higher = upload bottleneck)
- **Formula**: `(upload_ms / latency_ms) × 100`

#### `download_pct` (Download Percentage)
- **Calculation**:
  ```python
  # frame_state_manager.py
  if e2e_ms > 0:
      download_pct = (download_ms / e2e_ms) * 100.0
  else:
      download_pct = 0.0
  ```
- **Purpose**: Show what % of E2E time was download
- **Range**: 5-40% (higher = download bottleneck)
- **Formula**: `(download_ms / latency_ms) × 100`

**Validation**: `server_pct + upload_pct + download_pct ≈ 100%` (may not be exact due to parse_ms)

---

### 6. Detection Results

#### `detection_count`
- **Source**: Model inference result
- **Calculation**: Depends on mode
  - **PoseEstimation**: Count of detected persons
  - **MultiObjectDetection**: Count of detected objects
  - **Segmentation**: Count of detected persons with masks
- **Range**: 0 to N (0 is valid - no detections)
- **File**: Server response JSON `"detections": [...]`

#### `avg_confidence`
- **Source**: Model inference result
- **Calculation**:
  ```python
  # infer_human.py / segmentation.py
  if len(detections) > 0:
      avg_confidence = sum(d['confidence'] for d in detections) / len(detections)
  else:
      avg_confidence = 0.0
  ```
- **Range**: 0.0 to 1.0 (0.0 if no detections)
- **Formula**: `Average(confidence_i) for all detections`

#### `keypoint_avg_conf`
- **Source**: Model inference result (PoseEstimation only)
- **Calculation**:
  ```python
  # infer_human.py
  total_keypoints = 0
  total_confidence = 0.0
  for person in detections:
      for keypoint in person['keypoints']:
          total_keypoints += 1
          total_confidence += keypoint['confidence']

  keypoint_avg_conf = total_confidence / total_keypoints if total_keypoints > 0 else 0.0
  ```
- **Range**: 0.0 to 1.0 (0.0 for non-pose modes)
- **Formula**: `Average(keypoint_confidence_i) for all keypoints`

---

### 7. Image Metadata

#### `image_width` / `image_height`
- **Source**: Unity camera frame
- **Calculation**: Direct from Unity Texture2D
  ```csharp
  int width = cameraTexture.width;
  int height = cameraTexture.height;
  ```
- **Typical Values**: 640×480, 1280×720, 1920×1080
- **File**: Set in Unity camera configuration

---

### 8. Payload Size Metrics

#### `upload_bytes_uncompressed`
- **Calculation**:
  ```csharp
  // InferenceRunManager.cs
  // RGB24 raw size
  int uploadBytesUncompressed = width * height * 3;
  trace.upload_bytes_uncompressed = uploadBytesUncompressed;
  ```
- **Formula**: `width × height × 3` (RGB = 3 bytes per pixel)
- **Example**: 640×480×3 = 921,600 bytes

#### `upload_bytes_compressed`
- **Calculation**:
  ```csharp
  // InferenceRunManager.cs
  byte[] jpegBytes = cameraTexture.EncodeToJPG(jpegQuality);
  int uploadBytesCompressed = jpegBytes.Length;
  trace.upload_bytes_compressed = uploadBytesCompressed;
  ```
- **Method**: JPEG compression
- **Typical Compression**: 10-30× (e.g., 921KB → 30-90KB)

#### `download_bytes_uncompressed`
- **Calculation**:
  ```csharp
  // InferenceRunManager.cs
  string jsonResponse = request.downloadHandler.text;
  int downloadBytesUncompressed = System.Text.Encoding.UTF8.GetByteCount(jsonResponse);
  trace.download_bytes_uncompressed = downloadBytesUncompressed;
  ```
- **Method**: UTF-8 byte count of JSON string
- **Typical Range**: 500-5000 bytes (depends on detection count)

#### `download_bytes_compressed`
- **Calculation**:
  ```csharp
  // InferenceRunManager.cs
  // If server sends gzip
  int downloadBytesCompressed = request.downloadHandler.data.Length;
  trace.download_bytes_compressed = downloadBytesCompressed;
  ```
- **Method**: Gzip compression (if enabled on server)
- **Typical Compression**: 3-5× for JSON

---

### 9. State Tracking (CRITICAL)

#### `final_state`
- **Source**: Unity frame lifecycle
- **Calculation**: State machine transition
  ```csharp
  // FrameTrace.cs state transitions:
  Pending → Completed → Displayed  (normal path)
  Pending → Completed → Dropped    (superseded path)
  Pending → Failed                 (error path)
  ```
- **Valid Final States**: `"Displayed"`, `"Dropped"`, `"Failed"`
- **Invalid States**: `"Pending"`, `"Completed"` (intermediate states, should NOT appear in Excel)
- **Validation**: Server logs warning if non-final state received

#### `drop_reason`
- **Calculation**:
  ```csharp
  // InferenceRunManager.cs:TryDisplayNewestFrame()
  olderFrame.MarkDropped(currentTimestamp, $"superseded_by_newer_{newest.frame_id}");
  ```
- **Format**: `"superseded_by_newer_123"` (where 123 is newer frame ID)
- **Empty if**: Frame was displayed or failed

#### `error_reason`
- **Calculation**:
  ```csharp
  // Network error
  trace.MarkFailed($"{request.result}: {request.error}");

  // Parse error
  trace.MarkFailed("JSON parse error");

  // Timeout
  trace.MarkFailed($"Timeout after {timeSinceSendSec:F1}s");
  ```
- **Empty if**: Frame was displayed or dropped

---

### 10. Freeze Frame Metrics (NEW - Priority 3)

#### `freeze_frames_per_frame`
- **Purpose**: Count Unity Update() calls between displayed inference results
- **Calculation**:
  ```csharp
  // InferenceRunManager.cs
  private int m_framesSinceLastDisplay = 0;

  void Update() {
      m_framesSinceLastDisplay++;  // Increment EVERY Unity frame
      TryDisplayNewestFrame();
  }

  void TryDisplayNewestFrame() {
      if (newest != null) {
          // Assign to displayed frame (-1 because current doesn't count as freeze)
          newest.freeze_frames = m_framesSinceLastDisplay - 1;
          m_framesSinceLastDisplay = 0;  // Reset
      }
  }
  ```
- **Range**: 0 to N
  - **Typical**: 6-12 for 60 FPS Unity / 10 FPS inference (60/10 = 6)
  - **High values**: Indicates long gaps between displays (performance issue)
- **Interpretation**:
  - 0 = Consecutive displays (very fast inference)
  - 6 = Normal for 60 FPS / 10 FPS
  - 60+ = 1 second or more without display (problem!)

---

### 11. Performance Metrics (Legacy)

#### `target_fps`
- **Source**: Unity configuration
- **Calculation**: Direct from InferenceConfig
  ```csharp
  float targetFPS = m_inferenceConfig.targetFPS;
  ```
- **Typical Values**: 5.0, 10.0, 15.0

#### `dropped_frames`
- **Source**: Unity cumulative counter
- **Calculation**:
  ```csharp
  // InferenceRunManager.cs
  private int m_droppedFrames = 0;  // Cumulative

  // Incremented when frame dropped
  if (frame.state == FrameState.Dropped) {
      m_droppedFrames++;
  }

  // Sent via header
  request.SetRequestHeader("X-Dropped-Frames", m_droppedFrames.ToString());
  ```
- **Type**: Cumulative count (increases throughout session)
- **Range**: 0 to N

---

### 12. DEPRECATED Legacy Columns

#### `freeze_frames_LEGACY`
- **Old Definition**: Cumulative count of Unity frames without display
- **Current Value**: Always 0 (deprecated)
- **Reason**: Replaced by `freeze_frames_per_frame`

#### `freeze_ratio_LEGACY`
- **Old Definition**: `freeze_frames / total_frames`
- **Current Value**: Always 0.0 (deprecated)
- **Reason**: No longer meaningful in parallel mode

#### `new_frozen_LEGACY`
- **Old Definition**: Incremental freeze count
- **Current Value**: Always 0 (deprecated)
- **Reason**: No longer meaningful in parallel mode

---

## Data Flow Diagram

### Frame N Lifecycle

```
UNITY SIDE                                  SERVER SIDE                              EXCEL
═══════════════════════════════════════════════════════════════════════════════════════════

[Frame N Send]
unity_send_ts = Now()
m_frameId = N
session_id = GUID
state = Pending
                    ────HTTP Request────>   [Frame N Receive]
                    Image (JPEG)            server_receive_ts = Now()
                    Headers: scene,
                            session_id,     [Model Inference]
                            frame_id        detection_count = ?
                                           avg_confidence = ?

                                           [Frame N Send Response]
                                           server_send_ts = Now()
                                           processing_time_ms =
                                               server_send_ts - server_receive_ts
                    <───HTTP Response────
                    JSON: detections,
                          t_server_recv,
                          t_server_send,
                          processing_time_ms

[Frame N Receive]
unity_receive_ts = Now()
e2e_ms = unity_receive_ts - unity_send_ts
server_receive_ts = response.t_server_recv
server_send_ts = response.t_server_send
server_proc_ms = response.processing_time_ms
state = Completed
                                           [Frame N NOT logged yet]
                                           (waiting for final state)

[Frame N+1 Send]
                    ────HTTP Request────>   [Frame N+1 Receive]
                    Image (JPEG)
                    Headers:                [Extract Frame N delayed headers]
                      X-Prev-Session-Id     prev_session_id = ?
                      X-Prev-Frame-Id       prev_frame_id = N
                      X-Prev-Unity-Send-Ts  prev_unity_send_ts = ?
                      X-Prev-Unity-Receive-Ts
                      X-Prev-Unity-Display-Ts or unity_drop_ts
                      X-Prev-Server-Receive-Ts
                      X-Prev-Server-Send-Ts
                      X-Prev-Final-State    "Displayed" or "Dropped"
                      X-Prev-Drop-Reason
                      X-Prev-Freeze-Frames

                                           [Calculate Frame N metrics]
                                           upload_ms, download_ms
                                           server_pct, upload_pct, download_pct

                                           [Log Frame N to Excel] ────> [Excel Row]
                                                                       All fields complete
                                                                       One row per sent frame
```

### State Machine

```
          SendImage()
              |
              v
         ┌─────────┐
         │ Pending │  (Request sent, waiting for response)
         └─────────┘
              |
              |──────> HTTP success
              |              |
              |              v
              |        ┌───────────┐
              |        │ Completed │  (Response received, not displayed yet)
              |        └───────────┘
              |              |
              |              |──────> Newest frame
              |              |              |
              |              |              v
              |              |        ┌───────────┐
              |              |        │ Displayed │  (Shown to user) ────> EXCEL
              |              |        └───────────┘
              |              |
              |              |──────> Older frame (superseded)
              |              |              |
              |              |              v
              |              |        ┌─────────┐
              |              |        │ Dropped │  (Never displayed) ────> EXCEL
              |              |        └─────────┘
              |
              |──────> HTTP error / timeout
                             |
                             v
                       ┌────────┐
                       │ Failed │  (Error occurred) ────> EXCEL
                       └────────┘
```

---

## Validation Rules

### 1. Timestamp Consistency

**Rule**: `unity_send_ts < unity_receive_ts < (unity_display_ts OR unity_drop_ts)`

**Validation**:
```python
# Check timestamp ordering
assert unity_send_ts < unity_receive_ts, "Receive before send!"
assert server_receive_ts < server_send_ts, "Server send before receive!"

if unity_display_ts > 0:
    assert unity_receive_ts < unity_display_ts, "Display before receive!"

if unity_drop_ts > 0:
    assert unity_receive_ts < unity_drop_ts, "Drop before receive!"
```

### 2. Timing Equation

**Rule**: `latency_ms ≈ server_proc_ms + upload_ms + download_ms + parse_ms`

**Validation**:
```python
total_accounted = server_proc_ms + upload_ms + download_ms + parse_ms
difference = abs(latency_ms - total_accounted)

# Allow small discrepancy (network jitter, measurement error)
assert difference < 10, f"Timing mismatch: {difference}ms"
```

### 3. Percentage Sum

**Rule**: `server_pct + upload_pct + download_pct ≈ 100%`

**Validation**:
```python
total_pct = server_pct + upload_pct + download_pct
assert 95 <= total_pct <= 105, f"Percentage sum wrong: {total_pct}%"
```

### 4. State Validation

**Rule**: Only final states in Excel

**Validation**:
```python
VALID_FINAL_STATES = {"Displayed", "Dropped", "Failed"}
assert final_state in VALID_FINAL_STATES, f"Invalid state: {final_state}"

# State exclusivity
if final_state == "Displayed":
    assert unity_display_ts > 0, "Displayed but no display_ts"
    assert unity_drop_ts == 0, "Displayed but has drop_ts"

elif final_state == "Dropped":
    assert unity_drop_ts > 0, "Dropped but no drop_ts"
    assert unity_display_ts == 0, "Dropped but has display_ts"
    assert drop_reason != "", "Dropped but no reason"

elif final_state == "Failed":
    assert error_reason != "", "Failed but no error"
```

### 5. Frame Continuity

**Rule**: All sent frames must appear in Excel (no data loss)

**Validation**:
```python
# Check for gaps in frame_id
frame_ids = sorted([row['frame_id'] for row in excel_data if row['session_id'] == session])
expected = list(range(max(frame_ids) + 1))
missing = set(expected) - set(frame_ids)

assert len(missing) == 0, f"Missing frames: {missing}"
```

### 6. Freeze Frame Validation

**Rule**: freeze_frames_per_frame should be reasonable for target_fps

**Validation**:
```python
unity_fps = 60  # Typical
expected_freeze = unity_fps / target_fps

# Allow 50% variance
assert 0 <= freeze_frames_per_frame <= expected_freeze * 1.5, \
    f"Freeze {freeze_frames_per_frame} too high for {target_fps} FPS"
```

---

## Known Issues and Solutions

### Issue 1: Upload/Download Estimation Inaccuracy

**Problem**: `upload_ms` and `download_ms` are estimates, not measurements

**Current Method**: Proportional to byte size
```python
upload_ms = network_ms * (upload_bytes / total_bytes)
```

**Limitations**:
- Assumes symmetric network speed (upload = download)
- Ignores TCP overhead, packet loss, retransmission
- Cannot account for network congestion

**Better Solution** (future):
```csharp
// Unity measures upload time directly
float uploadStart = Time.realtimeSinceStartup;
request.SendWebRequest();  // Blocks until upload complete
float uploadEnd = Time.realtimeSinceStartup;
float uploadMs = (uploadEnd - uploadStart) * 1000;
```

**Validation**:
- Check if `upload_pct + download_pct > 80%` (network bottleneck indicator)
- Compare estimated vs actual E2E time

---

### Issue 2: Parse Time Fluctuation

**Problem**: `parse_ms` can spike unexpectedly (0.1ms → 50ms)

**Root Cause**: Unity garbage collection during JSON parsing

**Current Method**:
```csharp
float parseStartTime = Time.realtimeSinceStartup;
ServerResponse response = JsonUtility.FromJson<ServerResponse>(jsonResponse);
float parseEndTime = Time.realtimeSinceStartup;
```

**Validation**:
```python
# Flag suspicious parse times
if parse_ms > 20:
    print(f"WARNING: Frame {frame_id} parse_ms={parse_ms}ms (GC?)")
```

**Better Solution** (future):
- Use JSON streaming parser (avoid GC)
- Exclude parse_ms from E2E calculation

---

### Issue 3: Freeze Frames Zero on First Display

**Problem**: First displayed frame shows `freeze_frames_per_frame = 0`

**Root Cause**: `m_framesSinceLastDisplay` starts at 0

**Current Behavior**:
```
Frame 0: freeze_frames = 0 - 1 = -1 → clamped to 0
Frame 1: freeze_frames = 6 (correct)
```

**Is This Correct?**: YES
- First frame has no "previous display" to measure against
- Zero is semantically correct

---

### Issue 4: Dropped Frame Count Mismatch

**Problem**: `dropped_frames` (cumulative) vs count of Dropped rows

**Example**:
```
dropped_frames header: 5
Actual Dropped rows: 3
```

**Root Cause**: Two definitions of "dropped"
1. **Header `dropped_frames`**: Cumulative Unity counter
2. **Excel Dropped rows**: Frames with final_state="Dropped"

**Validation**:
```python
# Count Dropped rows for this session
excel_dropped = len([r for r in excel_data
                     if r['session_id'] == session
                     and r['final_state'] == 'Dropped'])

# Compare to last frame's dropped_frames header
last_frame_dropped_count = max([r['dropped_frames'] for r in excel_data
                                if r['session_id'] == session])

assert excel_dropped == last_frame_dropped_count, "Dropped count mismatch"
```

---

### Issue 5: Server Timestamp Skew

**Problem**: Unity and Server clocks may not be synchronized

**Impact**:
```
unity_send_ts:      2026-04-15 17:13:56.100 (Unity clock)
server_receive_ts:  2026-04-15 17:13:56.050 (Server clock)
                    ^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
                    Server time is 50ms BEHIND Unity!
```

**Solution**: Use relative timings only
- ✅ SAFE: `latency_ms = unity_receive_ts - unity_send_ts` (same clock)
- ✅ SAFE: `server_proc_ms = server_send_ts - server_receive_ts` (same clock)
- ❌ UNSAFE: `network_delay = server_receive_ts - unity_send_ts` (different clocks)

**Validation**:
```python
# Don't compare timestamps across systems
# Only compare within same system
```

---

## Summary Checklist

### For Each Excel Row, Verify:

- [ ] `session_id` is consistent across all frames in a session
- [ ] `frame_id` is sequential (0, 1, 2, 3, ...)
- [ ] `unity_send_ts < unity_receive_ts` (no time travel)
- [ ] `final_state` is one of: Displayed, Dropped, Failed
- [ ] If Displayed: `unity_display_ts > 0` and `unity_drop_ts == 0`
- [ ] If Dropped: `unity_drop_ts > 0` and `unity_display_ts == 0` and `drop_reason != ""`
- [ ] If Failed: `error_reason != ""`
- [ ] `latency_ms ≈ server_proc_ms + upload_ms + download_ms` (±10ms tolerance)
- [ ] `server_pct + upload_pct + download_pct ≈ 100%` (±5% tolerance)
- [ ] `freeze_frames_per_frame` is reasonable (0 to ~15 for 60 FPS / 10 FPS)
- [ ] `detection_count >= 0` (zero is valid)
- [ ] All timestamps formatted as `YYYY-MM-DD HH:MM:SS.mmm`

### For Each Session, Verify:

- [ ] All sent frames appear exactly once (no duplicates, no missing)
- [ ] Count of Dropped rows matches final `dropped_frames` header value
- [ ] No gaps in `frame_id` sequence
- [ ] All frames have same `session_id`

---

## Example Validation Query (Excel/Python)

```python
import pandas as pd

# Load Excel
df = pd.read_excel('inference_log_2026-04-15.xlsx')

# Group by session
for session_id, session_df in df.groupby('session_id'):
    print(f"\n=== Session: {session_id} ===")

    # Check frame continuity
    frame_ids = sorted(session_df['frame_id'].tolist())
    expected = list(range(max(frame_ids) + 1))
    missing = set(expected) - set(frame_ids)
    print(f"Missing frames: {missing if missing else 'None'}")

    # Check state distribution
    states = session_df['final_state'].value_counts()
    print(f"States: {dict(states)}")

    # Check timing consistency
    timing_errors = session_df[
        abs(session_df['latency_ms'] -
            (session_df['server_proc_ms'] +
             session_df['upload_ms'] +
             session_df['download_ms'])) > 10
    ]
    print(f"Timing inconsistencies: {len(timing_errors)}")

    # Check percentage sum
    pct_sum = (session_df['server_pct'] +
               session_df['upload_pct'] +
               session_df['download_pct'])
    bad_pct = session_df[abs(pct_sum - 100) > 5]
    print(f"Bad percentage sums: {len(bad_pct)}")
```

---

**Document Version**: 1.0
**Last Updated**: 2026-04-15
**Status**: Complete and validated
