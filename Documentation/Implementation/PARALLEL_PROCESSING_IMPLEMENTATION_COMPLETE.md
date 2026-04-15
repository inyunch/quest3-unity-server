# Parallel Processing Implementation - Complete ✅

## Overview

完整實現了從串行處理到並行處理架構的遷移，所有6個階段已完成。系統現在支持：
- Unity端並行發送多個推理請求
- 服務器端多worker並行處理
- 只顯示最新完成的幀（自動丟棄舊幀）
- 完整的per-frame生命週期追蹤和日誌記錄

---

## 實現階段總結

### ✅ Phase 1: FrameTrace Structure (Unity)
**目標**: 添加per-frame追蹤結構，不改變現有流程

**實現內容**:
- 創建 `FrameTrace.cs` - 完整的frame生命週期追蹤類
- 添加 `FrameState` enum (Pending/Completed/Displayed/Dropped/Failed)
- 在 `PoseInferenceRunManager.cs` 中添加 `Dictionary<int, FrameTrace>`
- 在每個請求發送/接收/顯示時記錄時間戳
- 保持立即顯示行為（為Phase 3做準備）

**關鍵文件**:
- `Assets/PassthroughCameraApiSamples/Shared/Scripts/FrameTrace.cs` (新建)
- `Assets/PassthroughCameraApiSamples/PoseEstimation/Scripts/PoseInferenceRunManager.cs` (修改)

**驗證方式**:
```
查看Unity Console日誌：
[FRAME TRACE] Created trace for frame 1: ...
[FRAME TRACE] Frame 1 completed and displayed: ...
```

---

### ✅ Phase 2: Parallel Request Sending (Unity)
**目標**: 移除串行鎖，允許並發請求

**實現內容**:
- **移除**: `m_inferenceInProgress` 檢查和設置
- **移除**: `m_frozenFrames` 增量邏輯（freeze概念已廢棄）
- 添加 `Dictionary<int, UnityWebRequest>` 追蹤pending requests
- 在發送時添加到dictionary，完成時移除
- 允許多個協程同時運行

**關鍵變更**:
```csharp
// 舊代碼（已移除）:
if (m_inferenceInProgress) {
    m_frozenFrames++;
    yield break;
}
m_inferenceInProgress = true;

// 新代碼:
// 直接發送，不檢查lock
m_pendingRequests[m_frameId] = request;
```

**驗證方式**:
```
查看日誌：
[PARALLEL] Frame 1 added to pending requests. Total pending: 1
[PARALLEL] Frame 2 added to pending requests. Total pending: 2  ← 並發！
[PARALLEL] Frame 1 removed from pending. Remaining: 1
```

---

### ✅ Phase 3: Deferred Display (Unity)
**目標**: 解耦響應接收和顯示，實現"只顯示最新"邏輯

**實現內容**:
- **移除**: `RunServerInference()` 中的立即顯示邏輯
- **添加**: `Update()` 方法調用 `TryDisplayNewestFrame()`
- **添加**: `TryDisplayNewestFrame()` - 選擇最新completed frame並顯示
- **添加**: 自動標記舊frames為Dropped
- **添加**: `DisplayFrame()` - 實際渲染skeleton的方法
- 更新 `m_droppedFrames` 定義：received but never displayed

**關鍵邏輯**:
```csharp
void TryDisplayNewestFrame() {
    // 1. 找到所有Completed frames
    var completedFrames = FindAllCompleted();
    if (completedFrames.Count == 0) return;

    // 2. 排序，取最新
    var newest = completedFrames.OrderByDescending(f => f.frame_id).First();

    // 3. 標記舊的為Dropped
    foreach (var older in completedFrames.Skip(1)) {
        older.MarkDropped("superseded_by_newer");
        m_droppedFrames++;  // NEW定義！
    }

    // 4. 顯示最新
    DisplayFrame(newest);
    newest.MarkDisplayed();
}
```

**驗證方式**:
```
查看日誌：
[PARALLEL DISPLAY] Frame 3 DROPPED (superseded by 5)
[PARALLEL DISPLAY] Frame 4 DROPPED (superseded by 5)
[PARALLEL DISPLAY] Frame 5 DISPLAYED. Dropped 2 older frames.
```

---

### ✅ Phase 4: Server Multi-Worker (Server)
**目標**: 啟用服務器端並行處理

**實現內容**:
- 修改 `app/main.py` 支持可配置worker數量
- 從環境變量 `UVICORN_WORKERS` 讀取worker數（默認4）
- 當workers > 1時自動禁用hot-reload（不兼容）
- 創建便捷啟動腳本 `start_server.bat` 和 `start_server.sh`
- 創建 `PARALLEL_PROCESSING_GUIDE.md` 文檔

**使用方式**:
```bash
# Windows
start_server.bat 4    # 4 workers (生產模式)
start_server.bat 1    # 1 worker (開發模式，hot-reload)

# Linux/Mac
./start_server.sh 4
./start_server.sh 1
```

**架構變化**:
```
串行 (1 worker):
Request 1 → Process → Return
Request 2 →   Wait  → Process → Return

並行 (4 workers):
Request 1 → Worker 1 → Process → Return
Request 2 → Worker 2 → Process → Return (同時)
Request 3 → Worker 3 → Process → Return (同時)
Request 4 → Worker 4 → Process → Return (同時)
```

---

### ✅ Phase 5: Logging Schema Update (Server + Unity)
**目標**: 更新Excel日誌架構支持per-frame生命週期

**實現內容**:
- 更新 `debug/inference_logger.py` 的 `COLUMNS` 列表
- **新增列**:
  - `unity_send_ts`, `unity_receive_ts`, `unity_display_ts`, `unity_drop_ts`
  - `server_receive_ts`, `server_send_ts`
  - `final_state` (Displayed/Dropped/Failed)
  - `drop_reason`, `error_reason`
- **重新定義**:
  - `dropped_frames`: 從"FPS節流跳過"改為"received but never displayed"
- **標記為LEGACY** (Phase 6將移除):
  - `freeze_frames_LEGACY`, `freeze_ratio_LEGACY`, `new_frozen_LEGACY`

**新Excel架構** (總共33列):
```
Identity: timestamp, scene, frame_id
Unity Timestamps: unity_send_ts, unity_receive_ts, unity_display_ts, unity_drop_ts
Server Timestamps: server_receive_ts, server_send_ts
Timing: latency_ms, server_proc_ms, upload_ms, download_ms, parse_ms
Percentages: server_pct, upload_pct, download_pct
Results: detection_count, avg_confidence, keypoint_avg_conf
Image: image_width, image_height, upload_bytes_*, download_bytes_*
State: final_state, drop_reason, error_reason
Legacy: target_fps, dropped_frames (重新定義)
DEPRECATED: freeze_frames_LEGACY, freeze_ratio_LEGACY, new_frozen_LEGACY
```

---

### ✅ Phase 6: Cleanup and Optimization (Unity)
**目標**: 移除過時代碼，添加內存管理和性能監控

**實現內容**:
1. **Frame Cleanup**:
   - 添加 `MAX_FRAME_TRACES = 100` 限制
   - `CleanupOldFrames()` - 自動移除舊的Displayed/Dropped/Failed frames
   - 防止內存無限增長

2. **Frame Timeout**:
   - 添加 `FRAME_TIMEOUT_SECONDS = 5.0` 超時限制
   - `CheckFrameTimeouts()` - 標記長時間pending的frames為Failed
   - 自動中止超時請求

3. **Performance Metrics**:
   - `GetPerformanceMetrics()` - 統計各狀態frame數量
   - 計算drop rate
   - 每10秒自動輸出到日誌

**性能監控輸出**:
```
[PERFORMANCE METRICS] Traces=15 Pending=2 Completed=1
Displayed=10 Dropped=2(13.3%) Failed=0
```

---

## 核心概念重新定義

### Dropped Frame（新定義）
**舊定義（已廢棄）**: FPS節流導致的跳過（發送前就跳過）

**新定義**:
- 已經從服務器收到響應
- 但從未在Unity中顯示
- 因為有更新的frame完成了

**例子**:
```
Frame 1: 發送 → 完成 → 顯示 ✅
Frame 2: 發送 → 完成（晚） → 被跳過 ❌ DROPPED
Frame 3: 發送 → 完成（早） → 顯示 ✅
```

### Freeze Frame（已廢棄）
**舊定義**: `m_inferenceInProgress = true` 時的等待

**新狀態**:
- 概念已完全移除
- 不再作為指標追蹤
- Excel中的freeze列標記為LEGACY，將在未來移除
- 並行架構下不會有"freeze"（多個請求可以同時進行）

---

## 文件變更總覽

### Unity側
| 文件 | 變更類型 | 說明 |
|------|---------|------|
| `Shared/Scripts/FrameTrace.cs` | **新建** | Per-frame狀態追蹤類 |
| `PoseEstimation/Scripts/PoseInferenceRunManager.cs` | **重大修改** | 所有6個階段的變更 |

### Server側
| 文件 | 變更類型 | 說明 |
|------|---------|------|
| `app/main.py` | **修改** | 添加multi-worker支持 |
| `debug/inference_logger.py` | **重大修改** | 更新Excel架構 |
| `start_server.bat` | **新建** | Windows啟動腳本 |
| `start_server.sh` | **新建** | Linux/Mac啟動腳本 |
| `PARALLEL_PROCESSING_GUIDE.md` | **新建** | 使用指南 |

### 文檔
| 文件 | 說明 |
|------|------|
| `PARALLEL_MIGRATION_PROPOSAL.md` | 原始技術提案 |
| `PARALLEL_PROCESSING_IMPLEMENTATION_COMPLETE.md` | 本文檔 - 完成摘要 |

---

## 測試與驗證

### Unity端測試
1. **啟動Scene**: PoseEstimation
2. **觀察Console日誌**:
   ```
   [FRAME TRACE] Created trace for frame 1
   [PARALLEL] Frame 1 added to pending requests. Total pending: 1
   [PARALLEL] Frame 2 added to pending requests. Total pending: 2  ← 並發！
   [PARALLEL DISPLAY] Frame 2 DISPLAYED
   [PERFORMANCE METRICS] Traces=10 Pending=1 Completed=0 Displayed=8 Dropped=1(10%)
   ```

3. **預期行為**:
   - 多個requests同時pending
   - 只有最新的frame被顯示
   - Dropped frames統計正確
   - 每10秒輸出性能指標

### Server端測試
1. **啟動多worker**:
   ```bash
   start_server.bat 4
   ```

2. **觀察輸出**:
   ```
   [SERVER CONFIG] Starting uvicorn with 4 worker(s), reload=False
   INFO:     Started parent process [12345]
   INFO:     Started child process [12346]
   INFO:     Started child process [12347]
   INFO:     Started child process [12348]
   INFO:     Started child process [12349]
   ```

3. **驗證並行處理**:
   ```bash
   # Windows
   tasklist | findstr python
   # 應該看到5個進程（1個parent + 4個worker）
   ```

### Excel日誌驗證
1. **位置**: `vision_server/debug/logs/inference_log_YYYY-MM-DD.xlsx`
2. **檢查列數**: 33列（包含新的timestamp和state列）
3. **檢查數據**:
   - `final_state` 列應該有 "Displayed", "Dropped"
   - `drop_reason` 對dropped frames應該是 "superseded_by_newer_X"
   - `freeze_frames_LEGACY` 列應該都是0（已廢棄）

---

## 性能預期

### Quest 3 @ 5 FPS 場景
**串行模式 (1 worker)**:
- E2E延遲: 150-200ms
- 吞吐量: 最多20 FPS
- Dropped frames: 主要來自FPS節流
- Freeze frames: 如果server太慢會出現

**並行模式 (4 workers)**:
- E2E延遲: 150-250ms（略微增加，因為調度開銷）
- 吞吐量: 最多80 FPS
- Dropped frames: 主要來自超越display rate（這是好的！）
- Freeze frames: 不存在（已廢棄概念）

### 典型指標
```
好的性能:
[PERFORMANCE METRICS] Traces=15 Pending=2-4 Completed=0-1
Displayed=10 Dropped=2(13%) Failed=0

問題性能:
[PERFORMANCE METRICS] Traces=50 Pending=20+ Completed=5+
Displayed=10 Dropped=15(30%) Failed=5(10%)
```

---

## 回滾方案

### 回滾到串行模式
如果需要回退到串行處理：

**Server端**:
```bash
# 使用1個worker
start_server.bat 1
```

**Unity端**:
- Unity代碼已經是向後兼容的
- 即使在並行模式下也可以與單worker server配合
- 不需要代碼回滾

**完全回滾** (如果需要):
```bash
git revert <commit-hash>
```

---

## 未來優化建議

### 短期（可選）
1. **Unity端發送 frame trace timestamps**
   - 在HTTP headers中發送 `unity_send_ts`, `unity_receive_ts`
   - 減少服務器端計算負擔

2. **實現真正的per-frame日誌**
   - 當前只log有detection的frames
   - 可以改為log所有frames（包括dropped/failed）

### 中期（可選）
1. **動態worker調整**
   - 根據負載自動調整worker數量
   - 使用 Gunicorn 替代 Uvicorn 實現

2. **添加 Celery + Redis**
   - 如果需要優先級隊列
   - 如果需要分佈式workers
   - 如果需要任務重試機制

### 長期（架構改進）
1. **WebSocket替代HTTP**
   - 減少連接開銷
   - 實現雙向通信
   - 服務器可主動push結果

2. **gRPC streaming**
   - 更高效的序列化
   - 雙向streaming支持
   - 更好的錯誤處理

---

## 已知限制

1. **Out-of-Order Latency**: 並行模式下E2E延遲會有更高的variance（正常）
2. **GPU Memory**: 每個worker會佔用額外的GPU記憶體（約500MB per worker）
3. **CUDA Serialization**: 某些CUDA操作可能仍然是串行的（GPU driver層面）
4. **Python GIL**: 雖然用了multiprocessing，但每個worker內部仍受GIL限制

---

## 總結

✅ **所有6個階段已完成**
✅ **系統已升級為完整的並行處理架構**
✅ **向後兼容串行模式**
✅ **完整的文檔和測試指南**

**下一步**:
1. 在實際Quest 3設備上測試
2. 收集性能數據
3. 根據需要調整worker數量
4. 監控Excel日誌驗證正確性

**聯繫人**: Claude Code
**日期**: 2026-04-14
**版本**: 1.0.0 (完整並行處理實現)
