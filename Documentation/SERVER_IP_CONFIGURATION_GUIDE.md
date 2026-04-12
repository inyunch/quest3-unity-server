# Server IP Configuration Guide

This guide explains how to configure and change the server IP address across all scenes in one place.

---

## Problem

Previously, you had to manually update the server IP in multiple places:
- `PoseEstimation` scene → `PoseInferenceRunManager` → `InferenceConfig.baseUrl`
- `Segmentation` scene → `SegmentationInferenceManager` → `m_serverUrl`
- `MultiObjectDetection` scene → Inspector settings
- And more...

This was tedious and error-prone when switching between different server IPs (local, remote, different ports, etc.).

---

## Solution: Centralized ServerConfig

We now have a **centralized ServerConfig system** that allows you to:

1. ✅ Set server IP **once** in a ScriptableObject asset
2. ✅ All scenes automatically use the centralized IP
3. ✅ Easy switching between presets (localhost, default, custom)
4. ✅ Visual editor tool for managing configuration

---

## Quick Start (5 Minutes)

### Step 1: Create ServerConfig Asset

**Option A: Using the Editor Tool** (Recommended)

1. Open Unity Editor
2. Go to: **Tools → Passthrough Camera → Server Config Editor**
3. Click **"Create ServerConfig Asset"**
4. Done! The asset is created at: `Assets/PassthroughCameraApiSamples/Shared/Resources/ServerConfig.asset`

**Option B: Using Create Menu**

1. In Project window, navigate to: `Assets/PassthroughCameraApiSamples/Shared/Resources/`
2. Right-click → **Create → Passthrough Camera Samples → Server Config**
3. Name it **"ServerConfig"** (must be exact name)
4. Done!

### Step 2: Configure Your Server IP

**Option A: Using the Editor Tool** (Recommended)

1. Open: **Tools → Passthrough Camera → Server Config Editor**
2. Enter your server IP in the "Server IP" field
3. Set the port (default: 8001)
4. Click **"Save Configuration"**
5. Done!

**Option B: Using Inspector**

1. Select the ServerConfig asset in Project window
2. Edit the fields in Inspector:
   - **Server IP**: `192.168.0.135` (or your server IP)
   - **Server Port**: `8001` (or your port)
   - **Request Timeout Seconds**: `10.0` (default)
3. Save (Ctrl+S)

### Step 3: Enable ServerConfig in Your Scenes

For each scene that uses server inference:

1. Select the inference manager GameObject:
   - **PoseEstimation**: `SentisInferenceManagerPrefab`
   - **Segmentation**: `SegmentationManager`
   - **MultiObjectDetection**: Detection manager
2. Find the component:
   - **PoseEstimation**: `PoseInferenceRunManager` → `Inference Config`
   - **Segmentation**: `SegmentationInferenceManager`
3. Check the box: **☑ Use Server Config**
4. Done! The scene will now use the centralized IP

---

## Using the Server Config Editor Tool

The Server Config Editor provides a visual interface for managing your server configuration.

### Opening the Tool

**Menu**: Tools → Passthrough Camera → Server Config Editor

### Tool Features

#### 1. Current Configuration Section

Shows the current server URLs:
- Base URL: `http://192.168.0.135:8001`
- Inference URL: `http://192.168.0.135:8001/infer_human`
- Segmentation URL: `http://192.168.0.135:8001/segmentation`
- ROI Depth URL: `http://192.168.0.135:8001/infer_roi_depth`

Displays validation warnings if configuration is invalid.

#### 2. Edit Server Settings

Change the server configuration:
- **Server IP**: Enter IP address (e.g., `192.168.1.100`)
- **Server Port**: Enter port number (e.g., `8000`, `8001`)
- **Request Timeout**: Adjust timeout (5-60 seconds)

**Save Configuration**: Applies changes to the ServerConfig asset

**Test Server Connection**: Shows how to test the connection (use curl)

#### 3. Quick Presets

One-click presets for common configurations:

| Preset | IP | Port |
|--------|-----|------|
| **Local (localhost)** | 127.0.0.1 | 8001 |
| **Default (192.168.0.135)** | 192.168.0.135 | 8001 |
| **Port 8000** | (unchanged) | 8000 |
| **Port 8001** | (unchanged) | 8001 |

#### 4. Update Scenes

**Find Scenes with Old IP Configuration**:
- Searches all scenes for hardcoded IPs
- Lists scenes that need updating
- Helps you migrate to ServerConfig

#### 5. Preview URLs

Shows what the URLs will be after saving.

---

## Code Integration

### For InferenceConfig Users (PoseEstimation, ObjectDetection)

**Before** (hardcoded):
```csharp
[SerializeField] private InferenceConfig m_inferenceConfig = new InferenceConfig
{
    baseUrl = "http://192.168.0.135:8001/infer_human",  // ❌ Hardcoded
    mode = InferenceMode.Both,
    // ...
};
```

**After** (using ServerConfig):
```csharp
[SerializeField] private InferenceConfig m_inferenceConfig = new InferenceConfig
{
    useServerConfig = true,  // ✅ Use centralized config
    mode = InferenceMode.Both,
    // ...
};
```

The `baseUrl` is automatically generated from ServerConfig when `useServerConfig = true`.

### For Custom Scripts (Segmentation, etc.)

**Before** (hardcoded):
```csharp
[SerializeField] private string m_serverUrl = "http://192.168.0.135:8001/segmentation";  // ❌ Hardcoded
```

**After** (using ServerConfig):
```csharp
// In Start() or when building URL:
string serverUrl = ServerConfig.Instance.SegmentationUrl;  // ✅ Centralized
```

### Available ServerConfig URLs

```csharp
using PassthroughCameraSamples.Shared;

// Base URL (http://IP:PORT)
string baseUrl = ServerConfig.Instance.BaseUrl;

// Full endpoint URLs
string inferenceUrl = ServerConfig.Instance.InferenceUrl;  // .../infer_human
string segmentationUrl = ServerConfig.Instance.SegmentationUrl;  // .../segmentation
string roiDepthUrl = ServerConfig.Instance.RoiDepthUrl;  // .../infer_roi_depth

// Build URL with query parameters
string url = ServerConfig.Instance.BuildInferenceUrl("both", includeMask: false, includeDepth: false);
// Result: http://192.168.0.135:8001/infer_human?mode=both&include_mask=false&include_depth=false

// Access individual settings
string ip = ServerConfig.Instance.ServerIP;
int port = ServerConfig.Instance.ServerPort;
float timeout = ServerConfig.Instance.RequestTimeoutSeconds;
```

---

## Migration Guide

### Migrating Existing Scenes

If you have scenes with hardcoded IPs, follow these steps:

#### Step 1: Find Affected Scenes

1. Open: **Tools → Passthrough Camera → Server Config Editor**
2. Click: **"Find Scenes with Old IP Configuration"**
3. Check Console for list of scenes with hardcoded IPs

#### Step 2: Update Each Scene

**For PoseEstimation Scene:**

1. Open scene: `PassthroughPoseEstimation.unity`
2. Select: `SentisInferenceManagerPrefab`
3. In Inspector, find: `PoseInferenceRunManager` component
4. In `Inference Config` section:
   - Check: ☑ **Use Server Config**
   - (The `Base Url` field will now be ignored)
5. Save scene

**For Segmentation Scene:**

1. Open scene: `Segmentation.unity`
2. Select: `SegmentationManager` (or similar)
3. Find: `SegmentationInferenceManager` component
4. Update the `Start()` method to use ServerConfig:

```csharp
// Before
private string m_serverUrl = "http://192.168.0.135:8001/segmentation";

// After
private void Start()
{
    // Use centralized config
    m_serverUrl = ServerConfig.Instance.SegmentationUrl;
    // ...
}
```

#### Step 3: Verify

1. Open: **Tools → Passthrough Camera → Server Config Editor**
2. Change IP to a test value (e.g., `127.0.0.1`)
3. Click: **"Save Configuration"**
4. Check Console logs in each scene to verify new IP is used
5. Change IP back to your actual server IP

---

## Common Scenarios

### Scenario 1: Testing Locally

**Goal**: Run server on the same PC (localhost)

1. Open: **Tools → Passthrough Camera → Server Config Editor**
2. Click preset: **"Local (localhost)"**
3. Click: **"Save Configuration"**
4. Done! All scenes now use `http://127.0.0.1:8001`

**Note**: This only works if Unity Editor and server are on same machine. For Quest device, use actual IP.

### Scenario 2: Switching Between Development Servers

**Goal**: Switch between two servers (e.g., laptop and desktop)

**Server A (Laptop)**: `192.168.1.100:8001`
**Server B (Desktop)**: `192.168.1.200:8001`

1. Open: **Tools → Passthrough Camera → Server Config Editor**
2. Enter IP: `192.168.1.100` (Server A)
3. Click: **"Save Configuration"**
4. Build and test
5. To switch to Server B:
   - Enter IP: `192.168.1.200`
   - Click: **"Save Configuration"**
   - Done! (No need to rebuild if just changing IP)

### Scenario 3: Different Port

**Goal**: Server runs on port 8000 instead of 8001

1. Open: **Tools → Passthrough Camera → Server Config Editor**
2. Click preset: **"Port 8000"**
3. Click: **"Save Configuration"**
4. Done!

### Scenario 4: Production vs Development

**Option A: Multiple ServerConfig Assets** (Recommended)

Create separate configs for different environments:

1. Create: `ServerConfig_Development.asset`
   - IP: `192.168.0.135:8001`
2. Create: `ServerConfig_Production.asset`
   - IP: `your.production.server:8000`
3. When deploying:
   - Rename current `ServerConfig.asset` → `ServerConfig_Dev.asset`
   - Copy `ServerConfig_Production.asset` → `ServerConfig.asset`
   - Build and deploy

**Option B: Build Configurations**

Use Unity build configurations to swap ServerConfig assets automatically (requires custom build script).

---

## Testing Your Configuration

### Test Server Connection

**Method 1: Using curl (Windows)**

```bash
# Test health endpoint
curl http://192.168.0.135:8001/

# Expected response:
# {"status":"ok","message":"..."}
```

**Method 2: Using PowerShell**

```powershell
Invoke-WebRequest -Uri "http://192.168.0.135:8001/" -Method GET
```

**Method 3: Using the Editor Tool**

1. Open: **Tools → Passthrough Camera → Server Config Editor**
2. Click: **"Test Server Connection"**
3. Follow instructions in the dialog (use curl)

**Method 4: Build and Run on Quest**

1. Build and deploy to Quest
2. Check Unity logs:

```bash
adb logcat -s Unity | findstr "InferenceConfig"

# Should see:
# [InferenceConfig] Using ServerConfig: true (IP: 192.168.0.135)
# [InferenceConfig] URL: http://192.168.0.135:8001/infer_human?mode=both...
```

---

## Troubleshooting

### Issue 1: "No ServerConfig asset found"

**Symptoms**:
```
[ServerConfig] No ServerConfig asset found in Resources folder.
Using default values (192.168.0.135:8001)
```

**Solution**:
1. Create ServerConfig asset (see Step 1 above)
2. Must be located at: `Assets/PassthroughCameraApiSamples/Shared/Resources/ServerConfig.asset`
3. Must be named exactly: **ServerConfig** (case-sensitive)

### Issue 2: Changes Not Taking Effect

**Symptoms**: Changed IP in Editor but scenes still use old IP

**Checklist**:
- [ ] Did you click **"Save Configuration"** in the Editor tool?
- [ ] Did you enable **"Use Server Config"** in the scene's InferenceConfig?
- [ ] Did you save the ServerConfig asset (Ctrl+S)?
- [ ] Did you restart Unity? (Sometimes needed for Resources to refresh)

**Solution**:
1. Close Unity Editor
2. Reopen project
3. Check ServerConfig asset in Inspector (verify IP is correct)
4. Rebuild and deploy

### Issue 3: ServerConfig Works in Editor but Not on Quest

**Symptoms**: Correct IP in Editor, but Quest still uses old hardcoded IP

**Cause**: The ServerConfig asset may not be included in the build

**Solution**:
1. Verify asset is in `Resources/` folder (required for runtime loading)
2. Check Build Settings → scenes are included
3. Clean build folder and rebuild:
   - Delete: `Build/` folder
   - Build And Run again

### Issue 4: Different Scenes Use Different IPs

**Symptoms**: PoseEstimation uses new IP, but Segmentation uses old IP

**Cause**: Some scenes haven't been migrated to use ServerConfig

**Solution**:
1. Open: **Tools → Passthrough Camera → Server Config Editor**
2. Click: **"Find Scenes with Old IP Configuration"**
3. Update each scene listed (see Migration Guide above)

---

## Advanced Usage

### Programmatic IP Changes

You can change the server IP at runtime (not recommended for production):

```csharp
using PassthroughCameraSamples.Shared;

// NOT RECOMMENDED: ServerConfig is a ScriptableObject asset
// Changes won't persist between sessions

// Instead, use different ServerConfig assets for different environments
```

### Custom Endpoints

If you add new server endpoints:

1. Edit: `ServerConfig.cs`
2. Add new endpoint property:

```csharp
[SerializeField] private string m_customEndpoint = "/custom";

public string CustomUrl => $"{BaseUrl}{m_customEndpoint}";
```

3. Use in your code:

```csharp
string url = ServerConfig.Instance.CustomUrl;
```

### Validation

ServerConfig includes automatic validation:

```csharp
string[] warnings = ServerConfig.Instance.Validate();

if (warnings.Length > 0)
{
    foreach (var warning in warnings)
    {
        Debug.LogWarning($"[ServerConfig] {warning}");
    }
}
```

---

## Best Practices

1. ✅ **Always use ServerConfig** for new code
2. ✅ **Migrate old scenes** as you work on them
3. ✅ **Create presets** for common environments (dev, staging, prod)
4. ✅ **Validate configuration** before building
5. ✅ **Test connection** after changing IP
6. ❌ **Don't hardcode IPs** in scene files anymore
7. ❌ **Don't modify ServerConfig.cs** unless adding new endpoints

---

## Summary

### What You Gain

- ✅ **Change IP once** → applies to all scenes
- ✅ **Quick switching** between environments (localhost, dev, prod)
- ✅ **Visual editor** for easy configuration
- ✅ **No more hunting** for hardcoded IPs in scenes
- ✅ **Less error-prone** deployment

### What Changed

**Old Way**:
```
Edit PoseEstimation scene → Change IP
Edit Segmentation scene → Change IP
Edit ObjectDetection scene → Change IP
Edit 5 more scenes → Change IP
Build → Deploy → Realize one scene still has old IP → Repeat
```

**New Way**:
```
Tools → Server Config Editor → Enter new IP → Save → Done
```

---

## Related Documentation

- [Quick Start Guide](./QUICK_START_GUIDE.md)
- [Pose Estimation Technical Guide](./POSE_ESTIMATION_TECHNICAL_GUIDE.md)
- [Main README](./README.md)

---

**Last Updated**: 2026-04-09
**Version**: 1.0
