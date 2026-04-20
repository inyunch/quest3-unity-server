# Excel Logging Architecture - Corrected

**Date**: 2026-04-16
**Status**: ✅ Complete

---

## 核心原則

遵循與 rendering 相同的原則：

1. ✅ **不發明新流程** - 沿用既有的 FrameTrace Dictionary 架構
2. ✅ **一個 frame 一筆記錄** - 整個 lifecycle 都只更新同一筆 `FrameTrace`
3. ✅ **不在 polling 時寫 Excel** - 只在 **實驗結束時一次性匯出**
4. ✅ **Unity 端負責** - Excel logging 是 Unity 的責任，不是 server 的責任

---

## 錯誤的舊架構（已移除）

### ❌ 之前的錯誤做法

**Server 端**：
- ❌ UDP worker 有 `_log_to_excel()` 方法
- ❌ 每次 inference 都調用 server 端 Excel logging
- ❌ 使用 N+1 delayed telemetry 從 Unity 發送 JSON
- ❌ Server 寫入 `C:\Repo\Github\vision_server\debug\logs\inference_log_*.xlsx`

**Unity 端**：
- ❌ `GetPreviousFrameTelemetryJson()` 方法在每次 UDP send 時序列化 telemetry
- ❌ 透過 UDP packet 發送 telemetry JSON 給 server
- ❌ 沒有任何 CSV/Excel export 功能

**問題**：
1. 違反 Unity 端應該負責 logging 的原則
2. 增加 UDP packet 大小（浪費帶寬）
3. Server 需要解析 Unity 的 telemetry（架構錯誤）
4. 無法在 Quest 上直接獲得 CSV 檔案

---

## 正確的新架構（已實作）

### ✅ Unity 端（主要責任）

**數據結構**：
```csharp
// 已存在的架構（不需新增）
private Dictionary<int, FrameTrace> m_frameTraces = new Dictionary<int, FrameTrace>();
private object m_frameTracesLock = new object();
```

**Lifecycle 更新**（已存在）：
```csharp
// Send
FrameTrace trace = new FrameTrace(m_frameId);
m_frameTraces[m_frameId] = trace;

// Receive
trace.MarkCompleted(receiveTs);

// Display
trace.MarkDisplayed(displayTs);

// Drop
trace.MarkDropped(dropTs, reason);

// Timeout
trace.MarkFailed("Timeout after 5.0s");
```

**Export 時機**（新增）：
```csharp
private void OnDestroy()
{
    // 實驗結束時一次性匯出所有 frame traces
    if (m_useServerInference && m_frameTraces.Count > 0)
    {
        ExportFrameTracesToCSV();
    }
}
```

**Export 實作**（新增）：
```csharp
private void ExportFrameTracesToCSV()
{
    // 1. 建立檔案路徑（Quest persistent data path）
    string filepath = Path.Combine(Application.persistentDataPath,
        $"InferenceLog_{m_sessionId}_{timestamp}.csv");

    // 2. 寫入 CSV header
    writer.WriteLine("scene,session_id,frame_id," +
        "unity_send_ts,unity_receive_ts,unity_display_ts,unity_drop_ts," +
        "server_receive_ts,server_process_start_ts,server_send_ts," +
        "e2e_ms,server_proc_ms,upload_ms,download_ms,parse_ms," +
        "final_state,drop_reason,error_reason,freeze_frames," +
        "upload_bytes_uncompressed,upload_bytes_compressed," +
        "download_bytes_uncompressed,download_bytes_compressed," +
        "detection_count,avg_confidence");

    // 3. 遍歷 m_frameTraces，每個 frame 寫一列
    foreach (var kvp in m_frameTraces)
    {
        FrameTrace trace = kvp.Value;

        // 只匯出 final states（Displayed, Dropped, Failed）
        if (trace.state != FrameState.Displayed &&
            trace.state != FrameState.Dropped &&
            trace.state != FrameState.Failed)
        {
            continue;  // 跳過 Pending/Completed
        }

        writer.WriteLine($"{scene},{trace.session_id},{trace.frame_id},...");
    }

    Debug.Log($"[EXCEL EXPORT] Exported {m_frameTraces.Count} frame traces to: {filepath}");
    Debug.Log($"[EXCEL EXPORT] Pull via: adb pull {filepath}");
}
```

### ✅ Server 端（無責任）

**完全移除**：
- ❌ `_log_to_excel()` 方法已刪除
- ❌ `await self._log_to_excel(...)` 調用已移除
- ❌ 不再有任何 Excel logging 邏輯

**UDP Transport 簡化**：
```python
# 不再接收 telemetry JSON
# 只處理 inference request 和 response
```

---

## 檔案修改摘要

### Server Side

#### `C:\Repo\Github\vision_server\app\workers\udp_inference_worker.py`

**移除**：
- Lines 137-138: `await self._log_to_excel(...)` 調用
- Lines 341-519: 整個 `_log_to_excel()` 方法（179 行）

**結果**：UDP worker 只負責 inference，不負責 logging

---

### Unity Side

#### `Assets/.../MultiObjectDetection/SentisInference/Scripts/SentisInferenceRunManager.cs`

**新增**：
- Lines 179-183: `OnDestroy()` 中調用 `ExportFrameTracesToCSV()`
- Lines 1494-1578: `ExportFrameTracesToCSV()` 和 `EscapeCSV()` 方法

**移除**：
- Lines 1201-1203: `GetPreviousFrameTelemetryJson()` 調用
- Lines 1360-1390: 整個 `GetPreviousFrameTelemetryJson()` 方法

**修改**：
- Line 1203: `UDPTransport.SendFrame(..., telemetryJson: null)` - 不再發送 telemetry

---

#### `Assets/.../PoseEstimation/Scripts/PoseInferenceRunManager.cs`

**新增**：
- Lines 151-158: `OnDestroy()` 中調用 `ExportFrameTracesToCSV()`
- Lines 1783-1867: `ExportFrameTracesToCSV()` 和 `EscapeCSV()` 方法

---

#### `Assets/.../Shared/Scripts/UDPTransport.cs`

**無需修改**：
- 已支持 `telemetryJson = null` 參數（line 51）
- 當 `telemetryJson == null` 時，`telemetryBytes.Length == 0`（lines 60-62）

---

## CSV 檔案位置與提取

### 檔案位置（Quest 3）

```
/sdcard/Android/data/{package_name}/files/InferenceLog_{session_id}_{timestamp}.csv
```

**Example**:
```
/sdcard/Android/data/com.meta.passthroughcamerasamples/files/InferenceLog_abc123_2026-04-16_15-30-45.csv
```

### 提取方法

**方法 1: adb pull（推薦）**
```bash
# Unity logs 會顯示完整路徑
adb logcat -s Unity | findstr "EXCEL EXPORT"
# Output: [EXCEL EXPORT] Pull via: adb pull /sdcard/Android/data/.../InferenceLog_abc123_2026-04-16_15-30-45.csv

# 使用該路徑 pull 檔案
adb pull /sdcard/Android/data/com.meta.passthroughcamerasamples/files/InferenceLog_abc123_2026-04-16_15-30-45.csv .
```

**方法 2: adb shell（查看所有檔案）**
```bash
adb shell
cd /sdcard/Android/data/com.meta.passthroughcamerasamples/files/
ls -lh InferenceLog_*
```

---

## CSV 格式

### Header

```csv
scene,session_id,frame_id,
unity_send_ts,unity_receive_ts,unity_display_ts,unity_drop_ts,
server_receive_ts,server_process_start_ts,server_send_ts,
e2e_ms,server_proc_ms,upload_ms,download_ms,parse_ms,
final_state,drop_reason,error_reason,freeze_frames,
upload_bytes_uncompressed,upload_bytes_compressed,
download_bytes_uncompressed,download_bytes_compressed,
detection_count,avg_confidence
```

### Example Row

```csv
MultiObjectDetection,abc123-def456-789,42,
1713369600000,1713369600245,1713369600280,0,
1713369600050,1713369600060,1713369600240,
245.00,180.00,50.00,10.00,5.00,
Displayed,"","",0,
921600,45678,
1234,1234,
2,0.8523
```

### Field Descriptions

| Field | Type | Description |
|-------|------|-------------|
| scene | string | "MultiObjectDetection" 或 "PoseEstimation" |
| session_id | GUID | 實驗 session GUID |
| frame_id | int | Frame 序號 |
| unity_send_ts | long | Unity 發送時間（Unix ms） |
| unity_receive_ts | long | Unity 收到回應時間（Unix ms） |
| unity_display_ts | long? | 顯示時間（Unix ms，0 表示未顯示） |
| unity_drop_ts | long? | Drop 時間（Unix ms，0 表示未 drop） |
| server_receive_ts | long | Server 收到時間（Unix ms） |
| server_process_start_ts | long | Server 開始處理時間（Unix ms） |
| server_send_ts | long | Server 送出時間（Unix ms） |
| e2e_ms | float | End-to-end latency（Unity → Server → Unity） |
| server_proc_ms | float | Server inference 時間 |
| upload_ms | float | Upload 時間（估計） |
| download_ms | float | Download 時間（估計） |
| parse_ms | float | JSON parse 時間 |
| **final_state** | enum | **Displayed**, **Dropped**, 或 **Failed**（不會有 Pending/Completed） |
| drop_reason | string | Drop 原因（CSV escaped） |
| error_reason | string | Error 訊息（CSV escaped） |
| freeze_frames | int | Unity frames between displays |
| upload_bytes_uncompressed | int | RGB24 size |
| upload_bytes_compressed | int | JPEG size |
| download_bytes_uncompressed | int | JSON text size |
| download_bytes_compressed | int | Gzip size（如有） |
| detection_count | int? | Detection 數量（nullable） |
| avg_confidence | float | Average confidence |

---

## 驗證規則

### Final State 互斥（在 Unity 端執行）

```csharp
// 只匯出 final states
if (trace.state != FrameState.Displayed &&
    trace.state != FrameState.Dropped &&
    trace.state != FrameState.Failed)
{
    continue;  // 跳過 Pending/Completed
}
```

### Timestamp XOR（隱含驗證）

```csharp
// Displayed: unity_display_ts != 0, unity_drop_ts == 0
trace.MarkDisplayed(displayTs);  // sets unity_display_ts, leaves unity_drop_ts as null

// Dropped: unity_drop_ts != 0, unity_display_ts == 0
trace.MarkDropped(dropTs, reason);  // sets unity_drop_ts, leaves unity_display_ts as null
```

**CSV output**：
```csv
...,unity_display_ts,unity_drop_ts,...,final_state,...
...,1713369600280,0,...,Displayed,...        ← unity_display_ts > 0, unity_drop_ts == 0
...,0,1713369600300,...,Dropped,...          ← unity_display_ts == 0, unity_drop_ts > 0
```

---

## 測試步驟

### Step 1: 運行實驗

1. 啟動 server
2. 在 Quest 3 上運行 MultiObjectDetection scene
3. 讓實驗運行一段時間（例如 30 秒，~300 frames）
4. **關閉 Unity app**（觸發 OnDestroy）

### Step 2: 查看 Unity Logs

```bash
adb logcat -s Unity | findstr "EXCEL EXPORT"
```

**Expected output**:
```
[EXCEL EXPORT] Exported 287 frame traces to: /sdcard/Android/data/com.meta.passthroughcamerasamples/files/InferenceLog_abc123_2026-04-16_15-30-45.csv
[EXCEL EXPORT] Pull via: adb pull /sdcard/Android/data/com.meta.passthroughcamerasamples/files/InferenceLog_abc123_2026-04-16_15-30-45.csv
```

### Step 3: 提取 CSV 檔案

```bash
adb pull /sdcard/Android/data/com.meta.passthroughcamerasamples/files/InferenceLog_abc123_2026-04-16_15-30-45.csv .
```

### Step 4: 驗證 CSV 內容

用 Excel 打開檔案，檢查：

1. ✅ **Header 正確**：24 個欄位
2. ✅ **No duplicate frame_ids**：每個 frame_id 只有一列
3. ✅ **Only final states**：所有 final_state 都是 "Displayed", "Dropped", 或 "Failed"
4. ✅ **XOR validation**：
   - Displayed rows: `unity_display_ts > 0 AND unity_drop_ts == 0`
   - Dropped rows: `unity_drop_ts > 0 AND unity_display_ts == 0`
5. ✅ **No Pending/Completed rows**：沒有中間狀態的列

---

## 優勢

### 1. 架構正確性 ✅

- Unity 端全權負責 frame lifecycle tracking
- Server 只負責 inference，不涉及 telemetry
- 符合「不發明新流程」原則

### 2. 性能改善 ✅

- UDP packet 不再包含 telemetry JSON（減少帶寬）
- Server 不需解析 telemetry（減少 CPU）
- 只在實驗結束時寫檔（不影響 runtime performance）

### 3. 數據完整性 ✅

- 一個 frame 一筆記錄（no duplicates）
- 只匯出 final states（no incomplete data）
- Quest 上直接可得 CSV（不需依賴 server）

### 4. 可擴展性 ✅

- 未來可添加手動 export 功能（例如按鈕觸發）
- 可支持多種格式（CSV, JSON, binary）
- 可添加自動上傳功能（例如 Wi-Fi sync）

---

## 未來改進建議

### 1. 手動 Export 功能

**現狀**：只在 OnDestroy 時自動匯出

**建議**：添加 UI 按鈕手動觸發 export
```csharp
public void ManualExportButtonClicked()
{
    ExportFrameTracesToCSV();
    Debug.Log("[EXCEL EXPORT] Manual export completed");
}
```

### 2. 自動上傳功能

**現狀**：需要 adb pull 手動提取

**建議**：實驗結束後自動上傳到 server
```csharp
IEnumerator UploadCSVToServer(string filepath)
{
    byte[] csvBytes = File.ReadAllBytes(filepath);
    UnityWebRequest request = UnityWebRequest.Put(
        $"{serverUrl}/upload_log",
        csvBytes
    );
    yield return request.SendWebRequest();
}
```

### 3. 多格式支持

**現狀**：只支持 CSV

**建議**：支持 JSON 和 binary 格式
```csharp
ExportFrameTracesToJSON();  // Smaller size
ExportFrameTracesToBinary();  // Fastest parsing
```

---

**Last Updated**: 2026-04-16
**Implementation**: Complete ✅
**Testing**: Pending ⏳
