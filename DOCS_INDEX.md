# 📚 Documentation Index

## 🚀 Quick Start (Start Here!)

| Document | Purpose | Time Needed |
|----------|---------|-------------|
| **[DEPLOYMENT_READY.md](./DEPLOYMENT_READY.md)** | Complete deployment checklist for segmentation integration | 5 min read, 20 min setup |
| **[SCENE_SETUP_CHECKLIST.md](./SCENE_SETUP_CHECKLIST.md)** | Step-by-step Unity scene configuration with screenshots | 10 min |
| **[TESTING_GUIDE.md](./TESTING_GUIDE.md)** | Testing procedures, troubleshooting, and validation | 15 min |

---

## 📖 Detailed Guides (Documentation Folder)

### For AI Inference Modes

| Document | Topic | When to Use |
|----------|-------|-------------|
| **[Quick Start Guide](./Documentation/QUICK_START_GUIDE.md)** | 20-minute pose estimation setup | First-time pose estimation setup |
| **[Latency HUD Guide](./Documentation/LATENCY_HUD_GUIDE.md)** | Real-time performance monitoring | Understanding metrics display |
| **[Pose Estimation Technical Guide](./Documentation/POSE_ESTIMATION_TECHNICAL_GUIDE.md)** | Architecture and implementation | Deep dive into pose estimation |
| **[ROI Depth Estimation Guide](./Documentation/ROI_DEPTH_ESTIMATION_GUIDE.md)** | Region-of-interest depth extraction | Extracting depth for specific areas |
| **[Coordinate Transformation Guide](./Documentation/COORDINATE_TRANSFORMATION_GUIDE.md)** | Unity/OpenCV coordinate systems | Understanding coordinate conversions |
| **[Excel Formulas by Mode](./Documentation/EXCEL_FORMULAS_BY_MODE.md)** | Metrics calculation formulas | Analyzing performance data |
| **[Server IP Configuration](./Documentation/SERVER_IP_CONFIGURATION_GUIDE.md)** | Network setup for Quest 3 | Configuring server connection |

---

## 🎯 Recommended Learning Path

### For First-Time Users

1. **Start**: Read [README.md](./README.md) for project overview
2. **Setup**: Follow [DEPLOYMENT_READY.md](./DEPLOYMENT_READY.md) for quick deployment
3. **Configure**: Use [SCENE_SETUP_CHECKLIST.md](./SCENE_SETUP_CHECKLIST.md) for Unity setup
4. **Test**: Follow [TESTING_GUIDE.md](./TESTING_GUIDE.md) to verify everything works

### For Developers

1. **Architecture**: Read [Pose Estimation Technical Guide](./Documentation/POSE_ESTIMATION_TECHNICAL_GUIDE.md)
2. **Metrics**: Study [Latency HUD Guide](./Documentation/LATENCY_HUD_GUIDE.md)
3. **Coordinates**: Understand [Coordinate Transformation Guide](./Documentation/COORDINATE_TRANSFORMATION_GUIDE.md)
4. **Performance**: Analyze using [Excel Formulas by Mode](./Documentation/EXCEL_FORMULAS_BY_MODE.md)

### For Troubleshooting

1. **Validation**: Run Tools → Validate Segmentation Setup in Unity
2. **Common Issues**: See [TESTING_GUIDE.md](./TESTING_GUIDE.md) troubleshooting section
3. **Scene Setup**: Verify using [SCENE_SETUP_CHECKLIST.md](./SCENE_SETUP_CHECKLIST.md)
4. **Network**: Check [Server IP Configuration](./Documentation/SERVER_IP_CONFIGURATION_GUIDE.md)

---

## 🏗️ Architecture Overview

### AI Inference Modes

The project supports **5 unified inference modes**:

| Mode | Description | Scene | Manager Script |
|------|-------------|-------|----------------|
| **Object Detection** | YOLO object detection | MultiObjectDetection | DetectionInferenceRunManager |
| **Pose Estimation** | Human pose tracking | PoseEstimation | PoseInferenceRunManager |
| **Depth Estimation** | MiDaS depth prediction | DepthEstimation | DepthInferenceRunManager |
| **Segmentation** | SAM instance segmentation (RGB) | Segmentation | SimpleSegmentationManager |
| **Seg + Depth** | SAM with depth enhancement (RGB-D) | Segmentation | SimpleSegmentationManager |

### Unified Architecture Components

All AI modes share:
- **InferenceConfig** - Unified configuration system (mode, FPS, quality, etc.)
- **SharedInferenceHUD** - Real-time metrics display (latency, bandwidth, detections)
- **ServerConfig** - Centralized server IP configuration
- **/infer_human** endpoint - Single server endpoint for all modes

This enables **direct performance comparison** across different AI models.

---

## 📊 Performance Expectations

| Mode | E2E Latency | Download Size | Use Case |
|------|-------------|---------------|----------|
| **Object Detection** | ~220ms | ~20KB | Fast object detection |
| **Pose Estimation** | ~290ms | ~20KB | Human pose tracking |
| **Depth Estimation** | ~300ms | ~300KB | Depth map generation |
| **Segmentation** | ~175ms | ~75KB | Fast instance segmentation |
| **Seg + Depth** | ~450ms | ~375KB | Enhanced segmentation with depth |

*Note: Latencies measured on Quest 3 with WiFi 6, local server on same network*

---

## 🛠️ Tools and Utilities

### Unity Editor Tools

| Tool | Menu Path | Purpose |
|------|-----------|---------|
| **Validate Segmentation Setup** | Tools → Validate Segmentation Setup | Check scene configuration before deployment |
| **Project Setup Tool** | Meta → Tools → Project Setup Tool | Fix Meta XR configuration issues |

### Command-Line Tools

See [Tools/README.md](./Tools/README.md) for ADB diagnostic scripts.

---

## 📝 Project Documentation (Meta)

| Document | Purpose |
|----------|---------|
| [CLAUDE.md](./CLAUDE.md) | AI assistant guidelines |
| [CODE_OF_CONDUCT.md](./CODE_OF_CONDUCT.md) | Community guidelines |
| [CONTRIBUTING.md](./CONTRIBUTING.md) | How to contribute |
| [README.md](./README.md) | Project overview and getting started |

---

## 🎓 External Resources

### Official Meta Documentation

- **[Passthrough Camera API Overview](https://developers.meta.com/horizon/documentation/unity/unity-pca-overview)** - Introduction and concepts
- **[Getting Started Guide](https://developers.meta.com/horizon/documentation/unity/unity-pca-documentation)** - Official setup guide
- **[Unity Inference Engine Integration](https://developers.meta.com/horizon/documentation/unity/unity-pca-sentis)** - ML/CV integration
- **[Migration Guide](https://developers.meta.com/horizon/documentation/unity/unity-pca-migration-from-webcamtexture)** - From WebCamTexture

---

## ❓ FAQ

### Which document should I read first?

**If you want to deploy segmentation**: Start with [DEPLOYMENT_READY.md](./DEPLOYMENT_READY.md)

**If you're new to the project**: Start with [README.md](./README.md)

**If you have compilation errors**: See [TESTING_GUIDE.md](./TESTING_GUIDE.md) troubleshooting section

### Where is the server code?

The AI inference server is located at `C:\Repo\Github\vision_server` (not included in this repository).

### How do I add a new inference mode?

1. Extend `InferenceMode` enum in `InferenceConfig.cs`
2. Add server-side handling in `/infer_human` endpoint
3. Create manager script following `SimpleSegmentationManager` pattern
4. Update `SharedInferenceHUD` display names
5. See existing modes for examples

---

## 🔄 Documentation Maintenance

**Last Updated**: 2026-04-09

**Cleanup Status**: ✅ All obsolete documentation removed

**Active Documents**: 7 root-level + 9 in Documentation folder

**Archived**: Old implementation notes and fix summaries have been removed for clarity

---

**Need help? Start with [DEPLOYMENT_READY.md](./DEPLOYMENT_READY.md) for the fastest path to deployment!**
