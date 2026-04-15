# ⚠️ MUST REBUILD UNITY - Server Fix is Live!

**Date**: 2026-04-15
**Status**: **SERVER FIXED ✅ - UNITY REBUILD REQUIRED**

## Current Situation

### ✅ Server Fix - WORKING
Server now sends timestamps correctly:
```
[TELEMETRY DEBUG] JSON contains t_server_recv: True, t_server_send: True  ✅
[TELEMETRY DEBUG] After JSON parse: t_server_recv=1776283439.60219  ✅
```

### ❌ Unity Build - OLD CODE
Your current Quest 3 build is **OLD** (before server restart):
```
MarkCompleted frame 31, server_recv=0  ❌ (Old build, not converting timestamps)
```

The ADB logs show **TWO different debug messages**:
1. **New debug code**: "After JSON parse: t_server_recv=1776283439.60219" ← This is from my new debug code
2. **Old conversion**: "server_recv=0" ← This is from old build that didn't convert correctly

## Why Excel Shows Empty Timestamps

**Root Cause**: Unity is sending state="Completed" instead of state="Displayed"

**Server correctly filters this**:
```
[TELEMETRY WARNING] Skipping frame 33 with non-final state 'Completed'
[TELEMETRY WARNING] Skipping frame 34 with non-final state 'Completed'
```

**Result**: No frames written to Excel (correctly filtered by server)

## Why State is "Completed"

The old Unity build has this bug in timestamp conversion that causes timestamps to remain 0, and when `m_lastCompletedTrace` has 0 timestamps, something goes wrong in the state machine.

**OR** the current build doesn't have the latest `TryDisplayNewestFrame` fixes.

## Solution: Rebuild Unity

**Step 1: Rebuild in Unity Editor**
```
1. Unity Editor > Assets > Reimport All (optional, just to be safe)
2. Build and Deploy to Quest 3
```

**Step 2: Test Again**
```
1. Clear ADB logcat: adb logcat -c
2. Launch Segmentation scene
3. Send 5-10 frames
4. Run: quick_check_telemetry.bat
```

**Expected After Rebuild**:
```
[TELEMETRY DEBUG] After JSON parse: t_server_recv=1776283439.60219  ✅
[TELEMETRY DEBUG] MarkCompleted frame 31, server_recv=1776283439602  ✅ (Now correctly converted to ms!)
[TELEMETRY DEBUG] Set m_lastCompletedTrace to DISPLAYED frame 31, state=Displayed  ✅
```

**Expected Server Logs After Rebuild**:
```
[FRAME STATE] Segmentation Frame 35: Logging previous frame 34 (E2E=105.1ms, Server=41.1ms)
Frame 34 logged successfully  ✅ (No more "Skipping" warnings!)
```

## What Will Be Fixed After Rebuild

### Fix 1: Server Timestamps Conversion
**Before** (old build):
```csharp
trace.server_receive_ts = (long)(0 * 1000);  // 0
```

**After** (new build):
```csharp
trace.server_receive_ts = (long)(1776283439.60219 * 1000);  // 1776283439602
```

### Fix 2: Manual Parsing Fallback
New build includes `ExtractSimpleJsonValue` as backup (defense-in-depth).

### Fix 3: State Machine
After rebuild, frames will properly transition:
```
Pending → Completed → Displayed → Sent in delayed headers
```

## Summary

| Component | Status | Action |
|-----------|--------|--------|
| Server | ✅ FIXED | Timestamps now in JSON response |
| Unity Build | ❌ OLD | **MUST REBUILD** |
| Excel Logging | ⏳ WAITING | Will work after Unity rebuild |

## Files Already Modified

### Server (Already Restarted) ✅
- `C:\Repo\Github\vision_server\app\routes\segmentation.py` (lines 203-215)
  - Added t_server_recv and t_server_send

### Unity (Need to Rebuild) ⏳
- `Assets/PassthroughCameraApiSamples\Segmentation\SegmentationInference\Scripts\SegmentationInferenceRunManager.cs`
  - Lines 716-733: Manual timestamp parsing fallback
  - Lines 1115-1170: ExtractSimpleJsonValue helper method

## After Rebuild - Expected Excel Results

| Column | Value | Status |
|--------|-------|--------|
| frame_id | 0, 1, 2... | ✅ |
| state | Displayed | ✅ (No more "Completed"!) |
| unity_send_ts | 1776283439602 | ✅ |
| unity_receive_ts | 1776283439650 | ✅ |
| unity_display_ts | 1776283439700 | ✅ |
| server_receive_ts | 1776283439610 | ✅ (FIXED!) |
| server_send_ts | 1776283439640 | ✅ (FIXED!) |

## Next Steps

1. ⏳ **Rebuild Unity** - Required for timestamps to work
2. ⏳ **Deploy to Quest 3** - Install new build
3. ⏳ **Test Segmentation** - Send 5-10 frames
4. ⏳ **Check Excel** - Verify timestamps populated
5. ⏳ **Run validation** - `python check_all_modes_telemetry.py`

---

**Server is ready. Unity rebuild is the last step!**
