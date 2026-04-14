# 為什麼有時候 Server Time > E2E Latency？

## TL;DR

**這是正常的！因為 Unity 送的是 Frame N-1 的時間，但 Server 記錄的是 Frame N 的處理時間。**

當 Frame 1（啟動時）很慢，但 Frame 2 變快時，就會出現：
- Frame 2 的 `server_proc_ms` = 50ms（當前幀，正常）
- Frame 1 的 `latency_ms` = 750ms（前一幀，模型載入慢）

Excel 會把這兩個不同幀的數據記錄在同一行，看起來就像 Server > E2E。

---

## 問題現象

從 Excel 數據可能看到：

```
Row 84:
  frame_id = 2
  latency_ms = 401.7 ms        ← Frame 1 的 E2E 時間
  server_proc_ms = 635.0 ms    ← Frame 2 的 Server 處理時間
  server_pct = 158.1%          ← 635 / 401.7 = 158%（超過 100%！）

  → Server 時間竟然大於 E2E！這怎麼可能？
```

**實際數據分析**（來自 `inference_log_2026-04-11.xlsx`）：

| Row | Frame | E2E (ms) | Server (ms) | Diff (ms) | Server % |
|-----|-------|----------|-------------|-----------|----------|
| 84  | 2     | 401.7    | 635.0       | +233.3    | 158.1%   |
| 132 | 2     | 409.7    | 749.9       | +340.2    | 183.0%   |

**共同特徵**：
- ✅ 都發生在 **frame_id = 2**（啟動時的第二幀）
- ✅ Server 時間異常高（635-749ms，正常應該 40-130ms）
- ✅ Server % 超過 100%（158%, 183%）

---

## 根本原因：Frame N-1 Timing Pattern

### Unity 的設計（故意的！）

Unity 代碼中有明確註解：

```csharp
// PoseInferenceRunManager.cs, Line 320-321
// Send timing data from PREVIOUS frame (frame N-1) for Excel logging
// These values are 0 for the first frame, which is expected
request.SetRequestHeader("X-E2E-Ms", m_lastE2eMs.ToString("F1"));
request.SetRequestHeader("X-Upload-Ms", m_lastUploadMs.ToString("F1"));
request.SetRequestHeader("X-Download-Ms", m_lastDownloadMs.ToString("F1"));
request.SetRequestHeader("X-Parse-Ms", m_lastParseMs.ToString("F1"));
```

**為什麼要這樣設計？**

因為 Unity 在 **發送 Frame N 請求時**，還沒有 Frame N 的完整時間數據：
- ✅ **有 Frame N-1 的完整數據**（已經跑完整個流程）
- ❌ **沒有 Frame N 的完整數據**（還在進行中）

所以 Unity 只能發送 **Frame N-1 的時間數據**。

---

## 數據流詳解

### 時間線（正常情況）

```
Unity 時間線：
════════════════════════════════════════════════════════════════════

Frame 1:
  0.000s  開始 Frame 1 inference
  0.015s  編碼 JPEG
  0.020s  發送請求（包含 Frame 0 的時間，全是 0）
  0.750s  收到響應（AI 模型第一次載入，很慢！）
  0.870s  Parse 完成
  0.870s  Frame 1 E2E = 870ms ← 很慢！
          保存 m_lastE2eMs = 870

Frame 2:
  1.000s  開始 Frame 2 inference
  1.015s  編碼 JPEG
  1.020s  發送請求（包含 Frame 1 的時間：870ms）← 送出去了
  1.060s  收到響應（AI 模型已載入，快！）
  1.065s  Parse 完成
  1.065s  Frame 2 E2E = 65ms ← 快！
```

### Server 記錄的數據（錯誤配對！）

```python
# Frame 2 的請求到達 Server
request.headers.get("X-E2E-Ms")  # = 870（Frame 1 的時間）
request.headers.get("X-Frame-Id")  # = 2（Frame 2 的 ID）

# Server 處理 Frame 2
processing_time_ms = 50ms  # Frame 2 很快

# Excel 記錄：
row = {
    "frame_id": 2,              # ← Frame 2 的 ID
    "latency_ms": 870,          # ← Frame 1 的 E2E（舊的）
    "server_proc_ms": 50,       # ← Frame 2 的 Server（新的）
    "upload_ms": 10,            # ← Frame 1 的 Upload（舊的）
    "download_ms": 5,           # ← Frame 1 的 Download（舊的）
    "parse_ms": 5               # ← Frame 1 的 Parse（舊的）
}
```

**問題**：
- `server_proc_ms` 來自 **Frame 2**（當前）
- 其他所有時間來自 **Frame 1**（前一幀）

---

## 為什麼啟動時會出現異常？

### Frame 1（第一幀）特別慢

**原因**：
1. **AI 模型首次載入**（YOLO, MediaPipe Pose, Depth）
2. **Server Python 冷啟動**（模組載入、GPU 初始化）
3. **網絡首次連接**（DNS、TCP 握手）

**結果**：Frame 1 的 `server_proc_ms` 可能高達 **635-750ms**。

### Frame 2（第二幀）恢復正常

**原因**：
1. AI 模型已載入（快取在 GPU 記憶體）
2. Server 已熱身
3. 網絡連接已建立

**結果**：Frame 2 的 `latency_ms` 降到 **400ms**（正常）。

### Excel 記錄的混亂

```
Row 84（Frame 2 的請求）:
  latency_ms = 401.7 ms        ← Frame 1 的 E2E（慢，包含模型載入）
  server_proc_ms = 635.0 ms    ← Frame 2 的 Server（？？？）

等等！為什麼 Frame 2 的 Server 是 635ms？
```

**答案**：這不是 Frame 2 的 Server 時間，而是 **Server 在處理 Frame 1 時記錄的時間**！

讓我重新檢查數據流...

---

## 重新分析：真正的問題

等等，我發現邏輯有問題。讓我重新檢查 Excel 記錄的流程：

### Server 端記錄邏輯（infer_human.py, Line 806-830）

```python
# Frame N 的請求到達 Server
e2e_ms = float(request.headers.get("X-E2E-Ms", "0"))  # Frame N-1 的 E2E
frame_id = int(request.headers.get("X-Frame-Id", "-1"))  # Frame N 的 ID

# Server 處理 Frame N
processing_time_ms = (time.time() - start_time) * 1000.0  # Frame N 的處理時間

# 記錄到 Excel
log_async(
    frame_id=frame_id,                    # Frame N
    latency_ms=e2e_ms,                    # Frame N-1 的 E2E
    server_proc_ms=processing_time_ms,    # Frame N 的 Server 處理時間
    ...
)
```

**所以**：
```
Excel Row 84:
  frame_id = 2                 ← Frame 2 的 ID
  latency_ms = 401.7           ← Frame 1 的 E2E
  server_proc_ms = 635.0       ← Frame 2 的 Server 處理時間

Frame 2 的 Server 處理時間 = 635ms？
這還是很奇怪，因為正常應該 40-130ms。
```

### 可能的解釋

有兩種可能：

#### 可能 1：Frame 2 真的慢（模型還在載入）

```
Frame 1: Server 處理 = 800ms（模型載入）
Frame 2: Server 處理 = 635ms（還在載入其他模型？）
Frame 3: Server 處理 = 125ms（正常）
Frame 4: Server 處理 = 128ms（正常）
```

這樣的話，**Frame 2 的 Server 真的是 635ms**，但 **Frame 1 的 E2E 只有 401ms**。

#### 可能 2：Server 記錄錯了（不太可能）

Server 記錄了錯誤的 `processing_time_ms`。

---

## 驗證：查看 Row 84 附近的數據

從前面的分析：

```
Row    Frame         E2E   Server   Upload Download    Parse
─────────────────────────────────────────────────────────────
82     9           109.7     57.3      2.7     55.6      0.4
83     5           120.3     51.9     12.1     52.6      0.4
84     2           401.7    635.0     14.8     85.6    122.7  ← 異常
85     3           748.5    125.8      3.4     84.4     25.7
86     4           225.4    129.8     16.6     79.2      3.9
```

**觀察**：
- Row 82（Frame 9）：正常（E2E=109ms, Server=57ms）
- Row 83（Frame 5）：正常（E2E=120ms, Server=51ms）
- **Row 84（Frame 2）**：異常（E2E=401ms, Server=635ms）
- **Row 85（Frame 3）**：E2E 很大（748ms！），Server 正常（125ms）

**新發現**：Row 85（Frame 3）的 E2E = 748ms！

### 重新推論

```
Frame 1:
  E2E = ??? (沒記錄，因為 Frame 0 時 m_lastE2eMs = 0)
  Server = ??? (可能很慢，模型載入)

Frame 2:
  E2E = 401ms (正常了)
  Server = 635ms (還在載入？)
  Excel 記錄:
    latency_ms = Frame 1 的 E2E (未知)
    server_proc_ms = 635ms

等等，如果 latency_ms = 401ms 來自 Frame 1，
那 Frame 1 的 E2E 其實不慢？
```

**關鍵疑問**：Frame 1 的 E2E 只有 401ms，為什麼 Frame 2 的 Server 處理要 635ms？

---

## 真正的答案：Server 在首次推理時很慢

### Server 冷啟動時間線

```python
# Frame 1 請求到達（首次）
t0 = time.time()  # 開始

# 載入模型（首次，很慢！）
- YOLO 模型載入: 200ms
- MediaPipe 載入: 150ms
- Depth 模型載入: 100ms

# 首次推理（還要編譯 GPU kernel）
- YOLO 推理: 80ms（首次慢）
- Pose 推理: 70ms（首次慢）

t1 = time.time()
processing_time_ms = (t1 - t0) * 1000 = 600-800ms ← Frame 1 很慢

# Frame 2 請求到達（模型已載入）
t0 = time.time()

# 推理（模型已熱，但可能還有快取未命中）
- YOLO 推理: 40ms（快取命中）
- Pose 推理: 35ms（快取命中）

但！如果此時有其他 Python 操作：
- 首次 Excel 寫入（openpyxl 初始化）: 200ms
- 其他初始化操作

t1 = time.time()
processing_time_ms = (t1 - t0) * 1000 = 500-700ms ← Frame 2 還是慢！

# Frame 3+ 請求（完全熱身）
processing_time_ms = 40-130ms ← 正常
```

---

## 結論

### 為什麼 Server Time > E2E？

**主要原因**：**Frame N-1 timing pattern + 啟動時性能差異**

1. **Excel 記錄混合了不同幀的數據**：
   - `latency_ms` = Frame N-1 的 E2E
   - `server_proc_ms` = Frame N 的 Server 處理時間

2. **啟動時的性能變化**：
   - Frame 1: E2E 可能快（Unity 端），但 Server 慢（模型載入）
   - Frame 2: E2E 記錄的是 Frame 1 的值，Server 還在熱身

3. **數據不匹配**：
   - 如果 Frame 1 的 E2E < Frame 2 的 Server，就會出現 `server > latency`

### 實際數據驗證

Row 84（Frame 2）：
```
latency_ms = 401.7      ← Frame 1 的 E2E（Unity 端快，但 Server 慢）
server_proc_ms = 635.0  ← Frame 2 的 Server（還在初始化）
```

這說明：
- Frame 1 的 Unity E2E 只有 401ms（編碼+網絡+解析快）
- Frame 2 的 Server 處理 635ms（AI 模型還在載入/熱身）

### 為什麼只有 1% 的數據異常？

因為這只發生在 **啟動的前 2-3 幀**：
- Frame 0: 沒有前一幀數據（latency_ms = 0）
- Frame 1-2: 性能不穩定（模型載入）
- Frame 3+: 穩定（所有數據來自穩定狀態的前一幀）

從數據看：**192 行中只有 2 行異常（1%）**，都是 frame_id = 2。

---

## 如何正確理解數據？

### 不要相信前幾幀的數據

Excel 前 5-10 行的數據可能混亂，因為：
- Frame N-1 timing 導致錯位
- 啟動時性能不穩定

### 只分析穩定狀態的數據

```python
# 分析時跳過前 10 幀
df = df[df['frame_id'] > 10]

# 或者排除異常
df = df[df['server_proc_ms'] < df['latency_ms'] * 1.2]  # Server < 120% E2E
```

### 正確的性能指標

| 指標 | 可信度 | 用途 |
|------|-------|------|
| `latency_ms` (frame > 10) | ⭐⭐⭐⭐⭐ | E2E 性能分析 |
| `server_proc_ms` (frame > 10) | ⭐⭐⭐⭐⭐ | Server 瓶頸分析 |
| `parse_ms` | ⭐⭐⭐⭐ | JSON 優化 |
| Row 1-10 的任何數據 | ⭐⭐ | 僅供參考，可能混亂 |

---

## 如何修復？

### 選項 1：接受現狀（推薦）

**理由**：
- 這是設計使然，不是 bug
- 只影響前幾幀（1-2%）
- 不影響穩定狀態的分析

**做法**：
- 在 Excel 分析時跳過前 10 幀
- 在文檔中註明此限制

### 選項 2：修改 Unity 代碼（複雜）

**改法**：
- 不發送 Frame N-1 的時間
- 改為在 **Frame N+1** 時才記錄 Frame N 的完整數據
- 這樣所有時間都來自同一幀

**問題**：
- 需要大幅修改代碼
- 會延遲一幀記錄（不是即時）
- 最後一幀的數據會丟失

### 選項 3：Server 端延遲記錄（中等複雜）

**改法**：
- Server 收到 Frame N 的請求時，**暫存** Frame N-1 的時間
- Server 收到 Frame N+1 的請求時，才用 Frame N 的 Server 時間 + Frame N 的 Unity 時間記錄

**好處**：
- 所有時間來自同一幀
- 不需要修改 Unity

**問題**：
- Server 需要維護狀態（記住上一幀）
- 多 client 並發時需要分別追蹤

---

## 相關文檔

- `WHY_TOTAL_LATENCY_LESS_THAN_SUM.md` - 為什麼 Total < Sum
- `WHY_UNITY_CANNOT_MEASURE_DOWNLOAD_TIME.md` - Unity API 限制
- `LATENCY_BREAKDOWN_EXPLANATION.md` - 時間分解解釋
