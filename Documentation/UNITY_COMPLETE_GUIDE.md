# Unity 端完整實作指南

**最後更新**: 2026-04-17
**版本**: 2.0 (UDP Transport + N+1 Telemetry)

這是 **Unity 端唯一且最新** 的完整文檔，涵蓋所有 UDP transport 和 frame tracking 實作細節。

---

## 目錄

1. [系統架構](#系統架構)
2. [關鍵組件](#關鍵組件)
3. [UDP Transport](#udp-transport)
4. [Frame Tracking](#frame-tracking)
5. [N+1 Delayed Telemetry](#n1-delayed-telemetry)
6. [設定步驟](#設定步驟)
7. [程式碼範例](#程式碼範例)

---

## 系統架構

### Unity Side Overview

```
┌────────────────────────────────────────────────────────────────┐
│                    Unity (Quest 3)                             │
│                                                                │
│  Update() @ 72 FPS (VR rendering)                             │
│    ↓                                                           │
│  Fixed Cadence Check (e.g., every 100ms for targetFPS=10)    │
│    ↓                                                           │
│  ┌──────────────────────────────────────────────────────┐    │
│  │ RunInferenceNonBlocking()                            │    │
│  │                                                       │    │
│  │ 1. Capture Frame                                     │    │
│  │    └─ PassthroughCameraAccess.GetCPUTexture()       │    │
│  │                                                       │    │
│  │ 2. Encode JPEG                                       │    │
│  │    └─ texture.EncodeToJPG(quality: 80)              │    │
│  │                                                       │    │
│  │ 3. Create FrameTrace                                 │    │
│  │    ├─ frame_id = m_frameCounter++                   │    │
│  │    ├─ unity_send_ts = Now()                         │    │
│  │    └─ Store in m_activeTraces[frameId]              │    │
│  │                                                       │    │
│  │ 4. Get Previous Frame's Telemetry (N-1)             │    │
│  │    └─ m_completedTraces[frameId - 1]                │    │
│  │                                                       │    │
│  │ 5. Send UDP Packet                                   │    │
│  │    ├─ Current frame: frameId, JPEG                  │    │
│  │    └─ Previous telemetry: FrameTrace(N-1)           │    │
│  │                                                       │    │
│  │ 6. Start Background Polling (Coroutine)             │    │
│  │    └─ PollForResult(frameId)                        │    │
│  │                                                       │    │
│  │ 7. Return (Non-blocking!)                            │    │
│  └──────────────────────────────────────────────────────┘    │
│                                                                │
│  ┌──────────────────────────────────────────────────────┐    │
│  │ PollForResult(frameId) - Coroutine                   │    │
│  │                                                       │    │
│  │ Loop:                                                 │    │
│  │   HTTP GET /response/{session}/{frameId}            │    │
│  │   ├─ 200 OK: Parse result → Render → Update trace   │    │
│  │   ├─ 404: Wait 100ms → Retry                        │    │
│  │   └─ Timeout 5s: Give up                            │    │
│  └──────────────────────────────────────────────────────┘    │
│                                                                │
│  ┌──────────────────────────────────────────────────────┐    │
│  │ ProcessInferenceResult(frameId, json)                │    │
│  │                                                       │    │
│  │ 1. Parse JSON → Get detections                       │    │
│  │                                                       │    │
│  │ 2. Render Overlay                                    │    │
│  │    └─ Draw bounding boxes, skeletons, masks         │    │
│  │                                                       │    │
│  │ 3. Update FrameTrace                                 │    │
│  │    ├─ unity_receive_ts = Now()                      │    │
│  │    ├─ unity_display_ts = Now()                      │    │
│  │    ├─ server_receive_ts (from JSON)                 │    │
│  │    ├─ Calculate latency breakdown                    │    │
│  │    ├─ Extract detection metrics                      │    │
│  │    └─ final_state = "Displayed"                     │    │
│  │                                                       │    │
│  │ 4. Move to Completed Traces                          │    │
│  │    └─ m_completedTraces[frameId] = trace            │    │
│  └──────────────────────────────────────────────────────┘    │
└────────────────────────────────────────────────────────────────┘
```

---

## 關鍵組件

### 1. InferenceConfig

**Location**: `Assets/.../Shared/Scripts/InferenceConfig.cs`

```csharp
[System.Serializable]
public class InferenceConfig
{
    [Header("Server Settings")]
    public bool useServerInference = true;
    public bool useServerConfig = true;  // Use centralized ServerConfig.asset
    public bool useUDPTransport = true;  // Enable UDP transport

    [Header("Inference Settings")]
    public InferenceMode mode = InferenceMode.Both;
    public float targetFPS = 10f;        // Fixed cadence (100ms interval)
    public int jpegQuality = 80;

    [Header("Advanced")]
    public int maxConcurrentRequests = 3;
    public float pollInterval = 0.1f;    // 100ms between polls
    public float pollTimeout = 5.0f;     // 5 second timeout

    // Auto-generated from ServerConfig
    public string BaseUrl => ServerConfig.Instance.BaseUrl;
}
```

### 2. FrameTrace (Telemetry Data Structure)

**Location**: `Assets/.../Shared/Scripts/FrameTrace.cs`

```csharp
[System.Serializable]
public class FrameTrace
{
    // ========================================
    // IDENTITY (3 columns)
    // ========================================
    public int frame_id;
    public string session_id;
    public string scene;

    // ========================================
    // UNITY-SIDE TIMING (4 columns)
    // Unix milliseconds
    // ========================================
    public long unity_send_ts;        // When UDP packet sent
    public long unity_receive_ts;     // When HTTP response received
    public long unity_display_ts;     // When overlay rendered
    public long unity_drop_ts;        // When marked as dropped (if applicable)

    // ========================================
    // SERVER-SIDE TIMING (3 columns)
    // From server response, Unix milliseconds
    // ========================================
    public long server_receive_ts;
    public long server_process_start_ts;
    public long server_send_ts;

    // ========================================
    // LATENCY BREAKDOWN (9 columns)
    // ========================================
    public float latency_ms;          // Total: unity_send → unity_display
    public float upload_ms;           // unity_send → server_receive
    public float queue_wait_ms;       // server_receive → server_process_start
    public float server_proc_ms;      // Inference time (from server)
    public float download_ms;         // server_send → unity_receive
    public float parse_ms;            // JSON parse time (measured in Unity)
    public float udp_send_ms;         // UDP send time (usually < 1ms)

    // Percentages
    public float server_pct;          // server_proc_ms / latency_ms * 100
    public float upload_pct;
    public float download_pct;

    // ========================================
    // INFERENCE RESULTS (5 columns)
    // ========================================
    public int detection_count;
    public float avg_confidence;
    public float keypoint_avg_conf;
    public int image_width;
    public int image_height;

    // ========================================
    // PAYLOAD SIZES (4 columns)
    // ========================================
    public int upload_bytes_uncompressed;    // JPEG size before UDP
    public int upload_bytes_compressed;      // Same for UDP (no compression)
    public int download_bytes_uncompressed;  // JSON size
    public int download_bytes_compressed;    // Same for HTTP (no compression)

    // ========================================
    // FINAL STATE (3 columns)
    // ========================================
    public string final_state;        // "Displayed", "Dropped", "Failed"
    public string drop_reason;
    public string error_reason;

    // ========================================
    // FREEZE METRICS (4 columns)
    // ========================================
    public int freeze_frames_per_frame;      // How many Update() cycles this frame was displayed
    public float freeze_duration_ms;         // Duration = freeze_frames_per_frame * (1000/72)
    public int cumulative_freeze_frames;     // Running total
    public float freeze_ratio;               // cumulative_freeze / cumulative_update_calls

    // ========================================
    // DROP METRICS (4 columns)
    // ========================================
    public int frame_gap;                    // Gap from previous frame (e.g., if prev=100, cur=105, gap=4)
    public int cumulative_dropped;           // Running total of dropped frames
    public int cumulative_displayed;         // Running total of displayed frames
    public float drop_rate;                  // cumulative_dropped / (cumulative_dropped + cumulative_displayed)

    // ========================================
    // SESSION CONTEXT (2 columns)
    // ========================================
    public int session_frame_index;          // Index within session (0, 1, 2, ...)
    public float target_fps;

    // Convert to dictionary for JSON serialization
    public Dictionary<string, object> ToDictionary()
    {
        return new Dictionary<string, object>
        {
            {"frame_id", frame_id},
            {"session_id", session_id},
            {"scene", scene},
            {"unity_send_ts", unity_send_ts},
            {"unity_receive_ts", unity_receive_ts},
            {"unity_display_ts", unity_display_ts},
            {"unity_drop_ts", unity_drop_ts},
            {"server_receive_ts", server_receive_ts},
            {"server_process_start_ts", server_process_start_ts},
            {"server_send_ts", server_send_ts},
            {"latency_ms", latency_ms},
            {"upload_ms", upload_ms},
            {"queue_wait_ms", queue_wait_ms},
            {"server_proc_ms", server_proc_ms},
            {"download_ms", download_ms},
            {"parse_ms", parse_ms},
            {"udp_send_ms", udp_send_ms},
            {"server_pct", server_pct},
            {"upload_pct", upload_pct},
            {"download_pct", download_pct},
            {"detection_count", detection_count},
            {"avg_confidence", avg_confidence},
            {"keypoint_avg_conf", keypoint_avg_conf},
            {"image_width", image_width},
            {"image_height", image_height},
            {"upload_bytes_uncompressed", upload_bytes_uncompressed},
            {"upload_bytes_compressed", upload_bytes_compressed},
            {"download_bytes_uncompressed", download_bytes_uncompressed},
            {"download_bytes_compressed", download_bytes_compressed},
            {"final_state", final_state},
            {"drop_reason", drop_reason},
            {"error_reason", error_reason},
            {"freeze_frames_per_frame", freeze_frames_per_frame},
            {"freeze_duration_ms", freeze_duration_ms},
            {"cumulative_freeze_frames", cumulative_freeze_frames},
            {"freeze_ratio", freeze_ratio},
            {"frame_gap", frame_gap},
            {"cumulative_dropped", cumulative_dropped},
            {"cumulative_displayed", cumulative_displayed},
            {"drop_rate", drop_rate},
            {"session_frame_index", session_frame_index},
            {"target_fps", target_fps}
        };
    }
}
```

### 3. UDPTransport

**Location**: `Assets/.../Shared/Scripts/UDPTransport.cs`

UDP 封包格式：

```
┌─────────────────────────────────────────────────┐
│ UDP Packet Structure                            │
├─────────────────────────────────────────────────┤
│ Magic Number (4 bytes)      = 0xDEADBEEF       │
│ Frame ID (4 bytes)           = int32            │
│ Session ID (16 bytes)        = UUID             │
│ Mode Length (4 bytes)        = int32            │
│ Mode String (N bytes)        = UTF-8            │
│ Telemetry Length (4 bytes)   = int32            │
│ Telemetry JSON (M bytes)     = UTF-8            │  ← N+1 Pattern
│ JPEG Length (4 bytes)        = int32            │
│ JPEG Data (K bytes)          = binary           │
│ Hash (4 bytes)               = CRC32            │
└─────────────────────────────────────────────────┘
```

---

## UDP Transport

### 完整實作

**Location**: `Assets/.../Shared/Scripts/UDPTransport.cs`

```csharp
using System;
using System.Net;
using System.Net.Sockets;
using System.IO;
using System.Text;
using System.Collections.Generic;
using UnityEngine;

public class UDPTransport
{
    private UdpClient udpClient;
    private IPEndPoint serverEndpoint;
    private const uint MAGIC_NUMBER = 0xDEADBEEF;

    public UDPTransport(string serverIP, int serverPort)
    {
        udpClient = new UdpClient();
        serverEndpoint = new IPEndPoint(IPAddress.Parse(serverIP), serverPort);
    }

    public void SendFrame(int frameId, string sessionId, string mode, byte[] jpegBytes,
                         Dictionary<string, object> telemetry = null)
    {
        try
        {
            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter writer = new BinaryWriter(ms))
            {
                // 1. Magic number (4 bytes)
                writer.Write(MAGIC_NUMBER);

                // 2. Frame ID (4 bytes)
                writer.Write(frameId);

                // 3. Session ID (16 bytes UUID)
                Guid sessionGuid = Guid.Parse(sessionId);
                writer.Write(sessionGuid.ToByteArray());

                // 4. Mode (length + string)
                byte[] modeBytes = Encoding.UTF8.GetBytes(mode);
                writer.Write(modeBytes.Length);
                writer.Write(modeBytes);

                // 5. Telemetry JSON (N+1 pattern)
                if (telemetry != null && telemetry.Count > 0)
                {
                    string telemetryJson = JsonUtility.ToJson(new SerializableDict(telemetry));
                    byte[] telemetryBytes = Encoding.UTF8.GetBytes(telemetryJson);
                    writer.Write(telemetryBytes.Length);
                    writer.Write(telemetryBytes);
                }
                else
                {
                    writer.Write(0);  // No telemetry
                }

                // 6. JPEG data (length + bytes)
                writer.Write(jpegBytes.Length);
                writer.Write(jpegBytes);

                // 7. Calculate and write hash (CRC32)
                byte[] packet = ms.ToArray();
                uint hash = CalculateCRC32(packet);
                writer.Write(hash);

                // 8. Send UDP packet
                byte[] finalPacket = ms.ToArray();
                udpClient.Send(finalPacket, finalPacket.Length, serverEndpoint);

                Debug.Log($"[UDP SEND] Frame {frameId} sent, size={finalPacket.Length} bytes" +
                         (telemetry != null ? $", telemetry={telemetry.Count} fields" : ""));
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"[UDP SEND] Error sending frame {frameId}: {e.Message}");
        }
    }

    private uint CalculateCRC32(byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        for (int i = 0; i < data.Length; i++)
        {
            byte index = (byte)(((crc) & 0xFF) ^ data[i]);
            crc = (crc >> 8) ^ crc32Table[index];
        }
        return ~crc;
    }

    // CRC32 lookup table
    private static readonly uint[] crc32Table = { /* ... CRC32 table ... */ };

    public void Close()
    {
        udpClient?.Close();
    }
}
```

---

## Frame Tracking

### Active Traces vs Completed Traces

```csharp
public class InferenceRunManager : MonoBehaviour
{
    // Active traces: Waiting for result
    private Dictionary<int, FrameTrace> m_activeTraces = new Dictionary<int, FrameTrace>();

    // Completed traces: Result received, ready to attach to next frame
    private Dictionary<int, FrameTrace> m_completedTraces = new Dictionary<int, FrameTrace>();

    // Cumulative metrics
    private int m_cumulativeDisplayed = 0;
    private int m_cumulativeDropped = 0;
    private int m_cumulativeFreezeFrames = 0;
    private int m_cumulativeUpdateCalls = 0;

    private void Update()
    {
        m_cumulativeUpdateCalls++;

        // Freeze tracking: Check if current displayed frame is being frozen
        if (m_currentDisplayedFrameId != -1)
        {
            if (m_activeTraces.TryGetValue(m_currentDisplayedFrameId, out FrameTrace trace))
            {
                trace.freeze_frames_per_frame++;
                m_cumulativeFreezeFrames++;

                // Update freeze metrics
                trace.freeze_duration_ms = trace.freeze_frames_per_frame * (1000f / 72f);  // 72 FPS
                trace.cumulative_freeze_frames = m_cumulativeFreezeFrames;
                trace.freeze_ratio = (float)m_cumulativeFreezeFrames / m_cumulativeUpdateCalls;
            }
        }

        // Fixed cadence check
        if (Time.time >= m_nextInferenceTime)
        {
            RunInferenceNonBlocking();
            m_nextInferenceTime = Time.time + (1f / m_config.targetFPS);
        }

        // Timeout check for dropped frames
        CheckForDroppedFrames();
    }

    private void CheckForDroppedFrames()
    {
        float timeout = m_config.pollTimeout;
        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        List<int> droppedFrames = new List<int>();

        foreach (var kvp in m_activeTraces)
        {
            int frameId = kvp.Key;
            FrameTrace trace = kvp.Value;

            float elapsed = (nowMs - trace.unity_send_ts) / 1000f;

            if (elapsed > timeout)
            {
                // Frame dropped (timeout)
                trace.unity_drop_ts = nowMs;
                trace.final_state = "Dropped";
                trace.drop_reason = "PollTimeout";

                // Update drop metrics
                m_cumulativeDropped++;
                trace.cumulative_dropped = m_cumulativeDropped;
                trace.cumulative_displayed = m_cumulativeDisplayed;
                trace.drop_rate = (float)m_cumulativeDropped / (m_cumulativeDropped + m_cumulativeDisplayed);

                // Move to completed (will be attached to next frame)
                m_completedTraces[frameId] = trace;
                droppedFrames.Add(frameId);

                Debug.LogWarning($"[FRAME DROP] Frame {frameId} dropped (timeout {timeout}s)");
            }
        }

        // Remove from active
        foreach (int frameId in droppedFrames)
        {
            m_activeTraces.Remove(frameId);
        }
    }
}
```

---

## N+1 Delayed Telemetry

### 核心概念

```
Frame N 被 Unity 處理
  ↓
Unity 產生完整的 FrameTrace(N)
  ├─ latency_ms
  ├─ detection_count
  ├─ final_state
  └─ freeze/drop metrics
  ↓
存入 m_completedTraces[N]
  ↓
Frame N+1 發送時
  ↓
從 m_completedTraces[N] 取出 telemetry
  ↓
嵌入 Frame N+1 的 UDP packet
  ↓
Server 接收 Frame N+1
  ↓
提取 telemetry → 寫入 Excel (Frame N 的完整記錄)
```

### 實作

```csharp
private void RunInferenceNonBlocking()
{
    // 1. Increment frame counter
    int frameId = m_frameCounter++;

    // 2. Capture and encode frame
    Texture2D texture = CaptureFrame();
    byte[] jpegBytes = texture.EncodeToJPG(m_config.jpegQuality);

    // 3. Create new FrameTrace
    FrameTrace trace = new FrameTrace
    {
        frame_id = frameId,
        session_id = m_sessionId,
        scene = SceneManager.GetActiveScene().name,
        unity_send_ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        session_frame_index = frameId,
        target_fps = m_config.targetFPS,

        // Initialize cumulative metrics
        cumulative_displayed = m_cumulativeDisplayed,
        cumulative_dropped = m_cumulativeDropped,
        cumulative_freeze_frames = m_cumulativeFreezeFrames,
        freeze_ratio = (float)m_cumulativeFreezeFrames / Mathf.Max(1, m_cumulativeUpdateCalls)
    };

    // Calculate frame gap
    if (m_lastSentFrameId != -1)
    {
        trace.frame_gap = frameId - m_lastSentFrameId - 1;
    }
    m_lastSentFrameId = frameId;

    m_activeTraces[frameId] = trace;

    // 4. Get telemetry from PREVIOUS frame (N-1)
    Dictionary<string, object> telemetry = null;
    int prevFrameId = frameId - 1;

    if (m_completedTraces.TryGetValue(prevFrameId, out FrameTrace prevTrace))
    {
        telemetry = prevTrace.ToDictionary();

        // Clean up
        m_completedTraces.Remove(prevFrameId);

        Debug.Log($"[N+1 TELEMETRY] Frame {frameId} carries telemetry for frame {prevFrameId} " +
                 $"(final_state={prevTrace.final_state})");
    }

    // 5. Send UDP packet with N+1 telemetry
    m_udpTransport.SendFrame(
        frameId,
        m_sessionId,
        m_config.mode.ToString().ToLower(),
        jpegBytes,
        telemetry  // ← N-1's complete telemetry
    );

    // 6. Start background polling
    StartCoroutine(PollForResult(frameId));
}
```

---

## 設定步驟

### 1. ServerConfig Setup

**Tools → Passthrough Camera → Server Config Editor**

```
Server IP:  192.168.0.135  (你的 PC WiFi IP)
Port:       8001
```

### 2. Inspector Settings

選擇 InferenceManager GameObject，設定：

```
Inference Config:
├─ Use Server Inference: ✓
├─ Use UDP Transport: ✓
├─ Mode: Both (或 Detection/Segmentation)
├─ Target FPS: 10
├─ JPEG Quality: 80
└─ Use Server Config: ✓
```

### 3. 驗證

Build and Run 後，查看 logcat：

```bash
adb logcat -s Unity | findstr "UDP"
```

預期輸出：
```
[UDP SEND] Frame 100 sent, size=9308 bytes
[UDP POLL] Starting polling for frame 100
[UDP POLL] Frame 100 received after 0.25s
[N+1 TELEMETRY] Frame 101 carries telemetry for frame 100 (final_state=Displayed)
```

---

## 程式碼範例

### 完整 RunInferenceNonBlocking()

```csharp
private void RunInferenceNonBlocking()
{
    if (!m_config.useServerInference || !m_config.useUDPTransport)
        return;

    // === 1. CAPTURE FRAME ===
    Texture2D texture = new Texture2D(m_cameraTexture.width, m_cameraTexture.height, TextureFormat.RGB24, false);
    texture.SetPixels(m_cameraTexture.GetPixels());
    texture.Apply();

    // === 2. ENCODE JPEG ===
    byte[] jpegBytes = texture.EncodeToJPG(m_config.jpegQuality);
    Destroy(texture);

    // === 3. CREATE FRAME TRACE ===
    int frameId = m_frameCounter++;
    long sendTs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    FrameTrace trace = new FrameTrace
    {
        frame_id = frameId,
        session_id = m_sessionId,
        scene = SceneManager.GetActiveScene().name,
        unity_send_ts = sendTs,
        session_frame_index = frameId,
        target_fps = m_config.targetFPS,
        image_width = m_cameraTexture.width,
        image_height = m_cameraTexture.height,
        upload_bytes_uncompressed = jpegBytes.Length,
        upload_bytes_compressed = jpegBytes.Length,

        // Cumulative metrics
        cumulative_displayed = m_cumulativeDisplayed,
        cumulative_dropped = m_cumulativeDropped,
        cumulative_freeze_frames = m_cumulativeFreezeFrames,
        freeze_ratio = (float)m_cumulativeFreezeFrames / Mathf.Max(1, m_cumulativeUpdateCalls),
        drop_rate = (float)m_cumulativeDropped / Mathf.Max(1, m_cumulativeDropped + m_cumulativeDisplayed)
    };

    // Frame gap
    if (m_lastSentFrameId != -1)
    {
        trace.frame_gap = frameId - m_lastSentFrameId - 1;
    }
    m_lastSentFrameId = frameId;

    m_activeTraces[frameId] = trace;

    // === 4. GET N-1 TELEMETRY ===
    Dictionary<string, object> telemetry = null;
    int prevFrameId = frameId - 1;

    if (m_completedTraces.TryGetValue(prevFrameId, out FrameTrace prevTrace))
    {
        telemetry = prevTrace.ToDictionary();
        m_completedTraces.Remove(prevFrameId);
    }

    // === 5. SEND UDP ===
    float udpStartTime = Time.realtimeSinceStartup;

    m_udpTransport.SendFrame(
        frameId,
        m_sessionId,
        m_config.mode.ToString().ToLower(),
        jpegBytes,
        telemetry
    );

    float udpEndTime = Time.realtimeSinceStartup;
    trace.udp_send_ms = (udpEndTime - udpStartTime) * 1000f;

    // === 6. START POLLING ===
    StartCoroutine(PollForResult(frameId));

    Debug.Log($"[PHASE 3] Triggered inference at fixed cadence (interval={(1f / m_config.targetFPS) * 1000f}ms)");
}
```

### 完整 PollForResult()

```csharp
private IEnumerator PollForResult(int frameId)
{
    string url = $"{m_config.BaseUrl}/response/{m_sessionId}/{frameId}";
    float timeout = m_config.pollTimeout;
    float pollInterval = m_config.pollInterval;
    float elapsed = 0f;

    Debug.Log($"[UDP POLL] Starting polling for frame {frameId}");

    while (elapsed < timeout)
    {
        UnityWebRequest request = UnityWebRequest.Get(url);
        yield return request.SendWebRequest();

        if (request.result == UnityWebRequest.Result.Success)
        {
            // === SUCCESS ===
            long receiveTs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            string json = request.downloadHandler.text;
            ProcessInferenceResult(frameId, json, receiveTs);

            Debug.Log($"[UDP POLL] Frame {frameId} received after {elapsed:F2}s");
            yield break;
        }
        else if (request.responseCode == 404)
        {
            // === NOT READY YET ===
            yield return new WaitForSeconds(pollInterval);
            elapsed += pollInterval;
        }
        else
        {
            // === ERROR ===
            Debug.LogError($"[UDP POLL] Error for frame {frameId}: {request.error}");
            yield break;
        }
    }

    // === TIMEOUT ===
    Debug.LogWarning($"[UDP POLL] Timeout for frame {frameId} after {timeout}s");
}
```

### 完整 ProcessInferenceResult()

```csharp
private void ProcessInferenceResult(int frameId, string json, long receiveTs)
{
    if (!m_activeTraces.TryGetValue(frameId, out FrameTrace trace))
    {
        Debug.LogWarning($"[PROCESS] No trace found for frame {frameId}");
        return;
    }

    // === 1. PARSE JSON ===
    float parseStartTime = Time.realtimeSinceStartup;

    InferenceResult result = JsonUtility.FromJson<InferenceResult>(json);

    float parseEndTime = Time.realtimeSinceStartup;
    trace.parse_ms = (parseEndTime - parseStartTime) * 1000f;

    // === 2. RENDER OVERLAY ===
    RenderDetections(result.detections);

    long displayTs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    // === 3. UPDATE FRAME TRACE ===

    // Timing
    trace.unity_receive_ts = receiveTs;
    trace.unity_display_ts = displayTs;
    trace.server_receive_ts = (long)(result.server_receive_ts * 1000);
    trace.server_process_start_ts = (long)(result.server_process_start_ts * 1000);
    trace.server_send_ts = (long)(result.t_server_send * 1000);

    // Latency breakdown
    trace.latency_ms = displayTs - trace.unity_send_ts;
    trace.upload_ms = trace.server_receive_ts - trace.unity_send_ts;
    trace.queue_wait_ms = trace.server_process_start_ts - trace.server_receive_ts;
    trace.server_proc_ms = result.processing_time_ms;
    trace.download_ms = trace.unity_receive_ts - trace.server_send_ts;

    // Percentages
    if (trace.latency_ms > 0)
    {
        trace.server_pct = (trace.server_proc_ms / trace.latency_ms) * 100f;
        trace.upload_pct = (trace.upload_ms / trace.latency_ms) * 100f;
        trace.download_pct = (trace.download_ms / trace.latency_ms) * 100f;
    }

    // Detection metrics
    trace.detection_count = result.detections?.num_detections ?? 0;

    if (trace.detection_count > 0)
    {
        float sumConf = 0f;
        foreach (var det in result.detections.detections)
        {
            sumConf += det.confidence;
        }
        trace.avg_confidence = sumConf / trace.detection_count;
    }

    // Download size
    trace.download_bytes_uncompressed = json.Length;
    trace.download_bytes_compressed = json.Length;

    // Final state
    trace.final_state = "Displayed";

    // Update cumulative metrics
    m_cumulativeDisplayed++;
    trace.cumulative_displayed = m_cumulativeDisplayed;
    trace.drop_rate = (float)m_cumulativeDropped / (m_cumulativeDropped + m_cumulativeDisplayed);

    // === 4. MOVE TO COMPLETED ===
    m_completedTraces[frameId] = trace;
    m_activeTraces.Remove(frameId);

    // Update current displayed frame (for freeze tracking)
    m_currentDisplayedFrameId = frameId;

    Debug.Log($"[PROCESS] Frame {frameId} processed successfully " +
             $"(latency={trace.latency_ms:F1}ms, detections={trace.detection_count})");
}
```

---

## 相關文檔

**Server Side**:
- `C:\Repo\Github\vision_server\SYSTEM_COMPLETE_GUIDE.md` - Server 端完整指南

**Unity Side**:
- `UDP_TRANSPORT_SETUP_GUIDE.md` - UDP 傳輸設定指南（本文檔包含更多細節）
- `UNITY_FRAME_TRACKING_IMPLEMENTATION.md` - Frame tracking 實作細節（本文檔已整合）

---

**建立日期**: 2026-04-17
**版本**: 2.0 (UDP + N+1 Telemetry)
