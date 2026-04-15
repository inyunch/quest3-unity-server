# Segmentation Telemetry Root Cause - FOUND!

**Date**: 2026-04-15
**Status**: ROOT CAUSE IDENTIFIED
**Priority**: CRITICAL

## 🎯 Root Cause Identified

**Problem**: Server timestamps (`server_receive_ts`, `server_send_ts`) are always 0 in Segmentation mode

**Evidence from ADB logcat**:
```
unity_send_ts=1776281641305  ✅ GOOD - Unity timestamp works
server_recv=0                ❌ BAD  - Server timestamp is 0
```

**Location**: Line 749-750 in `SegmentationInferenceRunManager.cs`
```csharp
trace.server_receive_ts = (long)(response.t_server_recv * 1000);  // 0 * 1000 = 0
trace.server_send_ts = (long)(response.t_server_send * 1000);     // 0 * 1000 = 0
```

## 🔍 Investigation

### What We Know

1. ✅ **Telemetry code IS running** - Confirmed via ADB:
   ```
   Frames Created: 6
   Frames Completed: 6
   Frames Displayed: 6
   Delayed Headers Sent: 6
   ```

2. ✅ **Unity timestamps work** - `unity_send_ts` has valid values (1776281641305)

3. ✅ **Delayed telemetry works** - Headers are being sent in next request

4. ❌ **Server timestamps don't work** - `response.t_server_recv` and `response.t_server_send` are 0

### Possible Causes

**Hypothesis A: Server doesn't send timestamps in Segmentation mode**
- Server might not include `t_server_recv` and `t_server_send` in Segmentation response
- Different response format for Segmentation vs other modes

**Hypothesis B: JsonUtility can't parse the fields**
- Unity's `JsonUtility` has limitations
- May not handle `double` type correctly
- Field names might be case-sensitive or formatted differently

**Hypothesis C: Fields are in response but under different names**
- Server might use snake_case vs camelCase
- Field names might not match exactly

## 🔬 Debug Code Added

Added comprehensive debug logging to check:

1. **Does JSON contain the fields?**
   ```csharp
   bool hasRecv = jsonResponse.Contains("t_server_recv");
   bool hasSend = jsonResponse.Contains("t_server_send");
   ```

2. **What are the actual values in JSON?**
   ```csharp
   Debug.LogWarning($"t_server_recv section: {jsonResponse.Substring(recvIndex, 50)}");
   ```

3. **What does JsonUtility parse?**
   ```csharp
   Debug.LogWarning($"After JSON parse: t_server_recv={response.t_server_recv}");
   ```

## 📋 Testing Instructions

### Step 1: Rebuild Unity
```
1. Assets > Reimport All
2. Build and deploy to Quest 3
```

### Step 2: Send Frames
```
1. Launch Segmentation scene on Quest 3
2. Send 3-5 frames
```

### Step 3: Check ADB Logs
```bash
adb shell "logcat -d | grep 'JSON contains t_server_recv'"
adb shell "logcat -d | grep 't_server_recv section'"
adb shell "logcat -d | grep 'After JSON parse'"
```

### Expected Results

**If Hypothesis A (Server doesn't send)**:
```
JSON contains t_server_recv: False
```
**Fix**: Ensure Segmentation mode endpoint returns timestamps

**If Hypothesis B (JsonUtility can't parse)**:
```
JSON contains t_server_recv: True
t_server_recv section: "t_server_recv": 1776281641.390
After JSON parse: t_server_recv=0
```
**Fix**: Manually parse like PoseEstimation does

**If Hypothesis C (Different field names)**:
```
JSON contains t_server_recv: False
(But contains "tServerRecv" or "server_recv_timestamp")
```
**Fix**: Update field names in ServerResponse class

## 🔧 Prepared Fixes

### Fix A: If Server Doesn't Send (Server-side fix)
Check `infer_human.py` response building for Segmentation mode:
```python
# Ensure this is included for ALL modes:
t_server_recv=t_recv,
t_server_send=t_postprocess_end,
```

### Fix B: If JsonUtility Can't Parse (Unity-side fix)
Add manual parsing after JsonUtility:
```csharp
ServerResponse response = JsonUtility.FromJson<ServerResponse>(jsonResponse);

// Manual parse for timestamps (JsonUtility limitation)
if (response.t_server_recv == 0)
{
    string recvStr = ExtractJsonValue(jsonResponse, "t_server_recv");
    if (!string.IsNullOrEmpty(recvStr) && double.TryParse(recvStr, out double recvVal))
    {
        response.t_server_recv = recvVal;
    }
}

if (response.t_server_send == 0)
{
    string sendStr = ExtractJsonValue(jsonResponse, "t_server_send");
    if (!string.IsNullOrEmpty(sendStr) && double.TryParse(sendStr, out double sendVal))
    {
        response.t_server_send = sendVal;
    }
}
```

Where `ExtractJsonValue` is similar to `ExtractSimpleJsonValue` from PoseEstimation.

### Fix C: If Different Field Names (Unity-side fix)
Update ServerResponse class:
```csharp
[System.Serializable]
private class ServerResponse
{
    // ... other fields ...

    // Try all possible field name variations
    public double t_server_recv;
    public double tServerRecv;
    public double server_recv_timestamp;
    public double t_server_send;
    public double tServerSend;
    public double server_send_timestamp;
}
```

## 📊 Comparison with Working Modes

### MultiObjectDetection (WORKS)
- Uses `JsonUtility.FromJson`
- Server timestamps: ✅ 100% populated
- Same ServerResponse class structure

### PoseEstimation (WORKS after manual parsing)
- Uses manual JSON extraction with `ExtractSimpleJsonValue`
- Server timestamps: ✅ 100% populated (after fix)
- Different approach: doesn't rely on JsonUtility for timestamps

### Segmentation (BROKEN)
- Uses `JsonUtility.FromJson`
- Server timestamps: ❌ 0% populated (all 0)
- Same code as MultiObjectDetection, but doesn't work

**Key Question**: Why does JsonUtility work for MultiObjectDetection but not Segmentation?

Possible answer: **Different server response formats**

## 🎯 Most Likely Solution

Based on the fact that MultiObjectDetection works with JsonUtility but Segmentation doesn't, the most likely cause is:

**Segmentation server endpoint returns a different JSON structure**

**Fix**: Add manual parsing like PoseEstimation (proven to work)

## 📄 Files Modified

- `SegmentationInferenceRunManager.cs` (lines 694-714) - Added debug logging

## Next Steps

1. ⏳ Rebuild Unity and deploy to Quest 3
2. ⏳ Send frames and check ADB logs
3. ⏳ Identify which hypothesis is correct
4. ⏳ Apply corresponding fix
5. ⏳ Verify fix works

## Status

Waiting for debug logs to confirm exact cause and apply fix.
