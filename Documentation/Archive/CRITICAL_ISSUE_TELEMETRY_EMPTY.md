# CRITICAL ISSUE: Telemetry Completely Empty on Server Side

**Date**: 2026-04-17
**Status**: 🔴 BLOCKING - Unity rebuild required

---

## Problem Summary

Despite all code fixes applied to `SegmentationInferenceRunManager.cs`, the server still receives:
- **Empty telemetry**: `telemetry keys: []`
- **Default mode=both**: Because `telemetry.get('mode', 'both')` returns default when mode field is missing
- **No segmentation masks**: Server runs detection+pose instead of segmentation

---

## Evidence from Server Logs

```
[UDP EXCEL DEBUG] Processing frame 32, telemetry keys: []
[UDP EXCEL] No telemetry for frame 32 (expected for first frame)
[UDP EXCEL DEBUG] Telemetry dict: {}

[UDP WORKER] Processing 687bcc49_32 (queue_wait=1.5ms, mode=both)
[UDP WORKER] Decoding image: 640x480, mode=both
[UDP WORKER mode=detection] YOLO detected 2 person(s)
[UDP WORKER mode=both] Pose on 2 crops...
```

This pattern repeats for ALL frames, not just the first few frames as expected with N+1 delayed telemetry.

---

## Root Cause Diagnosis

### Code is Correct ✅

The C# code in `SegmentationInferenceRunManager.cs` has been properly updated:

**BuildTelemetryJson() - Lines 1642-1690**:
```csharp
private string BuildTelemetryJson(FrameTrace trace)
{
    // Build telemetry JSON manually (JsonUtility doesn't support Dictionary!)
    // CRITICAL: mode field must be included for server to use correct inference type
    var json = "{" +
        $"\"scene\":\"Segmentation\"," +
        $"\"session_id\":\"{trace.session_id}\"," +
        $"\"frame_id\":{trace.frame_id}," +
        $"\"mode\":\"segmentation\"," +  // ✅ CRITICAL: Server reads this!
        // ... all other fields
        "}";
    return json;
}
```

**SendFrameUDP() - Lines 1399-1452**:
Shows N+1 delayed telemetry pattern correctly implemented.

### Unity APK is Outdated ❌

**The Unity APK deployed to Quest 3 does NOT contain the updated code.**

**Evidence**:
1. Server logs show telemetry is COMPLETELY EMPTY for ALL frames
2. If the new code were running, telemetry would contain AT MINIMUM the mode field for N+1 frames
3. The N+1 pattern means frame 2 should carry frame 1's telemetry, frame 3 carries frame 2's telemetry, etc.
4. But server shows `telemetry keys: []` for frames 32, 33, 34... ALL frames

**Conclusion**: The Quest 3 is running an OLD APK that:
- Either doesn't send telemetry at all
- Or uses the old JsonUtility.ToJson(Dictionary) code that returns `{}` empty JSON

---

## Required Action

### Step 1: Rebuild Unity APK ⚠️ CRITICAL

**User must rebuild the Unity project** to compile the updated C# code into the APK.

**In Unity Editor**:
1. File → Build Settings
2. Select Android platform
3. Click "Build And Run" (or "Build" then manually deploy)
4. Wait for build to complete (~5-10 minutes)
5. Deploy to Quest 3

### Step 2: Verify Unity Compilation (Before Building)

Check Unity Editor Console for:
- ✅ No red errors
- ✅ "Compilation succeeded" message
- ✅ All scripts compiled without errors

### Step 3: Verify APK Deployment

After building, confirm:
```bash
# Check APK is installed
adb shell pm list packages | findstr passthrough

# Check APK timestamp
adb shell ls -l /data/app/com.meta.passthroughcameraapi*/base.apk
```

### Step 4: Test with Fresh Logs

After deploying new APK, run Segmentation scene and check Unity logs:

**Expected to see** (if new code is running):
```
[UDP SEND] Frame sessionid_26 sent, size=45000 bytes
[UDP SEND] Telemetry JSON length: 850 bytes  ← Non-zero!
[UDP POLL] Starting polling for frame 26
```

**Server should show** (if telemetry is transmitted):
```
[UDP EXCEL DEBUG] Processing frame 2, telemetry keys: ['scene', 'session_id', 'frame_id', 'mode', ...]
[UDP EXCEL DEBUG] Telemetry dict: {'scene': 'Segmentation', 'mode': 'segmentation', ...}
[UDP WORKER] Processing sessionid_2 (queue_wait=1.5ms, mode=segmentation)  ← Correct mode!
[UDP WORKER mode=segmentation] YOLO detected 1 person(s)
[UDP WORKER SEGMENTATION] Mask 1: 340x180, base64: 12480 chars
```

---

## Why N+1 Means We Should See Non-Empty Telemetry

**N+1 Delayed Telemetry Pattern**:
- Frame 1 sent → no telemetry (nothing to report yet)
- Frame 2 sent → includes Frame 1's final state telemetry
- Frame 3 sent → includes Frame 2's final state telemetry
- ...
- Frame 32 sent → includes Frame 31's final state telemetry ✅ SHOULD HAVE TELEMETRY!

**Current Evidence** (from your logs):
- Frame 32 → telemetry keys: [] ❌
- Frame 33 → telemetry keys: [] ❌
- Frame 34 → telemetry keys: [] ❌

This proves the app is NOT running the updated code, because even with N+1 pattern, Frame 32 should carry Frame 31's telemetry.

---

## Files Modified (Ready for Build)

All code changes have been successfully applied:

1. **SegmentationInferenceRunManager.cs**:
   - Line 1642-1690: BuildTelemetryJson() with manual JSON string
   - Line 1695-1701: EscapeJson() helper method
   - Lines 1576-1619: UDP metrics extraction
   - Lines 1444-1451: Upload bytes tracking

2. **Segmentation.unity**:
   - jpegQuality: 40 (reduced from 60)
   - downsampleFactor: 2 (reduced from 1)
   - m_useUDPTransport: 1 (enabled)

3. **Server-side** (already deployed and running):
   - UDP worker handles segmentation mode ✅
   - Mode extraction from telemetry.get('mode', 'both') ✅

---

## Verification Checklist

### Before Build
- [x] Code updated with manual JSON string construction
- [x] Mode field added to telemetry
- [x] Scene file updated with UDP settings
- [x] Server running with segmentation mode handler

### After Build (User Action Required)
- [ ] Unity APK rebuilt with updated code
- [ ] APK deployed to Quest 3
- [ ] Segmentation scene tested
- [ ] Unity logs show non-zero telemetry length
- [ ] Server logs show mode=segmentation
- [ ] Server logs show non-empty telemetry keys
- [ ] Segmentation masks render correctly

---

## Expected Results After Rebuild

### Unity Logs (Segmentation Scene)

```
[UDP SEND] Frame sessionid_26 sent, size=45000 bytes
[UDP SEND] Telemetry JSON: {"scene":"Segmentation","mode":"segmentation",...}
[UDP POLL] Starting polling for frame 26
[UDP POLL] Frame 26 received after 0.35s
[SEGMENTATION] Received response with 1 detection(s)
[SEGMENTATION] Detection 1: person, conf=0.88, has_mask=True
```

### Server Logs

```
[UDP WORKER] Processing sessionid_1 (queue_wait=2.3ms, mode=segmentation)
[UDP WORKER] Decoding image: 640x480, mode=segmentation
[UDP WORKER mode=segmentation] YOLO detected 5 objects, 1 person(s)
[UDP WORKER SEGMENTATION] Mask 1: 340x180, base64: 12480 chars
[UDP WORKER mode=segmentation] 1 person(s) with masks
[UDP WORKER] ✓ Completed sessionid_1 (processing=340.2ms, total=342.5ms)

[UDP EXCEL DEBUG] Processing frame 2, telemetry keys: ['scene', 'session_id', 'frame_id', 'mode', 'unity_send_ts', ...]
[UDP EXCEL DEBUG] Telemetry dict: {'scene': 'Segmentation', 'mode': 'segmentation', 'frame_id': 2, ...}
```

### Excel Output

| scene | mode | detection_count | avg_confidence | latency_ms | upload_bytes | download_bytes |
|-------|------|-----------------|----------------|------------|--------------|----------------|
| Segmentation | segmentation | 1 | 0.88 | 485.3 | 45000 | 18500 |
| Segmentation | segmentation | 1 | 0.90 | 472.1 | 43000 | 17890 |

**All fields should be non-zero!**

---

## Summary

**Problem**: Server receives empty telemetry for all frames, causing mode to default to 'both'
**Root Cause**: Unity APK on Quest 3 doesn't contain the updated code
**Solution**: Rebuild Unity APK and deploy to Quest 3
**Status**: Waiting for user to rebuild and test

All code changes are complete and ready. The only remaining step is to rebuild the Unity APK.

---

**Last Updated**: 2026-04-17 10:30 UTC
**Status**: 🔴 BLOCKING - Rebuild required
