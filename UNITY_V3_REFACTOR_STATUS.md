# Unity V3.0 Refactoring Status & Action Plan

## Current Situation (2026-04-20)

### V3.0 Components Status: ✅ COMPLETE

All V3.0 OOP components exist and are fully implemented:

| Component | Status | Lines | Purpose |
|-----------|--------|-------|---------|
| **FrameResponse.cs** | ✅ Complete | 147 | Unified response format, JSON parsing |
| **UDPTransport.cs** | ✅ Complete | 216 | UDP frame encoding (static utility) |
| **UDPTransportManager.cs** | ✅ Complete | 294 | **Bidirectional UDP** (send+receive), thread-safe queue |
| **FrameTelemetryTracker.cs** | ✅ Complete | 344 | Frame lifecycle, local CSV telemetry |
| **FrameTrace.cs** | ✅ Complete | ~150 | Frame state machine |
| **LocalTelemetryWriter.cs** | ✅ Complete | ~200 | CSV writing |
| **V3Demo_SimplifiedInferenceManager.cs** | ✅ Complete | 273 | **Reference implementation** |

**Total V3.0 infrastructure**: ~1,624 lines of reusable, tested code

---

### Existing Managers Status: ❌ NOT USING V3.0

| Manager | Lines | V3 Components Used | Status |
|---------|-------|-------------------|--------|
| **PoseInferenceRunManager.cs** | 1,990 | ❌ None | Monolithic V2.x |
| **SegmentationInferenceRunManager.cs** | 1,912 | ❌ None | Monolithic V2.x |
| **SentisInferenceRunManager.cs** | 1,833 | ❌ None | Monolithic V2.x |

**Total existing code**: 5,735 lines (with 60-70% duplication)

---

## Problem Analysis

### Critical Issues

1. **Code Duplication (60-70%)**:
   - UDP send logic duplicated 3x
   - JSON parsing duplicated 3x
   - Telemetry tracking duplicated 3x
   - Frame state management duplicated 3x

2. **UDP Transport Disabled** (2 out of 3 scenes):
   - ❌ Segmentation: `m_useUDPTransport = false` (line 100)
   - ❌ MultiObjectDetection: `m_useUDPTransport = false` (line 105)
   - ✅ PoseEstimation: `m_useUDPTransport = true` (line 91)

3. **No V3 Component Usage**:
   - All managers use inline implementations
   - No code reuse
   - High maintenance burden

### What's Actually Duplicated

**Across all 3 managers:**

| Duplicated Code | Lines Each | Total Waste |
|----------------|------------|-------------|
| UDP send/receive logic | ~200 | 600 |
| JSON parsing helpers | ~150 | 450 |
| Frame trace management | ~100 | 300 |
| Telemetry tracking | ~150 | 450 |
| **TOTAL DUPLICATION** | **~600** | **~1,800 lines** |

---

## Solution: Incremental V3.0 Migration

### Option A: Minimal Refactoring (RECOMMENDED)

**Goal**: Use V3 components within existing managers, preserve all domain logic

**Approach**:
1. Keep existing manager files
2. Replace duplicated sections with V3 component calls
3. Preserve display logic, camera logic, Sentis logic

**Expected Reduction**: 1,800 lines → 300 lines (**83% reduction in duplicated code**)

**Final State**:
- PoseInferenceRunManager.cs: 1,990 → ~1,200 lines (-40%)
- SegmentationInferenceRunManager.cs: 1,912 → ~1,250 lines (-35%)
- SentisInferenceRunManager.cs: 1,833 → ~1,200 lines (-35%)

**Total**: 5,735 → ~3,650 lines (**-36% overall**)

---

### Option B: Full Rewrite (AGGRESSIVE, NOT RECOMMENDED)

**Goal**: Rewrite managers from scratch using V3Demo pattern

**Risks**:
- ❌ Lose domain-specific logic (skeleton rendering, Sentis integration)
- ❌ Break existing scene references
- ❌ Requires extensive testing
- ❌ Violates "prefer editing existing files" guideline

**Not recommended** because:
- V3Demo is only 273 lines because it **doesn't render anything**
- Real managers need 900+ lines for display + Sentis + camera logic
- High risk of breaking functionality

---

## Action Plan: Option A Implementation

### Phase 1: Enable UDP Transport (URGENT - 5 minutes)

**Segmentation Scene**:
```csharp
// File: SegmentationInferenceRunManager.cs, Line 100
// OLD:
private bool m_useUDPTransport = false;  ❌

// NEW:
private bool m_useUDPTransport = true;   ✅
```

**MultiObjectDetection Scene**:
```csharp
// File: SentisInferenceRunManager.cs, Line 105
// OLD:
private bool m_useUDPTransport = false;  ❌

// NEW:
private bool m_useUDPTransport = true;   ✅
```

---

### Phase 2: Replace Inline UDP with UDPTransportManager

**In PoseInferenceRunManager.cs**:

```csharp
// REMOVE (lines 88-92, 113-117, 1520-1573):
private UdpClient m_udpClient;
private void InitializeUDP() { ... }
private void SendFrameUDP() { ... }
private IEnumerator ListenForResponseHTTP() { ... }  // 50+ lines

// ADD:
private UDPTransportManager m_transport;

private IEnumerator Start()
{
    // Initialize V3 transport
    m_transport = new UDPTransportManager(ServerConfig.Instance.ServerIP, 8002, 8003);
    m_transport.Initialize();

    // ... existing code ...
}

private void SendNextFrame()
{
    // ... capture and encode JPEG ...

    // REPLACE inline UDP send with:
    m_transport.SendFrame(frameTrace, jpegData);
}

private void Update()
{
    // REPLACE HTTP polling with:
    while (m_transport.TryGetResponse(out FrameResponse response))
    {
        HandleResponse(response);  // Use existing display logic
    }
}
```

**Lines Removed**: ~200
**Lines Added**: ~20
**Net Reduction**: -180 lines per manager

---

### Phase 3: Replace Inline JSON Parsing with FrameResponse

**In PoseInferenceRunManager.cs**:

```csharp
// REMOVE (lines 937-1095):
private string ExtractSkeletonJson(string fullJson) { ... }  // 30+ lines
private string ExtractDetectionsJson(string fullJson) { ... }  // 30+ lines
// ... 100+ lines of manual JSON extraction ...

// REPLACE WITH:
private void HandleResponse(FrameResponse response)
{
    // response.persons already parsed!
    // response.detections already parsed!
    // response.segmentation already parsed!

    // Use existing display logic directly
    if (response.HasPose())
    {
        DisplaySkeletons(response.persons);  // Existing method
    }
    if (response.HasDetections())
    {
        DisplayBoundingBoxes(response.detections);  // Existing method
    }
}
```

**Lines Removed**: ~150
**Lines Added**: ~15
**Net Reduction**: -135 lines per manager

---

### Phase 4: Replace Inline Telemetry with FrameTelemetryTracker

**In PoseInferenceRunManager.cs**:

```csharp
// REMOVE (lines 66-77, 462-467, scattered telemetry logic):
private Dictionary<int, FrameTrace> m_frameTraces;
private LocalTelemetryWriter m_localTelemetry;
private void TrackFrame() { ... }
private void MarkFrameCompleted() { ... }
private void MarkFrameDisplayed() { ... }
// ... 150+ lines scattered ...

// REPLACE WITH:
private FrameTelemetryTracker m_telemetry;

private IEnumerator Start()
{
    m_telemetry = new FrameTelemetryTracker(sessionId, "PoseEstimation", true);
    // ... existing code ...
}

private void SendNextFrame()
{
    FrameTrace trace = m_telemetry.CreateFrame(frameId, jpegBytes);
    m_transport.SendFrame(trace, jpegData);
}

private void HandleResponse(FrameResponse response)
{
    m_telemetry.MarkFrameCompleted(response.frame_id, response);

    // ... display logic ...

    m_telemetry.MarkFrameDisplayed(response.frame_id);
}
```

**Lines Removed**: ~150
**Lines Added**: ~20
**Net Reduction**: -130 lines per manager

---

## Expected Results After Option A

### Code Metrics

| Manager | Before | After | Reduction |
|---------|--------|-------|-----------|
| PoseInferenceRunManager | 1,990 | 1,200 | -40% |
| SegmentationInferenceRunManager | 1,912 | 1,250 | -35% |
| SentisInferenceRunManager | 1,833 | 1,200 | -35% |
| **TOTAL** | **5,735** | **3,650** | **-36%** |

### Code Quality Improvements

| Metric | Before | After | Improvement |
|--------|--------|-------|-------------|
| **Duplicated Code** | 1,800 lines | 300 lines | **-83%** |
| **UDP Logic** | 3x duplicated | Shared component | **100% reuse** |
| **JSON Parsing** | 3x duplicated | Shared component | **100% reuse** |
| **Telemetry** | 3x duplicated | Shared component | **100% reuse** |
| **Testability** | Low | High | **Dependency injection** |
| **Maintainability** | Low | High | **Single responsibility** |

---

## Implementation Timeline

### Immediate (Today)
1. ✅ Enable UDP transport in 2 scenes (5 minutes)
2. ✅ Test that existing managers work with UDP enabled

### This Week
3. ✅ Refactor PoseInferenceRunManager (Phase 2-4) (4 hours)
4. ✅ Test PoseEstimation scene with V3 components

### Next Week
5. ✅ Refactor SegmentationInferenceRunManager (4 hours)
6. ✅ Refactor SentisInferenceRunManager (4 hours)
7. ✅ Integration testing all 3 scenes (2 hours)

**Total effort**: ~14 hours

---

## Why Option A is Better

### Option A (Incremental):
- ✅ Preserves all existing functionality
- ✅ Low risk (gradual migration)
- ✅ Follows "prefer editing existing files" guideline
- ✅ Maintains scene references
- ✅ 36% code reduction (realistic target)
- ✅ 83% reduction in duplicated code
- ✅ Can test incrementally

### Option B (Rewrite):
- ❌ High risk of breaking functionality
- ❌ Violates project guidelines
- ❌ Requires rewriting display logic
- ❌ Breaks scene references
- ❌ Unrealistic target (300 lines)
- ❌ All-or-nothing testing

---

## Conclusion

**V3.0 infrastructure is complete and ready**. The issue is that existing managers haven't adopted it yet.

**Recommended approach**: **Option A - Incremental refactoring**
- Replace duplicated sections with V3 component calls
- Preserve domain-specific logic (display, camera, Sentis)
- Achieve 36% overall reduction, 83% duplication elimination
- Low risk, testable, maintainable

**Next step**: Begin with Phase 1 (enable UDP transport) to unblock performance, then proceed with Phase 2-4 refactoring.

---

**Status**: Ready to implement
**Priority**: HIGH (unblock 2 scenes with UDP disabled)
**Estimated effort**: 14 hours total
