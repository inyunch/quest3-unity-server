# Excel Logging Fix Proposal

**Date**: 2026-04-16
**Status**: Design Proposal (Not Yet Implemented)
**Priority**: MEDIUM (Data quality issue, but not blocking core functionality)

---

## Current Problems

### Problem 1: Duplicate Frame IDs in InferenceLog

**Observed**: Excel sheet shows same frame_id multiple times with different states.

**Root Cause**: Current N+1 delayed telemetry sends frame state via HTTP headers with NEXT frame request, but server writes to Excel immediately on receive, before final state is determined.

**Example**:
```
Row  frame_id  final_state  display_ts    drop_ts      Issue
1    85        Pending      NULL          NULL         Written during polling (404)
2    85        Completed    123456        NULL         Written when 200 OK received
3    85        Displayed    123456        123457       Written again after display
```

### Problem 2: Both display_ts AND drop_ts Set

**Observed**: Some rows have both `unity_display_ts` and `unity_drop_ts` values.

**Root Cause**: Frame state changes (Pending → Completed → Displayed/Dropped) but Excel row is updated multiple times instead of written once at final state.

**Expected XOR Constraint**:
- Displayed frames: `display_ts != NULL AND drop_ts == NULL`
- Dropped frames: `drop_ts != NULL AND display_ts == NULL`

### Problem 3: 404 Polling Creates Noise

**Observed**: Hundreds of 404 HTTP responses logged as events.

**Root Cause**: HTTP polling loop polls `/response/{session}/{frame}` every 100ms until ready. Each 404 is logged as an event, but these are NOT errors.

**Semantics**:
- `404 Not Found` = "Result not ready yet, keep polling" (NORMAL)
- `200 OK` = "Result ready" (SUCCESS)
- `500/502` = "Server error" (ERROR)

---

## Root Cause Analysis

### Current Architecture: N+1 Delayed Telemetry

```
Frame N:
  Unity → Send frame N → Server receives
  Unity ← 404 (polling)
  Unity ← 200 OK (result ready)
  Unity → Process response, MarkCompleted()

Frame N+1:
  Unity → Send frame N+1 with N's telemetry in HTTP headers
  Server → Writes frame N's data to Excel
          (But frame N might not be in final state yet!)
```

**Problem**: Server writes to Excel when it receives headers, but Unity hasn't determined final state yet (Displayed vs Dropped).

### Why This Happens

1. Unity sends frame N+1
2. Server receives N+1 request, extracts N's telemetry from headers
3. **Server immediately writes N's data to Excel** ❌
4. Unity later decides to display or drop frame N
5. Unity sends frame N+2 with updated N's telemetry
6. **Server writes N's data AGAIN** ❌ → Duplicate row!

---

## Proposed Solutions

### Option A: Client-Side Excel Logging (Recommended)

**Approach**: Unity writes to Excel locally, sends completed data to server only for archival.

**Architecture**:
```csharp
// Unity side - write to Excel when final state determined
void TryDisplayNewestFrame()
{
    FrameTrace newest = GetNewestCompleted();

    // Mark older frames as Dropped
    foreach (var older in olderFrames)
    {
        older.MarkDropped(timestamp, "superseded_by_newer");

        // ✅ Write to Excel NOW (final state determined)
        if (ShouldExportToExcel(older))
        {
            ExportToExcelLocal(older);  // Local write
        }
    }

    // Display newest
    DisplayFrame(newest);
    newest.MarkDisplayed(timestamp);

    // ✅ Write to Excel NOW (final state determined)
    if (ShouldExportToExcel(newest))
    {
        ExportToExcelLocal(newest);  // Local write
    }
}

bool ShouldExportToExcel(FrameTrace trace)
{
    // Rule 1: Only export final states
    if (trace.state != FrameState.Displayed &&
        trace.state != FrameState.Dropped &&
        trace.state != FrameState.Failed)
    {
        return false;
    }

    // Rule 2: Validate XOR constraint
    bool hasDisplayTs = trace.unity_display_ts.HasValue;
    bool hasDropTs = trace.unity_drop_ts.HasValue;

    if (trace.state == FrameState.Displayed)
    {
        if (!hasDisplayTs || hasDropTs)
        {
            Debug.LogError($"[EXCEL] ✗ Frame {trace.frame_id} state=Displayed but display_ts={hasDisplayTs}, drop_ts={hasDropTs}!");
            return false;
        }
    }

    if (trace.state == FrameState.Dropped)
    {
        if (!hasDropTs || hasDisplayTs)
        {
            Debug.LogError($"[EXCEL] ✗ Frame {trace.frame_id} state=Dropped but display_ts={hasDisplayTs}, drop_ts={hasDropTs}!");
            return false;
        }
    }

    return true;
}

void ExportToExcelLocal(FrameTrace trace)
{
    // Check for duplicate frame_id
    if (m_exportedFrameIds.Contains(trace.frame_id))
    {
        Debug.LogError($"[EXCEL] ✗ Frame {trace.frame_id} already exported! Skipping to prevent duplicate.");
        return;
    }

    // Write to local Excel file (using EPPlus or similar library)
    var row = new Dictionary<string, object>
    {
        ["frame_id"] = trace.frame_id,
        ["session_id"] = trace.session_id,
        ["unity_send_ts"] = trace.unity_send_ts,
        ["server_receive_ts"] = trace.server_receive_ts,
        ["server_process_start_ts"] = trace.server_process_start_ts,
        ["unity_response_receive_ts"] = trace.unity_receive_ts,
        ["unity_display_ts"] = trace.unity_display_ts ?? 0,
        ["unity_drop_ts"] = trace.unity_drop_ts ?? 0,
        ["final_state"] = trace.state.ToString(),
        ["drop_reason"] = trace.drop_reason ?? "",
        ["error_reason"] = trace.error_reason ?? "",
        ["freeze_frames"] = trace.freeze_frames
    };

    m_excelLogger.AppendRow(row);
    m_exportedFrameIds.Add(trace.frame_id);

    Debug.Log($"[EXCEL] ✓ Frame {trace.frame_id} exported (state={trace.state})");
}
```

**Pros**:
- ✅ Unity has complete frame lifecycle information
- ✅ Can enforce validation rules before writing
- ✅ No duplicate rows (check before write)
- ✅ XOR constraint guaranteed

**Cons**:
- ❌ Requires Excel library on Quest (EPPlus, ClosedXML)
- ❌ Need to handle file storage on Quest device
- ❌ More complex Unity code

---

### Option B: Server-Side Validation (Alternative)

**Approach**: Server receives delayed telemetry but validates before writing to Excel.

**Implementation**:
```python
# Server side - validate before Excel write
class InferenceLogger:
    def __init__(self):
        self.exported_frames = set()  # Track frame_ids already written

    def log_frame(self, frame_data: dict):
        frame_id = frame_data["frame_id"]

        # Rule 1: No duplicates
        if frame_id in self.exported_frames:
            logger.warning(f"[EXCEL] Frame {frame_id} already exported, skipping")
            return

        # Rule 2: Only final states
        final_state = frame_data["final_state"]
        if final_state not in ["Displayed", "Dropped", "Failed"]:
            logger.debug(f"[EXCEL] Frame {frame_id} state={final_state} not final, skipping")
            return

        # Rule 3: XOR validation
        display_ts = frame_data.get("unity_display_ts")
        drop_ts = frame_data.get("unity_drop_ts")

        if final_state == "Displayed":
            if not display_ts or drop_ts:
                logger.error(f"[EXCEL] Frame {frame_id} state=Displayed but display_ts={display_ts}, drop_ts={drop_ts}")
                return

        if final_state == "Dropped":
            if not drop_ts or display_ts:
                logger.error(f"[EXCEL] Frame {frame_id} state=Dropped but display_ts={display_ts}, drop_ts={drop_ts}")
                return

        # ✅ Validation passed, write to Excel
        self.excel_writer.append_row(frame_data)
        self.exported_frames.add(frame_id)
        logger.info(f"[EXCEL] ✓ Frame {frame_id} exported (state={final_state})")
```

**Pros**:
- ✅ No Unity code changes needed
- ✅ Centralized validation logic
- ✅ Works with existing N+1 delayed telemetry

**Cons**:
- ❌ Server still receives multiple updates for same frame
- ❌ Relies on Unity sending correct final state in headers
- ❌ Doesn't fix root cause (just filters duplicates)

---

## Recommended Approach

**Use Option B (Server-Side Validation)** for quick fix:

1. Add frame_id tracking set in server
2. Add validation before Excel write
3. Skip duplicates and non-final states
4. Log warnings for validation failures

**Future improvement (Option A)**: Move Excel logging to Unity for better control.

---

## Testing Validation

After implementing the fix, validate with these checks:

```sql
-- Check 1: No duplicate frame_ids
SELECT frame_id, COUNT(*) as count
FROM InferenceLog
GROUP BY frame_id
HAVING COUNT(*) > 1;
-- Expected: 0 rows

-- Check 2: All final states are valid
SELECT DISTINCT final_state FROM InferenceLog;
-- Expected: Only "Displayed", "Dropped", "Failed"

-- Check 3: XOR constraint (Displayed)
SELECT frame_id, unity_display_ts, unity_drop_ts
FROM InferenceLog
WHERE final_state = 'Displayed'
  AND (unity_display_ts IS NULL OR unity_drop_ts IS NOT NULL);
-- Expected: 0 rows

-- Check 4: XOR constraint (Dropped)
SELECT frame_id, unity_display_ts, unity_drop_ts
FROM InferenceLog
WHERE final_state = 'Dropped'
  AND (unity_drop_ts IS NULL OR unity_display_ts IS NOT NULL);
-- Expected: 0 rows
```

---

## Implementation Priority

**Current Status**: Design proposal only

**Priority**: MEDIUM
- Does not block core functionality (inference still works)
- Affects data quality for analysis
- Can be fixed incrementally

**Recommended Timeline**:
1. **Phase 1** (Quick fix): Server-side validation (1-2 hours)
2. **Phase 2** (Full fix): Client-side Excel logging (4-6 hours)

---

**Next Steps**:
1. Decide which approach to implement (A or B)
2. Test with server-side validation first
3. Consider Unity-side Excel export for future improvement
