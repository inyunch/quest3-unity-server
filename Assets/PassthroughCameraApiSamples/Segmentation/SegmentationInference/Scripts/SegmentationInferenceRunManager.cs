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

namespace PassthroughCameraSamples.Segmentation
{
    [MetaCodeSample("PassthroughCameraApiSamples-Segmentation")]
    public class SegmentationInferenceRunManager : MonoBehaviour
    {
        [SerializeField] private PassthroughCameraAccess m_cameraAccess;
        [SerializeField] private SegmentationUiMenuManager m_uiMenuManager;
        [SerializeField] private SegmentationManager m_segmentationManager;

        [Header("Sentis Model config")]
        [SerializeField] private BackendType m_backend = BackendType.CPU;
        [SerializeField] private ModelAsset m_sentisModel;
        [SerializeField] private TextAsset m_labelsAsset;
        [SerializeField, Range(0, 1)] private float m_iouThreshold = 0.6f;
        [SerializeField, Range(0, 1)] private float m_scoreThreshold = 0.23f;

        [Header("UI display references")]
        [SerializeField] private SegmentationInferenceUiManager m_uiInference;
        [SerializeField] private InferenceHUD m_inferenceHUD;
        [SerializeField] private SharedInferenceHUD m_sharedHUD;

        [Header("Server Inference (NEW)")]
        [SerializeField] private bool m_useServerInference = true;
        [SerializeField] private InferenceConfig m_inferenceConfig = new InferenceConfig
        {
            mode = InferenceMode.Segmentation,
            targetFPS = 10f,
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

        private void Awake()
        {
            var model = ModelLoader.Load(m_sentisModel);
            var inputShape = model.inputs[0].shape;
            m_inputSize = new Vector2Int(inputShape.Get(2), inputShape.Get(3));
            m_engine = new Worker(model, m_backend);
        }

        private IEnumerator Start()
        {
            // CRITICAL DEBUG - ALWAYS PRINT
            Debug.LogError("========================================");
            Debug.LogError("SEGMENTATION INFERENCE RUN MANAGER START!");
            Debug.LogError("This is SegmentationInferenceRunManager (NOT SentisInferenceRunManager)");
            Debug.LogError("========================================");

            m_uiInference.SetLabels(m_labelsAsset);

            // Validate and log inference configuration
            if (m_useServerInference)
            {
                // Migrate from legacy settings if needed
                if (!string.IsNullOrEmpty(m_serverUrl) && m_inferenceConfig.baseUrl == "http://192.168.0.135:8001/infer_human")
                {
                    Debug.LogWarning("[SEGMENTATION] m_serverUrl is deprecated. Please use m_inferenceConfig instead.");
                }

                // Use legacy jpegQuality if it differs from config default
                if (m_jpegQuality != 80 && m_inferenceConfig.jpegQuality == 80)
                {
                    Debug.LogWarning($"[SEGMENTATION] Migrating legacy m_jpegQuality ({m_jpegQuality}) to m_inferenceConfig");
                    m_inferenceConfig.jpegQuality = m_jpegQuality;
                }

                // DETAILED DEBUG
                Debug.LogError("===== INFERENCE CONFIG VALUES =====");
                Debug.LogError($"m_inferenceConfig.mode = {m_inferenceConfig.mode} (should be 4)");
                Debug.LogError($"m_inferenceConfig.useServerConfig = {m_inferenceConfig.useServerConfig}");
                Debug.LogError($"m_inferenceConfig.baseUrl = '{m_inferenceConfig.baseUrl}'");
                Debug.LogError($"m_inferenceConfig.targetFPS = {m_inferenceConfig.targetFPS}");
                Debug.LogError($"ServerConfig.Instance.SegmentationUrl = '{ServerConfig.Instance.SegmentationUrl}'");
                Debug.LogError($"m_inferenceConfig.BuildUrl() = '{m_inferenceConfig.BuildUrl()}'");
                Debug.LogError("===================================");

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

            // CRITICAL: Wait for camera to start before beginning inference loop
            Debug.LogError("[CAMERA WAIT] Waiting for camera to start...");
            float cameraWaitStartTime = Time.time;
            float maxCameraWaitTime = 10f; // Maximum 10 seconds

            while (!m_cameraAccess.IsPlaying && (Time.time - cameraWaitStartTime) < maxCameraWaitTime)
            {
                Debug.LogWarning($"[CAMERA WAIT] Camera not playing yet, waiting... ({Time.time - cameraWaitStartTime:F1}s)");
                yield return new WaitForSeconds(0.5f);
            }

            if (m_cameraAccess.IsPlaying)
            {
                Debug.LogError($"[CAMERA WAIT] SUCCESS! Camera started after {Time.time - cameraWaitStartTime:F1}s");
            }
            else
            {
                Debug.LogError($"[CAMERA WAIT] TIMEOUT! Camera did not start after {maxCameraWaitTime}s");
                Debug.LogError("[CAMERA WAIT] Check Quest 3 Settings → Apps → PassthroughCameraSamples → Permissions → Camera");
                // Continue anyway - RunInference() will handle the camera not playing case
            }

            while (true)
            {
                // Add null check to prevent NullReferenceException
                while (m_uiMenuManager != null && m_uiMenuManager.IsPaused)
                {
                    yield return null;
                }
                yield return RunInference();
            }
        }

        private void OnDestroy()
        {
            m_engine.PeekOutput(0)?.CompleteAllPendingOperations();
            m_engine.PeekOutput(1)?.CompleteAllPendingOperations();
            m_engine.PeekOutput(2)?.CompleteAllPendingOperations();
            m_engine.Dispose();
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
            Debug.LogError($"[DEBUG] RunInference() CALLED! Camera playing: {m_cameraAccess != null && m_cameraAccess.IsPlaying}");

            if (!m_cameraAccess.IsPlaying)
            {
                Debug.LogError("[DEBUG] Camera NOT playing - yield break!");
                yield break;
            }

            Debug.LogError("[DEBUG] Camera IS playing - continuing...");

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

            Debug.LogError($"[DEBUG] m_useServerInference = {m_useServerInference}");

            if (m_useServerInference)
            {
                // SERVER INFERENCE PATH
                Debug.LogError("[INFERENCE] Using SERVER inference");
                yield return RunServerInference(targetTexture);
                // m_detections is now populated by RunServerInference
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
            if (!m_cameraAccess.IsPlaying || m_segmentationManager.m_spatialAnchor == null || !m_segmentationManager.m_spatialAnchor.IsTracked)
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
            public DetectionResultData detections;
            public int model_input_width;
            public int model_input_height;
            public int input_image_width;
            public int input_image_height;
            public float processing_time_ms;
            public float server_queue_ms;
            public float server_postprocess_ms;
            public double t_server_recv;
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
            public string mask_png_base64; // base64 PNG mask (YOLO-seg)
            public int mask_width;         // mask dimensions
            public int mask_height;
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
                Debug.Log($"[SEGMENTATION DOWNSAMPLE] {originalWidth}x{originalHeight} → {downsampledWidth}x{downsampledHeight} (factor={downsampleFactor})");
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

            // Use dedicated /segmentation endpoint from ServerConfig
            string serverUrl = ServerConfig.Instance.SegmentationUrl;
            Debug.Log($"[SEGMENTATION] Using endpoint: {serverUrl}");

            UnityWebRequest request = UnityWebRequest.Post(serverUrl, formData);

            // Add HTTP headers (including timing data from previous frame)
            request.SetRequestHeader("X-Scene-Name", "Segmentation");
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

            Debug.Log($"[SERVER SEND] >>> Sending frame {m_frameId} to: {serverUrl}");

            // 4. Send request (upload time will be calculated later based on network total and data size ratio)
            Debug.Log($"[LATENCY] requestStart={e2eStartTime:F4}");

            // Start the request
            UnityWebRequestAsyncOperation asyncOp = request.SendWebRequest();

            // Wait for response to complete
            yield return asyncOp;

            Debug.Log($"[SERVER SEND] <<< Request completed. Result: {request.result}");

            if (request.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError($"[SERVER] Inference failed: {request.error}");
                Debug.LogError($"[SERVER] Result type: {request.result}");
                Debug.LogError($"[SERVER] Response code: {request.responseCode}");
                Debug.LogError($"[SERVER] URL was: {serverUrl}");
                m_inferenceInProgress = false;  // Release lock on error
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

            // 6. Convert server detections to Unity format
            m_detections.Clear();

            if (response.detections != null && response.detections.detections != null)
            {
                // Calculate scale factors to convert from camera resolution to model input resolution
                float scaleX = response.model_input_width / (float)response.input_image_width;
                float scaleY = response.model_input_height / (float)response.input_image_height;

                Debug.Log($"[SERVER] Scale: {scaleX}x{scaleY} (model:{response.model_input_width}x{response.model_input_height}, image:{response.input_image_width}x{response.input_image_height})");

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

                    Debug.Log($"[SERVER] Detection: class={det.class_id} ({det.class_name}), conf={det.confidence:F2}, bbox={bboxUnity}, has_mask={!string.IsNullOrEmpty(det.mask_png_base64)}");
                }
            }

            Debug.Log($"[SERVER] Converted {m_detections.Count} detections, processing time: {response.processing_time_ms:F1}ms");

            // DEBUG: Check if mask data is present
            Debug.LogError($"[MASK CHECK] Total detections: {response.detections?.detections?.Length ?? 0}");
            if (response.detections != null && response.detections.detections != null)
            {
                for (int i = 0; i < response.detections.detections.Length; i++)
                {
                    var det = response.detections.detections[i];
                    Debug.LogError($"[MASK CHECK] Detection {i}: has_mask_field={det.mask_png_base64 != null}, is_empty={string.IsNullOrEmpty(det.mask_png_base64)}, mask_width={det.mask_width}, mask_height={det.mask_height}");
                }
            }

            // 7. Update metrics label with inference statistics
            // Compute average detection confidence
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

            // Get detection count
            int detectionCount = response.detections?.detections?.Length ?? 0;

            Debug.Log($"[LATENCY] UpdateMetrics: e2e={e2eMs:F1}ms, upload={uploadMs:F1}ms, server={serverProcMs:F1}ms, download={downloadMs:F1}ms, parse={parseMs:F1}ms");

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
                    downloadBytesCompressed,  // Compressed size
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

            // 8. Draw UI bounding boxes
            var cachedCameraPose = m_cameraAccess.GetCameraPose();
            m_uiInference.DrawUIBoxes(m_detections, m_inputSize, cachedCameraPose);
            Debug.Log($"[UI] Drew {m_detections.Count} bounding boxes on screen");

            // 9. Process and render segmentation masks
            if (response.detections != null && response.detections.detections != null)
            {
                int maskIndex = 0;
                foreach (var det in response.detections.detections)
                {
                    if (!string.IsNullOrEmpty(det.mask_png_base64))
                    {
                        Debug.LogError($"[MASK DEBUG] Processing mask {maskIndex}: {det.mask_width}x{det.mask_height}, base64 length={det.mask_png_base64.Length}");

                        try
                        {
                            Debug.LogError($"[MASK DEBUG] Step 1: Decoding base64...");
                            // Decode base64 to bytes
                            byte[] maskBytes = System.Convert.FromBase64String(det.mask_png_base64);
                            Debug.LogError($"[MASK DEBUG] Step 2: Decoded {maskBytes.Length} bytes");

                            // Create texture from PNG with RGBA support for alpha channel
                            Debug.LogError($"[MASK DEBUG] Step 3: Creating Texture2D with RGBA32 format...");
                            Texture2D maskTexture = new Texture2D(2, 2, TextureFormat.RGBA32, false);

                            Debug.LogError($"[MASK DEBUG] Step 4: Loading image into texture...");
                            if (maskTexture.LoadImage(maskBytes))
                            {
                                Debug.LogError($"[MASK DEBUG] Step 5: Successfully loaded! {maskTexture.width}x{maskTexture.height}, format={maskTexture.format}");

                                // Debug: Sample some pixels to verify alpha channel
                                Color centerPixel = maskTexture.GetPixel(maskTexture.width / 2, maskTexture.height / 2);
                                Color cornerPixel = maskTexture.GetPixel(0, 0);
                                Debug.LogError($"[MASK DEBUG] Center pixel: {centerPixel}, Corner pixel: {cornerPixel}");

                                // Pass mask to UI manager for rendering
                                Debug.LogError($"[MASK DEBUG] Step 6: Calling RenderMask with bbox={det.bbox_pixels[0]},{det.bbox_pixels[1]},{det.bbox_pixels[2]},{det.bbox_pixels[3]}");
                                m_uiInference.RenderMask(maskIndex, maskTexture, det.bbox_pixels, cachedCameraPose);
                                Debug.LogError($"[MASK DEBUG] Step 7: RenderMask completed");
                            }
                            else
                            {
                                Debug.LogError($"[MASK] Failed to load mask texture from PNG data");
                            }
                        }
                        catch (System.Exception e)
                        {
                            Debug.LogError($"[MASK] Exception: {e.Message}\nStack: {e.StackTrace}");
                        }

                        maskIndex++;
                    }
                }
            }

            // Store timing data for next frame's HTTP headers (to log in Excel)
            m_lastE2eMs = e2eMs;
            m_lastUploadMs = uploadMs;
            m_lastDownloadMs = downloadMs;
            m_lastParseMs = parseMs;
            m_lastUploadBytesUncompressed = uploadBytesUncompressed;  // Original RGB size
            m_lastUploadBytes = uploadBytesCompressed;  // Compressed JPEG size
            m_lastDownloadBytes = downloadBytesUncompressed;  // Uncompressed JSON size
            m_lastDownloadBytesCompressed = downloadBytesCompressed;  // Compressed network transfer size

            // 10. Release inference lock to allow next frame
            m_inferenceInProgress = false;
            Debug.Log($"[INFERENCE] Frame {m_frameId} complete, releasing lock");
        }
    }
}
