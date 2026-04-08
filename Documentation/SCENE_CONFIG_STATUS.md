# Scene Configuration Status Report

**日期**: 2026-04-07 23:10
**檢查項目**: 三個 Inference 場景的配置狀態

---

## 🔍 檢查結果

### ✅ DepthEstimation.unity - 完美配置

```yaml
場景位置: Assets/PassthroughCameraApiSamples/DepthEstimation/DepthEstimation.unity
大小: 49KB
創建時間: 2026-04-07 23:03

配置:
  m_inferenceConfig:
    baseUrl: http://192.168.0.135:8001/infer_human  ✅ 正確
    mode: 3 (DepthEstimation)                        ✅ 正確
    targetFPS: 5                                     ✅ 正確
    jpegQuality: 80                                  ✅ 正確
    includeMask: false                               ✅ 正確
    includeDepth: false                              ✅ 正確

組件:
  ✅ DepthInferenceManager (GameObject)
    ├─ ✅ PassthroughCameraAccess
    ├─ ✅ DetectionUiMenuManager
    ├─ ✅ DepthInferenceRunManager
    ├─ ✅ SharedInferenceHUD
    └─ ✅ DepthVisualization

  ✅ DepthVisualizationCanvas
    └─ Panel
        ├─ ✅ DepthDisplay (RawImage)
        ├─ ✅ CenterDepthText (TMP)
        └─ ✅ MetricsText (TMP)

狀態: ✅ 可以直接使用
```

---

### ❌ MultiObjectDetection.unity - 需要更新

```yaml
場景位置: Assets/PassthroughCameraApiSamples/MultiObjectDetection/MultiObjectDetection.unity

問題:
  ❌ 使用舊的 m_serverUrl 配置方式
  ❌ IP 地址錯誤: 35.9.28.119 (應該是 192.168.0.135)
  ❌ 沒有使用新的 InferenceConfig 系統

當前配置:
  m_serverUrl: http://35.9.28.119:8001/infer_human?mode=detection
               ^^^^^^^^^^^^ 錯誤的 IP！

應該改為:
  m_inferenceConfig:
    baseUrl: http://192.168.0.135:8001/infer_human
    mode: 0 (ObjectDetection)
    targetFPS: 10
    jpegQuality: 80

狀態: ❌ 需要更新才能使用
```

---

### ❌ PassthroughPoseEstimation.unity - 需要更新

```yaml
場景位置: Assets/PassthroughCameraApiSamples/PoseEstimation/PassthroughPoseEstimation.unity

問題:
  ❌ 使用舊的 m_serverUrl 配置方式
  ❌ IP 地址錯誤: 35.9.28.119 (應該是 192.168.0.135)
  ❌ 沒有使用新的 InferenceConfig 系統

當前配置:
  m_serverUrl: http://35.9.28.119:8001/infer_human?mode=both
               ^^^^^^^^^^^^ 錯誤的 IP！

應該改為:
  m_inferenceConfig:
    baseUrl: http://192.168.0.135:8001/infer_human
    mode: 2 (Both/PoseEstimation)
    targetFPS: 5
    jpegQuality: 80

狀態: ❌ 需要更新才能使用
```

---

## 🔧 修復方法

### 方法一：自動修復（推薦）⚡

**在 Unity Editor 中執行**:

```
Tools → PassthroughCameraApiSamples → Update All Scene Configs
```

這會自動：
1. 打開 MultiObjectDetection 場景
2. 更新為使用 InferenceConfig (mode=ObjectDetection, 10 FPS)
3. 設定正確的 IP (192.168.0.135)
4. 保存場景

5. 打開 PoseEstimation 場景
6. 更新為使用 InferenceConfig (mode=Both, 5 FPS)
7. 設定正確的 IP (192.168.0.135)
8. 保存場景

9. 驗證 DepthEstimation 場景配置
10. 顯示完成報告

**執行後驗證**:
```
Tools → PassthroughCameraApiSamples → Verify All Scene Configs
```

---

### 方法二：手動修復

#### MultiObjectDetection 場景

1. 打開場景: `Assets/PassthroughCameraApiSamples/MultiObjectDetection/MultiObjectDetection.unity`

2. 找到有 `SentisInferenceRunManager` 組件的 GameObject

3. 在 Inspector 中找到 `m_inferenceConfig` 欄位

4. 設定如下:
   ```
   m_inferenceConfig:
     baseUrl: http://192.168.0.135:8001/infer_human
     mode: ObjectDetection
     targetFPS: 10
     jpegQuality: 80
     includeMask: false
     includeDepth: false
   ```

5. 保存場景 (Ctrl+S)

#### PoseEstimation 場景

1. 打開場景: `Assets/PassthroughCameraApiSamples/PoseEstimation/PassthroughPoseEstimation.unity`

2. 找到有 `PoseInferenceRunManager` 組件的 GameObject

3. 在 Inspector 中找到 `m_inferenceConfig` 欄位

4. 設定如下:
   ```
   m_inferenceConfig:
     baseUrl: http://192.168.0.135:8001/infer_human
     mode: Both (或 PoseEstimation)
     targetFPS: 5
     jpegQuality: 80
     includeMask: false
     includeDepth: false
   ```

5. 保存場景 (Ctrl+S)

---

## 📊 三個模式的正確配置

| 場景 | Mode | Target FPS | IP | URL |
|------|------|------------|-----|-----|
| **MultiObjectDetection** | ObjectDetection (0) | 10 | 192.168.0.135 | http://192.168.0.135:8001/infer_human |
| **PassthroughPoseEstimation** | Both (2) | 5 | 192.168.0.135 | http://192.168.0.135:8001/infer_human |
| **DepthEstimation** | DepthEstimation (3) | 5 | 192.168.0.135 | http://192.168.0.135:8001/infer_human |

**注意**:
- Mode 參數會自動添加到 URL: `?mode=detection`, `?mode=both`, `?mode=depth`
- InferenceConfig 會自動生成完整 URL，不需要手動添加參數

---

## ✅ 更新後的驗證步驟

執行自動更新後，執行以下步驟驗證:

### 1. 檢查 Console 輸出

應該看到:
```
[Scene Config Updater] ✅ MultiObjectDetection scene saved successfully!
[Scene Config Updater] ✅ PoseEstimation scene saved successfully!
[Scene Config Updater] ✅ DepthEstimation scene config is correct!
```

### 2. 手動檢查每個場景

打開每個場景，檢查 Inspector:

**MultiObjectDetection**:
- 找到 SentisInferenceRunManager
- 檢查 m_inferenceConfig.baseUrl = http://192.168.0.135:8001/infer_human
- 檢查 m_inferenceConfig.mode = ObjectDetection
- 檢查 m_inferenceConfig.targetFPS = 10

**PassthroughPoseEstimation**:
- 找到 PoseInferenceRunManager
- 檢查 m_inferenceConfig.baseUrl = http://192.168.0.135:8001/infer_human
- 檢查 m_inferenceConfig.mode = Both
- 檢查 m_inferenceConfig.targetFPS = 5

**DepthEstimation**:
- 找到 DepthInferenceRunManager
- 檢查 m_inferenceConfig.baseUrl = http://192.168.0.135:8001/infer_human
- 檢查 m_inferenceConfig.mode = DepthEstimation
- 檢查 m_inferenceConfig.targetFPS = 5

### 3. Build and Run 測試

1. 啟動 Server:
   ```bash
   cd C:\Repo\Github\vision_server
   conda activate vision_server
   python -m app.main
   ```

2. Build Settings:
   ```
   File → Build Settings
   確認所有三個場景都在列表中
   Build and Run
   ```

3. 在 Quest 3 上測試每個模式

---

## 🎯 預期結果

更新後，所有三個場景應該:
- ✅ 使用正確的 IP (192.168.0.135)
- ✅ 使用新的 InferenceConfig 系統
- ✅ 有正確的 mode 設定
- ✅ 有合適的 targetFPS
- ✅ 可以成功連接到 server
- ✅ 顯示正確的 metrics

---

## 📝 問題排查

### 如果自動更新失敗

檢查 Console 錯誤訊息:
- 確認場景文件存在
- 確認沒有編譯錯誤
- 確認場景沒有被其他程式鎖定

### 如果更新後還是連不上 server

1. 檢查 IP 是否正確 (192.168.0.135)
2. 檢查 server 是否運行
3. 檢查 Quest 3 和 PC 是否在同一網路
4. 使用 `ipconfig` 確認 PC 的實際 IP

---

**工具位置**: `Assets/PassthroughCameraApiSamples/Shared/Editor/SceneConfigUpdater.cs`
**最後更新**: 2026-04-07 23:10
