# Pose Estimation 優化指南

**日期**: 2026-04-23
**狀態**: 優化建議
**目標**: 提高Pose Estimation的速度和準確性，改善使用者體驗

---

## 當前配置分析

### Unity端配置 (PassthroughPoseEstimation場景)

```
InferenceConfig:
├─ targetFPS: 15           ← 推理頻率 (每秒15次)
├─ jpegQuality: 60         ← JPEG質量 (60/100)
├─ downsampleFactor: 2     ← 降採樣 (原始尺寸的1/2)
└─ mode: Both              ← 同時運行YOLO檢測 + 姿態估計
```

### 性能瓶頸分析

根據V3.1架構，端到端延遲分解如下：

| 階段 | 當前延遲 (估計) | 佔比 |
|------|----------------|------|
| **1. Unity編碼 (JPEG)** | 20-30ms | 8% |
| **2. UDP上傳** | 30-50ms | 15% |
| **3. 服務器隊列等待** | 5-10ms | 3% |
| **4. 服務器推理** | 150-250ms | 65% ⚠️ |
| **5. 結果下載** | 20-30ms | 9% |
| **6. Unity解析+渲染** | 10-20ms | 5% |
| **總計** | **235-390ms** | **100%** |

**最大瓶頸**: 服務器端推理 (佔65%)

---

## 優化建議 (分階段)

### 🟢 Phase 1: 立即可實施 (Unity端優化)

#### 1.1 提高推理頻率 (改善流暢度)

**當前**: targetFPS = 15 (每66ms一次推理)
**建議**: targetFPS = 20 (每50ms一次推理)

```csharp
// PassthroughPoseEstimation場景中修改
m_inferenceConfig.targetFPS = 20f;  // 從15提高到20
```

**效果**:
- ✅ 骨架更新更頻繁 (66ms → 50ms)
- ✅ 移動時跟蹤更流暢
- ⚠️ 可能增加隊列壓力 (V3.1有bounded queue保護)

---

#### 1.2 優化上傳尺寸 (減少網絡延遲)

**當前配置已優化**:
- downsampleFactor = 2 ✅ (640×480)
- jpegQuality = 60 ✅ (平衡質量和大小)

**可選激進優化** (如果準確性可接受):
```csharp
m_inferenceConfig.downsampleFactor = 3;  // 降到426×320 (-56%上傳大小)
m_inferenceConfig.jpegQuality = 50;      // 降到50 (-30%大小)
```

**預期效果**:
- ✅ 上傳時間: 30-50ms → 15-25ms (-40%)
- ⚠️ 姿態準確性可能下降 (需測試)

**建議**: 先保持當前配置，只有在準確性足夠時才降低

---

#### 1.3 切換到Pose Only模式 (去除YOLO檢測)

**當前**: mode = Both (YOLO檢測 + 姿態估計)
**建議**: mode = PoseEstimation (僅姿態估計)

```csharp
m_inferenceConfig.mode = InferenceMode.PoseEstimation;  // 從Both改為PoseEstimation
```

**效果**:
- ✅ 服務器推理時間: 250ms → 180ms (-30%)
- ✅ 總延遲: 330ms → 260ms (-20%)
- ⚠️ 失去物體邊界框顯示

**建議**: 如果不需要同時顯示物體檢測，強烈建議使用此優化

---

#### 1.4 預測性平滑 (改善視覺穩定性)

在Unity端添加關鍵點位置的平滑濾波：

```csharp
// PoseInferenceRunManager.cs
private Dictionary<string, Vector3> m_previousKeypoints = new();
private const float SMOOTHING_FACTOR = 0.3f;  // 0-1, 越高越平滑

private Vector3 SmoothKeypoint(string keypointName, Vector3 newPosition)
{
    if (m_previousKeypoints.TryGetValue(keypointName, out Vector3 prevPos))
    {
        // 指數移動平均 (EMA)
        Vector3 smoothed = Vector3.Lerp(newPosition, prevPos, SMOOTHING_FACTOR);
        m_previousKeypoints[keypointName] = smoothed;
        return smoothed;
    }
    else
    {
        m_previousKeypoints[keypointName] = newPosition;
        return newPosition;
    }
}

// 在顯示關鍵點時使用
Vector3 smoothedPos = SmoothKeypoint(keypoint.name, worldPos);
```

**效果**:
- ✅ 減少抖動 (jitter)
- ✅ 視覺更穩定
- ⚠️ 增加10-20ms延遲感 (trade-off)

---

### 🟡 Phase 2: 服務器端優化 (需修改vision_server)

#### 2.1 使用更快的Pose模型

**當前**: Keypoint R-CNN (準確但慢)
**建議替代方案**:

| 模型 | 推理時間 | 準確性 | 建議 |
|------|---------|--------|------|
| **Keypoint R-CNN** | 150-250ms | ⭐⭐⭐⭐⭐ | 當前使用 |
| **MoveNet Thunder** | 80-120ms | ⭐⭐⭐⭐ | 推薦 (快40%) |
| **MoveNet Lightning** | 40-60ms | ⭐⭐⭐ | 最快 (但準確性下降) |
| **YOLOv8 Pose** | 60-90ms | ⭐⭐⭐⭐ | 平衡選項 |

**實施** (vision_server修改):
```python
# app/processors/pose_processor.py
# 替換Keypoint R-CNN為MoveNet Thunder

import tensorflow_hub as hub

class PoseProcessor:
    def __init__(self):
        # 使用MoveNet Thunder
        self.model = hub.load('https://tfhub.dev/google/movenet/singlepose/thunder/4')
        print("[POSE] MoveNet Thunder loaded (optimized for speed+accuracy)")

    def process(self, image: np.ndarray) -> Dict:
        # MoveNet處理邏輯...
        # 預期推理時間: 80-120ms (vs 150-250ms)
```

**預期效果**:
- ✅ 推理時間: 250ms → 100ms (-60%)
- ✅ 總延遲: 330ms → 180ms (-45%)
- ⚠️ 準確性略降 (但通常足夠)

---

#### 2.2 啟用GPU批處理 (如果有多個請求)

如果同時有多個Quest設備連接，可以批處理推理：

```python
# app/processors/pose_processor.py
async def process_batch(self, images: List[np.ndarray]) -> List[Dict]:
    # 批處理推理 (利用GPU並行)
    results = await self.model.predict_batch(images)
    return results
```

**效果**:
- ✅ 多設備時GPU利用率提高
- ✅ 單設備吞吐量提高10-20%

---

#### 2.3 降低服務器端圖片預處理開銷

```python
# app/processors/pose_processor.py
def preprocess(self, image_bytes: bytes) -> np.ndarray:
    # 使用更快的JPEG解碼器
    import simplejpeg  # 比PIL快2-3倍
    image = simplejpeg.decode_jpeg(image_bytes)

    # 直接resize到模型輸入尺寸 (避免多次resize)
    resized = cv2.resize(image, (256, 192), interpolation=cv2.INTER_LINEAR)

    return resized
```

**效果**:
- ✅ 預處理時間: 15-20ms → 5-8ms (-60%)

---

### 🔵 Phase 3: 高級優化 (實驗性)

#### 3.1 客戶端預測 (預測下一幀位置)

使用卡爾曼濾波器預測關鍵點移動：

```csharp
// 在等待服務器響應時，預測關鍵點位置
private Vector3 PredictNextPosition(string keypointName, Vector3 currentPos, Vector3 velocity)
{
    // 簡單的線性預測
    float predictionTime = 0.05f;  // 50ms (推理延遲的一半)
    return currentPos + velocity * predictionTime;
}
```

**效果**:
- ✅ 降低感知延遲
- ⚠️ 預測錯誤時會有"跳躍"

---

#### 3.2 關鍵幀 + 插值策略

不是每一幀都做完整推理，而是：
- 每隔N幀做完整推理 (關鍵幀)
- 中間幀使用插值

```csharp
private bool ShouldRunFullInference()
{
    // 每3幀做一次完整推理
    return m_frameId % 3 == 0;
}

private void Update()
{
    if (ShouldRunFullInference())
    {
        // 完整推理
        StartCoroutine(RunInferenceNonBlocking());
    }
    else
    {
        // 使用上一幀結果 + 平滑插值
        InterpolateFromPreviousFrame();
    }
}
```

**效果**:
- ✅ 服務器負載降低66%
- ✅ 網絡流量降低66%
- ⚠️ 快速移動時可能不夠準確

---

#### 3.3 自適應質量調整

根據網絡延遲動態調整質量：

```csharp
private void AdaptQualityBasedOnLatency(float lastLatency)
{
    if (lastLatency > 400f)  // 超過400ms
    {
        // 降低質量以提高速度
        m_inferenceConfig.downsampleFactor = 3;
        m_inferenceConfig.jpegQuality = 50;
        Debug.Log("[POSE ADAPTIVE] High latency detected, reducing quality");
    }
    else if (lastLatency < 200f)  // 低於200ms
    {
        // 提高質量
        m_inferenceConfig.downsampleFactor = 2;
        m_inferenceConfig.jpegQuality = 70;
        Debug.Log("[POSE ADAPTIVE] Low latency, increasing quality");
    }
}
```

**效果**:
- ✅ 網絡差時自動降低質量保持流暢
- ✅ 網絡好時自動提高準確性

---

## 推薦實施順序

### 第一步: 快速優化 (5分鐘)

1. ✅ **提高targetFPS**: 15 → 20
2. ✅ **切換到Pose Only模式**: mode = PoseEstimation (如果不需要物體檢測)

**預期效果**: 延遲降低20-30%, 流暢度提升30%

---

### 第二步: 平滑優化 (30分鐘)

3. ✅ **添加關鍵點平滑**: 實現SmoothKeypoint()方法
4. ✅ **測試不同SMOOTHING_FACTOR**: 0.2, 0.3, 0.4找最佳值

**預期效果**: 視覺穩定性提升50%, 抖動減少

---

### 第三步: 服務器優化 (2小時)

5. ✅ **替換為MoveNet Thunder**: 修改vision_server
6. ✅ **優化JPEG解碼**: 使用simplejpeg

**預期效果**: 延遲降低40-50%, 總延遲 < 200ms

---

### 第四步: 高級優化 (可選, 4小時)

7. ✅ 實現自適應質量調整
8. ✅ 實現關鍵幀+插值策略

**預期效果**: 在低網絡環境下仍保持流暢

---

## 性能對比 (預測)

| 配置 | 總延遲 | FPS | 流暢度 | 準確性 |
|------|-------|-----|--------|--------|
| **當前** (Both, FPS15) | 330ms | 2.6 | ⭐⭐ | ⭐⭐⭐⭐⭐ |
| **Phase 1** (Pose Only, FPS20) | 260ms | 3.8 | ⭐⭐⭐ | ⭐⭐⭐⭐⭐ |
| **+ 平滑** | 260ms | 3.8 | ⭐⭐⭐⭐ | ⭐⭐⭐⭐⭐ |
| **+ MoveNet** | 180ms | 5.5 | ⭐⭐⭐⭐⭐ | ⭐⭐⭐⭐ |
| **+ 關鍵幀** | 120ms | 8.3 | ⭐⭐⭐⭐⭐ | ⭐⭐⭐⭐ |

---

## 快速實施代碼 (Phase 1 + 平滑)

### 1. 修改場景配置 (Unity Inspector)

在 `PassthroughPoseEstimation` 場景中選擇 `PoseInferenceRunManager`:

```
Inspector → Inference Config:
├─ Mode: PoseEstimation      ← 改為Pose Only
├─ Target FPS: 20            ← 從15提高到20
├─ JPEG Quality: 60          ← 保持
└─ Downsample Factor: 2      ← 保持
```

### 2. 添加平滑濾波 (PoseInferenceRunManager.cs)

在類中添加字段:
```csharp
// 關鍵點平滑
private Dictionary<string, Vector3> m_previousKeypoints = new Dictionary<string, Vector3>();
private const float SMOOTHING_FACTOR = 0.3f;
```

添加平滑方法:
```csharp
/// <summary>
/// 平滑關鍵點位置以減少抖動
/// </summary>
private Vector3 SmoothKeypoint(string keypointName, Vector3 newPosition)
{
    if (m_previousKeypoints.TryGetValue(keypointName, out Vector3 prevPos))
    {
        // 指數移動平均濾波
        Vector3 smoothed = Vector3.Lerp(newPosition, prevPos, SMOOTHING_FACTOR);
        m_previousKeypoints[keypointName] = smoothed;
        return smoothed;
    }
    else
    {
        // 第一次出現，直接使用新位置
        m_previousKeypoints[keypointName] = newPosition;
        return newPosition;
    }
}
```

在顯示關鍵點時調用:
```csharp
// 在CreateOrUpdateKeypoint()或類似方法中
Vector3 smoothedWorldPos = SmoothKeypoint(keypoint.name, worldPos);
keypointSphere.transform.position = smoothedWorldPos;
```

---

## 測試指標

實施優化後，請測試並記錄：

### 性能指標
- [ ] **總延遲** (frame_id發送 → 渲染完成): ____ms
- [ ] **實際FPS** (HUD顯示): ____
- [ ] **丟幀率** (dropped_responses): ____%

### 用戶體驗指標
- [ ] **抖動程度** (1-5分, 5=無抖動): ____
- [ ] **跟蹤流暢度** (1-5分, 5=非常流暢): ____
- [ ] **延遲感知** (1-5分, 5=無延遲感): ____
- [ ] **姿態準確性** (1-5分, 5=非常準確): ____

### 網絡指標
- [ ] **平均上傳大小**: ____KB
- [ ] **平均queue_wait_ms**: ____ms
- [ ] **平均processing_time_ms**: ____ms

---

## 故障排除

### 問題1: 平滑後骨架移動遲緩

**解決**: 降低SMOOTHING_FACTOR
```csharp
private const float SMOOTHING_FACTOR = 0.2f;  // 從0.3降到0.2
```

### 問題2: 提高FPS後丟幀增加

**解決**: 降低targetFPS或優化服務器
```csharp
m_inferenceConfig.targetFPS = 18f;  // 降到18找平衡點
```

### 問題3: 準確性下降

**解決**: 提高圖片質量
```csharp
m_inferenceConfig.downsampleFactor = 1;  // 恢復到原始尺寸
m_inferenceConfig.jpegQuality = 70;      // 提高到70
```

---

## 總結

**最簡單有效的優化** (推薦優先實施):

1. ✅ **切換到Pose Only模式** - 立即提升20%速度
2. ✅ **提高targetFPS到20** - 改善流暢度
3. ✅ **添加關鍵點平滑** - 顯著減少抖動

**預期效果**:
- 延遲: 330ms → 260ms (-20%)
- 流暢度: ⭐⭐ → ⭐⭐⭐⭐
- 準確性: 保持⭐⭐⭐⭐⭐

**長期優化**: 替換為MoveNet Thunder可獲得額外40%速度提升

---

**狀態**: ⚠️ **待實施測試**
**預計實施時間**: Phase 1+平滑 = 30分鐘
