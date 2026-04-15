# 為什麼 Download Bytes 小但 Latency 大？

## 問題現象

從 Excel 數據看到：
- **Upload**: 135 KB (compressed) → 10-15 ms
- **Download**: 1.5 KB → 40-50 ms ⚠️

下載數據量只有上傳的 1%，但時間卻是 3-4 倍！

---

## 根本原因：`download_ms` 不是真正的下載時間

### 當前計算方式（Line 446）：

```csharp
float downloadMs = Mathf.Max(0f, e2eMs - uploadMs - serverProcMs - parseMs);
```

**這是一個推算值，包含了所有未被明確計時的部分：**

```
E2E總時間 (100ms 範例)
├─ upload_ms (15ms) ✅ 明確計時
├─ server_proc_ms (30ms) ✅ 明確計時
├─ parse_ms (5ms) ✅ 明確計時
└─ download_ms (50ms) ⚠️ = 100 - 15 - 30 - 5
   └─ 這50ms包含了：
       ├─ TCP/HTTP 握手時間
       ├─ 服務器準備響應時間（postprocess）
       ├─ 網絡往返延遲 (RTT)
       ├─ 真正的數據傳輸時間（實際很短！< 5ms）
       ├─ Unity UnityWebRequest 內部處理
       └─ 其他未計時的開銷
```

---

## 時間線詳解

```
時間軸 (單位: ms)
═══════════════════════════════════════════════════════════════

0ms    [Unity] 開始 upload
       ├─ 編碼 JPEG
       ├─ TCP 連接建立
       └─ HTTP POST 發送 135KB

15ms   [Server] 收到請求 (t_server_recv)
       ├─ 解碼圖像
       ├─ YOLO 推理
       └─ 構建 JSON 響應

45ms   [Server] 完成處理 (t_server_send)
       ├─ 發送 HTTP 響應 (1.5KB)
       └─ TCP 確認

50ms   [Unity] 收到響應 (request.isDone = true)
       ⚠️ 從 45ms → 50ms 這 5ms 才是真正的下載時間！
       └─ 開始 JSON parse

55ms   [Unity] Parse 完成
```

**真正的數據傳輸時間**：
- Upload: 15 ms（發送 135 KB）
- **Download: 約 5 ms**（接收 1.5 KB）← 這才是真實的下載時間！

**但 Excel 記錄的 `download_ms = 50ms`**，因為它包含了：
- Server 準備響應：30 ms
- 網絡 RTT：10 ms
- Unity 處理開銷：5 ms
- 真正下載：5 ms

---

## 為什麼會這樣設計？

Unity 的 `UnityWebRequest` **不提供下載開始時間戳**，所以我們無法精確測量純下載時間。

當前代碼只能測量：
- ✅ `uploadMs`: 從開始發送到 `SendWebRequest()` 返回
- ✅ `serverProcMs`: 從服務器收到請求到處理完成（server 返回）
- ✅ `parseMs`: JSON 反序列化時間
- ❌ `downloadMs`: **只能用減法推算**

---

## 正確理解

| 指標 | 實際含義 |
|------|---------|
| `upload_ms` | 上傳時間（編碼 + 發送） |
| `server_proc_ms` | 服務器 AI 推理時間 |
| `download_ms` | **剩餘時間**（包含下載、網絡延遲、處理開銷） |
| `parse_ms` | JSON 反序列化時間 |

**`download_ms` 大不代表下載慢**，而是代表：
1. 網絡往返延遲高
2. 服務器準備響應需要時間
3. Unity 內部處理有開銷

---

## 如何優化？

如果想減少 `download_ms`：

1. **減少網絡 RTT**
   - 使用更快的網絡連接
   - Server 和 Quest 3 在同一局域網
   - 使用有線連接（Quest 3 可通過 USB）

2. **優化 Server 後處理**
   - 減少 JSON 構建時間
   - 使用更高效的序列化格式（MessagePack, Protobuf）

3. **減少 Unity 開銷**
   - 使用 `UnityWebRequest` 的異步 API
   - 避免主線程阻塞

---

## 結論

**Download bytes 小但 latency 大是正常的**，因為：

1. `download_ms` 不只是下載時間，還包含網絡延遲、server準備時間等
2. 真正的下載時間（1.5 KB）實際上很快（約 5 ms）
3. 大部分的 `download_ms` 是網絡往返和處理開銷

**這不是問題，而是測量方式的特性！**
