# 為什麼 Unity 無法真正測量下載時間？

## TL;DR

**Unity 的 `UnityWebRequest` API 沒有提供「下載開始」的時間戳或事件**，所以我們無法區分：
- 服務器處理完成的時間
- 數據開始下載的時間
- 下載完成的時間

---

## Unity UnityWebRequest 的時間點限制

### 當前可以測量的時間點：

```csharp
// ✅ 1. 請求開始
float startTime = Time.realtimeSinceStartup;
UnityWebRequestAsyncOperation asyncOp = request.SendWebRequest();

// ✅ 2. 上傳完成（uploadProgress = 1.0）
while (!asyncOp.isDone && request.uploadProgress < 1.0f)
    yield return null;
float uploadDoneTime = Time.realtimeSinceStartup;

// ❌ 3. 下載開始 ← Unity 不提供這個時間點！
// ❌ 4. 第一個字節到達 (TTFB) ← Unity 不提供！

// ✅ 5. 整個請求完成（isDone = true）
yield return asyncOp;
float requestDoneTime = Time.realtimeSinceStartup;
```

---

## 問題詳解

### 從代碼看時間線（PoseInferenceRunManager.cs:341-442）

```
時間軸                     我們能測量的          Unity 提供的 API
═══════════════════════════════════════════════════════════════════
0ms   [開始]              ✅ uploadStartTime    request.SendWebRequest()
      ├─ 編碼 JPEG
      ├─ 建立連接
      └─ 發送 135KB

15ms  [上傳完成]          ✅ uploadDoneTime     uploadProgress = 1.0
      │                                         asyncOp.isDone = false
      │
      │   ❌ Unity 無法測量這段時間的細節！
      │   ├─ 網絡延遲 (RTT)
      │   ├─ Server 收到請求
      │   ├─ Server AI 推理 (30ms)
      │   ├─ Server 構建 JSON
      │   ├─ Server 發送響應
      │   └─ 數據在網絡上傳輸
      │
85ms  [響應完成]          ✅ parseStartTime     asyncOp.isDone = true
                                                downloadProgress = 1.0
```

**問題**：從 `uploadDoneTime` (15ms) 到 `parseStartTime` (85ms) 這 70ms 中：
- ❌ 不知道服務器何時開始處理
- ❌ 不知道服務器何時完成處理
- ❌ 不知道數據何時開始下載
- ❌ 不知道下載實際花了多少時間

---

## Unity API 的限制

### UnityWebRequest 只提供這些屬性：

| 屬性/方法 | 用途 | 限制 |
|----------|------|------|
| `SendWebRequest()` | 開始請求 | ✅ 可以記錄開始時間 |
| `uploadProgress` | 上傳進度 (0-1) | ✅ 可以檢測上傳完成 |
| `downloadProgress` | 下載進度 (0-1) | ⚠️ 無法知道何時開始 |
| `isDone` | 整個請求是否完成 | ✅ 可以記錄完成時間 |
| `downloadedBytes` | 已下載字節數 | ⚠️ 只有最終值 |

### Unity 缺少的 API：

```csharp
// ❌ 這些 Unity 都沒有提供！
public float downloadStartTime;          // 下載開始時間
public float timeToFirstByte;            // 服務器響應第一個字節的時間
public float downloadDuration;           // 純下載時間
public event OnDownloadStart();          // 下載開始事件
public event OnFirstByteReceived();      // 第一個字節到達事件
```

---

## 為什麼其他平台可以測量？

### 瀏覽器 (JavaScript fetch/XMLHttpRequest)

```javascript
const start = performance.now();
const response = await fetch(url);

// ✅ 可以獲取詳細時間
const timing = performance.getEntriesByType('resource')[0];
console.log({
  dns: timing.domainLookupEnd - timing.domainLookupStart,
  tcp: timing.connectEnd - timing.connectStart,
  request: timing.responseStart - timing.requestStart,  // ✅ TTFB
  download: timing.responseEnd - timing.responseStart,  // ✅ 下載時間
});
```

### 原生 C# (HttpClient)

```csharp
var stopwatch = Stopwatch.StartNew();
using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersReceived);
var ttfb = stopwatch.Elapsed;  // ✅ Time To First Byte

var downloadStart = Stopwatch.StartNew();
var content = await response.Content.ReadAsByteArrayAsync();
var downloadTime = downloadStart.Elapsed;  // ✅ 下載時間
```

---

## 當前的解決方案（不完美）

### 方案 1：用服務器時間戳推算（當前使用）

```csharp
// Server 返回時間戳
response.t_server_recv;   // Server 收到請求的時間
response.t_server_send;   // Server 發送響應的時間

// Unity 推算
downloadMs = e2eMs - uploadMs - serverProcMs - parseMs;
```

**問題**：
- 包含了網絡 RTT
- 包含了 Unity 內部處理
- 不是真正的下載時間

### 方案 2：使用 downloadProgress 輪詢（不精確）

```csharp
// 檢測 downloadProgress 從 0 變為 > 0
float downloadStartTime = 0f;
while (!asyncOp.isDone)
{
    if (request.downloadProgress > 0 && downloadStartTime == 0)
    {
        downloadStartTime = Time.realtimeSinceStartup;
    }
    yield return null;
}
```

**問題**：
- `downloadProgress` 更新頻率低（每幀一次）
- 精度只有 16-33ms (60/30 FPS)
- 小文件可能一次就下載完，無法測量

### 方案 3：使用原生插件（複雜）

```csharp
// 使用 C# HttpClient 或原生 Android/iOS API
// 需要寫原生插件，複雜且不跨平台
```

---

## 結論

### 為什麼 Unity 無法測量下載時間？

1. **API 設計限制**：UnityWebRequest 是高層抽象，隱藏了底層細節
2. **跨平台考慮**：不同平台的網絡 API 差異大，Unity 選擇最小公共集
3. **異步模型**：協程模型無法精確捕獲事件時間點
4. **性能開銷**：提供詳細時間戳會增加每幀開銷

### 當前最佳實踐

**接受這個限制，並正確理解數據含義**：

| 指標 | 實際含義 |
|------|---------|
| `upload_ms` | ✅ 真實上傳時間 |
| `server_proc_ms` | ✅ 真實服務器處理時間 |
| `download_ms` | ⚠️ 剩餘時間（網絡+下載+處理） |
| `parse_ms` | ✅ 真實 JSON 解析時間 |

**優化建議**：
1. 關注 `e2e_ms` 而非單獨的 `download_ms`
2. 通過對比不同 payload 大小來推斷網絡速度
3. 優化服務器響應時間和大小
4. 使用 profiler 分析 Unity 內部開銷

---

## 未來可能的改進

### Unity 可能的 API 增強：

```csharp
// 希望 Unity 未來能提供：
public class UnityWebRequest
{
    public float timeToFirstByte { get; }      // TTFB
    public float downloadStartTime { get; }    // 下載開始時間
    public float downloadDuration { get; }     // 純下載時間

    public event Action OnDownloadStart;       // 下載開始事件
    public event Action<long> OnBytesReceived; // 接收字節事件
}
```

但目前（Unity 2022.3+）這些都不存在。

---

## 參考資料

- [Unity Documentation: UnityWebRequest](https://docs.unity3d.com/ScriptReference/Networking.UnityWebRequest.html)
- [W3C Resource Timing API](https://www.w3.org/TR/resource-timing-2/)
- [HTTP Archive: Network Timing](https://developer.mozilla.org/en-US/docs/Web/API/Performance_API/Resource_timing)
