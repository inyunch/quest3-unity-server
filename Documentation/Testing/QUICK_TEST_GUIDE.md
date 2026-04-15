# Quick Test Guide - Parallel Processing

## 快速測試並行處理實現

### 前置條件
- Unity 項目已打開
- Python server 已安裝依賴
- Quest 3 已連接或使用 Unity Editor Play Mode

---

## 測試步驟

### Step 1: 啟動Server（並行模式）

**Windows**:
```bash
cd C:\Repo\Github\vision_server
start_server.bat 4
```

**Linux/Mac**:
```bash
cd /path/to/vision_server
./start_server.sh 4
```

**預期輸出**:
```
========================================
Vision Server Startup
========================================
Worker count: 4
Mode: Production (parallel processing)
========================================

[SERVER CONFIG] Starting uvicorn with 4 worker(s), reload=False
INFO:     Started parent process [12345]
INFO:     Started child process [12346]
INFO:     Started child process [12347]
INFO:     Started child process [12348]
INFO:     Started child process [12349]
INFO:     Application startup complete.
```

✅ **驗證**: 應該看到5個進程（1個parent + 4個workers）

---

### Step 2: 在Unity中打開並運行Scene

1. 打開 Unity
2. 打開 Scene: `Assets/PassthroughCameraApiSamples/PoseEstimation/PoseEstimation.unity`
3. 點擊 **Play**

---

### Step 3: 觀察Unity Console日誌

**預期看到以下日誌順序**:

#### Phase 1: Frame Trace 創建
```
[FRAME TRACE] Created trace for frame 1: [FRAME 1] State=Pending send=5.234
```

#### Phase 2: 並行請求
```
[PARALLEL] Frame 1 added to pending requests. Total pending: 1
[PARALLEL] Frame 2 added to pending requests. Total pending: 2  ← 並發！
[PARALLEL] Frame 3 added to pending requests. Total pending: 3
```

#### Phase 3: 延遲顯示與dropped frames
```
[FRAME TRACE] Frame 1 completed (state=Completed). Display deferred to Update().
[FRAME TRACE] Frame 2 completed (state=Completed). Display deferred to Update().
[PARALLEL DISPLAY] Frame 1 DROPPED (superseded by 2)
[PARALLEL DISPLAY] Frame 2 DISPLAYED. Dropped 1 older frames.
```

#### Phase 6: 性能指標（每10秒）
```
[PERFORMANCE METRICS] Traces=15 Pending=2 Completed=0 Displayed=10 Dropped=3(20%) Failed=0
```

✅ **驗證重點**:
- `Total pending` 應該 > 1（證明並行）
- 應該看到 "DROPPED (superseded by...)" 日誌
- `PERFORMANCE METRICS` 顯示合理的統計數據

---

### Step 4: 檢查Excel日誌

1. **打開文件**:
   ```
   C:\Repo\Github\vision_server\debug\logs\inference_log_YYYY-MM-DD.xlsx
   ```

2. **驗證列數**: 應該有 **33列**

3. **檢查新列**:
   - `unity_send_ts`: 應該有數值（如 5.234567）
   - `unity_receive_ts`: 應該有數值
   - `unity_display_ts`: Displayed frames 有數值
   - `unity_drop_ts`: Dropped frames 有數值
   - `final_state`: "Displayed" 或 "Dropped"
   - `drop_reason`: dropped frames 應該是 "superseded_by_newer_X"

4. **檢查LEGACY列**:
   - `freeze_frames_LEGACY`: 應該都是 0
   - `freeze_ratio_LEGACY`: 應該都是 0
   - `new_frozen_LEGACY`: 應該都是 0

✅ **驗證**: Excel有新架構的所有列，並且數據正確填充

---

## 測試場景

### 場景A: 正常並行處理（5 FPS）

**預期**:
- Pending requests: 2-4個同時進行
- Dropped frames: 10-20%（這是正常的！）
- Failed frames: 0%
- E2E latency: 150-250ms

**如果看到**:
```
[PARALLEL] Total pending: 1
[PARALLEL] Total pending: 1
[PARALLEL] Total pending: 1
```
⚠️ **問題**: Server太快，沒有真正並行（這也沒關係，說明server很快）

---

### 場景B: Server慢（模擬高負載）

如果server處理很慢：

**預期**:
- Pending requests: 10+個同時進行
- Dropped frames: 30-50%（正常，因為積壓）
- Completed frames: 5+個等待顯示

**日誌**:
```
[PARALLEL DISPLAY] Frame 5 DROPPED (superseded by 10)
[PARALLEL DISPLAY] Frame 6 DROPPED (superseded by 10)
[PARALLEL DISPLAY] Frame 7 DROPPED (superseded by 10)
[PARALLEL DISPLAY] Frame 8 DROPPED (superseded by 10)
[PARALLEL DISPLAY] Frame 9 DROPPED (superseded by 10)
[PARALLEL DISPLAY] Frame 10 DISPLAYED. Dropped 5 older frames.
```

✅ **這是好的行為**: 只顯示最新的，丟棄舊的

---

### 場景C: Timeout測試

讓server停止，Unity繼續運行：

**預期（5秒後）**:
```
[TIMEOUT] Frame 15 timed out after 5.1s
[TIMEOUT] Frame 16 timed out after 5.0s
[PERFORMANCE METRICS] ... Failed=2 ...
```

✅ **驗證**: Timeout機制正常工作

---

## 性能基準

### 好的性能指標
```
Pending: 2-4個
Dropped rate: 10-20%
Failed rate: 0%
E2E latency: 150-250ms
```

### 需要優化的指標
```
Pending: 10+個持續增長
Dropped rate: 40%+
Failed rate: 5%+
E2E latency: 500ms+
```

如果性能不佳，考慮：
1. 減少worker數量（從4降到2）
2. 降低Unity發送FPS（從5降到2）
3. 使用更小的模型
4. 檢查網絡連接

---

## 與串行模式對比

### 測試串行模式

**停止server，重啟為單worker**:
```bash
start_server.bat 1
```

**預期行為**:
- Unity仍然並行發送請求
- Server串行處理
- Pending requests會更多
- Dropped frames會更多（因為server慢）

**對比表**:
| 指標 | 串行(1 worker) | 並行(4 workers) |
|------|---------------|----------------|
| Max throughput | 20 FPS | 80 FPS |
| Pending (avg) | 5-8 | 2-4 |
| Drop rate | 30% | 15% |
| Latency variance | Low | Medium |

---

## Troubleshooting

### 問題1: 沒有看到並行請求
```
[PARALLEL] Total pending: 1
[PARALLEL] Total pending: 1  ← 總是1個
```

**原因**:
- Unity發送太慢（5 FPS = 每200ms一個）
- Server處理太快（< 200ms）
- 請求來不及積壓

**解決**:
- 這實際上是**好現象**（server很快）
- 如果想看並行，可以臨時增加Unity發送FPS到10-20

### 問題2: Unity沒有顯示skeleton
**檢查**:
1. Server有回傳detections嗎？（查看server日誌）
2. `final_state` 是 "Displayed" 嗎？
3. Unity Console有錯誤嗎？

### 問題3: Excel沒有新列
**原因**: 使用舊版 `inference_logger.py`

**解決**:
```bash
cd C:\Repo\Github\vision_server
git pull  # 確保最新代碼
```

### 問題4: Server啟動失敗
```
ERROR: Address already in use
```

**解決**:
```bash
# Windows
netstat -ano | findstr :8001
taskkill /PID <PID> /F

# Linux/Mac
lsof -i :8001
kill -9 <PID>
```

---

## 成功標準

✅ **所有測試通過的標誌**:

1. Unity Console顯示並行請求（Pending > 1）
2. 看到 "DROPPED (superseded by...)" 日誌
3. 看到 "DISPLAYED. Dropped X older frames" 日誌
4. 每10秒輸出 `[PERFORMANCE METRICS]`
5. Excel有33列
6. `final_state` 列正確填充
7. `freeze_frames_LEGACY` 列都是0
8. Server顯示4個worker進程
9. Skeleton正常渲染
10. 沒有Unity錯誤或崩潰

---

## 下一步

測試通過後：
1. 在Quest 3實際設備上測試
2. 收集長時間運行數據
3. 分析Excel日誌
4. 根據性能調整worker數量
5. 考慮未來優化（WebSocket, gRPC等）

**測試報告模板**:
```
測試日期: YYYY-MM-DD
Unity版本: 2022.X.X
Worker數量: 4
測試時長: 10分鐘
Total frames: 3000
Displayed: 2500
Dropped: 450 (15%)
Failed: 0 (0%)
Avg E2E: 187ms
Avg Pending: 3.2
結論: ✅ 通過 / ❌ 失敗
```
