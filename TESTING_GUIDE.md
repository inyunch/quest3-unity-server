# 統一 Segmentation 測試指南

## 🚀 快速測試步驟

### 步驟 1: 啟動 Server (必需)

打開終端並執行:

```bash
cd C:\Repo\Github\vision_server
python -m uvicorn app.main:app --host 0.0.0.0 --port 8001 --reload
```

**預期輸出**:
```
INFO:     Uvicorn running on http://0.0.0.0:8001 (Press CTRL+C to quit)
INFO:     Started reloader process
INFO:     Started server process
INFO:     Waiting for application startup.
INFO:     Application startup complete.
```

✅ **驗證**: 瀏覽器打開 `http://localhost:8001` 應該看到 "AI Inference Server Running"

---

### 步驟 2: 在 Unity 中自動配置 Scene

**方法 A: 使用自動配置工具 (推薦)**

1. 打開 Unity Editor
2. 打開 `Segmentation.unity` scene
3. 點擊菜單: **Tools → Configure Segmentation Scene (Simple)**
4. 等待自動配置完成
5. 看到成功對話框後，**保存場景 (Ctrl+S)**

**方法 B: 手動配置**

如果自動配置不work，請手動設置:

1. 打開 `Segmentation.unity` scene
2. 創建新的 GameObject，命名為 `SimpleSegmentationManager`
3. Add Component → `SimpleSegmentationManager`
4. 在 Inspector 中設置:

```
SimpleSegmentationManager
├─ Core References
│  ├─ Camera Access: [拖入 PassthroughCameraAccess GameObject]
│  └─ Ui Menu Manager: [拖入 DetectionUiMenuManager，可選]
│
├─ Rendering
│  └─ Renderer 3D: [拖入 Segmentation3DRenderer GameObject]
│
├─ UI Display
│  └─ Shared HUD: [拖入 SharedInferenceHUD GameObject]
│
└─ Unified Server Inference
   ├─ Use Server Config: ✓
   ├─ Mode: SegmentationWithDepth
   ├─ Target FPS: 5
   ├─ JPEG Quality: 80
   ├─ Include Mask: false (自動強制為 true)
   └─ Include Depth: false (自動強制為 true)
```

5. 如果沒有 Segmentation3DRenderer，創建新 GameObject:
   - 命名為 `Segmentation3DRenderer`
   - Add Component → `Segmentation3DRenderer`
   - 設置 Camera Rig reference

6. 如果沒有 SharedInferenceHUD，創建新 GameObject:
   - 在 Canvas 下創建 `SharedInferenceHUD`
   - Add Component → `SharedInferenceHUD`
   - 創建 TextMeshPro UI element 作為 Metrics Text
   - 設置 Show Detailed Metrics: ✓

7. **保存場景 (Ctrl+S)**

---

### 步驟 3: Build and Run

#### 選項A: Build to Quest 3 (完整測試)

1. 確保 Quest 3 通過 USB 連接並開啟 Developer Mode
2. File → Build Settings
3. 選擇 Android platform
4. 確認 Segmentation scene 在 "Scenes In Build" 中
5. 點擊 "Build And Run"
6. 等待 build 完成並自動安裝到 Quest 3

#### 選項B: Play in Editor (快速測試，無攝像頭)

1. 直接點擊 Play 按鈕
2. 查看 Console 輸出
3. **注意**: Editor 模式無法訪問 Quest 3 攝像頭，但可以驗證代碼邏輯

---

### 步驟 4: 驗證測試結果

#### A. Console 輸出檢查

**Unity Console 應該顯示**:

```
[SEG SIMPLE] SimpleSegmentationManager started
[SEG REF] cameraAccess=True
[SEG REF] uiMenuManager=True
[SEG REF] renderer3D=True
[SEG REF] sharedHUD=True

[InferenceConfig] === Configuration Summary ===
[InferenceConfig] Using ServerConfig: True (IP: 192.168.0.135)
[InferenceConfig] URL: http://192.168.0.135:8001/infer_human?mode=seg_depth&include_mask=true&include_depth=true
[InferenceConfig] Mode: Segmentation + Depth (seg_depth)
[InferenceConfig] Target FPS: 5.0 (200ms interval)
[InferenceConfig] JPEG Quality: 80
[InferenceConfig] Expected Download: ~0.4MB
[InferenceConfig] Include Mask: true (forced)
[InferenceConfig] Include Depth: true (forced)

[SEG SERVER TEST] Connecting to http://192.168.0.135:8001
[SEG SERVER TEST] ✓ Connection OK! Response: {"message":"AI Inference Server Running"}

[SEG SEND] Sending frame 1 to server...
[SEG] Encoded JPEG (quality=80): 75240 bytes (1280x720)
[SEG] Server URL: http://192.168.0.135:8001/infer_human?mode=seg_depth&include_mask=true&include_depth=true

[SEG] Request completed. Result: Success
[SEG RECV] Response received, length=85432
[SEG JSON] Extracted segmentation: 1245 chars
[SEG JSON] Segmentation parsed successfully
[SEG JSON] num_instances=3
[SEG JSON] mask_width=1280
[SEG JSON] mask_height=720
[SEG JSON] mask_png_base64 length=124583
[SEG JSON] Processing time: 187.5ms

[SEG TIMING] E2E=195ms (upload=42ms server=138ms download=12ms parse=3ms)
[SEG BYTES] Upload=75KB Download=83KB (compressed), 85KB (uncompressed), 1.02x compression

[SEG RENDER] Rendering segmentation mask: 3 instances
```

✅ **成功標誌**:
- Connection OK
- Request completed: Success
- num_instances > 0
- Segmentation parsed successfully

❌ **錯誤標誌**:
- Connection FAILED → 檢查 server 是否啟動
- Request failed → 檢查網絡連接和 IP 地址
- num_instances=0 → 檢查攝像頭視野中是否有物體

#### B. Server Console 輸出檢查

**Server Console 應該顯示**:

```
INFO:     Started server process [12345]
INFO:     Waiting for application startup.
INFO:     Application startup complete.

[API] POST /infer_human?mode=seg_depth&include_mask=true&include_depth=true
[API] Image received: 1280x720 JPEG (75240 bytes)
[API mode=seg_depth] Running segmentation inference...
[API mode=seg_depth] Running depth estimation for segmentation...
[DEPTH] MiDaS depth estimation: 1280x720 -> normalized depth map
[SEGMENTATION] SAM segmentation: 1280x720 RGB + depth
[SEGMENTATION] Found 3 instances
[API mode=seg_depth] Segmentation complete: 3 instances
[API] Segmentation mask encoded: 124583 bytes, 3 instances
[API] Inference complete in 187.5ms (mode=seg_depth)

INFO:     192.168.0.xxx:xxxxx - "POST /infer_human?mode=seg_depth&include_mask=true&include_depth=true HTTP/1.1" 200 OK
```

✅ **成功標誌**:
- Request received with correct mode
- Segmentation complete with instances
- 200 OK response

#### C. Unity HUD 顯示檢查

在 Quest 3 或 Unity Editor 的 Game view 中，應該看到:

```
┌────────────────────────────────────┐
│ Segmentation + Depth               │
│ Inference FPS: 5.1 (target: 5.0)  │
│ E2E: 187ms                         │
│  ├Upload: 42ms (22%)               │
│  ├Server: 138ms (73%)              │
│  ├Download: 12ms (6%)              │
│  └Parse: 3ms (2%)                  │
│ Detections: 3                      │
│ Upload: 75KB                       │
│ Download: 420KB                    │
│                                    │
│ Frame Stats (15s)                  │
│ Total: 75                          │
│ Dropped: 2 (2.7%)                  │
│ Frozen: 0                          │
└────────────────────────────────────┘
```

✅ **成功標誌**:
- Inference FPS 接近 target (5 FPS)
- E2E latency 在合理範圍 (150-500ms)
- Detections > 0
- Dropped frames < 10%

#### D. 3D Overlay 顯示檢查

在 Quest 3 中，應該看到:
- 彩色的 segmentation overlay 覆蓋在物體上
- 不同 instance 有不同顏色 (紅、綠、藍、黃、洋紅、青)
- Overlay 跟隨物體移動

---

## 🔬 測試不同模式

### 測試 Segmentation Only (RGB)

1. 在 SimpleSegmentationManager Inspector 中:
   - 改變 Mode: `Segmentation`
2. 保存並 Build and Run
3. 觀察 metrics:
   - E2E latency 應該 **更快** (~150-200ms)
   - Download size 應該 **更小** (~150KB)

### 測試 Segmentation + Depth (RGB-D)

1. 在 SimpleSegmentationManager Inspector 中:
   - 改變 Mode: `SegmentationWithDepth`
2. 保存並 Build and Run
3. 觀察 metrics:
   - E2E latency 會 **較慢** (~300-500ms)
   - Download size 會 **較大** (~450KB)
   - Segmentation 精度可能 **更高** (利用 depth 信息)

---

## 📊 性能比較測試

測試所有模式並記錄 metrics:

| Mode | E2E Latency | Upload | Server | Download | Size | Detections |
|------|------------|--------|--------|----------|------|------------|
| Detection | ? | ? | ? | ? | ? | ? |
| Pose | ? | ? | ? | ? | ? | ? |
| Depth | ? | ? | ? | ? | ? | ? |
| **Segmentation** | ? | ? | ? | ? | ? | ? |
| **Seg+Depth** | ? | ? | ? | ? | ? | ? |

填寫表格後可以清楚看到各模式的性能差異！

---

## 🐛 常見問題排查

### 1. "Connection FAILED"

**症狀**: Console 顯示 `[SEG SERVER TEST] ✗ Connection FAILED`

**原因**:
- Server 沒有啟動
- IP 地址錯誤
- 防火牆阻擋

**解決**:
```bash
# 1. 確認 server 正在運行
curl http://192.168.0.135:8001

# 2. 檢查 IP 地址
ipconfig  # Windows
ifconfig  # Mac/Linux

# 3. 更新 ServerConfig
Tools → Passthrough Camera → Server Config Editor
```

### 2. "Segmentation mask is null"

**症狀**: Console 顯示 `num_instances=0` 或 `mask_png_base64 length=0`

**原因**:
- 攝像頭視野中沒有物體
- SAM model 沒有偵測到任何東西
- Server 端錯誤

**解決**:
1. 確保攝像頭前有可見物體 (人、家具等)
2. 檢查 server console 是否有錯誤
3. 確認 SAM model 已加載

### 3. "Metrics not updating"

**症狀**: SharedInferenceHUD 顯示空白或不更新

**原因**:
- SharedInferenceHUD reference 未設置
- TextMeshPro component 缺失

**解決**:
1. 重新運行 Tools → Configure Segmentation Scene
2. 手動檢查 Inspector references
3. 確認 Canvas 和 TextMeshPro 存在

### 4. "Dropped frames > 50%"

**症狀**: Frame Stats 顯示大量 dropped frames

**原因**:
- Network latency 太高
- Target FPS 設置太高
- Server 處理太慢

**解決**:
1. 降低 Target FPS (從 10 降到 5 或 3)
2. 降低 JPEG Quality (從 80 降到 70)
3. 檢查網絡連接品質
4. 確認 server 有足夠資源

### 5. "FPS 太低"

**症狀**: Inference FPS 遠低於 target

**原因**:
- E2E latency 太高 (>200ms)
- Network 太慢
- Server GPU/CPU 不足

**解決**:
1. 使用有線連接 (避免 WiFi)
2. 優化 server (使用 GPU)
3. 降低圖像質量
4. 降低 target FPS 到合理範圍

---

## ✅ 測試檢查清單

在完成測試前，請確認:

- [ ] Server 成功啟動並顯示 "Application startup complete"
- [ ] Unity scene 已正確配置所有 references
- [ ] Connection test 成功 (Console 顯示 "Connection OK")
- [ ] 第一次 inference 請求成功
- [ ] SharedInferenceHUD 顯示實時 metrics
- [ ] 3D overlay 顯示在場景中
- [ ] num_instances > 0 (偵測到物體)
- [ ] E2E latency 在合理範圍 (<500ms)
- [ ] Dropped frames < 10%
- [ ] 測試了 Segmentation 和 SegmentationWithDepth 兩種模式

---

## 🎉 測試成功！

如果所有檢查都通過，恭喜你！統一 Segmentation 整合已經成功運行！

**下一步**:
1. 記錄不同模式的性能數據
2. 調整參數優化性能
3. 嘗試不同場景和物體
4. 導出 metrics 進行分析

**有問題?**
- 查看 Console logs 尋找錯誤訊息
- 檢查 server logs 確認請求處理
- 重新運行自動配置工具
- 參考 `UNIFIED_SEGMENTATION_SETUP.md` 獲取更多細節

Happy Testing! 🚀
