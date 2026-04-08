# Connection Troubleshooting Guide

**日期**: 2026-04-07
**問題**: Unity App 連接不上 Server + DepthEstimation 選項沒顯示

---

## 🔍 問題診斷

### 問題 1: StartScene 沒有顯示 DepthEstimation 選項 ❌

**可能原因**:
- DepthEstimation.unity 沒有加到 Build Settings
- Unity 還沒編譯新的 StartMenu.cs

### 問題 2: Unity App 連接不上 Server ❌

**發現的錯誤 IP**:
- ✅ 正確: `192.168.0.135:8001` (你的筆電)
- ❌ 錯誤: `192.168.0.155:8001` (在場景中發現)
- ❌ 錯誤: `35.9.28.119:8001` (舊的 IP)

**場景檢查結果**:
```
MultiObjectDetection.unity:
  - Found: http://192.168.0.155:8001  ❌ 錯誤!

PoseEstimation.unity:
  - Found: http://192.168.0.155:8001  ❌ 錯誤!
  - Found: http://192.168.0.135:8001  ✅ 部分正確

DepthEstimation.unity:
  - Found: http://192.168.0.135:8001  ✅ 正確
```

### 問題 3: Server Depth Model 載入失敗 ❌

**錯誤訊息**:
```
[Depth Inference] ⚠️  Failed to load MiDaS model:
Compose.__call__() missing 1 required positional argument: 'img'
[API] Depth estimation available: False
```

**根本原因**:
1. 缺少 `timm` 依賴
2. MiDaS transform API 使用不正確

---

## 🔧 修復步驟

### 第一步: 修復 Server Depth Model（在 PC 上）

#### 1.1 停止當前 Server
在 server 視窗按 `Ctrl+C`

#### 1.2 執行自動修復腳本
```cmd
cd C:\Repo\Github\vision_server
fix_depth.bat
```

**腳本會自動**:
- 安裝 `timm` 依賴
- 備份舊文件
- 替換為修復版本

#### 1.3 重啟 Server
```cmd
python -m uvicorn app.main:app --reload --host 0.0.0.0 --port 8001
```

#### 1.4 驗證修復成功
啟動後應該看到:
```
[Depth Inference] Using device: cuda
[Depth Inference] Loading MiDaS depth model: MiDaS_small...
[Depth Inference] MiDaS model and transforms loaded successfully!
[API] Depth estimation available: True  ← 重點！應該是 True
```

---

### 第二步: 修復 Unity 場景 IP（在 Unity Editor 中）

#### 2.1 打開 Unity Editor
```
開啟專案: C:\Users\user\Unity-PassthroughCameraApiSamples
```

#### 2.2 執行 IP 修復工具
```
Unity 選單:
Tools → PassthroughCameraApiSamples → Fix Scene IPs
```

點擊 "Fix All"

#### 2.3 驗證修復
```
Tools → PassthroughCameraApiSamples → Verify Scene IPs
```

應該看到:
```
MultiObjectDetection:
  ✅ Correct (192.168.0.135): X

PoseEstimation:
  ✅ Correct (192.168.0.135): X

DepthEstimation:
  ✅ Correct (192.168.0.135): X

✅ All IPs are correct!
```

---

### 第三步: 更新場景配置（在 Unity Editor 中）

#### 3.1 執行配置更新
```
Tools → PassthroughCameraApiSamples → Update All Scene Configs
```

這會確保所有場景使用 InferenceConfig 而不是舊的硬編碼 URL

#### 3.2 驗證配置
```
Tools → PassthroughCameraApiSamples → Verify All Scene Configs
```

---

### 第四步: 確認 Build Settings（在 Unity Editor 中）

#### 4.1 打開 Build Settings
```
File → Build Settings
```

#### 4.2 確認場景列表
應該包含（按順序）:
```
✅ StartScene.unity (Index 0)
✅ MultiObjectDetection.unity
✅ PassthroughPoseEstimation.unity
✅ DepthEstimation.unity  ← 確認有！
```

#### 4.3 如果 DepthEstimation.unity 不在列表
1. 點擊 "Add Open Scenes"
2. 或者手動拖曳場景到列表中

---

### 第五步: Build and Run（在 Unity Editor 中）

```
File → Build Settings → Build and Run
```

---

## ✅ 驗證所有修復

### Server 端驗證

1. **Server 啟動成功**:
   ```
   INFO:     Uvicorn running on http://0.0.0.0:8001
   ```

2. **Depth model 可用**:
   ```
   [API] Depth estimation available: True
   ```

3. **測試 endpoint**:
   ```cmd
   curl http://192.168.0.135:8001/
   ```
   應該返回: `{"status":"ok"}`

### Unity 端驗證

1. **StartScene 顯示所有選項**:
   ```
   AI Inference Modes
   - Object Detection (10 FPS)
   - Pose Estimation (5 FPS)
   - Depth Estimation (5 FPS)  ← 應該出現！
   ```

2. **場景配置正確**:
   ```
   Tools → Verify All Scene Configs
   所有場景都顯示 ✅
   ```

3. **IP 正確**:
   ```
   Tools → Verify Scene IPs
   All IPs are correct!
   ```

### Quest 3 端驗證

1. **啟動 App** → 看到 StartScene

2. **看到三個選項** → 包含 Depth Estimation

3. **選擇任一模式** → 能載入場景

4. **檢查 HUD** → 顯示連接狀態

---

## 🐛 如果還是連接失敗

### 檢查清單

#### 網路檢查
- [ ] PC 和 Quest 3 在同一 WiFi 網路
- [ ] PC 的 IP 確實是 192.168.0.135
  ```cmd
  ipconfig | findstr "IPv4"
  ```

#### 防火牆檢查
- [ ] Windows 防火牆允許 port 8001
- [ ] 在 PC 上測試:
  ```cmd
  curl http://192.168.0.135:8001/
  ```

#### Server 檢查
- [ ] Server 正在運行
- [ ] Server 綁定到 0.0.0.0（不是 127.0.0.1）
- [ ] Port 8001 沒被其他程式佔用
  ```cmd
  netstat -ano | findstr :8001
  ```

#### Unity 檢查
- [ ] 場景已重新 build
- [ ] 場景在 Build Settings 中
- [ ] InferenceConfig 有正確的 IP

---

## 📋 快速命令參考

### Server 端 (PC)

```cmd
# 激活環境
conda activate INSS

# 修復 depth model
cd C:\Repo\Github\vision_server
fix_depth.bat

# 啟動 server
python -m uvicorn app.main:app --reload --host 0.0.0.0 --port 8001

# 測試 connection
curl http://192.168.0.135:8001/

# 檢查 port
netstat -ano | findstr :8001
```

### Unity 端 (Unity Editor)

```
# 修復 IPs
Tools → PassthroughCameraApiSamples → Fix Scene IPs

# 更新配置
Tools → PassthroughCameraApiSamples → Update All Scene Configs

# 驗證
Tools → PassthroughCameraApiSamples → Verify Scene IPs
Tools → PassthroughCameraApiSamples → Verify All Scene Configs

# Build
File → Build Settings → Build and Run
```

---

## 📊 正確配置總覽

### Server Configuration
```
Host: 0.0.0.0
Port: 8001
IP (external): 192.168.0.135
Depth Model: MiDaS_small
Depth Available: True
```

### Unity Configuration
```
MultiObjectDetection:
  baseUrl: http://192.168.0.135:8001/infer_human
  mode: ObjectDetection
  targetFPS: 10

PoseEstimation:
  baseUrl: http://192.168.0.135:8001/infer_human
  mode: Both
  targetFPS: 5

DepthEstimation:
  baseUrl: http://192.168.0.135:8001/infer_human
  mode: DepthEstimation
  targetFPS: 5
```

### Build Settings
```
Scenes in Build:
  0: StartScene.unity
  1: MultiObjectDetection.unity
  2: PassthroughPoseEstimation.unity
  3: DepthEstimation.unity
```

---

**最後更新**: 2026-04-07
**狀態**: 待測試修復
