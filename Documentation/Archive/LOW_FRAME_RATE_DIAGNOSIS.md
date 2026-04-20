# Session ID Mismatch Bug Fix - 2026-04-16

**Status**: FIXED
**Issue**: Bounding boxes not displaying on Quest 3 despite successful inference
**Root Cause**: C# GUID byte order mismatch with Python UUID parsing

---

## Problem

Bounding boxes were not displaying on Quest 3 even though:
- Server was successfully processing frames
- YOLO was detecting persons
- Pose estimation was working
- Results were stored in cache

Unity was receiving 404 responses when polling for results:
```
INFO: 192.168.0.155:54340 - "GET /response/d515180b-8857-4989-8190-661e2acd7211/44 HTTP/1.1" 404 Not Found
```

But server was storing with a different session ID:
```
[UDP WORKER] Unity should poll: GET /response/0b1815d5-5788-8949-8190-661e2acd7211/51
[RESULT CACHE] Stored result for 0b1815d5_51, cache_size=52/1000
```

## Root Cause

**GUID Byte Order Mismatch** between C# and Python:

- **Unity (C#)** at `UDPTransport.cs:124`: Uses `Guid.ToByteArray()` which returns bytes in **Microsoft's mixed-endian format** (Windows GUID format)

- **Server (Python)** at `udp_ingest.py:161`: Was using `uuid.UUID(bytes=...)` which expects **RFC 4122 big-endian format**

This caused the first 3 fields of the GUID to be byte-swapped during parsing:

| Original GUID (Unity) | Parsed by Server (before fix) |
|----------------------|------------------------------|
| `0b1815d5-5788-8949-8190-661e2acd7211` | `d515180b-8857-4989-8190-661e2acd7211` |

Notice the last part `8190-661e2acd7211` is identical (these fields happen to match in both formats).

### Why This Happened

C#'s `System.Guid` uses Microsoft's GUID format where the first three fields are stored in little-endian:
- Data1 (4 bytes): little-endian
- Data2 (2 bytes): little-endian
- Data3 (2 bytes): little-endian
- Data4 (8 bytes): big-endian

But Python's `uuid.UUID(bytes=...)` expects standard RFC 4122 format where all fields are big-endian.

## The Fix

Changed `udp_ingest.py:163` from:
```python
session_id = str(uuid.UUID(bytes=session_bytes))
```

To:
```python
session_id = str(uuid.UUID(bytes_le=session_bytes))
```

The `bytes_le` parameter tells Python to interpret the first 3 fields as little-endian (Microsoft format), matching what C# sends.

## Files Modified

- **`C:\Repo\Github\vision_server\app\transport\udp_ingest.py`** (line 163)

## Testing Instructions

1. **Restart the server** (the fix requires server restart):
   ```bash
   cd C:\Repo\Github\vision_server
   python -m uvicorn app.main:app --host 0.0.0.0 --port 8001 --workers 1
   ```

2. **Test on Quest 3**:
   - Open MultiObjectDetection scene
   - Ensure "Use UDP Transport" is checked in Inspector
   - Build & Run to Quest 3

3. **Check server logs** - Session IDs should now match:
   ```
   [UDP WORKER] Unity should poll: GET /response/0b1815d5-5788-8949-8190-661e2acd7211/51
   INFO: 192.168.0.155:54340 - "GET /response/0b1815d5-5788-8949-8190-661e2acd7211/51 HTTP/1.1" 200 OK
   ```

4. **Verify bboxes appear** - You should now see bounding boxes on Quest 3 display

## Expected Behavior After Fix

- Session IDs match between UDP send and HTTP polling
- HTTP polling returns 200 OK (not 404)
- Bounding boxes display on Quest 3
- FPS should be ~5 FPS (100ms cadence, 200-300ms inference)
- No more session ID mismatches in logs

## Performance Notes

Some detected bboxes may still be filtered out due to strict height requirements:
- `PERSON_MIN_HEIGHT_PX = 120` (configured in `udp_inference_worker.py`)
- At 320x240 resolution, persons farther away may have bbox height < 120px
- Check server logs for bbox rejection reasons:
  ```
  [UDP WORKER BBOX] Rejecting: height too small (118px)
  ```

If you want to see more detections at lower resolutions, consider lowering the `PERSON_MIN_HEIGHT_PX` threshold.

---

**Status**: FIXED - 2026-04-16 23:20
**Tested**: Pending Quest 3 testing

---

# JSON Serialization Error Fix - 2026-04-16 23:30

## Problem

After fixing the session ID mismatch, a new error appeared when persons were detected:

```
ValueError: [TypeError("'numpy.float32' object is not iterable"), TypeError('vars() argument must have __dict__ attribute')]
INFO: 192.168.0.155:36624 - "GET /response/56b2fb70-bce7-4e12-88c7-bc3062d0ae11/7 HTTP/1.1" 500 Internal Server Error
```

**Pattern**:
- Frames with person detections → **500 Internal Server Error**
- Frames without person detections → **200 OK**

## Root Cause

In `inference_pose.py` line 503, the `detection_score` field was using YOLO's confidence value directly:

```python
"detection_score": detection.get("confidence", 1.0)
```

YOLO returns `numpy.float32` types, which FastAPI's `jsonable_encoder()` cannot serialize.

## The Fix

Changed line 503 to convert to native Python float:

```python
"detection_score": float(detection.get("confidence", 1.0))  # Convert numpy.float32 to Python float
```

## Files Modified

- **`C:\Repo\Github\vision_server\app\inference_pose.py`** (line 503)

## Testing

Server restarted with both fixes:
1. Session ID GUID byte-order fix (`uuid.UUID(bytes_le=...)`)
2. NumPy float serialization fix (`float(...)` conversion)

Unity should now receive **200 OK** responses with valid JSON for all frames.

---

**Status**: FIXED - 2026-04-16 23:30
**Both Fixes Applied**: Session ID + JSON Serialization

---
---

# Low Frame Rate Diagnosis (Previous Issue)

**日期**: 2026-04-15
**問題**: 預期每秒接收 10 或 5 個 frames，但實際上 1 分鐘遠少於 600 或 300 frames
**目標**: 診斷為什麼實際 frame rate 遠低於 target FPS

---

## 問題描述

### 預期行為
```
Target FPS = 10
→ 每秒應發送 10 frames
→ 1 分鐘 = 60 秒 × 10 = 600 frames

Target FPS = 5
→ 每秒應發送 5 frames
→ 1 分鐘 = 60 秒 × 5 = 300 frames
```

### 實際行為
```
實際接收的 frames << 預期 frames
例如：1 分鐘可能只有 50-100 frames（應該有 300-600）
```

---

## 可能的原因

### 1️⃣ Server 處理時間過長

**症狀**:
- `server_proc_ms` > 100ms（對於 10 FPS，應該 < 100ms）
- `latency_ms` > 500ms

**原因**:
```
Target FPS = 10 → Target Interval = 100ms

如果 Server 處理時間 = 500ms：
  T=0ms    發送 Frame 0
  T=500ms  接收 Frame 0 回應 ❌ (已經過了 5 個發送間隔！)

  期間應該發送的 frames：
  - T=100ms: Frame 1 (沒發，因為 Frame 0 還在處理)
  - T=200ms: Frame 2 (沒發)
  - T=300ms: Frame 3 (沒發)
  - T=400ms: Frame 4 (沒發)

  結果：5 個間隔只發送了 1 個 frame！
  實際 FPS = 10 / 5 = 2 FPS ❌
```

**檢查方法**:
```python
import pandas as pd

df = pd.read_excel('inference_log.xlsx')

# 檢查 Server 處理時間
print(df['server_proc_ms'].describe())

# 檢查是否有超過 target interval 的處理時間
target_fps = 10
target_interval = 1000 / target_fps  # 100ms for 10 FPS

slow_frames = df[df['server_proc_ms'] > target_interval]
print(f"慢於 target interval 的 frames: {len(slow_frames)} / {len(df)}")
print(f"百分比: {len(slow_frames) / len(df) * 100:.1f}%")
```

---

### 2️⃣ 並行處理架構問題

**問題**: 雖然 Unity 允許並行處理（多個請求同時 in-flight），但如果 Server 端沒有足夠的 workers，仍然會排隊處理。

**檢查 Server Workers**:
```bash
# 查看當前 uvicorn 配置
ps aux | grep uvicorn

# 應該看到：
# python -m uvicorn app.main:app --host 0.0.0.0 --port 8001 --workers 2
#                                                                      ↑
#                                                              只有 2 個 workers！
```

**問題分析**:
```
Unity 發送速度: 10 FPS (每 100ms 一個)
Server workers: 2
每個 worker 處理時間: 300ms

Timeline:
T=0ms    Worker 1 開始處理 Frame 0
T=100ms  Worker 2 開始處理 Frame 1
T=200ms  Frame 2 到達 → 等待 worker 可用 ⏳
T=300ms  Worker 1 完成 Frame 0 → 開始處理 Frame 2
T=400ms  Worker 2 完成 Frame 1 → 開始處理 Frame 3
T=500ms  Frame 4 到達 → 等待 worker 可用 ⏳
...

實際吞吐量 = 2 workers / 300ms = 6.67 frames/second
但 target = 10 FPS ❌

缺口：10 - 6.67 = 3.33 frames/second 被丟棄
```

---

### 3️⃣ RunInference() 觸發機制問題

**問題**: Segmentation 模式使用 `while (true)` 循環來觸發推理，但如果有任何阻塞操作，會影響觸發頻率。

**代碼位置**: `SegmentationInferenceRunManager.cs` Line 181-189
```csharp
while (true)
{
    // Add null check to prevent NullReferenceException
    while (m_uiMenuManager != null && m_uiMenuManager.IsPaused)
    {
        yield return null;  // 暫停時不發送
    }
    yield return RunInference();  // 每次循環調用一次
}
```

**可能的阻塞原因**:
1. **UI 暫停**: `m_uiMenuManager.IsPaused = true`
2. **RunInference() 內部阻塞**: 雖然應該是異步的，但可能有同步等待
3. **FPS Throttling**: Line 245 的 `yield break` 會跳過這次推理

---

### 4️⃣ FPS Throttling 過於嚴格

**代碼**: Line 245-254
```csharp
if (timeSinceLastInference < targetInterval)
{
    // Drop frame - respecting target FPS
    if (m_sharedHUD != null)
    {
        m_sharedHUD.ReportDroppedFrame();
    }
    yield break;  // 跳過這次推理
}
```

**問題**: 這個邏輯確保**最多**每 100ms 發送一次（對於 10 FPS），但如果 Server 處理慢，實際發送頻率會更低。

**示例**:
```
T=0ms     發送 Frame 0，設置 m_lastInferenceTime = 0
T=16ms    RunInference() 被調用
          → timeSinceLastInference = 16ms < 100ms
          → yield break (丟棄)
T=33ms    RunInference() 被調用
          → timeSinceLastInference = 33ms < 100ms
          → yield break (丟棄)
...
T=100ms   RunInference() 被調用
          → timeSinceLastInference = 100ms >= 100ms
          → 發送 Frame 1 ✅
          → 但此時 Frame 0 可能還沒返回！

如果 Frame 0 在 T=500ms 才返回：
  - Frame 1 at T=100ms ✅ (發送)
  - Frame 2 at T=200ms ✅ (發送)
  - Frame 3 at T=300ms ✅ (發送)
  - Frame 4 at T=400ms ✅ (發送)
  - Frame 5 at T=500ms ✅ (發送)

  → 5 個 frames 同時 in-flight
  → 如果 Server 只有 2 workers，Frame 3, 4, 5 會排隊等待
```

---

## 診斷步驟

### Step 1: 檢查實際發送的 Frame 數量

查看 Unity Console logs：
```
[SERVER SEND] >>> Sending frame 0 to: ...
[SERVER SEND] >>> Sending frame 1 to: ...
...
```

或者檢查 Excel 中的 `frame_id`：
```python
df = pd.read_excel('inference_log.xlsx')
print(f"總 frames: {len(df)}")
print(f"frame_id 範圍: {df['frame_id'].min()} - {df['frame_id'].max()}")

# 計算時間範圍
df['timestamp'] = pd.to_datetime(df['timestamp'])
duration = (df['timestamp'].max() - df['timestamp'].min()).total_seconds()
print(f"測試時長: {duration:.1f} 秒")
print(f"實際 FPS: {len(df) / duration:.2f}")
```

---

### Step 2: 檢查 Server 處理時間

```python
import pandas as pd
import matplotlib.pyplot as plt

df = pd.read_excel('inference_log.xlsx')

# 繪製 server_proc_ms 分布
plt.figure(figsize=(12, 4))
plt.subplot(1, 3, 1)
plt.hist(df['server_proc_ms'], bins=50)
plt.xlabel('Server Processing Time (ms)')
plt.ylabel('Frequency')
plt.title('Server Processing Time Distribution')
plt.axvline(x=100, color='r', linestyle='--', label='Target Interval (100ms for 10 FPS)')
plt.legend()

# 繪製 latency_ms 分布
plt.subplot(1, 3, 2)
plt.hist(df['latency_ms'], bins=50)
plt.xlabel('End-to-End Latency (ms)')
plt.ylabel('Frequency')
plt.title('E2E Latency Distribution')
plt.axvline(x=100, color='r', linestyle='--', label='Target Interval')
plt.legend()

# 繪製時間序列
plt.subplot(1, 3, 3)
plt.plot(df['frame_id'], df['server_proc_ms'], label='Server Proc', alpha=0.7)
plt.plot(df['frame_id'], df['latency_ms'], label='E2E Latency', alpha=0.7)
plt.axhline(y=100, color='r', linestyle='--', label='Target Interval')
plt.xlabel('Frame ID')
plt.ylabel('Time (ms)')
plt.title('Processing Time Over Time')
plt.legend()

plt.tight_layout()
plt.savefig('latency_analysis.png')
plt.show()

# 統計分析
print("Server Processing Time:")
print(df['server_proc_ms'].describe())
print(f"\n超過 100ms 的比例: {(df['server_proc_ms'] > 100).sum() / len(df) * 100:.1f}%")

print("\n\nE2E Latency:")
print(df['latency_ms'].describe())
print(f"\n超過 100ms 的比例: {(df['latency_ms'] > 100).sum() / len(df) * 100:.1f}%")
```

---

### Step 3: 檢查 Dropped Frames

Unity 端有 FPS throttling 會 drop frames，但這些不會出現在 Excel 中。

查看 Unity Console：
```
[FPS THROTTLE] Dropped frame (interval=16ms < target=100ms)
```

或者檢查 SharedInferenceHUD 的 UI 顯示。

---

### Step 4: 檢查 Server Workers 數量

```bash
# 查看運行中的 uvicorn 進程
ps aux | grep uvicorn

# 或者
netstat -ano | findstr :8001  # Windows
lsof -i :8001                  # Linux/Mac
```

---

## 解決方案

### Solution 1: 增加 Server Workers

如果 `server_proc_ms` 經常 > target interval，增加 workers：

```bash
# 從 2 workers 增加到 4 workers
python -m uvicorn app.main:app --host 0.0.0.0 --port 8001 --workers 4

# 或者 8 workers (如果 CPU 足夠)
python -m uvicorn app.main:app --host 0.0.0.0 --port 8001 --workers 8
```

**理論吞吐量**:
```
2 workers, 300ms 處理時間: 2/0.3 = 6.67 FPS
4 workers, 300ms 處理時間: 4/0.3 = 13.3 FPS ✅ (滿足 10 FPS)
8 workers, 300ms 處理時間: 8/0.3 = 26.7 FPS ✅ (滿足 10 FPS + buffer)
```

---

### Solution 2: 優化 Server 推理速度

如果單個 frame 處理太慢，優化推理：

1. **使用更快的模型**:
   - 從 YOLO-v8 大模型換成小模型
   - 降低圖像分辨率

2. **使用 GPU**:
   ```python
   # 確認 CUDA 可用
   import torch
   print(torch.cuda.is_available())

   # 將模型移到 GPU
   model = model.to('cuda')
   ```

3. **批處理**:
   如果 Server 支援，可以批量處理多個 frames（但會增加延遲）

---

### Solution 3: 降低 Target FPS

如果 Server 無法支援 10 FPS，降低目標：

```csharp
// 從 10 FPS 降到 5 FPS
[SerializeField] private InferenceConfig m_inferenceConfig = new InferenceConfig
{
    mode = InferenceMode.Segmentation,
    targetFPS = 5f,  // 改為 5
    jpegQuality = 80,
    includeMask = false,
    includeDepth = false
};
```

---

### Solution 4: 監控並行請求數量

添加 log 來追蹤同時 in-flight 的請求數量：

```csharp
// 在 RunServerInference() 開始時
Debug.Log($"[PARALLEL] Frame {m_frameId} starting. Pending requests: {m_pendingRequests.Count}");

// 在收到回應時
Debug.Log($"[PARALLEL] Frame {trace.frame_id} completed. Remaining pending: {m_pendingRequests.Count}");
```

**預期值**:
- 如果 `m_pendingRequests.Count` 經常 > 5，表示 Server 處理不過來
- 理想情況：`m_pendingRequests.Count` 應該 <= 2-3

---

## 快速診斷腳本

創建一個 Python 腳本來快速分析：

```python
import pandas as pd
import sys

def diagnose_frame_rate(excel_file):
    df = pd.read_excel(excel_file)

    # 基本統計
    print("=" * 60)
    print("FRAME RATE DIAGNOSIS")
    print("=" * 60)

    # 1. 總 frames 和時長
    df['timestamp'] = pd.to_datetime(df['timestamp'])
    duration = (df['timestamp'].max() - df['timestamp'].min()).total_seconds()
    total_frames = len(df)
    actual_fps = total_frames / duration if duration > 0 else 0

    print(f"\n總 Frames: {total_frames}")
    print(f"測試時長: {duration:.1f} 秒")
    print(f"實際 FPS: {actual_fps:.2f}")

    # 2. Target FPS (從第一個 frame 取得)
    target_fps = df['target_fps'].iloc[0] if 'target_fps' in df.columns else 10
    expected_frames = int(duration * target_fps)
    print(f"\nTarget FPS: {target_fps}")
    print(f"預期 Frames: {expected_frames}")
    print(f"缺少 Frames: {expected_frames - total_frames} ({(expected_frames - total_frames) / expected_frames * 100:.1f}%)")

    # 3. Server 處理時間分析
    target_interval = 1000 / target_fps
    slow_frames = df[df['server_proc_ms'] > target_interval]

    print(f"\n=== Server 處理時間 ===")
    print(f"平均: {df['server_proc_ms'].mean():.1f}ms")
    print(f"中位數: {df['server_proc_ms'].median():.1f}ms")
    print(f"最大: {df['server_proc_ms'].max():.1f}ms")
    print(f"超過 target interval ({target_interval:.0f}ms): {len(slow_frames)} frames ({len(slow_frames) / len(df) * 100:.1f}%)")

    # 4. E2E Latency 分析
    print(f"\n=== E2E Latency ===")
    print(f"平均: {df['latency_ms'].mean():.1f}ms")
    print(f"中位數: {df['latency_ms'].median():.1f}ms")
    print(f"最大: {df['latency_ms'].max():.1f}ms")

    # 5. 建議
    print(f"\n=== 建議 ===")
    if actual_fps < target_fps * 0.8:
        print("❌ 實際 FPS 遠低於目標！")
        if df['server_proc_ms'].median() > target_interval:
            print("   → Server 處理太慢，建議：")
            print("     1. 增加 Server workers")
            print("     2. 優化推理模型")
            print("     3. 降低 target FPS")
        else:
            print("   → Server 處理速度正常，可能是其他原因")
    else:
        print("✅ 實際 FPS 接近目標")

if __name__ == "__main__":
    if len(sys.argv) < 2:
        print("Usage: python diagnose_frame_rate.py <excel_file>")
        sys.exit(1)

    diagnose_frame_rate(sys.argv[1])
```

**使用方法**:
```bash
python diagnose_frame_rate.py inference_log_2026-04-15.xlsx
```

---

## 總結

### 檢查順序

1. ✅ **檢查實際 FPS** - Excel 中的 frame 數量 / 時長
2. ✅ **檢查 Server 處理時間** - `server_proc_ms` 是否 > target interval
3. ✅ **檢查 Server workers** - 是否足夠處理並行請求
4. ✅ **檢查 Dropped frames** - Unity Console logs
5. ✅ **檢查並行請求數量** - `m_pendingRequests.Count`

### 最可能的原因

根據你的描述（1 分鐘遠少於 300-600 frames），最可能的原因是：

1. **Server 處理太慢** (最常見)
2. **Server workers 不足** (2 workers 可能不夠)
3. **並行請求堆積** (請求排隊等待 worker 可用)

### 建議的第一步

**增加 Server workers 到 4-8 個**，然後重新測試：
```bash
python -m uvicorn app.main:app --host 0.0.0.0 --port 8001 --workers 8
```

然後運行診斷腳本查看是否改善。

---

## Bug 修復紀錄

### Bug 3: NumPy Float32 Serialization Error (2026-04-16 23:38) ✅ FIXED

**症狀**:
- 當 UDP worker 偵測到人時，FastAPI 返回 500 Internal Server Error
- Frames **有**偵測到人 → 500 Error
- Frames **沒有**偵測到人 → 200 OK

**錯誤訊息**:
```python
TypeError: 'numpy.float32' object is not iterable
ValueError: [TypeError("'numpy.float32' object is not iterable")]
```

**根本原因**:
`inference_pose.py` 第 503 行的 `detection.get("confidence", 1.0)` 回傳 YOLO 的 `numpy.float32` 類型，FastAPI 的 `jsonable_encoder()` 無法序列化。

**修復**:
```python
# Before (broken):
"detection_score": detection.get("confidence", 1.0)

# After (fixed):
"detection_score": float(detection.get("confidence", 1.0))  # Convert numpy.float32 to Python float
```

**檔案**: `C:\Repo\Github\vision_server\app\inference_pose.py` 第 503 行

**Python Cache 問題**:
修復後需要刪除所有 `__pycache__` 目錄並用 `-B` 旗標重啟 server：
```bash
# 1. 刪除所有 Python cache
cd C:\Repo\Github\vision_server
Get-ChildItem -Path . -Filter __pycache__ -Recurse -Directory | Remove-Item -Recurse -Force

# 2. 用 -B 旗標重啟（不寫入 .pyc 檔案）
python -B -m uvicorn app.main:app --host 0.0.0.0 --port 8001 --workers 1
```

**驗證**:
```bash
# 確認修復已套用
cd C:\Repo\Github\vision_server
grep "detection_score.*float.*detection\.get" app\inference_pose.py
# 應該看到: "detection_score": float(detection.get("confidence", 1.0))
```

**完整文檔**: 參見 `NUMPY_SERIALIZATION_FIX.md`
