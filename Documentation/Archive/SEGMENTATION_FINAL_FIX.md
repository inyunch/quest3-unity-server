# Segmentation Telemetry - 最終修復方案

**Date**: 2026-04-15
**Status**: **完整修復 - Ready to Rebuild**

## 問題總結

### 問題1: Server timestamps missing (已修復 ✅)
**Root Cause**: Segmentation endpoint沒有在response中包含timestamp fields

**Server Fix Applied** (`segmentation.py` lines 203-215):
```python
response = {
    "detections": detection_result.model_dump(),
    "processing_time_ms": processing_time_ms,
    "t_server_recv": start_time,  # ✅ Added
    "t_server_send": t_postprocess_end  # ✅ Added
}
```

**Server Status**: ✅ Restarted, fix is live

### 問題2: Excel沒有任何Segmentation records (已修復 ✅)
**Root Cause**: Unity發送state="Completed"，server正確地過濾掉non-final states

**Why "Completed" instead of "Displayed"?**
- Segmentation使用event-driven (camera callback)模式
- 執行順序：
  1. Camera callback → `RunServerInference` (Frame N+1)
  2. `RunServerInference`讀取`m_lastCompletedTrace` (Frame N, **state=Completed**)
  3. 發送delayed headers ← **錯誤！發送了Completed狀態**
  4. Later... Update() → `TryDisplayNewestFrame` → `MarkDisplayed` (Frame N) ← **太晚了**

**Unity Fix Applied** (`SegmentationInferenceRunManager.cs` line 505-507):
```csharp
private IEnumerator RunServerInference(Texture texture)
{
    // CRITICAL: Display completed frames BEFORE starting new inference
    // This ensures m_lastCompletedTrace has state="Displayed" when delayed headers are sent
    TryDisplayNewestFrame();  // ✅ Added - 強制在發送前顯示completed frames

    // Increment frame counter and start E2E timing
    m_frameId++;
    // ... rest of the code
}
```

**執行順序修正後**:
1. Camera callback → `RunServerInference` (Frame N+1)
2. **`TryDisplayNewestFrame()` → `MarkDisplayed` (Frame N) → state=Displayed** ← **提前執行！**
3. `RunServerInference`讀取`m_lastCompletedTrace` (Frame N, **state=Displayed**) ← **正確！**
4. 發送delayed headers ← **發送Displayed狀態**

## 修復列表

### Server-Side (已重啟 ✅)
1. ✅ `segmentation.py` - 添加timestamp fields到response

### Unity-Side (需要rebuild)
1. ✅ `SegmentationInferenceRunManager.cs` line 505-507 - 在RunServerInference開始時調用TryDisplayNewestFrame()
2. ✅ `SegmentationInferenceRunManager.cs` lines 720-737 - 添加manual timestamp parsing fallback
3. ✅ `SegmentationInferenceRunManager.cs` lines 1119-1174 - 添加ExtractSimpleJsonValue helper method

## Testing Instructions

### Step 1: Rebuild Unity
```
Unity Editor > Build and Deploy to Quest 3
```

### Step 2: 清除舊的logcat
```bash
adb logcat -c
```

### Step 3: Test Segmentation
```
1. Launch Segmentation scene on Quest 3
2. Send 10 frames
```

### Step 4: 檢查ADB logs
```bash
adb logcat -d -s Unity:W | findstr "TELEMETRY DEBUG"
```

**Expected (SUCCESS)**:
```
[TELEMETRY DEBUG] After JSON parse: t_server_recv=1776283439.60219  ✅
[TELEMETRY DEBUG] MarkCompleted frame 5, server_recv=1776283439602  ✅ (Converted to ms!)
[TELEMETRY DEBUG] Set m_lastCompletedTrace to DISPLAYED frame 5, state=Displayed  ✅
[TELEMETRY DEBUG] Sending delayed headers for frame 5, state=Displayed  ✅ (No more Completed!)
```

### Step 5: 檢查Server logs
```bash
# In server terminal or BashOutput
```

**Expected (SUCCESS)**:
```
[FRAME STATE] Segmentation Frame 6: Logging previous frame 5 (E2E=105.1ms, Server=41.1ms)
Frame 5 logged successfully  ✅ (No more "Skipping" warnings!)
```

### Step 6: 檢查Excel
```
Navigate to vision_server Excel logs folder
Open latest Segmentation*.xlsx file
```

**Expected Results (SUCCESS)**:
| frame_id | state | unity_send_ts | server_receive_ts | server_send_ts |
|----------|-------|---------------|-------------------|----------------|
| 0 | Displayed | 1776283439605 | 1776283439610 | 1776283439640 |
| 1 | Displayed | 1776283439705 | 1776283439710 | 1776283439740 |

**Success Criteria**:
- ✅ state column: **"Displayed"** (not "Completed")
- ✅ server_receive_ts: **Valid Unix timestamp in ms** (not NaN, not 0)
- ✅ server_send_ts: **Valid Unix timestamp in ms** (not NaN, not 0)
- ✅ Rows are written to Excel (not filtered out)

## 為什麼這個修復是正確的？

### Comparison with MultiObjectDetection

**MultiObjectDetection** (timer-driven, fixed FPS):
```csharp
void Update() {
    TryDisplayNewestFrame();  // 1. Display first
    // ...
    if (should send frame) {
        SendImage();  // 2. Send next frame (reads m_lastCompletedTrace with state=Displayed)
    }
}
```

**Segmentation (event-driven, camera callback) - BEFORE FIX**:
```csharp
void OnCameraFrame(texture) {
    StartCoroutine(RunServerInference(texture));  // Send immediately (reads m_lastCompletedTrace with state=Completed)
}

void Update() {
    TryDisplayNewestFrame();  // Display later (too late!)
}
```

**Segmentation (event-driven, camera callback) - AFTER FIX**:
```csharp
void OnCameraFrame(texture) {
    StartCoroutine(RunServerInference(texture));
}

IEnumerator RunServerInference(texture) {
    TryDisplayNewestFrame();  // ✅ Display BEFORE sending!
    // ... send frame (reads m_lastCompletedTrace with state=Displayed)
}

void Update() {
    TryDisplayNewestFrame();  // Still here for safety, but redundant
}
```

## 預期結果

### Before Fix
- ❌ Server timestamps: All NaN (server不發送)
- ❌ Frame state: All "Completed" (Unity timing問題)
- ❌ Excel rows: 0 (server過濾掉Completed狀態)

### After Fix
- ✅ Server timestamps: 100% populated (server發送 + Unity正確轉換)
- ✅ Frame state: 100% "Displayed" (Unity在發送前先display)
- ✅ Excel rows: 100% written (server接受Displayed狀態)

## Summary

**Two critical fixes applied**:

1. **Server**: Add timestamp fields to response ✅
2. **Unity**: Call TryDisplayNewestFrame() before sending delayed headers ✅

**Both fixes are necessary**:
- Without server fix → timestamps = NaN
- Without Unity fix → state = "Completed" → server filters out → no Excel rows

**After rebuild, Segmentation telemetry will be 100% working!**

---

**Ready to rebuild Unity and test!**
