# Telemetry Status Report - All Modes

**Date**: 2026-04-14
**Excel File**: C:\Repo\Github\vision_server\debug\logs\inference_log_2026-04-14.xlsx
**Total Frames**: 688

## Executive Summary

| Mode | Unity TS | Server TS | Final State | Frame Return | Status |
|------|----------|-----------|-------------|--------------|--------|
| **PoseEstimation** | ✅ 90% | ❌ 0% | ⚠️  96% Displayed, 4% Completed | ❓ PARTIAL | **PARTIAL** |
| **MultiObjectDetection** | ✅ 100% | ✅ 100% | ⚠️  98% Displayed, 2% Completed | ✅ YES | **WORKING** |
| **Segmentation** | ❌ 0% | ❌ 0% | ❌ 100% Completed | ❌ NO | **BROKEN** |

---

## Detailed Analysis

### 1. PoseEstimation (136 frames)

#### Timestamp Status
- ✅ **unity_send_ts**: 90% populated
- ✅ **unity_receive_ts**: 90% populated
- ✅ **unity_display_ts**: 90% populated
- ❌ **unity_drop_ts**: 0% (no dropped frames)
- ❌ **server_receive_ts**: 0% - **ALL NaN**
- ❌ **server_send_ts**: 0% - **ALL NaN**

#### Final State Distribution (Last 50 frames)
- Displayed: 48 (96%)
- **Completed: 2 (4%)** ⚠️  PROBLEM!

#### Frame Continuity
- ⚠️  WARNING: Frame IDs have backwards jumps (116 → 1, 11 → 1)
- This indicates scene was restarted

#### Critical Issues
1. **Server timestamps are ALL NaN** - Unity is NOT sending X-Prev-Server-* headers
2. **"Completed" state appears in Excel** - Frames logged before final determination
3. Unity timestamps exist BUT server timestamps don't - suggests Unity→Server header transmission failure

#### Frame Return Status
❓ **PARTIAL** - Frames have latency_ms and server_proc_ms, so server IS responding, but delayed telemetry is broken

---

### 2. MultiObjectDetection (530 frames) ✅ BEST PERFORMING

#### Timestamp Status
- ✅ **unity_send_ts**: 100% populated
- ✅ **unity_receive_ts**: 100% populated
- ✅ **unity_display_ts**: 100% populated
- ❌ **unity_drop_ts**: 0% (no dropped frames)
- ✅ **server_receive_ts**: 100% populated 🎉
- ✅ **server_send_ts**: 100% populated 🎉

#### Final State Distribution (Last 50 frames)
- Displayed: 49 (98%)
- **Completed: 1 (2%)** ⚠️  Minor issue

#### Frame Continuity
- ✅ **No gaps** - all frames consecutive

#### Critical Issues
1. **Minor**: 1 frame with "Completed" state (likely the last frame in queue)
2. **No Dropped frames** - suggests no parallel out-of-order completion yet

#### Frame Return Status
✅ **YES** - All frames are being returned from server with complete telemetry

---

### 3. Segmentation (22 frames) ❌ COMPLETELY BROKEN

#### Timestamp Status
- ❌ **unity_send_ts**: 0% - **ALL NaN**
- ❌ **unity_receive_ts**: 0% - **ALL NaN**
- ❌ **unity_display_ts**: 0% - **ALL NaN**
- ❌ **unity_drop_ts**: 0% - **ALL NaN**
- ❌ **server_receive_ts**: 0% - **ALL NaN**
- ❌ **server_send_ts**: 0% - **ALL NaN**

#### Final State Distribution
- **Completed: 22 (100%)** ❌ CRITICAL!
- **ALL** frames stuck in "Completed" state

#### Frame Continuity
- ⚠️  Frame IDs jump backwards (9 → 1)

#### Critical Issues
1. **NO timestamps AT ALL** - Unity is not populating FrameTrace OR not sending headers
2. **ALL frames = "Completed"** - Frames are logged immediately, not after Display/Drop
3. Server IS processing (latency_ms exists) but telemetry completely broken

#### Frame Return Status
❌ **NO** - Frames are returned (latency exists) but with ZERO telemetry data

---

## Root Cause Analysis

### Problem 1: "Completed" State in Excel

**What it means**: `final_state = "Completed"` indicates the frame received a server response but has NOT yet been Displayed or Dropped.

**Why it's wrong**: According to delayed telemetry architecture:
1. Frame N receives response → state = "Completed"
2. Frame N is queued for display
3. Update() processes queue → Frame N marked as "Displayed" or "Dropped"
4. Frame N is **Enqueued** in `m_completedFramesQueue`
5. Frame N+1 sends request → **Dequeues** Frame N and sends its final state
6. Server logs Frame N with final state

**Current behavior**: Frames are being logged with state = "Completed", which means they're being logged **before step 4** (before Enqueue).

**Likely cause**: Server is logging frames from **current frame's client data** instead of **delayed telemetry headers**.

### Problem 2: Server Timestamps Missing (PoseEstimation)

**Symptoms**:
- Unity timestamps exist (unity_send_ts, unity_receive_ts, unity_display_ts)
- Server timestamps are ALL NaN

**Analysis**:
Looking at MultiObjectDetection (which works):
- server_receive_ts: 100% ✅
- server_send_ts: 100% ✅

This means:
1. Server IS returning timestamps in response JSON
2. Unity IS parsing response timestamps (for MultiObjectDetection)
3. Unity IS sending X-Prev-Server-* headers (for MultiObjectDetection)

**Likely cause for PoseEstimation failure**:
1. PoseInferenceRunManager may have compilation errors
2. OR the queue-based code wasn't applied correctly
3. OR Unity code is running old version (not recompiled)

### Problem 3: Segmentation Completely Broken

**Symptoms**: ALL timestamps are NaN, ALL states are "Completed"

**Likely causes**:
1. SegmentationInferenceRunManager has compilation errors
2. Unity is running OLD code (before our changes)
3. FrameTrace is not being created/populated
4. Delayed telemetry headers are not being sent

---

## Action Items

### CRITICAL: Fix "Completed" State Issue

The core problem is that frames are being logged BEFORE they reach final state. Let me check where logging happens:

**Hypothesis**: `frame_state_manager.process_frame()` is being called with current frame data instead of delayed headers.

**Expected flow**:
```
Frame N arrives at server:
  - Headers contain Frame N-1's delayed telemetry (from queue)
  - Server calls process_frame() with delayed headers
  - Returns Frame N-1's complete data
  - Logs Frame N-1 (with final_state = "Displayed" or "Dropped")
```

**Current (broken) flow**:
```
Frame N arrives at server:
  - Server calls process_frame() with Frame N's current data
  - Returns Frame N-1 (if exists)
  - BUT Frame N-1's final_state is still "Completed" (not yet determined!)
```

### Required Fixes

1. **Verify Unity Compilation**
   - Check for errors in PoseInferenceRunManager
   - Check for errors in SegmentationInferenceRunManager
   - Ensure queue-based code compiled successfully

2. **Verify Header Transmission**
   - Add debug logging to see if X-Prev-* headers are sent
   - Check server logs for [TELEMETRY] messages

3. **Fix Segmentation FrameTrace Population**
   - Check if FrameTrace constructor is being called
   - Check if timestamps are being populated
   - Check if MarkDisplayed/MarkDropped are being called

4. **Verify Frame Return**
   - Check if server response includes t_server_recv and t_server_send
   - Check if Unity is parsing these fields

---

## Question: "務必確認每一個frame最終有沒有被送回unity side?"

### Answer: YES for inference results, NO for complete telemetry

**Inference Results (Detection/Pose/Segmentation)**:
- ✅ **YES** - All frames show latency_ms and server_proc_ms
- ✅ **YES** - All frames have detection_count
- This proves server IS returning results to Unity

**Complete Telemetry Data**:
- ✅ **MultiObjectDetection**: YES - 100% complete telemetry
- ⚠️  **PoseEstimation**: PARTIAL - Unity timestamps yes, server timestamps no
- ❌ **Segmentation**: NO - All telemetry missing

### Proof Server is Responding

From Excel data:
```
Segmentation Frame 13:
  latency_ms: 257.8 ms
  server_proc_ms: 50.4 ms
  detection_count: exists
```

This PROVES:
1. Unity sent request with image
2. Server processed and returned result
3. Unity received response and calculated latency
4. Unity displayed/used the result

**But telemetry is missing because**:
- Unity is not populating FrameTrace with timestamps
- OR delayed headers are not being sent
- OR server is not reading headers correctly

---

## Next Steps

1. ✅ Check Unity compilation errors
2. ✅ Verify queue-based code is actually running
3. ✅ Add debug logging to confirm header transmission
4. ✅ Fix Segmentation FrameTrace population
5. ✅ Test with fresh run after fixes
