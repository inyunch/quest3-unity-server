# Start Menu Update - AI Inference Modes

**日期**: 2026-04-07
**修改檔案**: `Assets/PassthroughCameraApiSamples/StartScene/Scripts/StartMenu.cs`

---

## 📝 修改內容

### 新增 "AI Inference Modes" 區塊

在 StartScene 的選單中新增了專門的 AI Inference Modes 區塊，將三個 inference 場景集中顯示在左側面板頂部。

### 修改前

選單結構：
```
左側：
  - Passthrough Sample Scenes
    - PassthroughPoseEstimation

中間：
  - Sample Scenes
    - MultiObjectDetection
    - [其他場景]
```

**問題**:
- MultiObjectDetection 和 DepthEstimation 沒有 "Passthrough" 在檔名中
- 三個 inference 模式分散在不同區塊
- 沒有顯示 FPS 資訊

### 修改後

選單結構：
```
左側：
  ╔══════════════════════════╗
  ║ AI Inference Modes       ║
  ╠══════════════════════════╣
  ║ Object Detection (10 FPS)║ ← MultiObjectDetection
  ║ Pose Estimation (5 FPS)  ║ ← PassthroughPoseEstimation
  ║ Depth Estimation (5 FPS) ║ ← DepthEstimation (新！)
  ╠══════════════════════════╣
  ║ Other Passthrough Scenes ║
  ║ [其他 passthrough 場景]  ║
  ╚══════════════════════════╝

右側：
  - Pro Controller Sample Scenes
    - [pro controller 場景]

中間：
  - Sample Scenes
    - [其他場景]
```

**改進**:
- ✅ 三個 inference 模式集中顯示在頂部
- ✅ 友好的顯示名稱
- ✅ 顯示每個模式的 target FPS
- ✅ 明確標示為 "AI Inference Modes"
- ✅ 自動包含新的 DepthEstimation 場景

---

## 🔧 程式碼修改

### 1. 新增 inferenceScenes 列表

```csharp
var inferenceScenes = new List<Tuple<int, string>>();
```

### 2. 場景分類邏輯

```csharp
// Inference modes (detection, pose, depth)
if (path.Contains("MultiObjectDetection") ||
    path.Contains("PoseEstimation") ||
    path.Contains("DepthEstimation"))
{
    inferenceScenes.Add(new Tuple<int, string>(sceneIndex, path));
}
```

### 3. UI 顯示邏輯

```csharp
// Inference Modes section (left pane)
if (inferenceScenes.Count > 0)
{
    _ = uiBuilder.AddLabel("AI Inference Modes", DebugUIBuilder.DEBUG_PANE_LEFT);
    foreach (var scene in inferenceScenes)
    {
        var sceneName = Path.GetFileNameWithoutExtension(scene.Item2);

        // Friendly display names
        if (sceneName.Contains("MultiObjectDetection"))
            sceneName = "Object Detection (10 FPS)";
        else if (sceneName.Contains("PoseEstimation"))
            sceneName = "Pose Estimation (5 FPS)";
        else if (sceneName.Contains("DepthEstimation"))
            sceneName = "Depth Estimation (5 FPS)";

        _ = uiBuilder.AddButton(sceneName, () => LoadScene(scene.Item1), -1, DebugUIBuilder.DEBUG_PANE_LEFT);
    }
}
```

---

## 📊 顯示名稱對應表

| 場景檔案名稱 | 顯示名稱 | Target FPS | 模式 |
|-------------|---------|------------|------|
| **MultiObjectDetection.unity** | Object Detection (10 FPS) | 10 | mode=detection |
| **PassthroughPoseEstimation.unity** | Pose Estimation (5 FPS) | 5 | mode=both |
| **DepthEstimation.unity** | Depth Estimation (5 FPS) | 5 | mode=depth |

---

## ✅ 使用方式

### 在 Quest 3 上

1. **啟動 App**
   - 會先進入 StartScene

2. **看到選單**
   ```
   左側面板頂部會顯示:

   AI Inference Modes
   ┌────────────────────────────┐
   │ Object Detection (10 FPS)  │
   │ Pose Estimation (5 FPS)    │
   │ Depth Estimation (5 FPS)   │ ← 新增！
   └────────────────────────────┘
   ```

3. **選擇模式**
   - 用手柄指向並點擊想要的模式
   - 場景會自動載入

4. **返回選單**
   - 按 ☰ (Menu) 按鈕隨時返回 StartScene

---

## 🎯 優點

### 1. 使用者體驗改善
- 明確的 "AI Inference Modes" 標題
- 一目了然的三個模式選擇
- 顯示 FPS 資訊幫助使用者理解性能差異

### 2. 自動檢測
- 不需要手動配置場景列表
- 新增 inference 場景時自動出現
- 基於場景檔名自動分類

### 3. 可擴展性
- 未來新增其他 inference 模式時
- 只需確保場景名稱包含關鍵字
- 自動會出現在 "AI Inference Modes" 區塊

---

## 🔍 技術細節

### 場景檢測邏輯

StartMenu.cs 會在啟動時：

1. 掃描所有 Build Settings 中的場景
2. 檢查場景路徑是否包含關鍵字：
   - `MultiObjectDetection` → Object Detection
   - `PoseEstimation` → Pose Estimation
   - `DepthEstimation` → Depth Estimation
3. 將匹配的場景添加到 `inferenceScenes` 列表
4. 在左側面板頂部顯示

### 顯示名稱映射

使用字串檢查來映射友好名稱：
```csharp
if (sceneName.Contains("MultiObjectDetection"))
    sceneName = "Object Detection (10 FPS)";
else if (sceneName.Contains("PoseEstimation"))
    sceneName = "Pose Estimation (5 FPS)";
else if (sceneName.Contains("DepthEstimation"))
    sceneName = "Depth Estimation (5 FPS)";
```

### 執行順序

場景在選單中的顯示順序：
1. **AI Inference Modes** (左側頂部)
2. **Other Passthrough Scenes** (左側下方)
3. **Pro Controller Sample Scenes** (右側)
4. **Sample Scenes** (中間)

---

## 📋 測試檢查清單

確認以下項目：

### Build Settings
- [ ] MultiObjectDetection.unity 在 Build Settings 中
- [ ] PassthroughPoseEstimation.unity 在 Build Settings 中
- [ ] DepthEstimation.unity 在 Build Settings 中

### StartScene 測試
- [ ] 啟動 App 後看到 StartScene
- [ ] 左側面板顯示 "AI Inference Modes"
- [ ] 看到三個按鈕：
  - [ ] "Object Detection (10 FPS)"
  - [ ] "Pose Estimation (5 FPS)"
  - [ ] "Depth Estimation (5 FPS)"
- [ ] 點擊每個按鈕都能正確載入對應場景
- [ ] 在任何場景按 ☰ 按鈕能返回 StartScene

---

## 🚀 下一步

1. **更新 Build Settings**
   ```
   File → Build Settings
   確保三個場景都在列表中:
   - StartScene.unity (Index 0)
   - MultiObjectDetection.unity
   - PassthroughPoseEstimation.unity
   - DepthEstimation.unity
   ```

2. **Build and Run**
   ```
   Build and Run 到 Quest 3
   測試選單功能
   ```

3. **驗證所有模式**
   - 從 StartScene 進入每個模式
   - 確認能正常運行
   - 確認能返回 StartScene

---

## 💡 未來改進建議

### 可能的增強功能

1. **顯示當前 Server 狀態**
   ```
   AI Inference Modes
   Server: 192.168.0.135:8001 ✅ Connected
   ```

2. **顯示更多資訊**
   ```
   Object Detection (10 FPS)
   └─ E2E: ~220ms, Bandwidth: ~1 MB/s
   ```

3. **動態 FPS 調整**
   - 允許使用者在選單中調整 target FPS
   - 保存設定到 PlayerPrefs

4. **模式說明**
   - 長按按鈕顯示模式詳細說明
   - 解釋每個模式的用途

---

**修改完成**: ✅
**測試狀態**: 待測試
**文檔更新**: 2026-04-07
