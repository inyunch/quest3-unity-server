# Segmentation Scene 設置檢查清單

## 📋 快速設置步驟 (10 分鐘)

### 前置檢查

- [ ] Unity Editor 已打開
- [ ] 項目已加載: `Unity-PassthroughCameraApiSamples`
- [ ] Console 沒有編譯錯誤

---

## 步驟 1: 打開 Segmentation Scene

1. 在 Project 視窗中找到:
   ```
   Assets/PassthroughCameraApiSamples/Segmentation/Segmentation.unity
   ```

2. 雙擊打開場景

3. 驗證場景已載入: Hierarchy 視窗應該顯示場景內容

---

## 步驟 2: 檢查現有組件

在 Hierarchy 中尋找以下 GameObjects (如果存在):

### 必需組件 (應該已存在)
- [ ] **OVRCameraRig** 或類似的 Camera Rig
- [ ] **PassthroughCameraAccess** (component 或 GameObject)
- [ ] **Canvas** (UI Canvas)

### 可選組件
- [ ] **DetectionUiMenuManager**
- [ ] **Segmentation3DRenderer**
- [ ] **SegmentationInferenceManager** (舊的，可以保留)

---

## 步驟 3: 創建 SimpleSegmentationManager

### 3A. 創建 GameObject

1. 在 Hierarchy 視窗中右鍵
2. 選擇 **Create Empty**
3. 命名為: `SimpleSegmentationManager`

### 3B. 添加 Component

1. 選中 `SimpleSegmentationManager` GameObject
2. 在 Inspector 中點擊 **Add Component**
3. 搜索: `SimpleSegmentationManager`
4. 點擊添加

**驗證**: Inspector 應該顯示 SimpleSegmentationManager component

---

## 步驟 4: 設置 SimpleSegmentationManager References

在 Inspector 中的 `SimpleSegmentationManager` component:

### 4A. Core References

**Camera Access**:
1. 找到欄位: `M Camera Access`
2. 點擊右側的圓圈圖標 (對象選擇器)
3. 在彈出視窗中搜索: `PassthroughCameraAccess`
4. 選擇該 GameObject 或 Component
5. 驗證: 欄位應該顯示 `PassthroughCameraAccess (PassthroughCameraAccess)`

**Ui Menu Manager** (可選):
1. 找到欄位: `M Ui Menu Manager`
2. 如果場景中有 `DetectionUiMenuManager`:
   - 拖入該 GameObject 或 component
3. 如果沒有: 留空

### 4B. Rendering

**Renderer 3D**:

**選項 A: 使用現有的 Segmentation3DRenderer**
1. 在 Hierarchy 中搜索: `Segmentation3DRenderer`
2. 如果找到: 拖入 `M Renderer 3D` 欄位

**選項 B: 創建新的 Segmentation3DRenderer**
1. Hierarchy 右鍵 → Create Empty
2. 命名為: `Segmentation3DRenderer`
3. Add Component → `Segmentation3DRenderer`
4. 在 Segmentation3DRenderer component 中:
   - `M Camera Rig`: 拖入 OVRCameraRig 的 **TrackingSpace** transform
   - 其他設置保持默認
5. 拖入 SimpleSegmentationManager 的 `M Renderer 3D` 欄位

### 4C. UI Display

**Shared HUD**:

**檢查是否已存在**:
1. 在 Hierarchy 中搜索: `SharedInferenceHUD`
2. 如果找到: 跳到步驟 4C-拖入
3. 如果沒有: 繼續創建

**創建 SharedInferenceHUD**:

1. **創建 Canvas** (如果沒有):
   - Hierarchy 右鍵 → UI → Canvas
   - 命名為: `Canvas`
   - 設置 Render Mode: Screen Space - Overlay

2. **創建 HUD GameObject**:
   - 在 Canvas 下右鍵 → Create Empty
   - 命名為: `SharedInferenceHUD`

3. **添加 Component**:
   - 選中 `SharedInferenceHUD`
   - Add Component → `SharedInferenceHUD`

4. **創建 TextMeshPro**:
   - 在 `SharedInferenceHUD` 下右鍵 → UI → Text - TextMeshPro
   - 如果提示 "Import TMP Essentials": 點擊 Import
   - 命名為: `MetricsText`

5. **配置 TextMeshPro**:
   - 選中 `MetricsText`
   - 在 Rect Transform 中:
     - Anchor Presets: 左上角 (Left-Top)
     - Pos X: `10`
     - Pos Y: `-10`
     - Width: `400`
     - Height: `400`
   - 在 TextMeshPro component 中:
     - Font Size: `18`
     - Color: 白色
     - Alignment: 左上對齊
     - Text: 留空 (會自動更新)

6. **連接 Reference**:
   - 選中 `SharedInferenceHUD` GameObject
   - 在 SharedInferenceHUD component 中:
     - `M Metrics Text`: 拖入 `MetricsText` component
     - `M Show Detailed Metrics`: 勾選 ✓

**4C-拖入**:
7. 回到 `SimpleSegmentationManager`
8. `M Shared HUD` 欄位: 拖入 `SharedInferenceHUD` component

---

## 步驟 5: 配置 Inference Config

在 `SimpleSegmentationManager` component 的 **Unified Server Inference** 區域:

### 基本設置
- **Use Server Config**: ✓ 勾選
- **Base Url**: (留空，會自動從 ServerConfig 讀取)
- **Mode**: 選擇 `SegmentationWithDepth` (或 `Segmentation`)
- **Include Mask**: 不勾選 (會自動強制為 true)
- **Include Depth**: 不勾選 (如果 mode=SegmentationWithDepth 會自動強制為 true)
- **JPEG Quality**: `80`
- **Target FPS**: `5`

**驗證**: Inspector 應該顯示所有設置

---

## 步驟 6: 保存場景

1. File → Save Scene (或 Ctrl+S)
2. 確認保存成功

**驗證**: Scene 文件應該沒有 * 標記

---

## 步驟 7: 驗證配置

### 7A. 檢查 Inspector

選中 `SimpleSegmentationManager`，確認所有欄位都已填入:

```
SimpleSegmentationManager
├─ Core References
│  ├─ M Camera Access: PassthroughCameraAccess ✓
│  └─ M Ui Menu Manager: (可選)
├─ Rendering
│  └─ M Renderer 3D: Segmentation3DRenderer ✓
├─ UI Display
│  └─ M Shared HUD: SharedInferenceHUD ✓
└─ Unified Server Inference
   ├─ Use Server Config: ✓
   ├─ Mode: SegmentationWithDepth
   ├─ Target FPS: 5
   └─ JPEG Quality: 80
```

### 7B. 檢查 Console

1. 打開 Console (Window → General → Console)
2. 確認沒有紅色錯誤
3. 如果有警告 (黃色): 檢查是否與 ServerConfig 相關

### 7C. 測試編譯

1. 點擊 Play 按鈕 (或 Ctrl+P)
2. 觀察 Console 輸出:
   ```
   [SEG SIMPLE] SimpleSegmentationManager started
   [SEG REF] cameraAccess=True
   [SEG REF] renderer3D=True
   [SEG REF] sharedHUD=True
   [InferenceConfig] Mode: Segmentation + Depth
   ```
3. 點擊 Stop 停止運行

---

## ✅ 完成檢查清單

配置完成後，確認以下所有項目:

- [ ] SimpleSegmentationManager GameObject 已創建
- [ ] SimpleSegmentationManager component 已添加
- [ ] Camera Access reference 已設置
- [ ] Renderer 3D reference 已設置
- [ ] Shared HUD reference 已設置
- [ ] Inference Config 已配置 (Mode, FPS, Quality)
- [ ] Scene 已保存
- [ ] Console 沒有錯誤
- [ ] Play mode 測試通過 (顯示正確的 log)

---

## 🚀 下一步

配置完成後:

1. **啟動 Server**:
   ```bash
   cd C:\Repo\Github\vision_server
   python -m uvicorn app.main:app --host 0.0.0.0 --port 8001 --reload
   ```

2. **Build and Run**:
   - File → Build Settings
   - 選擇 Android platform
   - 確認 Segmentation scene 在 Scenes In Build 中
   - 連接 Quest 3
   - Build And Run

3. **驗證測試**:
   - 查看 Quest 3 中的 SharedInferenceHUD 顯示
   - 確認 segmentation overlay 顯示
   - 檢查 metrics 更新

---

## 🐛 常見問題

### "找不到 SimpleSegmentationManager component"

**原因**: 編譯錯誤或腳本未編譯

**解決**:
1. Window → General → Console
2. 檢查是否有紅色錯誤
3. 如果有錯誤: 修復後等待重新編譯
4. 如果沒有錯誤: 嘗試重啟 Unity Editor

### "PassthroughCameraAccess 找不到"

**原因**: 場景中沒有該組件

**解決**:
1. 在 Hierarchy 中搜索: `Passthrough`
2. 檢查是否有相關的 GameObject
3. 如果沒有: 這不是正確的 Segmentation scene

### "SharedInferenceHUD 不顯示"

**原因**: TextMeshPro 沒有正確設置

**解決**:
1. 確認 Canvas 存在且 active
2. 確認 MetricsText GameObject 是 Canvas 的子物件
3. 確認 Rect Transform 位置正確 (左上角)
4. Play mode 中檢查 Game view 是否顯示

### "Segmentation3DRenderer 報錯"

**原因**: Camera Rig reference 未設置

**解決**:
1. 選中 Segmentation3DRenderer GameObject
2. 在 component 中設置 M Camera Rig
3. 拖入 OVRCameraRig 的 TrackingSpace transform

---

## 📸 參考截圖位置

理想的 Hierarchy 結構:

```
Segmentation (Scene)
├── OVRCameraRig
│   └── TrackingSpace
│       ├── CenterEyeAnchor
│       └── ...
├── PassthroughCameraAccess (或作為 component)
├── SimpleSegmentationManager
├── Segmentation3DRenderer
├── Canvas
│   └── SharedInferenceHUD
│       └── MetricsText (TextMeshProUGUI)
└── (其他 GameObjects...)
```

完成所有步驟後，你就可以 Build and Run 到 Quest 3 進行測試了！
