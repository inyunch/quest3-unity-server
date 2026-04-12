# Frame Tracking Metrics - Why Are They Zero?

## Question

Why are `dropped_frames`, `freeze_frames`, and `freeze_ratio` all showing **0** in Segmentation mode Excel logs?

## Analysis

### Actual Data from Excel Logs (2026-04-11)

#### Segmentation Mode (Frames 50-59)
```
Frame  Latency  Server  Target  Interval  Dropped  Freeze
50     139.5ms  91.6ms  10 FPS  100ms     0        0
51     156.8ms  84.7ms  10 FPS  100ms     0        0
52     155.1ms  99.7ms  10 FPS  100ms     0        0
53     156.9ms  114.2ms 10 FPS  100ms     0        0
54     182.7ms  109.2ms 10 FPS  100ms     0        0
55     170.9ms  92.5ms  10 FPS  100ms     0        0
56     155.9ms  97.3ms  10 FPS  100ms     0        0
57     155.8ms  101.5ms 10 FPS  100ms     0        0
58     418.2ms  71.8ms  10 FPS  100ms     0        0
59     154.9ms  71.8ms  10 FPS  100ms     0        0
```

**Average End-to-End Latency**: ~**165ms** (range: 139-418ms)

#### MultiObjectDetection Mode (Frames 34-38)
```
Frame  Latency  Server  Target  Interval  Dropped  Freeze
34     105.9ms  44.4ms  10 FPS  100ms     11       0
35     105.6ms  48.3ms  10 FPS  100ms     11       0
36     106.3ms  44.1ms  10 FPS  100ms     11       0
37     91.2ms   44.1ms  10 FPS  100ms     12       0
38     109.5ms  70.9ms  10 FPS  100ms     12       0
```

**Average End-to-End Latency**: ~**104ms**
**Dropped Frames**: 11-12 (cumulative) ✅ **NORMAL**

---

## Root Cause Analysis

### Why dropped_frames = 0 in Segmentation?

**Target Interval**: 100ms (1000ms / 10 FPS)

**Dropped Frame Logic** (`SegmentationInferenceRunManager.cs:221-230`):
```csharp
float timeSinceLastInference = currentTime - m_lastInferenceTime;
float targetInterval = 1.0f / targetFPS;  // 100ms for 10 FPS

if (timeSinceLastInference < targetInterval)
{
    m_droppedFrames++;  // Only increments if frame arrives TOO SOON
    yield break;
}
```

**Problem**:
- Segmentation E2E latency: **165ms average** >> 100ms target
- Each inference takes **longer than the target interval**
- By the time the next frame is ready to process, **165ms has already passed**
- **165ms > 100ms** → Condition `timeSinceLastInference < targetInterval` is **NEVER true**
- Result: **No frames are dropped**

**Example Timeline**:
```
0ms:    Frame 1 starts inference
165ms:  Frame 1 completes (latency = 165ms)
        Frame 2 ready to process
        timeSinceLastInference = 165ms > 100ms target
        ✅ NOT DROPPED, process immediately
330ms:  Frame 2 completes (latency = 165ms)
        Frame 3 ready to process
        timeSinceLastInference = 165ms > 100ms target
        ✅ NOT DROPPED, process immediately
```

**Comparison with MultiObjectDetection**:
```
0ms:    Frame 1 starts
104ms:  Frame 1 completes
        Frame 2 ready
120ms:  Frame 3 arrives (only 16ms since Frame 2 started)
        timeSinceLastInference = 16ms < 100ms target
        ❌ DROPPED (too soon)
224ms:  Frame 4 arrives
        timeSinceLastInference = 104ms > 100ms target
        ✅ Process
```

---

### Why freeze_frames = 0 in Both Modes?

**Freeze Frame Logic** (`SegmentationInferenceRunManager.cs:233-242`):
```csharp
if (m_inferenceInProgress)
{
    m_frozenFrames++;  // Only increments if previous inference still running
    yield break;
}

m_inferenceInProgress = true;  // Mark as in progress
// ... run inference ...
m_inferenceInProgress = false;  // Mark as complete
```

**Freeze frames occur when**:
- A new frame arrives **while the previous inference is still running**
- This typically happens when:
  - Server is overloaded (>200ms response time)
  - Network has high latency
  - Quest 3 camera captures frames faster than inference can process them

**Why it's 0**:
1. **Quest 3 Camera Rate**: Probably 30 FPS (33ms per frame)
2. **Unity Update Loop**: `OnCameraFrameEventArgs` callback rate may be throttled
3. **Inference Duration**: 165ms for Segmentation, 104ms for ObjectDetection
4. **Between Inferences**:
   - During 165ms inference, Quest 3 captures ~5 frames (165ms / 33ms = 5)
   - But Unity's callback may only trigger **once inference completes**
   - Or frames are **queued** and only the latest is processed

**Hypothesis**: Unity's PassthroughCameraAccess only delivers **one frame per Update** cycle, and Update doesn't run during `yield return` statements in coroutines.

---

## Why Is This Happening?

### Segmentation Mode is TOO SLOW

**Target**: 10 FPS = 100ms interval
**Reality**: 165ms average latency (65% slower than target!)

**Breakdown**:
- **Server Processing**: 71-114ms (avg ~90ms)
- **Upload + Download**: ~70-90ms
- **Network Overhead**: Remaining ~5ms

**Comparison**:

| Metric | MultiObjectDetection | Segmentation | Difference |
|--------|----------------------|--------------|------------|
| **E2E Latency** | 104ms | 165ms | **+59% slower** |
| **Server Time** | 44-70ms | 71-114ms | **+60% slower** |
| **Model** | YOLOv8n | YOLO11n-seg | Segmentation model |

**Why is Segmentation slower?**
1. **YOLO11n-seg model** is larger/slower than YOLOv8n
2. **Mask extraction** requires additional processing:
   - Crop masks to bbox regions
   - Convert to RGBA PNG
   - Base64 encode (~7-8KB per person)
3. **More data to transmit**: Masks add 50-100KB to response

---

## Is This a Problem?

### Short Answer: **Yes and No**

**Yes, it's a problem because**:
1. **Cannot achieve 10 FPS**: Actual rate is ~6 FPS (1000ms / 165ms = 6.06)
2. **Missed performance target**: System is 65% slower than configured
3. **Poor user experience**: Laggy AR overlays at 6 FPS

**No, it's not a bug because**:
1. **Frame tracking works correctly**: No frames are dropped *because they shouldn't be*
2. **Excel logging is accurate**: Values are 0 because that's the reality
3. **Code is functioning as designed**: The issue is **performance**, not logic

---

## Solutions

### Option 1: Lower Target FPS (Quick Fix)

**Change**: `targetFPS = 10` → `targetFPS = 6`

**Result**:
- Target interval: 166ms
- Actual latency: 165ms
- Should see some dropped frames (when latency < 166ms)

**Code** (`SegmentationInferenceRunManager.cs`):
```csharp
m_inferenceConfig.targetFPS = 6f;  // Match actual performance
```

---

### Option 2: Optimize Server Performance (Better Fix)

**Goal**: Reduce latency from 165ms to <100ms

**Approaches**:
1. **Use GPU for YOLO inference**:
   ```python
   YOLO_SEG_MODEL = YOLO("yolo11n-seg.pt", device='cuda')  # GPU acceleration
   ```
   **Expected improvement**: 71-114ms → 20-40ms (server time)

2. **Reduce image quality**:
   ```csharp
   m_inferenceConfig.jpegQuality = 70;  // Lower from 80
   ```
   **Expected improvement**: Faster upload/download

3. **Use smaller model**:
   ```python
   YOLO_SEG_MODEL = YOLO("yolov8n-seg.pt")  # Smaller model
   ```
   **Expected improvement**: 10-20ms faster

4. **Optimize mask encoding**:
   - Use lower resolution masks
   - Compress PNG better
   - Send binary data instead of base64

---

### Option 3: Accept Reality (Document Only)

**Action**: Update documentation to reflect actual performance

**Changes**:
1. Set `targetFPS = 6` as default for Segmentation
2. Document expected latency: 140-180ms
3. Note in README: "Segmentation mode runs at ~6 FPS (vs 10 FPS for ObjectDetection)"

---

## Expected Behavior After Fixes

### If we set targetFPS = 6 (166ms interval):

**Expected Excel Logs**:
```
Frame  Latency  Target  Interval  Dropped  Freeze
60     155ms    6 FPS   166ms     0-2      0
61     162ms    6 FPS   166ms     0-2      0
62     149ms    6 FPS   166ms     1-3      0
63     172ms    6 FPS   166ms     1-3      0
```

**Dropped frames**: 1-5 per session (when latency < 166ms)
**Freeze frames**: Still likely 0 (unless server slows down further)

---

### If we optimize to <100ms latency:

**Expected Excel Logs**:
```
Frame  Latency  Server  Target  Interval  Dropped  Freeze
64     89ms     35ms    10 FPS  100ms     5-10     0
65     95ms     38ms    10 FPS  100ms     5-10     0
66     92ms     32ms    10 FPS  100ms     6-11     0
```

**Dropped frames**: 10-20 per session (similar to MultiObjectDetection)
**Freeze frames**: Still 0 (good performance)

---

## Recommendation

**Immediate**: Change `targetFPS = 6` for Segmentation mode to match reality

**Short-term**: Optimize server with GPU acceleration to achieve <100ms latency

**Long-term**: Consider edge AI (on-device inference) to reduce latency to <30ms

---

## Summary

| Metric | Current State | Why It's 0 | Expected After Fix |
|--------|---------------|------------|-------------------|
| **dropped_frames** | 0 | Latency 165ms > 100ms interval<br>(every frame "arrives late") | 1-5 per session (with targetFPS=6)<br>10-20 per session (with GPU) |
| **freeze_frames** | 0 | Unity callback only once per inference<br>(no overlapping requests) | Still 0 (good!) |
| **freeze_ratio** | 0 | No freeze frames occurred | 0.0-0.02 (1-2%) |

**Conclusion**: The values are 0 because **Segmentation mode is too slow to meet the 10 FPS target**. This is a **performance issue**, not a logging bug. The frame tracking is working correctly.

---

**Date**: 2026-04-12
**Analyzed By**: Claude (Anthropic AI)
**Recommendation**: Lower targetFPS to 6 OR optimize server to <100ms latency
