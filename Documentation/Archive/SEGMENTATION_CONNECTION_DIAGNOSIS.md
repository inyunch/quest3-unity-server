# Segmentation 連接診斷報告

**日期**: 2026-04-17
**問題**: Segmentation 場景「伺服器完全沒反應」

---

## 問題診斷結果

### ✅ Server 端：正常運作

**Server 檢查結果**:
```bash
$ curl http://localhost:8001/
{
    "status": "ok",
    "service": "Human Inference API",
    "endpoints": [
        "/infer_human",
        "/segmentation",  ← ✅ Segmentation endpoint 已註冊
        "/infer_roi_depth"
    ]
}
```

**Server 日誌**:
```
[SEGMENTATION] Using device: cpu (PID: 57564)
[SEGMENTATION] Loading YOLO segmentation model...
[SEGMENTATION] YOLO11n-seg model loaded successfully on cpu
```

✅ Server 正常啟動
✅ `/segmentation` endpoint 已註冊
✅ YOLO11n-seg 模型已載入

---

### ❌ Unity 端：UDP Transport 未啟用

**程式碼檢查** (`SegmentationInferenceRunManager.cs`):

```csharp
[Header("Server Inference (NEW)")]
[SerializeField] private bool m_useServerInference = true;   // ✅ 已啟用

// UDP Transport (Phase 1 - Non-Blocking)
private System.Net.Sockets.UdpClient m_udpClient;
private const int UDP_PORT = 8002;
[SerializeField] private bool m_useUDPTransport = false;     // ❌ 預設關閉！
```

**與其他場景比較**:

| Scene | m_useServerInference | m_useUDPTransport | 預期行為 |
|-------|---------------------|-------------------|---------|
| MultiObjectDetection | true | true (?) | UDP 模式，非阻塞 |
| PoseEstimation | true | true (?) | UDP 模式，非阻塞 |
| **Segmentation** | **true** | **false** ❌ | **HTTP blocking 或沒送** |

---

## 根本原因

Segmentation 場景的 **UDP Transport 沒有啟用**，導致可能的問題：

### 情況 A：使用 HTTP Blocking Mode
如果 `m_useUDPTransport = false` 但 `m_useServerInference = true`，Unity 會用舊的 HTTP POST 同步模式：
- 每個 frame 阻塞 Unity main thread ~500ms
- FPS 降到 ~2 FPS
- 使用者體驗很差，看起來像「沒反應」

### 情況 B：完全沒送 Request
如果程式碼檢查 `m_useUDPTransport == false` 就跳過發送，那就真的沒送任何 request 到 server。

---

## 解決方案

### 選項 1：在 Unity Inspector 啟用 UDP Transport（推薦）

**步驟**:
1. 開啟 Unity Editor
2. 載入 Segmentation scene
3. 選擇 Segmentation Manager GameObject（或有 SegmentationInferenceRunManager 的物件）
4. 在 Inspector 中找到:
   ```
   Server Inference (NEW)
   ├─ Use Server Inference: ✓
   └─ Use UDP Transport: ☐  ← 勾選這個！
   ```
5. 勾選 **Use UDP Transport**
6. 儲存場景
7. Build and Deploy

**預期結果**:
- Segmentation 改用 UDP 非阻塞模式
- FPS 從 ~2 提升到 5-10
- Server 應該開始接收 UDP frames
- Excel 應該開始記錄 Segmentation 的 telemetry

---

### 選項 2：程式碼修改預設值

如果希望預設就啟用 UDP，可修改程式碼：

**檔案**: `Assets/PassthroughCameraApiSamples/Segmentation/SegmentationInference/Scripts/SegmentationInferenceRunManager.cs`

**第 100 行**:
```csharp
// 修改前
[SerializeField] private bool m_useUDPTransport = false;

// 修改後
[SerializeField] private bool m_useUDPTransport = true;  // ✅ 預設啟用 UDP
```

**注意**: 即使改了預設值，已存在的 scene 不會自動更新。需要：
1. 刪除舊的 scene 檔案，或
2. 在 Inspector 手動勾選，或
3. 用新的 prefab 重新建立 GameObject

---

## 驗證步驟

啟用 UDP Transport 後，執行以下驗證：

### 1. Unity 啟動日誌

**應該看到**:
```
[UDP INIT] UDP client initialized for port 8002
[SEGMENTATION] Using endpoint: http://192.168.0.135:8001/segmentation
[DEBUG] m_useServerInference = true
[UDP SEND] Frame 1 sent to 192.168.0.135:8002, upload_bytes=25340
```

**不應該看到**:
```
[SERVER POST] >>> Sending frame 1 to: http://...  ← 表示還在用 HTTP blocking
```

### 2. Server 端日誌

**應該看到**:
```
[UDP WORKER] Processing sessionid_1 (queue_wait=2.3ms, mode=segmentation)
[SEGMENTATION] Processing RGB-D frame (1280x960)
[SEGMENTATION] YOLO detected 1 person(s)
[UDP WORKER] ✓ Completed sessionid_1 (processing=340.2ms, total=342.5ms)
```

### 3. Excel 檔案

**應該看到 Segmentation 的 rows**:
```
scene         | frame_id | latency_ms | server_proc_ms | final_state
--------------|----------|------------|----------------|------------
Segmentation  | 1        | 425.7      | 340.2          | Displayed
Segmentation  | 2        | 418.3      | 335.5          | Displayed
Segmentation  | 3        | 432.1      | 345.1          | Displayed
```

---

## 為什麼 Segmentation 預設關閉 UDP？

可能原因：
1. **開發順序**: Segmentation 可能是最後更新的場景，當時 UDP transport 還在測試階段
2. **安全起見**: 新功能預設關閉，避免影響現有功能
3. **忘記更新**: 其他場景已啟用，但 Segmentation 漏掉了

---

## 其他可能問題（如果啟用 UDP 後仍無反應）

### A. ServerConfig IP 錯誤
檢查 `Assets/Resources/ServerConfig.asset`:
```
Server IP: 192.168.0.135  ← 應該是你 PC 的 WiFi IP
Port: 8001
Segmentation Endpoint: /segmentation
```

### B. Firewall 阻擋
確認 Windows Firewall 允許:
- 入站 TCP 8001 (HTTP API)
- 入站 UDP 8002 (UDP frames)

### C. Quest 3 網路問題
確認 Quest 3 可以 ping 到 server:
```bash
adb shell
ping 192.168.0.135
```

### D. Server 未啟動 UDP Worker
檢查 server 啟動日誌有沒有:
```
============================================================
UDP INFERENCE WORKER - Started
============================================================
[UDP WORKER] Worker loop started, waiting for UDP frames...
```

---

## 總結

**問題**: Segmentation 的 `m_useUDPTransport = false`

**解決**: 在 Unity Inspector 勾選 **Use UDP Transport**

**預期效果**:
- Segmentation 改用 UDP 非阻塞模式
- Server 開始接收和處理 Segmentation frames
- Excel 開始記錄 Segmentation telemetry
- 「伺服器完全沒反應」問題解決

---

**建議**: 統一所有場景都使用 UDP Transport，避免混用 HTTP/UDP 造成困惑。

---

**最後更新**: 2026-04-17 08:00 UTC
