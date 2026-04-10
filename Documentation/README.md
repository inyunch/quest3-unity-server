# Passthrough Camera API Samples - Documentation

Meta Quest 3 Passthrough Camera API 範例專案文檔

## 專案概述

這個專案展示如何在 Meta Quest 3 上使用 Passthrough Camera API 進行實時 AI 推理，包含三種推理模式：
- **Object Detection** (物體檢測) - 使用 YOLOv8 進行實時物體檢測
- **Pose Estimation** (姿態估計) - 使用 Keypoint R-CNN 進行人體姿態估計
- **Depth Estimation** (深度估計) - 使用 ROI-based MiDaS 進行深度估計

## 架構

```
Quest 3 (Unity) <---> WiFi <---> Python Server (FastAPI + PyTorch)
    |                                    |
    |-- Camera Feed (JPEG) -->           |
    |                                    |-- YOLO / Keypoint R-CNN / MiDaS
    |                                    |
    |<-- Detection/Pose/Depth JSON --    |
    |
    |-- 3D Visualization in AR
```

## 技術棧

### Unity (Quest 3)
- Unity 6000.0.61f1
- Meta XR SDK
- Passthrough Camera API
- C# Networking (UnityWebRequest)
- 3D GameObject Rendering (Object Pooling)

### Python Server
- FastAPI (異步 Web 框架)
- PyTorch + CUDA (GPU 加速)
- YOLOv8n (物體檢測)
- Keypoint R-CNN (姿態估計)
- MiDaS_small (深度估計)
- Uvicorn (ASGI Server)

## 文檔索引

### 快速開始
- **[QUICK_START_GUIDE.md](QUICK_START_GUIDE.md)** - 快速開始指南
- **[SERVER_IP_CONFIGURATION_GUIDE.md](SERVER_IP_CONFIGURATION_GUIDE.md)** - 伺服器 IP 設定指南（一鍵修改所有場景的 IP）

### 技術指南
- **[ROI_DEPTH_ESTIMATION_GUIDE.md](ROI_DEPTH_ESTIMATION_GUIDE.md)** - ROI Depth Estimation 完整技術指南
- **[POSE_ESTIMATION_TECHNICAL_GUIDE.md](POSE_ESTIMATION_TECHNICAL_GUIDE.md)** - Pose Estimation 技術指南
- **[COORDINATE_TRANSFORMATION_GUIDE.md](COORDINATE_TRANSFORMATION_GUIDE.md)** - 座標轉換指南

### 性能與優化
- **[LATENCY_HUD_GUIDE.md](LATENCY_HUD_GUIDE.md)** - Latency HUD 系統使用指南
- **[LATENCY_OPTIMIZATION_GUIDE.md](LATENCY_OPTIMIZATION_GUIDE.md)** - 延遲優化指南
- **[EXCEL_FORMULAS_BY_MODE.md](EXCEL_FORMULAS_BY_MODE.md)** - Excel 指標公式

## 三種推理模式對比

| 模式 | 模型 | 輸出 | 目標 FPS | 特色功能 |
|------|------|------|----------|----------|
| **Object Detection** | YOLOv8n | 2D 檢測框 + 標籤 | 10 FPS | 實時物體識別，80 種類別 |
| **Pose Estimation** | Keypoint R-CNN | 17 個關節點 + 骨架 | 5 FPS | 人體姿態追蹤，3D 可視化 |
| **Depth Estimation** | MiDaS_small + YOLO | 深度點雲 (僅 ROI) | 5 FPS | 物體深度估計，ROI 優化 |

## ROI Depth Estimation 特色

Depth Estimation 模式使用了創新的 **ROI (Region of Interest)** 方法：

1. **兩階段推理**：
   - 第一階段：YOLO 物體檢測（找出所有物體）
   - 第二階段：MiDaS 深度估計（生成全圖深度）

2. **客戶端 ROI 過濾**：
   - Unity 只渲染檢測到物體邊界框內的深度點
   - 大幅減少渲染點數（從數千點降至數百點）
   - 提升性能並聚焦於感興趣的物體

詳見 [ROI_DEPTH_ESTIMATION_GUIDE.md](ROI_DEPTH_ESTIMATION_GUIDE.md)

## API 端點

### `/infer_human` (統一端點)
支援多種模式：
- `mode=detection` - 物體檢測
- `mode=pose` - 姿態估計
- `mode=both` - 檢測 + 姿態
- `mode=depth` - 深度估計

### `/infer_roi_depth` (ROI Depth 專用)
專為 ROI-based 深度估計設計：
- 返回物體檢測結果 + 完整深度圖
- Unity 端進行 ROI 過濾
- 優化的 JSON 響應格式

## 性能指標

### 延遲分解
- **Upload**: ~50-100ms
- **Server**: Detection: 50ms, Pose: 150ms, Depth: 100ms
- **Download**: ~50-150ms
- **Parse**: ~10-30ms
- **Total E2E**: ~200-500ms

### 帶寬使用
- **Upload**: ~20-50 KB (JPEG quality=80)
- **Download**: Detection: ~5 KB, Pose: ~10 KB, Depth: ~150 KB (4x downsampled)

## 授權

Copyright (c) Meta Platforms, Inc. and affiliates.
