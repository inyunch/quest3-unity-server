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

        [Header("Server Inference")]
        [SerializeField] private bool m_useServerInference = false;
        [SerializeField] private InferenceConfig m_inferenceConfig = new InferenceConfig
        {
            mode = InferenceMode.ObjectDetection,
            targetFPS = 5f,
            jpegQuality = 80,
            includeMask = false,
            includeDepth = false
        };

        [Header("[Editor Only] Convert to Sentis")]
        public ModelAsset OnnxModel;
        [Space(40)]

        // ============================================================================
        // V3.0 OOP COMPONENTS - Replaces inline UDP + telemetry code
        // ============================================================================
        private UDPTransportManager m_transport;
        private FrameTelemetryTracker m_telemetry;
        [SerializeField] private bool m_useUDPTransport = true;

        // Sentis engine and detections
        private Worker m_engine;
        private Vector2Int m_inputSize;
        private readonly List<(int classId, Vector4 boundingBox)> m_detections = new List<(int classId, Vector4 boundingBox)>();

        // Session tracking
        private int m_frameId = 0;
        private string m_sessionId;
        private bool m_cameraReady = false;
        private float m_nextInferenceTime = 0f;

        private void Awake()
        {
            var model = ModelLoader.Load(m_sentisModel);
            var inputShape = model.inputs[0].shape;
            m_inputSize = new Vector2Int(inputShape.Get(2), inputShape.Get(3));
            m_engine = new Worker(model, m_backend);
        }

        private IEnumerator Start()
        {
            // Generate unique session ID for this recording session
            m_sessionId = System.Guid.NewGuid().ToString();
            Debug.Log($"[V3 DETECTION] Started session: {m_sessionId}");

            // V3.0: Initialize OOP components
            if (m_useServerInference && m_useUDPTransport)
            {
                try
                {
                    // Initialize UDP Transport Manager
                    m_transport = new UDPTransportManager(
                        serverIP: ServerConfig.Instance.ServerIP,
                        sendPort: 8002,
                        receivePort: 8003
                    );
                    m_transport.Initialize();
                    Debug.Log($"[V3 DETECTION] UDP Transport initialized");

                    // Initialize Telemetry Tracker
                    m_telemetry = new FrameTelemetryTracker(
                        sessionId: m_sessionId,
                        sceneName: "MultiObjectDetection",
                        enableLocalTelemetry: true
                    );
                    Debug.Log($"[V3 DETECTION] Telemetry tracker initialized");
                }
                catch (System.Exception e)
                {
                    Debug.LogError($"[V3 DETECTION] Failed to initialize V3 components: {e.Message}");
                    m_useUDPTransport = false;  // Fall back to local mode
                }
            }

            m_uiInference.SetLabels(m_labelsAsset);

            // Validate and log inference configuration
            if (m_useServerInference)
            {
                m_inferenceConfig.Validate();
                m_inferenceConfig.LogSummary();

                // Initialize SharedInferenceHUD if available
                if (m_sharedHUD != null)
                {
                    m_sharedHUD.SetMode(m_inferenceConfig.mode, m_inferenceConfig.targetFPS);
                }

                // Test server connection at startup
                Debug.Log("[V3 DETECTION] Testing connection to server...");
                yield return TestServerConnection();
            }

            // Set camera ready flag and initialize timing
            m_cameraReady = true;
            m_nextInferenceTime = Time.time;

            Debug.Log("[V3 DETECTION] Start() complete - inference now driven by Update() at fixed cadence");
        }

        private void OnDestroy()
        {
            m_engine.PeekOutput(0)?.CompleteAllPendingOperations();
            m_engine.PeekOutput(1)?.CompleteAllPendingOperations();
            m_engine.PeekOutput(2)?.CompleteAllPendingOperations();
            m_engine.Dispose();

            // V3.0: Shutdown OOP components
            m_transport?.Shutdown();
            m_telemetry?.Shutdown();

            Debug.Log("[V3 DETECTION] Cleanup complete");
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

        // ============================================================================
        // V3.0: UPDATE LOOP - Fixed cadence send + response polling
        // ============================================================================

        private void Update()
        {
            // V3.0: Fixed cadence inference triggering (UDP mode only)
            if (m_useServerInference && m_useUDPTransport && m_cameraReady)
            {
                // Check if paused
                if (m_uiMenuManager != null && m_uiMenuManager.IsPaused)
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

                    Debug.Log($"[V3 DETECTION] Triggered inference at fixed cadence (interval={targetInterval * 1000f:F0}ms)");
                }

                // V3.0: Poll for UDP responses (non-blocking!)
                while (m_transport.TryGetResponse(out FrameResponse response))
                {
                    HandleV3Response(response);
                }

                // V3.0: Periodic telemetry cleanup
                if (Time.frameCount % 300 == 0)
                {
                    m_telemetry.CleanupOldTraces();
                }
            }
        }

        // ============================================================================
        // V3.0: RESPONSE HANDLING
        // ============================================================================

        /// <summary>
        /// V3.0: Handle received inference response from UDPTransportManager.
        /// Updates telemetry and displays detection results.
        /// </summary>
        private void HandleV3Response(FrameResponse response)
        {
            Debug.Log($"[V3 DETECTION] Received response for frame {response.frame_id}, " +
                      $"server_proc={response.processing_time_ms:F1}ms, " +
                      $"queue_wait={response.queue_wait_ms:F1}ms");

            // 1. Update telemetry (mark completed)
            m_telemetry.MarkFrameCompleted(response.frame_id, response);

            // 2. Display detection results
            DisplayV3Frame(response);

            // 3. Mark as displayed (this automatically writes to CSV)
            m_telemetry.MarkFrameDisplayed(response.frame_id);
        }

        /// <summary>
        /// V3.0: Display detection frame using FrameResponse.
        /// Replaces old DisplayFrame() that used ServerResponse.
        /// </summary>
        private void DisplayV3Frame(FrameResponse response)
        {
            // Get cached camera pose
            var cachedCameraPose = m_cameraAccess.GetCameraPose();

            // Check if spatial anchor is tracked
            if (!m_cameraAccess.IsPlaying || m_detectionManager.m_spatialAnchor == null || !m_detectionManager.m_spatialAnchor.IsTracked)
            {
                m_detections.Clear();
                return;
            }

            // Check if response has detection data
            if (!response.HasDetections())
            {
                m_detections.Clear();
                Debug.LogWarning($"[V3 DETECTION] Frame {response.frame_id} has no detection data");
                return;
            }

            // Convert FrameResponse.DetectionData[] to Unity format
            m_detections.Clear();

            // Calculate scale factors to convert from camera resolution to model input resolution
            // Note: response.input_width/height are the camera image dimensions
            // m_inputSize is the Sentis model input dimensions (640x640)
            float scaleX = m_inputSize.x / (float)response.input_width;
            float scaleY = m_inputSize.y / (float)response.input_height;

            foreach (var det in response.detections)
            {
                // Convert bbox from image space to model input space
                // bbox_pixels is in camera resolution, scale to model input resolution
                Vector4 bboxUnity = new Vector4(
                    det.bbox_pixels[0] * scaleX,  // x1
                    det.bbox_pixels[1] * scaleY,  // y1
                    det.bbox_pixels[2] * scaleX,  // x2
                    det.bbox_pixels[3] * scaleY   // y2
                );

                m_detections.Add((det.class_id, bboxUnity));
            }

            Debug.Log($"[V3 DETECTION] Frame {response.frame_id}: Converted {m_detections.Count} detections");

            // Draw detections using UI inference (DrawUIBoxes method)
            m_uiInference.DrawUIBoxes(m_detections, m_inputSize, cachedCameraPose);

            // Update metrics with this frame's data
            UpdateMetricsDisplay(response);
        }

        /// <summary>
        /// V3.0: Update all HUD displays with frame metrics.
        /// </summary>
        private void UpdateMetricsDisplay(FrameResponse response)
        {
            // Calculate average detection confidence
            float avgConfidence = 0f;
            if (response.detections != null && response.detections.Length > 0)
            {
                float sum = 0f;
                foreach (var det in response.detections)
                {
                    sum += det.confidence;
                }
                avgConfidence = sum / response.detections.Length;
            }

            // Calculate latency breakdown
            float e2eMs = response.latency_ms;
            float uploadMs = response.upload_ms;
            float downloadMs = response.download_ms;
            float serverProcMs = response.processing_time_ms;
            float parseMs = response.parse_ms;

            int uploadBytesCompressed = response.upload_bytes_compressed;
            int downloadBytesCompressed = response.download_bytes_compressed;
            int downloadBytesUncompressed = 0;  // Not tracked in V3

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
                    response.detections?.Length ?? 0,
                    avgConfidence
                );
            }

            // Update SharedInferenceHUD with metrics
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
                    response.detections?.Length ?? 0,
                    avgConfidence,
                    0f  // No keypoint confidence for detection mode
                );
            }
        }

        // ============================================================================
        // V3.0: NON-BLOCKING INFERENCE SEND
        // ============================================================================

        /// <summary>
        /// V3.0: Non-blocking inference runner called from Update() at fixed intervals.
        /// Encodes frame, creates trace, sends via UDP, returns immediately.
        /// </summary>
        private IEnumerator RunInferenceNonBlocking()
        {
            // Quick checks
            if (!m_cameraAccess.IsPlaying)
            {
                Debug.Log("[V3 DETECTION] Camera not playing, skipping inference");
                yield break;
            }

            // Get current frame texture
            Texture targetTexture = m_cameraAccess.GetTexture();

            // 1. Encode JPEG
            byte[] jpegData = EncodeTextureToJPEG(targetTexture);
            if (jpegData == null)
            {
                Debug.LogError("[V3 DETECTION] Failed to encode texture to JPEG");
                yield break;
            }

            // 2. Create frame trace
            m_frameId++;
            FrameTrace trace = m_telemetry.CreateFrame(m_frameId, jpegData.Length);

            Debug.Log($"[V3 DETECTION] Frame {trace.frame_id} created, size={jpegData.Length} bytes");

            // 3. Send UDP (returns immediately - no blocking!)
            m_transport.SendFrame(trace, jpegData);

            Debug.Log($"[V3 DETECTION] Frame {trace.frame_id} sent via UDP");

            // NO yield return - method completes immediately
            // Response will arrive asynchronously via background UDP listener
        }

        /// <summary>
        /// Encode texture to JPEG bytes.
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

            // 2. Downsample texture if configured
            Texture2D textureToEncode = tex2D;
            int downsampleFactor = m_inferenceConfig.downsampleFactor;

            if (downsampleFactor > 1)
            {
                int downsampledWidth = tex2D.width / downsampleFactor;
                int downsampledHeight = tex2D.height / downsampleFactor;

                RenderTexture rt = RenderTexture.GetTemporary(downsampledWidth, downsampledHeight, 0, RenderTextureFormat.ARGB32);
                rt.filterMode = FilterMode.Bilinear;

                Graphics.Blit(tex2D, rt);

                RenderTexture previous = RenderTexture.active;
                RenderTexture.active = rt;
                Texture2D downsampledTex = new Texture2D(downsampledWidth, downsampledHeight, TextureFormat.RGB24, false);
                downsampledTex.ReadPixels(new Rect(0, 0, downsampledWidth, downsampledHeight), 0, 0);
                downsampledTex.Apply();
                RenderTexture.active = previous;

                RenderTexture.ReleaseTemporary(rt);

                textureToEncode = downsampledTex;
                Debug.Log($"[V3 DETECTION] Downsampled {tex2D.width}x{tex2D.height} -> {downsampledWidth}x{downsampledHeight} (factor={downsampleFactor})");
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

        // ============================================================================
        // LOCAL SENTIS INFERENCE (ORIGINAL CODE - PRESERVED)
        // ============================================================================

        /// <summary>
        /// Legacy RunInference for local Sentis mode (non-server).
        /// This is the original local inference path that runs on-device.
        /// </summary>
        private IEnumerator RunInference()
        {
            if (!m_cameraAccess.IsPlaying)
            {
                yield break;
            }

            [DllImport("OVRPlugin", CallingConvention = CallingConvention.Cdecl)]
            static extern OVRPlugin.Result ovrp_GetNodePoseStateAtTime(double time, OVRPlugin.Node nodeId, out OVRPlugin.PoseStatef nodePoseState);
            if (!ovrp_GetNodePoseStateAtTime(OVRPlugin.GetTimeInSeconds(), OVRPlugin.Node.Head, out _).IsSuccess())
            {
                Debug.Log("ovrp_GetNodePoseStateAtTime failed, which means 'm_cameraAccess.GetCameraPose()' is not reliable, skipping.");
                yield break;
            }

            var cachedCameraPose = m_cameraAccess.GetCameraPose();
            Texture targetTexture = m_cameraAccess.GetTexture();

            // LOCAL SENTIS INFERENCE PATH (ORIGINAL)
            Debug.Log("[SENTIS] Using LOCAL Sentis inference");

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
                {
                    Debug.Log($"[V3 DETECTION] Connection OK! Response: {req.downloadHandler.text}");
                }
                else
                {
                    Debug.LogError($"[V3 DETECTION] Connection FAILED: {req.error}");
                    Debug.LogError($"[V3 DETECTION] Result: {req.result}");
                    Debug.LogError($"[V3 DETECTION] Response Code: {req.responseCode}");
                }
            }
        }
    }
}
