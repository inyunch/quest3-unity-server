# Unity Performance Optimization Guide

**Last Updated**: 2026-04-18
**Target**: Quest 3 @ 72 Hz with Real-time AI Inference

---

## 📊 性能瓶頸分析

### Unity 端的時間消耗分布（典型 5 FPS 推理）

| 階段 | 時間 (ms) | 佔比 | 類型 | 可優化性 |
|------|----------|------|------|---------|
| **Texture Processing** | 5-15 | 15-30% | CPU/GPU | ⭐⭐⭐⭐⭐ 高 |
| **JPEG Encoding** | 10-30 | 30-40% | CPU | ⭐⭐⭐⭐ 中高 |
| **UDP Send** | 0.5-2 | 1-2% | Network | ⭐⭐ 低 |
| **HTTP Polling** | 0.1-1 | <1% | Network | ⭐ 很低 |
| **JSON Parsing** | 5-20 | 10-25% | CPU | ⭐⭐⭐ 中 |
| **Result Display** | 5-15 | 15-25% | CPU/GPU | ⭐⭐⭐⭐ 中高 |

### 關鍵瓶頸

1. **Texture.ReadPixels()** - 🔴 最大瓶頸 (5-10ms)
   - CPU 阻塞操作，強制 GPU → CPU 同步
   - 每幀都執行，無法避免

2. **Texture2D.EncodeToJPG()** - 🔴 次要瓶頸 (10-30ms)
   - CPU 密集型 JPEG 壓縮
   - 品質越高越慢（quality 80 vs 60）

3. **JsonUtility.FromJson()** - 🟡 中等影響 (5-20ms)
   - CPU JSON 解析
   - 取決於回應大小（Segmentation > Pose > Detection）

---

## 🚀 優化策略（按優先級）

---

## 1️⃣ 優化 Texture Processing (⭐⭐⭐⭐⭐ 最高優先級)

### 問題：ReadPixels 阻塞主線程

**當前代碼** (PoseInferenceRunManager.cs:479):
```csharp
// ❌ 阻塞主線程 5-10ms
tex2D.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
tex2D.Apply();
```

### 優化 A: 使用 AsyncGPUReadback（Unity 2023.1+）

**影響**: 減少 5-10ms 主線程阻塞 → **改善 50-100%**

```csharp
using Unity.Collections;
using UnityEngine.Rendering;

// 異步讀取 GPU 數據（不阻塞主線程）
private void CaptureTextureAsync(RenderTexture rt)
{
    AsyncGPUReadback.Request(rt, 0, TextureFormat.RGB24, OnTextureReadback);
}

private void OnTextureReadback(AsyncGPUReadbackRequest request)
{
    if (request.hasError)
    {
        Debug.LogError("[ASYNC READBACK] GPU readback error");
        return;
    }

    // 在另一個線程處理（不阻塞主線程）
    NativeArray<byte> data = request.GetData<byte>();

    // 創建 Texture2D（仍需要在主線程，但可以延遲）
    Texture2D tex = new Texture2D(request.width, request.height, TextureFormat.RGB24, false);
    tex.LoadRawTextureData(data);
    tex.Apply();

    // 繼續處理（JPEG 編碼等）
    ProcessTexture(tex);
}
```

**優點**:
- ✅ 不阻塞主線程
- ✅ GPU → CPU 傳輸在背景進行
- ✅ 可以提前觸發下一幀的渲染

**缺點**:
- ⚠️ 需要 Unity 2023.1+
- ⚠️ 增加 1-2 幀延遲（可接受）
- ⚠️ 代碼複雜度增加

---

### 優化 B: 減少紋理解析度（已實現，但可進一步優化）

**當前**: 使用 `downsampleFactor`

**進一步優化**:

```csharp
// 當前 downsample 設置
public int downsampleFactor = 2;  // 640x480 → 320x240

// 建議：根據 inference mode 動態調整
private int GetOptimalDownsampleFactor()
{
    switch (m_inferenceConfig.mode)
    {
        case InferenceMode.ObjectDetection:
            return 4;  // 640x480 → 160x120 (YOLO 對解析度要求低)

        case InferenceMode.PoseEstimation:
            return 2;  // 640x480 → 320x240 (Pose 需要中等解析度)

        case InferenceMode.Both:
            return 2;  // 平衡檢測和姿態

        default:
            return 2;
    }
}
```

**影響**:
```
640x480 (downsample=2) → 320x240:
  ReadPixels: 10ms → 2.5ms (-75%)
  EncodeToJPG: 25ms → 12ms (-52%)
  Upload: 50KB → 25KB (-50%)

640x480 (downsample=4) → 160x120:
  ReadPixels: 10ms → 0.6ms (-94%)
  EncodeToJPG: 25ms → 6ms (-76%)
  Upload: 50KB → 12KB (-76%)
```

---

### 優化 C: 使用 Compute Shader 進行下採樣

**當前**: 使用 `Graphics.Blit()` (GPU) + `ReadPixels()` (CPU)

**優化**: 使用 Compute Shader 直接在 GPU 上下採樣和格式轉換

```csharp
// ComputeShader: DownsampleRGB.compute
#pragma kernel Downsample

Texture2D<float4> Input;
RWTexture2D<float4> Output;
uint2 InputSize;
uint2 OutputSize;

[numthreads(8,8,1)]
void Downsample(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= OutputSize.x || id.y >= OutputSize.y)
        return;

    // 計算對應的輸入像素位置（2x2 block average）
    uint2 inputPos = id.xy * 2;

    float4 sum = Input[inputPos + uint2(0,0)] +
                 Input[inputPos + uint2(1,0)] +
                 Input[inputPos + uint2(0,1)] +
                 Input[inputPos + uint2(1,1)];

    Output[id.xy] = sum / 4.0;
}
```

**C# 端**:
```csharp
public ComputeShader downsampleShader;
private RenderTexture downsampledRT;

void DownsampleWithCompute(RenderTexture source, int factor)
{
    int width = source.width / factor;
    int height = source.height / factor;

    if (downsampledRT == null || downsampledRT.width != width)
    {
        downsampledRT = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32);
        downsampledRT.enableRandomWrite = true;
        downsampledRT.Create();
    }

    int kernel = downsampleShader.FindKernel("Downsample");
    downsampleShader.SetTexture(kernel, "Input", source);
    downsampleShader.SetTexture(kernel, "Output", downsampledRT);
    downsampleShader.SetInts("InputSize", source.width, source.height);
    downsampleShader.SetInts("OutputSize", width, height);

    downsampleShader.Dispatch(kernel, (width + 7) / 8, (height + 7) / 8, 1);
}
```

**影響**: Graphics.Blit (2-3ms) → Compute Shader (0.5-1ms)

---

## 2️⃣ 優化 JPEG 編碼 (⭐⭐⭐⭐ 高優先級)

### 優化 A: 降低 JPEG 品質

**當前**: `jpegQuality = 80` (預設)

**建議**:

```csharp
// 根據 network 條件動態調整
public int jpegQuality = 60;  // 從 80 降到 60

// 測試不同品質的影響
private void BenchmarkJpegQuality()
{
    int[] qualities = { 40, 50, 60, 70, 80, 90 };

    foreach (int quality in qualities)
    {
        float startTime = Time.realtimeSinceStartup;
        byte[] jpegData = texture.EncodeToJPG(quality);
        float encodeTime = (Time.realtimeSinceStartup - startTime) * 1000f;

        Debug.Log($"[JPEG BENCH] Quality={quality}: " +
                  $"time={encodeTime:F1}ms, size={jpegData.Length} bytes");
    }
}
```

**影響**:
```
Quality 80: 25-30ms, ~50KB
Quality 60: 15-20ms, ~25KB (-40% time, -50% size)
Quality 40: 10-12ms, ~15KB (-60% time, -70% size)
```

**視覺品質測試**:
- Quality 60: ✅ 大多數情況無明顯差異
- Quality 40: ⚠️ 可能影響檢測準確度（需測試）

---

### 優化 B: 使用多線程 JPEG 編碼（進階）

**問題**: `EncodeToJPG()` 阻塞主線程

**解決方案**: 使用 Unity Job System 或 C# Task

```csharp
using System.Threading.Tasks;

private async Task<byte[]> EncodeJpegAsync(Texture2D texture, int quality)
{
    // 獲取原始像素數據
    byte[] rawData = texture.GetRawTextureData();
    int width = texture.width;
    int height = texture.height;

    // 在背景線程編碼
    byte[] jpegData = await Task.Run(() =>
    {
        // 使用第三方 JPEG encoder (如 ImageSharp, TurboJPEG)
        return EncodeJpegNative(rawData, width, height, quality);
    });

    return jpegData;
}
```

**注意**: Unity 的 `EncodeToJPG()` 不是線程安全的，需要使用第三方庫。

---

### 優化 C: 預分配緩衝區

**當前**: 每次編碼都分配新 buffer

**優化**: 重複使用 buffer

```csharp
private byte[] m_jpegBuffer;
private const int MAX_JPEG_SIZE = 100 * 1024;  // 100KB

void Start()
{
    m_jpegBuffer = new byte[MAX_JPEG_SIZE];
}

byte[] EncodeJpegOptimized(Texture2D texture, int quality)
{
    // 使用預分配的 buffer（需要 native plugin）
    int actualSize = NativeJpegEncoder.EncodeToBuffer(
        texture.GetRawTextureData(),
        texture.width,
        texture.height,
        quality,
        m_jpegBuffer
    );

    // 返回實際大小的 slice
    byte[] result = new byte[actualSize];
    System.Array.Copy(m_jpegBuffer, 0, result, 0, actualSize);
    return result;
}
```

**影響**: 減少 GC 壓力，避免記憶體分配 spike

---

## 3️⃣ 優化 JSON 解析 (⭐⭐⭐ 中等優先級)

### 優化 A: 使用 JSON 串流解析

**當前**: `JsonUtility.FromJson()` 一次性解析整個 JSON

**問題**: Segmentation 的大 JSON (~400KB) 解析慢 (20-50ms)

**優化**: 使用增量解析器（如 Newtonsoft.Json）

```csharp
using Newtonsoft.Json;
using System.IO;

private ServerResponse ParseJsonIncremental(string jsonString)
{
    using (var stringReader = new StringReader(jsonString))
    using (var jsonReader = new JsonTextReader(stringReader))
    {
        var serializer = new JsonSerializer();
        return serializer.Deserialize<ServerResponse>(jsonReader);
    }
}
```

**影響**: 20ms → 12ms (-40%)

---

### 優化 B: 跳過不需要的欄位

**當前**: 解析整個回應，包括不需要的欄位

**優化**: 只解析需要的欄位

```csharp
// 使用部分解析（JsonUtility 不支援，需 Newtonsoft.Json）
[JsonObject(MemberSerialization.OptIn)]
public class ServerResponseOptimized
{
    [JsonProperty("detections")]
    public DetectionResultData detections;

    [JsonProperty("skeleton")]
    public SkeletonData skeleton;

    [JsonProperty("processing_time_ms")]
    public float processing_time_ms;

    // 不解析 segmentation_mask（大部分情況不需要）
    // public string segmentation_mask;  // 跳過
}
```

---

### 優化 C: 使用 Binary 格式（進階）

**最佳方案**: 完全避免 JSON，改用 MessagePack 或 Protobuf

**Server 端**:
```python
import msgpack

@app.post("/infer_human_binary")
async def infer_human_binary(image: UploadFile):
    # 處理推理
    result = run_inference(image)

    # 序列化為 MessagePack
    binary_data = msgpack.packb(result)
    return Response(content=binary_data, media_type="application/msgpack")
```

**Unity 端**:
```csharp
using MessagePack;

private ServerResponse ParseBinary(byte[] data)
{
    return MessagePackSerializer.Deserialize<ServerResponse>(data);
}
```

**影響**: JSON (20ms) → MessagePack (3-5ms) **(-75-85%)**

---

## 4️⃣ 優化結果顯示 (⭐⭐⭐⭐ 中高優先級)

### 優化 A: 對象池 (Object Pooling)

**問題**: 每幀 `Instantiate()` 新 GameObject (5-10ms)

**當前代碼** (假設):
```csharp
// ❌ 每幀創建新對象
foreach (var keypoint in skeleton.keypoints)
{
    GameObject sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
    sphere.transform.position = keypoint.position;
}
```

**優化**: 使用對象池重複使用

```csharp
// 對象池管理器
public class SpherePool
{
    private List<GameObject> pool = new List<GameObject>();
    private GameObject prefab;
    private Transform parent;

    public SpherePool(GameObject prefab, Transform parent, int initialSize)
    {
        this.prefab = prefab;
        this.parent = parent;

        for (int i = 0; i < initialSize; i++)
        {
            CreateNewSphere();
        }
    }

    private GameObject CreateNewSphere()
    {
        GameObject sphere = GameObject.Instantiate(prefab, parent);
        sphere.SetActive(false);
        pool.Add(sphere);
        return sphere;
    }

    public GameObject Get()
    {
        foreach (var sphere in pool)
        {
            if (!sphere.activeInHierarchy)
            {
                sphere.SetActive(true);
                return sphere;
            }
        }

        // Pool 不夠，創建新的
        return CreateNewSphere();
    }

    public void ReturnAll()
    {
        foreach (var sphere in pool)
        {
            sphere.SetActive(false);
        }
    }
}

// 使用對象池
private SpherePool m_spherePool;

void Start()
{
    GameObject spherePrefab = GameObject.CreatePrimitive(PrimitiveType.Sphere);
    m_spherePool = new SpherePool(spherePrefab, transform, 50);
}

void DisplayKeypoints(List<Keypoint> keypoints)
{
    m_spherePool.ReturnAll();

    foreach (var kp in keypoints)
    {
        GameObject sphere = m_spherePool.Get();
        sphere.transform.position = kp.position;
    }
}
```

**影響**: Instantiate (10ms) → Reuse (0.5ms) **(-95%)**

---

### 優化 B: 使用 GPU Instancing 批次渲染

**問題**: 每個 keypoint 一個 GameObject = 高 draw call

**優化**: 使用 `Graphics.DrawMeshInstanced()`

```csharp
using UnityEngine;

public class KeypointRenderer
{
    private Mesh sphereMesh;
    private Material material;
    private Matrix4x4[] matrices;

    void Start()
    {
        // 創建球體 mesh
        sphereMesh = CreateSphereMesh();

        // 使用支援 instancing 的 material
        material = new Material(Shader.Find("Standard"));
        material.enableInstancing = true;

        matrices = new Matrix4x4[17];  // COCO 17 keypoints
    }

    void DisplayKeypoints(List<Keypoint> keypoints)
    {
        for (int i = 0; i < keypoints.Count; i++)
        {
            matrices[i] = Matrix4x4.TRS(
                keypoints[i].position,
                Quaternion.identity,
                Vector3.one * 0.05f  // sphere scale
            );
        }

        // 一次 draw call 渲染所有 keypoints
        Graphics.DrawMeshInstanced(sphereMesh, 0, material, matrices, keypoints.Count);
    }
}
```

**影響**: 17 draw calls → 1 draw call **(-94%)**

---

### 優化 C: 使用 Sprite/UI Image 而非 3D GameObject

**對於 2D overlay**:

```csharp
using UnityEngine.UI;

public class KeypointOverlayUI
{
    private List<Image> keypointImages = new List<Image>();
    private Canvas canvas;

    void DisplayKeypointsAs2D(List<Keypoint> keypoints)
    {
        // 確保有足夠的 UI images
        while (keypointImages.Count < keypoints.Count)
        {
            Image img = new GameObject("Keypoint").AddComponent<Image>();
            img.transform.SetParent(canvas.transform, false);
            img.sprite = Resources.Load<Sprite>("circle");
            keypointImages.Add(img);
        }

        // 更新位置（2D screen space）
        for (int i = 0; i < keypoints.Count; i++)
        {
            keypointImages[i].rectTransform.anchoredPosition =
                WorldToCanvasPosition(keypoints[i].position);
            keypointImages[i].gameObject.SetActive(true);
        }

        // 隱藏多餘的
        for (int i = keypoints.Count; i < keypointImages.Count; i++)
        {
            keypointImages[i].gameObject.SetActive(false);
        }
    }
}
```

**影響**: 3D rendering → 2D UI (更快，更省資源)

---

## 5️⃣ 系統層級優化 (⭐⭐⭐ 中等優先級)

### 優化 A: 減少 GC (Garbage Collection) 壓力

**問題**: 頻繁的記憶體分配導致 GC spike

**優化檢查清單**:

```csharp
// ✅ 重複使用 buffer
private byte[] m_reusableBuffer = new byte[1024 * 1024];  // 1MB

// ✅ 預分配 List
private List<Keypoint> m_keypointsCache = new List<Keypoint>(17);

// ✅ 使用 StringBuilder 而非字串連接
private System.Text.StringBuilder m_logBuilder = new System.Text.StringBuilder(256);

// ❌ 避免每幀分配
void Update()
{
    // ❌ 錯誤：每幀創建新 List
    List<Keypoint> keypoints = new List<Keypoint>();

    // ✅ 正確：重複使用
    m_keypointsCache.Clear();
    // 填充數據...
}
```

---

### 優化 B: 降低日誌頻率

**問題**: 過多的 `Debug.Log()` 影響性能

**當前**: 每幀記錄大量日誌

**優化**: 只在需要時記錄

```csharp
// 條件日誌
#if UNITY_EDITOR || DEVELOPMENT_BUILD
    private bool enableVerboseLogging = false;
#else
    private bool enableVerboseLogging = false;  // Release 關閉
#endif

void LogVerbose(string message)
{
    if (enableVerboseLogging)
    {
        Debug.Log(message);
    }
}

// 或使用採樣日誌
private int logFrameCounter = 0;
void LogSampled(string message, int interval = 60)
{
    if (logFrameCounter % interval == 0)
    {
        Debug.Log(message);
    }
    logFrameCounter++;
}
```

**影響**: 減少 5-10ms (在高日誌情況下)

---

### 優化 C: 使用 Burst Compiler (Unity Jobs)

**對於密集計算** (如座標轉換):

```csharp
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;

[BurstCompile]
struct TransformKeypointsJob : IJobParallelFor
{
    [ReadOnly] public NativeArray<float2> normalizedKeypoints;
    [WriteOnly] public NativeArray<float3> worldKeypoints;

    public float2 imageSize;
    public float4x4 cameraToWorld;

    public void Execute(int index)
    {
        // 歸一化座標 → 像素座標 → 世界座標
        float2 pixel = normalizedKeypoints[index] * imageSize;
        float3 world = math.mul(cameraToWorld, new float4(pixel.x, pixel.y, 0, 1)).xyz;
        worldKeypoints[index] = world;
    }
}

void TransformKeypointsFast(List<Keypoint> keypoints)
{
    NativeArray<float2> input = new NativeArray<float2>(keypoints.Count, Allocator.TempJob);
    NativeArray<float3> output = new NativeArray<float3>(keypoints.Count, Allocator.TempJob);

    // 填充 input
    for (int i = 0; i < keypoints.Count; i++)
    {
        input[i] = new float2(keypoints[i].x, keypoints[i].y);
    }

    // 執行 job
    var job = new TransformKeypointsJob
    {
        normalizedKeypoints = input,
        worldKeypoints = output,
        imageSize = new float2(1280, 720),
        cameraToWorld = Camera.main.cameraToWorldMatrix
    };

    JobHandle handle = job.Schedule(keypoints.Count, 64);
    handle.Complete();

    // 讀取結果
    for (int i = 0; i < keypoints.Count; i++)
    {
        keypoints[i].worldPosition = output[i];
    }

    input.Dispose();
    output.Dispose();
}
```

**影響**: CPU 計算加速 2-10x

---

## 📊 優化效果估算

### 保守估算（實現所有優化）

| 階段 | 當前 (ms) | 優化後 (ms) | 改善 |
|------|----------|-----------|------|
| Texture Processing | 10 | 3 | -70% |
| JPEG Encoding | 25 | 12 | -52% |
| UDP Send | 1 | 1 | 0% |
| HTTP Polling | 0.5 | 0.5 | 0% |
| JSON Parsing | 15 | 5 | -67% |
| Result Display | 10 | 2 | -80% |
| **Total Unity Time** | **61.5** | **23.5** | **-62%** |

### 激進估算（使用所有高級技術）

| 階段 | 當前 (ms) | 優化後 (ms) | 改善 |
|------|----------|-----------|------|
| Texture Processing | 10 | 1 | -90% (AsyncGPUReadback) |
| JPEG Encoding | 25 | 5 | -80% (多線程 + 低品質) |
| UDP Send | 1 | 1 | 0% |
| HTTP Polling | 0.5 | 0.5 | 0% |
| JSON Parsing | 15 | 2 | -87% (MessagePack) |
| Result Display | 10 | 0.5 | -95% (GPU Instancing) |
| **Total Unity Time** | **61.5** | **10** | **-84%** |

---

## 🎯 推薦實施順序

### Phase 1: Quick Wins（1-2 小時）

1. ✅ 降低 JPEG 品質 (80 → 60)
2. ✅ 增加 downsample factor (2 → 3 或 4)
3. ✅ 實現對象池
4. ✅ 減少日誌頻率

**預期改善**: 20-30%

---

### Phase 2: 中等改進（1-2 天）

1. ✅ 使用 AsyncGPUReadback
2. ✅ 優化 JSON 解析（Newtonsoft.Json）
3. ✅ 實現 GPU Instancing
4. ✅ 減少 GC 壓力

**預期改善**: 40-50%

---

### Phase 3: 高級優化（1-2 週）

1. ✅ 多線程 JPEG 編碼
2. ✅ MessagePack / Protobuf
3. ✅ Compute Shader 下採樣
4. ✅ Burst Compiler

**預期改善**: 60-70%

---

## 🔍 性能測試工具

### Unity Profiler

```csharp
using UnityEngine.Profiling;

void CaptureAndEncode()
{
    Profiler.BeginSample("Texture.ReadPixels");
    tex2D.ReadPixels(rect, 0, 0);
    tex2D.Apply();
    Profiler.EndSample();

    Profiler.BeginSample("Texture.EncodeToJPG");
    byte[] jpegData = tex2D.EncodeToJPG(quality);
    Profiler.EndSample();
}
```

**查看**: Window → Analysis → Profiler

---

### 自訂計時器

```csharp
using System.Diagnostics;

public class PerformanceTimer
{
    private Stopwatch stopwatch = new Stopwatch();
    private Dictionary<string, List<long>> timings = new Dictionary<string, List<long>>();

    public void Start(string label)
    {
        stopwatch.Restart();
    }

    public void Stop(string label)
    {
        stopwatch.Stop();
        if (!timings.ContainsKey(label))
        {
            timings[label] = new List<long>();
        }
        timings[label].Add(stopwatch.ElapsedMilliseconds);
    }

    public void PrintStats()
    {
        foreach (var kvp in timings)
        {
            long avg = (long)kvp.Value.Average();
            long min = kvp.Value.Min();
            long max = kvp.Value.Max();
            UnityEngine.Debug.Log($"[PERF] {kvp.Key}: avg={avg}ms, min={min}ms, max={max}ms");
        }
    }
}
```

---

## 📌 總結

### 最大瓶頸

1. **Texture.ReadPixels()** - 5-10ms (無法完全避免)
2. **Texture2D.EncodeToJPG()** - 10-30ms (可優化到 5-10ms)
3. **JsonUtility.FromJson()** - 5-20ms (可優化到 2-5ms)

### 最有效的優化

1. ⭐⭐⭐⭐⭐ **AsyncGPUReadback** - 減少 5-10ms 主線程阻塞
2. ⭐⭐⭐⭐⭐ **降低 JPEG 品質** - 減少 10-15ms 編碼時間
3. ⭐⭐⭐⭐ **增加 Downsample** - 減少 5-10ms 處理時間
4. ⭐⭐⭐⭐ **對象池** - 減少 5-10ms 實例化時間

### 實際可達成的目標

**當前**: Unity 端處理時間 ~60ms
**優化後**: Unity 端處理時間 ~20-30ms
**改善**: 50-70%

這將使整體 E2E 延遲從 ~500ms 降至 ~350-400ms，推理 FPS 從 2-3 提升到 3-5。

---

**Last Updated**: 2026-04-18
**Version**: 1.0
