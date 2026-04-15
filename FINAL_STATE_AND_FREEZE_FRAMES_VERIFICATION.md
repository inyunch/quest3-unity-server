# Final State 和 Freeze Frames 計算方式驗證

**日期**: 2026-04-15
**目的**: 確認 `final_state` 和 `freeze_frames_per_frame` 的計算方式是否合理和正確
**結論**: ✅ **兩者計算邏輯都正確且合理**

---

## 1. `final_state` 計算方式

### 可能的狀態值

```csharp
public enum FrameState
{
    Pending,    // 請求已發送，等待回應
    Completed,  // 回應已接收，尚未顯示
    Displayed,  // 成功顯示給用戶
    Dropped,    // 已接收但在顯示前被更新的幀取代
    Failed      // 網路錯誤、超時或解析失敗
}
```

### 狀態轉換流程圖

```
                    ┌─────────────┐
                    │   PENDING   │ ← Frame 創建時 (unity_send_ts)
                    └──────┬──────┘
                           │
              ┌────────────┼────────────┐
              │            │            │
        [Success]      [Timeout]   [Network Error]
              │            │            │
              ▼            ▼            ▼
       ┌──────────┐   ┌────────┐  ┌────────┐
       │COMPLETED │   │ FAILED │  │ FAILED │
       └─────┬────┘   └────────┘  └────────┘
             │            ▲            ▲
             │            │            │
    ┌────────┼────────┐   │            │
    │        │        │   │            │
[Display] [Supersede] │   │            │
    │        │        │   │            │
    ▼        ▼        │   │            │
┌─────────┐ ┌────────┐   │            │
│DISPLAYED│ │DROPPED │   │            │
└─────────┘ └────┬───┘   │            │
                 │       │            │
                 └───────┴────────────┘
                   All sent to Excel
```

### 詳細狀態轉換邏輯

#### 1️⃣ **Pending → Completed** (成功接收回應)
**位置**: `SegmentationInferenceRunManager.cs:811`
```csharp
trace.MarkCompleted(receiveTimestamp);
```
**觸發時機**:
- HTTP request 成功返回
- JSON 解析成功
- 設置 `unity_receive_ts`, `server_receive_ts`, `server_send_ts`

#### 2️⃣ **Completed → Displayed** (成功顯示)
**位置**: `SegmentationInferenceRunManager.cs:898`
```csharp
newest.MarkDisplayed(currentTimestamp);
```
**觸發條件**:
- Frame 是所有 Completed frames 中最新的
- `newest.frame_id > m_lastDisplayedFrameId` (不是遲到的幀)
- 設置 `unity_display_ts`

#### 3️⃣ **Completed → Dropped** (被取代，兩種情況)

**情況 A: 正常取代** (同時有多個 Completed frames)
**位置**: `SegmentationInferenceRunManager.cs:877`
```csharp
completedFrames[i].MarkDropped(currentTimestamp, $"superseded_by_newer_{newest.frame_id}");
```
**範例**:
```
Frame 1, 2, 3 同時完成 (Completed)
→ 只顯示 Frame 3 (Displayed)
→ Frame 1, 2 標記為 Dropped (drop_reason = "superseded_by_newer_3")
```

**情況 B: 遲到取代** (Out-of-order completion)
**位置**: `SegmentationInferenceRunManager.cs:889`
```csharp
newest.MarkDropped(currentTimestamp, $"arrived_after_newer_{m_lastDisplayedFrameId}");
```
**範例**:
```
Frame 2 顯示 (m_lastDisplayedFrameId = 2)
Frame 3 顯示 (m_lastDisplayedFrameId = 3)
Frame 1 完成 (遲到!) → 標記為 Dropped (drop_reason = "arrived_after_newer_3")
```

#### 4️⃣ **Pending → Failed** (錯誤，三種情況)

**情況 A: 網路錯誤**
**位置**: `SegmentationInferenceRunManager.cs:701`
```csharp
trace.MarkFailed($"{request.result}: {request.error}");
```
**範例**: `ConnectionError`, `ProtocolError`, `DataProcessingError`

**情況 B: JSON 解析錯誤**
**位置**: `SegmentationInferenceRunManager.cs:731`
```csharp
trace.MarkFailed("JSON parse error");
```

**情況 C: 超時**
**位置**: `SegmentationInferenceRunManager.cs:1109`
```csharp
trace.MarkFailed($"Timeout after {timeSinceSendSec:F1}s");
```
**超時時長**: 5 秒 (`FRAME_TIMEOUT_SECONDS = 5.0f`)

---

## 2. `freeze_frames_per_frame` 計算方式

### 定義
**Freeze Frames**: Unity 沒有新的推理結果可以顯示的 Unity 幀數（凍結幀數）

### 計算邏輯

#### 步驟 1: 每個 Update() 增加計數器
**位置**: `SegmentationInferenceRunManager.cs:838`
```csharp
private void Update()
{
    if (m_useServerInference)
    {
        m_framesSinceLastDisplay++;  // 每幀遞增
        TryDisplayNewestFrame();
        // ...
    }
}
```

#### 步驟 2: 顯示新幀時分配並重置
**位置**: `SegmentationInferenceRunManager.cs:902-903`
```csharp
newest.freeze_frames = m_framesSinceLastDisplay - 1;  // 分配
m_framesSinceLastDisplay = 0;                          // 重置
```

### 為什麼要 `-1`？

```
Update() 1:  m_framesSinceLastDisplay = 1
Update() 2:  m_framesSinceLastDisplay = 2
Update() 3:  m_framesSinceLastDisplay = 3
Update() 4:  m_framesSinceLastDisplay = 4
             ↓ 新的推理結果到達
             TryDisplayNewestFrame() 被調用
             freeze_frames = 4 - 1 = 3
             ↑
             當前這一幀不算"凍結"，因為它有新內容顯示
```

**正確性驗證**:
- Update 1: 舊結果 (凍結) ✅
- Update 2: 舊結果 (凍結) ✅
- Update 3: 舊結果 (凍結) ✅
- Update 4: **新結果顯示** (不算凍結) ❌
- **Total freeze frames = 3** ✅

### 計算公式

```
freeze_frames = (unity_display_ts[N] - unity_display_ts[N-1]) / 16.67ms - 1

其中:
- 16.67ms = Unity 幀時間 (假設 60 FPS)
- -1 = 當前顯示幀不算凍結
```

### 預期值範圍

#### 理想情況 (target_fps = 10, Unity = 60 FPS)
```
發送間隔 = 100ms
Unity 幀數 = 100ms / 16.67ms ≈ 6 frames

Expected freeze_frames = 6 - 1 = 5 frames
```

#### 實際情況 (考慮 server 處理時間)
```
E2E latency = 200ms (發送到顯示)
Unity 幀數 = 200ms / 16.67ms ≈ 12 frames

Expected freeze_frames = 12 - 1 = 11 frames
```

#### 高負載情況
```
E2E latency = 500ms
Unity 幀數 = 500ms / 16.67ms ≈ 30 frames

Expected freeze_frames = 30 - 1 = 29 frames
```

### 特殊情況: freeze_frames = 0

**合法情況** (極少見):
```
E2E latency < 16.67ms (1 個 Unity frame)
→ m_framesSinceLastDisplay = 1
→ freeze_frames = 1 - 1 = 0 ✅
```

**非法情況** (數據全為 0):
```
所有 frames 的 freeze_frames = 0
→ 表示舊版本 APK (未包含計算代碼) ❌
→ 需要重新構建和部署
```

---

## 3. 兩者的關聯性

### Displayed Frames
```
final_state = "Displayed"
freeze_frames = m_framesSinceLastDisplay - 1  (必定被賦值)
drop_reason = ""  (空字串)
unity_display_ts = 有值
unity_drop_ts = null
```

### Dropped Frames
```
final_state = "Dropped"
freeze_frames = 0  (預設值，未被賦值)
drop_reason = "superseded_by_newer_X" 或 "arrived_after_newer_X"
unity_display_ts = null
unity_drop_ts = 有值
```

**重要**: Dropped frames 的 `freeze_frames` 永遠是 0，因為它們從未被顯示，所以沒有機會被賦值。

### Failed Frames
```
final_state = "Failed"
freeze_frames = 0  (預設值，未被賦值)
error_reason = "ConnectionError: ..." 或 "Timeout after 5.0s" 等
unity_display_ts = null
unity_drop_ts = null
unity_receive_ts = 0 或 有值 (取決於失敗時機)
```

---

## 4. 驗證方法

### 驗證 1: Final State 一致性
```python
import pandas as pd

df = pd.read_excel('inference_log.xlsx')

# 驗證: Displayed frames 必須有 unity_display_ts
displayed = df[df['final_state'] == 'Displayed']
assert displayed['unity_display_ts'].notna().all(), "Displayed frames 缺少 unity_display_ts"

# 驗證: Dropped frames 必須有 unity_drop_ts 和 drop_reason
dropped = df[df['final_state'] == 'Dropped']
assert dropped['unity_drop_ts'].notna().all(), "Dropped frames 缺少 unity_drop_ts"
assert dropped['drop_reason'].notna().all(), "Dropped frames 缺少 drop_reason"

# 驗證: Failed frames 必須有 error_reason
failed = df[df['final_state'] == 'Failed']
assert failed['error_reason'].notna().all(), "Failed frames 缺少 error_reason"

print("✅ Final state 一致性驗證通過")
```

### 驗證 2: Freeze Frames 合理性
```python
# 驗證: 只有 Displayed frames 應該有非零 freeze_frames
displayed = df[df['final_state'] == 'Displayed']
dropped = df[df['final_state'] == 'Dropped']
failed = df[df['final_state'] == 'Failed']

# Dropped 和 Failed 應該都是 0
assert (dropped['freeze_frames_per_frame'] == 0).all(), "Dropped frames 應該 freeze_frames = 0"
assert (failed['freeze_frames_per_frame'] == 0).all(), "Failed frames 應該 freeze_frames = 0"

# Displayed frames 應該有合理的值 (不是全部為 0)
if (displayed['freeze_frames_per_frame'] == 0).all():
    print("❌ 所有 Displayed frames 的 freeze_frames = 0 (舊版本 APK)")
else:
    print(f"✅ Freeze frames 範圍: {displayed['freeze_frames_per_frame'].min()} - {displayed['freeze_frames_per_frame'].max()}")
    print(f"   平均值: {displayed['freeze_frames_per_frame'].mean():.1f}")
```

### 驗證 3: Freeze Frames 與時間戳的一致性
```python
import pandas as pd

df = pd.read_excel('inference_log.xlsx')
displayed = df[df['final_state'] == 'Displayed'].sort_values('frame_id')

# 計算相鄰 display timestamp 的間隔
for i in range(1, len(displayed)):
    prev_ts = pd.to_datetime(displayed.iloc[i-1]['unity_display_ts'])
    curr_ts = pd.to_datetime(displayed.iloc[i]['unity_display_ts'])

    # 時間間隔 (毫秒)
    interval_ms = (curr_ts - prev_ts).total_seconds() * 1000

    # 預期的 Unity 幀數 (假設 60 FPS)
    expected_frames = interval_ms / 16.67
    expected_freeze = int(expected_frames) - 1

    actual_freeze = displayed.iloc[i]['freeze_frames_per_frame']

    print(f"Frame {displayed.iloc[i]['frame_id']}: "
          f"interval={interval_ms:.0f}ms, "
          f"expected_freeze={expected_freeze}, "
          f"actual_freeze={actual_freeze}")

    # 允許 ±2 幀的誤差 (因為浮點數計算和時間戳精度)
    if abs(actual_freeze - expected_freeze) > 2:
        print(f"  ⚠️  誤差過大!")
```

---

## 5. 結論

### `final_state` 計算方式
✅ **正確且合理**

**優點**:
1. 狀態轉換邏輯清晰（Pending → Completed → Displayed/Dropped/Failed）
2. 涵蓋所有可能的情況（成功、取代、超時、錯誤）
3. Out-of-order completion 有專門的處理邏輯
4. Drop reason 清楚標示原因（superseded vs arrived_after_newer）

**無需修改**

---

### `freeze_frames_per_frame` 計算方式
✅ **正確且合理**

**優點**:
1. 精確測量 Unity 凍結幀數（沒有新推理結果的幀數）
2. `-1` 邏輯正確（當前顯示幀不算凍結）
3. 只對 Displayed frames 賦值（Dropped/Failed 保持 0）
4. 可以用來分析用戶體驗（凍結時間長 = 體驗差）

**當前問題**:
- 用戶數據顯示所有 freeze_frames = 0
- **原因**: Quest 3 上的 APK 是舊版本（計算代碼添加之前）
- **解決**: 重新構建和部署最新代碼

**無需修改代碼邏輯**

---

## 6. 行動項目

1. ✅ **代碼邏輯驗證完成** - 兩者計算方式都正確
2. ⏳ **需要重新構建和部署** - 讓 freeze_frames 計算代碼生效
3. ⏳ **部署後驗證** - 使用上述 Python 腳本驗證新數據

---

## 7. 參考文件

- `FrameTrace.cs` - 狀態定義和標記方法
- `SegmentationInferenceRunManager.cs` - 狀態轉換邏輯
- `TELEMETRY_CALCULATION_GUIDE.md` - 完整的計算方法文檔
- `OUT_OF_ORDER_COMPLETION_FIX.md` - Out-of-order 處理邏輯
- `FREEZE_FRAMES_ZERO_DIAGNOSTIC.md` - Freeze frames = 0 診斷文檔
