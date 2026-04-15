# latency_ms 計算方式

**日期**: 2026-04-15
**問題**: latency_ms 是根據哪兩個 timestamp 計算的？
**答案**: `unity_receive_ts - unity_send_ts`

---

## 計算公式

```
latency_ms = unity_receive_ts - unity_send_ts
```

**單位**: 毫秒（milliseconds）

---

## 詳細說明

### Unity 端計算（FrameTrace.cs）

**文件**: `Assets/PassthroughCameraApiSamples/Shared/Scripts/FrameTrace.cs`
**位置**: Line 77

```csharp
public void MarkCompleted(long receiveTime)
{
    unity_receive_ts = receiveTime;
    e2e_ms = unity_receive_ts - unity_send_ts;  // Already in ms
    state = FrameState.Completed;
}
```

### 數據流

```
Unity 發送請求:
  unity_send_ts = TimestampUtil.GetUnixTimestampMs()
  例如: 1713216836143 (2026-04-15 17:13:56.143)

Unity 接收回應:
  unity_receive_ts = TimestampUtil.GetUnixTimestampMs()
  例如: 1713216836543 (2026-04-15 17:13:56.543)

Unity 計算:
  e2e_ms = 1713216836543 - 1713216836143 = 400ms

Unity 發送到 Server (Delayed Telemetry):
  SetRequestHeader("X-E2E-Latency", "400")

Server 接收:
  client_e2e_ms = 400

Server 寫入 Excel:
  latency_ms = 400
```

---

## Excel 欄位對應

| Excel 欄位 | Unity 端名稱 | 計算方式 | 說明 |
|-----------|------------|---------|------|
| `latency_ms` | `e2e_ms` | `unity_receive_ts - unity_send_ts` | 端到端延遲（發送到接收） |
| `unity_send_ts` | `unity_send_ts` | `TimestampUtil.GetUnixTimestampMs()` | HTTP 請求發送時間 |
| `unity_receive_ts` | `unity_receive_ts` | `TimestampUtil.GetUnixTimestampMs()` | HTTP 回應接收時間 |

---

## 時間軸範例

### 正常流程（400ms 延遲）

```
T=0ms    Unity 發送請求
         unity_send_ts = 1713216836143
         ↓
         [網路上傳 50ms]
         ↓
T=50ms   Server 接收請求
         server_receive_ts = 1713216836193
         ↓
         [Server 處理 300ms]
         ↓
T=350ms  Server 發送回應
         server_send_ts = 1713216836493
         ↓
         [網路下載 50ms]
         ↓
T=400ms  Unity 接收回應
         unity_receive_ts = 1713216836543
         ↓
         計算: latency_ms = 543 - 143 = 400ms ✅
```

---

## 與其他時間指標的關係

### latency_ms 組成

```
latency_ms (E2E)
  = upload_ms + server_proc_ms + download_ms + parse_ms

例如:
  latency_ms = 400ms
  upload_ms = 50ms    (12.5%)
  server_proc_ms = 300ms (75.0%)
  download_ms = 40ms  (10.0%)
  parse_ms = 10ms     (2.5%)

驗證: 50 + 300 + 40 + 10 = 400ms ✅
```

### 百分比計算

```python
server_pct = (server_proc_ms / latency_ms) * 100
upload_pct = (upload_ms / latency_ms) * 100
download_pct = (download_ms / latency_ms) * 100

例如:
  server_pct = (300 / 400) * 100 = 75.0%
  upload_pct = (50 / 400) * 100 = 12.5%
  download_pct = (40 / 400) * 100 = 10.0%
```

---

## 常見問題

### Q1: latency_ms 包含 Unity 端的處理時間嗎？

**A**: 不包含。latency_ms 只測量從「發送 HTTP 請求」到「接收 HTTP 回應」的時間。Unity 端的其他處理（例如 JSON 解析、渲染）不包含在內。

### Q2: latency_ms 和 server_proc_ms 的區別？

**A**:
- **latency_ms**: 端到端延遲（包括網路上傳 + Server 處理 + 網路下載 + JSON 解析）
- **server_proc_ms**: 只有 Server 端的處理時間（YOLO + KeypointRCNN 推理）

### Q3: 為什麼 latency_ms 有時會比 server_proc_ms 小？

**A**: 這是不可能的！如果發生這種情況，表示有 Bug。正常情況下：
```
latency_ms >= server_proc_ms
```

如果出現 `latency_ms < server_proc_ms`，可能的原因：
- 時間戳不同步（Unity 時間 vs Server 時間）
- 計算錯誤
- 數據損壞

### Q4: latency_ms 的合理範圍？

**A**: 取決於網路和 Server 性能：

| 場景 | latency_ms 範圍 | 說明 |
|-----|----------------|------|
| **理想情況** | 100-200ms | 低延遲網路 + 快速 Server |
| **正常情況** | 200-500ms | WiFi + 中等負載 |
| **高負載** | 500-1000ms | Server 處理多個請求 |
| **異常** | > 1000ms | 網路問題或 Server 過載 |

---

## 驗證方法

### 檢查 latency_ms 是否合理

```python
import pandas as pd

df = pd.read_excel('inference_log.xlsx')

# 檢查基本統計
print(df['latency_ms'].describe())

# 檢查是否有異常值
abnormal = df[df['latency_ms'] > 1000]
print(f"異常高延遲的幀數: {len(abnormal)}")

# 檢查組成是否正確
df['calc_latency'] = df['upload_ms'] + df['server_proc_ms'] + df['download_ms'] + df['parse_ms']
df['latency_diff'] = abs(df['latency_ms'] - df['calc_latency'])

# 允許 ±10ms 的誤差（浮點數計算和時間戳精度）
inconsistent = df[df['latency_diff'] > 10]
print(f"latency_ms 計算不一致的幀數: {len(inconsistent)}")
```

---

## 總結

**latency_ms 計算公式**:
```
latency_ms = unity_receive_ts - unity_send_ts
```

**測量內容**:
- 從 Unity 發送 HTTP 請求
- 到 Unity 接收 HTTP 回應
- 包括：網路上傳 + Server 處理 + 網路下載 + JSON 解析

**不包含**:
- Unity 端的推理觸發邏輯
- Unity 端的結果顯示/渲染
- Unity 端的其他處理

**關鍵特性**:
- 單位：毫秒（ms）
- 類型：float
- 範圍：通常 100-1000ms
- 組成：upload_ms + server_proc_ms + download_ms + parse_ms

---

**文檔建立日期**: 2026-04-15
**相關文檔**: `TELEMETRY_CALCULATION_GUIDE.md`
