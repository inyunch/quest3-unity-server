# 並行處理架構提案

## 當前問題

### 串行架構的限制

**當前行為**：
```
Unity: Send Frame 1 → Wait → Receive → Send Frame 2 → Wait → Receive
Server: Process Frame 1 → Idle → Process Frame 2 → Idle
```

**問題**：
1. ❌ Unity 必須等待 Server 回應才能發下一幀
2. ❌ Server 處理完後閒置，浪費算力
3. ❌ 如果 Server 慢（> 200ms），Unity 會 freeze
4. ❌ 無法利用 Server 的處理能力（可能可以同時處理 2-3 幀）

---

## 新架構：並行處理

### 核心概念

**Unity 端**：
- ✅ **固定 FPS 發送**（例如 5 FPS = 每 200ms 發一次）
- ✅ **不等待回應**（Fire-and-forget）
- ✅ **並行發送**多個 frames
- ✅ **只顯示最新處理完的 frame**

**Server 端**：
- ✅ **並行處理**多個 frames（使用 worker pool 或 async queue）
- ✅ **可能亂序完成**（Frame 3 可能比 Frame 2 先完成）
- ✅ **每個 frame 獨立處理**

**顯示邏輯**：
- ✅ **永遠顯示最新的已完成 frame**
- ✅ **Drop 舊的未顯示 frame**（如果有更新的已完成）
- ✅ **Freeze = 沒有新 frame 可顯示時的等待時間**

---

## 重新定義：Drop Frame vs Freeze Frame

### 在並行架構下的新定義

#### 1. **Dropped Frame**（丟棄幀）

**定義**：已經處理完但**沒有被顯示**的 frame。

**發生情境**：

**情境 A：被更新的 frame 取代**
```
Unity Timeline:
Frame 1 (0ms)    → Send to Server
Frame 2 (200ms)  → Send to Server
Frame 3 (400ms)  → Send to Server

Server Processing (亂序完成):
Frame 1: 0ms   → 250ms (完成) → 放入結果 queue
Frame 2: 200ms → 500ms (完成) → 放入結果 queue  ← 晚於 Frame 3
Frame 3: 400ms → 480ms (完成) → 放入結果 queue

Unity Display:
Frame 1 (250ms): 顯示 ✅
Frame 2 (500ms): Frame 3 已經存在 → DROP Frame 2 ❌
Frame 3 (480ms): 顯示 ✅ (在 Frame 2 之前完成)
```

**原因**：Frame 3 比 Frame 2 先完成，所以 Frame 2 被 drop。

---

**情境 B：積壓過多**
```
Unity 發送：Frame 1, 2, 3, 4, 5 (每 200ms)
Server 處理：每個花 800ms

完成順序：
Frame 1: 800ms 完成
Frame 2: 1000ms 完成
Frame 3: 1200ms 完成
Frame 4: 1400ms 完成
Frame 5: 1600ms 完成

Unity 顯示策略（只顯示最新）：
- 800ms:  顯示 Frame 1 ✅
- 1000ms: DROP Frame 2 ❌（因為 Frame 3 即將完成，或者已經完成但還沒檢查）
- 1200ms: 顯示 Frame 3 ✅
- 1400ms: DROP Frame 4 ❌
- 1600ms: 顯示 Frame 5 ✅

結果：Drop 了 Frame 2 和 Frame 4
```

**計算**：
```csharp
int m_droppedFrames = 0;  // 累計被 drop 的 frame 數

void OnFrameReceived(int frameId, Result result)
{
    // 檢查是否有更新的 frame 已經在 queue 中
    if (HasNewerFrameInQueue(frameId))
    {
        m_droppedFrames++;
        Debug.Log($"[DROP] Frame {frameId} dropped (newer frame available)");
        return;  // 不顯示
    }

    // 顯示這一幀
    DisplayFrame(frameId, result);
}
```

---

#### 2. **Freeze Frame**（凍結幀）

**定義**：Unity 在兩次**顯示更新**之間的等待時間（以 Unity frames 計數）。

**發生情境**：

**情境 A：等待第一個 frame 完成**
```
Unity Timeline (60 FPS):
Frame 1  (0ms)   → Send → Wait for result
Frame 2  (16ms)  → No update (waiting)  ← FREEZE
Frame 3  (33ms)  → No update (waiting)  ← FREEZE
...
Frame 15 (250ms) → Frame 1 result arrives → Display ✅

Freeze count = 14 frames (Frame 2-15)
```

**情境 B：所有發出的 frames 都還在處理中**
```
Unity sends (5 FPS):
Frame 1 (0ms)    → Processing
Frame 2 (200ms)  → Processing
Frame 3 (400ms)  → Processing

Unity display updates (60 FPS):
0ms:   Display nothing (no results yet)
16ms:  FREEZE (no new results)
33ms:  FREEZE (no new results)
...
250ms: Frame 1 arrives → Display ✅
260ms: FREEZE (Frame 2 and 3 still processing)
276ms: FREEZE
...
480ms: Frame 3 arrives → Display ✅ (Frame 2 still not done)
496ms: FREEZE
...
500ms: Frame 2 arrives → DROP (Frame 3 already displayed)

Total freeze frames = (250/16) + (230/16) = 15 + 14 = 29 frames
```

**計算**：
```csharp
private int m_lastDisplayedFrame = -1;
private float m_lastDisplayTime = 0f;
private int m_frozenFrames = 0;

void Update()  // 60 FPS
{
    // 檢查是否有新的結果可以顯示
    if (TryGetLatestCompletedFrame(out int frameId, out Result result))
    {
        // 有新結果，顯示
        DisplayFrame(frameId, result);
        m_lastDisplayedFrame = frameId;
        m_lastDisplayTime = Time.time;
    }
    else
    {
        // 沒有新結果，freeze
        m_frozenFrames++;
    }
}
```

**Freeze Duration**：
```csharp
// 計算實際凍結時間（毫秒）
float freezeDurationMs = m_frozenFrames * (1000f / 60f);  // 60 FPS
// 例如：30 frozen frames = 30 * 16.67ms = 500ms
```

---

### 新定義總結

| 指標 | 舊定義（串行） | 新定義（並行） |
|------|---------------|---------------|
| **Dropped Frame** | Unity 主動跳過（距離太近） | 已處理但未顯示（被更新的取代） |
| **Freeze Frame** | 上一幀還在處理中（被動等待） | 沒有新結果可顯示（Unity frame 計數） |
| **計數單位** | Inference attempts | Display updates (60 FPS) |
| **觸發原因** | `timeSinceLastInference < targetInterval` | `!HasNewCompletedFrame()` |
| **影響** | 降低 inference FPS | 顯示卡頓（重複顯示舊畫面） |

---

## 實現提案

### Unity 端架構

#### 1. 並行發送 Coroutine

```csharp
public class PoseInferenceRunManager : MonoBehaviour
{
    // === 配置 ===
    [SerializeField] private InferenceConfig m_inferenceConfig;

    // === 並行請求管理 ===
    private Dictionary<int, UnityWebRequest> m_pendingRequests = new();  // frameId -> request
    private ConcurrentQueue<FrameResult> m_completedFrames = new();      // 已完成的結果
    private int m_nextFrameId = 0;

    // === 顯示狀態 ===
    private int m_lastDisplayedFrameId = -1;
    private float m_lastDisplayTime = 0f;

    // === 統計 ===
    private int m_totalFramesSent = 0;
    private int m_totalFramesReceived = 0;
    private int m_totalFramesDisplayed = 0;
    private int m_droppedFrames = 0;   // 收到但未顯示
    private int m_frozenFrames = 0;    // Unity frames without new display


    void Start()
    {
        StartCoroutine(SendFramesPeriodically());
        StartCoroutine(ProcessCompletedFrames());
    }


    // === 發送邏輯：固定 FPS 發送，不等待回應 ===
    IEnumerator SendFramesPeriodically()
    {
        float targetInterval = m_inferenceConfig.GetInferenceInterval();  // 例如 0.2s = 5 FPS

        while (true)
        {
            yield return new WaitForSeconds(targetInterval);

            // 獲取當前相機圖像
            Texture2D tex2D = GetCameraTexture();
            if (tex2D == null)
                continue;

            // 分配 Frame ID
            int frameId = m_nextFrameId++;
            m_totalFramesSent++;

            // 發送（不等待回應）
            StartCoroutine(SendFrameAsync(frameId, tex2D));

            Debug.Log($"[SEND] Frame {frameId} sent (pending: {m_pendingRequests.Count})");
        }
    }


    // === 並行發送單個 frame ===
    IEnumerator SendFrameAsync(int frameId, Texture2D tex2D)
    {
        // 準備數據
        byte[] jpegBytes = tex2D.EncodeToJPG(m_inferenceConfig.jpegQuality);

        WWWForm form = new WWWForm();
        form.AddBinaryData("image", jpegBytes, "frame.jpg", "image/jpeg");
        form.AddField("frame_id", frameId.ToString());

        string url = m_inferenceConfig.BuildUrl();
        UnityWebRequest request = UnityWebRequest.Post(url, form);

        // 記錄發送時間
        float sendTime = Time.realtimeSinceStartup;

        // 設置 headers
        request.SetRequestHeader("X-Frame-ID", frameId.ToString());
        request.SetRequestHeader("X-Send-Time", sendTime.ToString("F6"));

        // 添加到 pending（追蹤中）
        m_pendingRequests[frameId] = request;

        // 發送（非阻塞）
        yield return request.SendWebRequest();

        // 完成
        m_pendingRequests.Remove(frameId);

        if (request.result == UnityWebRequest.Result.Success)
        {
            // 解析結果
            float receiveTime = Time.realtimeSinceStartup;
            float latency = (receiveTime - sendTime) * 1000f;

            string json = request.downloadHandler.text;
            // TODO: Parse JSON to Result object

            FrameResult result = new FrameResult
            {
                frameId = frameId,
                receiveTime = receiveTime,
                latency = latency,
                data = json  // or parsed object
            };

            // 放入完成 queue
            m_completedFrames.Enqueue(result);
            m_totalFramesReceived++;

            Debug.Log($"[RECV] Frame {frameId} completed (latency: {latency:F1}ms, queue: {m_completedFrames.Count})");
        }
        else
        {
            Debug.LogError($"[RECV] Frame {frameId} failed: {request.error}");
        }
    }


    // === 處理完成的 frames：只顯示最新的 ===
    IEnumerator ProcessCompletedFrames()
    {
        while (true)
        {
            yield return null;  // 每個 Unity frame 檢查一次（60 FPS）

            // 檢查是否有完成的 frame
            if (m_completedFrames.TryPeek(out FrameResult latest))
            {
                // 找到最新的 frame ID（可能有多個完成，取最新的）
                int newestFrameId = latest.frameId;
                FrameResult newestResult = latest;

                List<FrameResult> toRemove = new();

                // 遍歷所有已完成的，找最新的
                while (m_completedFrames.TryDequeue(out FrameResult result))
                {
                    if (result.frameId > newestFrameId)
                    {
                        // 發現更新的，drop 之前的
                        if (newestResult.frameId > m_lastDisplayedFrameId)
                        {
                            m_droppedFrames++;
                            Debug.Log($"[DROP] Frame {newestResult.frameId} dropped (newer {result.frameId} available)");
                        }

                        newestFrameId = result.frameId;
                        newestResult = result;
                    }
                    else
                    {
                        // 這個比已有的舊，drop
                        if (result.frameId > m_lastDisplayedFrameId)
                        {
                            m_droppedFrames++;
                            Debug.Log($"[DROP] Frame {result.frameId} dropped (newer {newestFrameId} available)");
                        }
                    }
                }

                // 顯示最新的（如果比上次顯示的新）
                if (newestFrameId > m_lastDisplayedFrameId)
                {
                    DisplayFrame(newestResult);
                    m_lastDisplayedFrameId = newestFrameId;
                    m_lastDisplayTime = Time.realtimeSinceStartup;
                    m_totalFramesDisplayed++;

                    Debug.Log($"[DISPLAY] Frame {newestFrameId} displayed (dropped: {m_droppedFrames}, frozen: {m_frozenFrames})");
                }
            }
            else
            {
                // 沒有新 frame 可顯示，freeze
                m_frozenFrames++;
            }
        }
    }


    void DisplayFrame(FrameResult result)
    {
        // TODO: 顯示骨架、bbox 等
        // Update UI, draw gizmos, etc.
    }


    // === 數據結構 ===
    struct FrameResult
    {
        public int frameId;
        public float receiveTime;
        public float latency;
        public string data;  // JSON or parsed object
    }
}
```

---

#### 2. Freeze Frame 計算

```csharp
void Update()
{
    // 計算 freeze duration
    if (m_lastDisplayTime > 0)
    {
        float timeSinceLastDisplay = Time.realtimeSinceStartup - m_lastDisplayTime;

        // 如果超過 2 個 Unity frames 沒更新，視為 freeze
        if (timeSinceLastDisplay > (2f / 60f))  // > 33ms
        {
            m_frozenFrames++;
        }
    }
}
```

---

### Server 端架構

#### 1. 多 Worker 配置

**文件**：`app/main.py`

**選項 A：Uvicorn 多 worker**
```python
if __name__ == "__main__":
    import uvicorn
    uvicorn.run(
        "app.main:app",
        host="0.0.0.0",
        port=8001,
        workers=4,  # 4 個並行 worker processes
        reload=False  # Production mode
    )
```

**選項 B：Gunicorn + Uvicorn Workers**
```bash
gunicorn app.main:app \
  --workers 4 \
  --worker-class uvicorn.workers.UvicornWorker \
  --bind 0.0.0.0:8001 \
  --timeout 120
```

---

#### 2. Async Task Queue（推薦）

**使用 Celery + Redis**

**安裝**：
```bash
pip install celery redis
```

**文件**：`app/celery_worker.py`（新增）

```python
"""
Celery worker for async inference processing
"""
from celery import Celery
import time
from PIL import Image
import io
import base64

# Celery app
celery_app = Celery(
    'vision_server',
    broker='redis://localhost:6379/0',
    backend='redis://localhost:6379/0'
)

celery_app.conf.update(
    task_serializer='json',
    accept_content=['json'],
    result_serializer='json',
    timezone='UTC',
    enable_utc=True,
    task_track_started=True,
    task_time_limit=30,  # 30 seconds max
    worker_prefetch_multiplier=1,  # Process one at a time per worker
)


@celery_app.task(bind=True)
def process_frame_async(self, frame_id: int, image_base64: str, mode: str):
    """
    Process a single frame asynchronously

    Args:
        frame_id: Frame ID from Unity
        image_base64: Base64-encoded JPEG image
        mode: Inference mode (pose, detection, etc.)

    Returns:
        Dict with inference results
    """
    from app.inference_yolo import run_all_models_with_yolo

    start_time = time.time()

    # Decode image
    image_bytes = base64.b64decode(image_base64)
    pil_image = Image.open(io.BytesIO(image_bytes)).convert("RGB")

    # Run inference
    results = run_all_models_with_yolo(
        pil_image,
        conf_threshold=0.5,
        label_filter=["person"],
        person_min_conf=0.3
    )

    processing_time = (time.time() - start_time) * 1000

    return {
        "frame_id": frame_id,
        "processing_time_ms": processing_time,
        "results": results,
        "worker_id": self.request.id
    }
```

**文件**：`app/routes/infer_human.py`（修改）

```python
from app.celery_worker import process_frame_async
from celery.result import AsyncResult
import base64

@router.post("/infer_human")
async def infer_human(
    request: Request,
    image: UploadFile = File(...),
):
    """
    Submit inference task to Celery queue (non-blocking)
    """
    # Read image
    contents = await image.read()

    # Get frame ID from header
    frame_id = int(request.headers.get("X-Frame-ID", "0"))
    mode = request.query_params.get("mode", "pose")

    # Encode image to base64 for Celery
    image_base64 = base64.b64encode(contents).decode('utf-8')

    # Submit to Celery (non-blocking)
    task = process_frame_async.delay(frame_id, image_base64, mode)

    return {
        "frame_id": frame_id,
        "task_id": task.id,
        "status": "submitted"
    }


@router.get("/result/{task_id}")
async def get_result(task_id: str):
    """
    Poll for task result
    """
    task_result = AsyncResult(task_id)

    if task_result.ready():
        return {
            "status": "completed",
            "result": task_result.result
        }
    else:
        return {
            "status": "processing",
            "state": task_result.state
        }
```

**啟動**：
```bash
# Terminal 1: Start Redis
redis-server

# Terminal 2: Start Celery workers (4 workers)
celery -A app.celery_worker worker --loglevel=info --concurrency=4

# Terminal 3: Start FastAPI
uvicorn app.main:app --host 0.0.0.0 --port 8001
```

---

#### 3. 選項 C：AsyncIO + ThreadPoolExecutor（簡單版）

**文件**：`app/routes/infer_human.py`

```python
from concurrent.futures import ThreadPoolExecutor
import asyncio

# Global thread pool (4 workers)
executor = ThreadPoolExecutor(max_workers=4)

@router.post("/infer_human")
async def infer_human(
    request: Request,
    image: UploadFile = File(...),
):
    """
    Process inference in background thread pool
    """
    contents = await image.read()
    frame_id = int(request.headers.get("X-Frame-ID", "0"))

    # Decode image
    pil_image = Image.open(io.BytesIO(contents)).convert("RGB")

    # Run inference in thread pool (non-blocking)
    loop = asyncio.get_event_loop()
    results = await loop.run_in_executor(
        executor,
        run_all_models_with_yolo,
        pil_image
    )

    # 注意：這仍然是等待結果返回才回應
    # 要真正非阻塞，需要改成兩階段（submit + poll）

    return {
        "frame_id": frame_id,
        "results": results
    }
```

---

## 完整流程圖（並行架構）

### 正常情況（Server 能並行處理）

```
Unity Side (60 FPS, Send at 5 FPS):
Frame 1  (0ms)    → Send Frame 1 (don't wait) ───┐
Frame 13 (200ms)  → Send Frame 2 (don't wait) ───┼─┐
Frame 25 (400ms)  → Send Frame 3 (don't wait) ───┼─┼─┐
Frame 37 (600ms)  → Send Frame 4 (don't wait) ───┼─┼─┼─┐
                                                  │ │ │ │
Server Side (4 workers, parallel):                │ │ │ │
Worker 1: Frame 1 (0ms → 250ms)    ←─────────────┘ │ │ │
Worker 2: Frame 2 (200ms → 450ms)  ←───────────────┘ │ │
Worker 3: Frame 3 (400ms → 480ms)  ←─────────────────┘ │  ← 最快！
Worker 4: Frame 4 (600ms → 850ms)  ←───────────────────┘
                                    │         │    │     │
Unity Display (checks every frame): │         │    │     │
250ms: Frame 1 completed → Display ─┘         │    │     │
450ms: Frame 2 completed, but Frame 3 done ───┼────┘     │
       → DROP Frame 2 ❌                       │          │
480ms: Frame 3 completed → Display ────────────┘          │
850ms: Frame 4 completed → Display ───────────────────────┘

Result:
- Sent: 4 frames
- Received: 4 frames
- Displayed: 3 frames (1, 3, 4)
- Dropped: 1 frame (2)
- Frozen: ~15 Unity frames (250ms / 16ms)
```

---

### 異常情況（Server 太慢，積壓）

```
Unity Send (5 FPS):
0ms:   Frame 1 sent
200ms: Frame 2 sent
400ms: Frame 3 sent
600ms: Frame 4 sent
800ms: Frame 5 sent

Server Process (每個 800ms，4 workers):
Worker 1: Frame 1 (0ms → 800ms)
Worker 2: Frame 2 (200ms → 1000ms)
Worker 3: Frame 3 (400ms → 1200ms)
Worker 4: Frame 4 (600ms → 1400ms)
[積壓] Frame 5 等待 worker... (800ms → 1600ms)

Unity Display:
800ms:  Frame 1 arrives → Display ✅
1000ms: Frame 2 arrives, check if newer...
        Frame 3, 4, 5 all still processing → Display Frame 2 ✅
1200ms: Frame 3 arrives → Display ✅
1400ms: Frame 4 arrives → Display ✅
1600ms: Frame 5 arrives → Display ✅

Freeze Analysis:
- 0ms → 800ms: 等待 Frame 1 (800ms = 48 Unity frames)
- 800ms → 1000ms: 等待 Frame 2 (200ms = 12 Unity frames)
- 1000ms → 1200ms: 等待 Frame 3 (200ms = 12 Unity frames)
- 1200ms → 1400ms: 等待 Frame 4 (200ms = 12 Unity frames)
- 1400ms → 1600ms: 等待 Frame 5 (200ms = 12 Unity frames)

Total Freeze: 96 Unity frames = 1600ms
```

---

## Excel 記錄更新

### 新增欄位

| 欄位 | 類型 | 說明 |
|------|------|------|
| `frame_id` | Integer | Frame ID（可能不連續） |
| `send_time` | Float | Unity 發送時間戳記 |
| `receive_time` | Float | Unity 接收時間戳記 |
| `display_time` | Float | Unity 顯示時間戳記（可能晚於 receive） |
| `was_dropped` | Boolean | 這一幀是否被 drop（True/False） |
| `drop_reason` | String | Drop 原因（"newer_available", "outdated", etc.） |
| `pending_count` | Integer | 這一幀發送時，有多少 frames 還在 pending |
| `queue_size` | Integer | 這一幀完成時，complete queue 有多少 frames |
| `display_delay_ms` | Float | 從 receive 到 display 的延遲 |

---

## 優缺點分析

### 並行架構優點

1. ✅ **吞吐量提升**：Server 可以同時處理多個 frames
2. ✅ **降低延遲感知**：即使單個 frame 慢，用戶看到的是平均延遲
3. ✅ **容錯性**：某個 frame 失敗不影響其他
4. ✅ **充分利用資源**：GPU/CPU 不會閒置
5. ✅ **更流暢**：總是顯示最新結果

### 並行架構挑戰

1. ⚠️ **複雜度**：需要管理並行請求、queue、亂序處理
2. ⚠️ **記憶體佔用**：同時處理多個 frames 需要更多 RAM/VRAM
3. ⚠️ **Drop 率可能高**：如果 Server 慢，很多 frames 會被 drop
4. ⚠️ **調試困難**：亂序處理難以追蹤
5. ⚠️ **網路頻寬**：同時發送多個 frames 可能塞爆網路

---

## 實施步驟

### Phase 1: Unity 端並行發送（不等待回應）

**修改文件**：
- `PoseInferenceRunManager.cs`
- `SentisInferenceRunManager.cs`
- `SegmentationInferenceRunManager.cs`

**改動**：
1. 移除 `m_inferenceInProgress` lock
2. 添加 `Dictionary<int, UnityWebRequest>` 追蹤 pending requests
3. 實現 `SendFrameAsync()` 不等待回應
4. 實現 `ProcessCompletedFrames()` 只顯示最新

---

### Phase 2: Server 端多 worker

**選項 A：簡單版（Uvicorn workers）**
```python
uvicorn.run("app.main:app", workers=4)
```

**選項 B：完整版（Celery + Redis）**
- 安裝 Redis
- 實現 `celery_worker.py`
- 改成兩階段 API（submit + poll）

---

### Phase 3: 新的 Drop/Freeze 定義

**Unity 端**：
- `m_droppedFrames` = 已接收但未顯示的 frames
- `m_frozenFrames` = Unity frames without new display

**Server 端**：
- 不再需要計算 drop/freeze（這是 Unity 顯示邏輯）

---

### Phase 4: Excel 記錄更新

**添加新欄位**：
- `was_dropped`, `drop_reason`, `display_delay_ms`

**記錄時機**：
- 每個 frame **接收時**記錄一次（不管是否顯示）
- `was_dropped` 標記是否實際顯示

---

## 下一步

1. **確認架構選擇**：
   - 簡單版（ThreadPool）還是完整版（Celery）？
   - 要幾個 workers？（建議 2-4）

2. **Unity 端原型**：
   - 先實現並行發送
   - 測試是否正常接收

3. **Server 端原型**：
   - 先用 `workers=2` 測試
   - 確認並行處理正常

4. **測試場景**：
   - Server 快（< 200ms）→ 應該沒有 drop
   - Server 慢（> 200ms）→ 應該有 drop，但顯示流暢

---

## 相關文檔

- `SERVER_PROCESSING_ARCHITECTURE.md` - 當前串行架構
- `DROP_VS_FREEZE_DETAILED.md` - 舊的 drop/freeze 定義
