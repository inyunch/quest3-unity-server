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

namespace PassthroughCameraSamples.MultiObjectDetection
{
    [MetaCodeSample("PassthroughCameraApiSamples-MultiObjectDetection")]
    public class SentisInferenceRunManager : MonoBehaviour
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

        [Header("Server Inference (NEW)")]
        [SerializeField] private bool m_useServerInference = false;
        [SerializeField] private InferenceConfig m_inferenceConfig = new InferenceConfig
        {
            mode = InferenceMode.ObjectDetection,
            targetFPS = 5f,  // Reduced from 10 to 5 for better completion rate
            jpegQuality = 80,
            includeMask = false,
            includeDepth = false
        };

        [Header("Legacy Server Inference (DEPRECATED - use m_inferenceConfig instead)")]
        [SerializeField] private string m_serverUrl = "";  // Left for backward compatibility, use m_inferenceConfig.BuildUrl() instead
        [SerializeField, Range(60, 100)] private int m_jpegQuality = 80;  // DEPRECATED: use m_inferenceConfig.jpegQuality

        [Header("[Editor Only] Convert to Sentis")]
        public ModelAsset OnnxModel;
        [Space(40)]

        private Worker m_engine;
        private Vector2Int m_inputSize;
        private readonly List<(int classId, Vector4 boundingBox)> m_detections = new List<(int classId, Vector4 boundingBox)>();
        private int m_frameId = 0;
        private string m_sessionId;  // GUID to uniquely identify this recording session

        // Store timing data from previous frame to send as headers in next request
        private float m_lastE2eMs = 0f;
        private float m_lastUploadMs = 0f;
        private float m_lastDownloadMs = 0f;
        private float m_lastParseMs = 0f;
        private int m_lastUploadBytesUncompressed = 0;
        private int m_lastUploadBytes = 0;
        private int m_lastDownloadBytes = 0;
        private int m_lastDownloadBytesCompressed = 0;

        // FPS throttling (Part C)
        private float m_lastInferenceTime = 0f;
        private bool m_inferenceInProgress = false;

        // Frame statistics (Part D)
        private int m_totalFrames = 0;
        private int m_droppedFrames = 0;
        private int m_frozenFrames = 0;

        // PHASE 1: Frame trace tracking (parallel processing preparation)
        private Dictionary<int, FrameTrace> m_frameTraces = new Dictionary<int, FrameTrace>();
        private object m_frameTracesLock = new object();

        // PHASE 2: Parallel request tracking
        private Dictionary<int, UnityWebRequest> m_pendingRequests = new Dictionary<int, UnityWebRequest>();

        // PHASE 3: Display control
        private int m_lastDisplayedFrameId = -1;

        // Delayed telemetry: Store last completed frame's final state to send in next request
        private FrameTrace m_lastCompletedTrace = null;  // DEPRECATED - will be removed after migration
        private Queue<FrameTrace> m_completedFramesQueue = new Queue<FrameTrace>();  // PRIORITY 1: Queue for all completed frames

        // PRIORITY 3: Freeze frame calculation
        private int m_framesSinceLastDisplay = 0;  // Counts Unity Update() calls between displayed frames

        // PHASE 6: Frame cleanup and optimization
        private const int MAX_FRAME_TRACES = 100;
        private const float FRAME_TIMEOUT_SECONDS = 5.0f;
        private float m_lastMetricsLogTime = 0f;
        private const float METRICS_LOG_INTERVAL = 10.0f;

        // UDP Transport (Phase 1 - Non-Blocking)
        private System.Net.Sockets.UdpClient m_udpClient;
        private const int UDP_PORT = 8002;
        [SerializeField] private bool m_useUDPTransport = true;  // Feature flag for safe rollout

        // Phase 3: Fixed cadence non-blocking send
        private float m_nextInferenceTime = 0f;  // Next time to send inference request

        // LOCAL TELEMETRY: Direct CSV logging (bypasses N+1 delayed pattern for complete tracking)
        private LocalTelemetryWriter m_localTelemetry;
        [SerializeField] private bool m_enableLocalTelemetry = true;  // Feature flag
        private bool m_cameraReady = false;  // Camera initialization complete

        // NEW: Improved Freeze/Drop Metrics Tracking
        private float m_unityFrameTimeMs = 15.4f;  // Measured Unity frame time (default 65 FPS estimate)
        private int m_sessionFrameIndex = 0;       // Sequential index for logged frames
        private int m_cumulativeFreezeFrames = 0;  // Running total of freeze frames
        private int m_cumulativeDropped = 0;       // Running total of dropped frames
        private int m_cumulativeDisplayed = 0;     // Running total of displayed frames
        private int m_lastLoggedFrameId = -1;      // Last frame_id that was logged
        private int m_lastDisplayedFrameCount = -1; // Last Unity Time.frameCount when frame was displayed

        private void Awake()
        {
            var model = ModelLoader.Load(m_sentisModel);
            var inputShape = model.inputs[0].shape;
            m_inputSize = new Vector2Int(inputShape.Get(2), inputShape.Get(3));
            m_engine = new Worker(model, m_backend);
        }

        private IEnumerator Start()
        {
            // PHASE 1: Generate unique session ID for this recording session
            m_sessionId = System.Guid.NewGuid().ToString();
            Debug.Log($"[SESSION] Started session: {m_sessionId}");

            // Initialize local telemetry writer if enabled
            if (m_enableLocalTelemetry)
            {
                m_localTelemetry = new LocalTelemetryWriter();
                if (m_localTelemetry.Initialize(m_sessionId))
                {
                    Debug.Log($"[LOCAL TELEMETRY] Initialized: {m_localTelemetry.GetFilePath()}");
                }
                else
                {
                    Debug.LogWarning("[LOCAL TELEMETRY] Failed to initialize, telemetry will be limited");
                    m_localTelemetry = null;
                }
            }

            // Initialize UDP client if using UDP transport
            if (m_useServerInference && m_useUDPTransport)
            {
                m_udpClient = new System.Net.Sockets.UdpClient();
                Debug.Log($"[UDP] Initialized UDP client for port {UDP_PORT}");
            }

            m_uiInference.SetLabels(m_labelsAsset);

            // Validate and log inference configuration
            if (m_useServerInference)
            {
                // Migrate from legacy settings if needed
                if (!string.IsNullOrEmpty(m_serverUrl) && m_inferenceConfig.baseUrl == "http://192.168.0.135:8001/infer_human")
                {
                    Debug.LogWarning("[DETECTION] m_serverUrl is deprecated. Please use m_inferenceConfig instead.");
                }

                // Use legacy jpegQuality if it differs from config default
                if (m_jpegQuality != 80 && m_inferenceConfig.jpegQuality == 80)
                {
                    Debug.LogWarning($"[DETECTION] Migrating legacy m_jpegQuality ({m_jpegQuality}) to m_inferenceConfig");
                    m_inferenceConfig.jpegQuality = m_jpegQuality;
                }

                m_inferenceConfig.Validate();
                m_inferenceConfig.LogSummary();

                // Initialize SharedInferenceHUD if available
                if (m_sharedHUD != null)
                {
                    m_sharedHUD.SetMode(m_inferenceConfig.mode, m_inferenceConfig.targetFPS);
                }

                // Test server connection at startup
                Debug.Log("[SERVER TEST] Testing connection to server...");
                yield return TestServerConnection();
            }

            // NEW: Measure actual Unity FPS on startup
            StartCoroutine(MeasureUnityFPS());

            // PHASE 3: Set camera ready flag and initialize timing
            m_cameraReady = true;
            m_nextInferenceTime = Time.time;

            // PHASE 3: Removed while(true) loop - now driven by Update() at fixed cadence
            Debug.Log("[PHASE 3 SENTIS] Start() complete - inference now driven by Update() at fixed cadence");
        }

        private void OnDestroy()
        {
            m_engine.PeekOutput(0)?.CompleteAllPendingOperations();
            m_engine.PeekOutput(1)?.CompleteAllPendingOperations();
            m_engine.PeekOutput(2)?.CompleteAllPendingOperations();
            m_engine.Dispose();

            // Close local telemetry writer
            if (m_localTelemetry != null)
            {
                m_localTelemetry.Close();
                Debug.Log($"[LOCAL TELEMETRY] Session ended, {m_localTelemetry.GetRowCount()} rows written");
            }

            // Note: Excel logging is handled server-side via N+1 delayed telemetry
            // No need to export CSV here
        }

        /// <summary>
        /// Measure actual Unity FPS over 2 seconds to calculate frame time.
        /// This is used for freeze_duration_ms calculation.
        /// </summary>
        private IEnumerator MeasureUnityFPS()
        {
            // Measure over 120 frames
            int startFrame = Time.frameCount;
            float startTime = Time.realtimeSinceStartup;

            yield return new WaitForSeconds(2.0f);

            int endFrame = Time.frameCount;
            float endTime = Time.realtimeSinceStartup;

            float frameCount = endFrame - startFrame;
            float duration = endTime - startTime;
            float fps = frameCount / duration;

            m_unityFrameTimeMs = 1000.0f / fps;

            Debug.Log($"[UNITY FPS] Measured {fps:F1} FPS over {duration:F1}s, frame time = {m_unityFrameTimeMs:F2}ms/frame");
        }

        internal static void PreloadModel(ModelAsset modelAsset)
        {
            // Load model
            var model = ModelLoader.Load(modelAsset);
            var inputShape = model.inputs[0].shape;

            // Create engine to run model
            using var worker = new Worker(model, BackendType.CPU);

            // Run inference with an empty image to load the model in the memory. The first inference blocks the main thread for a long time, so we're doing it on the app launch
            Texture tempTexture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            var textureTransform = new TextureTransform().SetDimensions(tempTexture.width, tempTexture.height, 3);
            using var input = new Tensor<float>(new TensorShape(1, 3, inputShape.Get(2), inputShape.Get(3)));
            TextureConverter.ToTensor(tempTexture, input, textureTransform);
            worker.Schedule(input);

            // Complete the inference immediately and destroy the temporary texture
            worker.PeekOutput(0).CompleteAllPendingOperations();
            worker.PeekOutput(1).CompleteAllPendingOperations();
            worker.PeekOutput(2).CompleteAllPendingOperations();
            Destroy(tempTexture);
        }

        private IEnumerator RunInference()
        {
            if (!m_cameraAccess.IsPlaying)
            {
                yield break;
            }

            // ============================================================================
            // FPS THROTTLING (Part C)
            // ============================================================================
            if (m_useServerInference)
            {
                float currentTime = Time.time;
                float targetInterval = m_inferenceConfig.GetInferenceInterval();
                float timeSinceLastInference = currentTime - m_lastInferenceTime;

                // Check if we should drop this frame (too soon since last inference)
                if (timeSinceLastInference < targetInterval)
                {
                    // Drop frame - respecting target FPS
                    // NOTE: m_droppedFrames++ is now handled in SharedInferenceHUD.ReportDroppedFrame()
                    if (m_sharedHUD != null)
                    {
                        m_sharedHUD.ReportDroppedFrame();
                    }
                    //Debug.Log($"[FPS THROTTLE] Dropped frame (interval={timeSinceLastInference * 1000f:F0}ms < target={targetInterval * 1000f:F0}ms)");
                    yield break;
                }

                // Check if previous inference is still in progress (freeze frame)
                if (m_inferenceInProgress)
                {
                    // NOTE: m_frozenFrames++ is now handled in SharedInferenceHUD.ReportFrozenFrame()
                    if (m_sharedHUD != null)
                    {
                        m_sharedHUD.ReportFrozenFrame();
                    }
                    //Debug.Log($"[FREEZE FRAME] Previous inference still in progress, keeping old visualization");
                    yield break;
                }

                // Mark inference as in progress
                m_inferenceInProgress = true;
                m_lastInferenceTime = currentTime;
            }

            [DllImport("OVRPlugin", CallingConvention = CallingConvention.Cdecl)]
            static extern OVRPlugin.Result ovrp_GetNodePoseStateAtTime(double time, OVRPlugin.Node nodeId, out OVRPlugin.PoseStatef nodePoseState);
            if (!ovrp_GetNodePoseStateAtTime(OVRPlugin.GetTimeInSeconds(), OVRPlugin.Node.Head, out _).IsSuccess())
            {
                Debug.Log("ovrp_GetNodePoseStateAtTime failed, which means 'm_cameraAccess.GetCameraPose()' is not reliable, skipping.");
                m_inferenceInProgress = false;
                yield break;
            }

            var cachedCameraPose = m_cameraAccess.GetCameraPose();

            // Update Capture data
            Texture targetTexture = m_cameraAccess.GetTexture();

            // ============================================================================
            // INFERENCE: Choose between Server or Local (Sentis)
            // ============================================================================

            if (m_useServerInference)
            {
                if (m_useUDPTransport)
                {
                    // NEW: UDP NON-BLOCKING PATH
                    Debug.Log("[DETECTION UDP] Using UDP transport");

                    // Display completed frames BEFORE starting new inference
                    TryDisplayNewestFrame();

                    // 1. Encode JPEG
                    byte[] jpegData = EncodeTextureToJPEG(targetTexture);
                    if (jpegData == null)
                    {
                        Debug.LogError("[UDP] Failed to encode texture to JPEG");
                        yield break;
                    }

                    // 2. Create frame trace with hash
                    m_frameId++;
                    FrameTrace trace = new FrameTrace(m_frameId);
                    trace.session_id = m_sessionId;
                    trace.payload_hash = UDPTransport.ComputeSHA256Base64(jpegData);
                    trace.upload_bytes_compressed = jpegData.Length;
                    trace.upload_bytes_uncompressed = targetTexture.width * targetTexture.height * 3;  // RGB24

                    // Store trace
                    lock (m_frameTracesLock)
                    {
                        m_frameTraces[trace.frame_id] = trace;
                    }

                    Debug.Log($"[UDP] Frame {trace.frame_id} created, hash={trace.payload_hash.Substring(0, 8)}...");

                    // 3. Send UDP (returns immediately - no blocking!)
                    SendFrameUDP(trace, jpegData);

                    // 4. Start async response listener (runs in background)
                    StartCoroutine(ListenForResponseHTTP(trace.frame_id));

                    Debug.Log($"[UDP] Frame {trace.frame_id} sent, listener started");
                }
                else
                {
                    // OLD: HTTP BLOCKING PATH (fallback for safety)
                    Debug.Log("[INFERENCE] Using SERVER inference with HTTP transport (blocking)");
                    yield return RunServerInference(targetTexture);
                    // m_detections is now populated by RunServerInference
                }
            }
            else
            {
                // LOCAL SENTIS INFERENCE PATH (ORIGINAL)
                Debug.Log("[INFERENCE] Using LOCAL Sentis inference");

                // Convert the texture to a Tensor and schedule the inference
                var textureTransform = new TextureTransform().SetDimensions(targetTexture.width, targetTexture.height, 3);
                using var input = new Tensor<float>(new TensorShape(1, 3, m_inputSize.x, m_inputSize.y));
                TextureConverter.ToTensor(targetTexture, input, textureTransform);

                // Schedule all model layers
                m_engine.Schedule(input);

                // Get the results. ReadbackAndCloneAsync waits for all layers to complete before returning the result
                var boxesAwaiter = (m_engine.PeekOutput(0) as Tensor<float>).ReadbackAndCloneAsync().GetAwaiter();
                while (!boxesAwaiter.IsCompleted)
                {
                    yield return null;
                }
                using var boxes = boxesAwaiter.GetResult();
                if (boxes.shape[0] == 0)
                {
                    yield break;
                }

                var classIDsAwaiter = (m_engine.PeekOutput(1) as Tensor<int>).ReadbackAndCloneAsync().GetAwaiter();
                while (!classIDsAwaiter.IsCompleted)
                {
                    yield return null;
                }
                using var classIDs = classIDsAwaiter.GetResult();
                if (classIDs.shape[0] == 0)
                {
                    Debug.LogError("classIDs.shape[0] == 0");
                    yield break;
                }

                var scoresAwaiter = (m_engine.PeekOutput(2) as Tensor<float>).ReadbackAndCloneAsync().GetAwaiter();
                while (!scoresAwaiter.IsCompleted)
                {
                    yield return null;
                }
                using var scores = scoresAwaiter.GetResult();
                if (scores.shape[0] == 0)
                {
                    Debug.LogError("scores.shape[0] == 0");
                    yield break;
                }

                NonMaxSuppression(m_detections, boxes, classIDs, scores, m_iouThreshold, m_scoreThreshold);
            }

            // Mark inference as complete
            if (m_useServerInference)
            {
                m_inferenceInProgress = false;
                // NOTE: m_totalFrames++ is now handled in SharedInferenceHUD.UpdateMetrics()
            }

            // Checking if spatial anchor is tracked ensures bounding boxes are placed at correct world space positions.
            if (!m_cameraAccess.IsPlaying || m_detectionManager.m_spatialAnchor == null || !m_detectionManager.m_spatialAnchor.IsTracked)
            {
                yield break;
            }

            // Update UI.
            m_uiInference.DrawUIBoxes(m_detections, m_inputSize, cachedCameraPose);
        }

        private static void NonMaxSuppression(List<(int classId, Vector4 boundingBox)> outDetections, Tensor<float> boxes, Tensor<int> classIDs, Tensor<float> scores, float iouThreshold, float scoreThreshold)
        {
            outDetections.Clear();

            // Filter by score threshold first
            List<int> filteredIndices = new List<int>();
            NativeArray<float>.ReadOnly scoresArray = scores.AsReadOnlyNativeArray();
            for (int i = 0; i < scoresArray.Length; i++)
            {
                if (scoresArray[i] >= scoreThreshold)
                {
                    filteredIndices.Add(i);
                }
            }

            if (filteredIndices.Count == 0)
            {
                return;
            }

            // Sort filtered indices by scores in descending order
            filteredIndices.Sort((a, b) => scoresArray[b].CompareTo(scoresArray[a]));

            // Apply NMS algorithm
            bool[] suppressed = new bool[filteredIndices.Count];
            for (int i = 0; i < filteredIndices.Count; i++)
            {
                if (suppressed[i])
                    continue;

                int idx = filteredIndices[i];

                // Add this detection to results
                outDetections.Add((classIDs[idx], GetBox(idx)));

                // Suppress overlapping boxes regardless of class
                for (int j = i + 1; j < filteredIndices.Count; j++)
                {
                    if (suppressed[j])
                        continue;

                    int jdx = filteredIndices[j];

                    float iou = CalculateIoU(GetBox(idx), GetBox(jdx));
                    if (iou > iouThreshold)
                    {
                        suppressed[j] = true;
                    }
                }
            }

            Vector4 GetBox(int i) => new Vector4(boxes[i, 0], boxes[i, 1], boxes[i, 2], boxes[i, 3]);
        }

        internal static float CalculateIoU(Vector4 boxA, Vector4 boxB)
        {
            // Boxes are in format (topLeftX, topLeftY, bottomRightX, bottomRightY)
            // Calculate intersection coordinates
            float x1 = Mathf.Max(boxA.x, boxB.x);
            float y1 = Mathf.Max(boxA.y, boxB.y);
            float x2 = Mathf.Min(boxA.z, boxB.z);
            float y2 = Mathf.Min(boxA.w, boxB.w);

            // Calculate intersection area
            float intersectionWidth = Mathf.Max(0, x2 - x1);
            float intersectionHeight = Mathf.Max(0, y2 - y1);
            float intersectionArea = intersectionWidth * intersectionHeight;

            // Calculate individual box areas
            float boxAArea = (boxA.z - boxA.x) * (boxA.w - boxA.y);
            float boxBArea = (boxB.z - boxB.x) * (boxB.w - boxB.y);

            // Calculate union area
            float unionArea = boxAArea + boxBArea - intersectionArea;

            // Return IoU (Intersection over Union)
            if (unionArea == 0)
                return 0;

            return intersectionArea / unionArea;
        }

        // ============================================================================
        // SERVER INFERENCE - JSON Response Classes
        // ============================================================================

        [System.Serializable]
        private class ServerResponse
        {
            // OLD format - keep compatible with old renderer
            public DetectionResultData detections;
            public int model_input_width;
            public int model_input_height;
            public int input_image_width;
            public int input_image_height;
            public float processing_time_ms;
            public float server_queue_ms;
            public float server_postprocess_ms;
            public double t_server_recv;
            public double server_process_start_ts;  // NEW: For queue_wait_ms calculation
            public double t_server_send;
        }

        [System.Serializable]
        private class DetectionResultData
        {
            public DetectionData[] detections;
            public int num_detections;
        }

        [System.Serializable]
        private class DetectionData
        {
            public int class_id;
            public string class_name;
            public float confidence;
            public float[] bbox;           // normalized [0-1]
            public int[] bbox_pixels;      // absolute pixels
        }

        // ============================================================================
        // PHASE 3: NON-BLOCKING INFERENCE (Fixed cadence send, async response)
        // ============================================================================

        /// <summary>
        /// PHASE 3: Non-blocking inference runner called from Update() at fixed intervals.
        /// Simplified version of RunInference() that only handles UDP path.
        /// </summary>
        private IEnumerator RunInferenceNonBlocking()
        {
            // Quick checks
            if (!m_cameraAccess.IsPlaying)
            {
                Debug.Log("[PHASE 3 SENTIS] Camera not playing, skipping inference");
                yield break;
            }

            // Get current frame texture
            Texture targetTexture = m_cameraAccess.GetTexture();

            // 1. Encode JPEG
            byte[] jpegData = EncodeTextureToJPEG(targetTexture);
            if (jpegData == null)
            {
                Debug.LogError("[PHASE 3 SENTIS] Failed to encode texture to JPEG");
                yield break;
            }

            // 2. Create frame trace with hash
            m_frameId++;
            FrameTrace trace = new FrameTrace(m_frameId);
            trace.session_id = m_sessionId;
            trace.payload_hash = UDPTransport.ComputeSHA256Base64(jpegData);
            trace.upload_bytes_compressed = jpegData.Length;
            trace.upload_bytes_uncompressed = targetTexture.width * targetTexture.height * 3;

            // Store trace
            lock (m_frameTracesLock)
            {
                m_frameTraces[trace.frame_id] = trace;
            }

            Debug.Log($"[PHASE 3 SENTIS] Frame {trace.frame_id} created, size={jpegData.Length} bytes");

            // 3. Send UDP (returns immediately - no blocking!)
            SendFrameUDP(trace, jpegData);

            // 4. Start async response listener (runs in background)
            StartCoroutine(ListenForResponseHTTP(trace.frame_id));

            Debug.Log($"[PHASE 3 SENTIS] Frame {trace.frame_id} sent via UDP");

            // PHASE 3: NO yield return - method completes immediately
            // Response will arrive asynchronously via ListenForResponseHTTP()
        }

        // ============================================================================
        // SERVER INFERENCE - Connection Test
        // ============================================================================

        private IEnumerator TestServerConnection()
        {
            string testUrl = "http://192.168.0.135:8001/";
            Debug.Log($"[SERVER TEST] Connecting to {testUrl}");

            using (UnityWebRequest req = UnityWebRequest.Get(testUrl))
            {
                req.timeout = 5;
                yield return req.SendWebRequest();

                if (req.result == UnityWebRequest.Result.Success)
                {
                    Debug.Log($"[SERVER TEST] ? Connection OK! Response: {req.downloadHandler.text}");
                }
                else
                {
                    Debug.LogError($"[SERVER TEST] ? Connection FAILED: {req.error}");
                    Debug.LogError($"[SERVER TEST] Result: {req.result}");
                    Debug.LogError($"[SERVER TEST] Response Code: {req.responseCode}");
                }
            }
        }

        // ============================================================================
        // SERVER INFERENCE - HTTP Request Method
        // ============================================================================

        private IEnumerator RunServerInference(Texture texture)
        {
            // Increment frame counter and start E2E timing
            m_frameId++;
            float e2eStartTime = Time.realtimeSinceStartup;

            // PHASE 1: Create frame trace for this request
            FrameTrace trace = new FrameTrace(m_frameId);
            trace.session_id = m_sessionId;  // Set session ID for global uniqueness
            // Note: unity_send_ts is set by constructor using TimestampUtil.GetUnixTimestampMs()
            lock (m_frameTracesLock)
            {
                m_frameTraces[m_frameId] = trace;
            }

            Debug.Log($"[FRAME TRACE] Created trace for frame {m_frameId}: {trace}");

            // 1. Convert texture to Texture2D if needed
            Texture2D tex2D = texture as Texture2D;
            if (tex2D == null)
            {
                // Handle RenderTexture case
                RenderTexture rt = texture as RenderTexture;
                if (rt != null)
                {
                    tex2D = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
                    RenderTexture.active = rt;
                    tex2D.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                    tex2D.Apply();
                    RenderTexture.active = null;
                }
                else
                {
                    Debug.LogError("Unsupported texture type for server inference");
                    yield break;
                }
            }

            // 2. Downsample texture if configured (reduces upload size significantly)
            Texture2D textureToEncode = tex2D;
            int originalWidth = tex2D.width;
            int originalHeight = tex2D.height;
            int downsampleFactor = m_inferenceConfig.downsampleFactor;

            if (downsampleFactor > 1)
            {
                int downsampledWidth = tex2D.width / downsampleFactor;
                int downsampledHeight = tex2D.height / downsampleFactor;

                // Create temporary RenderTexture for downsampling
                RenderTexture rt = RenderTexture.GetTemporary(downsampledWidth, downsampledHeight, 0, RenderTextureFormat.ARGB32);
                rt.filterMode = FilterMode.Bilinear;

                // Blit original texture to downsampled RenderTexture
                Graphics.Blit(tex2D, rt);

                // Read back to Texture2D
                RenderTexture previous = RenderTexture.active;
                RenderTexture.active = rt;
                Texture2D downsampledTex = new Texture2D(downsampledWidth, downsampledHeight, TextureFormat.RGB24, false);
                downsampledTex.ReadPixels(new Rect(0, 0, downsampledWidth, downsampledHeight), 0, 0);
                downsampledTex.Apply();
                RenderTexture.active = previous;

                // Release temporary RenderTexture
                RenderTexture.ReleaseTemporary(rt);

                textureToEncode = downsampledTex;
                Debug.Log($"[DETECTION DOWNSAMPLE] {originalWidth}x{originalHeight} ??{downsampledWidth}x{downsampledHeight} (factor={downsampleFactor})");
            }

            // 2.5. Calculate uncompressed size (AFTER downsampling, using actual texture to encode)
            int uploadBytesUncompressed = textureToEncode.width * textureToEncode.height * 3; // RGB24 = 3 bytes per pixel

            // 3. Encode texture as JPEG (use configurable quality from InferenceConfig)
            int jpegQuality = m_inferenceConfig.jpegQuality;
            byte[] jpegBytes = textureToEncode.EncodeToJPG(jpegQuality);
            int uploadBytesCompressed = jpegBytes.Length;
            float compressionRatio = uploadBytesUncompressed > 0 ? (float)uploadBytesUncompressed / uploadBytesCompressed : 1f;
            Debug.Log($"[SERVER] Encoded JPEG (quality={jpegQuality}): {uploadBytesCompressed} bytes ({textureToEncode.width}x{textureToEncode.height}), " +
                     $"uncompressed={uploadBytesUncompressed} bytes, {compressionRatio:F2}x compression");

            // Clean up downsampled texture if created
            if (downsampleFactor > 1 && textureToEncode != tex2D)
            {
                Destroy(textureToEncode);
            }

            // 4. Create multipart form POST
            List<IMultipartFormSection> formData = new List<IMultipartFormSection>();
            formData.Add(new MultipartFormFileSection("image", jpegBytes, "frame.jpg", "image/jpeg"));

            // Use InferenceConfig to build URL (with fallback to legacy m_serverUrl)
            string serverUrl = !string.IsNullOrEmpty(m_serverUrl) ? m_serverUrl : m_inferenceConfig.BuildUrl();

            UnityWebRequest request = UnityWebRequest.Post(serverUrl, formData);

            // Add HTTP headers (including timing data from previous frame)
            request.SetRequestHeader("X-Scene-Name", "MultiObjectDetection");
            request.SetRequestHeader("X-Session-Id", m_sessionId);
            request.SetRequestHeader("X-Frame-Id", m_frameId.ToString());

            // Send timing data from PREVIOUS frame (frame N-1) for Excel logging
            // These values are 0 for the first frame, which is expected
            request.SetRequestHeader("X-E2E-Ms", m_lastE2eMs.ToString("F1"));
            request.SetRequestHeader("X-Upload-Ms", m_lastUploadMs.ToString("F1"));
            request.SetRequestHeader("X-Download-Ms", m_lastDownloadMs.ToString("F1"));
            request.SetRequestHeader("X-Parse-Ms", m_lastParseMs.ToString("F1"));
            request.SetRequestHeader("X-Upload-Bytes-Uncompressed", m_lastUploadBytesUncompressed.ToString());
            request.SetRequestHeader("X-Upload-Bytes", m_lastUploadBytes.ToString());
            request.SetRequestHeader("X-Download-Bytes", m_lastDownloadBytes.ToString());
            request.SetRequestHeader("X-Download-Bytes-Compressed", m_lastDownloadBytesCompressed.ToString());

            // Performance metrics headers
            float freezeRatio = m_totalFrames > 0 ? (float)m_frozenFrames / m_totalFrames : 0f;
            request.SetRequestHeader("X-Target-FPS", m_inferenceConfig.targetFPS.ToString("F1"));
            request.SetRequestHeader("X-Dropped-Frames", m_droppedFrames.ToString());
            request.SetRequestHeader("X-Freeze-Frames", m_frozenFrames.ToString());
            request.SetRequestHeader("X-Freeze-Ratio", freezeRatio.ToString("F4"));

            // PRIORITY 1: Dequeue from queue if available, otherwise fall back to legacy
            FrameTrace traceToSend = null;
            if (m_completedFramesQueue.Count > 0)
            {
                traceToSend = m_completedFramesQueue.Dequeue();
                Debug.Log($"[TELEMETRY QUEUE] Dequeued frame {traceToSend.frame_id} (state={traceToSend.state}) for delayed headers (remaining: {m_completedFramesQueue.Count})");
            }
            else if (m_lastCompletedTrace != null)
            {
                // LEGACY FALLBACK: Use m_lastCompletedTrace if queue is empty
                traceToSend = m_lastCompletedTrace;
                Debug.LogWarning($"[TELEMETRY QUEUE] Queue empty, using legacy m_lastCompletedTrace (frame {traceToSend.frame_id})");
            }

            // DELAYED TELEMETRY: Send previous frame's final state (Frame N-1's complete lifecycle)
            if (traceToSend != null)
            {
                request.SetRequestHeader("X-Prev-Session-Id", traceToSend.session_id);  // PRIORITY 2: Send session ID
                request.SetRequestHeader("X-Prev-Frame-Id", traceToSend.frame_id.ToString());
                request.SetRequestHeader("X-Prev-Unity-Send-Ts", traceToSend.unity_send_ts.ToString());
                request.SetRequestHeader("X-Prev-Unity-Receive-Ts", traceToSend.unity_receive_ts.ToString());
                request.SetRequestHeader("X-Prev-Unity-Display-Ts", traceToSend.unity_display_ts?.ToString() ?? "0");
                request.SetRequestHeader("X-Prev-Unity-Drop-Ts", traceToSend.unity_drop_ts?.ToString() ?? "0");
                request.SetRequestHeader("X-Prev-Server-Receive-Ts", traceToSend.server_receive_ts.ToString());
                request.SetRequestHeader("X-Prev-Server-Process-Start-Ts", traceToSend.server_process_start_ts.ToString());  // NEW: For queue_wait_ms
                request.SetRequestHeader("X-Prev-Server-Send-Ts", traceToSend.server_send_ts.ToString());
                request.SetRequestHeader("X-Prev-Final-State", traceToSend.state.ToString());
                request.SetRequestHeader("X-Prev-Drop-Reason", traceToSend.drop_reason ?? "");
                request.SetRequestHeader("X-Prev-Error-Reason", traceToSend.error_reason ?? "");
                request.SetRequestHeader("X-Prev-Freeze-Frames", traceToSend.freeze_frames.ToString());  // PRIORITY 3: Send per-frame freeze count
            }

            Debug.Log($"[SERVER SEND] >>> Sending frame {m_frameId} to: {serverUrl}");

            // 4. Send request (upload time will be calculated later based on network total and data size ratio)
            Debug.Log($"[LATENCY] requestStart={e2eStartTime:F4}");

            // PHASE 2: Track pending request and upload metadata
            trace.upload_bytes_uncompressed = uploadBytesUncompressed;
            trace.upload_bytes_compressed = uploadBytesCompressed;

            lock (m_frameTracesLock)
            {
                m_pendingRequests[m_frameId] = request;
            }

            Debug.Log($"[PARALLEL] Frame {m_frameId} added to pending requests. Total pending: {m_pendingRequests.Count}");

            // Start the request
            UnityWebRequestAsyncOperation asyncOp = request.SendWebRequest();

            // Wait for response to complete
            yield return asyncOp;

            // PHASE 2: Remove from pending requests
            lock (m_frameTracesLock)
            {
                m_pendingRequests.Remove(m_frameId);
            }

            Debug.Log($"[PARALLEL] Frame {m_frameId} removed from pending. Remaining: {m_pendingRequests.Count}");
            Debug.Log($"[SERVER SEND] <<< Request completed. Result: {request.result}");

            if (request.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError($"[SERVER] Inference failed: {request.error}");
                Debug.LogError($"[SERVER] Result type: {request.result}");
                Debug.LogError($"[SERVER] Response code: {request.responseCode}");
                Debug.LogError($"[SERVER] URL was: {serverUrl}");

                // PHASE 2: Mark trace as failed (no lock release - parallel mode)
                trace.MarkFailed($"{request.result}: {request.error}");

                // PRIORITY 1: Enqueue failed frame for telemetry
                m_completedFramesQueue.Enqueue(trace);
                Debug.Log($"[TELEMETRY QUEUE] Frame {trace.frame_id} FAILED ??queued (queue depth: {m_completedFramesQueue.Count})");

                yield break;
            }

            // 5. Parse JSON response and measure PARSE time
            float parseStartTime = Time.realtimeSinceStartup;

            string jsonResponse = request.downloadHandler.text;
            Debug.Log($"[SERVER] Response: {jsonResponse.Substring(0, Mathf.Min(200, jsonResponse.Length))}...");

            ServerResponse response = JsonUtility.FromJson<ServerResponse>(jsonResponse);

            if (response == null)
            {
                Debug.LogError("[SERVER] Failed to parse JSON response");
                trace.MarkFailed("JSON parse error");
                yield break;
            }

            // Parse time measurement complete
            float parseMs = (Time.realtimeSinceStartup - parseStartTime) * 1000f;

            // Calculate E2E time and server times from response
            float e2eMs = (Time.realtimeSinceStartup - e2eStartTime) * 1000f;
            int downloadBytesCompressed = (int)request.downloadedBytes;
            float serverQueueMs = response.server_queue_ms;
            float serverProcMs = response.processing_time_ms;
            float serverPostprocessMs = response.server_postprocess_ms;
            float serverTotalMs = serverQueueMs + serverProcMs + serverPostprocessMs;

            // Calculate network time (upload + download)
            float networkTotalMs = Mathf.Max(0f, e2eMs - serverTotalMs - parseMs);

            // Allocate network time based on compressed data size ratio
            int totalBytes = uploadBytesCompressed + downloadBytesCompressed;
            float uploadRatio = totalBytes > 0 ? (float)uploadBytesCompressed / totalBytes : 0.5f;
            float downloadRatio = 1.0f - uploadRatio;

            float uploadMs = networkTotalMs * uploadRatio;
            float downloadMs = networkTotalMs * downloadRatio;

            Debug.Log($"[LATENCY] responseReceived={Time.realtimeSinceStartup:F4}");
            Debug.Log($"[LATENCY] e2eMs={e2eMs:F1}ms");
            Debug.Log($"[LATENCY] parseMs={parseMs:F1}ms");
            Debug.Log($"[LATENCY] serverQueueMs={serverQueueMs:F1}ms");
            Debug.Log($"[LATENCY] serverProcMs={serverProcMs:F1}ms");
            Debug.Log($"[LATENCY] serverPostprocessMs={serverPostprocessMs:F1}ms");
            Debug.Log($"[LATENCY] networkTotalMs={networkTotalMs:F1}ms (uploadRatio={uploadRatio:F2}, downloadRatio={downloadRatio:F2})");
            Debug.Log($"[LATENCY] uploadMs={uploadMs:F1}ms (weighted by {uploadBytesCompressed}B/{totalBytes}B)");
            Debug.Log($"[LATENCY] downloadMs={downloadMs:F1}ms (weighted by {downloadBytesCompressed}B/{totalBytes}B)");

            Debug.Log($"[TIMING] E2E={e2eMs:F0}ms (upload={uploadMs:F0}ms queue={serverQueueMs:F0}ms server={serverProcMs:F0}ms post={serverPostprocessMs:F0}ms download={downloadMs:F0}ms parse={parseMs:F0}ms)");

            // Log both compressed and uncompressed sizes
            int downloadBytesUncompressed = System.Text.Encoding.UTF8.GetByteCount(request.downloadHandler.text);
            float downloadCompressionRatio = downloadBytesCompressed > 0 ? (float)downloadBytesUncompressed / downloadBytesCompressed : 1f;
            Debug.Log($"[BYTES] Upload={uploadBytesCompressed}B (compressed from {uploadBytesUncompressed}B), Download={downloadBytesCompressed}B (compressed), {downloadBytesUncompressed}B (uncompressed), {downloadCompressionRatio:F2}x compression");

            // PHASE 3: Store response in trace and mark as Completed (DO NOT display immediately)
            long receiveTimestamp = TimestampUtil.GetUnixTimestampMs();
            trace.e2e_ms = e2eMs;
            trace.server_proc_ms = serverProcMs;
            trace.response = response;  // Store entire response for later display

            // Parse server timestamps from response
            trace.server_receive_ts = (long)(response.t_server_recv * 1000);  // Convert to milliseconds
            trace.server_process_start_ts = (long)(response.server_process_start_ts * 1000);  // NEW: For queue_wait_ms
            trace.server_send_ts = (long)(response.t_server_send * 1000);     // Convert to milliseconds

            // Store latency breakdown (for Excel telemetry)
            trace.upload_ms = uploadMs;
            trace.download_ms = downloadMs;
            trace.parse_ms = parseMs;

            // Store payload sizes (for Excel telemetry)
            trace.upload_bytes_uncompressed = uploadBytesUncompressed;
            trace.upload_bytes_compressed = uploadBytesCompressed;
            trace.download_bytes_uncompressed = downloadBytesUncompressed;
            trace.download_bytes_compressed = downloadBytesCompressed;

            // Extract detection metrics from response (for Excel telemetry)
            int detectionCount = response.detections?.detections?.Length ?? 0;
            float avgConfidence = 0f;
            if (response.detections != null && response.detections.detections != null && response.detections.detections.Length > 0)
            {
                float sum = 0f;
                foreach (var det in response.detections.detections)
                {
                    sum += det.confidence;
                }
                avgConfidence = sum / response.detections.detections.Length;
            }
            trace.detection_count = detectionCount;
            trace.avg_confidence = avgConfidence;

            trace.MarkCompleted(receiveTimestamp);

            Debug.Log($"[FRAME TRACE] Frame {m_frameId} completed (state={trace.state}). Display deferred to Update().");
            Debug.Log($"[SERVER] Received {response.detections?.detections?.Length ?? 0} detections, processing time: {response.processing_time_ms:F1}ms");

            // PHASE 3: Store timing data for next frame's HTTP headers (to log in Excel)
            // Metrics updates will happen in DisplayFrame() when frame is actually displayed
            m_lastE2eMs = e2eMs;
            m_lastUploadMs = uploadMs;
            m_lastDownloadMs = downloadMs;
            m_lastParseMs = parseMs;
            m_lastUploadBytesUncompressed = uploadBytesUncompressed;  // Original RGB size
            m_lastUploadBytes = uploadBytesCompressed;  // Compressed JPEG size
            m_lastDownloadBytes = downloadBytesUncompressed;  // Uncompressed JSON size
            m_lastDownloadBytesCompressed = downloadBytesCompressed;  // Compressed network transfer size
        }

        // ============================================================================
        // PHASE 3: DEFERRED DISPLAY LOGIC (Parallel Processing)
        // ============================================================================

        private void Update()
        {
            // PHASE 3: Fixed cadence inference triggering (UDP mode only)
            if (m_useServerInference && m_useUDPTransport && m_cameraReady)
            {
                // Check if paused
                if (m_uiMenuManager.IsPaused)
                {
                    return;  // Don't send inference requests while paused
                }

                // Check if it's time for next inference
                float currentTime = Time.time;
                if (currentTime >= m_nextInferenceTime)
                {
                    // Calculate next inference time BEFORE starting inference (fixed cadence)
                    float targetInterval = m_inferenceConfig.GetInferenceInterval();
                    m_nextInferenceTime = currentTime + targetInterval;

                    // Start inference without blocking (fire and forget)
                    StartCoroutine(RunInferenceNonBlocking());

                    Debug.Log($"[PHASE 3 SENTIS] Triggered inference at fixed cadence (interval={targetInterval * 1000f:F0}ms)");
                }
            }

            if (m_useServerInference)
            {
                // PRIORITY 3: Increment freeze counter BEFORE trying to display
                // This counts Unity frames since last display
                m_framesSinceLastDisplay++;

                TryDisplayNewestFrame();
                CleanupOldFrames();
                CheckFrameTimeouts();

                if (Time.realtimeSinceStartup - m_lastMetricsLogTime > METRICS_LOG_INTERVAL)
                {
                    Debug.Log($"[SENTIS PERFORMANCE] {GetPerformanceMetrics()}");
                    m_lastMetricsLogTime = Time.realtimeSinceStartup;
                }
            }
        }

        private void TryDisplayNewestFrame()
        {
            lock (m_frameTracesLock)
            {
                var completedFrames = new List<FrameTrace>();
                foreach (var trace in m_frameTraces.Values)
                {
                    if (trace.state == FrameState.Completed)
                    {
                        completedFrames.Add(trace);
                    }
                }

                if (completedFrames.Count == 0) return;

                completedFrames.Sort((a, b) => b.frame_id.CompareTo(a.frame_id));
                FrameTrace newest = completedFrames[0];

                long currentTimestamp = TimestampUtil.GetUnixTimestampMs();

                // PRIORITY 1: Mark ALL older frames as dropped (including frames older than m_lastDisplayedFrameId)
                // This handles out-of-order completion (e.g., Frame 4 arrives before Frame 1)
                for (int i = 1; i < completedFrames.Count; i++)
                {
                    var olderFrame = completedFrames[i];
                    olderFrame.MarkDropped(currentTimestamp, $"superseded_by_newer_{newest.frame_id}");
                    m_droppedFrames++;

                    // Write to local telemetry IMMEDIATELY
                    WriteLocalTelemetry(olderFrame);

                    // PRIORITY 1: Enqueue dropped frame instead of overwriting
                    m_completedFramesQueue.Enqueue(olderFrame);
                    Debug.Log($"[TELEMETRY QUEUE] Frame {olderFrame.frame_id} DROPPED ??queued (queue depth: {m_completedFramesQueue.Count})");
                }

                // Check if newest frame is too old (arrived after a newer frame was already displayed)
                if (newest.frame_id <= m_lastDisplayedFrameId)
                {
                    // This frame arrived late, mark as dropped
                    newest.MarkDropped(currentTimestamp, $"arrived_after_newer_{m_lastDisplayedFrameId}");
                    m_droppedFrames++;

                    // Write to local telemetry IMMEDIATELY
                    WriteLocalTelemetry(newest);

                    m_completedFramesQueue.Enqueue(newest);
                    Debug.Log($"[TELEMETRY QUEUE] Frame {newest.frame_id} DROPPED (late arrival) ??queued (queue depth: {m_completedFramesQueue.Count})");
                    return;
                }

                // Display newest frame
                DisplayFrame(newest);
                newest.MarkDisplayed(currentTimestamp);
                m_lastDisplayedFrameId = newest.frame_id;

                // PRIORITY 3: Calculate improved freeze/drop metrics
                int currentFrameCount = Time.frameCount;
                if (m_lastDisplayedFrameCount >= 0)
                {
                    newest.freeze_frames = currentFrameCount - m_lastDisplayedFrameCount - 1;
                }
                else
                {
                    newest.freeze_frames = 0;  // First displayed frame
                }
                m_lastDisplayedFrameCount = currentFrameCount;

                // NEW: Calculate improved freeze metrics
                newest.freeze_duration_ms = newest.freeze_frames * m_unityFrameTimeMs;
                m_cumulativeFreezeFrames += newest.freeze_frames;
                newest.cumulative_freeze_frames = m_cumulativeFreezeFrames;
                newest.freeze_ratio = newest.freeze_frames > 0
                    ? (float)newest.freeze_frames / (newest.freeze_frames + 1)
                    : 0f;

                // NEW: Calculate drop metrics
                if (m_lastLoggedFrameId >= 0)
                {
                    newest.frame_gap = newest.frame_id - m_lastLoggedFrameId - 1;
                    if (newest.frame_gap > 0)
                    {
                        m_cumulativeDropped += newest.frame_gap;
                    }
                }
                else
                {
                    newest.frame_gap = newest.frame_id;  // First frame
                    m_cumulativeDropped += newest.frame_id;
                }

                m_cumulativeDisplayed++;
                newest.cumulative_dropped = m_cumulativeDropped;
                newest.cumulative_displayed = m_cumulativeDisplayed;

                int totalFrames = m_cumulativeDropped + m_cumulativeDisplayed;
                newest.drop_rate = totalFrames > 0 ? (float)m_cumulativeDropped / totalFrames : 0f;

                // NEW: Session context
                newest.session_frame_index = m_sessionFrameIndex++;
                m_lastLoggedFrameId = newest.frame_id;

                Debug.Log($"[FREEZE METRICS] Frame {newest.frame_id} displayed: " +
                          $"freeze={newest.freeze_frames} ({newest.freeze_duration_ms:F1}ms), " +
                          $"gap={newest.frame_gap}, drop_rate={newest.drop_rate:P1}, " +
                          $"session_idx={newest.session_frame_index}");

                // Write to local telemetry IMMEDIATELY (bypasses N+1 delay)
                WriteLocalTelemetry(newest);

                // Frame marked as Displayed - telemetry will be sent with next frame (N+1 pattern)
                Debug.Log($"[TELEMETRY] Frame {newest.frame_id} marked as DISPLAYED (telemetry ready for N+1 send)");

                // LEGACY COMPATIBILITY: Also set m_lastCompletedTrace for backward compatibility
                m_lastCompletedTrace = newest;
            }
        }

        private void DisplayFrame(FrameTrace trace)
        {
            ServerResponse response = trace.response as ServerResponse;
            if (response == null)
            {
                // No response - just clear detections list (UI will timeout and clear automatically)
                m_detections.Clear();
                return;
            }

            // Convert server detections to Unity format and update m_detections
            m_detections.Clear();

            // OLD nested structure (restore compatibility)
            if (response.detections != null && response.detections.detections != null)
            {
                // Calculate scale factors to convert from camera resolution to model input resolution
                float scaleX = response.model_input_width / (float)response.input_image_width;
                float scaleY = response.model_input_height / (float)response.input_image_height;

                foreach (var det in response.detections.detections)
                {
                    // Convert bbox from image space (1280x960) to model input space (640x640)
                    Vector4 bboxUnity = new Vector4(
                        det.bbox_pixels[0] * scaleX,  // x1
                        det.bbox_pixels[1] * scaleY,  // y1
                        det.bbox_pixels[2] * scaleX,  // x2
                        det.bbox_pixels[3] * scaleY   // y2
                    );

                    m_detections.Add((det.class_id, bboxUnity));
                }

                Debug.Log($"[DISPLAY] Frame {trace.frame_id}: Converted {m_detections.Count} detections");
            }

            // Draw detections using UI inference (DrawUIBoxes method)
            var cachedCameraPose = m_cameraAccess.GetCameraPose();
            m_uiInference.DrawUIBoxes(m_detections, m_inputSize, cachedCameraPose);

            // Update metrics with this frame's data
            float e2eMs = trace.e2e_ms;
            float uploadMs = m_lastUploadMs;
            float downloadMs = m_lastDownloadMs;
            float parseMs = m_lastParseMs;
            int uploadBytesCompressed = trace.upload_bytes_compressed;
            int uploadBytesUncompressed = trace.upload_bytes_uncompressed;
            int downloadBytesCompressed = m_lastDownloadBytesCompressed;
            int downloadBytesUncompressed = m_lastDownloadBytes;
            float serverProcMs = trace.server_proc_ms;

            // Compute average detection confidence (OLD nested structure)
            int detectionCount = response.detections?.detections?.Length ?? 0;
            float avgConfidence = 0f;
            if (response.detections != null && response.detections.detections != null && response.detections.detections.Length > 0)
            {
                float sum = 0f;
                foreach (var det in response.detections.detections)
                {
                    sum += det.confidence;
                }
                avgConfidence = sum / response.detections.detections.Length;
            }

            // Update metrics in the main info panel (grey bottom panel)
            if (m_uiMenuManager != null)
            {
                m_uiMenuManager.UpdateMetrics(
                    e2eMs,
                    uploadMs,
                    serverProcMs,
                    downloadMs,
                    parseMs,
                    uploadBytesCompressed,
                    downloadBytesCompressed,
                    avgConfidence
                );
            }

            // Update real-time HUD overlay (legacy)
            if (m_inferenceHUD != null)
            {
                m_inferenceHUD.UpdateHUD(
                    e2eMs,
                    uploadMs,
                    serverProcMs,
                    downloadMs,
                    parseMs,
                    uploadBytesCompressed,
                    downloadBytesUncompressed,
                    downloadBytesCompressed,
                    detectionCount,
                    avgConfidence
                );
            }

            // Update SharedInferenceHUD with metrics (NEW)
            if (m_sharedHUD != null)
            {
                m_sharedHUD.UpdateMetrics(
                    e2eMs,
                    uploadMs,
                    serverProcMs,
                    downloadMs,
                    parseMs,
                    uploadBytesCompressed,
                    downloadBytesUncompressed,
                    downloadBytesCompressed,
                    detectionCount,
                    avgConfidence,
                    0f  // No keypoint confidence for detection mode
                );
            }
        }

        // ============================================================================
        // PHASE 6: CLEANUP AND OPTIMIZATION
        // ============================================================================

        private void CleanupOldFrames()
        {
            lock (m_frameTracesLock)
            {
                if (m_frameTraces.Count <= MAX_FRAME_TRACES) return;

                var completedFrames = new List<int>();
                foreach (var kvp in m_frameTraces)
                {
                    var state = kvp.Value.state;
                    if (state == FrameState.Displayed || state == FrameState.Dropped || state == FrameState.Failed)
                    {
                        completedFrames.Add(kvp.Key);
                    }
                }

                completedFrames.Sort();
                int toRemove = m_frameTraces.Count - MAX_FRAME_TRACES;

                for (int i = 0; i < Mathf.Min(toRemove, completedFrames.Count); i++)
                {
                    m_frameTraces.Remove(completedFrames[i]);
                }
            }
        }

        private void CheckFrameTimeouts()
        {
            long currentTimeMs = TimestampUtil.GetUnixTimestampMs();

            lock (m_frameTracesLock)
            {
                foreach (var trace in m_frameTraces.Values)
                {
                    if (trace.state != FrameState.Pending) continue;

                    long timeSinceSendMs = currentTimeMs - trace.unity_send_ts;
                    float timeSinceSendSec = timeSinceSendMs / 1000f;
                    if (timeSinceSendSec > FRAME_TIMEOUT_SECONDS)
                    {
                        trace.MarkFailed($"Timeout after {timeSinceSendSec:F1}s");

                        // Write to local telemetry IMMEDIATELY
                        WriteLocalTelemetry(trace);

                        // PRIORITY 1: Enqueue timeout failed frame for telemetry
                        m_completedFramesQueue.Enqueue(trace);
                        Debug.Log($"[TELEMETRY QUEUE] Frame {trace.frame_id} TIMEOUT ??queued (queue depth: {m_completedFramesQueue.Count})");

                        if (m_pendingRequests.ContainsKey(trace.frame_id))
                        {
                            m_pendingRequests[trace.frame_id].Abort();
                            m_pendingRequests.Remove(trace.frame_id);
                        }
                    }
                }
            }
        }

        private string GetPerformanceMetrics()
        {
            lock (m_frameTracesLock)
            {
                int pendingCount = 0, completedCount = 0, displayedCount = 0, droppedCount = 0, failedCount = 0;

                foreach (var trace in m_frameTraces.Values)
                {
                    switch (trace.state)
                    {
                        case FrameState.Pending: pendingCount++; break;
                        case FrameState.Completed: completedCount++; break;
                        case FrameState.Displayed: displayedCount++; break;
                        case FrameState.Dropped: droppedCount++; break;
                        case FrameState.Failed: failedCount++; break;
                    }
                }

                int totalFrames = m_frameTraces.Count;
                float dropRate = totalFrames > 0 ? (float)droppedCount / totalFrames * 100f : 0f;

                return $"Traces={totalFrames} Pending={pendingCount} Completed={completedCount} " +
                       $"Displayed={displayedCount} Dropped={droppedCount}({dropRate:F1}%) Failed={failedCount}";
            }
        }

        // ============================================================================
        // UDP TRANSPORT - Phase 1 Non-Blocking Implementation
        // ============================================================================

        /// <summary>
        /// Send frame via UDP (non-blocking)
        /// N+1 telemetry: Attach previous frame's final state (if ready and not yet sent)
        /// </summary>
        private void SendFrameUDP(FrameTrace trace, byte[] jpegData)
        {
            string serverUrl = m_inferenceConfig.BuildUrl();
            System.Uri uri = new System.Uri(serverUrl);
            string serverIP = uri.Host;

            // N+1 delayed telemetry: Get telemetry for previous frame (currentFrameId - 1)
            int prevFrameId = trace.frame_id - 1;
            string prevTelemetryJson = null;

            lock (m_frameTracesLock)
            {
                // Check if previous frame exists and is ready to send
                if (prevFrameId > 0 && m_frameTraces.TryGetValue(prevFrameId, out var prevTrace))
                {
                    // Only send if:
                    // 1. Frame has reached a FINAL state (Displayed/Dropped/Failed)
                    // 2. Telemetry has NOT been sent yet
                    bool isFinalState = (prevTrace.state == FrameState.Displayed ||
                                        prevTrace.state == FrameState.Dropped ||
                                        prevTrace.state == FrameState.Failed);

                    if (isFinalState && !prevTrace.telemetry_sent)
                    {
                        // Build telemetry JSON for this specific frame
                        prevTelemetryJson = BuildTelemetryJson(prevTrace);

                        // Mark as sent to prevent re-sending
                        prevTrace.telemetry_sent = true;

                        Debug.Log($"[UNITY TELEMETRY] Sending trace for frame {prevTrace.frame_id}, " +
                                  $"session={prevTrace.session_id}, " +
                                  $"final_state={prevTrace.state}, " +
                                  $"detection_count={prevTrace.detection_count ?? 0}");
                    }
                    else if (!isFinalState)
                    {
                        Debug.Log($"[UNITY TELEMETRY] Frame {prevFrameId} not final yet (state={prevTrace.state})");
                    }
                    else
                    {
                        Debug.Log($"[UNITY TELEMETRY] Frame {prevFrameId} telemetry already sent");
                    }
                }
                else if (prevFrameId > 0)
                {
                    Debug.Log($"[UNITY TELEMETRY] Frame {prevFrameId} not found in traces dictionary");
                }
            }

            // Store upload payload sizes (for Excel telemetry)
            trace.upload_bytes_compressed = jpegData.Length;  // JPEG compressed size
            // upload_bytes_uncompressed already set correctly before SendFrameUDP() was called

            // Send UDP packet with attached telemetry (or null if not ready)
            UDPTransport.SendFrame(m_udpClient, serverIP, UDP_PORT, trace, jpegData, prevTelemetryJson);

            Debug.Log($"[UDP SEND] Frame {trace.frame_id} sent to {serverIP}:{UDP_PORT}, upload_bytes={jpegData.Length}");
        }

        /// <summary>
        /// Poll HTTP response endpoint for inference result (async, non-blocking)
        /// </summary>
        private IEnumerator ListenForResponseHTTP(int expectedFrameId)
        {
            // Build response polling URL
            string serverUrl = m_inferenceConfig.BuildUrl();
            System.Uri uri = new System.Uri(serverUrl);
            string responseUrl = $"http://{uri.Host}:{uri.Port}/response/{m_sessionId}/{expectedFrameId}";

            float timeout = 5f;  // 5 second timeout
            float elapsed = 0f;
            float pollInterval = 0.1f;  // Poll every 100ms

            // Get trace reference
            FrameTrace trace = null;
            lock (m_frameTracesLock)
            {
                if (!m_frameTraces.TryGetValue(expectedFrameId, out trace))
                {
                    Debug.LogWarning($"[UDP POLL] Frame {expectedFrameId} not found in traces!");
                    yield break;
                }
            }

            Debug.Log($"[UDP POLL] Starting polling for frame {expectedFrameId}");

            // Poll until result available or timeout
            while (elapsed < timeout)
            {
                using (UnityWebRequest request = UnityWebRequest.Get(responseUrl))
                {
                    yield return request.SendWebRequest();

                    if (request.result == UnityWebRequest.Result.Success)
                    {
                        // Response received!
                        long receiveTs = TimestampUtil.GetUnixTimestampMs();

                        // Parse and process response (reuse existing logic)
                        string jsonResponse = request.downloadHandler.text;
                        ProcessServerResponse(trace, jsonResponse, receiveTs);

                        Debug.Log($"[UDP POLL] Frame {expectedFrameId} received after {elapsed:F2}s");
                        yield break;
                    }
                    else if (request.responseCode == 404)
                    {
                        // Not ready yet - this is expected, continue polling
                        // (Server returns 404 while processing)
                    }
                    else
                    {
                        // Actual error (not just "not ready")
                        Debug.LogError($"[UDP POLL] Error polling frame {expectedFrameId}: {request.error}");
                        trace.MarkFailed($"Poll error: {request.error}");

                        // Enqueue failed frame for telemetry
                        lock (m_frameTracesLock)
                        {
                            m_completedFramesQueue.Enqueue(trace);
                        }
                        Debug.Log($"[TELEMETRY QUEUE] Frame {trace.frame_id} FAILED ??queued (queue depth: {m_completedFramesQueue.Count})");
                        yield break;
                    }
                }

                yield return new WaitForSeconds(pollInterval);
                elapsed += pollInterval;
            }

            // Timeout - mark as failed
            Debug.LogWarning($"[UDP POLL] Timeout waiting for frame {expectedFrameId} after {timeout}s");
            trace.MarkFailed("Response timeout (5s)");

            // Enqueue timeout failed frame for telemetry
            lock (m_frameTracesLock)
            {
                m_completedFramesQueue.Enqueue(trace);
            }
            Debug.Log($"[TELEMETRY QUEUE] Frame {trace.frame_id} TIMEOUT ??queued (queue depth: {m_completedFramesQueue.Count})");
        }

        /// <summary>
        /// Process server response JSON and update frame trace
        /// (Extracted from old RunServerInference to be reusable by UDP polling)
        /// </summary>
        private void ProcessServerResponse(FrameTrace trace, string jsonResponse, long receiveTs)
        {
            // Parse JSON response
            Debug.Log($"[UDP RESPONSE] Response: {jsonResponse.Substring(0, Mathf.Min(200, jsonResponse.Length))}...");

            ServerResponse response = JsonUtility.FromJson<ServerResponse>(jsonResponse);

            if (response == null)
            {
                Debug.LogError("[UDP RESPONSE] Failed to parse JSON response");
                trace.MarkFailed("JSON parse error");

                // Enqueue failed frame for telemetry
                lock (m_frameTracesLock)
                {
                    m_completedFramesQueue.Enqueue(trace);
                }
                Debug.Log($"[TELEMETRY QUEUE] Frame {trace.frame_id} FAILED (parse error) ??queued (queue depth: {m_completedFramesQueue.Count})");
                return;
            }

            // Manual parse server timestamps (JsonUtility limitation)
            if (response.t_server_recv == 0 || response.t_server_send == 0)
            {
                string recvStr = ExtractSimpleJsonValue(jsonResponse, "t_server_recv");
                if (!string.IsNullOrEmpty(recvStr) && double.TryParse(recvStr, out double recvVal))
                {
                    response.t_server_recv = recvVal;
                    Debug.LogWarning($"[TELEMETRY FIX] Manually parsed t_server_recv: {recvVal}");
                }

                string sendStr = ExtractSimpleJsonValue(jsonResponse, "t_server_send");
                if (!string.IsNullOrEmpty(sendStr) && double.TryParse(sendStr, out double sendVal))
                {
                    response.t_server_send = sendVal;
                    Debug.LogWarning($"[TELEMETRY FIX] Manually parsed t_server_send: {sendVal}");
                }
            }

            // Store server timestamps
            trace.server_receive_ts = (long)(response.t_server_recv * 1000);
            trace.server_process_start_ts = (long)(response.server_process_start_ts * 1000);
            trace.server_send_ts = (long)(response.t_server_send * 1000);
            trace.server_proc_ms = response.processing_time_ms;

            // Calculate latency breakdown (METHOD B: Residual estimation)
            // E2E latency (Unity-only, avoids clock skew)
            float e2eMs = receiveTs - trace.unity_send_ts;

            // Server-side metrics (from response, server-only timing)
            float serverProcMs = response.processing_time_ms;
            float queueWaitMs = (float)((response.server_process_start_ts - response.t_server_recv) * 1000);

            // Network total time (residual: E2E - server_time)
            float networkTotalMs = Mathf.Max(0f, e2eMs - serverProcMs - queueWaitMs);

            // Split network time by compressed data size ratio (METHOD B)
            int uploadBytesCompressed = trace.upload_bytes_compressed;
            int downloadBytesUncompressed = System.Text.Encoding.UTF8.GetByteCount(jsonResponse);
            int downloadBytesCompressed = downloadBytesUncompressed;  // No compression info in UDP polling

            int totalBytes = uploadBytesCompressed + downloadBytesCompressed;
            float uploadRatio = totalBytes > 0 ? (float)uploadBytesCompressed / totalBytes : 0.5f;

            float uploadMs = networkTotalMs * uploadRatio;
            float downloadMs = networkTotalMs * (1.0f - uploadRatio);
            float parseMs = 0f;  // UDP polling doesn't track parse time separately

            // Store latency breakdown
            trace.e2e_ms = e2eMs;
            trace.upload_ms = uploadMs;
            trace.download_ms = downloadMs;
            trace.parse_ms = parseMs;

            Debug.Log($"[LATENCY METHOD B] Frame {trace.frame_id}: E2E={e2eMs:F1}ms, " +
                      $"network_total={networkTotalMs:F1}ms (upload={uploadMs:F1}ms, download={downloadMs:F1}ms), " +
                      $"server={serverProcMs:F1}ms, queue={queueWaitMs:F1}ms, " +
                      $"upload_ratio={uploadRatio:F2} ({uploadBytesCompressed}/{totalBytes} bytes)");

            // Store payload sizes (for Excel telemetry)
            trace.download_bytes_uncompressed = downloadBytesUncompressed;
            trace.download_bytes_compressed = downloadBytesCompressed;

            // Extract detection metrics from response (for Excel telemetry)
            int detectionCount = response.detections?.detections?.Length ?? 0;
            float avgConfidence = 0f;
            if (response.detections != null && response.detections.detections != null && response.detections.detections.Length > 0)
            {
                float sum = 0f;
                foreach (var det in response.detections.detections)
                {
                    sum += det.confidence;
                }
                avgConfidence = sum / response.detections.detections.Length;
            }
            trace.detection_count = detectionCount;
            trace.avg_confidence = avgConfidence;

            // Store response
            trace.response = response;

            // Mark as completed (state updated in m_frameTraces dictionary)
            trace.MarkCompleted(receiveTs);
            Debug.Log($"[TELEMETRY] Frame {trace.frame_id} marked as COMPLETED (detection_count={detectionCount}, avg_conf={avgConfidence:F2})");

            Debug.Log($"[UDP RESPONSE] Frame {trace.frame_id} processed successfully");
        }

        /// <summary>
        /// Build telemetry JSON for a specific FrameTrace (N+1 delayed telemetry).
        /// Matches the 34-column Excel schema expected by server.
        /// NOTE: Should only be called when frame is in final state (Displayed/Dropped/Failed).
        /// </summary>
        private string BuildTelemetryJson(FrameTrace trace)
        {
            // Build telemetry JSON matching the 34-column Excel schema
            // Server will extract these fields and write one row to InferenceLog
            var json = "{" +
                $"\"scene\":\"MultiObjectDetection\"," +
                $"\"session_id\":\"{trace.session_id}\"," +
                $"\"frame_id\":{trace.frame_id}," +
                $"\"mode\":\"detection\"," +  // CRITICAL: Server reads this to determine inference type

                // Unity-side timing (all Unix milliseconds)
                $"\"unity_send_ts\":{trace.unity_send_ts}," +
                $"\"unity_receive_ts\":{trace.unity_receive_ts}," +
                $"\"unity_display_ts\":{trace.unity_display_ts ?? 0}," +
                $"\"unity_drop_ts\":{trace.unity_drop_ts ?? 0}," +

                // Server-side timing (from response, Unix milliseconds)
                $"\"server_receive_ts\":{trace.server_receive_ts}," +
                $"\"server_process_start_ts\":{trace.server_process_start_ts}," +
                $"\"server_send_ts\":{trace.server_send_ts}," +

                // Latency breakdown (milliseconds)
                $"\"latency_ms\":{trace.e2e_ms:F2}," +
                $"\"upload_ms\":{trace.upload_ms:F2}," +
                $"\"queue_wait_ms\":{(trace.server_process_start_ts - trace.server_receive_ts):F2}," +
                $"\"server_proc_ms\":{trace.server_proc_ms:F2}," +
                $"\"download_ms\":{trace.download_ms:F2}," +
                $"\"parse_ms\":{trace.parse_ms:F2}," +
                $"\"udp_send_ms\":{trace.udp_send_ms:F2}," +

                // NEW: Improved Freeze Metrics
                $"\"freeze_duration_ms\":{trace.freeze_duration_ms:F2}," +
                $"\"cumulative_freeze_frames\":{trace.cumulative_freeze_frames}," +
                $"\"freeze_ratio\":{trace.freeze_ratio:F3}," +

                // NEW: Improved Drop Metrics
                $"\"frame_gap\":{trace.frame_gap}," +
                $"\"cumulative_dropped\":{trace.cumulative_dropped}," +
                $"\"cumulative_displayed\":{trace.cumulative_displayed}," +
                $"\"drop_rate\":{trace.drop_rate:F3}," +

                // NEW: Session Context
                $"\"session_frame_index\":{trace.session_frame_index}," +

                // Payload sizes (bytes)
                $"\"upload_bytes_uncompressed\":{trace.upload_bytes_uncompressed}," +
                $"\"upload_bytes_compressed\":{trace.upload_bytes_compressed}," +
                $"\"download_bytes_uncompressed\":{trace.download_bytes_uncompressed}," +
                $"\"download_bytes_compressed\":{trace.download_bytes_compressed}," +

                // Final state and reasons
                $"\"final_state\":\"{trace.state}\"," +
                $"\"drop_reason\":\"{EscapeJson(trace.drop_reason ?? "")}\"," +
                $"\"error_reason\":\"{EscapeJson(trace.error_reason ?? "")}\"," +

                // Inference results (from response)
                $"\"detection_count\":{trace.detection_count ?? 0}," +
                $"\"avg_confidence\":{trace.avg_confidence:F4}," +

                // Legacy/compatibility
                $"\"freeze_frames_per_frame\":{trace.freeze_frames}," +
                $"\"target_fps\":{m_inferenceConfig.targetFPS:F1}" +
                "}";

            return json;
        }

        /// <summary>
        /// Escape string for JSON (handle quotes and backslashes)
        /// </summary>
        private string EscapeJson(string value)
        {
            if (string.IsNullOrEmpty(value))
                return "";

            return value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");
        }

        /// <summary>
        /// Write frame trace to local CSV telemetry (immediate logging, bypasses N+1 delay)
        /// Call this whenever a frame reaches a FINAL state (Displayed/Dropped/Failed)
        /// </summary>
        private void WriteLocalTelemetry(FrameTrace trace)
        {
            if (m_localTelemetry != null)
            {
                try
                {
                    m_localTelemetry.WriteFrameTrace(trace, "MultiObjectDetection");
                }
                catch (System.Exception ex)
                {
                    Debug.LogError($"[LOCAL TELEMETRY] Failed to write frame {trace.frame_id}: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Encode texture to JPEG bytes (extracted from old RunServerInference)
        /// </summary>
        private byte[] EncodeTextureToJPEG(Texture texture)
        {
            // 1. Convert texture to Texture2D if needed
            Texture2D tex2D = texture as Texture2D;
            if (tex2D == null)
            {
                // Handle RenderTexture case
                RenderTexture rt = texture as RenderTexture;
                if (rt != null)
                {
                    tex2D = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
                    RenderTexture.active = rt;
                    tex2D.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                    tex2D.Apply();
                    RenderTexture.active = null;
                }
                else
                {
                    Debug.LogError("Unsupported texture type for server inference");
                    return null;
                }
            }

            // 2. Downsample texture if configured (reduces upload size significantly)
            Texture2D textureToEncode = tex2D;
            int downsampleFactor = m_inferenceConfig.downsampleFactor;

            if (downsampleFactor > 1)
            {
                int downsampledWidth = tex2D.width / downsampleFactor;
                int downsampledHeight = tex2D.height / downsampleFactor;

                // Create temporary RenderTexture for downsampling
                RenderTexture rt = RenderTexture.GetTemporary(downsampledWidth, downsampledHeight, 0, RenderTextureFormat.ARGB32);
                rt.filterMode = FilterMode.Bilinear;

                // Blit original texture to downsampled RenderTexture
                Graphics.Blit(tex2D, rt);

                // Read back to Texture2D
                RenderTexture previous = RenderTexture.active;
                RenderTexture.active = rt;
                Texture2D downsampledTex = new Texture2D(downsampledWidth, downsampledHeight, TextureFormat.RGB24, false);
                downsampledTex.ReadPixels(new Rect(0, 0, downsampledWidth, downsampledHeight), 0, 0);
                downsampledTex.Apply();
                RenderTexture.active = previous;

                // Release temporary RenderTexture
                RenderTexture.ReleaseTemporary(rt);

                textureToEncode = downsampledTex;
                Debug.Log($"[DETECTION DOWNSAMPLE] {tex2D.width}x{tex2D.height} ??{downsampledWidth}x{downsampledHeight} (factor={downsampleFactor})");
            }

            // 3. Encode texture as JPEG
            int jpegQuality = m_inferenceConfig.jpegQuality;
            byte[] jpegBytes = textureToEncode.EncodeToJPG(jpegQuality);

            // Clean up downsampled texture if created
            if (downsampleFactor > 1 && textureToEncode != tex2D)
            {
                Destroy(textureToEncode);
            }

            return jpegBytes;
        }

        /// <summary>
        /// Manually extract a simple JSON value from a JSON string.
        /// Required because JsonUtility has limitations with certain field types.
        /// </summary>
        private string ExtractSimpleJsonValue(string json, string fieldName)
        {
            string searchPattern = $"\"{fieldName}\":";
            int fieldStart = json.IndexOf(searchPattern);

            if (fieldStart < 0)
            {
                return null;
            }

            // Skip past the field name and colon
            int valueStart = fieldStart + searchPattern.Length;

            // Skip whitespace
            while (valueStart < json.Length && char.IsWhiteSpace(json[valueStart]))
            {
                valueStart++;
            }

            if (valueStart >= json.Length)
            {
                return null;
            }

            // Find end of value (comma, closing brace, or end of string)
            int valueEnd = valueStart;
            bool inString = json[valueStart] == '"';

            if (inString)
            {
                // Skip opening quote
                valueStart++;
                valueEnd = valueStart;
                // Find closing quote
                while (valueEnd < json.Length && json[valueEnd] != '"')
                {
                    if (json[valueEnd] == '\\') valueEnd++; // Skip escaped characters
                    valueEnd++;
                }
            }
            else
            {
                // Find end of number
                while (valueEnd < json.Length && json[valueEnd] != ',' && json[valueEnd] != '}' && json[valueEnd] != ']' && !char.IsWhiteSpace(json[valueEnd]))
                {
                    valueEnd++;
                }
            }

            if (valueEnd > valueStart)
            {
                return json.Substring(valueStart, valueEnd - valueStart).Trim();
            }

            return null;
        }

    }
}



