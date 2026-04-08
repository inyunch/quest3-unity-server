# Depth Estimation Scene 設定指南

**日期**: 2026-04-07
**目的**: 創建和配置 DepthEstimation.unity 場景

---

## 方法一：自動化創建（推薦）⚡

### 步驟

1. **啟動 Unity Editor**
   ```
   開啟專案: C:\Users\user\Unity-PassthroughCameraApiSamples
   ```

2. **執行自動化腳本**
   ```
   Unity 選單 → Tools → PassthroughCameraApiSamples → Create Depth Estimation Scene
   ```

3. **等待完成**
   - 腳本會自動創建場景結構
   - 自動配置所有組件
   - 自動設定 InferenceConfig
   - 場景會自動保存到: `Assets/PassthroughCameraApiSamples/DepthEstimation/DepthEstimation.unity`

4. **驗證設定**
   ```
   Unity 選單 → Tools → PassthroughCameraApiSamples → Validate Depth Scene Setup
   ```
   - 檢查 Console 確認所有組件都顯示 ✅

5. **完成！**
   - 場景已經可以 Build and Run 到 Quest 3

---

## 方法二：手動創建（如果自動化失敗）

### 前置準備

確認以下檔案已存在：
- ✅ `Assets/PassthroughCameraApiSamples/Shared/Scripts/InferenceConfig.cs`
- ✅ `Assets/PassthroughCameraApiSamples/Shared/Scripts/SharedInferenceHUD.cs`
- ✅ `Assets/PassthroughCameraApiSamples/DepthEstimation/Scripts/DepthInferenceRunManager.cs`
- ✅ `Assets/PassthroughCameraApiSamples/DepthEstimation/Scripts/DepthVisualization.cs`

### 步驟 1: 複製參考場景

1. **在 Unity Project 視窗中**：
   ```
   導航到: Assets/PassthroughCameraApiSamples/MultiObjectDetection/
   ```

2. **複製場景**：
   - 右鍵點擊 `MultiObjectDetection.unity`
   - 選擇 `Duplicate`
   - 重命名為 `DepthEstimation.unity`

3. **移動場景**：
   - 將 `DepthEstimation.unity` 拖曳到
   - `Assets/PassthroughCameraApiSamples/DepthEstimation/` 資料夾

4. **開啟場景**：
   - 雙擊 `DepthEstimation.unity` 開啟

---

### 步驟 2: 修改場景結構

#### 2.1 刪除不需要的組件

在 Hierarchy 視窗中，找到管理器物件（通常叫 `InferenceManager` 或類似名稱）：

1. **移除舊的腳本**：
   - 在 Inspector 中找到 `SentisInferenceRunManager` 組件
   - 右鍵 → Remove Component

2. **移除舊的 HUD**：
   - 移除 `InferenceHUD` 組件（如果有）

#### 2.2 創建 DepthInferenceManager

1. **重命名管理器**：
   - 在 Hierarchy 中選擇管理器物件
   - 重命名為 `DepthInferenceManager`

2. **添加必要組件**：
   - 確保有 `PassthroughCameraAccess` 組件
   - 確保有 `DetectionUiMenuManager` 組件

3. **添加 DepthInferenceRunManager**：
   ```
   點擊 Add Component
   搜尋: DepthInferenceRunManager
   點擊添加
   ```

4. **配置 InferenceConfig**：
   在 Inspector 的 DepthInferenceRunManager 組件中：
   ```
   m_inferenceConfig:
     ├─ mode: DepthEstimation
     ├─ targetFPS: 5
     ├─ jpegQuality: 80
     ├─ baseUrl: http://192.168.0.135:8001/infer_human
     ├─ includeMask: false
     └─ includeDepth: false (會自動被 mode=depth 覆蓋)
   ```

5. **添加 SharedInferenceHUD**：
   ```
   點擊 Add Component
   搜尋: SharedInferenceHUD
   點擊添加
   ```

6. **添加 DepthVisualization**：
   ```
   點擊 Add Component
   搜尋: DepthVisualization
   點擊添加
   ```

---

### 步驟 3: 創建 UI Canvas

#### 3.1 創建 Canvas

1. **創建 Canvas GameObject**：
   ```
   Hierarchy 右鍵 → UI → Canvas
   重命名為: DepthVisualizationCanvas
   ```

2. **配置 Canvas**：
   ```
   Canvas 組件:
     ├─ Render Mode: World Space
     ├─ Event Camera: (拖曳 Main Camera)
     └─ Sorting Layer: Default

   Transform:
     ├─ Position: (0, 1.5, 2)
     ├─ Rotation: (0, 0, 0)
     └─ Scale: (0.01, 0.01, 0.01)

   Rect Transform:
     └─ Width/Height: 800 x 600 (預設)
   ```

#### 3.2 創建 Panel

1. **創建 Panel**：
   ```
   右鍵 DepthVisualizationCanvas → UI → Panel
   重命名為: Panel
   ```

2. **配置 Panel**：
   ```
   Rect Transform:
     ├─ Anchors: Stretch (左上右下都拉到角落)
     └─ Offsets: 全部設為 0

   Image 組件:
     └─ Color: (0, 0, 0, 128) 半透明黑色背景
   ```

#### 3.3 創建 Depth Display (RawImage)

1. **創建 RawImage**：
   ```
   右鍵 Panel → UI → Raw Image
   重命名為: DepthDisplay
   ```

2. **配置 RawImage**：
   ```
   Rect Transform:
     ├─ Anchors:
     │   ├─ Min: (0.1, 0.3)
     │   └─ Max: (0.5, 0.9)
     └─ Offsets: 全部設為 0

   Raw Image:
     ├─ Texture: (留空，會由 DepthVisualization 動態設定)
     └─ Color: White (255, 255, 255, 255)
   ```

#### 3.4 創建 Center Depth Text

1. **創建 TextMeshPro**：
   ```
   右鍵 Panel → UI → Text - TextMeshPro
   重命名為: CenterDepthText
   ```
   - 如果第一次使用 TMP，會彈出導入視窗，點擊 "Import TMP Essentials"

2. **配置 Text**：
   ```
   Rect Transform:
     ├─ Anchors:
     │   ├─ Min: (0.55, 0.7)
     │   └─ Max: (0.9, 0.9)
     └─ Offsets: 全部設為 0

   TextMeshProUGUI:
     ├─ Text: "Center Depth: --"
     ├─ Font Size: 24
     ├─ Alignment: Top Left
     └─ Color: White
   ```

#### 3.5 創建 Metrics Text (SharedInferenceHUD)

1. **創建 TextMeshPro**：
   ```
   右鍵 Panel → UI → Text - TextMeshPro
   重命名為: MetricsText
   ```

2. **配置 Text**：
   ```
   Rect Transform:
     ├─ Anchors:
     │   ├─ Min: (0.55, 0.3)
     │   └─ Max: (0.9, 0.65)
     └─ Offsets: 全部設為 0

   TextMeshProUGUI:
     ├─ Text: "Metrics: --"
     ├─ Font Size: 20
     ├─ Alignment: Top Left
     └─ Color: White
   ```

---

### 步驟 4: 連接組件引用

回到 Hierarchy 中的 `DepthInferenceManager`，在 Inspector 中：

#### 4.1 DepthInferenceRunManager 組件

```
Server Inference:
  ├─ m_cameraAccess: (拖曳同物件的 PassthroughCameraAccess)
  ├─ m_uiMenuManager: (拖曳同物件的 DetectionUiMenuManager)
  └─ m_inferenceConfig: (已在步驟 2.4 配置)

UI Display References:
  ├─ m_sharedHUD: (拖曳同物件的 SharedInferenceHUD)
  └─ m_depthVisualization: (拖曳同物件的 DepthVisualization)
```

#### 4.2 SharedInferenceHUD 組件

```
m_metricsText: (拖曳 Canvas/Panel/MetricsText)
```

#### 4.3 DepthVisualization 組件

```
Display Settings:
  ├─ m_depthDisplay: (拖曳 Canvas/Panel/DepthDisplay 的 RawImage)
  └─ m_centerDepthText: (拖曳 Canvas/Panel/CenterDepthText)

Visualization Settings:
  ├─ m_colormap: Inferno (或選擇其他: Grayscale, Viridis, Turbo)
  ├─ m_minDepthClip: 0
  └─ m_maxDepthClip: 1
```

---

### 步驟 5: 保存場景

1. **保存**：
   ```
   File → Save (Ctrl+S)
   ```

2. **確認路徑**：
   ```
   場景應該保存在:
   Assets/PassthroughCameraApiSamples/DepthEstimation/DepthEstimation.unity
   ```

---

## 驗證檢查清單

完成後，檢查以下項目：

### Hierarchy 結構

```
DepthEstimation (Scene)
├─ [BuildingBlock] Camera Rig
├─ [BuildingBlock] Passthrough
├─ [BuildingBlock] Hand Tracking
├─ DepthInferenceManager
│   ├─ PassthroughCameraAccess (Component)
│   ├─ DetectionUiMenuManager (Component)
│   ├─ DepthInferenceRunManager (Component)
│   │   ├─ m_inferenceConfig.mode = DepthEstimation
│   │   ├─ m_inferenceConfig.targetFPS = 5
│   │   └─ m_inferenceConfig.baseUrl = http://192.168.0.135:8001/infer_human
│   ├─ SharedInferenceHUD (Component)
│   └─ DepthVisualization (Component)
└─ DepthVisualizationCanvas
    └─ Panel
        ├─ DepthDisplay (RawImage)
        ├─ CenterDepthText (TextMeshProUGUI)
        └─ MetricsText (TextMeshProUGUI)
```

### 組件連接

- ✅ DepthInferenceRunManager.m_cameraAccess → PassthroughCameraAccess
- ✅ DepthInferenceRunManager.m_uiMenuManager → DetectionUiMenuManager
- ✅ DepthInferenceRunManager.m_sharedHUD → SharedInferenceHUD
- ✅ DepthInferenceRunManager.m_depthVisualization → DepthVisualization
- ✅ SharedInferenceHUD.m_metricsText → MetricsText (TextMeshProUGUI)
- ✅ DepthVisualization.m_depthDisplay → DepthDisplay (RawImage)
- ✅ DepthVisualization.m_centerDepthText → CenterDepthText (TextMeshProUGUI)

### Inspector 配置

```
InferenceConfig:
  ✅ mode = DepthEstimation
  ✅ targetFPS = 5
  ✅ jpegQuality = 80
  ✅ baseUrl = http://192.168.0.135:8001/infer_human
  ✅ includeMask = false
  ✅ includeDepth = false
```

---

## 測試場景

### 1. Play Mode 測試（需要 Quest 3）

1. **啟動 Server**：
   ```bash
   cd C:\Repo\Github\vision_server
   conda activate vision_server
   python -m app.main
   ```
   - 確認看到: `[API] Depth estimation available: True`

2. **Build and Run**：
   ```
   File → Build Settings
   ├─ Platform: Android
   ├─ Add Open Scenes (確認 DepthEstimation.unity 在列表中)
   └─ Build and Run
   ```

3. **戴上 Quest 3**：
   - 應該看到 Passthrough
   - 看到 Depth 視覺化 (colored depth map)
   - 看到 HUD 顯示 metrics

### 2. 預期行為

**HUD 顯示**：
```
Depth Estimation
Inference FPS: 4.8 (target: 5.0)
E2E: 305ms
  Upload: 30ms
  Server: 85ms
  Download: 152ms
  Parse: 38ms

Upload: 85 KB
Download: 298 KB

Frame Stats (60s)
Total: 300
Dropped: 6 (2.0%)
Frozen: 0 (0.0%)
```

**Depth Display**：
- 應該看到彩色深度圖（近處=深色，遠處=淺色）
- Center Depth 顯示中心像素的深度值

---

## 常見問題排查

### 問題 1: Console 顯示 "Connection FAILED"

**原因**: Server 沒有啟動或 IP 不正確

**解決**:
1. 確認 server 正在運行
2. 檢查 IP 是否正確 (192.168.0.135)
3. 確認 Quest 3 和 PC 在同一網路

### 問題 2: Depth map 不顯示

**原因**: DepthVisualization 引用未連接

**解決**:
1. 檢查 DepthInferenceRunManager.m_depthVisualization 是否連接
2. 檢查 DepthVisualization.m_depthDisplay 是否連接到 RawImage

### 問題 3: HUD 不顯示 metrics

**原因**: SharedInferenceHUD 引用未連接

**解決**:
1. 檢查 DepthInferenceRunManager.m_sharedHUD 是否連接
2. 檢查 SharedInferenceHUD.m_metricsText 是否連接到 TextMeshProUGUI

### 問題 4: 編譯錯誤

**原因**: 腳本未編譯完成

**解決**:
1. 等待 Unity 編譯完成（底部狀態欄不顯示 "Compiling..."）
2. 檢查 Console 是否有編譯錯誤
3. 如果有錯誤，確認所有檔案都已創建且路徑正確

---

## 下一步

完成 DepthEstimation 場景後，你還需要：

1. **更新 MultiObjectDetection 場景**
   - 確認已使用 SharedInferenceHUD
   - 確認已使用 InferenceConfig

2. **更新 PoseEstimation 場景**
   - 確認已使用 SharedInferenceHUD
   - 確認已使用 InferenceConfig

3. **添加到 Build Settings**
   ```
   File → Build Settings → Add Open Scenes
   確保三個場景都在列表中:
   - MultiObjectDetection
   - PassthroughPoseEstimation
   - DepthEstimation
   ```

4. **測試所有三個模式**
   - Detection (10 FPS target)
   - Pose (5 FPS target)
   - Depth (5 FPS target)

---

**文檔版本**: 1.0
**最後更新**: 2026-04-07
**狀態**: 完整指南
