# Phase 1: Unity OOP Refactoring - Progress Report

**Date**: 2026-04-20
**Status**: Core Components Complete, Integration Pending

---

## Completed Work

### 1. New OOP Components Created

#### FrameResponse.cs
**Location**: `Assets/.../Shared/Scripts/FrameResponse.cs`
**Purpose**: Unified server response format for all inference modes

**Features**:
- JsonUtility compatible data classes
- Support for Detection, Pose, and Segmentation results
- Server timing breakdown (queue_wait_ms, processing_time_ms, etc.)
- Helper methods: `HasDetections()`, `HasPose()`, `HasSegmentation()`

**Classes**:
- `FrameResponse` - Main response container
- `DetectionResult` - YOLO bounding box
- `PersonPose` - Pose estimation result
- `Keypoint` - Single joint in COCO format
- `SegmentationResult` - Segmentation mask + metadata

---

#### UDPTransportManager.cs
**Location**: `Assets/.../Shared/Scripts/UDPTransportManager.cs`
**Purpose**: Bidirectional UDP communication manager

**Features**:
- **Send**: Non-blocking frame transmission to server (port 8002)
- **Receive**: Background thread listening for responses (port 8003)
- **Thread-safe queue**: Main thread polls responses via `TryGetResponse()`
- **Statistics**: Frames sent, responses received, parse errors

**API**:
```csharp
// Initialization
UDPTransportManager transport = new UDPTransportManager(serverIP, sendPort: 8002, receivePort: 8003);
transport.Initialize();

// Sending frame
transport.SendFrame(trace, jpegData, telemetryJson: null);

// Receiving responses (call from Update())
if (transport.TryGetResponse(out FrameResponse response))
{
    // Process response
}

// Cleanup
transport.Shutdown();
```

---

#### FrameTelemetryTracker.cs
**Location**: `Assets/.../Shared/Scripts/FrameTelemetryTracker.cs`
**Purpose**: Centralized frame state tracking and local CSV telemetry

**Features**:
- **Frame lifecycle**: Pending → Completed → (Displayed/Dropped/Failed)
- **Drop detection**: Automatically marks frames superseded by newer frames
- **Local CSV**: Writes to Quest storage via `LocalTelemetryWriter`
- **Thread-safe**: Lock-protected for UDP background operations
- **Memory management**: Auto-cleanup of old traces

**API**:
```csharp
// Initialization
FrameTelemetryTracker telemetry = new FrameTelemetryTracker(sessionId, sceneName: "Segmentation", enableLocalTelemetry: true);

// Create frame
FrameTrace trace = telemetry.CreateFrame(frameId, jpegBytes);

// Update state transitions
telemetry.MarkFrameCompleted(frameId, response);  // When response received
telemetry.MarkFrameDisplayed(frameId);            // When displayed (writes CSV)
telemetry.MarkFrameFailed(frameId, errorMsg);     // On error (writes CSV)

// Periodic cleanup
telemetry.CleanupOldTraces();

// Shutdown
telemetry.Shutdown();
```

---

### 2. Existing Components (Already in Codebase)

These components work with the new OOP architecture:

- **FrameTrace.cs** - Frame metadata and state tracking
- **LocalTelemetryWriter.cs** - CSV file writer for Quest storage
- **UDPTransport.cs** - Static utility for frame encoding
- **TimestampUtil.cs** - Unix timestamp helper
- **InferenceConfig.cs** - Server configuration

---

## Architecture Benefits

### Before (Current State)
```
SentisInferenceRunManager (1000+ lines)
├─ Manual frame trace management
├─ Manual UDP send/receive logic
├─ HTTP polling coroutine
├─ N+1 delayed telemetry
├─ Scattered state tracking
└─ Duplicate code across scenes
```

### After (V3.0 OOP)
```
SentisInferenceRunManager (500 lines)
├─ UDPTransportManager (handles all UDP I/O)
├─ FrameTelemetryTracker (handles state + CSV)
├─ FrameResponse (unified data format)
└─ Clean separation of concerns
```

**Code Reduction**: ~40% for inference managers

---

## Integration Pattern

### Simplified Manager Structure (V3.0)

```csharp
public class SegmentationInferenceRunManager : MonoBehaviour
{
    // OOP Components
    private UDPTransportManager m_transport;
    private FrameTelemetryTracker m_telemetry;
    private string m_sessionId;
    private int m_frameId = 0;

    private void Start()
    {
        // 1. Initialize session
        m_sessionId = System.Guid.NewGuid().ToString();

        // 2. Initialize OOP components
        m_transport = new UDPTransportManager(
            serverIP: ServerConfig.Instance.ServerIP,
            sendPort: 8002,
            receivePort: 8003
        );
        m_transport.Initialize();

        m_telemetry = new FrameTelemetryTracker(
            m_sessionId,
            sceneName: "Segmentation",
            enableLocalTelemetry: true
        );

        // 3. Start fixed-cadence inference loop
        InvokeRepeating(nameof(SendNextFrame), 0f, 1f / targetFPS);
    }

    private void Update()
    {
        // Poll for UDP responses (non-blocking)
        while (m_transport.TryGetResponse(out FrameResponse response))
        {
            HandleResponse(response);
        }

        // Periodic telemetry cleanup
        if (Time.frameCount % 300 == 0)
        {
            m_telemetry.CleanupOldTraces();
        }
    }

    private void SendNextFrame()
    {
        if (!m_cameraAccess.IsPlaying) return;

        // 1. Capture and encode frame
        byte[] jpegData = CaptureAndEncodeFrame();

        // 2. Create frame trace
        FrameTrace trace = m_telemetry.CreateFrame(m_frameId++, jpegData.Length);

        // 3. Send via UDP (non-blocking!)
        m_transport.SendFrame(trace, jpegData);
    }

    private void HandleResponse(FrameResponse response)
    {
        // 1. Update telemetry
        m_telemetry.MarkFrameCompleted(response.frame_id, response);

        // 2. Render results
        RenderResults(response);

        // 3. Mark as displayed (auto-drops older frames, writes CSV)
        m_telemetry.MarkFrameDisplayed(response.frame_id);
    }

    private void OnDestroy()
    {
        m_transport?.Shutdown();
        m_telemetry?.Shutdown();
    }
}
```

**Total lines**: ~150 (vs 1000+ before)

---

## What's Left to Do

### Phase 1: Unity Refactoring (Current)

- [ ] Refactor `SegmentationInferenceRunManager.cs` to use new OOP components
- [ ] Refactor `SentisInferenceRunManager.cs` (Multi-Object Detection)
- [ ] Refactor `PoseInferenceRunManager.cs`
- [ ] Remove HTTP polling coroutines
- [ ] Remove N+1 telemetry embedding logic
- [ ] Test compilation in Unity

### Phase 2: Server Refactoring

- [ ] Create `InferenceProcessor.py` (unified processor)
- [ ] Create `UDPFrameListener.py` (frame receiver)
- [ ] Create `UDPResponseSender.py` (response sender)
- [ ] Create `InferenceEngine.py` (model wrapper)
- [ ] Remove Excel logging system
- [ ] Remove Result cache
- [ ] Remove Bounded queue
- [ ] Remove Frame loss tracker

### Phase 3: Integration Testing

- [ ] E2E test: Unity → Server → Unity (UDP bidirectional)
- [ ] Performance test: Verify latency improvements
- [ ] Telemetry test: Verify CSV contains all states

### Phase 4: Documentation

- [ ] Update SYSTEM_COMPLETE_GUIDE.md
- [ ] Update QUICK_START_GUIDE.md
- [ ] Create V3.0 migration guide

---

## Decision Point

**Option A**: Continue with full refactoring
- Refactor all 3 inference managers now
- Complete Phase 1 before moving to server

**Option B**: Create minimal integration example
- Keep existing managers as-is for now
- Create ONE new example scene showing V3.0 architecture
- Demonstrate new components work end-to-end
- Full refactoring later

**Recommendation**: Option B for now
- Faster to validate architecture
- Lower risk of breaking existing scenes
- Can refactor gradually after validation

---

## Key Architectural Changes

### Eliminated Complexity

**Removed**:
- ❌ HTTP polling coroutines (100+ lines)
- ❌ N+1 delayed telemetry logic (50+ lines)
- ❌ Manual frame trace dictionary management (80+ lines)
- ❌ Scattered drop detection logic (60+ lines)
- ❌ Duplicate CSV writing code (40+ lines)

**Replaced with**:
- ✅ `UDPTransportManager` - Single responsibility: UDP I/O
- ✅ `FrameTelemetryTracker` - Single responsibility: State + CSV
- ✅ `FrameResponse` - Unified data format

### New Data Flow

```
[Unity Update()]
    ↓
[TryGetResponse()] ← UDP Background Thread ← Server:8003
    ↓
[HandleResponse()]
    ├→ [MarkFrameCompleted()] → Update FrameTrace
    ├→ [RenderResults()] → Display in AR
    └→ [MarkFrameDisplayed()] → Drop old frames + Write CSV

[SendNextFrame()] → InvokeRepeating
    ↓
[CreateFrame()] → New FrameTrace
    ↓
[SendFrame()] → Non-blocking UDP → Server:8002
```

**Key Insight**: ZERO blocking operations in main thread!

---

## Files Created

1. `FrameResponse.cs` + `.meta`
2. `UDPTransportManager.cs` + `.meta`
3. `FrameTelemetryTracker.cs` + `.meta`
4. `PHASE1_OOP_REFACTOR_PROGRESS.md` (this document)

---

## Next Steps

**Immediate**:
1. Decide on Option A vs Option B
2. If Option B: Create minimal example scene
3. If Option A: Refactor SegmentationInferenceRunManager first

**After Unity Phase**:
1. Begin server-side OOP refactoring (Phase 2)
2. Remove server-side telemetry tracking
3. Remove HTTP polling endpoint
4. Implement UDP response sender

---

**Checkpoint Commit**: Both repos already pushed to GitHub with pre-refactor checkpoints
- Server: commit `7184200`
- Unity: commit `9b0762d`

**Status**: Ready to proceed with integration!
