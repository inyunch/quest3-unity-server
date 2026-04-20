# Freeze Frames Zero Diagnostic

**Date**: 2026-04-15
**Issue**: All frames in Excel show `freeze_frames_per_frame = 0`
**Status**: 🔍 DIAGNOSIS COMPLETE - Needs rebuild and redeploy

---

## Problem Analysis

### User's Data (Segmentation Mode)
```
frame_id | final_state | freeze_frames_per_frame | unity_display_ts (intervals)
---------|-------------|------------------------|---------------------------
1        | Displayed   | 0                      | 29.536s
2        | Displayed   | 0                      | 29.716s (+180ms)
3        | Displayed   | 0                      | 30.216s (+500ms)
4        | Displayed   | 0                      | 30.648s (+432ms)
... (frames 5-18 all show 0)
```

### Expected Behavior

For Unity running at 60 FPS (16.67ms per frame):
- **180ms gap** = 180 / 16.67 ≈ 11 frames → `freeze_frames = 11 - 1 = 10`
- **500ms gap** = 500 / 16.67 ≈ 30 frames → `freeze_frames = 30 - 1 = 29`
- **432ms gap** = 432 / 16.67 ≈ 26 frames → `freeze_frames = 26 - 1 = 25`

### Actual Behavior
All frames show `freeze_frames_per_frame = 0` ❌

---

## Root Cause: Old Build on Quest 3

### Evidence

1. **Code exists in source** (verified in `SegmentationInferenceRunManager.cs:902`)
   ```csharp
   // PRIORITY 3: Assign freeze count
   newest.freeze_frames = m_framesSinceLastDisplay - 1;
   m_framesSinceLastDisplay = 0;
   Debug.Log($"[FREEZE METRICS] Frame {newest.frame_id} displayed after {newest.freeze_frames} Unity frames");
   ```

2. **Counter increments in Update()** (line 838)
   ```csharp
   m_framesSinceLastDisplay++;
   ```

3. **Freeze frames sent via delayed telemetry** (line 659)
   ```csharp
   request.SetRequestHeader("X-Prev-Freeze-Frames", traceToSend.freeze_frames.ToString());
   ```

4. **Git status shows new/modified files**
   - Segmentation mode files are marked as new (`??`) or modified (`M`)
   - This indicates recent code changes not yet deployed

### Conclusion

**The APK deployed to Quest 3 was built BEFORE the freeze_frames calculation code was added.**

The C# `int` type defaults to 0, so:
- Old build: `freeze_frames` is never assigned → stays 0
- Sent to server as 0
- Written to Excel as 0

---

## Solution: Rebuild and Redeploy

### Steps to Fix

1. **Build new APK with latest code**
   ```bash
   # In Unity Editor:
   # 1. File → Build Settings
   # 2. Select Android platform
   # 3. Click "Build" or "Build And Run"
   # 4. Make sure all scenes are included (especially Segmentation scene)
   ```

2. **Deploy to Quest 3**
   ```bash
   # Option A: Via Unity Editor
   # Click "Build And Run" (requires Quest 3 connected via USB)

   # Option B: Manual install
   adb install -r path/to/PassthroughCameraSamples.apk
   ```

3. **Verify the fix**
   - Run a test session on Quest 3
   - Check the new Excel file
   - Look for non-zero `freeze_frames_per_frame` values

4. **Expected result after fix**
   ```
   frame_id | freeze_frames_per_frame | unity_display_ts | Calculation
   ---------|------------------------|------------------|-------------
   1        | 10-30 (varies)         | 29.536s          | First frame varies based on startup
   2        | ~10                    | 29.716s          | 180ms / 16.67ms ≈ 11 - 1 = 10
   3        | ~29                    | 30.216s          | 500ms / 16.67ms ≈ 30 - 1 = 29
   4        | ~25                    | 30.648s          | 432ms / 16.67ms ≈ 26 - 1 = 25
   ```

---

## Verification Checklist

After rebuild and redeploy, verify:

- [ ] Build completed without errors
- [ ] APK deployed to Quest 3
- [ ] Run test session (10-20 frames minimum)
- [ ] Download new Excel file from server
- [ ] Check `freeze_frames_per_frame` column shows non-zero values
- [ ] Verify calculation makes sense (compare with display timestamp gaps)

### Quick Verification Script

```python
import pandas as pd

# Load the NEW Excel file (after rebuild)
df = pd.read_excel('inference_log_2026-04-15_NEW.xlsx')

# Filter to your test session
session_df = df[df['session_id'] == 'YOUR-SESSION-ID-HERE']

# Check freeze frames distribution
print("Freeze frames distribution:")
print(session_df['freeze_frames_per_frame'].describe())

# Should show:
# mean: ~20-30 (for 10 FPS inference at 60 FPS Unity)
# min: >= 0 (can be 0 for very fast responses)
# max: varies based on server load

# Check for all zeros (BAD - means old build still deployed)
if (session_df['freeze_frames_per_frame'] == 0).all():
    print("❌ STILL ALL ZEROS - Old build still running on Quest 3!")
    print("   Make sure you deployed the NEW APK")
else:
    print("✅ Freeze frames working correctly!")
```

---

## Why Zero is Technically Possible (But Rare)

In normal operation, `freeze_frames = 0` means:
- Frame sent at Update() N
- Response arrived and displayed at Update() N+1
- Only 1 Unity frame elapsed
- `freeze_frames = 1 - 1 = 0` ✅

This would require:
- **Total latency < 16.67ms** (one Unity frame at 60 FPS)
- **Extremely fast server** (< 5ms inference)
- **Low network latency** (< 5ms round-trip)

**Possible scenarios for legitimate zero:**
1. First frame when server is warm (cached models)
2. Very simple scene (1 person, minimal computation)
3. Local server on same WiFi network

**But ALL 18 frames showing 0 is NOT realistic** - indicates old build.

---

## Debug Logs to Check

If you want to verify the code is running, check Unity logs for:

```
[FREEZE METRICS] Frame {frame_id} displayed after {N} Unity frames
```

**In old build**: This log message will NOT appear
**In new build**: This log appears every time a frame is displayed

### How to Access Quest 3 Logs

```bash
# View live logs
adb logcat -s Unity

# Search for freeze metrics
adb logcat -s Unity | grep "FREEZE METRICS"

# Should see output like:
# [FREEZE METRICS] Frame 1 displayed after 25 Unity frames
# [FREEZE METRICS] Frame 2 displayed after 12 Unity frames
# [FREEZE METRICS] Frame 3 displayed after 31 Unity frames
```

---

## Next Steps

1. ✅ **Rebuild** Unity project with latest code
2. ✅ **Deploy** new APK to Quest 3
3. ✅ **Run test** session (capture 10-20 frames)
4. ✅ **Verify** Excel shows non-zero freeze_frames_per_frame
5. ✅ **Confirm** calculation matches display timestamp intervals

Once verified, the freeze_frames metric will accurately show how many Unity frames passed between displayed inference results, which is critical for understanding the user experience (perceived smoothness vs. stuttering).

---

## Summary

**Problem**: All frames show `freeze_frames_per_frame = 0`

**Root Cause**: Quest 3 is running an OLD build from before freeze_frames code was added

**Solution**: Rebuild and redeploy with latest code

**Verification**: Check new Excel file shows non-zero values matching timestamp intervals

**Expected after fix**: Most frames will show 20-40 freeze frames (for 10 FPS inference at 60 FPS Unity)
