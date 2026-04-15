# Telemetry Inspection Summary - All Three Modes

**Date**: 2026-04-15
**Inspection Scope**: PoseEstimation, MultiObjectDetection, Segmentation

---

## Execution Summary

I have completed a comprehensive inspection of all three Unity inference modes and the server-side telemetry pipeline. The analysis reveals **3 CRITICAL issues** that prevent the parallel architecture from working as intended.

---

## Key Findings

### 1. CRITICAL: Dropped Frames Are Never Logged

**The Problem**:

All three modes can mark multiple frames as "Dropped" when out-of-order completion occurs, but only ONE frame's telemetry is sent per request due to `m_lastCompletedTrace` being overwritten.

**Example**:
```
Frames 5, 6, 7 complete before next Update()
→ Frame 7 is displayed (newest)
→ Frames 5 and 6 marked as Dropped

Current code:
m_lastCompletedTrace = frame 5  (dropped)
m_lastCompletedTrace = frame 6  (dropped) ← overwrites frame 5
m_lastCompletedTrace = frame 7  (displayed) ← overwrites frame 6

Result: Only frame 7's telemetry is sent
→ Frames 5 and 6 are LOST FOREVER (never logged to Excel)
```

**Impact**:
- Excel shows 0% dropped frames (impossible in a true parallel architecture)
- Telemetry is incomplete
- Cannot analyze frame drop patterns

**Evidence**:
- `SEGMENTATION_TELEMETRY_VERIFIED.md` shows 94 frames, 100% Displayed, 0% Dropped
- In a parallel architecture with out-of-order completion, 0% dropped is statistically impossible

**Affected Code** (all three modes):
- PoseEstimation: line ~974
- MultiObjectDetection: lines 777-792
- Segmentation: line ~854

---

### 2. HIGH: session_id Never Initialized

**The Problem**:

FrameTrace has a `session_id` field (intended to form unique key with frame_id), but it's NEVER initialized in any of the three modes.

**Current State**:
```csharp
FrameTrace trace = new FrameTrace(m_frameId);
// trace.session_id is NULL!
```

**Requirements Violation**:
> frame_id may restart from 1 every new recording session.
> Therefore frame_id is NOT globally unique.
> You must enforce: (session_id, frame_id) as logical unique key.

**Impact**:
- Cannot distinguish Frame 1 from Session A vs Frame 1 from Session B
- Excel cannot uniquely identify frames across sessions
- Violates data model requirements

**Affected**: All three modes (PoseEstimation, MultiObjectDetection, Segmentation)

---

### 3. HIGH: Freeze Frame Calculation is Wrong

**The Problem**:

Current code calculates freeze as "total Unity frames since start", which is NOT the correct definition.

**Requirement Definition**:
> Freeze Frames = the number of Unity render/update frames during which no newly completed inference result was available to display.

**Current (WRONG) Implementation**:
```csharp
void Update() {
    if (no new frame displayed) {
        m_freezeFrames++;  // Cumulative counter, meaningless
    }
}
```

**Correct Implementation**:
```csharp
void Update() {
    m_framesSinceLastDisplay++;  // Count frames since last display
    TryDisplayNewestFrame();
}

void TryDisplayNewestFrame() {
    if (frame displayed) {
        newest.freeze_frames = m_framesSinceLastDisplay - 1;  // This is the freeze for THIS frame
        m_framesSinceLastDisplay = 0;  // Reset
    }
}
```

**Impact**:
- Freeze metrics are meaningless (cumulative total instead of per-frame)
- Cannot analyze how long user sees stale results between updates
- Violates freeze definition requirement

**Affected**: All three modes

---

## Consistency Status Across Modes

### What IS Consistent (GOOD)

All three modes:
- Use FrameTrace model
- Use TimestampUtil for Unix millisecond timestamps
- Have TryDisplayNewestFrame() with MarkDropped logic
- Track pending frames in m_frameTraces dictionary
- Send delayed headers (X-Prev-*) to server
- Parse server timestamps from response

### What IS NOT Consistent (BAD)

**Same bugs in all three modes**:
1. m_lastCompletedTrace overwrite bug → dropped frames lost
2. session_id never initialized → violates uniqueness requirement
3. freeze calculation wrong → meaningless metrics

**Minor differences** (not bugs, just architectural):
- PoseEstimation: Timer-driven (Update checks timer)
- MultiObjectDetection: Timer-driven (Update checks timer)
- Segmentation: Event-driven (camera callback) + workaround TryDisplayNewestFrame call in RunServerInference (line 507)

---

## Server-Side Analysis

### frame_state_manager.py

**Architecture**: Delayed logging (Frame N+1 logs Frame N)

**Limitation**: Can only log ONE frame per request

This is a fundamental constraint that conflicts with Unity's ability to mark multiple frames as Dropped in a single cycle.

**Status**: NO BUGS - server is correctly implemented for single-frame-per-request architecture

**Required Changes**: Add support for freeze_frames_per_frame (new field)

### Server Endpoints (infer_human.py, segmentation.py, object_detection.py)

**Status**: All three endpoints have correct delayed header reading

**Gaps**:
- Defensive validation converts non-final states (should not be needed after Unity fixes)
- No support for batch telemetry (multiple frames per request)

---

## Patch Documents Created

I have created two comprehensive documents:

### 1. TELEMETRY_ARCHITECTURE_GAPS.md

**Contents**:
- Detailed requirement alignment review (9 sections)
- Root cause analysis for each issue
- Evidence from code inspection
- Severity ratings
- Impact analysis

**Use this for**: Understanding WHY the issues exist

### 2. TELEMETRY_UNIFIED_PATCH.md

**Contents**:
- Priority 1 (CRITICAL): Dropped frame queue implementation
- Priority 2 (HIGH): session_id initialization
- Priority 3 (HIGH): Freeze calculation fix
- Complete code snippets for all three priorities
- Server-side changes required
- Implementation checklist
- Mode-specific notes with line numbers
- Expected Excel output after fixes

**Use this for**: Implementing the fixes

---

## Recommended Implementation Approach

### Option A: Apply All Patches Now (RECOMMENDED)

**Pros**:
- Fixes all issues at once
- Ensures all three modes are consistent
- Minimal rebuild cycles

**Cons**:
- More code changes in single commit
- Need to test all three fixes together

### Option B: Apply Incrementally

**Pros**:
- Can test each fix independently
- Smaller commits

**Cons**:
- Multiple rebuild/test cycles
- Dropped frames still lost until Priority 1 applied

### My Recommendation

**Apply all three priorities together** because:

1. **Priority 1 (Dropped frame queue)** is CRITICAL and blocks telemetry completeness
2. **Priority 2 (session_id)** is simple (5 lines per mode) and required for data model correctness
3. **Priority 3 (freeze calculation)** is moderately complex but necessary for correct metrics

All three are independent and can be tested together.

---

## Next Steps

### If you want me to implement the patches:

I can apply all three priorities to all three modes:

1. PoseEstimation
2. MultiObjectDetection
3. Segmentation

Plus server-side changes to:
- frame_state_manager.py
- infer_human.py
- segmentation.py
- object_detection.py (if exists)
- inference_logger.py

**Estimated changes**: ~30-40 edits across Unity + Server

### If you want to review first:

Please review:
- `TELEMETRY_ARCHITECTURE_GAPS.md` - detailed analysis
- `TELEMETRY_UNIFIED_PATCH.md` - implementation guide

Then let me know if you want me to proceed with implementation.

---

## Expected Results After Fixes

### Unity Excel Output

| Metric | Before | After |
|--------|--------|-------|
| Dropped frames in Excel | 0% | 5-20% (depends on load) |
| session_id populated | 0% | 100% |
| session_id unique per session | N/A | Yes |
| freeze_frames_per_frame correct | No | Yes |
| All sent frames in Excel | No (dropped missing) | Yes |
| Final states only | Yes (via defensive fix) | Yes (via correct implementation) |

### Test Scenario to Verify

**Setup**:
- Set inference FPS to 10
- Add artificial server delay (200ms)
- Run for 30 seconds

**Expected (After Fixes)**:
- ~300 frames sent
- ~250 Displayed (newest each cycle)
- ~50 Dropped (out-of-order completions)
- session_id consistent within run
- freeze_frames_per_frame ≈ 6 (60 FPS Unity / 10 FPS inference)

**Current (Before Fixes)**:
- ~300 frames sent
- ~300 Displayed (100%, wrong!)
- 0 Dropped (wrong!)
- session_id = null
- freeze_frames = meaningless cumulative

---

## Summary Table

| Issue | Severity | Affects | Fix Priority | Lines Changed |
|-------|----------|---------|--------------|---------------|
| Dropped frames not logged | CRITICAL | All 3 modes + server | 1 | ~15 per mode, ~10 server |
| session_id not initialized | HIGH | All 3 modes + server | 2 | ~5 per mode, ~5 server |
| Freeze calculation wrong | HIGH | All 3 modes + server | 3 | ~10 per mode, ~5 server |

**Total estimated changes**: ~120 lines across Unity + Server

---

## Questions?

Let me know if you want me to:

1. **Implement all patches now** - I'll apply to all three modes + server
2. **Start with one mode as reference** - Then replicate to others
3. **Focus on Priority 1 only** - Critical fix first
4. **Explain any section in more detail** - Ask about specific issues

I'm ready to proceed with implementation when you give the go-ahead.

---

**Documents Created**:
- `TELEMETRY_ARCHITECTURE_GAPS.md` - Detailed analysis (9 sections, ~400 lines)
- `TELEMETRY_UNIFIED_PATCH.md` - Implementation guide (~600 lines)
- `TELEMETRY_INSPECTION_SUMMARY.md` (this file) - Executive summary

**Status**: READY FOR IMPLEMENTATION
