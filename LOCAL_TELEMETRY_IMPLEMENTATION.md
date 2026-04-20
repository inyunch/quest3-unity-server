# Local Telemetry Implementation Summary

**Date**: 2026-04-20
**Purpose**: 完整記錄每個frame的狀態,包含Dropped和Failed frames,繞過N+1 delayed telemetry的限制

---

## 🎯 解決的問題

### 問題1: Missing Drop Reasons
- **現象**: Excel只顯示Displayed (15%) 和 ServerProcessed (85%),沒有Dropped或Failed entries
- **原因**: Unity的drop tracking存在,但frames必須先到達`Completed`狀態才能被標記為Dropped
- **結果**: 73個frames卡在Pending狀態,從未被記錄drop原因

### 問題2: N+1 Delayed Telemetry Limitation
- **現象**: Unity只在發送Frame N+1時才發送Frame N的telemetry
- **問題**:
  - 如果session結束在pending frames還沒到達final state,這些frames永遠不會有telemetry
  - 無法即時看到drop/failed frames
  - 依賴server-side fallback logging (scene=server_detection)

---

## ✅ 實作的解決方案

### 1. LocalTelemetryWriter (新增)

**檔案**: `Assets/PassthroughCameraApiSamples/Shared/Scripts/LocalTelemetryWriter.cs`

**功能**:
- 直接寫入CSV到Quest本地儲存: `/sdcard/Android/data/{package}/files/telemetry_{session_id}_{timestamp}.csv`
- 在每個frame到達**final state**時立即寫入 (Displayed/Dropped/Failed)
- **完全繞過N+1 delayed pattern** - 不需要等待下一個frame
- 完整34個欄位,匹配server-side Excel schema
- 每次寫入後立即flush,確保crash時不會丟失資料

**特點**:
- ✅ 即時記錄 - 不延遲
- ✅ 完整追蹤 - 包含所有states
- ✅ Crash-safe - 每行立即寫入檔案
- ✅ 本地儲存 - 不依賴網路

### 2. SentisInferenceRunManager 修改

**檔案**: `Assets/PassthroughCameraApiSamples/MultiObjectDetection/SentisInference/Scripts/SentisInferenceRunManager.cs`

**新增private變數** (line 110-112):
```csharp
private LocalTelemetryWriter m_localTelemetry;
[SerializeField] private bool m_enableLocalTelemetry = true;  // Feature flag
```

**Start() 初始化** (line 138-151):
```csharp
if (m_enableLocalTelemetry)
{
    m_localTelemetry = new LocalTelemetryWriter();
    if (m_localTelemetry.Initialize(m_sessionId))
    {
        Debug.Log($"[LOCAL TELEMETRY] Initialized: {m_localTelemetry.GetFilePath()}");
    }
}
```

**新增WriteLocalTelemetry() helper** (line 1666-1679):
```csharp
private void WriteLocalTelemetry(FrameTrace trace)
{
    if (m_localTelemetry != null)
    {
        m_localTelemetry.WriteFrameTrace(trace, "MultiObjectDetection");
    }
}
```

**在所有final state transitions調用** (4個位置):
1. **Line 1108** - After `MarkDisplayed()` (Displayed state)
2. **Line 1036** - After `MarkDropped()` (superseded by newer)
3. **Line 1051** - After `MarkDropped()` (late arrival)
4. **Line 1287** - After `MarkFailed()` (timeout)

**OnDestroy() cleanup** (line 210-215):
```csharp
if (m_localTelemetry != null)
{
    m_localTelemetry.Close();
    Debug.Log($"[LOCAL TELEMETRY] Session ended, {m_localTelemetry.GetRowCount()} rows written");
}
```

### 3. Performance Optimizations

**targetFPS調整** (line 40):
```csharp
targetFPS = 5f,  // Reduced from 10 to 5 for better completion rate
```

**影響**:
- 從每100ms發送一個frame → 每200ms發送一個
- 給每個frame更多時間complete
- 預期Displayed rate從15% → 30-40%

### 4. ADB Collection Script

**檔案**: `Tools/pull_quest_telemetry.bat`

**功能**:
- 自動從Quest pull所有telemetry CSV files
- 檢查Quest連接狀態
- 創建本地`telemetry/`目錄
- 列出所有複製的檔案

**使用方法**:
```batch
# 雙擊執行
Tools\pull_quest_telemetry.bat

# 或手動拉取
adb pull /sdcard/Android/data/com.DefaultCompany.PassthroughCameraApiSamples/files/telemetry_*.csv ./telemetry/
```

---

## 📊 資料格式

### CSV檔案位置

**Quest端**:
```
/sdcard/Android/data/com.DefaultCompany.PassthroughCameraApiSamples/files/
  └─ telemetry_{session_id}_{timestamp}.csv
```

**PC端** (pull後):
```
C:\Users\user\Unity-PassthroughCameraApiSamples\telemetry\
  └─ telemetry_{session_id}_{timestamp}.csv
```

### CSV欄位 (34欄,匹配Excel schema)

```
timestamp, scene, session_id, frame_id,
unity_send_ts, unity_receive_ts, unity_display_ts, unity_drop_ts,
server_receive_ts, server_process_start_ts, server_send_ts,
latency_ms, upload_ms, queue_wait_ms, server_proc_ms, download_ms, parse_ms, udp_send_ms,
server_pct, upload_pct, download_pct,
detection_count, avg_confidence, keypoint_avg_conf,
image_width, image_height,
upload_bytes_uncompressed, upload_bytes_compressed,
download_bytes_uncompressed, download_bytes_compressed,
final_state, drop_reason, error_reason,
freeze_frames_per_frame, freeze_duration_ms, cumulative_freeze_frames, freeze_ratio,
frame_gap, cumulative_dropped
```

### final_state 值

- **Displayed**: Frame成功顯示在Unity
- **Dropped**: Frame被newer frame取代或late arrival
- **Failed**: Polling timeout或network error

### drop_reason 範例

- `"superseded_by_newer_25"` - 被Frame 25取代
- `"arrived_after_newer_30"` - 在Frame 30已顯示後才到達
- `null` - Displayed frames沒有drop_reason

### error_reason 範例

- `"Timeout after 5.2s"` - Polling超時
- `"Response timeout (5s)"` - HTTP polling失敗
- `"JSON parse error"` - 解析失敗

---

## 🔄 雙軌Telemetry系統

現在系統有**兩個獨立的telemetry tracks**:

### Track 1: Local CSV (Unity本地)
- **目的**: 完整記錄所有frames,包含Dropped/Failed
- **時機**: 每個frame到達final state時立即寫入
- **位置**: Quest本地儲存
- **優點**: 即時、完整、不丟失
- **缺點**: 需要手動pull from Quest

### Track 2: Server Excel (N+1 delayed)
- **目的**: Server-side analysis,包含server fallback
- **時機**: Frame N+1發送時才包含Frame N的telemetry
- **位置**: Server端 `C:\Repo\Github\vision_server\debug\logs\`
- **優點**: 自動收集,包含server perspective
- **缺點**: 可能缺少pending frames

---

## 📈 預期改善

### Before (targetFPS=10)
```
Total frames sent: 86
Displayed: 13 (15.3%)
ServerProcessed: 72 (84.7%)  # Unity沒發送telemetry,server fallback記錄
Dropped: 0 (missing!)
Failed: 0 (missing!)
```

### After (targetFPS=5 + Local Telemetry)
```
Total frames sent: ~40-50 (in same time period)
Displayed: 15-20 (30-40%)  # 改善!
Dropped: 15-20 (30-40%)    # 現在會看到!
Failed: 5-10 (10-20%)      # 現在會看到!

Local CSV完整記錄: ALL 40-50 frames
Server Excel: Displayed frames + server fallback
```

---

## 🚀 使用流程

### 1. Build & Deploy
```bash
# Unity中
File → Build Settings → Build And Run

# 確保Inspector中:
☑ Use Server Inference
☑ Enable Local Telemetry  # 新增的checkbox
☑ Use UDP Transport
```

### 2. Run Test
```bash
# Quest上執行app 20-30秒
# Unity logs會顯示:
[LOCAL TELEMETRY] Initialized: /sdcard/.../telemetry_{guid}_{timestamp}.csv
[LOCAL TELEMETRY] Wrote frame 5 (state=Displayed, row=1)
[LOCAL TELEMETRY] Wrote frame 3 (state=Dropped, row=2)
[LOCAL TELEMETRY] Session ended, 25 rows written
```

### 3. Pull CSV
```bash
# 方法1: 使用batch script (推薦)
Tools\pull_quest_telemetry.bat

# 方法2: 手動adb pull
adb pull /sdcard/Android/data/com.DefaultCompany.PassthroughCameraApiSamples/files/telemetry_*.csv ./telemetry/

# 方法3: Windows File Explorer
This PC → Quest 3 → Internal shared storage → Android → data → com.DefaultCompany.PassthroughCameraApiSamples → files
```

### 4. Analysis
```bash
# 用Excel打開CSV
start excel telemetry\telemetry_*.csv

# 或用Python分析
python -c "import pandas as pd; df = pd.read_csv('telemetry/telemetry_xxx.csv'); print(df['final_state'].value_counts())"
```

---

## 🔍 Debug & Troubleshooting

### 找不到CSV檔案?

**檢查Unity logs**:
```bash
adb logcat -s Unity | findstr "LOCAL TELEMETRY"
```

**可能原因**:
1. `m_enableLocalTelemetry = false` (Inspector中取消勾選)
2. App沒執行或crash了
3. Package name不同 (檢查: `adb shell pm list packages | findstr Passthrough`)

### CSV是空的?

**可能原因**:
1. 沒有frames到達final state
2. LocalTelemetryWriter初始化失敗
3. 檔案權限問題

**解決**:
```bash
# 檢查Unity logs
adb logcat -s Unity | findstr "Wrote frame"

# 檢查檔案是否存在且有內容
adb shell ls -lh /sdcard/Android/data/com.DefaultCompany.PassthroughCameraApiSamples/files/
```

### 還是只看到Displayed frames?

**可能原因**:
1. 所有frames都成功complete了 (好事!)
2. WriteLocalTelemetry()沒被調用

**驗證**:
- 檢查final_state欄位是否有Dropped/Failed entries
- 如果都是Displayed,表示performance很好!
- 比較local CSV row count vs server Excel Displayed count

---

## 📝 Code Locations Summary

### 新增檔案
- `Assets/.../Shared/Scripts/LocalTelemetryWriter.cs` - CSV writer實作
- `Tools/pull_quest_telemetry.bat` - ADB pull script

### 修改檔案
- `Assets/.../MultiObjectDetection/SentisInference/Scripts/SentisInferenceRunManager.cs`
  - Line 40: `targetFPS = 5f`
  - Line 110-112: LocalTelemetryWriter variables
  - Line 138-151: Initialize LocalTelemetryWriter
  - Line 1036, 1051, 1108, 1287: WriteLocalTelemetry() calls
  - Line 1666-1679: WriteLocalTelemetry() method
  - Line 210-215: OnDestroy() cleanup

---

## 🎉 Summary

### 實作完成
1. ✅ LocalTelemetryWriter - 本地CSV writer
2. ✅ SentisInferenceRunManager整合 - 4個WriteLocalTelemetry()調用
3. ✅ targetFPS優化 (10 → 5)
4. ✅ ADB collection script
5. ✅ 完整documentation

### 新功能
- ✅ 每個frame的final state都會被記錄 (Displayed/Dropped/Failed)
- ✅ 即時寫入,不延遲
- ✅ 完整34欄位,匹配Excel schema
- ✅ Crash-safe logging
- ✅ 簡單的pull script

### 下一步
1. Build & Deploy到Quest
2. 執行測試session (20-30秒)
3. Pull CSV files
4. 分析結果,確認看到Dropped/Failed entries
5. 比較local CSV vs server Excel,理解兩者的差異

---

**完成日期**: 2026-04-20
