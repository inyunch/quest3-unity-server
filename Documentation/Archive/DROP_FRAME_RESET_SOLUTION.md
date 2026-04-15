# Drop Frame 重置方案

## 問題

當前的 `dropped_frames` 和 `freeze_frames` 是**累計值**，從場景啟動開始一直累加。

### 現象

```
PoseEstimation 場景運行很久：
Row 557: frame=1,   dropped=0
Row 600: frame=50,  dropped=25
Row 700: frame=150, dropped=80
Row 780: frame=288, dropped=161   ← 累計到很高！
```

如果場景運行時間很長（例如幾小時測試），數字會變得很大（例如 1174）。

---

## 當前行為

### Unity 端

**變數宣告**：
```csharp
// Line 60-61
private int m_droppedFrames = 0;  // 只在宣告時初始化一次
private int m_frozenFrames = 0;
```

**累加邏輯**：
```csharp
// 每次 drop 時
m_droppedFrames++;  // 一直累加，沒有重置

// 每次 freeze 時
m_frozenFrames++;   // 一直累加，沒有重置
```

**重置時機**：
- ✅ **場景切換時重置**（Unity 重新載入 script）
- ❌ **場景內不會重置**（持續累計）

### Excel 記錄

**範例數據**：
```
同一個 PoseEstimation session：
Frame 1   → dropped=0
Frame 50  → dropped=25   (+25)
Frame 100 → dropped=60   (+35)
Frame 200 → dropped=135  (+75)
Frame 300 → dropped=220  (+85)
```

**問題**：
- 數字越來越大，不直觀
- 無法看出「最近的 drop 情況」

---

## 解決方案選項

### 選項 1：保持累計值（當前）

**優點**：
- ✅ 簡單，不需要修改
- ✅ 可以看到總體統計

**缺點**：
- ❌ 數字會很大
- ❌ 需要計算差值才能看出趨勢

**適用場景**：
- 短時間測試（< 100 幀）
- 需要總體統計

**使用方式**：
```python
# 計算每段的新增 drop
new_dropped = dropped[row] - dropped[row-1]
```

---

### 選項 2：定期重置（推薦）

**實現**：每 N 幀重置一次累計值。

**Unity 修改**：
```csharp
// 在 RunServerInference 結束時（Line 595 附近）
m_totalFrames++;

// 每 100 幀重置一次統計
if (m_totalFrames % 100 == 0)
{
    Debug.Log($"[STATS RESET] Resetting stats at frame {m_totalFrames}");
    m_droppedFrames = 0;
    m_frozenFrames = 0;
}
```

**Excel 結果**：
```
Frame 1   → dropped=0
Frame 50  → dropped=25
Frame 100 → dropped=60
Frame 101 → dropped=0   ← 重置
Frame 150 → dropped=30
Frame 200 → dropped=65
Frame 201 → dropped=0   ← 重置
```

**優點**：
- ✅ 數字保持在合理範圍（0-100）
- ✅ 可以看出每段的 drop 情況
- ✅ 容易分析趨勢

**缺點**：
- ⚠️ 無法看到總體累計（需要手動加總）

---

### 選項 3：移動視窗統計（最佳）

**實現**：記錄「最近 N 幀的 drop 數」。

**Unity 修改**：
```csharp
// 新增變數
private Queue<bool> m_recentDrops = new Queue<bool>();  // 最近 100 幀的 drop 記錄
private const int STATS_WINDOW_SIZE = 100;

// Drop frame 時
if (timeSinceLastInference < targetInterval)
{
    m_droppedFrames++;  // 保留累計值
    m_recentDrops.Enqueue(true);  // 記錄這一幀 drop 了

    // 維持視窗大小
    if (m_recentDrops.Count > STATS_WINDOW_SIZE)
    {
        m_recentDrops.Dequeue();
    }

    yield break;
}

// 成功執行推理時
m_recentDrops.Enqueue(false);  // 記錄這一幀沒有 drop
if (m_recentDrops.Count > STATS_WINDOW_SIZE)
{
    m_recentDrops.Dequeue();
}

// 計算最近 N 幀的 drop 率
int recentDropCount = m_recentDrops.Count(d => d);
float recentDropRate = m_recentDrops.Count > 0 ? (float)recentDropCount / m_recentDrops.Count : 0f;

// 發送兩個值
request.SetRequestHeader("X-Dropped-Frames", m_droppedFrames.ToString());  // 累計
request.SetRequestHeader("X-Recent-Drop-Count", recentDropCount.ToString());  // 最近 N 幀
request.SetRequestHeader("X-Recent-Drop-Rate", recentDropRate.ToString("F4"));  // Drop 率
```

**Excel 欄位**：
- `dropped_frames`：累計值（總覽）
- `recent_drop_count`：最近 100 幀的 drop 數（即時）
- `recent_drop_rate`：最近 100 幀的 drop 率（0.0-1.0）

**優點**：
- ✅ 保留累計值（總體統計）
- ✅ 提供即時統計（最近趨勢）
- ✅ 數字直觀（recent_drop_count 在 0-100 之間）

**缺點**：
- ⚠️ 需要額外記憶體（Queue<bool>，100 個元素）
- ⚠️ 稍微複雜

---

### 選項 4：添加手動重置按鈕

**實現**：在 UI 添加重置按鈕。

**Unity 修改**：
```csharp
// 公開方法
public void ResetStats()
{
    m_droppedFrames = 0;
    m_frozenFrames = 0;
    m_totalFrames = 0;
    Debug.Log("[STATS] Statistics reset");
}

// UI Button 調用
// 在 Inspector 中綁定按鈕到此方法
```

**優點**：
- ✅ 用戶可以手動重置
- ✅ 靈活（想重置時才重置）

**缺點**：
- ⚠️ 需要手動操作
- ⚠️ 不自動

---

## 推薦方案

### 短期方案：選項 2（定期重置）

**簡單修改**：
```csharp
// 在 RunServerInference 結束後（Line 595 附近）
m_totalFrames++;

// 每 100 幀重置統計（可調整）
const int RESET_INTERVAL = 100;
if (m_totalFrames % RESET_INTERVAL == 0)
{
    Debug.Log($"[STATS] Reset stats at frame {m_totalFrames}: dropped={m_droppedFrames}, frozen={m_frozenFrames}");
    m_droppedFrames = 0;
    m_frozenFrames = 0;
}
```

**修改文件**：
- `PoseInferenceRunManager.cs`
- `SentisInferenceRunManager.cs`
- `SegmentationInferenceRunManager.cs`

**好處**：
- 簡單（3 行代碼）
- 數字保持在合理範圍
- Excel 容易分析

---

### 長期方案：選項 3（移動視窗）

如果需要更詳細的統計，可以實現移動視窗。

**額外欄位**：
- `recent_drop_count`：最近 100 幀的 drop 數
- `recent_drop_rate`：Drop 率（0.0-1.0）
- `recent_freeze_count`：最近 100 幀的 freeze 數
- `recent_freeze_rate`：Freeze 率

**Excel 分析**：
```python
# 看總體情況
total_dropped = df['dropped_frames'].max()

# 看即時情況
recent_avg = df['recent_drop_rate'].mean()

# 找出問題段落
problem_frames = df[df['recent_drop_rate'] > 0.5]
```

---

## 當前狀態

### 場景切換時會重置

✅ **已驗證**：切換場景時，Unity 重新載入 script，變數自動重置。

**證據**：
```
Row 557: PoseEstimation, frame=1, dropped=0  ← 場景開始
Row 782: Segmentation, frame=15, dropped=0   ← 切換場景，重置
```

### 同一場景內會累計

✅ **已驗證**：在同一個場景內，dropped_frames 持續累計。

**證據**：
```
Row 557: frame=1,   dropped=0
Row 782: frame=325, dropped=161  ← 累計了 161
```

**如果運行幾小時**：
```
Frame 1000: dropped=500
Frame 2000: dropped=1100
Frame 2500: dropped=1174  ← 你看到的值
```

---

## 建議行動

### 立即可做（不修改代碼）

**Excel 分析時計算差值**：
```python
df['new_dropped'] = df['dropped_frames'].diff().fillna(0)
df['new_frozen'] = df['freeze_frames'].diff().fillna(0)

# 只看每段新增的 drop
print(df[['frame_id', 'dropped_frames', 'new_dropped']])
```

**Server 端記錄差值**（修改 frame_state_manager.py）：
```python
# 計算新增的 drop
if previous_frame:
    new_dropped = current_frame.dropped_frames - previous_frame.dropped_frames
    new_frozen = current_frame.freeze_frames - previous_frame.freeze_frames
else:
    new_dropped = 0
    new_frozen = 0

complete_frame_data = {
    ...
    'dropped_frames': current_frame.dropped_frames,  # 累計值
    'freeze_frames': current_frame.freeze_frames,    # 累計值
    'new_dropped': new_dropped,  # 新增欄位：這一幀新增的 drop
    'new_frozen': new_frozen     # 新增欄位：這一幀新增的 freeze
}
```

**Excel 新增兩個欄位**：
- `new_dropped`：相鄰兩幀之間新增的 drop
- `new_frozen`：相鄰兩幀之間新增的 freeze

---

### 修改 Unity（推薦）

**實現選項 2（定期重置）**：

修改三個文件，添加重置邏輯：
```csharp
// 在每個 InferenceRunManager 的 RunServerInference 結束後
m_totalFrames++;

// 每 100 幀重置統計
const int RESET_INTERVAL = 100;
if (m_totalFrames % RESET_INTERVAL == 0)
{
    Debug.Log($"[STATS] Reset at frame {m_totalFrames}");
    m_droppedFrames = 0;
    m_frozenFrames = 0;
}
```

**好處**：
- 數字保持在 0-100 之間
- 容易看出最近的性能

---

## 總結

### 問題

- `dropped_frames` 和 `freeze_frames` 是累計值
- 同一場景內會持續累加（可能到 1174）
- 場景切換時會自動重置

### 解決方案

**短期（不修改代碼）**：
- Excel 分析時計算差值
- 或在 Server 端添加 `new_dropped` 欄位

**長期（修改 Unity）**：
- 定期重置（每 100 幀）
- 或實現移動視窗統計

### 推薦

**立即實現 Server 端差值記錄**，這樣：
- ✅ 不需要修改 Unity
- ✅ Excel 直接顯示每幀新增的 drop
- ✅ 容易分析趨勢

**未來考慮 Unity 端定期重置**，讓數字更直觀。

---

## 相關文檔

- `DROP_FRAME_EXPLANATION.md` - Drop Frame 詳細說明
- `EXCEL_METRICS_EXPLANATION.md` - Excel 指標說明
