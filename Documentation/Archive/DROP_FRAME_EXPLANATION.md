# Drop Frame 詳細說明

## TL;DR

**Drop Frame 是 Unity 主動跳過的幀，用於控制推理頻率（FPS）。**

- ✅ **只影響 Unity 端**：不會發送到 Server，不會顯示
- ✅ **保持舊的視覺化**：畫面保持上一幀的結果
- ✅ **兩種 Drop**：Dropped Frame（太快）和 Frozen Frame（太慢）

---

## Drop Frame 的兩種類型

### 1. Dropped Frame（主動限流）

**定義**：距離上次推理時間**太短**，主動跳過。

**目的**：控制推理頻率，避免 GPU/網絡過載。

**計算邏輯**：
```csharp
// PoseInferenceRunManager.cs, Line 118-132
float currentTime = Time.time;
float targetInterval = m_inferenceConfig.GetInferenceInterval();  // 1.0 / targetFPS
float timeSinceLastInference = currentTime - m_lastInferenceTime;

if (timeSinceLastInference < targetInterval)
{
    // 距離上次推理太近，跳過！
    m_droppedFrames++;
    if (m_sharedHUD != null)
    {
        m_sharedHUD.ReportDroppedFrame();
    }
    yield break;  // 直接結束，不做任何事
}
```

**範例**：
```
目標 FPS = 5
目標間隔 = 1.0 / 5 = 0.2 秒 = 200ms

Frame 1: Time = 0.000s → 執行推理
Frame 2: Time = 0.016s (16ms after Frame 1) → DROP（< 200ms）
Frame 3: Time = 0.033s (33ms after Frame 1) → DROP（< 200ms）
...
Frame 13: Time = 0.208s (208ms after Frame 1) → 執行推理（> 200ms）
```

**影響**：
- ❌ **不會發送到 Server**
- ❌ **不會更新視覺化**
- ✅ **畫面保持 Frame 1 的結果**

---

### 2. Frozen Frame（被動等待）

**定義**：上一幀推理**還在進行中**，被迫跳過。

**目的**：避免併發，確保每次只有一個推理在執行。

**計算邏輯**：
```csharp
// Line 135-143
if (m_inferenceInProgress)
{
    // 上一幀還在處理，無法開始新的推理
    m_frozenFrames++;
    if (m_sharedHUD != null)
    {
        m_sharedHUD.ReportFrozenFrame();
    }
    yield break;  // 直接結束，不做任何事
}
```

**範例**：
```
目標 FPS = 5（目標間隔 200ms）

Frame 1: Time = 0.000s → 開始推理（m_inferenceInProgress = true）
  → 發送到 Server
  → 等待響應...
  → Time = 0.150s，響應收到，處理完成（m_inferenceInProgress = false）

Frame 2: Time = 0.210s → 執行推理（距離 Frame 1 開始 > 200ms）

如果 Server 很慢：
Frame 1: Time = 0.000s → 開始推理
  → 發送到 Server
  → 等待響應...（還在等）
  → Time = 0.210s，還沒收到響應！

Frame 2: Time = 0.210s → FROZEN（m_inferenceInProgress = true）
Frame 3: Time = 0.420s → FROZEN（還在等 Frame 1）
Frame 4: Time = 0.500s → Frame 1 完成 → 執行推理
```

**影響**：
- ❌ **不會發送到 Server**
- ❌ **不會更新視覺化**
- ✅ **畫面保持上一幀的結果**（凍結）

---

## 完整的 Frame 處理流程

```
Unity 每幀（60 FPS）：
════════════════════════════════════════════════════════════════════

Frame 1 (Time=0.000s):
  ├─ Check: timeSinceLastInference < targetInterval? → NO（第一幀）
  ├─ Check: m_inferenceInProgress? → NO
  ├─ ✅ 執行推理
  │   ├─ m_inferenceInProgress = true
  │   ├─ 編碼 JPEG
  │   ├─ 發送到 Server
  │   ├─ 等待響應（yield return asyncOp）
  │   ├─ Time=0.150s 響應收到
  │   ├─ 解析 JSON
  │   ├─ 更新視覺化（骨架、框框）
  │   └─ m_inferenceInProgress = false
  └─ m_totalFrames++ (= 1)

Frame 2-12 (Time=0.016s - 0.192s):
  ├─ Check: timeSinceLastInference < targetInterval? → YES（< 200ms）
  ├─ ❌ DROPPED
  ├─ m_droppedFrames++ (= 11)
  └─ yield break（保持 Frame 1 的畫面）

Frame 13 (Time=0.208s):
  ├─ Check: timeSinceLastInference < targetInterval? → NO（> 200ms）
  ├─ Check: m_inferenceInProgress? → NO
  ├─ ✅ 執行推理
  ...
  └─ m_totalFrames++ (= 2)

如果 Server 突然很慢：
Frame 13 (Time=0.208s):
  ├─ ✅ 執行推理
  ├─ m_inferenceInProgress = true
  ├─ 發送到 Server
  └─ 等待響應...（很慢，還沒收到）

Frame 14-24 (Time=0.224s - 0.400s):
  ├─ Check: timeSinceLastInference < targetInterval? → NO（> 200ms）
  ├─ Check: m_inferenceInProgress? → YES（Frame 13 還在處理）
  ├─ ❌ FROZEN
  ├─ m_frozenFrames++ (= 11)
  └─ yield break（保持 Frame 13 的畫面，凍結）

Frame 25 (Time=0.416s):
  ├─ Frame 13 完成（m_inferenceInProgress = false）
  ├─ Check: timeSinceLastInference < targetInterval? → NO
  ├─ Check: m_inferenceInProgress? → NO
  ├─ ✅ 執行推理
  └─ m_totalFrames++ (= 3)
```

---

## Drop Frame 的影響

### 對 Unity 端

**視覺化**：
- ❌ **Drop 的幀不會更新畫面**
- ✅ **保持上一次成功推理的結果**
- 範例：
  ```
  Frame 1: 顯示骨架 A（人在左邊）
  Frame 2-12: DROPPED → 仍然顯示骨架 A
  Frame 13: 顯示骨架 B（人移動到右邊）
  ```

**效果**：
- 畫面會有「跳幀」感（不流暢）
- 骨架/框框會「瞬移」（從舊位置突然跳到新位置）

### 對 Server 端

**完全不影響！**
- ❌ **Drop 的幀不會發送到 Server**
- ✅ **Server 只會收到實際執行推理的幀**
- 範例：
  ```
  Unity 端：
    Frame 1 → 執行推理 → 發送到 Server
    Frame 2-12 → DROPPED → 不發送
    Frame 13 → 執行推理 → 發送到 Server

  Server 端：
    收到 Frame 1（frame_id = 1）
    收到 Frame 13（frame_id = 13）← frame_id 跳躍！
  ```

**Frame ID 會跳躍**：
- Unity 的 `m_frameId` 是**成功執行推理的幀數**
- 不是 Unity 的實際幀數（60 FPS）
- Excel 中的 `frame_id` 會不連續

---

## 統計數據

### Unity 端記錄

```csharp
// Line 60-61
private int m_droppedFrames = 0;  // 主動限流（太快）
private int m_frozenFrames = 0;   // 被動等待（太慢）
private int m_totalFrames = 0;    // 成功執行的推理幀數
```

**計算**：
```
實際 Unity 幀數 = 成功推理 + Dropped + Frozen + 其他跳過的幀
                = m_totalFrames + m_droppedFrames + m_frozenFrames + ...

成功率 = m_totalFrames / (m_totalFrames + m_droppedFrames + m_frozenFrames)
```

### 發送到 Server

```csharp
// Line 372-375
request.SetRequestHeader("X-Dropped-Frames", m_droppedFrames.ToString());
request.SetRequestHeader("X-Freeze-Frames", m_frozenFrames.ToString());
request.SetRequestHeader("X-Freeze-Ratio", freezeRatio.ToString("F4"));
```

**Freeze Ratio 計算**：
```csharp
// Line 371
float freezeRatio = m_totalFrames > 0 ? (float)m_frozenFrames / m_totalFrames : 0f;
```

**意義**：
- `freezeRatio = 0.0`：完全沒有凍結（Server 很快）
- `freezeRatio = 0.1`：10% 的成功幀之間有凍結
- `freezeRatio = 1.0`：凍結幀數 = 成功幀數（Server 很慢）

---

## Excel 記錄的數據

### 欄位說明

| 欄位 | 說明 | 範例值 |
|------|------|--------|
| `frame_id` | 成功推理的幀 ID（會跳躍） | 1, 13, 25, ... |
| `dropped_frames` | **累計**的 dropped frame 數 | 11 → 22 → ... |
| `freeze_frames` | **累計**的 frozen frame 數 | 0 → 11 → ... |
| `freeze_ratio` | 凍結比例 | 0.0 → 0.5 → ... |

**注意**：
- ✅ **累計值**：不是「這一幀 drop 了幾個」，而是「總共 drop 了幾個」
- ✅ **單調遞增**：只會增加，不會減少

### 計算每幀的 Drop 數

如果要計算「Frame N 到 Frame N+1 之間 drop 了幾個」：

```python
import openpyxl

wb = openpyxl.load_workbook("inference_log.xlsx")
ws = wb.active

for row in range(3, ws.max_row + 1):  # 從第三行開始（第二行是 Frame 1）
    frame_id_curr = ws.cell(row, 3).value  # frame_id
    frame_id_prev = ws.cell(row - 1, 3).value

    dropped_curr = ws.cell(row, 22).value  # dropped_frames
    dropped_prev = ws.cell(row - 1, 22).value

    frozen_curr = ws.cell(row, 23).value  # freeze_frames
    frozen_prev = ws.cell(row - 1, 23).value

    # 這一次新增的 drop
    new_dropped = dropped_curr - dropped_prev
    new_frozen = frozen_curr - frozen_prev

    # Frame ID 跳躍（期望是 +1）
    frame_gap = frame_id_curr - frame_id_prev

    print(f"Frame {frame_id_prev} → {frame_id_curr}: "
          f"+{new_dropped} dropped, +{new_frozen} frozen, gap={frame_gap}")
```

**範例輸出**：
```
Frame 1 → 13: +11 dropped, +0 frozen, gap=12
Frame 13 → 25: +0 dropped, +11 frozen, gap=12
Frame 25 → 37: +11 dropped, +0 frozen, gap=12
```

**解讀**：
- Frame 1 → 13：中間 11 幀被 drop（太快）
- Frame 13 → 25：中間 11 幀被 frozen（太慢，Frame 13 處理太久）
- Frame 25 → 37：中間 11 幀被 drop（恢復正常）

---

## 為什麼會有 Drop Frame？

### 1. 目標 FPS 設定（Dropped Frame）

**原因**：限制推理頻率，避免過載。

**設定位置**：
```csharp
// InferenceConfig
public float targetFPS = 5.0f;  // 每秒 5 次推理
```

**計算**：
```
目標間隔 = 1.0 / 5.0 = 0.2 秒 = 200ms

Unity 60 FPS：
  每幀間隔 = 1.0 / 60 ≈ 16.67ms

每次推理之間：
  成功推理 1 幀 + Drop 11 幀 ≈ 12 × 16.67ms = 200ms
```

**調整**：
```csharp
// 增加推理頻率（更新更快，但更耗資源）
targetFPS = 10.0f;  // 每秒 10 次，間隔 100ms

// 減少推理頻率（更新更慢，但更省資源）
targetFPS = 2.0f;   // 每秒 2 次，間隔 500ms
```

### 2. Server 處理太慢（Frozen Frame）

**原因**：推理時間 > 目標間隔。

**範例**：
```
目標間隔 = 200ms
實際推理時間 = 400ms

Frame 1 (0.000s): 開始推理
Frame 2-12 (0.016s-0.192s): DROPPED（距離太近）
Frame 13 (0.208s): 應該開始推理，但 Frame 1 還沒完成！
  → FROZEN
Frame 14-24 (0.224s-0.400s): FROZEN（等 Frame 1）
Frame 25 (0.416s): Frame 1 完成，開始推理
```

**結果**：
- 實際推理頻率 = 1000ms / 400ms = 2.5 FPS（低於目標 5 FPS）
- 畫面更新會延遲（凍結 400ms）

**解決方案**：
1. 優化 Server（降低推理時間）
2. 降低目標 FPS（增加間隔）
3. 減少圖像大小（加快編碼和傳輸）

---

## 對延遲記錄的影響

### Drop Frame 會丟失數據嗎？

**Dropped Frame**：
- ✅ **完全不影響**
- 因為 dropped frame 根本沒有執行推理，也沒發送到 Server

**Frozen Frame**：
- ✅ **完全不影響**
- Frozen frame 也沒有執行推理，沒發送到 Server

**結論**：
- Excel 只記錄「成功執行推理的幀」
- Drop 的幀不會有任何記錄

### Frame ID 跳躍

**範例**：
```
Excel:
Row 1: frame_id = 1
Row 2: frame_id = 13  ← 跳了 12 個！
Row 3: frame_id = 25  ← 又跳了 12 個！
```

**原因**：
- Frame 2-12 被 dropped（沒有發送到 Server）
- Frame 14-24 被 frozen（沒有發送到 Server）

**影響延遲記錄嗎？**
- ❌ **不影響**！
- 延遲記錄只關心「連續收到的兩個請求」
- Frame 1 → Frame 13：
  - Frame 13 到達時，記錄 Frame 1 的完整數據 ✅
  - 中間的 Frame 2-12 根本不存在（沒執行推理）

---

## 總結

### Drop Frame 是什麼？

**兩種 Drop**：
1. **Dropped Frame**：主動限流（距離上次推理太近）
2. **Frozen Frame**：被動等待（上一幀還在處理）

### 影響範圍

**Unity 端**：
- ❌ 不更新視覺化（保持舊畫面）
- ❌ 不發送到 Server
- ✅ 記錄統計數據（dropped_frames, freeze_frames）

**Server 端**：
- ✅ **完全不影響**
- 只會收到成功執行的幀
- Frame ID 會跳躍

**延遲記錄**：
- ✅ **完全不影響**
- 只記錄成功執行的幀
- Drop 的幀不存在，不需要處理

### 關鍵數字

```
目標 FPS = 5
Unity 實際幀率 = 60 FPS

理想情況（Server 很快）：
  每次推理：1 成功 + 11 dropped = 12 Unity 幀 = 200ms
  實際推理頻率 = 5 FPS ✅

Server 慢的情況（推理 400ms）：
  第一次：1 成功 + 11 dropped = 200ms（但推理還在進行）
  等待：11 frozen = 200ms（等推理完成）
  第二次：1 成功 = 16.67ms
  總計：1 成功 + 11 dropped + 11 frozen = 416.67ms
  實際推理頻率 = 2.4 FPS ❌（低於目標）
```

### 優化建議

**減少 Dropped Frame**：
- 降低 targetFPS（例如 5 → 3）
- 接受較低的更新頻率

**減少 Frozen Frame**：
- 優化 Server 推理速度
- 減少圖像大小
- 降低 JPEG 質量
- 使用更快的網絡

---

## 相關文檔

- `DELAYED_LOGGING_IMPLEMENTATION.md` - 延遲記錄實現
- `E2E_CALCULATION_DETAILED_ANALYSIS.md` - E2E 計算分析
