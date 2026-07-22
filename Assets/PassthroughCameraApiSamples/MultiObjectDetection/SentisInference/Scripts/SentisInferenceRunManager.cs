// Copyright (c) Meta Platforms, Inc. and affiliates.

using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Meta.XR;
using Meta.XR.Samples;
using Unity.Collections;
using Unity.InferenceEngine;
using UnityEngine;
using UnityEngine.Networking;
using PassthroughCameraSamples.Shared;
using PassthroughCameraSamples.Shared.ControlPlane;

namespace PassthroughCameraSamples.MultiObjectDetection
{
    [MetaCodeSample("PassthroughCameraApiSamples-MultiObjectDetection")]
    public class SentisInferenceRunManager : MonoBehaviour, IControlPlaneTarget
    {
        [SerializeField] private PassthroughCameraAccess m_cameraAccess;
        [SerializeField] private DetectionUiMenuManager m_uiMenuManager;
        [SerializeField] private DetectionManager m_detectionManager;

        [Header("Sentis Model config")]
        [SerializeField] private BackendType m_backend = BackendType.CPU;
        [SerializeField] private ModelAsset m_sentisModel;
        [SerializeField] private TextAsset m_labelsAsset;
        [SerializeField, Range(0, 1)] private float m_iouThreshold = 0.6f;
        [SerializeField, Range(0, 1)] private float m_scoreThreshold = 0.23f;

        [Header("UI display references")]
        [SerializeField] private SentisInferenceUiManager m_uiInference;
        [SerializeField] private InferenceHUD m_inferenceHUD;
        [SerializeField] private SharedInferenceHUD m_sharedHUD;

        [Header("Server Inference")]
        [SerializeField] private bool m_useServerInference = false;
        [SerializeField] private bool m_useUDPTransport = true;
        [SerializeField] private InferenceConfig m_inferenceConfig = new InferenceConfig
        {
            mode = InferenceMode.ObjectDetection,
            targetFPS = 5f,
            jpegQuality = 80,
            includeMask = false,
            includeDepth = false
        };

        [Header("Control Plane")]
        [Tooltip("Enable control plane: dynamic pacing, N gate, profile-based resize/quality. " +
                 "When off, falls back to InferenceConfig targetFPS/jpegQuality/downsampleFactor.")]
        [SerializeField] private bool m_useControlPlane = true;
        [Tooltip("Initial OperatingProfile id (P1-P5). Add RuntimeController to this GameObject for adaptive policies.")]
        [SerializeField] private string m_initialProfileId = "P3";

        [Header("[Editor Only] Convert to Sentis")]
        public ModelAsset OnnxModel;
        [Space(40)]

        // ============================================================================
        // V3.0 OOP COMPONENTS
        // ============================================================================
        private UDPTransportManager m_transport;
        private FrameTelemetryTracker m_telemetry;

        // Control plane
        private ControlKnobs m_knobs;
        private MetricsAggregator m_metrics;
        private Coroutine m_sendLoop;
        private int m_nGateDrops;

        // Sentis engine
        private Worker m_engine;
        private Vector2Int m_inputSize;
        private readonly List<(int classId, Vector4 boundingBox)> m_detections = new List<(int classId, Vector4 boundingBox)>();

        // Session tracking
        private int m_frameId = 0;
        private string m_sessionId;
        private bool m_cameraReady = false;
        private bool m_isInitialized = false;

        // ============================================================================
        // IControlPlaneTarget (RuntimeController binds to these)
        // ============================================================================
        public ControlKnobs Knobs        => m_knobs;
        public MetricsAggregator Metrics  => m_metrics;
        public string SessionId           => m_sessionId;

        private void Awake()
        {
            var model = ModelLoader.Load(m_sentisModel);
            var inputShape = model.inputs[0].shape;
            m_inputSize = new Vector2Int(inputShape.Get(2), inputShape.Get(3));
            m_engine = new Worker(model, m_backend);
        }

        private IEnumerator Start()
        {
            m_sessionId = System.Guid.NewGuid().ToString();
            Debug.Log($"[V3 DETECTION] Started session: {m_sessionId}");

            if (m_useServerInference && m_useUDPTransport)
            {
                try
                {
                    m_transport = new UDPTransportManager(
                        serverIP:    ServerConfig.Instance.ServerIP,
                        sendPort:    8002,
                        receivePort: 8003
                    );
                    m_transport.Initialize();
                    Debug.Log("[V3 DETECTION] UDP Transport initialized");

                    m_telemetry = new FrameTelemetryTracker(
                        sessionId:            m_sessionId,
                        sceneName:            "MultiObjectDetection",
                        enableLocalTelemetry: true
                    );
                    Debug.Log("[V3 DETECTION] Telemetry tracker initialized");

                    // Control plane: initialize from profile table (only when enabled)
                    if (m_useControlPlane)
                    {
                        var initialProfile = OperatingProfile.Get(m_initialProfileId);
                        if (initialProfile == null)
                        {
                            Debug.LogWarning($"[V3 DETECTION] Unknown profile '{m_initialProfileId}', defaulting to P3");
                            initialProfile = OperatingProfile.Get("P3");
                        }
                        m_knobs   = new ControlKnobs(initialProfile);
                        m_metrics = new MetricsAggregator(() => m_telemetry?.GetPendingCount() ?? 0);
                        Debug.Log($"[V3 DETECTION] Control plane initialized. profile={initialProfile.Id}, " +
                                  $"fps={initialProfile.TargetFps}, resFactor={initialProfile.ResFactor}");
                    }
                    else
                    {
                        Debug.Log("[V3 DETECTION] Control plane disabled — using InferenceConfig settings");
                    }
                }
                catch (System.Exception e)
                {
                    Debug.LogError($"[V3 DETECTION] Failed to initialize V3 components: {e.Message}");
                    m_useUDPTransport = false;
                }
            }

            m_uiInference.SetLabels(m_labelsAsset);

            if (m_useServerInference)
            {
                m_inferenceConfig.Validate();
                m_inferenceConfig.LogSummary();

                if (m_sharedHUD != null)
                {
                    float displayFps = m_knobs != null ? m_knobs.CurrentProfile.TargetFps : m_inferenceConfig.targetFPS;
                    m_sharedHUD.SetMode(m_inferenceConfig.mode, displayFps);
                }

                Debug.Log("[V3 DETECTION] Testing connection to server...");
                yield return TestServerConnection();
            }

            m_cameraReady   = true;
            m_isInitialized = m_useServerInference && m_useUDPTransport && m_transport != null;

            if (m_isInitialized)
                m_sendLoop = StartCoroutine(SendLoop());

            Debug.Log("[V3 DETECTION] Start() complete");
        }

        private void OnDestroy()
        {
            m_engine.PeekOutput(0)?.CompleteAllPendingOperations();
            m_engine.PeekOutput(1)?.CompleteAllPendingOperations();
            m_engine.PeekOutput(2)?.CompleteAllPendingOperations();
            m_engine.Dispose();

            if (m_sendLoop != null) StopCoroutine(m_sendLoop);
            m_transport?.Shutdown();
            m_telemetry?.Shutdown();

            Debug.Log($"[V3 DETECTION] Cleanup complete. N-gate drops={m_nGateDrops}");
        }

        internal static void PreloadModel(ModelAsset modelAsset)
        {
            var model = ModelLoader.Load(modelAsset);
            var inputShape = model.inputs[0].shape;

            using var worker = new Worker(model, BackendType.CPU);

            // Warm-up inference to front-load JIT compilation; first real inference would otherwise block the thread
            Texture tempTexture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            var textureTransform = new TextureTransform().SetDimensions(tempTexture.width, tempTexture.height, 3);
            using var input = new Tensor<float>(new TensorShape(1, 3, inputShape.Get(2), inputShape.Get(3)));
            TextureConverter.ToTensor(tempTexture, input, textureTransform);
            worker.Schedule(input);

            worker.PeekOutput(0).CompleteAllPendingOperations();
            worker.PeekOutput(1).CompleteAllPendingOperations();
            worker.PeekOutput(2).CompleteAllPendingOperations();
            Destroy(tempTexture);
        }

        // ============================================================================
        // V3.0: UPDATE LOOP
        // ============================================================================

        private void Update()
        {
            if (!m_isInitialized) return;

            // Drain UDP response queue
            while (m_transport.TryGetResponse(out FrameResponse response))
                HandleV3Response(response);

            // Age sampler (P2): only when control plane is active
            if (m_metrics != null)
            {
                int displayedId = m_telemetry.GetLastDisplayedFrameId();
                if (displayedId >= 0)
                {
                    FrameTrace displayedTrace = m_telemetry.GetTrace(displayedId);
                    if (displayedTrace != null && displayedTrace.unity_send_ts > 0)
                    {
                        float ageMs = TimestampUtil.GetUnixTimestampMs() - displayedTrace.unity_send_ts;
                        m_metrics.PushAge(ageMs);
                    }
                }

                if (m_sharedHUD != null)
                    m_sharedHUD.UpdateControlPlaneMetrics(m_metrics.Snapshot(), m_knobs.CurrentProfile.Id);
            }

            // Periodic telemetry cleanup (~every 5 seconds at 60fps)
            if (Time.frameCount % 300 == 0)
                m_telemetry.CleanupOldTraces();

#if UNITY_EDITOR
            DebugProfileSwitch();
#endif
        }

        // ============================================================================
        // V3.0: DYNAMIC-PACING SEND LOOP (P1 - replaces fixed cadence from Update)
        // ============================================================================

        private IEnumerator SendLoop()
        {
            while (true)
            {
                bool paused = m_uiMenuManager != null && m_uiMenuManager.IsPaused;
                if (m_cameraReady && !paused)
                    SendNextFrame();

                float fps = m_knobs != null
                    ? m_knobs.CurrentProfile.TargetFps
                    : m_inferenceConfig.targetFPS;
                yield return new WaitForSeconds(1f / Mathf.Max(0.1f, fps));
            }
        }

        // ============================================================================
        // V3.0: FRAME SEND (N gate + profile resize + encode)
        // ============================================================================

        private void SendNextFrame()
        {
            if (!m_cameraAccess.IsPlaying) return;

            bool cpActive = m_knobs != null;

            // N gate (P2): skip if too many frames already in-flight (control plane only)
            if (cpActive)
            {
                int pending = m_telemetry.GetPendingCount();
                if (pending >= m_knobs.CurrentProfile.InflightCap)
                {
                    m_nGateDrops++;
                    m_metrics.RecordDropped();
                    Debug.Log($"[V3 DETECTION] N gate: pending={pending} >= cap={m_knobs.CurrentProfile.InflightCap}, " +
                              $"total_drops={m_nGateDrops}");
                    return;
                }
            }

            try
            {
                // 1. Capture camera frame as Texture2D
                Texture2D frame = GetCameraTexture2D();
                if (frame == null)
                {
                    Debug.LogWarning("[V3 DETECTION] Failed to capture camera frame");
                    return;
                }

                // 2. Resize — profile ResFactor (control plane) or InferenceConfig downsampleFactor (fallback)
                Texture2D toEncode = frame;
                bool didResize = false;
                if (cpActive && m_knobs.CurrentProfile.ResFactor < 0.99f)
                {
                    toEncode  = ResizeTexture(frame, m_knobs.CurrentProfile.ResFactor);
                    didResize = true;
                }
                else if (!cpActive && m_inferenceConfig.downsampleFactor > 1)
                {
                    float factor = 1f / m_inferenceConfig.downsampleFactor;
                    toEncode  = ResizeTexture(frame, factor);
                    didResize = true;
                }

                // 3. JPEG encode — profile quality (control plane) or InferenceConfig (fallback)
                int jpegQuality = cpActive ? m_knobs.CurrentProfile.JpegQuality : m_inferenceConfig.jpegQuality;
                byte[] jpegData = toEncode.EncodeToJPG(jpegQuality);
                int rawBytes    = toEncode.width * toEncode.height * 3;  // capture before Destroy

                StartCoroutine(DestroyNextFrame(frame));
                if (didResize) StartCoroutine(DestroyNextFrame(toEncode));

                // 4. Create frame trace (with profile metadata if control plane active)
                m_frameId++;
                OperatingProfile profile = cpActive ? m_knobs.CurrentProfile : null;
                FrameTrace trace = m_telemetry.CreateFrame(m_frameId, jpegData.Length, profile, policyId: "");
                trace.upload_bytes_uncompressed = rawBytes;

                // 5. Send via UDP (non-blocking fire-and-forget)
                string profileId = cpActive ? m_knobs.CurrentProfile.Id : "off";
                string telemetryJson = $"{{\"mode\":\"detection\",\"scene\":\"MultiObjectDetection\",\"profile\":\"{profileId}\"}}";
                m_transport.SendFrame(trace, jpegData, telemetryJson);

                if (cpActive)
                {
                    m_metrics.RecordSent();
                    m_metrics.PushBytes(jpegData.Length);
                }

                Debug.Log($"[V3 DETECTION] Sent frame {trace.frame_id} " +
                          $"({toEncode.width}x{toEncode.height}, q={jpegQuality}, " +
                          $"size={jpegData.Length / 1024}KB)");
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[V3 DETECTION] Error sending frame {m_frameId}: {e.Message}");
            }
        }

        // ============================================================================
        // V3.0: RESPONSE HANDLING
        // ============================================================================

        private void HandleV3Response(FrameResponse response)
        {
            Debug.Log($"[V3 DETECTION] Received response for frame {response.frame_id}, " +
                      $"server_proc={response.processing_time_ms:F1}ms, " +
                      $"queue_wait={response.queue_wait_ms:F1}ms");

            m_telemetry.MarkFrameCompleted(response.frame_id, response);
            m_metrics?.PushLatency(response.latency_ms);

            DisplayV3Frame(response);
            UpdateMetricsDisplay(response);
            m_telemetry.MarkFrameDisplayed(response.frame_id);
        }

        private void DisplayV3Frame(FrameResponse response)
        {
            var cachedCameraPose = m_cameraAccess.GetCameraPose();

            if (!m_cameraAccess.IsPlaying || m_detectionManager.m_spatialAnchor == null || !m_detectionManager.m_spatialAnchor.IsTracked)
            {
                m_detections.Clear();
                return;
            }

            if (!response.HasDetections())
            {
                m_detections.Clear();
                Debug.LogWarning($"[V3 DETECTION] Frame {response.frame_id} has no detection data");
                return;
            }

            m_detections.Clear();

            // Scale bbox from camera resolution -> Sentis model input resolution
            float scaleX = m_inputSize.x / (float)response.input_width;
            float scaleY = m_inputSize.y / (float)response.input_height;

            foreach (var det in response.detections)
            {
                Vector4 bboxUnity = new Vector4(
                    det.bbox_pixels[0] * scaleX,
                    det.bbox_pixels[1] * scaleY,
                    det.bbox_pixels[2] * scaleX,
                    det.bbox_pixels[3] * scaleY
                );
                m_detections.Add((det.class_id, bboxUnity));
            }

            Debug.Log($"[V3 DETECTION] Frame {response.frame_id}: Converted {m_detections.Count} detections");
            m_uiInference.DrawUIBoxes(m_detections, m_inputSize, cachedCameraPose);
        }

        private void UpdateMetricsDisplay(FrameResponse response)
        {
            float avgConfidence = 0f;
            if (response.detections != null && response.detections.Length > 0)
            {
                float sum = 0f;
                foreach (var det in response.detections) sum += det.confidence;
                avgConfidence = sum / response.detections.Length;
            }

            if (m_sharedHUD != null)
                m_sharedHUD.UpdateMetrics(response);

            if (m_uiMenuManager != null || m_inferenceHUD != null)
            {
                float e2eMs        = response.latency_ms;
                float serverE2eMs  = response.server_e2e_ms;
                float parseMs      = response.parse_ms;
                float networkMs    = e2eMs - serverE2eMs - parseMs;
                float serverProcMs = response.processing_time_ms;

                m_uiMenuManager?.UpdateMetrics(
                    e2eMs, networkMs / 2, serverProcMs, networkMs / 2, parseMs,
                    0, 0, avgConfidence);

                if (m_inferenceHUD != null)
                {
                    m_inferenceHUD.UpdateHUD(
                        e2eMs, networkMs / 2, serverProcMs, networkMs / 2, parseMs,
                        0, 0, 0,
                        response.detections?.Length ?? 0, avgConfidence);
                }
            }
        }

        // ============================================================================
        // TEXTURE HELPERS
        // ============================================================================

        private Texture2D GetCameraTexture2D()
        {
            Texture texture = m_cameraAccess.GetTexture();
            if (texture == null) return null;

            if (texture is Texture2D tex2D) return tex2D;

            if (texture is RenderTexture rt)
            {
                Texture2D result = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
                RenderTexture.active = rt;
                result.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                result.Apply();
                RenderTexture.active = null;
                return result;
            }

            Debug.LogError("[V3 DETECTION] Unsupported texture type");
            return null;
        }

        private static Texture2D ResizeTexture(Texture2D src, float resFactor)
        {
            int w = Mathf.Max(1, Mathf.RoundToInt(src.width  * resFactor));
            int h = Mathf.Max(1, Mathf.RoundToInt(src.height * resFactor));

            RenderTexture rt   = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32);
            RenderTexture prev = RenderTexture.active;
            Graphics.Blit(src, rt);
            RenderTexture.active = rt;

            Texture2D result = new Texture2D(w, h, TextureFormat.RGB24, false);
            result.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            result.Apply();

            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
            return result;
        }

        private IEnumerator DestroyNextFrame(UnityEngine.Object obj)
        {
            yield return null;
            if (obj != null) Destroy(obj);
        }

        // ============================================================================
        // LOCAL SENTIS INFERENCE (ORIGINAL CODE - PRESERVED for non-server mode)
        // ============================================================================

        private IEnumerator RunInference()
        {
            if (!m_cameraAccess.IsPlaying)
                yield break;

            [DllImport("OVRPlugin", CallingConvention = CallingConvention.Cdecl)]
            static extern OVRPlugin.Result ovrp_GetNodePoseStateAtTime(double time, OVRPlugin.Node nodeId, out OVRPlugin.PoseStatef nodePoseState);
            if (!ovrp_GetNodePoseStateAtTime(OVRPlugin.GetTimeInSeconds(), OVRPlugin.Node.Head, out _).IsSuccess())
            {
                Debug.Log("ovrp_GetNodePoseStateAtTime failed, skipping.");
                yield break;
            }

            var cachedCameraPose = m_cameraAccess.GetCameraPose();
            Texture targetTexture = m_cameraAccess.GetTexture();

            var textureTransform = new TextureTransform().SetDimensions(targetTexture.width, targetTexture.height, 3);
            using var input = new Tensor<float>(new TensorShape(1, 3, m_inputSize.x, m_inputSize.y));
            TextureConverter.ToTensor(targetTexture, input, textureTransform);
            m_engine.Schedule(input);

            var boxesAwaiter = (m_engine.PeekOutput(0) as Tensor<float>).ReadbackAndCloneAsync().GetAwaiter();
            while (!boxesAwaiter.IsCompleted) yield return null;
            using var boxes = boxesAwaiter.GetResult();
            if (boxes.shape[0] == 0) yield break;

            var classIDsAwaiter = (m_engine.PeekOutput(1) as Tensor<int>).ReadbackAndCloneAsync().GetAwaiter();
            while (!classIDsAwaiter.IsCompleted) yield return null;
            using var classIDs = classIDsAwaiter.GetResult();
            if (classIDs.shape[0] == 0) { Debug.LogError("classIDs.shape[0] == 0"); yield break; }

            var scoresAwaiter = (m_engine.PeekOutput(2) as Tensor<float>).ReadbackAndCloneAsync().GetAwaiter();
            while (!scoresAwaiter.IsCompleted) yield return null;
            using var scores = scoresAwaiter.GetResult();
            if (scores.shape[0] == 0) { Debug.LogError("scores.shape[0] == 0"); yield break; }

            NonMaxSuppression(m_detections, boxes, classIDs, scores, m_iouThreshold, m_scoreThreshold);

            if (!m_cameraAccess.IsPlaying || m_detectionManager.m_spatialAnchor == null || !m_detectionManager.m_spatialAnchor.IsTracked)
                yield break;

            m_uiInference.DrawUIBoxes(m_detections, m_inputSize, cachedCameraPose);
        }

        private static void NonMaxSuppression(List<(int classId, Vector4 boundingBox)> outDetections, Tensor<float> boxes, Tensor<int> classIDs, Tensor<float> scores, float iouThreshold, float scoreThreshold)
        {
            outDetections.Clear();

            List<int> filteredIndices = new List<int>();
            NativeArray<float>.ReadOnly scoresArray = scores.AsReadOnlyNativeArray();
            for (int i = 0; i < scoresArray.Length; i++)
            {
                if (scoresArray[i] >= scoreThreshold)
                    filteredIndices.Add(i);
            }

            if (filteredIndices.Count == 0) return;

            filteredIndices.Sort((a, b) => scoresArray[b].CompareTo(scoresArray[a]));

            bool[] suppressed = new bool[filteredIndices.Count];
            for (int i = 0; i < filteredIndices.Count; i++)
            {
                if (suppressed[i]) continue;

                int idx = filteredIndices[i];
                outDetections.Add((classIDs[idx], GetBox(idx)));

                for (int j = i + 1; j < filteredIndices.Count; j++)
                {
                    if (suppressed[j]) continue;
                    if (CalculateIoU(GetBox(idx), GetBox(filteredIndices[j])) > iouThreshold)
                        suppressed[j] = true;
                }
            }

            Vector4 GetBox(int i) => new Vector4(boxes[i, 0], boxes[i, 1], boxes[i, 2], boxes[i, 3]);
        }

        internal static float CalculateIoU(Vector4 boxA, Vector4 boxB)
        {
            float x1 = Mathf.Max(boxA.x, boxB.x);
            float y1 = Mathf.Max(boxA.y, boxB.y);
            float x2 = Mathf.Min(boxA.z, boxB.z);
            float y2 = Mathf.Min(boxA.w, boxB.w);

            float intersectionArea = Mathf.Max(0, x2 - x1) * Mathf.Max(0, y2 - y1);

            float boxAArea = (boxA.z - boxA.x) * (boxA.w - boxA.y);
            float boxBArea = (boxB.z - boxB.x) * (boxB.w - boxB.y);
            float unionArea = boxAArea + boxBArea - intersectionArea;

            return unionArea == 0 ? 0 : intersectionArea / unionArea;
        }

        // ============================================================================
        // SERVER CONNECTION TEST
        // ============================================================================

        private IEnumerator TestServerConnection()
        {
            string testUrl = ServerConfig.Instance.BaseUrl;
            Debug.Log($"[V3 DETECTION] Connecting to {testUrl}");

            using (UnityWebRequest req = UnityWebRequest.Get(testUrl))
            {
                req.timeout = 5;
                yield return req.SendWebRequest();

                if (req.result == UnityWebRequest.Result.Success)
                    Debug.Log($"[V3 DETECTION] Connection OK! Response: {req.downloadHandler.text}");
                else
                    Debug.LogError($"[V3 DETECTION] Connection FAILED: {req.error} (code={req.responseCode})");
            }
        }

#if UNITY_EDITOR
        private void DebugProfileSwitch()
        {
            if (m_knobs == null) return;
            if (Input.GetKeyDown(KeyCode.Alpha1)) m_knobs.Apply(OperatingProfile.Get("P1"));
            if (Input.GetKeyDown(KeyCode.Alpha2)) m_knobs.Apply(OperatingProfile.Get("P2"));
            if (Input.GetKeyDown(KeyCode.Alpha3)) m_knobs.Apply(OperatingProfile.Get("P3"));
            if (Input.GetKeyDown(KeyCode.Alpha4)) m_knobs.Apply(OperatingProfile.Get("P4"));
            if (Input.GetKeyDown(KeyCode.Alpha5)) m_knobs.Apply(OperatingProfile.Get("P5"));
        }
#endif
    }
}
