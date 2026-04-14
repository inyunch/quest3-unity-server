# Excel 指標詳細說明

## 三個 Drop Frame 相關欄位

Excel 中有三個欄位記錄 Frame Drop 的情況：

| 欄位名稱 | 類型 | 說明 |
|---------|------|------|
| `dropped_frames` | 整數（累計） | 主動跳過的幀數（距離太近） |
| `freeze_frames` | 整數（累計） | 被動等待的幀數（上一幀還在處理） |
| `freeze_ratio` | 浮點數（比例） | 凍結比例 = freeze_frames / total_frames |

---

## 1. dropped_frames（累計值）

### 定義

**主動跳過的幀數**：距離上次推理時間太短，主動跳過。

### Unity 計算邏輯

```csharp
// PoseInferenceRunManager.cs, Line 56
private int m_droppedFrames = 0;  // 初始化

// Line 118-132
float timeSinceLastInference = currentTime - m_lastInferenceTime;
float targetInterval = m_inferenceConfig.GetInferenceInterval();  // 1.0 / targetFPS

if (timeSinceLastInference < targetInterval)
{
    m_droppedFrames++;  // 累加
    yield break;
}
```

### 發送到 Server

```csharp
// Line 373
request.SetRequestHeader("X-Dropped-Frames", m_droppedFrames.ToString());
```

### 意義

**累計值**：從場景啟動到當前幀，總共跳過了多少幀。

**範例**：
```
Frame 1 執行：m_droppedFrames = 0   → 發送 X-Dropped-Frames: 0
Frame 2-12 跳過：m_droppedFrames = 0+1+1+...+1 = 11
Frame 13 執行：m_droppedFrames = 11  → 發送 X-Dropped-Frames: 11
Frame 14-25 跳過：m_droppedFrames = 11+1+1+...+1 = 23
Frame 26 執行：m_droppedFrames = 23  → 發送 X-Dropped-Frames: 23
```

### Excel 記錄

| Row | frame_id | dropped_frames | 說明 |
|-----|----------|---------------|------|
| 1 | 1 | 0 | 第一幀，還沒有 drop |
| 2 | 13 | 11 | Frame 2-12 被 drop（11 幀） |
| 3 | 25 | 23 | Frame 14-24 被 drop（又 12 幀，但 Frame 13 成功所以只有 11 幀）|

**計算每次新增的 drop**：
```python
new_dropped = dropped_frames[row] - dropped_frames[row-1]
# Row 2: 11 - 0 = 11（Frame 1 → 13 之間 drop 了 11 幀）
# Row 3: 23 - 11 = 12（Frame 13 → 25 之間 drop 了 12 幀）
```

---

## 2. freeze_frames（累計值）

### 定義

**被動等待的幀數**：上一幀推理還在進行中，被迫跳過。

### Unity 計算邏輯

```csharp
// Line 57
private int m_frozenFrames = 0;  // 初始化

// Line 135-143
if (m_inferenceInProgress)
{
    m_frozenFrames++;  // 累加
    yield break;
}
```

### 發送到 Server

```csharp
// Line 374
request.SetRequestHeader("X-Freeze-Frames", m_frozenFrames.ToString());
```

### 意義

**累計值**：從場景啟動到當前幀，總共凍結了多少幀。

**範例**：
```
Frame 1 執行（處理時間 400ms）：
  0.000s 開始，m_inferenceInProgress = true
  0.016s Frame 2 到達 → 檢查間隔（16ms < 200ms）→ DROP（不是 freeze）
  ...
  0.208s Frame 13 到達 → 檢查間隔（208ms > 200ms）✅
         → 檢查 m_inferenceInProgress（還是 true）❌
         → FREEZE，m_frozenFrames = 1
  0.224s Frame 14 到達 → FREEZE，m_frozenFrames = 2
  ...
  0.400s Frame 1 完成，m_inferenceInProgress = false
  0.416s Frame 25 到達 → 執行，發送 X-Freeze-Frames: 12
```

### Excel 記錄

| Row | frame_id | freeze_frames | 說明 |
|-----|----------|--------------|------|
| 1 | 1 | 0 | 第一幀，還沒有 freeze |
| 2 | 13 | 0 | Frame 1 處理很快，沒有 freeze |
| 3 | 25 | 12 | Frame 13-24 被 freeze（Frame 1 處理太慢）|

**計算每次新增的 freeze**：
```python
new_frozen = freeze_frames[row] - freeze_frames[row-1]
# Row 2: 0 - 0 = 0（Frame 1 → 13 之間沒有 freeze）
# Row 3: 12 - 0 = 12（Frame 13 → 25 之間 freeze 了 12 幀）
```

---

## 3. freeze_ratio（比例）

### 定義

**凍結比例**：freeze_frames / total_frames

### Unity 計算邏輯

```csharp
// Line 168
private int m_totalFrames = 0;  // 成功執行的推理幀數

// 每次成功執行推理後
m_totalFrames++;

// Line 371
float freezeRatio = m_totalFrames > 0 ? (float)m_frozenFrames / m_totalFrames : 0f;
```

### 發送到 Server

```csharp
// Line 375
request.SetRequestHeader("X-Freeze-Ratio", freezeRatio.ToString("F4"));
```

### 意義

**凍結比例**：平均每個成功幀之間，有多少幀被凍結。

**公式**：
```
freeze_ratio = freeze_frames / total_frames
```

**解讀**：
- `0.0`：完全沒有凍結（Server 很快）
- `0.5`：平均每個成功幀之間，凍結了 0.5 幀
- `1.0`：平均每個成功幀之間，凍結了 1 幀
- `2.0`：平均每個成功幀之間，凍結了 2 幀（Server 很慢）

### 範例計算

**場景 1：Server 很快**
```
Frame 1 執行：m_totalFrames = 1, m_frozenFrames = 0
  → freeze_ratio = 0 / 1 = 0.0

Frame 13 執行：m_totalFrames = 2, m_frozenFrames = 0
  → freeze_ratio = 0 / 2 = 0.0

完全沒有凍結 ✅
```

**場景 2：Server 偶爾慢**
```
Frame 1 執行：m_totalFrames = 1, m_frozenFrames = 0
  → freeze_ratio = 0 / 1 = 0.0

Frame 13 執行（Frame 1 處理慢，Frame 2-12 freeze）：
  m_totalFrames = 2, m_frozenFrames = 11
  → freeze_ratio = 11 / 2 = 5.5

Frame 25 執行：m_totalFrames = 3, m_frozenFrames = 11
  → freeze_ratio = 11 / 3 = 3.67

有凍結，但隨著成功幀增加，比例下降
```

**場景 3：Server 一直很慢**
```
每次推理都需要 400ms（超過目標間隔 200ms）

Frame 1 執行：m_totalFrames = 1, m_frozenFrames = 0
Frame 13 執行：m_totalFrames = 2, m_frozenFrames = 11
Frame 25 執行：m_totalFrames = 3, m_frozenFrames = 22
Frame 37 執行：m_totalFrames = 4, m_frozenFrames = 33

freeze_ratio 穩定在：
  (Frame N 的 freeze) / (Frame N 的 total)
  = 11 / 1 = 11.0（平均每個成功幀凍結 11 幀）
```

### Excel 記錄

| Row | frame_id | total_frames | freeze_frames | freeze_ratio | 說明 |
|-----|----------|-------------|--------------|--------------|------|
| 1 | 1 | 1 | 0 | 0.0000 | 沒有凍結 |
| 2 | 13 | 2 | 11 | 5.5000 | 11 / 2 |
| 3 | 25 | 3 | 11 | 3.6667 | 11 / 3（沒有新增 freeze）|
| 4 | 37 | 4 | 22 | 5.5000 | 22 / 4（又新增 11 freeze）|

**注意**：Excel 中沒有 `total_frames` 欄位，但可以從 `frame_id` 或 Row 推算。

---

## 實際範例分析

### 從實際數據看

假設我們有以下 Excel 數據：

| Row | frame_id | dropped_frames | freeze_frames | freeze_ratio |
|-----|----------|---------------|--------------|--------------|
| 1 | 1 | 0 | 0 | 0.0000 |
| 2 | 13 | 11 | 0 | 0.0000 |
| 3 | 25 | 23 | 0 | 0.0000 |
| 4 | 37 | 35 | 0 | 0.0000 |

**分析**：
- **dropped_frames 持續增加**：11 → 23 → 35（每次 +12）
- **freeze_frames 始終為 0**：沒有凍結
- **freeze_ratio 始終為 0**：Server 很快，沒有來不及的情況

**結論**：
- 目標 FPS = 5（間隔 200ms）
- Unity 以 60 FPS 運行（每幀 16.67ms）
- 每次推理之間跳過 12 幀 = 200ms ✅
- Server 處理很快，沒有凍結 ✅

---

### 另一個範例（有 freeze）

| Row | frame_id | dropped_frames | freeze_frames | freeze_ratio |
|-----|----------|---------------|--------------|--------------|
| 1 | 1 | 0 | 0 | 0.0000 |
| 2 | 13 | 11 | 11 | 5.5000 |
| 3 | 37 | 23 | 22 | 7.3333 |
| 4 | 61 | 35 | 33 | 8.2500 |

**分析**：

**Row 1 → Row 2（Frame 1 → 13）**：
```
dropped 增加：11 - 0 = 11
freeze 增加：11 - 0 = 11
總跳過：11 + 11 = 22 幀

Frame ID 跳躍：13 - 1 = 12
Unity 幀數：12 × 16.67ms = 200ms

freeze_ratio = 11 / 2 = 5.5
→ Frame 1 處理太慢，導致 Frame 2-12 凍結
```

**Row 2 → Row 3（Frame 13 → 37）**：
```
dropped 增加：23 - 11 = 12
freeze 增加：22 - 11 = 11
總跳過：12 + 11 = 23 幀

Frame ID 跳躍：37 - 13 = 24
Unity 幀數：24 × 16.67ms = 400ms

→ Frame 13 也處理太慢
```

**結論**：
- Server 處理時間超過目標間隔（200ms）
- 每次推理都導致凍結
- freeze_ratio 持續上升（越來越慢）

---

## 是否正確儲存？

### 檢查點 1：Unity 發送

**Unity 端代碼**（Line 373-375）：
```csharp
request.SetRequestHeader("X-Dropped-Frames", m_droppedFrames.ToString());
request.SetRequestHeader("X-Freeze-Frames", m_frozenFrames.ToString());
request.SetRequestHeader("X-Freeze-Ratio", freezeRatio.ToString("F4"));
```

✅ **正確發送**：三個值都通過 HTTP headers 發送

### 檢查點 2：Server 接收

**Server 端代碼**（app/routes/infer_human.py, Line 787-790）：
```python
target_fps = float(request.headers.get("X-Target-FPS", "5.0"))
dropped_frames = int(request.headers.get("X-Dropped-Frames", "0"))
freeze_frames = int(request.headers.get("X-Freeze-Frames", "0"))
freeze_ratio = float(request.headers.get("X-Freeze-Ratio", "0.0"))
```

✅ **正確接收**：從 headers 讀取並轉換類型

### 檢查點 3：傳遞到 Frame Manager

**Server 端代碼**（Line 833-835）：
```python
complete_frame_data = frame_manager.process_frame(
    ...
    target_fps=target_fps,
    dropped_frames=dropped_frames,
    freeze_frames=freeze_frames,
    freeze_ratio=freeze_ratio
)
```

✅ **正確傳遞**：傳給 frame state manager

### 檢查點 4：延遲記錄

**Frame State Manager**（debug/frame_state_manager.py）：
```python
complete_frame_data = {
    ...
    'target_fps': current_frame.target_fps,
    'dropped_frames': current_frame.dropped_frames,
    'freeze_frames': current_frame.freeze_frames,
    'freeze_ratio': current_frame.freeze_ratio
}
```

⚠️ **問題**：這裡記錄的是 **Frame N-1** 的 dropped/freeze 數據嗎？

讓我檢查邏輯...

**實際邏輯**：
```python
# Frame N 到達時
current_frame = FrameState(
    target_fps=target_fps,        # Frame N-1 的數據（從 headers）
    dropped_frames=dropped_frames, # Frame N-1 的數據（從 headers）
    freeze_frames=freeze_frames,   # Frame N-1 的數據（從 headers）
    freeze_ratio=freeze_ratio      # Frame N-1 的數據（從 headers）
)

# 返回 Frame N-1 的完整數據
complete_frame_data = {
    'target_fps': current_frame.target_fps,      # Frame N-1
    'dropped_frames': current_frame.dropped_frames, # Frame N-1
    'freeze_frames': current_frame.freeze_frames,   # Frame N-1
    'freeze_ratio': current_frame.freeze_ratio      # Frame N-1
}
```

✅ **正確**：記錄的是 Frame N-1 的數據（與其他時間一致）

### 檢查點 5：Excel 寫入

**Inference Logger**（debug/inference_logger.py, Line 22）：
```python
COLUMNS = [
    ...,
    "target_fps", "dropped_frames", "freeze_frames", "freeze_ratio"
]
```

✅ **正確定義**：欄位有定義

**寫入邏輯**（Line 106-149）：
```python
def log_inference(
    ...
    target_fps=5.0,
    dropped_frames=0,
    freeze_frames=0,
    freeze_ratio=0.0
):
    # 寫入到 Excel
    row_data = [
        timestamp, scene, frame_id, latency_ms,
        server_proc_ms, upload_ms, download_ms, parse_ms,
        upload_bytes_uncompressed, upload_bytes_compressed,
        download_bytes_uncompressed, download_bytes_compressed,
        server_pct, upload_pct, download_pct,
        detection_count, avg_confidence, keypoint_avg_conf,
        image_width, image_height, model_used,
        target_fps, dropped_frames, freeze_frames, freeze_ratio
    ]

    for col, value in enumerate(row_data, 1):
        ws.cell(row=next_row, column=col, value=value)
```

✅ **正確寫入**：按照 COLUMNS 順序寫入

---

## 結論

### 是否正確儲存？

✅ **是的，完全正確！**

**數據流**：
1. Unity 計算 `m_droppedFrames`、`m_frozenFrames`
2. Unity 計算 `freezeRatio = m_frozenFrames / m_totalFrames`
3. Unity 通過 HTTP headers 發送（Frame N 發送 Frame N-1 的數據）
4. Server 接收並傳遞給 Frame Manager
5. Frame Manager 記錄 Frame N-1 的完整數據
6. Excel 記錄到對應的欄位

### 數值意義

**dropped_frames**：
- 累計值（單調遞增）
- 主動跳過的幀數（距離太近）

**freeze_frames**：
- 累計值（單調遞增）
- 被動等待的幀數（上一幀還在處理）

**freeze_ratio**：
- 比例值（可能上升或下降）
- `freeze_frames / total_frames`
- 表示平均每個成功幀之間凍結了多少幀

### 使用建議

**分析 dropped_frames**：
```python
# 計算每次新增的 drop
for row in range(3, ws.max_row + 1):
    new_dropped = ws.cell(row, 22).value - ws.cell(row - 1, 22).value
    if new_dropped > 15:
        print(f"Row {row}: 大量 drop（{new_dropped} 幀）")
```

**分析 freeze_frames**：
```python
# 計算每次新增的 freeze
for row in range(3, ws.max_row + 1):
    new_frozen = ws.cell(row, 23).value - ws.cell(row - 1, 23).value
    if new_frozen > 0:
        print(f"Row {row}: Server 太慢，凍結了 {new_frozen} 幀")
```

**分析 freeze_ratio**：
```python
# 查看凍結比例趨勢
freeze_ratios = [ws.cell(row, 24).value for row in range(2, ws.max_row + 1)]
avg_freeze_ratio = sum(freeze_ratios) / len(freeze_ratios)

if avg_freeze_ratio < 0.1:
    print("Server 性能良好")
elif avg_freeze_ratio < 1.0:
    print("Server 性能一般")
else:
    print("Server 性能不佳，需要優化")
```

---

## 相關文檔

- `DROP_FRAME_EXPLANATION.md` - Drop Frame 完整說明
- `DELAYED_LOGGING_IMPLEMENTATION.md` - 延遲記錄實現
