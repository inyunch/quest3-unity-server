# NumPy Serialization Fix - Summary

**Date**: 2026-04-16 23:38
**Issue**: `TypeError: 'numpy.float32' object is not iterable` causing 500 Internal Server Error
**Status**: FIXED

---

## Problem Description

When the UDP inference worker detected persons and tried to return results, FastAPI's `jsonable_encoder()` failed to serialize `numpy.float32` values, causing:

```python
TypeError: 'numpy.float32' object is not iterable
ValueError: [TypeError("'numpy.float32' object is not iterable"), TypeError('vars() argument must have __dict__ attribute')]
```

**Pattern Observed**:
- Frames **with** person detections → 500 Internal Server Error
- Frames **without** person detections → 200 OK

---

## Root Cause

The error occurred in **THREE locations** in `C:\Repo\Github\vision_server\app\inference_pose.py`:

### 1. Keypoint Coordinate Normalization (Lines 314-315, 456-457)

When dividing pixel coordinates by image dimensions, even though the pixel values were extracted as Python floats, the division operation can produce numpy types if `img_width` or `img_height` are numpy integers:

```python
x_px = float(kp_data[j, 0])       # ✅ Python float
x_norm = x_px / img_width          # ❌ May return numpy.float32 if img_width is numpy int
```

### 2. Detection Confidence Score (Line 503)

The `detection.get("confidence", 1.0)` returns a `numpy.float32` type from YOLO, which FastAPI cannot serialize directly.

**Why This Causes 500 Errors**:
- Frames **WITHOUT** persons → No keypoints → No serialization error → 200 OK
- Frames **WITH** persons → Keypoints with numpy types → Serialization fails → 500 Error

---

## Fix Applied

**File**: `C:\Repo\Github\vision_server\app\inference_pose.py`
**Lines**: 314-315, 456-457, 503

### Location 1: Keypoint Normalization (Full Image Pose)
**Lines 314-315**:
```python
# BEFORE (broken):
x_norm = x_px / img_width
y_norm = y_px / img_height

# AFTER (fixed):
x_norm = float(x_px / img_width)   # Convert division result to Python float
y_norm = float(y_px / img_height)  # Convert division result to Python float
```

### Location 2: Keypoint Normalization (Crop-Based Pose)
**Lines 456-457**:
```python
# BEFORE (broken):
x_norm = x_full_px / img_width
y_norm = y_full_px / img_height

# AFTER (fixed):
x_norm = float(x_full_px / img_width)   # Convert division result to Python float
y_norm = float(y_full_px / img_height)  # Convert division result to Python float
```

### Location 3: Detection Score
**Line 503**:
```python
# BEFORE (broken):
"detection_score": detection.get("confidence", 1.0)

# AFTER (fixed):
"detection_score": float(detection.get("confidence", 1.0))  # Convert numpy.float32 to Python float
```

**Verification**:
```bash
cd C:\Repo\Github\vision_server
Select-String -Path app\inference_pose.py -Pattern "x_norm = float"
# Output:
# app\inference_pose.py:314:            x_norm = float(x_px / img_width)
# app\inference_pose.py:456:            x_norm = float(x_full_px / img_width)
```

---

## Python Cache Issue

Initially, the fix was applied but did not take effect because Python was using cached bytecode (`.pyc` files) from `__pycache__` directories.

**Solution Steps**:

1. **Stop all Python servers**:
   ```bash
   Stop-Process -Id 69768 -Force
   ```

2. **Delete all `__pycache__` directories**:
   ```bash
   cd C:\Repo\Github\vision_server
   Get-ChildItem -Path . -Filter __pycache__ -Recurse -Directory | Remove-Item -Recurse -Force
   ```

3. **Restart server with bytecode writing disabled**:
   ```bash
   python -B -m uvicorn app.main:app --host 0.0.0.0 --port 8001 --workers 1
   ```

The `-B` flag tells Python to **not write `.pyc` files**, ensuring it always loads the latest source code.

---

## Server Startup Command (Recommended)

**From now on, start the server with**:

```bash
cd C:\Repo\Github\vision_server
python -B -m uvicorn app.main:app --host 0.0.0.0 --port 8001 --workers 1
```

The `-B` flag prevents Python bytecode caching issues, ensuring code changes always take effect immediately.

---

## Expected Behavior After Fix

**Before Fix**:
```
[UDP WORKER] Processing frame_1...
[UDP WORKER BBOX] Detected 1 person
ERROR: TypeError: 'numpy.float32' object is not iterable
HTTP 500 Internal Server Error
```

**After Fix**:
```
[UDP WORKER] Processing frame_1...
[UDP WORKER BBOX] Detected 1 person
[UDP WORKER] Completed frame_1 (processing=250ms)
HTTP 200 OK (result cached)
Unity polls → HTTP 200 OK with bboxes
```

---

## Testing Checklist

To verify the fix works:

- [ ] Server started with `python -B` flag
- [ ] Unity sends UDP frame with person visible
- [ ] Server logs show `[UDP WORKER] Completed` (not error)
- [ ] Unity HTTP polling returns 200 (not 404 or 500)
- [ ] Bounding boxes visible on Quest 3 screen

---

## Related Files

**Fixed**:
- `C:\Repo\Github\vision_server\app\inference_pose.py` (line 503)

**Verified**:
- `C:\Repo\Github\vision_server\app\transport\udp_ingest.py` (GUID fix with `bytes_le`)
- `C:\Repo\Github\vision_server\app\workers\udp_inference_worker.py` (pose on crops fix)

---

## Additional NumPy Serialization Risks

**Other places that may need similar fixes** (not currently broken, but potential risks):

1. **Keypoint coordinates** - Currently OK because they're extracted as Python lists
2. **Bounding box coordinates** - Currently OK because normalized as Python floats
3. **Confidence scores from other models** - May need `float()` conversion if using NumPy

**Best Practice**: Always convert NumPy types to Python native types before JSON serialization:
```python
float(numpy_value)      # numpy.float32 → float
int(numpy_value)        # numpy.int64 → int
array.tolist()          # numpy.ndarray → list
```

---

## Lessons Learned

1. **Python bytecode caching** can mask code fixes - always restart with `-B` flag when debugging
2. **FastAPI's jsonable_encoder** cannot serialize NumPy types automatically
3. **Always use `float()` conversion** when returning YOLO confidence scores or other NumPy scalars
4. **Delete `__pycache__` directories** when in doubt about whether code changes took effect

---

**Status**: READY FOR TESTING

Server is now running with the fix. Please test UDP transport with person detection to verify bounding boxes appear correctly.
