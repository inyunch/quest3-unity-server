# Telemetry Patch Implementation Progress

**Date**: 2026-04-15
**Status**: IN PROGRESS

---

## Completed Work

### 1. FrameTrace Model (Shared) - COMPLETE

**File**: `Assets/PassthroughCameraApiSamples/Shared/Scripts/FrameTrace.cs`

**Changes Made**:
- Added `freeze_frames` field (line 41) for per-frame freeze count

**Status**: COMPLETE - Used by all three modes

---

### 2. MultiObjectDetection - ALL PRIORITIES COMPLETE

**File**: `Assets/PassthroughCameraApiSamples/MultiObjectDetection/SentisInference/Scripts/SentisInferenceRunManager.cs`

#### Priority 1: Dropped Frame Queue - COMPLETE

**Changes**:
1. Added fields (lines 90-94):
   - `m_completedFramesQueue` - Queue for all completed frames
   - `m_framesSinceLastDisplay` - Freeze counter

2. Modified `Update()` (lines 745-747):
   - Increment freeze counter BEFORE TryDisplayNewestFrame()

3. Modified `TryDisplayNewestFrame()` (lines 786-814):
   - Enqueue dropped frames instead of overwriting m_lastCompletedTrace
   - Assign freeze count to displayed frame
   - Enqueue displayed frame
   - Reset freeze counter

4. Modified delayed header sending (lines 598-627):
   - Dequeue from queue if available
   - Send X-Prev-Session-Id header
   - Send X-Prev-Freeze-Frames header

5. Added enqueue in error handler (lines 670-672):
   - Enqueue failed frames

6. Added enqueue in timeout handler (lines 1000-1002):
   - Enqueue timeout frames

#### Priority 2: session_id - ALREADY IMPLEMENTED

**Status**: Was already implemented correctly (line 58 declaration, line 109 initialization, line 482 assignment)

#### Priority 3: Freeze Calculation - COMPLETE

**Changes**: Integrated with Priority 1 (freeze counter in Update, assign in TryDisplayNewestFrame)

**Summary**: MultiObjectDetection is FULLY PATCHED and ready to test.

---

### 3. PoseEstimation - ALL PRIORITIES COMPLETE

**File**: `Assets/PassthroughCameraApiSamples/PoseEstimation/Scripts/PoseInferenceRunManager.cs`

#### Priority 1: Dropped Frame Queue - COMPLETE

**Changes**:
1. Added fields (lines 75-79):
   - `m_completedFramesQueue` - Queue for all completed frames
   - `m_framesSinceLastDisplay` - Freeze counter

2. Modified `Update()` (lines 922-923):
   - Increment freeze counter BEFORE TryDisplayNewestFrame()

3. Modified `TryDisplayNewestFrame()` (lines 977-1004):
   - Enqueue dropped frames instead of overwriting m_lastCompletedTrace
   - Assign freeze count to displayed frame
   - Enqueue displayed frame
   - Reset freeze counter

4. Modified delayed header sending (lines 412-439):
   - Dequeue from queue if available
   - Send X-Prev-Session-Id header
   - Send X-Prev-Freeze-Frames header

5. Added enqueue in error handler (lines 484-486):
   - Enqueue failed frames

6. Added enqueue in timeout handler (lines 1128-1130):
   - Enqueue timeout frames

#### Priority 2: session_id - ALREADY IMPLEMENTED

**Status**: Was already implemented correctly (line 43 declaration, line 86 initialization)

#### Priority 3: Freeze Calculation - COMPLETE

**Changes**: Integrated with Priority 1 (freeze counter in Update, assign in TryDisplayNewestFrame)

**Summary**: PoseEstimation is FULLY PATCHED and ready to test.

---

### 4. Segmentation - ALL PRIORITIES COMPLETE

**File**: `Assets/PassthroughCameraApiSamples/Segmentation/SegmentationInference/Scripts/SegmentationInferenceRunManager.cs`

#### Priority 1: Dropped Frame Queue - COMPLETE

**Changes**:
1. Added fields (lines 87-89):
   - `m_completedFramesQueue` - Queue for all completed frames
   - `m_framesSinceLastDisplay` - Freeze counter

2. Modified `Update()` (lines 816-817):
   - Increment freeze counter BEFORE TryDisplayNewestFrame()

3. Modified `TryDisplayNewestFrame()` (lines 855-881):
   - Enqueue dropped frames instead of overwriting m_lastCompletedTrace
   - Assign freeze count to displayed frame
   - Enqueue displayed frame
   - Reset freeze counter

4. Modified delayed header sending (lines 631-660):
   - Dequeue from queue if available
   - Send X-Prev-Session-Id header
   - Send X-Prev-Freeze-Frames header

5. Added enqueue in error handlers:
   - Network error (lines 703-705)
   - JSON parse error (lines 733-735)

6. Added enqueue in timeout handler (lines 1102-1104):
   - Enqueue timeout frames

#### Priority 2: session_id - ALREADY IMPLEMENTED

**Status**: Was already implemented correctly (line 58 declaration, line 106 initialization)

#### Priority 3: Freeze Calculation - COMPLETE

**Changes**: Integrated with Priority 1 (freeze counter in Update, assign in TryDisplayNewestFrame)

**Summary**: Segmentation is FULLY PATCHED and ready to test.

---

### 5. Server Side - ALL COMPLETE

#### frame_state_manager.py - COMPLETE

**File**: `C:\Repo\Github\vision_server\debug\frame_state_manager.py`

**Changes Made**:
1. ✅ session_id already existed in FrameState dataclass (line 17)
2. ✅ Added `freeze_frames_per_frame` to FrameState dataclass (line 66)
3. ✅ Added `freeze_frames_per_frame` parameter to process_frame() signature (line 137)
4. ✅ Added `freeze_frames_per_frame` to complete_frame_data dict (line 268)

**Summary**: frame_state_manager.py is FULLY PATCHED

#### Server Endpoints - COMPLETE

**Files Updated**:
- ✅ `C:\Repo\Github\vision_server\app\routes\infer_human.py`
- ✅ `C:\Repo\Github\vision_server\app\routes\segmentation.py`

**Changes Made** (both endpoints):
1. ✅ Read X-Prev-Session-Id header (lines 278 & 821)
2. ✅ Read X-Prev-Freeze-Frames header (lines 289 & 832)
3. ✅ Pass freeze_frames_per_frame to frame_manager.process_frame() (lines 349 & 893)

**Note**: No object_detection.py endpoint exists - MultiObjectDetection uses segmentation.py

**Summary**: Both endpoints FULLY PATCHED

#### inference_logger.py - COMPLETE

**File**: `C:\Repo\Github\vision_server\debug\inference_logger.py`

**Changes Made**:
1. ✅ session_id already existed in COLUMNS list (line 20)
2. ✅ Added 'freeze_frames_per_frame' to COLUMNS list (line 49)
3. ✅ Added freeze_frames_per_frame parameter to log_inference() signature (line 129)
4. ✅ Added freeze_frames_per_frame to ws.append() row data (line 244)

**Summary**: inference_logger.py is FULLY PATCHED

---

## Testing Plan

### Phase 1: Unity Build Test

**After completing PoseEstimation and Segmentation**:

1. Build and deploy to Quest 3
2. Test each mode separately:
   - PoseEstimation: 20 frames at 10 FPS
   - MultiObjectDetection: 20 frames at 10 FPS
   - Segmentation: 20 frames at 10 FPS

### Phase 2: Server Logs Verification

**Expected in server logs**:
```
[TELEMETRY QUEUE] Frame X DROPPED → queued (queue depth: Y)
[TELEMETRY QUEUE] Frame X DISPLAYED → queued (queue depth: Y)
[TELEMETRY QUEUE] Dequeued frame X (state=Dropped) for delayed headers
```

### Phase 3: Excel Data Verification

**Expected in Excel**:
- Dropped frames appear with state="Dropped"
- unity_drop_ts populated for dropped frames
- drop_reason populated (e.g., "superseded_by_newer_123")
- freeze_frames_per_frame shows ~6-12 for 60 FPS Unity / 10 FPS inference
- session_id consistent within run, unique across runs

### Phase 4: High Load Test

**Setup**:
- Increase inference FPS to 15
- Add artificial server delay (100ms)
- Run for 1 minute

**Expected**:
- 10-20% dropped frames (out-of-order completion)
- All frames (Displayed + Dropped + Failed) logged to Excel
- No data loss

---

## Summary

**✅ ALL WORK COMPLETE**:
- ✅ FrameTrace model update (freeze_frames field)
- ✅ MultiObjectDetection all 3 priorities
- ✅ PoseEstimation all 3 priorities
- ✅ Segmentation all 3 priorities
- ✅ Server frame_state_manager (freeze_frames_per_frame field and parameter)
- ✅ Server endpoints (X-Prev-Session-Id and X-Prev-Freeze-Frames headers)
- ✅ Server logger (freeze_frames_per_frame column)

**Status**: 100% COMPLETE - Ready for testing

---

## What Changed

### Unity Side (3 files modified):
1. **FrameTrace.cs**: Added `freeze_frames` field
2. **All 3 InferenceRunManager.cs files**:
   - Added dropped frame queue (m_completedFramesQueue)
   - Added freeze counter (m_framesSinceLastDisplay)
   - Modified Update() to increment freeze counter
   - Modified TryDisplayNewestFrame() to enqueue all frames
   - Modified delayed header sending to dequeue and send queue frames
   - Added enqueue in error/timeout handlers

### Server Side (3 files modified):
1. **frame_state_manager.py**: Added `freeze_frames_per_frame` field to FrameState and process_frame()
2. **infer_human.py & segmentation.py**: Added reading of X-Prev-Session-Id and X-Prev-Freeze-Frames headers
3. **inference_logger.py**: Added `freeze_frames_per_frame` column

---

**Next Steps**: Testing Phase 1 - Build and deploy to Quest 3, test all three modes
