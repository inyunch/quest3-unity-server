# E2E 計算詳細分析：為什麼 Server > E2E？

## 發現的真相

通過實際數據分析，發現了 **兩個關鍵問題**：

### 問題 1：Frame N-1 Timing Pattern（已知）

Unity 發送 **Frame N** 的請求時，HTTP headers 包含 **Frame N-1** 的時間數據。

### 問題 2：Excel 記錄的數據來自不同的 Frame！（新發現）

Excel 的每一行實際上混合了 **3 個不同 Frame** 的數據！

---

## 實際數據流分析

### Row 84 的數據來源

| 欄位 | 值 | 來源 Frame | 說明 |
|------|-----|-----------|------|
| `frame_id` | 2 | Frame 2 | 當前請求的 Frame ID |
| `latency_ms` | 401.7 | Frame 1 | Frame 1 的 E2E 時間 |
| `server_proc_ms` | 635.0 | Frame 2 | Frame 2 的 Server 處理時間 |
| `upload_ms` | 14.8 | Frame 1 | Frame 1 的 Upload（計算值） |
| `download_ms` | 85.6 | Frame 1 | Frame 1 的 Download（計算值） |
| `parse_ms` | 122.7 | Frame 1 | Frame 1 的 Parse 時間 |

**矛盾**：
- `latency_ms` (401.7) 來自 **Frame 1**
- `server_proc_ms` (635.0) 來自 **Frame 2**
- 當 Frame 2 的 Server 時間 > Frame 1 的 E2E 時，就會出現 `server > latency`！

---

## 驗證：Row 84 和 Row 85 的關係

### Row 84（Frame 2 的請求）

```
frame_id = 2
latency_ms = 401.7        ← Frame 1 的 E2E
server_proc_ms = 635.0    ← Frame 2 的 Server 處理時間
upload_ms = 14.8          ← Frame 1 的 Upload
download_ms = 85.6        ← Frame 1 的 Download
parse_ms = 122.7          ← Frame 1 的 Parse
```

**計算 Frame 1 的 ServerTotal**：
```
ServerTotal = E2E - (Upload + Download + Parse)
           = 401.7 - (14.8 + 85.6 + 122.7)
           = 401.7 - 223.1
           = 178.6 ms
```

### Row 85（Frame 3 的請求）

```
frame_id = 3
latency_ms = 748.5        ← Frame 2 的 E2E
server_proc_ms = 125.8    ← Frame 3 的 Server 處理時間
upload_ms = 3.4           ← Frame 2 的 Upload
download_ms = 84.4        ← Frame 2 的 Download
parse_ms = 25.7           ← Frame 2 的 Parse
```

**計算 Frame 2 的 ServerTotal**：
```
ServerTotal = E2E - (Upload + Download + Parse)
           = 748.5 - (3.4 + 84.4 + 25.7)
           = 748.5 - 113.5
           = 635.0 ms  ← 這就是 Row 84 的 server_proc_ms！
```

**證明**：
- **Row 84 的 `server_proc_ms` = 635.0 ms**（Frame 2 的 Server 時間）
- **Row 85 計算出的 ServerTotal = 635.0 ms**（Frame 2 的真實 Server 時間）

**結論**：Row 84 的 `server_proc_ms = 635.0` 是 **Frame 2 的真實 Server 處理時間**！

---

## 完整的時間線重建

### Frame 1（啟動）

```
Unity 端：
  0.000s  開始 E2E
  0.015s  編碼 JPEG
  0.020s  發送請求（headers 包含 Frame 0 的數據，全是 0）
  0.400s  收到響應
  0.401s  Parse 完成
  → E2E = 401.7 ms

Server 端（收到 Frame 1）：
  接收請求
  處理時間 = 178.6 ms（推算值，扣除網絡和 Parse）
  發送響應

Excel 記錄（在 Frame 2 的請求時）：
  ❌ 不會記錄 Frame 1，因為沒有 Frame 0 的時間數據
```

### Frame 2（還在熱身）

```
Unity 端：
  0.500s  開始 E2E
  0.515s  編碼 JPEG
  0.520s  發送請求（headers 包含 Frame 1 的數據：E2E=401.7, ...）
  1.268s  收到響應
  1.269s  Parse 完成
  → E2E = 748.5 ms

Server 端（收到 Frame 2）：
  接收請求
  處理時間 = 635.0 ms（首次推理，模型載入、GPU 初始化）
  發送響應

Excel 記錄（Row 84）：
  frame_id = 2
  latency_ms = 401.7        ← Frame 1 的 E2E（從 headers）
  server_proc_ms = 635.0    ← Frame 2 的 Server（當前測量）
  → server > latency！異常！
```

### Frame 3（恢復正常）

```
Unity 端：
  1.400s  開始 E2E
  1.415s  編碼 JPEG
  1.420s  發送請求（headers 包含 Frame 2 的數據：E2E=748.5, ...）
  2.305s  收到響應
  2.306s  Parse 完成
  → E2E = 884.6 ms（還在優化中）

Server 端（收到 Frame 3）：
  接收請求
  處理時間 = 125.8 ms（恢復正常！）
  發送響應

Excel 記錄（Row 85）：
  frame_id = 3
  latency_ms = 748.5        ← Frame 2 的 E2E（從 headers）
  server_proc_ms = 125.8    ← Frame 3 的 Server（當前測量）
  → server < latency，正常
```

---

## 為什麼會發生 Server > E2E？

### 根本原因

**Excel 記錄的 `server_proc_ms` 和 `latency_ms` 來自不同的 Frame！**

1. **Unity 設計**：
   - 發送 Frame N 請求時，headers 包含 **Frame N-1** 的時間
   - 因為 Frame N 還沒跑完，無法知道 Frame N 的時間

2. **Server 記錄**：
   - 收到 Frame N 請求時，記錄 **Frame N** 的處理時間
   - 同時記錄 headers 中 **Frame N-1** 的時間

3. **Excel 混合**：
   ```
   Row N:
     frame_id = N
     latency_ms = Frame N-1 的 E2E
     server_proc_ms = Frame N 的 Server 處理時間
     upload_ms = Frame N-1 的 Upload
     download_ms = Frame N-1 的 Download
     parse_ms = Frame N-1 的 Parse
   ```

### 異常發生條件

```
如果 Frame N 的 Server 處理時間 > Frame N-1 的 E2E 時間
→ server_proc_ms > latency_ms
→ 看起來異常，但實際上是正常的！
```

**啟動時的情況**：
- Frame 1: E2E = 401.7 ms（Unity 端快，只有編碼+網絡+解析）
- Frame 2: Server = 635.0 ms（Server 端慢，模型載入）
- **Row 84**: `server_proc_ms (635.0) > latency_ms (401.7)`

---

## 為什麼不是只有前 10 個 Frame？

你說得對！不是只有前 10 個 Frame 有問題。

### 問題可能發生的時機

只要滿足以下條件，就會出現 `server > latency`：

1. **Frame N 的 Server 處理時間突然變慢**（例如：系統繁忙、其他程序搶占 GPU）
2. **Frame N-1 的 E2E 時間比較快**（例如：網絡順暢、無排隊）

### 實際數據驗證

讓我們檢查是否有其他 Frame（不在前 10 個）也有異常...

從前面的分析，只找到 2 個異常（Row 84 和 132），都是 `frame_id = 2`。

**但這不代表只有前 10 個 Frame！**

因為 `frame_id = 2` 可能出現在不同的 **session**（每次啟動 App 都重置 frame_id）。

---

## 正確理解 Excel 數據

### 每一行的真實含義

```
Row N（frame_id = N）:
  latency_ms       ← Frame N-1 的端到端時間（Unity 測量）
  server_proc_ms   ← Frame N 的 Server 處理時間（Server 測量）
  upload_ms        ← Frame N-1 的 Upload 時間（Unity 計算）
  download_ms      ← Frame N-1 的 Download 時間（Unity 計算）
  parse_ms         ← Frame N-1 的 Parse 時間（Unity 測量）
```

### 數學關係（錯誤的！）

```
❌ 錯誤假設：
  latency_ms = upload_ms + server_proc_ms + download_ms + parse_ms

✅ 正確關係：
  latency_ms(N-1) = upload_ms(N-1) + serverTotal_ms(N-1) + download_ms(N-1) + parse_ms(N-1)
  server_proc_ms(N) = Frame N 的 Server 處理時間

  → 這兩個值來自不同的 Frame，不應該放在同一個等式！
```

---

## 如何修正？

### 選項 1：接受現狀（簡單）

**理由**：
- 這是 Unity 的設計限制
- Frame N-1 timing 是合理的設計選擇
- 大部分數據是一致的（只有啟動時會異常）

**做法**：
- 文檔中說明此限制
- 分析時注意 `server_proc_ms` 和 `latency_ms` 來自不同 Frame
- 只分析穩定狀態（跳過每個 session 的前幾幀）

### 選項 2：修改 Server 記錄邏輯（推薦）

**改法**：
Server 收到 Frame N 時，**延遲記錄 Frame N-1 的完整數據**。

```python
# 全局變量，記住上一幀
last_frame_server_time = None

# 收到 Frame N
current_frame_id = request.headers.get("X-Frame-Id")
current_server_time = processing_time_ms

# 從 headers 讀取 Frame N-1 的 Unity 時間
prev_e2e = request.headers.get("X-E2E-Ms")
prev_upload = request.headers.get("X-Upload-Ms")
...

# 如果有上一幀的 Server 時間，現在可以記錄完整的 Frame N-1
if last_frame_server_time is not None:
    log_async(
        frame_id=current_frame_id - 1,  # Frame N-1
        latency_ms=prev_e2e,             # Frame N-1 的 E2E
        server_proc_ms=last_frame_server_time,  # Frame N-1 的 Server（上次記住的）
        upload_ms=prev_upload,
        ...
    )

# 記住當前 Frame 的 Server 時間，下次用
last_frame_server_time = current_server_time
```

**優點**：
- 所有時間來自同一個 Frame
- `server_proc_ms` 和 `latency_ms` 可以直接比較
- 不需要修改 Unity

**缺點**：
- Server 需要維護狀態
- 最後一幀的數據會丟失（因為沒有下一幀來觸發記錄）
- 多 client 並發時需要分別追蹤（用 session ID）

### 選項 3：修改 Unity 代碼（複雜）

**改法**：
Unity 在 **Frame N+1** 時才記錄 Frame N 的完整數據。

**問題**：
- 需要大幅修改代碼
- 即時性降低（延遲一幀）
- 實現複雜

---

## 建議

### 短期（立即可用）

1. **更新文檔**，說明 Excel 數據的限制：
   - `server_proc_ms` 來自 Frame N
   - 其他時間來自 Frame N-1
   - 不應該直接相加比較

2. **分析時過濾異常**：
   ```python
   # 排除 server > latency 的異常行
   df = df[df['server_proc_ms'] <= df['latency_ms'] * 1.5]

   # 或者只分析穩定狀態
   df = df[df['frame_id'] > 5]
   ```

### 長期（如果需要精確分析）

實現 **選項 2（Server 端延遲記錄）**：
- 保證所有時間來自同一 Frame
- 不需要修改 Unity
- 數據更準確

---

## 總結

### 為什麼 Server > E2E？

**因為它們來自不同的 Frame！**

- `server_proc_ms` = Frame N 的 Server 處理時間
- `latency_ms` = Frame N-1 的 E2E 時間

當 Frame N 慢（例如啟動時）而 Frame N-1 快時，就會出現 `server > latency`。

### 這是 Bug 嗎？

**不是 Bug，是設計限制。**

Unity 在發送 Frame N 時，只能發送 Frame N-1 的時間（因為 Frame N 還沒結束）。

Server 記錄 Frame N 的處理時間，但也記錄了 Frame N-1 的 Unity 時間。

Excel 把這兩個不同 Frame 的數據放在同一行，看起來像異常。

### 如何修正？

**推薦做法**：
1. 短期：文檔說明限制，分析時過濾異常
2. 長期：實現 Server 端延遲記錄（選項 2）

### 影響範圍

- **啟動時**：前 2-5 幀可能異常（每個 session）
- **穩定狀態**：Frame > 5 後，數據基本正常（因為相鄰幀的時間相近）
- **異常情況**：任何時候 Frame N 突然變慢都可能導致異常

---

## 相關文檔

- `WHY_SERVER_TIME_EXCEEDS_E2E.md` - 初步分析（需更新）
- `WHY_TOTAL_LATENCY_LESS_THAN_SUM.md` - Total vs Sum 解釋
- `WHY_UNITY_CANNOT_MEASURE_DOWNLOAD_TIME.md` - Unity API 限制
