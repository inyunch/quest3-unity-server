# Claude AI Assistant Guide for Unity-PassthroughCameraApiSamples

This document provides guidelines for AI assistants (like Claude) working on this project.

---

## Project Overview

This is a **Meta Quest 3/3S** Unity project that demonstrates real-time AI inference using the Passthrough Camera API. The project uses a **client-server architecture**:

- **Unity (Quest 3)**: Captures camera feed, sends to server, renders results in AR
- **Python Server**: Runs AI models (YOLO, Pose Estimation, Depth, Segmentation)

---

## Server Setup Instructions

### Server Location

**IMPORTANT**: The vision_server repository is **NOT** in this repository.

Based on documentation references, it should be located at:
```
C:\Repo\Github\vision_server\
```

If this path doesn't exist, ask the user for the correct server location.

### Starting the Server

**Correct way** (from QUICK_START_GUIDE.md):

```bash
# Navigate to server directory
cd C:\Repo\Github\vision_server

# Activate virtual environment (if using conda)
conda activate vision_server

# Run server
python server.py
# OR
python -m app.main
# OR
uvicorn app.main:app --host 0.0.0.0 --port 8000
```

**Expected output**:
```
INFO:     Started server process
INFO:     Uvicorn running on http://0.0.0.0:8000
```

### Server Endpoints

The server provides these endpoints:

| Endpoint | Purpose | Modes |
|----------|---------|-------|
| `/infer_human` | Unified inference endpoint | detection, pose, both, depth |
| `/segmentation` | RGB-D segmentation (dedicated) | rgb, rgb_d |
| `/infer_roi_depth` | ROI-based depth estimation | N/A |
| `/` | Health check | N/A |

**Query Parameters**:
- `mode`: detection, pose, both, depth
- `include_mask`: true/false (default: false)
- `include_depth`: true/false (default: false)

---

## Code Modification Rules

### 1. Unity C# Code

#### DO:
- ✅ Use `Debug.Log()` for logging (automatically visible in `adb logcat`)
- ✅ Prefix logs with scene/component identifier: `[POSE SERVER]`, `[SEGMENTATION]`, etc.
- ✅ Follow existing naming conventions (PascalCase for public, camelCase with m_ prefix for private fields)
- ✅ Add XML documentation comments `/// <summary>` for public methods
- ✅ Use `[SerializeField]` for private fields that need Inspector visibility
- ✅ Check for null references before using components
- ✅ Use coroutines for network operations
- ✅ Handle UnityWebRequest errors properly

#### DON'T:
- ❌ Use `System.Console.WriteLine()` (won't appear in Quest logs)
- ❌ Create new files unless absolutely necessary (prefer editing existing)
- ❌ Add emojis unless explicitly requested
- ❌ Use `WebClient` or `HttpClient` (use `UnityWebRequest` instead)
- ❌ Block the main thread with synchronous network calls
- ❌ Modify Meta's MRUK package files

### 2. Python Server Code

#### DO:
- ✅ Use type hints for function parameters and returns
- ✅ Use async/await for FastAPI endpoints
- ✅ Log with prefixes: `[POSE]`, `[DEPTH]`, `[SEG]`, etc.
- ✅ Return proper HTTP status codes (200, 400, 500)
- ✅ Include timing metrics in responses (`processing_time_ms`)
- ✅ Validate input images before processing
- ✅ Use try-except blocks for model inference

#### DON'T:
- ❌ Use `print()` for logging (use `logging` module)
- ❌ Hardcode model paths (use config files)
- ❌ Return huge responses (compress or downsample masks/depth maps)
- ❌ Ignore the `include_mask` parameter (respect user settings)

### 3. Scene Modifications

#### DO:
- ✅ Use prefabs when available (don't recreate from scratch)
- ✅ Maintain GameObject hierarchy structure
- ✅ Keep references connected in Inspector
- ✅ Test in Unity Editor before suggesting builds

#### DON'T:
- ❌ Manually edit .unity files (use Unity Editor)
- ❌ Break existing GameObject references
- ❌ Remove components without understanding dependencies

---

## Project Structure

### Unity Scenes

```
Assets/PassthroughCameraApiSamples/
├── StartScene/              # Main menu (select inference mode)
├── PoseEstimation/          # Pose estimation scene
│   └── Scripts/
│       └── PoseInferenceRunManager.cs  # Uses /infer_human?mode=both
├── Segmentation/            # RGB-D segmentation scene
│   └── Scripts/
│       ├── SegmentationInferenceManager.cs  # Uses /segmentation
│       ├── SegmentationOverlayRenderer.cs
│       └── QuestDepthCaptureManager.cs
├── DepthEstimation/         # DEPRECATED (use ROI depth instead)
├── MultiObjectDetection/    # Object detection scene
└── Shared/                  # Shared utilities
    └── Scripts/
        ├── InferenceConfig.cs  # Shared server config
        └── SharedInferenceHUD.cs
```

### Key Files

**Unity**:
- `PoseInferenceRunManager.cs`: Pose estimation + detection (uses `/infer_human`)
- `SegmentationInferenceManager.cs`: Segmentation pipeline (uses `/segmentation`)
- `InferenceConfig.cs`: Server configuration (URL, mode, quality)

**Server** (expected location):
- `app/main.py`: FastAPI app entry point
- `app/routes/infer_human.py`: Main inference endpoint
- `app/routes/segmentation.py`: Segmentation endpoint
- `app/models/`: Pydantic schemas

---

## Unity MCP (Model Context Protocol)

This project has **Unity MCP for Claude** integrated, which provides direct access to Unity Editor state.

### MCP Server Status

**Server URL**: `http://127.0.0.1:8080/mcp`

When Unity is running, you'll see:
```
[04/08/26 22:44:35] INFO Starting MCP server 'mcp-for-unity-server' with transport 'http' on http://127.0.0.1:8080/mcp
```

### Quick Start Example

**User asks**: "Is the Segmentation scene properly configured?"

**Your workflow**:
1. Use MCP: "Check Unity compilation errors"
2. Use MCP: "Show me the Segmentation scene hierarchy"
3. Use MCP: "Inspect SegmentationInferenceManager component"
4. Analyze the results and report to user

**Do NOT**:
- ❌ Ask user to manually check Unity Editor
- ❌ Guess based on code alone
- ✅ Use MCP to get real-time Unity state

### Available MCP Tools

You have access to Unity-specific tools for:

#### 1. **Check Compilation Errors**
Use MCP tools to check for Unity compilation errors without manually reading logs.

**Example queries**:
- "Are there any compilation errors in Unity?"
- "Check the Unity console for errors"
- "What errors exist in the project?"

#### 2. **Inspect Scene Hierarchy**
Use MCP tools to inspect GameObject hierarchy, components, and references.

**Example queries**:
- "Show me the hierarchy of the PoseEstimation scene"
- "What components are attached to SentisInferenceManagerPrefab?"
- "Check if all references are connected in the Segmentation scene"
- "Find GameObjects with missing script references"

#### 3. **Verify Component Configuration**
Check Inspector values and component settings.

**Example queries**:
- "What is the Server URL configured in PoseInferenceRunManager?"
- "Show me the InferenceConfig settings"
- "Is PassthroughCameraAccess properly referenced?"

#### 4. **Scene Validation**
Validate scene setup and dependencies.

**Example queries**:
- "Are all required components present in the scene?"
- "Check for missing prefab connections"
- "Validate the Segmentation scene setup"

### When to Use MCP vs Manual Commands

**Use MCP when**:
- ✅ Checking Unity compilation status
- ✅ Inspecting scene hierarchy
- ✅ Verifying component references
- ✅ Checking Inspector values
- ✅ Finding missing references

**Use manual commands when**:
- ✅ Reading Quest device logs (`adb logcat`)
- ✅ Testing server connectivity (`curl`)
- ✅ Building and deploying (Unity Build Settings)
- ✅ File operations

### MCP Best Practices

1. **Check errors before making changes**:
   ```
   "Check Unity for compilation errors" → Fix errors → Suggest changes
   ```

2. **Verify scene state before modifications**:
   ```
   "Show me the hierarchy" → Understand structure → Propose changes
   ```

3. **Validate references after edits**:
   ```
   Make changes → "Check for missing references" → Confirm success
   ```

### Common MCP Workflows for This Project

#### Debugging Segmentation Issues
```
1. "Check if Unity has compilation errors"
2. "Show hierarchy of Segmentation scene"
3. "Inspect SegmentationInferenceManager component"
4. "Check QuestDepthCaptureManager configuration"
5. "Verify SegmentationOverlayRenderer references"
```

#### Verifying Pose Estimation Setup
```
1. "Check compilation status"
2. "Show PoseEstimation scene hierarchy"
3. "Inspect PoseInferenceRunManager component"
4. "Check InferenceConfig values (Server URL, mode, targetFPS)"
5. "Verify PassthroughCameraAccess reference is connected"
```

#### Before Making Code Changes
```
1. "Are there any Unity errors?" ← Fix first if yes
2. "Show me the affected scene hierarchy"
3. "What are the current Inspector values for [ComponentName]?"
4. Make changes
5. "Check for missing references"
6. "Are there compilation errors now?"
```

---

## Common Commands

### Unity Build & Deploy

```bash
# Build and deploy to Quest
# File → Build Settings → Build And Run (in Unity)

# Or manually install APK
adb install -r YourApp.apk
```

### Viewing Logs

**Windows**:
```bash
# View all Unity logs
adb logcat -s Unity

# Filter for specific component
adb logcat -s Unity | findstr "POSE"
adb logcat -s Unity | findstr "SEGMENTATION"
adb logcat -s Unity | findstr "SERVER"

# View errors only
adb logcat -s Unity:E

# Save to file
adb logcat -s Unity > unity_log.txt
```

**Python script** (provided in repo):
```bash
python read_unity_log.py
```

### Testing Server Connection

```bash
# Health check
curl http://192.168.0.135:8000/

# Test inference (from PC)
curl -X POST http://192.168.0.135:8000/infer_human?mode=both \
  -F "image=@test_image.jpg"
```

### Network Debugging

```bash
# Get PC IP address
# Windows:
ipconfig

# Check Quest can reach server
# On Quest via ADB shell:
adb shell
ping 192.168.0.135

# Check firewall allows port 8000
# Windows: Firewall settings
```

---

## Important Conventions

### Coordinate Systems

The project uses **three coordinate systems**:

1. **Normalized coordinates** (0-1): Server responses
2. **Pixel coordinates**: Image space
3. **World coordinates** (meters): Unity AR space

**Conversion flow**:
```
Server Response (normalized)
  → Multiply by image width/height
  → Pixel coordinates
  → Raycast from camera
  → World coordinates
```

See: [COORDINATE_TRANSFORMATION_GUIDE.md](./Documentation/COORDINATE_TRANSFORMATION_GUIDE.md)

### Inference Modes

The project supports multiple inference modes via `InferenceConfig.cs`:

```csharp
public enum InferenceMode
{
    ObjectDetection = 0,  // YOLO only
    PoseEstimation = 1,   // Pose only
    Both = 2,             // YOLO + Pose
    DepthEstimation = 3   // DEPRECATED
}
```

**Current best practices**:
- Use `mode=both` for pose estimation (gets both detections and skeleton)
- Use dedicated `/segmentation` endpoint for segmentation (NOT `/infer_human`)
- Avoid old depth estimation mode (use ROI depth or Quest native depth API)

### Response Format

**Expected from `/infer_human?mode=both`**:

```json
{
  "detections": {
    "detections": [...],
    "num_detections": 1
  },
  "skeleton": {
    "persons": [
      {
        "keypoints": [
          {"name": "nose", "x": 0.5, "y": 0.3, "score": 0.95},
          ...
        ],
        "bbox": [0.2, 0.1, 0.8, 0.9]
      }
    ]
  },
  "input_image_width": 1280,
  "input_image_height": 720,
  "processing_time_ms": 250.5
}
```

**Important**: Unity currently **bypasses segmentation mask** in responses from `/infer_human` due to JSON size. See segmentation verification report for details.

---

## Known Issues

### Issue 1: Segmentation in `/infer_human` is Ignored

**Status**: CONFIRMED

**Details**:
- Server may return `segmentation_mask` in `/infer_human?mode=both` response
- Unity's `PoseInferenceRunManager.cs` **does not parse or use it**
- Unity uses custom JSON extraction to bypass the large mask field
- This wastes server compute and bandwidth

**Solution**:
- Use dedicated `/segmentation` endpoint if segmentation is needed
- OR: Fix server to respect `include_mask=false` parameter

See: Segmentation Pipeline Verification Report

### Issue 2: Old Depth Estimation Mode

**Status**: DEPRECATED

**Details**:
- `DepthEstimation` scene uses server-side MiDaS
- Quest 3 has native depth API (better, faster)
- Should use ROI depth or Quest native API instead

**Migration**:
- Use Quest Environment Depth Manager
- See: `QuestDepthCaptureManager.cs` in Segmentation scene

### Issue 3: Large Download Size with Segmentation

**Status**: KNOWN LIMITATION

**Details**:
- Full 1280×960 segmentation mask = ~150KB
- Slows download, may timeout on slow networks

**Mitigation**:
- Server downsamples to 320×240 (4x reduction)
- Use `include_mask=false` when not needed
- Future: Use protobuf instead of JSON

---

## Performance Targets

### Latency Budget

| Component | Target (ms) | Acceptable (ms) |
|-----------|-------------|-----------------|
| Upload | 50-100 | < 150 |
| Server Inference | 150-250 | < 400 |
| Download | 50-150 | < 200 |
| Parse | 10-30 | < 50 |
| **E2E Total** | **~300** | **< 500** |

### Bandwidth Budget

| Payload | Size | Notes |
|---------|------|-------|
| Upload JPEG (quality=80) | ~20-50 KB | Configurable |
| Detection response | ~5 KB | JSON |
| Pose response | ~10 KB | JSON |
| Depth map (4x downsampled) | ~150 KB | Binary |
| Segmentation mask (4x downsampled) | ~75 KB | PNG |

### FPS Targets

| Mode | Target FPS | Notes |
|------|-----------|-------|
| Object Detection | 10 FPS | Fast |
| Pose Estimation | 5 FPS | Medium |
| Depth Estimation | 3-5 FPS | Slow (large download) |

**Configured via**: `InferenceConfig.targetFPS`

---

## Testing Checklist

Before suggesting code changes, verify:

### Unity-Specific (Use MCP)
- [ ] Check Unity compilation status (MCP: "Check for compilation errors")
- [ ] Verify scene hierarchy and references (MCP: "Show scene hierarchy")
- [ ] Confirm component configuration (MCP: "Check Inspector values")
- [ ] No missing script references (MCP: "Find missing references")

### Code Quality
- [ ] Unity compiles without errors
- [ ] Scene references are intact (check Inspector)
- [ ] Changes logged appropriately (Debug.Log with prefix)
- [ ] Error handling added for network calls
- [ ] No hardcoded IPs (use InferenceConfig)
- [ ] Documentation updated if API changed
- [ ] Performance impact considered

### Server & Network
- [ ] Server is running and reachable
- [ ] Server endpoint matches Unity configuration
- [ ] Network timeouts configured appropriately

---

## Server IP Configuration

### Centralized ServerConfig System

The project uses a **centralized ServerConfig ScriptableObject** to manage server IPs across all scenes.

**Benefits**:
- ✅ Change IP **once** → applies to all scenes
- ✅ No more hunting for hardcoded IPs
- ✅ Easy switching between environments (localhost, dev, prod)
- ✅ Visual editor tool

### Quick Configuration

**Option 1: Using Editor Tool** (Recommended)
```
1. Tools → Passthrough Camera → Server Config Editor
2. Enter server IP
3. Click "Save Configuration"
4. Done!
```

**Option 2: Using Inspector**
```
1. Select: Assets/PassthroughCameraApiSamples/Shared/Resources/ServerConfig.asset
2. Edit Server IP and Port
3. Save (Ctrl+S)
```

### Code Usage

**For InferenceConfig users** (PoseEstimation, ObjectDetection):
```csharp
[SerializeField] private InferenceConfig m_inferenceConfig = new InferenceConfig
{
    useServerConfig = true,  // ✅ Use centralized config
    mode = InferenceMode.Both,
    // baseUrl is auto-generated from ServerConfig
};
```

**For custom scripts** (Segmentation, etc.):
```csharp
using PassthroughCameraSamples.Shared;

// Get URLs from centralized config
string inferenceUrl = ServerConfig.Instance.InferenceUrl;
string segmentationUrl = ServerConfig.Instance.SegmentationUrl;
string baseUrl = ServerConfig.Instance.BaseUrl;
```

### Editor Tool Features

Access via: **Tools → Passthrough Camera → Server Config Editor**

- Current configuration display
- Edit server IP and port
- Quick presets (localhost, default)
- Find scenes with old hardcoded IPs
- Test server connection
- Validation warnings

### When Helping User Configure IPs

**DO**:
- ✅ Suggest using ServerConfig for new code
- ✅ Guide them to the Editor tool (Tools menu)
- ✅ Recommend migrating old scenes to use ServerConfig
- ✅ Use MCP to check current ServerConfig values

**DON'T**:
- ❌ Hardcode IPs directly in scene files
- ❌ Tell them to manually edit multiple scenes
- ❌ Forget that ServerConfig must be in `Resources/` folder

### Troubleshooting

**"No ServerConfig asset found"**:
1. Create via: Tools → Passthrough Camera → Server Config Editor → Create ServerConfig Asset
2. OR: Right-click in Resources folder → Create → Passthrough Camera Samples → Server Config

**Changes not taking effect**:
1. Check ServerConfig asset is in `Resources/ServerConfig.asset` (exact path)
2. Enable "Use Server Config" checkbox in InferenceConfig
3. Restart Unity if needed

**See**:
- [Server IP Configuration Guide](./Documentation/SERVER_IP_CONFIGURATION_GUIDE.md) - Full setup and migration guide
- [URL Reference](./URL_REFERENCE.md) - Visual guide showing URLs for all modes

---

## Useful Documentation Links

**In this repository**:
- [Server IP Configuration Guide](./Documentation/SERVER_IP_CONFIGURATION_GUIDE.md) ⭐ **NEW**
- [URL Reference (All Modes)](./URL_REFERENCE.md) ⭐ **NEW**
- [Quick Start Guide](./Documentation/QUICK_START_GUIDE.md)
- [Pose Estimation Technical Guide](./Documentation/POSE_ESTIMATION_TECHNICAL_GUIDE.md)
- [ROI Depth Estimation Guide](./Documentation/ROI_DEPTH_ESTIMATION_GUIDE.md)
- [Latency Optimization Guide](./Documentation/LATENCY_OPTIMIZATION_GUIDE.md)

**External**:
- [Meta Passthrough Camera API](https://developers.meta.com/horizon/documentation/unity/unity-pca-overview)
- [Unity Inference Engine (Sentis)](https://unity.com/sentis)
- [Ultralytics YOLO](https://docs.ultralytics.com/)

---

## When to Ask the User

**First, try using MCP to verify** if you can answer the question yourself:
- Use MCP to check Unity state (hierarchy, components, errors)
- Use MCP to inspect configuration values
- Use MCP to find missing references or errors

**Ask the user when**:

- Creating new files (prefer editing existing)
- Modifying scene structure significantly
- Changing server API contracts
- Adding new dependencies
- Making changes that affect performance significantly
- Removing code that might be referenced elsewhere
- The server repository location is needed (if not at expected path)
- Clarification needed on requirements or expected behavior

---

## Logging Best Practices

### Unity Logs

**Format**: `[COMPONENT] Message`

**Examples**:
```csharp
Debug.Log("[POSE SERVER] Starting inference...");
Debug.Log($"[POSE RECV] Response received, length={jsonResponse.Length}");
Debug.LogError($"[POSE SERVER] Inference failed: {error}");
Debug.LogWarning($"[POSE PARSE] skeleton.persons is NULL or empty!");
```

**Avoid**:
```csharp
Debug.Log("Starting");  // Too vague
Console.WriteLine("Test");  // Won't appear in Quest logs
```

### Server Logs

**Format**: `[COMPONENT] Message`

**Examples**:
```python
print(f"[POSE] Processing frame {frame_id}, mode={mode}")
print(f"[POSE] Detected {len(persons)} person(s)")
print(f"[SEG] Mask size: {mask.shape}, downsample={factor}")
```

---

## Quick Reference

### File Paths

```
# Unity scenes
Assets/PassthroughCameraApiSamples/PoseEstimation/PassthroughPoseEstimation.unity
Assets/PassthroughCameraApiSamples/Segmentation/Segmentation.unity

# Key scripts
Assets/PassthroughCameraApiSamples/PoseEstimation/Scripts/PoseInferenceRunManager.cs
Assets/PassthroughCameraApiSamples/Segmentation/Scripts/SegmentationInferenceManager.cs
Assets/PassthroughCameraApiSamples/Shared/Scripts/InferenceConfig.cs

# Documentation
Documentation/QUICK_START_GUIDE.md
Documentation/POSE_ESTIMATION_TECHNICAL_GUIDE.md

# Tools
Tools/README.md
read_unity_log.py
```

### IP Configuration

**Default server IP**: `192.168.0.135:8001` or `192.168.0.135:8000`

**Configure in Unity**:
```
Inspector → SentisInferenceManagerPrefab → PoseInferenceRunManager → Inference Config → Base Url
```

### Build Target

**Platform**: Android
**Minimum Horizon OS**: v74
**Recommended Unity**: 6000.0.38f1+

---

**Last Updated**: 2026-04-09
**Version**: 1.0
