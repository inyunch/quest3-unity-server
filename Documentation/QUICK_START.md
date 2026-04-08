# Quick Start - Depth Estimation 場景創建

## 最快方式（1 分鐘）⚡

### 在 Unity Editor 中：

```
1. Tools → PassthroughCameraApiSamples → Create Depth Estimation Scene
2. 等待完成對話框
3. 完成！
```

場景會自動創建在：
`Assets/PassthroughCameraApiSamples/DepthEstimation/DepthEstimation.unity`

---

## 驗證場景

```
Tools → PassthroughCameraApiSamples → Validate Depth Scene Setup
```

檢查 Console，所有組件應該顯示 ✅

---

## 測試流程

### 1. 啟動 Server（在 PC 上）

```bash
cd C:\Repo\Github\vision_server
conda activate vision_server
python -m app.main
```

等待看到：`[API] Depth estimation available: True`

### 2. Build and Run（在 Unity 中）

```
File → Build Settings
Platform: Android
Build and Run
```

### 3. 在 Quest 3 上測試

你應該看到：
- ✅ Passthrough 背景
- ✅ 彩色深度圖（左側）
- ✅ 中心深度值（右上）
- ✅ Metrics HUD（右下）

---

## 預期 Metrics

```
Depth Estimation
Inference FPS: ~4-5 (target: 5.0)
E2E: ~300ms
  Upload: ~30ms
  Server: ~85ms
  Download: ~150ms
  Parse: ~35ms

Upload: 85 KB
Download: ~300 KB
```

---

## 如果自動創建失敗

查看詳細手動步驟：
`Documentation/DEPTH_SCENE_SETUP_GUIDE.md`

---

## 三種 Inference 模式

| 模式 | 場景 | Target FPS | E2E Latency | 用途 |
|------|------|------------|-------------|------|
| **Object Detection** | MultiObjectDetection.unity | 10 | ~220ms | 物體偵測 |
| **Pose Estimation** | PassthroughPoseEstimation.unity | 5 | ~320ms | 人體姿態 |
| **Depth Estimation** | DepthEstimation.unity | 5 | ~300ms | 深度估計 |

---

## 常見問題

**Q: Server connection failed?**
A: 確認 IP 是否正確（目前設定為 192.168.0.135）

**Q: Depth map 不顯示?**
A: 執行 Validate 檢查組件連接

**Q: 編譯錯誤?**
A: 等待 Unity 編譯完成，檢查 Console

---

**需要詳細說明？** → `Documentation/DEPTH_SCENE_SETUP_GUIDE.md`
