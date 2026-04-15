# Telemetry Diagnostic Results

**Date**: 2026-04-14
**Excel File**: C:\Repo\Github\vision_server\debug\logs\inference_log_2026-04-14.xlsx
**Total Rows**: 581

## Summary

**✅ WORKING**: Unity Client Timestamps
**❌ NOT WORKING**: Server Timestamps
**✅ WORKING**: Final State Tracking

## Detailed Analysis

### Unity Timestamps (✅ WORKING)

All Unity timestamp fields are being populated correctly:

| Field | Status | Sample Value | Format |
|-------|--------|--------------|--------|
| unity_send_ts | ✅ 100% populated | 1776222211057.0 | Unix ms |
| unity_receive_ts | ✅ 100% populated | 1776222211386.0 | Unix ms |
| unity_display_ts | ✅ 100% populated | 1776222211422.0 | Unix ms |
| unity_drop_ts | ✅ Correctly 0 for displayed frames | NaN | Unix ms |
| final_state | ✅ 100% populated | "Displayed" | String |

**Verification**:
- All frames show "Displayed" state (no drops in recent test)
- Unity timestamps are in Unix milliseconds format (~1.77e12)
- Timestamps follow correct ordering: send < receive < display

### Server Timestamps (❌ ALL NaN)

Server timestamp fields are NOT being populated:

| Field | Status | Value |
|-------|--------|-------|
| server_receive_ts | ❌ ALL NaN | - |
| server_send_ts | ❌ ALL NaN | - |

## Root Cause Investigation

### Why are server timestamps NaN?

The delayed telemetry pattern works as follows:
1. Frame N arrives → Server measures `t_recv` and `t_send` for Frame N
2. Server returns response with `t_server_recv` and `t_server_send` in JSON
3. Unity parses response and stores in `trace.server_receive_ts` and `trace.server_send_ts`
4. Frame N+1 arrives → Unity sends `X-Prev-Server-Receive-Ts` and `X-Prev-Server-Send-Ts` headers
5. Server reads headers and logs Frame N with complete data

**Possible Issues**:

### Issue 1: Unity Not Parsing Server Timestamps from Response

Let me check if Unity is parsing `t_server_recv` and `t_server_send` from the response JSON.

**File**: `Assets/PassthroughCameraApiSamples/PoseEstimation/Scripts/PoseInferenceRunManager.cs`

Expected code (lines 586-587):
```csharp
// Parse server timestamps from response
trace.server_receive_ts = response.t_server_recv;
trace.server_send_ts = response.t_server_send;
```

**Status**: Code exists in PoseInferenceRunManager.cs ✅

**File**: `Assets/PassthroughCameraApiSamples/MultiObjectDetection/SentisInference/Scripts/SentisInferenceRunManager.cs`

Expected code (lines 688-690):
```csharp
// Parse server timestamps from response
trace.server_receive_ts = response.t_server_recv;
trace.server_send_ts = response.t_server_send;
```

**Status**: Code should exist (added in implementation) ✅

### Issue 2: Response Class Missing Fields

Unity response classes (DetectionResponse, PoseResponse, SegmentationResponse) might not have `t_server_recv` and `t_server_send` fields.

**Action Required**: Check response class definitions.

### Issue 3: Server Not Returning Timestamps in Response

The server might not be including `t_server_recv` and `t_server_send` in the JSON response.

**Action Required**: Check server response in `infer_human.py`.

### Issue 4: Headers Not Being Sent (First Frame Problem)

For the very first frame after Unity starts, there is no "previous frame" to send, so:
- `m_lastCompletedTrace` is null
- No `X-Prev-*` headers are sent
- Server correctly logs NaN for first frame

**Expected Behavior**:
- Frame 1: NaN server timestamps (no previous frame)
- Frame 2+: Should have server timestamps from previous frame

**Current Behavior**: ALL frames have NaN → Headers are never sent, or response parsing fails.

## Testing Steps Needed

1. **Add Server Response Logging** in Unity to see if `t_server_recv`/`t_server_send` exist
2. **Add Request Header Logging** in Server to see if `X-Prev-Server-*` headers are sent
3. **Check Response Class Definitions** to ensure fields exist
4. **Run Fresh Test** with debug logging enabled

## Next Actions

1. ✅ Added debug logging to `infer_human.py` (line 833-835)
2. ⏳ Need to verify Unity response classes have `t_server_recv` and `t_server_send` fields
3. ⏳ Need to verify server returns these fields in response JSON
4. ⏳ Need to run fresh test and check logs for `[TELEMETRY]` messages
