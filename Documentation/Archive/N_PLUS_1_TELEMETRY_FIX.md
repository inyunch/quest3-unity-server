# N+1 Telemetry Pipeline Fix

**Date**: 2026-04-17
**Status**: ⚠️ **SERVER BUG FOUND - UDP worker not starting**

---

## Summary

The Excel logging implementation is complete and the `TypeError` has been fixed, but a critical server bug was discovered: **the UDP worker is not starting** because the startup event handler returns early when CUDA is unavailable.

---

## Fixes Applied

### 1. ✅ Fixed `TypeError: log_inference() got an unexpected keyword argument 'timestamp'`

**File**: `C:\Repo\Github\vision_server\app\workers\udp_inference_worker.py`
**Lines**: 402-451

**Issue**: The `log_inference()` function auto-generates the timestamp internally, but we were passing it as a parameter.

**Fix**: Removed `'timestamp': time.time()` from the `row_data` dict in `_log_frame_to_excel()`.

**Status**: ✅ Fixed

---

## Critical Server Bug Discovered

### ⚠️ UDP Worker Not Starting on CPU-Only Systems

**File**: `C:\Repo\Github\vision_server\app\main.py`
**Lines**: 108-250

**Problem**: The `warmup_models()` startup event handler has this code:

```python
@app.on_event("startup")
async def warmup_models():
    # ... GPU warmup code ...

    # Check CUDA availability
    if not torch.cuda.is_available():
        print("⚠️  CUDA not available - running on CPU")
        print("   Performance will be significantly slower (200-1300ms vs 10-50ms)")
        print("=" * 60)
        return  # ← EXITS FUNCTION EARLY!

    # ... GPU warmup continues ...

    # Initialize bounded admission queue (LINE 201)
    # Initialize UDP frame ingest (LINE 212)
    # Start result cache (LINE 226)
    # Start UDP inference worker (LINE 239)
    # ↑ NONE OF THIS CODE RUNS ON CPU-ONLY SYSTEMS!
```

**Impact**: On systems without CUDA (running on CPU), the UDP components are never initialized:
- ❌ Bounded admission queue not created
- ❌ UDP listener not started (port 8002 not open)
- ❌ Result cache not initialized
- ❌ UDP inference worker not started

**Expected Behavior**:
- Server should initialize UDP components regardless of CUDA availability
- GPU warmup should be optional, UDP infrastructure should always start

**Evidence** (Server Logs):
```
[GPU BALANCE] CUDA not available, running on CPU
============================================================
GPU WARMUP - Eliminating Cold Start Delays
============================================================
⚠️  CUDA not available - running on CPU
   Performance will be significantly slower (200-1300ms vs 10-50ms)
============================================================
INFO:     Application startup complete.
```

**Missing** (Should appear but doesn't):
```
============================================================
BOUNDED ADMISSION QUEUE - Initialized
============================================================
  Max pending frames: 3
  Drop policy: FIFO (oldest pending dropped when full)
============================================================

============================================================
UDP FRAME INGEST - Started
============================================================
  Listening on: 0.0.0.0:8002
  Max frame size: 512.0 KB
  Deduplication TTL: 5s
============================================================

============================================================
RESULT CACHE - Initialized
============================================================
  TTL: 30s
  Max size: 1000
  Cleanup interval: 10s
============================================================

============================================================
UDP INFERENCE WORKER - Started
============================================================
  Worker will process UDP frames from admission queue
  Inference results stored in result cache
  Unity polls via GET /response/{session_id}_{frame_id}
============================================================

[UDP WORKER] Worker loop started, waiting for UDP frames...
```

---

## Fix Required

**File**: `C:\Repo\Github\vision_server\app\main.py`
**Function**: `warmup_models()` (lines 108-250)

**Solution**: Move UDP initialization code BEFORE the CUDA check, or remove the early return.

**Option 1** (Recommended): Restructure to always initialize UDP:

```python
@app.on_event("startup")
async def warmup_models():
    """
    Warmup GPU models and initialize UDP infrastructure.
    """
    print("")
    print("=" * 60)
    print("SERVER STARTUP")
    print("=" * 60)

    # Check CUDA availability
    if not torch.cuda.is_available():
        print("⚠️  CUDA not available - running on CPU")
        print("   Performance will be significantly slower (200-1300ms vs 10-50ms)")
        print("=" * 60)
        print("")
        # DO NOT RETURN - continue with UDP initialization
    else:
        # GPU warmup code (lines 130-199)
        print(f"✓ CUDA available")
        print(f"  Device: {torch.cuda.get_device_name(0)}")
        # ... warmup YOLO, Pose, Segmentation ...
        print("=" * 60)
        print("WARMUP COMPLETE - Server ready for inference at full speed")
        print("=" * 60)
        print("")

    # Initialize bounded admission queue (ALWAYS run)
    queue = get_admission_queue()
    # ... rest of UDP initialization ...
```

**Option 2**: Simply remove the `return` statement on line 128:

```python
if not torch.cuda.is_available():
    print("⚠️  CUDA not available - running on CPU")
    print("   Performance will be significantly slower (200-1300ms vs 10-50ms)")
    print("=" * 60)
    # REMOVED: return  ← Delete this line
    print("")
```

---

## Testing Checklist

Once the server bug is fixed:

- [ ] **Restart server** with fixed `main.py`
- [ ] **Verify UDP worker starts** - Check logs for:
  ```
  [UDP WORKER] Worker loop started, waiting for UDP frames...
  ```
- [ ] **Verify port 8002 is listening** - Check with:
  ```powershell
  netstat -an | Select-String '8002'
  # Should show: UDP    0.0.0.0:8002           *:*
  ```
- [ ] **Build and deploy Unity** with N+1 telemetry
- [ ] **Run on Quest 3** for ~30 seconds
- [ ] **Check Unity logs** for `[UNITY TELEMETRY]` messages
- [ ] **Check server logs** for `[UDP EXCEL DEBUG]` and `[UDP EXCEL] Logged frame X`
- [ ] **Check Excel file** at `C:\Repo\Github\vision_server\debug\logs\inference_log_2026-04-17.xlsx`
- [ ] **Verify data quality**:
  - First 1-2 frames show "No telemetry" (expected)
  - Frame 3+ show "Logged frame X" (success)
  - Excel has rows for frames 2, 3, 4, ...
  - All 34 columns present in correct order
  - Only final states (Displayed/Dropped/Failed)
  - No duplicate frame_ids

---

## Files Modified in This Session

### Unity Files (N+1 Telemetry Implementation)

1. **`Assets/.../MultiObjectDetection/.../SentisInferenceRunManager.cs`**
   - Lines 1207-1223: Added `[UNITY TELEMETRY]` debug logging in `SendFrameUDP()`
   - Lines 1371-1447: Implemented `GetPreviousFrameTelemetryJson()` (serializes FrameTrace to JSON)
   - Lines 172-181: Removed Unity-side CSV export from `OnDestroy()`

2. **`Assets/.../PoseEstimation/Scripts/PoseInferenceRunManager.cs`**
   - Removed Unity-side CSV export from `OnDestroy()`

### Server Files (Excel Logging Implementation)

1. **`C:\Repo\Github\vision_server\app\workers\udp_inference_worker.py`**
   - Lines 135-138: Added `_log_frame_to_excel()` call after inference
   - Lines 341-462: Implemented `_log_frame_to_excel()` method
     - Extracts telemetry from `req.headers` (N+1 delayed packet)
     - Validates final state (Displayed/Dropped/Failed)
     - Builds 34-column row data dict
     - Calls `log_async()` to write to Excel
     - ✅ Fixed: Removed `timestamp` parameter (auto-generated)

2. **`C:\Repo\Github\vision_server\app\main.py`**
   - ⚠️ **BUG FOUND**: Lines 124-128 return early on CPU-only systems
   - ⚠️ **BUG IMPACT**: UDP worker never starts (lines 239-250 not reached)

---

## Current State

### ✅ Completed
- Unity N+1 telemetry implementation
- Unity debug logging (`[UNITY TELEMETRY]`)
- Server telemetry extraction fix (`req.headers` is the dict)
- Server debug logging (`[UDP EXCEL DEBUG]`)
- Server Excel logging implementation
- Fixed `TypeError` (removed timestamp parameter)

### ⚠️ Blocked
- **UDP worker not starting** (server bug in `main.py`)
- Cannot test end-to-end until server is fixed

### 🔧 Next Steps
1. Fix `main.py` startup bug (move UDP init before CUDA check or remove early return)
2. Restart server and verify UDP worker starts
3. Deploy Unity build to Quest 3
4. Test end-to-end and verify Excel logging works

---

**Last Updated**: 2026-04-17 04:13 UTC

---

## Quick Fix Commands

### Fix Server Startup Bug

**Option 1** (Safe): Remove the early return on line 128:

```python
# In C:\Repo\Github\vision_server\app\main.py, line 128:
# DELETE THIS LINE:
        return

# So lines 124-128 become:
    if not torch.cuda.is_available():
        print("⚠️  CUDA not available - running on CPU")
        print("   Performance will be significantly slower (200-1300ms vs 10-50ms)")
        print("=" * 60)
        # Removed return - continue with UDP initialization
```

### Restart Server

```powershell
# Kill existing server
tasklist | findstr python
taskkill //F //PID <PID_FROM_ABOVE>

# Start server
cd C:\Repo\Github\vision_server
python -m uvicorn app.main:app --host 0.0.0.0 --port 8001 --workers 1
```

### Verify UDP Worker Started

```powershell
# Check logs for UDP worker message
# Should see: [UDP WORKER] Worker loop started, waiting for UDP frames...

# Check port 8002 is listening
netstat -an | Select-String '8002'
# Should show: UDP    0.0.0.0:8002           *:*
```
