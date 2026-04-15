# Dropped Frame vs Frozen Frame 完整解析

## TL;DR

| 類型 | 定義 | 原因 | 誰決定 |
|------|------|------|--------|
| **Dropped Frame** | 主動跳過 | **距離太近**（< 目標間隔） | Unity 主動限流 |
| **Frozen Frame** | 被動等待 | **上一幀還在處理** | Server 太慢 |

---

## 1. Dropped Frame（主動跳過）

### 定義

**「距離上次推理時間太短，主動跳過以控制 FPS」**

### 什麼時候發生？

Unity 以 **60 FPS** 運行（每幀 16.67ms），但推理只需要 **5 FPS**（每 200ms 一次）。

中間的幀就會被 **主動 drop**。

### 計算邏輯

```csharp
// PoseInferenceRunManager.cs, Line 118-132
float currentTime = Time.time;  // 當前時間
float targetInterval = 1.0f / m_inferenceConfig.targetFPS;  // 目標間隔 = 1.0 / 5 = 0.2s
float timeSinceLastInference = currentTime - m_lastInferenceTime;  // 距離上次推理的時間

// 檢查：距離上次推理太近了嗎？
if (timeSinceLastInference < targetInterval)
{
    // 太近了！跳過這一幀
    m_droppedFrames++;  // 累計計數

    if (m_sharedHUD != null)
    {
        m_sharedHUD.ReportDroppedFrame();  // 通知 HUD
    }

    yield break;  // 直接結束，不執行推理
}

// 通過檢查，執行推理...
```

### 實際例子

**設定**：
- Unity 幀率：60 FPS（每幀 16.67ms）
- 目標推理頻率：5 FPS（每 200ms 一次）

**時間線**：
```
Time = 0.000s  → Frame 1 執行推理
                 m_lastInferenceTime = 0.000s

Time = 0.016s  → Unity Frame 2 到達
                 timeSinceLastInference = 0.016s
                 targetInterval = 0.2s
                 0.016s < 0.2s？YES → DROP！
                 m_droppedFrames = 1

Time = 0.033s  → Unity Frame 3 到達
                 timeSinceLastInference = 0.033s
                 0.033s < 0.2s？YES → DROP！
                 m_droppedFrames = 2

...（中間省略）

Time = 0.208s  → Unity Frame 13 到達
                 timeSinceLastInference = 0.208s
                 0.208s < 0.2s？NO（0.208s > 0.2s）→ 執行推理！
                 m_lastInferenceTime = 0.208s

總計：Frame 2-12 被 drop（11 幀）
```

### 為什麼需要 Drop？

**目的**：**控制推理頻率，避免過載**

如果不 drop，Unity 60 FPS 會觸發 60 次推理/秒：
- ❌ GPU 過載（推理太頻繁）
- ❌ 網絡擁塞（每秒發 60 個請求）
- ❌ 電池耗盡（Quest 3 是電池供電）
- ❌ 發熱嚴重

通過 drop frame，控制在 5 FPS：
- ✅ GPU 負載合理
- ✅ 網絡流量可控
- ✅ 電池壽命延長
- ✅ 溫度穩定

### 影響

**視覺效果**：
- ❌ 畫面不流暢（骨架/框框每 200ms 更新一次）
- ✅ 但這是**預期的設計**，不是問題

**性能**：
- ✅ 節省資源
- ✅ 延長電池壽命

---

## 2. Frozen Frame（被動等待）

### 定義

**「上一幀推理還在進行中，被迫等待」**

### 什麼時候發生？

當 **Server 處理時間 > 目標間隔** 時，下一幀到達時上一幀還沒處理完。

### 計算邏輯

```csharp
// Line 135-143
if (m_inferenceInProgress)  // 檢查：上一幀還在處理嗎？
{
    // 上一幀還沒完成！被迫等待
    m_frozenFrames++;  // 累計計數

    if (m_sharedHUD != null)
    {
        m_sharedHUD.ReportFrozenFrame();  // 通知 HUD
    }

    yield break;  // 直接結束，不能開始新的推理
}

// 通過檢查（上一幀已完成），開始推理
m_inferenceInProgress = true;  // 設置標記：推理進行中
...
// 推理完成後
m_inferenceInProgress = false;  // 清除標記
```

### 實際例子

**設定**：
- 目標推理頻率：5 FPS（目標間隔 200ms）
- Server 處理時間：**400ms**（太慢！超過目標）

**時間線**：
```
Time = 0.000s  → Frame 1 開始推理
                 m_inferenceInProgress = true
                 m_lastInferenceTime = 0.000s

                 發送請求到 Server...
                 等待響應...（需要 400ms）

Time = 0.016s  → Unity Frame 2 到達
                 timeSinceLastInference = 0.016s < 0.2s
                 → DROP（距離太近）

...（Frame 2-12 都被 drop）

Time = 0.208s  → Unity Frame 13 到達
                 timeSinceLastInference = 0.208s > 0.2s ✅
                 但！m_inferenceInProgress = true ❌
                 → FREEZE！（Frame 1 還在處理）
                 m_frozenFrames = 1

Time = 0.224s  → Unity Frame 14 到達
                 timeSinceLastInference = 0.224s > 0.2s ✅
                 但！m_inferenceInProgress = true ❌
                 → FREEZE！
                 m_frozenFrames = 2

...（繼續等待）

Time = 0.400s  → Frame 1 響應收到！
                 解析 JSON，更新畫面
                 m_inferenceInProgress = false ✅

Time = 0.416s  → Unity Frame 25 到達
                 timeSinceLastInference = 0.416s > 0.2s ✅
                 m_inferenceInProgress = false ✅
                 → 執行推理！
                 m_lastInferenceTime = 0.416s

總計：
  - Frame 2-12: dropped（11 幀）
  - Frame 13-24: frozen（12 幀）
  - Frame 25: 執行推理
```

### 為什麼會 Freeze？

**原因**：**Server 處理太慢**

可能的原因：
1. **AI 模型太慢**（YOLO + Pose + Depth 推理時間長）
2. **GPU 繁忙**（其他程序在用 GPU）
3. **網絡延遲**（WiFi 不穩定）
4. **Server CPU 過載**（Python 程序太慢）
5. **首次推理**（模型載入需要時間）

### 影響

**視覺效果**：
- ❌ 畫面**凍結**（骨架/框框停在舊位置）
- ❌ 用戶體驗差（感覺卡頓）

**性能**：
- ❌ 實際推理頻率 < 目標頻率
- ❌ 延遲增加

**這是問題**！需要優化 Server。

---

## 3. 兩者的區別

### 對比表

| 項目 | Dropped Frame | Frozen Frame |
|------|--------------|--------------|
| **觸發條件** | 距離太近（< 目標間隔） | 上一幀還在處理 |
| **原因** | Unity 主動限流 | Server 太慢 |
| **是否預期** | ✅ 預期的設計 | ❌ 性能問題 |
| **影響** | 節省資源 | 體驗變差 |
| **解決方案** | 降低 targetFPS | 優化 Server |
| **檢查順序** | 第一個檢查 | 第二個檢查 |

### 檢查順序

Unity 會**依序檢查**：

```csharp
// 第一步：檢查是否太快（Dropped）
if (距離上次推理 < 目標間隔)
{
    DROP → yield break
}

// 第二步：檢查上一幀是否完成（Frozen）
if (m_inferenceInProgress)
{
    FREEZE → yield break
}

// 第三步：執行推理
m_inferenceInProgress = true
執行推理...
m_inferenceInProgress = false
```

### 實際例子：同時發生

**場景**：目標 5 FPS，但 Server 很慢（400ms）

```
Frame 1:
  檢查距離：0.000s（第一幀，通過）
  檢查進行中：false（通過）
  → 執行推理（400ms）

Frame 2-12（0.016s - 0.192s）:
  檢查距離：< 0.2s（失敗）
  → DROPPED（11 幀）

Frame 13（0.208s）:
  檢查距離：0.208s > 0.2s（通過）
  檢查進行中：true（失敗，Frame 1 還在處理）
  → FROZEN（1 幀）

Frame 14-24（0.224s - 0.400s）:
  檢查距離：> 0.2s（通過）
  檢查進行中：true（失敗）
  → FROZEN（11 幀）

Frame 25（0.416s）:
  檢查距離：0.416s > 0.2s（通過）
  檢查進行中：false（通過，Frame 1 完成了）
  → 執行推理
```

**結果**：
- `m_droppedFrames = 11`
- `m_frozenFrames = 12`

---

## 4. 什麼是「正常」的 Drop/Freeze？

### 正常情況

**Dropped Frame**：
```
目標 FPS = 5（間隔 200ms）
Unity FPS = 60（每幀 16.67ms）

每次推理之間應該 drop：
  200ms / 16.67ms ≈ 12 幀

實際會 drop 11 幀（12 - 1 = 11）
```

**Frozen Frame**：
```
Server 處理時間 < 目標間隔
→ freeze_frames = 0 ✅
```

### 異常情況

**Dropped Frame 太少**：
```
new_dropped < 5
→ 可能 targetFPS 設太高，或 Server 超級快
→ 檢查是否浪費資源
```

**Dropped Frame 太多**：
```
new_dropped > 20
→ 可能 targetFPS 設太低
→ 畫面更新太慢
```

**Frozen Frame > 0**：
```
new_frozen > 0
→ Server 處理太慢！
→ 需要優化
```

---

## 5. 如何從 Excel 數據判斷

### 看 new_dropped

**正常模式**（5 FPS）：
```
new_dropped ≈ 11-12（穩定）
→ 正常，符合 5 FPS 設計
```

**變化很大**：
```
new_dropped: 5 → 20 → 8 → 15 → 25
→ 不穩定，可能網絡或 Server 波動
```

### 看 new_frozen

**理想情況**：
```
new_frozen = 0（所有行）
→ Server 很快，沒有來不及的情況
```

**偶爾凍結**：
```
大部分 new_frozen = 0
少數 new_frozen = 5-10
→ Server 偶爾慢（可能首次推理、GC 等）
```

**持續凍結**：
```
new_frozen: 10 → 12 → 11 → 13（持續 > 0）
→ Server 一直很慢，需要優化！
```

### 看 freeze_ratio

**計算公式**：
```
freeze_ratio = freeze_frames / total_frames
```

**解讀**：
```
freeze_ratio < 0.1   → 優秀（< 10% 凍結）
freeze_ratio < 0.5   → 一般（< 50% 凍結）
freeze_ratio < 1.0   → 不佳（凍結很多）
freeze_ratio > 1.0   → 很差（平均每幀都凍結 > 1 次）
```

---

## 6. 優化建議

### 減少 Dropped Frame（如果需要更高更新率）

**方法**：提高 targetFPS

```csharp
// InferenceConfig
targetFPS = 10.0f;  // 從 5 改成 10

結果：
  目標間隔 = 100ms（原本 200ms）
  每次推理之間 drop：100ms / 16.67ms ≈ 6 幀
  new_dropped ≈ 5-6
```

**代價**：
- ⚠️ GPU 負載增加（推理次數翻倍）
- ⚠️ 網絡流量增加
- ⚠️ 電池消耗增加

### 減少 Frozen Frame（重要！）

**方法 1：優化 Server 推理速度**
```python
# 減少模型複雜度
- 使用更小的模型（YOLO11n → YOLOv8n）
- 降低圖像解析度
- 只運行必要的模型（關閉 depth）
```

**方法 2：減少網絡延遲**
```
- 使用有線連接（USB tethering）
- 改善 WiFi 信號
- Server 和 Quest 3 在同一網段
```

**方法 3：降低 targetFPS**
```csharp
targetFPS = 3.0f;  // 從 5 改成 3

結果：
  目標間隔 = 333ms（原本 200ms）
  Server 有更多時間處理
  freeze_frames 減少
```

**方法 4：減少圖像大小**
```csharp
// 降低 JPEG 質量
jpegQuality = 60;  // 從 80 改成 60

結果：
  Upload 時間減少
  文件更小
  總延遲降低
```

---

## 7. 實際數據分析範例

### 範例 1：正常情況

```
Row  frame_id  dropped  freeze  new_dropped  new_frozen  latency_ms  server_ms
1    1         0        0       0            0           100         45
2    13        11       0       11           0           95          42
3    25        23       0       12           0           98          44
4    37        35       0       12           0           102         46
```

**分析**：
- ✅ new_dropped 穩定在 11-12（符合 5 FPS）
- ✅ new_frozen = 0（沒有凍結）
- ✅ server_ms ≈ 45ms（遠小於目標間隔 200ms）
- **結論**：性能優秀，無需優化

### 範例 2：Server 太慢

```
Row  frame_id  dropped  freeze  new_dropped  new_frozen  latency_ms  server_ms
1    1         0        0       0            0           800         750
2    13        11       11      11           11          850         780
3    37        23       22      12           11          820         760
4    61        35       33      12           11          840         770
```

**分析**：
- ⚠️ new_frozen 持續 = 11（每次都凍結）
- ⚠️ server_ms ≈ 760ms（遠大於目標間隔 200ms）
- ❌ latency_ms ≈ 830ms（用戶體驗差）
- **結論**：Server 太慢，需要優化

**建議**：
1. 檢查 Server GPU 使用率
2. 減少模型數量（關閉不必要的模型）
3. 降低圖像大小
4. 或降低 targetFPS 到 2-3

### 範例 3：偶爾卡頓

```
Row  frame_id  dropped  freeze  new_dropped  new_frozen  latency_ms  server_ms
1    1         0        0       0            0           120         50
2    13        11       0       11           0           95          42
3    25        23       0       12           0           98          44
4    37        35       23      12           23          450         380
5    49        47       23      12           0           100         45
```

**分析**：
- ✅ 大部分 new_frozen = 0
- ❌ Row 4: new_frozen = 23（突然凍結）
- ⚠️ Row 4: server_ms = 380ms（突然變慢）
- **結論**：偶爾卡頓（可能 GC、其他程序干擾）

**建議**：
1. 檢查 Server 是否有其他程序搶占資源
2. 檢查網絡是否穩定
3. 如果只是偶爾發生，可以接受

---

## 8. 總結

### Drop vs Freeze

| 類型 | 定義 | 原因 | 影響 | 是否正常 |
|------|------|------|------|---------|
| **Dropped** | 主動跳過 | 距離太近 | 節省資源 | ✅ 正常 |
| **Frozen** | 被動等待 | Server 太慢 | 體驗變差 | ❌ 需要優化 |

### 計算方式

**Dropped Frame**：
```csharp
if (timeSinceLastInference < targetInterval)
{
    m_droppedFrames++;  // 累加
}
```

**Frozen Frame**：
```csharp
if (m_inferenceInProgress)
{
    m_frozenFrames++;  // 累加
}
```

### 理想狀態

```
new_dropped ≈ 11-12（穩定）
new_frozen = 0（完全沒有）
freeze_ratio = 0.0
```

### 需要優化

```
new_frozen > 0（持續）
freeze_ratio > 0.1
server_ms > targetInterval
```

---

## 相關文檔

- `DROP_FRAME_EXPLANATION.md` - Drop Frame 詳細說明
- `EXCEL_METRICS_EXPLANATION.md` - Excel 指標說明
- `NEW_DROP_METRICS_IMPLEMENTATION.md` - 增量指標實現
