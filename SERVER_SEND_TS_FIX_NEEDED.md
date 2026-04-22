# server_send_ts Issue - Fix Required

## 問題

CSV telemetry 中的 `server_send_ts` 欄位全部是 `0`

## 根本原因

**Server 端沒有在 response 中返回 `server_send_ts` timestamp。**

### Unity 端代碼（正確）

**FrameTelemetryTracker.cs:134**
```csharp
trace.server_send_ts = response.server_send_ts;  // ← 從 server response 複製
```

**FrameResponse.cs:37**
```csharp
public long server_send_ts;  // When server sent UDP response
```

Unity 期望從 server response JSON 中獲取此欄位，但 server 沒有提供。

---

## Server 端缺失

### 當前 Server Response (InferenceResult.to_legacy_format())

**檔案**: `C:\Repo\Github\vision_server\app\core\inference\base.py:80-112`

```python
response = {
    "processing_time_ms": self.processing_time_ms,
    "input_image_width": self.input_image_width,
    "input_image_height": self.input_image_height,
    "model_input_width": self.model_input_width,
    "model_input_height": self.model_input_height,
}
# ❌ 缺少 server_send_ts
# ❌ 缺少 server_receive_ts
# ❌ 缺少 server_process_start_ts
```

Server 只返回 `processing_time_ms`，但不返回 timestamp。

---

## 修復方案

### 選項 1: 修改 InferenceResult（推薦）

在 `app/core/inference/base.py` 中添加 timestamp 欄位：

```python
@dataclass
class InferenceResult:
    """Pure inference result."""
    # Processing time (ms)
    processing_time_ms: float

    # ✅ 添加 server timestamps (Unix milliseconds)
    server_receive_ts: Optional[int] = None      # When server received frame
    server_process_start_ts: Optional[int] = None # When inference started (after queue)
    server_send_ts: Optional[int] = None         # When server sent response

    # ... rest of fields
```

更新 `to_legacy_format()`:
```python
def to_legacy_format(self, mode: InferenceMode) -> Dict[str, Any]:
    response = {
        "processing_time_ms": self.processing_time_ms,
        "input_image_width": self.input_image_width,
        "input_image_height": self.input_image_height,
        "model_input_width": self.model_input_width,
        "model_input_height": self.model_input_height,

        # ✅ 添加 timestamps
        "server_receive_ts": self.server_receive_ts or 0,
        "server_process_start_ts": self.server_process_start_ts or 0,
        "server_send_ts": self.server_send_ts or 0,
    }
    # ... rest
```

---

### 選項 2: 在 UDP Worker 添加 timestamp

**UDP Inference Worker** (發送 response 的地方):

```python
import time

# Before sending response
result_dict = result.to_legacy_format(mode)

# ✅ 添加 send timestamp
result_dict["server_send_ts"] = int(time.time() * 1000)

# Serialize and send
response_json = json.dumps(result_dict)
```

**同時在 frame 接收時添加**:
```python
# When frame is received
frame_data["server_receive_ts"] = int(time.time() * 1000)

# When inference starts
frame_data["server_process_start_ts"] = int(time.time() * 1000)
```

---

## 驗證方法

### 1. 檢查 Unity Logs

```bash
adb logcat -s Unity | findstr "server_send"
```

**預期看到** (修復後):
```
[TELEMETRY DEBUG] server_send=1713712245375
```

**當前看到** (錯誤):
```
[TELEMETRY DEBUG] server_send=0
```

---

### 2. 檢查 CSV 檔案

打開 CSV 檔案，檢查 `server_send_ts` 欄位（第 11 欄）:

**當前**:
```csv
...,server_receive_ts,server_process_start_ts,server_send_ts,...
...,1713712245150,1713712245155,0,...          ← 全部是 0
```

**修復後**:
```csv
...,server_receive_ts,server_process_start_ts,server_send_ts,...
...,1713712245150,1713712245155,1713712245375,...  ← 有正確值
```

---

### 3. 計算 Download Time

有了 `server_send_ts`，可以準確計算 download time:

```
download_ms = unity_receive_ts - server_send_ts
```

**當前**: `download_ms` 計算不準確（因為 `server_send_ts = 0`）

---

## 影響範圍

### 受影響的 Telemetry 欄位

1. ✅ `server_receive_ts` - 正確（Unity 有正確接收）
2. ✅ `server_process_start_ts` - 正確（Unity 有正確接收）
3. ❌ `server_send_ts` - **錯誤（全部是 0）**
4. ⚠️ `download_ms` - 不準確（依賴 `server_send_ts`）
5. ⚠️ `server_e2e_ms` - 可能不準確（依賴完整 timestamps）

### 不受影響的欄位

- `latency_ms` (E2E) - 正確（使用 Unity 端 timestamps）
- `upload_ms` - 正確（計算自 Unity 端）
- `queue_wait_ms` - 正確（= server_process_start_ts - server_receive_ts）
- `processing_time_ms` - 正確（server 有返回）

---

## 優先級

**高優先級** - 應該修復

原因：
- `download_ms` 是關鍵性能指標
- `server_send_ts` 用於計算網絡往返時間
- 影響所有 3 個 inference modes (Segmentation, Pose, Detection)
- 修復簡單（只需在 server 端添加一行代碼）

---

## 修復步驟

### 在 vision_server 中：

1. 找到 UDP worker 發送 response 的位置
2. 添加 `server_send_ts` timestamp (Unix milliseconds)
3. 測試驗證

### 驗證：

```bash
# 1. 重啟 server
python -m uvicorn app.main:app --host 0.0.0.0 --port 8001 --workers 1

# 2. 在 Quest 上執行 inference
# 3. 檢查 Unity logs
adb logcat -s Unity | findstr "server_send"

# 4. 拉取 CSV 並檢查
adb shell cp /storage/emulated/0/Android/data/com.samples.passthroughcamera/files/telemetry_*.csv /sdcard/
adb pull /sdcard/telemetry_*.csv .

# 5. 用 Excel 打開，檢查 server_send_ts 欄位（第 11 欄）
```

---

## 臨時解決方案（Unity 端）

如果暫時無法修改 server，可以在 Unity 端估算：

```csharp
// FrameTelemetryTracker.cs
if (response.server_send_ts == 0)
{
    // Estimate: server_send_ts ≈ server_process_start_ts + processing_time_ms
    trace.server_send_ts = trace.server_process_start_ts + (long)response.processing_time_ms;
}
else
{
    trace.server_send_ts = response.server_send_ts;
}
```

**注意**: 這只是估算，不如 server 直接返回準確。

---

**建立日期**: 2026-04-21
**狀態**: 待修復 (Server 端)
