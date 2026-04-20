# Excel Columns Complete Reference

**Last Updated**: 2026-04-17
**Transport Mode**: UDP + HTTP Polling (支援 HTTP POST fallback)
**Calculation Method**: Residual Method B (避免時鐘偏移)

---

## 📋 目錄

1. [總覽](#總覽)
2. [欄位分類](#欄位分類)
3. [詳細計算公式](#詳細計算公式)
4. [數據驗證規則](#數據驗證規則)
5. [常見問題](#常見問題)
6. [代碼位置](#代碼位置)

---

## 總覽

### 主鍵 (Primary Key)
```
(session_id, frame_id)
```
- **session_id**: Unity 場景啟動時生成的 GUID，全局唯一
- **frame_id**: 場景內的幀序號，從 0 開始遞增

### 計算原則

| 原則 | 說明 | 原因 |
|------|------|------|
| ✅ **Unity-only timing** | 只用 Unity 時間戳計算 Unity 端時間 | 避免時鐘偏移 |
| ✅ **Server-only timing** | 只用 Server 時間戳計算 Server 端時間 | 避免時鐘偏移 |
| ❌ **Cross-device timing** | 絕對不跨設備相減時間戳 | 會產生 ±1.2 秒誤差 |
| ✅ **Residual method** | 用剩餘時間估算網路時間 | 無法直接測量網路延遲 |
| ✅ **Data size ratio** | 按數據大小分配上傳/下載時間 | 合理的統計估算 |

### 時鐘偏移問題

**Unity (Quest 3) 和 Server (PC) 的時鐘相差約 1.2 秒：**

```
Unity:  2026-04-17 12:48:57.095  (unix_ms: 1713352137095)
Server: 2026-04-17 12:48:58.348  (unix_ms: 1713352138348)
差異:    約 1253ms (時鐘偏移)
```

**錯誤示範 (會產生負值或異常值):**
```csharp
// ❌ 錯誤：跨設備計算
upload_ms = server_receive_ts - unity_send_ts;  // = 1253ms (錯誤!)
```

**正確做法 (Residual Method B):**
```csharp
// ✅ 正確：只用 Unity 時間戳
latency_ms = unity_receive_ts - unity_send_ts;  // = 102ms ✓

// ✅ 正確：只用 Server 時間戳
queue_wait_ms = server_process_start_ts - server_receive_ts;  // = 43ms ✓

// ✅ 正確：用剩餘時間估算網路時間
network_total = latency_ms - queue_wait_ms - server_proc_ms - parse_ms;
upload_ms = network_total × upload_ratio;
```

---

## 欄位分類

### 1️⃣ 身份欄位 (Identity Columns)

| 欄位 | 數據類型 | 來源 | 計算方式 | 說明 |
|------|---------|------|---------|------|
| **timestamp** | DateTime | Python Logger | `datetime.now()` | 日誌寫入時間 (非幀時間) |
| **scene** | String | Unity | 字面值 | "PoseEstimation", "MultiObjectDetection", "Segmentation" |
| **session_id** | String (GUID) | Unity | `System.Guid.NewGuid()` | 場景啟動時生成，全局唯一 |
| **frame_id** | Integer | Unity | 遞增計數器 | 場景內唯一，從 0 開始 |

**代碼位置 (Unity)**:
```csharp
// Assets/PassthroughCameraApiSamples/*/Scripts/*InferenceRunManager.cs
private string m_sessionId = System.Guid.NewGuid().ToString();
private int m_frameId = 0;

void Update() {
    m_frameId++;
    FrameTrace trace = new FrameTrace(m_frameId);
    trace.session_id = m_sessionId;
    // ...
}
```

---

### 2️⃣ 時間戳欄位 (Timestamp Columns)

**所有時間戳都是 Unix 毫秒 (Unix milliseconds)，在 Excel 中轉換為人類可讀格式。**

#### Unity 時間戳 (Quest 3 Clock)

| 欄位 | 數據類型 | 捕獲時機 | 計算方式 | 說明 |
|------|---------|---------|---------|------|
| **unity_send_ts** | DateTime | UDP/HTTP 發送前 | `TimestampUtil.GetUnixTimestampMs()` | Unity 發送請求的時間 |
| **unity_receive_ts** | DateTime | HTTP 回應接收後 | `TimestampUtil.GetUnixTimestampMs()` | Unity 接收回應的時間 |
| **unity_display_ts** | DateTime | 顯示結果時 | `TimestampUtil.GetUnixTimestampMs()` | Unity 顯示結果的時間 (null 如果未顯示) |
| **unity_drop_ts** | DateTime | 決定丟棄時 | `TimestampUtil.GetUnixTimestampMs()` | Unity 丟棄幀的時間 (null 如果未丟棄) |

**代碼位置 (Unity)**:
```csharp
// Assets/PassthroughCameraApiSamples/Shared/Scripts/FrameTrace.cs:81
trace.unity_send_ts = TimestampUtil.GetUnixTimestampMs();

// *InferenceRunManager.cs (UDP polling 回調)
trace.unity_receive_ts = TimestampUtil.GetUnixTimestampMs();

// DisplayResults() 方法
trace.unity_display_ts = TimestampUtil.GetUnixTimestampMs();
```

#### Server 時間戳 (PC Clock)

| 欄位 | 數據類型 | 捕獲時機 | 計算方式 | 說明 |
|------|---------|---------|---------|------|
| **server_receive_ts** | DateTime | UDP 封包到達時 | `time.time() * 1000` | Server 接收 UDP 的時間 |
| **server_process_start_ts** | DateTime | 開始推理前 | `time.time() * 1000` | Server 開始處理的時間 |
| **server_send_ts** | DateTime | 結果存入快取時 | `time.time() * 1000` | Server 完成推理的時間 |

**代碼位置 (Server)**:
```python
# C:/Repo/Github/vision_server/debug/udp_inference_worker.py
t_receive = time.time()
server_receive_ts = int(t_receive * 1000)

# 開始推理
t_process_start = time.time()
server_process_start_ts = int(t_process_start * 1000)

# 完成推理
t_send = time.time()
server_send_ts = int(t_send * 1000)
```

**格式轉換 (Excel)**:
```python
# C:/Repo/Github/vision_server/debug/inference_logger.py:83-91
def _format_timestamp(unix_ms):
    """Convert Unix milliseconds to human-readable: 2026-04-15 17:13:56.143"""
    if unix_ms == 0 or unix_ms is None:
        return ""
    dt = datetime.fromtimestamp(unix_ms / 1000.0)
    return dt.strftime("%Y-%m-%d %H:%M:%S.%f")[:-3]  # 保留 3 位毫秒
```

---

### 3️⃣ 時間測量欄位 (Timing Columns)

#### 主要時間指標

| 欄位 | 單位 | 計算方式 | 數據來源 | 可靠性 |
|------|------|---------|---------|--------|
| **latency_ms** | ms | `unity_receive_ts - unity_send_ts` | Unity-only | ✅ 可靠 |
| **queue_wait_ms** | ms | `server_process_start_ts - server_receive_ts` | Server-only | ✅ 可靠 |
| **server_proc_ms** | ms | 從 `response.processing_time_ms` | Server-only | ✅ 可靠 |
| **parse_ms** | ms | Stopwatch 測量 | Unity-only | ✅ 可靠 (HTTP), 0 (UDP) |
| **udp_send_ms** | ms | Stopwatch 測量 UDP send() | Unity-only | ✅ 可靠 (Method A 驗證) |

**詳細公式**:

```csharp
// 1. E2E 延遲 (Unity-only, 避免時鐘偏移)
latency_ms = unity_receive_ts - unity_send_ts;

// 2. 排隊等待時間 (Server-only)
queue_wait_ms = server_process_start_ts - server_receive_ts;

// 3. Server 處理時間 (從回應取得)
server_proc_ms = response.processing_time_ms;

// 4. JSON 解析時間 (只在 HTTP POST 模式測量)
// UDP 模式：parse_ms = 0 (解析在背景協程，不計入 E2E)
long parseStart = TimestampUtil.GetUnixTimestampMs();
// ... JsonUtility.FromJson(jsonResponse) ...
long parseEnd = TimestampUtil.GetUnixTimestampMs();
parse_ms = parseEnd - parseStart;

// 5. UDP 發送時間 (Method A 驗證用)
long sendStart = TimestampUtil.GetUnixTimestampMs();
udpClient.Send(frameData, frameData.Length, serverEndpoint);
long sendEnd = TimestampUtil.GetUnixTimestampMs();
udp_send_ms = sendEnd - sendStart;
```

**代碼位置**:
- **latency_ms**: `FrameTrace.cs:97` (`MarkCompleted()`)
- **queue_wait_ms**: `*InferenceRunManager.cs` (UDP 回調處理)
- **server_proc_ms**: 從 Server 回應解析
- **parse_ms**: `*InferenceRunManager.cs` (HTTP 模式)
- **udp_send_ms**: `UDPTransport.cs:82-94`

#### 網路時間指標 (Residual Method B)

| 欄位 | 單位 | 計算方式 | 說明 |
|------|------|---------|------|
| **upload_ms** | ms | `network_total × upload_ratio` | 上傳時間（估算） |
| **download_ms** | ms | `network_total × (1 - upload_ratio)` | 下載時間（估算） |

**完整計算步驟**:

```csharp
// Step 1: 計算剩餘時間（扣除 Server 時間和解析時間）
float networkTotalMs = Mathf.Max(0f, latency_ms - queue_wait_ms - server_proc_ms - parse_ms);

// Step 2: 計算上傳比例（基於壓縮後的數據大小）
int uploadBytesCompressed = trace.upload_bytes_compressed;    // JPEG 大小
int downloadBytesCompressed = trace.download_bytes_compressed; // JSON 大小
int totalBytes = uploadBytesCompressed + downloadBytesCompressed;
float uploadRatio = totalBytes > 0 ? (float)uploadBytesCompressed / totalBytes : 0.5f;

// Step 3: 按比例分配網路時間
float upload_ms = networkTotalMs × uploadRatio;
float download_ms = networkTotalMs × (1.0f - uploadRatio);
```

**實際範例 (frame_id=25)**:
```
E2E = 102 ms
queue_wait_ms = 43 ms
server_proc_ms = 42.1 ms
parse_ms = 0 ms (UDP mode)

Step 1: network_total = Mathf.Max(0f, 102 - 43 - 42.1 - 0) = 16.9 ms

Step 2:
  upload_bytes = 25511 bytes (JPEG)
  download_bytes = 4408 bytes (JSON)
  total_bytes = 29919 bytes
  upload_ratio = 25511 / 29919 = 0.853

Step 3:
  upload_ms = 16.9 × 0.853 = 14.4 ms ✅
  download_ms = 16.9 × (1 - 0.853) = 2.5 ms ✅
```

**為什麼用 `Mathf.Max(0f, ...)`?**

防止 **timing jitter** 導致的負值：

```csharp
// 範例：server 時間略大於 E2E 時間（±5ms 測量誤差）
E2E = 59 ms
queue_wait_ms = 39 ms
server_proc_ms = 35.8 ms

network_total = 59 - 39 - 35.8 = -15.8 ms  ❌ 負值!

// 使用 Mathf.Max 保護
network_total = Mathf.Max(0f, -15.8) = 0 ms  ✅ Clamp 到 0
```

**代碼位置**:
```csharp
// Assets/PassthroughCameraApiSamples/Segmentation/SegmentationInference/Scripts/SegmentationInferenceRunManager.cs:1693
float networkTotalMs = Mathf.Max(0f, e2eMs - serverProcMs - queueWaitMs);

// Assets/PassthroughCameraApiSamples/PoseEstimation/Scripts/PoseInferenceRunManager.cs:1763
float networkTotalMs = Mathf.Max(0f, e2eMs - serverProcMs - queueWaitMs);

// Assets/PassthroughCameraApiSamples/MultiObjectDetection/SentisInference/Scripts/SentisInferenceRunManager.cs:1511
float networkTotalMs = Mathf.Max(0f, e2eMs - serverProcMs - queueWaitMs);
```

---

### 4️⃣ 百分比欄位 (Percentage Columns)

| 欄位 | 單位 | 計算方式 | 說明 |
|------|------|---------|------|
| **server_pct** | % | `(server_proc_ms / latency_ms) × 100` | Server 處理時間佔 E2E 的百分比 |
| **upload_pct** | % | `(upload_ms / latency_ms) × 100` | 上傳時間佔 E2E 的百分比 |
| **download_pct** | % | `(download_ms / latency_ms) × 100` | 下載時間佔 E2E 的百分比 |

**公式**:

```csharp
// 計算百分比
float server_pct = latency_ms > 0 ? (server_proc_ms / latency_ms) * 100.0f : 0.0f;
float upload_pct = latency_ms > 0 ? (upload_ms / latency_ms) * 100.0f : 0.0f;
float download_pct = latency_ms > 0 ? (download_ms / latency_ms) * 100.0f : 0.0f;
```

**實際範例 (frame_id=25)**:
```
latency_ms = 102 ms
server_proc_ms = 42.1 ms
upload_ms = 14.4 ms
download_ms = 2.5 ms
queue_wait_ms = 43 ms (不計入百分比)

server_pct = (42.1 / 102) × 100 = 41.3% ✅
upload_pct = (14.4 / 102) × 100 = 14.1% ✅
download_pct = (2.5 / 102) × 100 = 2.5% ✅

總和 = 41.3% + 14.1% + 2.5% = 57.9%
(不到 100% 因為 queue_wait_ms = 42.2% 未計入)
```

**⚠️ 注意**: `queue_wait_ms` **不計入**百分比（歷史慣例），所以三個百分比相加可能不等於 100%。

---

### 5️⃣ 數據大小欄位 (Payload Size Columns)

| 欄位 | 單位 | 計算方式 | 說明 |
|------|------|---------|------|
| **image_width** | pixels | 原始相機解析度 | 下採樣前的寬度 |
| **image_height** | pixels | 原始相機解析度 | 下採樣前的高度 |
| **upload_bytes_uncompressed** | bytes | `width × height × 3` | RGB24 原始大小 (理論值) |
| **upload_bytes_compressed** | bytes | `jpegData.Length` | JPEG 壓縮後實際大小 |
| **download_bytes_uncompressed** | bytes | `Encoding.UTF8.GetByteCount(jsonResponse)` | JSON 文字大小 |
| **download_bytes_compressed** | bytes | 與 uncompressed 相同 | UDP 模式無壓縮 |

**計算範例**:

```csharp
// 上傳大小
int width = 640, height = 480, channels = 3;
int uploadBytesUncompressed = width * height * channels;  // 921600 bytes (900 KB)

// JPEG 編碼
byte[] jpegData = texture.EncodeToJPEG(quality: 60);
int uploadBytesCompressed = jpegData.Length;  // ~25000 bytes (24 KB)

// 壓縮率
float compressionRatio = (float)uploadBytesUncompressed / uploadBytesCompressed;
// = 921600 / 25000 = 36.9x (97% reduction)

// 下載大小
string jsonResponse = "{ ... }";
int downloadBytesUncompressed = System.Text.Encoding.UTF8.GetByteCount(jsonResponse);
// ~4408 bytes (4.3 KB)

int downloadBytesCompressed = downloadBytesUncompressed;  // UDP 模式無壓縮
```

**壓縮率統計**:
- **上傳 (JPEG)**: 3-4% of raw RGB (96-97% reduction)
- **下載 (JSON)**: 無壓縮（UDP 模式）

**代碼位置**:
```csharp
// Assets/PassthroughCameraApiSamples/*/Scripts/*InferenceRunManager.cs
int uploadBytesUncompressed = textureToEncode.width * textureToEncode.height * 3;
byte[] jpegBytes = textureToEncode.EncodeToJPG(jpegQuality);
int uploadBytesCompressed = jpegBytes.Length;

// UDP 回調處理
int downloadBytesUncompressed = System.Text.Encoding.UTF8.GetByteCount(jsonResponse);
int downloadBytesCompressed = downloadBytesUncompressed;  // 無壓縮
```

---

### 6️⃣ 狀態和結果欄位 (State and Result Columns)

#### 幀狀態

| 欄位 | 數據類型 | 可能值 | 說明 |
|------|---------|--------|------|
| **final_state** | Enum | `Displayed`, `Dropped`, `Failed` | 幀的最終狀態 |
| **drop_reason** | String | `arrived_after_newer_{N}`, `queue_full`, ... | 丟棄原因（如適用） |
| **error_reason** | String | Exception message | 錯誤詳情（如 Failed） |

**狀態轉換**:
```
Pending → Completed → Displayed (正常流程)
Pending → Completed → Dropped (到達太晚)
Pending → Failed (網路/Server 錯誤)
```

**⚠️ 重要**: 只有**最終狀態** (`Displayed`, `Dropped`, `Failed`) 會寫入 Excel。中間狀態 (`Pending`, `Completed`) 會被過濾掉。

**代碼位置 (Server)**:
```python
# C:/Repo/Github/vision_server/debug/inference_logger.py:237-243
VALID_FINAL_STATES = {"Displayed", "Dropped", "Failed"}
if final_state not in VALID_FINAL_STATES:
    print(f"[TELEMETRY WARNING] Skipping frame {frame_id} with non-final state '{final_state}'")
    return  # SKIP THIS ROW
```

#### 推理結果

| 欄位 | 數據類型 | 計算方式 | 說明 |
|------|---------|---------|------|
| **detection_count** | Integer | `len(response.detections)` 或 `len(response.persons)` | 檢測數量（0 表示無檢測） |
| **avg_confidence** | Float | `sum(confidences) / len(detections)` | 平均檢測信心度 |
| **keypoint_avg_conf** | Float | `sum(keypoint_scores) / len(keypoints)` | 平均關鍵點信心度（僅 Pose） |

**計算範例 (MultiObjectDetection)**:
```csharp
// 從回應解析
int detectionCount = response.detections.detections.Length;  // 2

float confidenceSum = 0f;
foreach (var det in response.detections.detections) {
    confidenceSum += det.confidence;  // 0.34, 0.35
}
float avgConfidence = confidenceSum / detectionCount;  // (0.34 + 0.35) / 2 = 0.345
```

**計算範例 (PoseEstimation)**:
```csharp
// 關鍵點信心度
int totalKeypoints = 0;
float keypointScoreSum = 0f;

foreach (var person in response.skeleton.persons) {
    foreach (var kp in person.keypoints) {
        keypointScoreSum += kp.score;
        totalKeypoints++;
    }
}

float keypointAvgConf = totalKeypoints > 0 ? keypointScoreSum / totalKeypoints : 0f;
```

**代碼位置**:
```csharp
// Assets/PassthroughCameraApiSamples/MultiObjectDetection/SentisInference/Scripts/SentisInferenceRunManager.cs
// Assets/PassthroughCameraApiSamples/PoseEstimation/Scripts/PoseInferenceRunManager.cs
// Assets/PassthroughCameraApiSamples/Segmentation/SegmentationInference/Scripts/SegmentationInferenceRunManager.cs
```

---

### 7️⃣ Freeze 和 Drop 指標 (Performance Metrics)

#### Freeze 指標

| 欄位 | 單位 | 計算方式 | 說明 |
|------|------|---------|------|
| **freeze_frames_per_frame** | frames | `Time.frameCount - lastDisplayedFrameCount - 1` | 兩次顯示之間的 Unity 幀數 |
| **freeze_duration_ms** | ms | `freeze_frames × unity_frame_time` | Freeze 持續時間（毫秒） |
| **cumulative_freeze_frames** | frames | 累加總和 | Session 開始以來的總 freeze 幀數 |
| **freeze_ratio** | ratio | `freeze_frames / (freeze_frames + 1)` | Freeze 比例 |

**計算範例**:

```csharp
// 當顯示一個新幀時
int currentFrameCount = Time.frameCount;  // Unity 的全局幀計數器
int freezeFrames = currentFrameCount - m_lastDisplayedFrameCount - 1;

trace.freeze_frames = freezeFrames;
m_lastDisplayedFrameCount = currentFrameCount;

// 計算 freeze 持續時間
float unityFrameTime = Time.deltaTime * 1000f;  // 轉換為毫秒
trace.freeze_duration_ms = freezeFrames * unityFrameTime;

// 累加總數
m_cumulativeFreezeFrames += freezeFrames;
trace.cumulative_freeze_frames = m_cumulativeFreezeFrames;

// 計算比例
trace.freeze_ratio = (float)freezeFrames / (freezeFrames + 1);
```

**實際範例**:
```
Frame N 顯示於 Time.frameCount = 1000
  freeze_frames = 1000 - 977 - 1 = 22

Frame N+1 顯示於 Time.frameCount = 1023
  freeze_frames = 1023 - 1000 - 1 = 22

Frame N+2 顯示於 Time.frameCount = 1035
  freeze_frames = 1035 - 1023 - 1 = 11
```

**解讀**:
- `freeze_frames = 0`: 連續幀（理想但罕見）
- `freeze_frames = 11`: 11 個 Unity 幀沒有新的推理結果（視覺"凍結"）
- 數值越高 = 視覺品質越差（幀"卡頓"或"停滯"）

**計算預期 Freeze 幀數**:

```
freeze_frames = (inference_interval_ms / unity_frame_time_ms) - 1

其中:
  inference_interval_ms = 1000 / target_fps  (例如 1000/5 = 200ms for 5 FPS)
  unity_frame_time_ms = 1000 / unity_fps     (例如 1000/72 = 13.9ms for Quest 3 @ 72Hz)
```

**範例**:

| Unity FPS | Inference FPS | Inference Interval | Expected Freeze Frames |
|-----------|---------------|-------------------|------------------------|
| 72 Hz (Quest 3 預設) | 5 FPS | 200ms | (200 ÷ 13.9) - 1 = **13.4** |
| 90 Hz (Quest 3 高) | 5 FPS | 200ms | (200 ÷ 11.1) - 1 = **17** |
| 120 Hz (Quest 3 最大) | 5 FPS | 200ms | (200 ÷ 8.33) - 1 = **23** |
| 60 FPS (受限) | 5 FPS | 200ms | (200 ÷ 16.67) - 1 = **11** |
| 72 Hz | 10 FPS | 100ms | (100 ÷ 13.9) - 1 = **6.2** |

**代碼位置**:
```csharp
// Assets/PassthroughCameraApiSamples/*/Scripts/*InferenceRunManager.cs
int currentFrameCount = Time.frameCount;
int freezeFrames = currentFrameCount - m_lastDisplayedFrameCount - 1;
trace.freeze_frames = freezeFrames;

float unityFrameTime = Time.deltaTime * 1000f;
trace.freeze_duration_ms = freezeFrames * unityFrameTime;

m_cumulativeFreezeFrames += freezeFrames;
trace.cumulative_freeze_frames = m_cumulativeFreezeFrames;

trace.freeze_ratio = (float)freezeFrames / (freezeFrames + 1);

m_lastDisplayedFrameCount = currentFrameCount;
```

#### Drop 指標

| 欄位 | 單位 | 計算方式 | 說明 |
|------|------|---------|------|
| **frame_gap** | frames | `frame_id - prev_frame_id - 1` | 與前一個記錄幀的間隔 |
| **cumulative_dropped** | frames | 累加總和 | Session 開始以來的總丟棄幀數 |
| **cumulative_displayed** | frames | 累加總和 | Session 開始以來的總顯示幀數 |
| **drop_rate** | ratio | `cumulative_dropped / (cumulative_dropped + cumulative_displayed)` | 丟棄率 |

**計算範例**:

```csharp
// 計算 frame gap (僅在記錄幀時)
int frameGap = trace.frame_id - m_lastLoggedFrameId - 1;
trace.frame_gap = frameGap;
m_lastLoggedFrameId = trace.frame_id;

// 更新累計數量
if (trace.state == FrameState.Displayed) {
    m_cumulativeDisplayed++;
} else if (trace.state == FrameState.Dropped) {
    m_cumulativeDropped++;
}

trace.cumulative_displayed = m_cumulativeDisplayed;
trace.cumulative_dropped = m_cumulativeDropped;

// 計算 drop rate
int total = m_cumulativeDropped + m_cumulativeDisplayed;
trace.drop_rate = total > 0 ? (float)m_cumulativeDropped / total : 0f;
```

**實際範例**:
```
Frame 0: frame_gap = 0 (第一幀), cumulative_displayed = 1, drop_rate = 0%
Frame 1: frame_gap = 0 (連續), cumulative_displayed = 2, drop_rate = 0%
Frame 3: frame_gap = 1 (跳過 frame 2), cumulative_displayed = 3, cumulative_dropped = 1, drop_rate = 25%
Frame 5: frame_gap = 1 (跳過 frame 4), cumulative_displayed = 4, cumulative_dropped = 2, drop_rate = 33%
```

**Drop Reason 代碼**:

| Drop Reason | 說明 | 發生時機 |
|-------------|------|---------|
| `arrived_after_newer_{N}` | 幀在幀 N 已顯示後才到達 | 亂序到達 |
| `queue_full` | Server 接收隊列已滿（最多 3 個待處理） | Server 過載 |
| `inference_timeout` | Server 推理時間過長 | 模型卡住或崩潰 |
| `network_error` | UDP 封包丟失或 HTTP polling 失敗 | 網路問題 |
| `invalid_payload` | 幀標頭驗證失敗 | 封包損壞 |

**代碼位置 (Unity)**:
```csharp
// 檢查晚到達（亂序）
if (trace.frame_id < m_lastDisplayedFrameId)
{
    trace.state = FrameState.Dropped;
    trace.drop_reason = $"arrived_after_newer_{m_lastDisplayedFrameId}";
    trace.unity_drop_ts = TimestampUtil.GetUnixTimestampMs();
}
```

---

### 8️⃣ 配置欄位 (Configuration Columns)

| 欄位 | 數據類型 | 來源 | 說明 |
|------|---------|------|------|
| **target_fps** | Float | Unity Config | 配置的推理 FPS（5, 10 等） |
| **session_frame_index** | Integer | Unity | 本次記錄幀的順序索引（0, 1, 2, ...） |

**計算範例**:

```csharp
// target_fps 從 InferenceConfig 讀取
public class InferenceConfig
{
    public float targetFPS = 5.0f;  // 5 FPS 推理目標
}

// session_frame_index 是記錄幀的計數器
private int m_sessionFrameIndex = 0;

void LogFrameToServer(FrameTrace trace) {
    trace.session_frame_index = m_sessionFrameIndex;
    m_sessionFrameIndex++;
    // ...
}
```

---

### 9️⃣ GPU 指標欄位 (GPU Metrics Columns)

**狀態**: 當前**未實現**（所有值 = 0）。

| 欄位 | 單位 | 計劃來源 | 說明 |
|------|------|---------|------|
| **gpu_id** | Integer | `nvidia-smi` | GPU 設備 ID（0-3 for 4-GPU 系統） |
| **gpu_name** | String | `nvidia-smi` | GPU 型號（例如 "NVIDIA RTX A6000"） |
| **gpu_clock_mhz** | Integer | `nvidia-smi` | 圖形時鐘頻率（MHz）- **低值表示節流** |
| **gpu_mem_clock_mhz** | Integer | `nvidia-smi` | 記憶體時鐘頻率（MHz） |
| **gpu_temp_c** | Integer | `nvidia-smi` | 溫度（攝氏度） |
| **gpu_utilization_pct** | Integer | `nvidia-smi` | GPU 使用率（0-100%） |
| **gpu_memory_used_mb** | Integer | `nvidia-smi` | GPU 記憶體使用量（MB） |
| **gpu_power_draw_w** | Float | `nvidia-smi` | 功耗（瓦特） |

**未來實現**: 這些將通過在推理期間調用 `nvidia-smi` 來填充。

---

## 詳細計算公式

### 完整時間軸範例

**Frame 25 from MultiObjectDetection**:

```
Unity (Quest 3):
  T=0ms    發送 UDP (unity_send_ts = 12:48:57.095)
           jpegData.Length = 25511 bytes
           udp_send_ms = 0.5ms
           ↓
Server (PC):
  T=?      接收 UDP (server_receive_ts = 12:48:58.348)  ⚠️ 時鐘偏移 ~1.2s
           ↓ [排隊等待]
  T=?      開始推理 (server_process_start_ts = 12:48:58.391)
           queue_wait_ms = 391 - 348 = 43ms ✓
           ↓ [AI 推理]
  T=?      完成推理 (server_send_ts = 12:48:58.391)
           server_proc_ms = 42.1ms (從 response)
           jsonResponse.Length = 4408 bytes
           ↓
Unity (Quest 3):
  T=102ms  接收 HTTP (unity_receive_ts = 12:48:57.197)
           latency_ms = 197 - 095 = 102ms ✓
           parse_ms = 0ms (UDP mode)
           ↓
  T=107ms  顯示結果 (unity_display_ts = 12:48:57.202)
```

**計算**:

```
1. E2E 延遲 (Unity-only):
   latency_ms = 1713352137197 - 1713352137095 = 102ms ✓

2. Server 時間 (Server-only):
   queue_wait_ms = 1713352138391 - 1713352138348 = 43ms ✓
   server_proc_ms = 42.1ms (from response) ✓

3. 網路時間 (Residual):
   network_total = Mathf.Max(0f, 102 - 43 - 42.1 - 0) = 16.9ms ✓

4. 上傳/下載分配:
   upload_bytes = 25511 bytes
   download_bytes = 4408 bytes
   total_bytes = 29919 bytes
   upload_ratio = 25511 / 29919 = 0.853

   upload_ms = 16.9 × 0.853 = 14.4ms ✓
   download_ms = 16.9 × 0.147 = 2.5ms ✓

5. 百分比:
   server_pct = (42.1 / 102) × 100 = 41.3% ✓
   upload_pct = (14.4 / 102) × 100 = 14.1% ✓
   download_pct = (2.5 / 102) × 100 = 2.5% ✓

6. 驗證:
   43 + 42.1 + 14.4 + 2.5 = 102ms ✓ (匹配 E2E!)
```

---

## 數據驗證規則

### ✅ 完整性檢查

```python
import pandas as pd

df = pd.read_excel('inference_log.xlsx')

# 1. 時間戳單調性
assert (df['unity_send_ts'] < df['unity_receive_ts']).all(), "unity_receive_ts must be after unity_send_ts"
assert (df[df['final_state'] == 'Displayed']['unity_receive_ts'] < df[df['final_state'] == 'Displayed']['unity_display_ts']).all(), "unity_display_ts must be after unity_receive_ts"
assert (df['server_receive_ts'] <= df['server_process_start_ts']).all(), "server_process_start_ts must be >= server_receive_ts"
assert (df['server_process_start_ts'] <= df['server_send_ts']).all(), "server_send_ts must be >= server_process_start_ts"

# 2. 時間加總（允許 ±10ms 誤差）
df['calc_latency'] = df['upload_ms'] + df['queue_wait_ms'] + df['server_proc_ms'] + df['download_ms'] + df['parse_ms']
df['latency_diff'] = abs(df['latency_ms'] - df['calc_latency'])
inconsistent = df[df['latency_diff'] > 10]
print(f"時間加總不一致的幀數: {len(inconsistent)} / {len(df)} ({len(inconsistent)/len(df)*100:.1f}%)")

# 3. 無負值
assert (df['latency_ms'] >= 0).all(), "latency_ms must be non-negative"
assert (df['upload_ms'] >= 0).all(), "upload_ms must be non-negative"
assert (df['download_ms'] >= 0).all(), "download_ms must be non-negative"
assert (df['queue_wait_ms'] >= 0).all(), "queue_wait_ms must be non-negative"
assert (df['server_proc_ms'] >= 0).all(), "server_proc_ms must be non-negative"

# 4. 百分比合理性
assert (df['server_pct'] >= 0).all() and (df['server_pct'] <= 100).all(), "server_pct must be 0-100%"
assert (df['upload_pct'] >= 0).all() and (df['upload_pct'] <= 100).all(), "upload_pct must be 0-100%"
assert (df['download_pct'] >= 0).all() and (df['download_pct'] <= 100).all(), "download_pct must be 0-100%"

# 5. 狀態一致性
displayed_frames = df[df['final_state'] == 'Displayed']
assert displayed_frames['unity_display_ts'].notna().all(), "Displayed frames must have unity_display_ts"
assert displayed_frames['unity_drop_ts'].isna().all(), "Displayed frames must not have unity_drop_ts"

dropped_frames = df[df['final_state'] == 'Dropped']
assert dropped_frames['unity_drop_ts'].notna().all(), "Dropped frames must have unity_drop_ts"
assert dropped_frames['drop_reason'].str.len().gt(0).all(), "Dropped frames must have drop_reason"

failed_frames = df[df['final_state'] == 'Failed']
assert failed_frames['error_reason'].str.len().gt(0).all(), "Failed frames must have error_reason"

print("✅ 所有驗證通過!")
```

### 🔍 常見問題診斷

| 症狀 | 原因 | 修復 |
|------|------|------|
| `upload_ms` 負值 | 缺少 `Mathf.Max(0f, ...)` | 加入 clamp（已在 2026-04-17 修復） |
| `download_ms` 負值 | 同上 | 加入 clamp（已在 2026-04-17 修復） |
| `latency_ms` 很高 (>500ms) | Server 過載或網路擁塞 | 檢查 `queue_wait_ms` 和 `server_proc_ms` |
| `upload_ms = 0` 經常出現 | `networkTotalMs` clamp 到 0（server 時間 > E2E） | 正常，如果 server 處理主導 |
| `freeze_frames = 0` 經常出現 | 連續幀顯示（不切實際） | 檢查推理是否實際運行 |
| `百分比總和 > 100%` | 計算錯誤 | 檢查時間加總是否正確 |
| `百分比總和 < 100%` | `queue_wait_ms` 未計入 | 正常（歷史慣例） |

---

## 常見問題

### Q1: 為什麼 `upload_ms` / `download_ms` 有時為 0？

**A**: 這是正常的！當 `networkTotalMs` 被 clamp 到 0 時發生：

```csharp
network_total = Mathf.Max(0f, latency_ms - queue_wait_ms - server_proc_ms - parse_ms)
```

**原因**:
1. **Timing jitter**: Unity 和 Server 時間戳有 ±1-5ms 測量誤差
2. **Server 時間略大於 E2E**: 在這些 frame 中，server 處理時間 + 排隊時間超過了測量的 E2E 時間

**範例**:
```
E2E = 59 ms
queue_wait_ms = 39 ms
server_proc_ms = 35.8 ms

network_total = 59 - 39 - 35.8 = -15.8 ms
↓ (使用 Mathf.Max 保護)
network_total = 0 ms ✓

upload_ms = 0 × 0.853 = 0 ms
download_ms = 0 × 0.147 = 0 ms
```

**處理建議**: 在分析時可以保留這些數據，但標記為 "network time dominated by server processing"。

---

### Q2: 2026-04-17 之前的負值是什麼原因？

**A**: 代碼 bug - 缺少 `Mathf.Max(0f, ...)` 保護。

**舊代碼 (WRONG)**:
```csharp
float networkTotalMs = e2eMs - serverProcMs - queueWaitMs;  // 可能為負!
```

**修復後的代碼**:
```csharp
float networkTotalMs = Mathf.Max(0f, e2eMs - serverProcMs - queueWaitMs);  // ✅ Clamp 到 0
```

**分析建議**: 過濾掉所有負值（`upload_ms < 0` 或 `download_ms < 0`），這些數據不可信。

---

### Q3: `queue_wait_ms` 為什麼不計入百分比？

**A**: 歷史慣例。`queue_wait_ms` 是後來加入的欄位（2026-04-15），百分比計算保持向後兼容。

**如果要包含 `queue_wait_ms` 在百分比中**:
```python
df['queue_pct'] = (df['queue_wait_ms'] / df['latency_ms']) * 100
df['total_pct'] = df['server_pct'] + df['upload_pct'] + df['download_pct'] + df['queue_pct']
# total_pct 應該接近 100% (parse_ms 除外)
```

---

### Q4: 如何區分 HTTP POST 和 UDP Transport 模式？

**A**: 檢查 `udp_send_ms` 和 `parse_ms`：

| 模式 | `udp_send_ms` | `parse_ms` |
|------|--------------|-----------|
| **HTTP POST** | 0 | > 0 (5-150ms) |
| **UDP Transport** | > 0 (0.5-2ms) | 0 |

**代碼檢查**:
```python
df['transport_mode'] = 'unknown'
df.loc[df['udp_send_ms'] > 0, 'transport_mode'] = 'UDP'
df.loc[(df['udp_send_ms'] == 0) & (df['parse_ms'] > 0), 'transport_mode'] = 'HTTP'

print(df['transport_mode'].value_counts())
```

---

### Q5: `freeze_frames_per_frame` 的合理範圍是多少？

**A**: 取決於 **Unity FPS** 和 **推理 FPS**：

```
expected_freeze_frames = (1000 / inference_fps) / (1000 / unity_fps) - 1
```

**常見配置**:
- **5 FPS 推理, 72 Hz Unity**: `freeze_frames ≈ 13-14`
- **10 FPS 推理, 72 Hz Unity**: `freeze_frames ≈ 6-7`
- **5 FPS 推理, 60 FPS Unity**: `freeze_frames ≈ 11-12`

**如果實際值與預期差異很大**:
- 太低 → Unity FPS 可能低於預期（檢查性能瓶頸）
- 太高 → 推理 FPS 低於目標（檢查 `latency_ms` 和 server 性能）

---

## 代碼位置

### Unity 端

| 組件 | 文件 | 行數 | 功能 |
|------|------|------|------|
| **FrameTrace** | `FrameTrace.cs` | 全部 | 幀元數據結構 |
| **Segmentation Timing** | `SegmentationInferenceRunManager.cs` | 1693-1722 | Method B 計算 |
| **PoseEstimation Timing** | `PoseInferenceRunManager.cs` | 1763-1792 | Method B 計算 |
| **MultiObjectDetection Timing** | `SentisInferenceRunManager.cs` | 1511-1540 | Method B 計算 |
| **UDP Send Time** | `UDPTransport.cs` | 82-94 | Method A 測量 |
| **Timestamp Util** | `TimestampUtil.cs` | 全部 | Unix 毫秒時間戳 |

### Server 端

| 組件 | 文件 | 行數 | 功能 |
|------|------|------|------|
| **Excel Logger** | `inference_logger.py` | 119-298 | 寫入行到 Excel |
| **Column Definitions** | `inference_logger.py` | 18-77 | COLUMNS 列表 |
| **UDP Worker** | `udp_inference_worker.py` | 全部 | UDP 推理處理 |
| **Segmentation Route** | `app/routes/segmentation.py` | 全部 | Segmentation 端點 |

---

## 附錄：快速參考公式總結

```python
# ============== 身份 ==============
session_id = Guid.NewGuid()  # 場景啟動時
frame_id = counter++  # 遞增

# ============== 時間戳 (Unity-only) ==============
unity_send_ts = TimestampUtil.GetUnixTimestampMs()  # 發送前
unity_receive_ts = TimestampUtil.GetUnixTimestampMs()  # 接收後
unity_display_ts = TimestampUtil.GetUnixTimestampMs()  # 顯示時
unity_drop_ts = TimestampUtil.GetUnixTimestampMs()  # 丟棄時

# ============== 時間戳 (Server-only) ==============
server_receive_ts = time.time() * 1000  # UDP 監聽器
server_process_start_ts = time.time() * 1000  # 推理前
server_send_ts = time.time() * 1000  # 推理後

# ============== 主要時間指標 ==============
latency_ms = unity_receive_ts - unity_send_ts  # Unity-only
queue_wait_ms = server_process_start_ts - server_receive_ts  # Server-only
server_proc_ms = response.processing_time_ms  # 從 server
parse_ms = 0  # UDP 模式 (HTTP 模式測量 JSON 解析時間)
udp_send_ms = sendEnd - sendStart  # 實際 UDP send() 時間

# ============== 網路時間 (Residual Method B) ==============
network_total = Mathf.Max(0f, latency_ms - queue_wait_ms - server_proc_ms - parse_ms)
upload_ratio = upload_bytes_compressed / (upload_bytes_compressed + download_bytes_compressed)
upload_ms = network_total × upload_ratio
download_ms = network_total × (1 - upload_ratio)

# ============== 百分比 ==============
server_pct = (server_proc_ms / latency_ms) × 100
upload_pct = (upload_ms / latency_ms) × 100
download_pct = (download_ms / latency_ms) × 100

# ============== Freeze 指標 ==============
freeze_frames_per_frame = Time.frameCount - lastDisplayedFrameCount - 1
freeze_duration_ms = freeze_frames × unity_frame_time
cumulative_freeze_frames += freeze_frames
freeze_ratio = freeze_frames / (freeze_frames + 1)

# ============== Drop 指標 ==============
frame_gap = frame_id - prev_frame_id - 1
cumulative_dropped += 1  # (if Dropped)
cumulative_displayed += 1  # (if Displayed)
drop_rate = cumulative_dropped / (cumulative_dropped + cumulative_displayed)

# ============== 狀態 ==============
final_state ∈ {Displayed, Dropped, Failed}
drop_reason = "arrived_after_newer_{N}" | "queue_full" | ...
```

---

**Last Updated**: 2026-04-17
**Version**: 1.0
**Changelog**:
- 2026-04-17: 初始版本，包含完整的 77 個欄位定義
- 2026-04-17: 加入 `Mathf.Max(0f, ...)` 防止負值
- 2026-04-15: 加入 UDP transport 支援
- 2026-04-15: 加入 `queue_wait_ms` 和 `session_id`
