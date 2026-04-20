# Excel Logging Implementation - Complete

**Date**: 2026-04-16
**Status**: ✅ Ready for Testing

---

## Summary

Excel logging 已完全重構，遵循與 rendering 相同的原則：

1. ✅ **不發明新流程** - 使用既有 `Dictionary<int, FrameTrace>` 架構
2. ✅ **一個 frame 一筆記錄** - 整個 lifecycle 只更新同一筆
3. ✅ **實驗結束時匯出** - 不在 polling 時寫檔
4. ✅ **Unity 端負責** - Server 完全不涉及 Excel logging

---

## What Was Fixed

### ❌ Removed (Wrong Architecture)

**Server Side**:
- Removed `_log_to_excel()` method from UDP worker (179 lines)
- Removed `await self._log_to_excel(...)` call
- Server no longer writes to `debug/logs/inference_log_*.xlsx`

**Unity Side**:
- Removed `GetPreviousFrameTelemetryJson()` method
- Removed telemetry JSON embedding in UDP packets
- Simplified `SendFrameUDP()` - no more telemetry parameter

### ✅ Added (Correct Architecture)

**Unity Side**:
- `ExportFrameTracesToCSV()` in both scenes (MultiObjectDetection, PoseEstimation)
- `OnDestroy()` triggers export when app closes
- CSV files saved to Quest persistent data path
- Only exports final states (Displayed, Dropped, Failed)

---

## Files Modified

### Server (vision_server)

| File | Lines Changed | Description |
|------|---------------|-------------|
| `app/workers/udp_inference_worker.py` | -179 | Removed Excel logging |

### Unity (PassthroughCameraApiSamples)

| File | Lines Changed | Description |
|------|---------------|-------------|
| `MultiObjectDetection/.../SentisInferenceRunManager.cs` | +96, -34 | Added CSV export, removed telemetry JSON |
| `PoseEstimation/Scripts/PoseInferenceRunManager.cs` | +93 | Added CSV export |
| `Shared/Scripts/UDPTransport.cs` | 0 | No changes needed (already supports null telemetry) |

---

## How to Test

### Step 1: Start Server

```bash
cd C:\Repo\Github\vision_server
python -m uvicorn app.main:app --host 0.0.0.0 --port 8001 --workers 1
```

### Step 2: Run Unity on Quest 3

1. Build and deploy to Quest 3
2. Run MultiObjectDetection scene
3. Let experiment run for ~30 seconds
4. **Close the app** (triggers OnDestroy → CSV export)

### Step 3: Check Unity Logs

```bash
adb logcat -s Unity | findstr "EXCEL EXPORT"
```

**Expected output**:
```
[EXCEL EXPORT] Exported 287 frame traces to: /sdcard/Android/data/com.meta.passthroughcamerasamples/files/InferenceLog_abc123_2026-04-16_15-30-45.csv
[EXCEL EXPORT] Pull via: adb pull /sdcard/Android/data/com.meta.passthroughcamerasamples/files/InferenceLog_abc123_2026-04-16_15-30-45.csv
```

### Step 4: Pull CSV File

```bash
adb pull /sdcard/Android/data/com.meta.passthroughcamerasamples/files/InferenceLog_abc123_2026-04-16_15-30-45.csv .
```

### Step 5: Validate CSV

Open in Excel and verify:

- ✅ Header: 24 columns
- ✅ No duplicate frame_ids
- ✅ Only final states (Displayed, Dropped, Failed)
- ✅ XOR validation: Displayed rows have display_ts > 0 AND drop_ts == 0
- ✅ XOR validation: Dropped rows have drop_ts > 0 AND display_ts == 0

---

## CSV Format

```csv
scene,session_id,frame_id,
unity_send_ts,unity_receive_ts,unity_display_ts,unity_drop_ts,
server_receive_ts,server_process_start_ts,server_send_ts,
e2e_ms,server_proc_ms,upload_ms,download_ms,parse_ms,
final_state,drop_reason,error_reason,freeze_frames,
upload_bytes_uncompressed,upload_bytes_compressed,
download_bytes_uncompressed,download_bytes_compressed,
detection_count,avg_confidence
```

---

## Differences from Old Architecture

| Aspect | Old (WRONG) | New (CORRECT) |
|--------|-------------|---------------|
| **Responsibility** | Server writes Excel | Unity writes CSV |
| **Timing** | Every inference | Session end only |
| **Data Flow** | Unity → UDP (telemetry JSON) → Server → Excel | Unity → FrameTrace Dictionary → CSV |
| **Location** | Server PC (`debug/logs/*.xlsx`) | Quest 3 (`/sdcard/Android/data/{package}/files/*.csv`) |
| **UDP Overhead** | +200-500 bytes telemetry JSON | 0 bytes (no telemetry) |
| **Architecture** | Violates separation of concerns | Clean: Unity owns frame lifecycle |

---

## Next Steps

1. **Test on Quest 3** (see above)
2. **Verify CSV data quality** (no duplicates, correct states)
3. **(Optional) Add manual export button** (future improvement)
4. **(Optional) Add auto-upload to server** (future improvement)

---

## Documentation

- **Architecture Details**: [EXCEL_LOGGING_ARCHITECTURE.md](./EXCEL_LOGGING_ARCHITECTURE.md)
- **UDP Transport**: [Documentation/UDP_TRANSPORT_SETUP_GUIDE.md](./Documentation/UDP_TRANSPORT_SETUP_GUIDE.md)
- **Frame Lifecycle**: [Assets/PassthroughCameraApiSamples/Shared/Scripts/FrameTrace.cs](./Assets/PassthroughCameraApiSamples/Shared/Scripts/FrameTrace.cs)

---

**Implementation Complete!** ✅

Ready for testing. If CSV export works correctly, this closes the Excel logging implementation.
