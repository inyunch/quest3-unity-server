# Deployment Checklist - Parallel Processing Migration

## 📋 部署前檢查清單

### Unity側檢查

- [ ] `FrameTrace.cs` 已添加到項目
- [ ] `FrameTrace.cs.meta` 文件存在
- [ ] `PoseInferenceRunManager.cs` 已更新（所有6個階段）
- [ ] 移除了 `m_inferenceInProgress` 檢查
- [ ] 添加了 `Update()` 方法和 `TryDisplayNewestFrame()`
- [ ] 添加了 `CleanupOldFrames()` 和 `CheckFrameTimeouts()`
- [ ] Unity編譯無錯誤
- [ ] 在Editor Play Mode測試通過

### Server側檢查

- [ ] `app/main.py` 已更新支持multi-worker
- [ ] `debug/inference_logger.py` 已更新新架構
- [ ] `start_server.bat` 和 `start_server.sh` 已創建
- [ ] 啟動腳本已設置為可執行（Linux/Mac）
- [ ] Python環境依賴完整
- [ ] Server能以4 workers啟動
- [ ] Server能以1 worker啟動（開發模式）

### 文檔檢查

- [ ] `PARALLEL_MIGRATION_PROPOSAL.md` 已創建
- [ ] `PARALLEL_PROCESSING_IMPLEMENTATION_COMPLETE.md` 已創建
- [ ] `PARALLEL_PROCESSING_GUIDE.md` 已創建（Server）
- [ ] `QUICK_TEST_GUIDE.md` 已創建
- [ ] `DEPLOYMENT_CHECKLIST.md` 已創建（本文件）

### 功能驗證

- [ ] Unity可以並行發送多個請求
- [ ] Unity只顯示最新完成的frame
- [ ] 舊frames被正確標記為Dropped
- [ ] Server可以同時處理多個請求
- [ ] Excel日誌有新的33列架構
- [ ] `final_state` 列正確記錄
- [ ] `freeze_frames_LEGACY` 列都是0
- [ ] Timeout機制正常工作（5秒）
- [ ] Frame cleanup正常工作（限制100個）
- [ ] Performance metrics每10秒輸出

---

## 🔍 測試檢查清單

### 基礎功能測試

- [ ] Server啟動（4 workers）無錯誤
- [ ] Unity Scene啟動無錯誤
- [ ] Skeleton正常渲染
- [ ] Console日誌正常（無紅色錯誤）
- [ ] Excel文件自動生成
- [ ] Excel有33列

### 並行處理測試

- [ ] Console顯示 `Total pending: 2+`（並行證明）
- [ ] Console顯示 "DROPPED (superseded by...)"
- [ ] Console顯示 "DISPLAYED. Dropped X older frames"
- [ ] `[PERFORMANCE METRICS]` 每10秒輸出
- [ ] Dropped rate合理（10-30%）

### 性能測試

- [ ] E2E延遲在預期範圍（150-300ms）
- [ ] 沒有內存泄漏（長時間運行）
- [ ] Frame traces數量不超過100
- [ ] Pending requests不會無限增長
- [ ] Failed frames很少（< 5%）

### Excel日誌測試

- [ ] 每個displayed frame有一行
- [ ] `unity_send_ts` 有數值
- [ ] `unity_receive_ts` 有數值
- [ ] `unity_display_ts` 有數值（displayed frames）
- [ ] `unity_drop_ts` 有數值（dropped frames）
- [ ] `server_receive_ts` 有數值
- [ ] `server_send_ts` 有數值
- [ ] `final_state` 正確（Displayed/Dropped）
- [ ] `drop_reason` 正確填充
- [ ] Legacy列都是0

---

## 🚀 部署流程

### Step 1: 備份現有代碼
```bash
# Unity
cd C:\Users\user\Unity-PassthroughCameraApiSamples
git add -A
git commit -m "Backup before parallel processing migration"

# Server
cd C:\Repo\Github\vision_server
git add -A
git commit -m "Backup before parallel processing migration"
```

### Step 2: 部署Server端
```bash
cd C:\Repo\Github\vision_server

# 測試單worker啟動
start_server.bat 1

# 測試多worker啟動
start_server.bat 4

# 驗證進程數
tasklist | findstr python
# 應該看到5個進程
```

### Step 3: 部署Unity端
1. 打開Unity項目
2. 等待編譯完成
3. 檢查Console無錯誤
4. 測試Play Mode
5. 驗證功能正常

### Step 4: 運行完整測試
參考 `QUICK_TEST_GUIDE.md` 執行所有測試

### Step 5: 收集基準數據
運行10分鐘，收集：
- Total frames
- Displayed frames
- Dropped frames
- Failed frames
- Avg E2E latency
- Avg pending count
- Drop rate

### Step 6: Build和部署到Quest 3
```
1. Unity: File → Build Settings → Build
2. 安裝APK到Quest 3
3. 在Quest 3上測試
4. 收集實際設備數據
```

---

## 🔧 配置建議

### 開發環境
```bash
# Server: 1 worker（hot-reload）
start_server.bat 1

# Unity: Editor Play Mode
# Target FPS: 5
```

### 測試環境
```bash
# Server: 2 workers
start_server.bat 2

# Unity: Build to Quest 3
# Target FPS: 5
```

### 生產環境
```bash
# Server: 4 workers
start_server.bat 4

# Unity: Optimized Build to Quest 3
# Target FPS: 5-10（根據性能調整）
```

---

## 📊 性能基準

### 正常指標（Quest 3 @ 5 FPS, 4 workers）

| 指標 | 目標範圍 | 警告閾值 |
|------|---------|---------|
| E2E Latency | 150-250ms | > 400ms |
| Pending Requests | 2-4 | > 10 |
| Drop Rate | 10-20% | > 40% |
| Failed Rate | 0-2% | > 5% |
| Memory (Unity) | < 2GB | > 3GB |
| GPU Memory (Server) | 2-3GB | > 5GB |

### 異常情況處理

| 異常 | 可能原因 | 解決方案 |
|------|---------|---------|
| Pending > 20 | Server太慢 | 減少worker或降低FPS |
| Drop Rate > 50% | Server太慢 | 增加worker或優化模型 |
| Failed Rate > 10% | 網絡問題 | 檢查連接，增加timeout |
| Memory增長 | 內存泄漏 | 檢查cleanup邏輯 |
| No parallel | Unity太慢 | 增加Unity FPS |

---

## 🔄 回滾計劃

### 如果需要緊急回滾

**Server端**（即時生效）:
```bash
# 回滾到單worker
start_server.bat 1
```

**Unity端**（需要重新build）:
```bash
git revert <commit-hash>
# 或
git checkout <previous-commit>
```

**最小化影響回滾**:
- 只回滾Server到單worker
- Unity代碼保持新版本（向後兼容）
- 系統會變回串行，但仍可工作

---

## ✅ 部署簽核

### 開發團隊簽核
- [ ] Unity工程師確認代碼正確
- [ ] Python工程師確認server配置正確
- [ ] QA確認測試通過
- [ ] Tech Lead審核架構變更

### 部署後驗證（24小時內）
- [ ] 監控Excel日誌是否正常生成
- [ ] 監控drop rate是否在預期範圍
- [ ] 監控failed rate是否< 5%
- [ ] 監控server CPU/Memory使用率
- [ ] 收集用戶反饋

### 長期監控（1週）
- [ ] 每日檢查Excel日誌
- [ ] 分析性能趨勢
- [ ] 收集drop rate統計
- [ ] 評估是否需要調整worker數量
- [ ] 評估是否需要進一步優化

---

## 📞 聯繫信息

**技術負責人**: Claude Code
**實施日期**: 2026-04-14
**版本**: 1.0.0

**問題報告**:
- Unity問題: 檢查 `Unity Editor Console`
- Server問題: 檢查 `vision_server/logs/`
- 性能問題: 分析 `debug/logs/inference_log_*.xlsx`

**緊急聯繫**: 如有嚴重問題，立即回滾並報告

---

## 🎉 部署成功標誌

當看到以下情況，表示部署成功：

1. ✅ Server顯示4個worker進程
2. ✅ Unity Console顯示並行請求
3. ✅ Skeleton正常渲染
4. ✅ Excel日誌正常生成（33列）
5. ✅ Drop rate在10-30%範圍
6. ✅ Failed rate < 5%
7. ✅ 無內存泄漏
8. ✅ 無Unity錯誤
9. ✅ 性能指標正常
10. ✅ 用戶體驗良好

**恭喜！並行處理架構部署成功！** 🎊
