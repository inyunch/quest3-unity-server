// Copyright (c) Meta Platforms, Inc. and affiliates.

using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Meta.XR;
using Meta.XR.Samples;
using UnityEngine;
using UnityEngine.Networking;
using Newtonsoft.Json;
using PassthroughCameraSamples.Shared;

namespace PassthroughCameraSamples.PoseEstimation
{
    [MetaCodeSample("PassthroughCameraApiSamples-PoseEstimation")]
    public class PoseInferenceRunManager : MonoBehaviour
    {
        [SerializeField] private PassthroughCameraAccess m_cameraAccess;
        [SerializeField] private PassthroughCameraSamples.MultiObjectDetection.DetectionUiMenuManager m_uiMenuManager;
        [SerializeField] private PoseEstimationManager m_poseManager;

        [Header("UI display references")]
        [SerializeField] private PoseSkeletonUiManager m_uiPose;
        [SerializeField] private InferenceHUD m_inferenceHUD;
        [SerializeField] private SharedInferenceHUD m_sharedHUD;

        [Header("Server Inference (NEW)")]
        [SerializeField] private InferenceConfig m_inferenceConfig = new InferenceConfig
        {
            mode = InferenceMode.Both,
            targetFPS = 5f,
            jpegQuality = 80,
            includeMask = false,
            includeDepth = false
        };
        [SerializeField] private float m_minKeypointScore = 0.3f;

        [Header("Legacy Server Inference (DEPRECATED - use m_inferenceConfig instead)")]
        [SerializeField] private string m_serverUrl = "";  // Left for backward compatibility
        [SerializeField, Range(60, 100)] private int m_jpegQuality = 80;  // DEPRECATED

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
        private const int MAX_FRAME_TRACES = 100;  // Limit memory usage
        private const float FRAME_TIMEOUT_SECONDS = 5.0f;  // Mark as Failed after 5 seconds
        private float m_lastMetricsLogTime = 0f;
        private const float METRICS_LOG_INTERVAL = 10.0f;  // Log metrics every 10 seconds

        // UDP Transport (Phase 1 - Non-Blocking)
        private System.Net.Sockets.UdpClient m_udpClient;
        private const int UDP_PORT = 8002;
        [SerializeField] private bool m_useUDPTransport = true;  // FIXED: Enable UDP transport by default

        // Phase 3: Fixed cadence non-blocking send
        private float m_nextInferenceTime = 0f;  // Next time to send inference request
        private bool m_cameraReady = false;  // Camera initialization complete

        // NEW: Improved Freeze/Drop Metrics Tracking
        private float m_unityFrameTimeMs = 15.4f;  // Measured Unity frame time (default 65 FPS estimate)
        private int m_sessionFrameIndex = 0;       // Sequential index for logged frames
        private int m_cumulativeFreezeFrames = 0;  // Running total of freeze frames
        private int m_cumulativeDropped = 0;       // Running total of dropped frames
        private int m_cumulativeDisplayed = 0;     // Running total of displayed frames
        private int m_lastLoggedFrameId = -1;      // Last frame_id that was logged
        private int m_lastDisplayedFrameCount = -1; // Last Unity Time.frameCount when frame was displayed

        private IEnumerator Start()
        {
            // PHASE 1: Generate unique session ID for this recording session
            m_sessionId = System.Guid.NewGuid().ToString();
            Debug.Log($"[SESSION] Started session: {m_sessionId}");

            // Initialize UDP client if using UDP transport
            if (m_inferenceConfig.useServerConfig && m_useUDPTransport)
            {
                m_udpClient = new System.Net.Sockets.UdpClient();
                Debug.Log($"[UDP] Initialized UDP client for port {UDP_PORT}");
            }

            Debug.Log("[POSE INF] PoseInferenceRunManager started");

            // Reference checks
            Debug.Log($"[POSE REF] cameraAccess={m_cameraAccess != null}");
            Debug.Log($"[POSE REF] uiMenuManager={m_uiMenuManager != null}");
            Debug.Log($"[POSE REF] poseManager={m_poseManager != null}");
            Debug.Log($"[POSE REF] uiPose={m_uiPose != null}");

            // Migrate from legacy settings if needed
            if (!string.IsNullOrEmpty(m_serverUrl) && m_inferenceConfig.baseUrl == "http://192.168.0.135:8001/infer_human")
            {
                Debug.LogWarning("[POSE] m_serverUrl is deprecated. Please use m_inferenceConfig instead.");
            }

            if (m_jpegQuality != 80 && m_inferenceConfig.jpegQuality == 80)
            {
                Debug.LogWarning($"[POSE] Migrating legacy m_jpegQuality ({m_jpegQuality}) to m_inferenceConfig");
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

            // NEW: Measure actual Unity FPS on startup
            StartCoroutine(MeasureUnityFPS());

            // PHASE 3: Set camera ready flag and initialize timing
            m_cameraReady = true;
            m_nextInferenceTime = Time.time;

            // PHASE 3: Removed while(true) loop - now driven by Update() at fixed cadence
            Debug.Log("[PHASE 3 POSE] Start() complete - inference now driven by Update() at fixed cadence");
        }

        private void OnDestroy()
        {
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

        private IEnumerator RunInference()
        {
            if (!m_cameraAccess.IsPlaying)
            {
                yield break;
            }

            // ============================================================================
            // FPS THROTTLING (Part C)
            // ============================================================================
            float currentTime = Time.time;
            float targetInterval = m_inferenceConfig.GetInferenceInterval();
            float timeSinceLastInference = currentTime - m_lastInferenceTime;

            // Check if we should drop this frame (too soon since last inference)
            if (timeSinceLastInference < targetInterval)
            {
                // Drop frame - respecting target FPS
                // NOTE: This is FPS throttling, NOT the new dropped frame definition
                // (new definition: received from server but never displayed)
                if (m_sharedHUD != null)
                {
                    m_sharedHUD.ReportDroppedFrame();
                }
                yield break;
            }

            // PHASE 2: REMOVED m_inferenceInProgress check to allow parallel requests
            // Old serial code (REMOVED):
            // if (m_inferenceInProgress) {
            //     m_frozenFrames++;
            //     yield break;
            // }
            // m_inferenceInProgress = true;

            m_lastInferenceTime = currentTime;

            [DllImport("OVRPlugin", CallingConvention = CallingConvention.Cdecl)]
            static extern OVRPlugin.Result ovrp_GetNodePoseStateAtTime(double time, OVRPlugin.Node nodeId, out OVRPlugin.PoseStatef nodePoseState);
            if (!ovrp_GetNodePoseStateAtTime(OVRPlugin.GetTimeInSeconds(), OVRPlugin.Node.Head, out _).IsSuccess())
            {
                Debug.Log("ovrp_GetNodePoseStateAtTime failed, which means 'm_cameraAccess.GetCameraPose()' is not reliable, skipping.");
                // PHASE 2: No m_inferenceInProgress to clear (removed for parallel)
                yield break;
            }

            var cachedCameraPose = m_cameraAccess.GetCameraPose();

            // Update Capture data
            Texture targetTexture = m_cameraAccess.GetTexture();

            // Run server inference with UDP or HTTP
            if (m_useUDPTransport)
            {
                // NEW: UDP NON-BLOCKING PATH
                Debug.Log("[POSE UDP] Using UDP transport");

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
                Debug.Log("[POSE HTTP] Using HTTP transport (blocking)");
                yield return RunServerInference(targetTexture);
            }

            // PHASE 2: No need to mark m_inferenceInProgress = false (removed for parallel)
            // NOTE: m_totalFrames++ is now handled in SharedInferenceHUD.UpdateMetrics()

            // Checking if spatial anchor is tracked ensures skeleton is placed at correct world space positions
            if (!m_cameraAccess.IsPlaying || m_poseManager.m_spatialAnchor == null || !m_poseManager.m_spatialAnchor.IsTracked)
            {
                yield break;
            }

            // m_uiPose.DrawPoseSkeletons() is called from within RunServerInference after parsing
        }

        // ============================================================================
        // SERVER INFERENCE - JSON Response Classes (OLD nested format - RESTORED)
        // ============================================================================

        [System.Serializable]
        private class PoseServerResponse
        {
            // OLD format - restore compatibility with old renderer
            public DetectionResultData detections;  // Nested structure
            public SkeletonData skeleton;

            public int input_image_width;
            public int input_image_height;
            public float processing_time_ms;
            public float server_queue_ms;
            public float server_postprocess_ms;
            public double t_server_recv;
            public double t_server_send;
            public double server_receive_ts;
            public double server_process_start_ts;
            public double server_process_end_ts;
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

        [System.Serializable]
        private class SkeletonData
        {
            public List<PersonSkeleton> persons;
        }

        [System.Serializable]
        public class PersonSkeleton
        {
            public List<Keypoint> keypoints;
            public float[] bbox;
        }

        [System.Serializable]
        public class Keypoint
        {
            public string name;
            public float x;      // Normalized coordinates [0-1]
            public float y;
            public float score;  // confidence 0-1
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
                Debug.Log("[PHASE 3 POSE] Camera not playing, skipping inference");
                yield break;
            }

            // Get current frame texture
            Texture targetTexture = m_cameraAccess.GetTexture();

            // 1. Encode JPEG
            byte[] jpegData = EncodeTextureToJPEG(targetTexture);
            if (jpegData == null)
            {
                Debug.LogError("[PHASE 3 POSE] Failed to encode texture to JPEG");
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

            Debug.Log($"[PHASE 3 POSE] Frame {trace.frame_id} created, size={jpegData.Length} bytes");

            // 3. Send UDP (returns immediately - no blocking!)
            SendFrameUDP(trace, jpegData);

            // 4. Start async response listener (runs in background)
            StartCoroutine(ListenForResponseHTTP(trace.frame_id));

            Debug.Log($"[PHASE 3 POSE] Frame {trace.frame_id} sent via UDP");

            // PHASE 3: NO yield return - method completes immediately
            // Response will arrive asynchronously via ListenForResponseHTTP()
        }

        // ============================================================================
        // SERVER INFERENCE - Connection Test
        // ============================================================================

        private IEnumerator TestServerConnection()
        {
            string testUrl = "http://192.168.0.135:8001/";
            Debug.Log($"[POSE SERVER TEST] Connecting to {testUrl}");

            using (UnityWebRequest req = UnityWebRequest.Get(testUrl))
            {
                req.timeout = 5;
                yield return req.SendWebRequest();

                if (req.result == UnityWebRequest.Result.Success)
                {
                    Debug.Log($"[POSE SERVER TEST] ??Connection OK! Response: {req.downloadHandler.text}");
                }
                else
                {
                    Debug.LogError($"[POSE SERVER TEST] ??Connection FAILED: {req.error}");
                    Debug.LogError($"[POSE SERVER TEST] Result: {req.result}");
                    Debug.LogError($"[POSE SERVER TEST] Response Code: {req.responseCode}");
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
                    Debug.LogError("[POSE SERVER] Unsupported texture type for server inference");
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
                Debug.Log($"[POSE DOWNSAMPLE] {originalWidth}x{originalHeight} ??{downsampledWidth}x{downsampledHeight} (factor={downsampleFactor})");
            }

            // 2.5. Calculate uncompressed size (AFTER downsampling, using actual texture to encode)
            int uploadBytesUncompressed = textureToEncode.width * textureToEncode.height * 3; // RGB24 = 3 bytes per pixel

            // 3. Encode texture as JPEG (use configurable quality from InferenceConfig)
            int jpegQuality = m_inferenceConfig.jpegQuality;
            byte[] jpegBytes = textureToEncode.EncodeToJPG(jpegQuality);
            int uploadBytesCompressed = jpegBytes.Length;
            float compressionRatio = uploadBytesUncompressed > 0 ? (float)uploadBytesUncompressed / uploadBytesCompressed : 1f;
            Debug.Log($"[POSE SERVER] Encoded JPEG (quality={jpegQuality}): {uploadBytesCompressed} bytes ({textureToEncode.width}x{textureToEncode.height}), " +
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
            request.SetRequestHeader("X-Scene-Name", "PoseEstimation");
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

            // DELAYED TELEMETRY: Send previous frame's final state (Frame N-1's complete lifecycle)
            // PRIORITY 1: Dequeue from queue if available, otherwise fall back to legacy
            FrameTrace traceToSend = null;
            if (m_completedFramesQueue.Count > 0)
            {
                traceToSend = m_completedFramesQueue.Dequeue();
                Debug.Log($"[TELEMETRY QUEUE] Dequeued frame {traceToSend.frame_id} (state={traceToSend.state}) for delayed headers (remaining: {m_completedFramesQueue.Count})");
            }
            else if (m_lastCompletedTrace != null)
            {
                traceToSend = m_lastCompletedTrace;
                Debug.LogWarning($"[TELEMETRY QUEUE] Queue empty, using legacy m_lastCompletedTrace (frame {traceToSend.frame_id})");
            }

            if (traceToSend != null)
            {
                request.SetRequestHeader("X-Prev-Session-Id", traceToSend.session_id);  // PRIORITY 2
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
                request.SetRequestHeader("X-Prev-Freeze-Frames", traceToSend.freeze_frames.ToString());  // PRIORITY 3
            }

            Debug.Log($"[POSE SEND] Sending frame {m_frameId} to server...");

            // PHASE 1: Update trace with upload bytes
            trace.upload_bytes_uncompressed = uploadBytesUncompressed;
            trace.upload_bytes_compressed = uploadBytesCompressed;

            // PHASE 2: Track pending request
            lock (m_frameTracesLock)
            {
                m_pendingRequests[m_frameId] = request;
            }
            Debug.Log($"[PARALLEL] Frame {m_frameId} added to pending requests. Total pending: {m_pendingRequests.Count}");

            // 4. Send request (upload time will be calculated later based on network total and data size ratio)
            // Start the request
            UnityWebRequestAsyncOperation asyncOp = request.SendWebRequest();

            // Wait for response to complete
            yield return asyncOp;

            // PHASE 2: Remove from pending requests after completion
            lock (m_frameTracesLock)
            {
                m_pendingRequests.Remove(m_frameId);
            }
            Debug.Log($"[PARALLEL] Frame {m_frameId} removed from pending. Remaining: {m_pendingRequests.Count}");

            Debug.Log($"[POSE SERVER SEND] <<< Request completed. Result: {request.result}");

            // PHASE 1: Mark receive time
            float receiveTime = Time.realtimeSinceStartup;

            if (request.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError($"[POSE SERVER] Inference failed: {request.error}");
                Debug.LogError($"[POSE SERVER] Result type: {request.result}");
                Debug.LogError($"[POSE SERVER] Response code: {request.responseCode}");
                Debug.LogError($"[POSE SERVER] URL was: {serverUrl}");

                // PHASE 1: Mark trace as failed
                trace.MarkFailed($"{request.result}: {request.error}");
                Debug.Log($"[FRAME TRACE] Frame {m_frameId} failed: {trace}");

                // PRIORITY 1: Enqueue failed frame for telemetry
                m_completedFramesQueue.Enqueue(trace);
                Debug.Log($"[TELEMETRY QUEUE] Frame {trace.frame_id} FAILED ??queued (queue depth: {m_completedFramesQueue.Count})");

                // PHASE 2: Remove request from pending dictionary
                lock (m_frameTracesLock)
                {
                    m_pendingRequests.Remove(m_frameId);
                }

                yield break;
            }

            // PHASE 1: Mark trace as completed (convert to Unix timestamp)
            long receiveTimestamp = TimestampUtil.GetUnixTimestampMs();
            trace.MarkCompleted(receiveTimestamp);

            // 5. Parse JSON response and measure PARSE time
            float parseStartTime = Time.realtimeSinceStartup;

            string jsonResponse = request.downloadHandler.text;
            Debug.Log($"[POSE RECV] Response received, length={jsonResponse.Length}");
            Debug.Log($"[POSE RECV] First 200 chars: {jsonResponse.Substring(0, Mathf.Min(200, jsonResponse.Length))}");

            // WORKAROUND: Extract skeleton and detections BEFORE parsing full JSON
            // This bypasses the huge segmentation mask that causes parser to fail
            PoseServerResponse response = new PoseServerResponse();

            // Extract skeleton portion using string parsing
            Debug.Log($"[POSE JSON] Extracting skeleton from large JSON (size={jsonResponse.Length})...");
            string skeletonJson = ExtractJsonField(jsonResponse, "skeleton");
            if (!string.IsNullOrEmpty(skeletonJson))
            {
                // CRITICAL: Show actual skeleton JSON structure
                Debug.Log($"[SKELETON RAW] Full skeleton JSON ({skeletonJson.Length} chars): {skeletonJson}");
                Debug.Log($"[SKELETON RAW] First 300 chars: {skeletonJson.Substring(0, Mathf.Min(300, skeletonJson.Length))}");

                try
                {
                    response.skeleton = JsonConvert.DeserializeObject<SkeletonData>(skeletonJson);
                    Debug.Log($"[POSE JSON] Skeleton extracted successfully");
                }
                catch (System.Exception e)
                {
                    Debug.LogError($"[POSE JSON] Failed to parse extracted skeleton: {e.Message}");
                    Debug.LogError($"[POSE JSON] Error details: {e}");
                }
            }
            else
            {
                Debug.LogError($"[POSE JSON] Failed to extract skeleton field from JSON");
            }

            // Extract detections portion (OLD nested structure)
            string detectionsJson = ExtractJsonField(jsonResponse, "detections");
            if (!string.IsNullOrEmpty(detectionsJson))
            {
                try
                {
                    // OLD format: detections is nested DetectionResultData
                    response.detections = JsonConvert.DeserializeObject<DetectionResultData>(detectionsJson);
                    Debug.Log($"[POSE JSON] Detections extracted successfully (count={response.detections?.detections?.Length ?? 0})");
                }
                catch (System.Exception e)
                {
                    Debug.LogError($"[POSE JSON] Failed to parse extracted detections: {e.Message}");
                }
            }

            // Extract processing_time_ms
            string procTimeStr = ExtractSimpleJsonValue(jsonResponse, "processing_time_ms");
            if (!string.IsNullOrEmpty(procTimeStr) && float.TryParse(procTimeStr, out float procTime))
            {
                response.processing_time_ms = procTime;
                Debug.Log($"[POSE JSON] Processing time: {procTime:F1}ms");
            }

            // PHASE 6.1: Extract server timestamps for telemetry
            string serverRecvStr = ExtractSimpleJsonValue(jsonResponse, "t_server_recv");
            if (!string.IsNullOrEmpty(serverRecvStr) && double.TryParse(serverRecvStr, out double serverRecv))
            {
                response.t_server_recv = serverRecv;
                Debug.Log($"[POSE JSON] Server receive timestamp: {serverRecv}");
            }

            string serverProcessStartStr = ExtractSimpleJsonValue(jsonResponse, "server_process_start_ts");
            if (!string.IsNullOrEmpty(serverProcessStartStr) && double.TryParse(serverProcessStartStr, out double serverProcessStart))
            {
                response.server_process_start_ts = serverProcessStart;
                Debug.Log($"[POSE JSON] Server process start timestamp: {serverProcessStart}");
            }

            string serverSendStr = ExtractSimpleJsonValue(jsonResponse, "t_server_send");
            if (!string.IsNullOrEmpty(serverSendStr) && double.TryParse(serverSendStr, out double serverSend))
            {
                response.t_server_send = serverSend;
                Debug.Log($"[POSE JSON] Server send timestamp: {serverSend}");
            }

            if (response == null || response.skeleton == null)
            {
                Debug.LogError("[POSE SERVER] Failed to parse JSON response");
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

            Debug.Log($"[TIMING] E2E={e2eMs:F0}ms (upload={uploadMs:F0}ms queue={serverQueueMs:F0}ms server={serverProcMs:F0}ms post={serverPostprocessMs:F0}ms download={downloadMs:F0}ms parse={parseMs:F0}ms)");

            // Log both compressed and uncompressed sizes
            int downloadBytesUncompressed = System.Text.Encoding.UTF8.GetByteCount(request.downloadHandler.text);
            float downloadCompressionRatio = downloadBytesCompressed > 0 ? (float)downloadBytesUncompressed / downloadBytesCompressed : 1f;
            Debug.Log($"[BYTES] Upload={uploadBytesCompressed}B (compressed from {uploadBytesUncompressed}B), Download={downloadBytesCompressed}B (compressed), {downloadBytesUncompressed}B (uncompressed), {downloadCompressionRatio:F2}x compression");

            // Detailed parse verification logs
            Debug.Log($"[POSE PARSE] skeleton null={response.skeleton == null}");
            int personsCount = response.skeleton?.persons?.Count ?? 0;
            Debug.Log($"[POSE PARSE] persons count={personsCount}");
            Debug.Log($"[POSE PARSE] detections={response.detections?.detections?.Length ?? 0}");

            if (response.skeleton?.persons != null && response.skeleton.persons.Count > 0)
            {
                Debug.Log($"[POSE PARSE] persons array has {response.skeleton.persons.Count} person(s)");
                var p = response.skeleton.persons[0];
                Debug.Log($"[POSE PARSE] person 0: {p.keypoints?.Count ?? 0} keypoints");

                if (p.keypoints != null && p.keypoints.Count > 0)
                {
                    // Find nose keypoint
                    var nose = p.keypoints.Find(k => k.name == "nose");
                    if (nose != null)
                    {
                        Debug.Log($"[POSE PARSE] nose: x={nose.x:F3} y={nose.y:F3} score={nose.score:F2}");
                    }

                    // Log first 3 keypoints
                    for (int i = 0; i < Mathf.Min(3, p.keypoints.Count); i++)
                    {
                        var kp = p.keypoints[i];
                        Debug.Log($"[POSE PARSE] kp={kp.name} pos=({kp.x:F3},{kp.y:F3}) score={kp.score:F2}");
                    }
                }
            }
            else
            {
                Debug.LogWarning($"[POSE PARSE] skeleton.persons is NULL or empty!");
            }

            // PHASE 1: Update trace with detailed timing and response data
            trace.server_proc_ms = response.processing_time_ms;
            trace.server_receive_ts = (long)(response.t_server_recv * 1000);  // Convert to milliseconds
            trace.server_process_start_ts = (long)(response.server_process_start_ts * 1000);  // NEW: For queue_wait_ms
            trace.server_send_ts = (long)(response.t_server_send * 1000);     // Convert to milliseconds
            trace.upload_ms = uploadMs;
            trace.download_ms = downloadMs;
            trace.parse_ms = parseMs;
            trace.download_bytes_uncompressed = downloadBytesUncompressed;
            trace.download_bytes_compressed = downloadBytesCompressed;
            trace.response = response;  // Cache the response
            trace.detection_count = response.skeleton?.persons?.Count ?? 0;

            // PHASE 3: NO immediate display - deferred to Update() loop
            // Display will happen in TryDisplayNewestFrame() which runs every Update()
            // This allows us to implement "display only newest" logic
            Debug.Log($"[FRAME TRACE] Frame {m_frameId} completed (state=Completed). Display deferred to Update(). {trace}");

            // OLD Phase 1 code (REMOVED for Phase 3):
            // m_uiPose.DrawPoseSkeletons(...)
            // trace.MarkDisplayed(...)
            // Now handled in TryDisplayNewestFrame()

            // 7. Update HUD with inference metrics
            // Compute average detection confidence (OLD nested structure)
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

            // Compute average keypoint confidence
            float keypointAvgConf = 0f;
            if (response.skeleton != null && response.skeleton.persons != null && response.skeleton.persons.Count > 0)
            {
                List<float> allScores = new List<float>();
                foreach (var person in response.skeleton.persons)
                {
                    if (person != null && person.keypoints != null)
                    {
                        foreach (var kp in person.keypoints)
                        {
                            if (kp.score > 0f)
                            {
                                allScores.Add(kp.score);
                            }
                        }
                    }
                }
                if (allScores.Count > 0)
                {
                    float sum = 0f;
                    foreach (var score in allScores)
                    {
                        sum += score;
                    }
                    keypointAvgConf = sum / allScores.Count;
                }
            }

            // Get detection count (number of persons)
            int detectionCount = response.skeleton?.persons?.Count ?? 0;

            // Update legacy HUD
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
                    avgConfidence,
                    keypointAvgConf
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
                    keypointAvgConf
                );
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
        }

        // ============================================================================
        // JSON FIELD EXTRACTION - Bypasses large segmentation mask
        // ============================================================================

        /// <summary>
        /// Extracts a specific field from JSON without parsing the entire document.
        /// This bypasses the huge segmentation mask that causes full parsing to fail.
        /// </summary>
        private string ExtractJsonField(string json, string fieldName)
        {
            string searchPattern = $"\"{fieldName}\":";
            int fieldStart = json.IndexOf(searchPattern);

            if (fieldStart < 0)
            {
                Debug.LogError($"[POSE JSON] Field '{fieldName}' not found in JSON");
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
                Debug.LogError($"[POSE JSON] Unexpected end of JSON after field '{fieldName}'");
                return null;
            }

            // Determine if value is object {...} or array [...]
            char startChar = json[valueStart];
            char endChar;
            if (startChar == '{')
            {
                endChar = '}';
            }
            else if (startChar == '[')
            {
                endChar = ']';
            }
            else
            {
                Debug.LogError($"[POSE JSON] Field '{fieldName}' does not start with {{ or [");
                return null;
            }

            // Find matching closing bracket/brace
            int depth = 0;
            int valueEnd = valueStart;
            bool inString = false;
            bool escaped = false;

            for (int i = valueStart; i < json.Length; i++)
            {
                char c = json[i];

                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (c == '\\')
                {
                    escaped = true;
                    continue;
                }

                if (c == '"')
                {
                    inString = !inString;
                    continue;
                }

                if (!inString)
                {
                    if (c == startChar)
                    {
                        depth++;
                    }
                    else if (c == endChar)
                    {
                        depth--;
                        if (depth == 0)
                        {
                            valueEnd = i + 1;
                            break;
                        }
                    }
                }
            }

            if (depth != 0)
            {
                Debug.LogError($"[POSE JSON] Mismatched brackets for field '{fieldName}'");
                return null;
            }

            string extracted = json.Substring(valueStart, valueEnd - valueStart);
            Debug.Log($"[POSE JSON] Extracted '{fieldName}': {extracted.Length} chars");
            return extracted;
        }

        /// <summary>
        /// Extracts a simple numeric or string value from JSON.
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

        // ============================================================================
        // PHASE 3: DEFERRED DISPLAY LOGIC (Parallel Processing)
        // ============================================================================

        /// <summary>
        /// Update() is called every frame - we use it to display the newest completed frame
        /// This decouples response receipt from display, enabling "display only newest" logic
        /// </summary>
        private void Update()
        {
            // PHASE 3: Fixed cadence inference triggering (UDP mode only)
            if (m_inferenceConfig.useServerConfig && m_useUDPTransport && m_cameraReady)
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

                    Debug.Log($"[PHASE 3 POSE] Triggered inference at fixed cadence (interval={targetInterval * 1000f:F0}ms)");
                }
            }

            // PRIORITY 3: Increment freeze counter BEFORE trying to display
            m_framesSinceLastDisplay++;

            TryDisplayNewestFrame();

            // PHASE 6: Cleanup and optimization
            CleanupOldFrames();
            CheckFrameTimeouts();

            // Log performance metrics periodically
            if (Time.realtimeSinceStartup - m_lastMetricsLogTime > METRICS_LOG_INTERVAL)
            {
                Debug.Log($"[PERFORMANCE METRICS] {GetPerformanceMetrics()}");
                m_lastMetricsLogTime = Time.realtimeSinceStartup;
            }
        }

        /// <summary>
        /// Find the newest completed frame and display it, marking older frames as dropped
        /// </summary>
        private void TryDisplayNewestFrame()
        {
            lock (m_frameTracesLock)
            {
                // Find all completed frames (responses received but not yet displayed/dropped)
                var completedFrames = new List<FrameTrace>();
                foreach (var trace in m_frameTraces.Values)
                {
                    if (trace.state == FrameState.Completed)
                    {
                        completedFrames.Add(trace);
                    }
                }

                // If no completed frames, nothing to do
                if (completedFrames.Count == 0)
                {
                    return;
                }

                // Sort by frame_id descending to get newest first
                completedFrames.Sort((a, b) => b.frame_id.CompareTo(a.frame_id));

                // Get the newest completed frame
                FrameTrace newest = completedFrames[0];

                long currentTimestamp = TimestampUtil.GetUnixTimestampMs();

                // PRIORITY 1: Mark ALL older frames as dropped (including frames older than m_lastDisplayedFrameId)
                // This handles out-of-order completion (e.g., Frame 4 arrives before Frame 1)
                for (int i = 1; i < completedFrames.Count; i++)
                {
                    var olderFrame = completedFrames[i];
                    olderFrame.MarkDropped(currentTimestamp, $"superseded_by_newer_{newest.frame_id}");
                    m_droppedFrames++;

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

                // PRIORITY 1: Enqueue displayed frame for telemetry
                m_completedFramesQueue.Enqueue(newest);
                Debug.Log($"[TELEMETRY QUEUE] Frame {newest.frame_id} DISPLAYED ??queued (queue depth: {m_completedFramesQueue.Count})");

                // LEGACY COMPATIBILITY: Also set m_lastCompletedTrace
                m_lastCompletedTrace = newest;

                Debug.Log($"[PARALLEL DISPLAY] Frame {newest.frame_id} DISPLAYED. Dropped {completedFrames.Count - 1} older frames. {newest}");
            }
        }

        /// <summary>
        /// Actually display a frame (render skeletons on UI)
        /// </summary>
        private void DisplayFrame(FrameTrace trace)
        {
            // Get cached camera pose
            var cachedCameraPose = m_cameraAccess.GetCameraPose();

            // Extract response from trace
            PoseServerResponse response = trace.response as PoseServerResponse;
            if (response == null)
            {
                Debug.LogError($"[PARALLEL DISPLAY] Frame {trace.frame_id} has no response data!");
                m_uiPose.ClearSkeletons();
                return;
            }

            // Draw pose skeletons (OLD rendering logic - unchanged)
            if (response.skeleton != null && response.skeleton.persons != null && response.skeleton.persons.Count > 0)
            {
                Debug.Log($"[PARALLEL DISPLAY] Displaying frame {trace.frame_id} with {response.skeleton.persons.Count} person(s)");
                m_uiPose.DrawPoseSkeletons(response.skeleton.persons.ToArray(), cachedCameraPose, m_minKeypointScore);
            }
            else
            {
                Debug.Log($"[PARALLEL DISPLAY] Frame {trace.frame_id} has no pose data, clearing skeletons");
                m_uiPose.ClearSkeletons();
            }

            // Update HUD with metrics from this frame
            float e2eMs = trace.upload_ms + trace.server_proc_ms + trace.download_ms + trace.parse_ms;
            float uploadMs = trace.upload_ms > 0 ? trace.upload_ms : 0f;
            float serverProcMs = trace.server_proc_ms > 0 ? trace.server_proc_ms : 0f;
            float downloadMs = trace.download_ms > 0 ? trace.download_ms : 0f;
            float parseMs = trace.parse_ms > 0 ? trace.parse_ms : 0f;

            // detectionCount - using nested structure
            int detectionCount = response.detections?.detections?.Length ?? 0;
            int skeletonPersonCount = response.skeleton?.persons?.Count ?? 0;

            // Average confidence - using OLD nested structure
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

            // Keypoint average confidence
            float keypointAvgConf = 0f;
            if (response.skeleton != null && response.skeleton.persons != null && response.skeleton.persons.Count > 0)
            {
                List<float> allScores = new List<float>();
                foreach (var person in response.skeleton.persons)
                {
                    if (person != null && person.keypoints != null)
                    {
                        foreach (var kp in person.keypoints)
                        {
                            if (kp.score > 0f)
                            {
                                allScores.Add(kp.score);
                            }
                        }
                    }
                }
                if (allScores.Count > 0)
                {
                    float sum = 0f;
                    foreach (var score in allScores)
                    {
                        sum += score;
                    }
                    keypointAvgConf = sum / allScores.Count;
                }
            }

            int uploadBytes = trace.upload_bytes_compressed > 0 ? trace.upload_bytes_compressed : 0;
            int downloadBytes = trace.download_bytes_uncompressed > 0 ? trace.download_bytes_uncompressed : 0;
            int downloadBytesCompressed = trace.download_bytes_compressed > 0 ? trace.download_bytes_compressed : 0;

            Debug.Log($"[HUD UPDATE] Frame {trace.frame_id}: e2e={e2eMs:F0}ms, upload={uploadMs:F0}ms, server={serverProcMs:F0}ms, download={downloadMs:F0}ms, parse={parseMs:F0}ms, count={skeletonPersonCount}");

            // Update HUD
            if (m_inferenceHUD != null)
            {
                m_inferenceHUD.UpdateHUD(
                    e2eMs,
                    uploadMs,
                    serverProcMs,
                    downloadMs,
                    parseMs,
                    uploadBytes,
                    downloadBytes,
                    downloadBytesCompressed,
                    skeletonPersonCount,
                    avgConfidence,
                    keypointAvgConf
                );
            }

            // Update SharedInferenceHUD
            if (m_sharedHUD != null)
            {
                m_sharedHUD.UpdateMetrics(
                    e2eMs,
                    uploadMs,
                    serverProcMs,
                    downloadMs,
                    parseMs,
                    uploadBytes,
                    downloadBytes,
                    downloadBytesCompressed,
                    skeletonPersonCount,
                    avgConfidence,
                    keypointAvgConf
                );
            }
        }

        // ============================================================================
        // PHASE 6: CLEANUP AND OPTIMIZATION
        // ============================================================================

        /// <summary>
        /// Remove old frames from memory to prevent unbounded growth
        /// </summary>
        private void CleanupOldFrames()
        {
            lock (m_frameTracesLock)
            {
                if (m_frameTraces.Count <= MAX_FRAME_TRACES)
                {
                    return;  // Within limit, no cleanup needed
                }

                // Find frames that are in final states (Displayed, Dropped, Failed)
                var completedFrames = new List<int>();
                foreach (var kvp in m_frameTraces)
                {
                    var state = kvp.Value.state;
                    if (state == FrameState.Displayed || state == FrameState.Dropped || state == FrameState.Failed)
                    {
                        completedFrames.Add(kvp.Key);
                    }
                }

                // Sort by frame_id and remove oldest
                completedFrames.Sort();
                int toRemove = m_frameTraces.Count - MAX_FRAME_TRACES;

                for (int i = 0; i < Mathf.Min(toRemove, completedFrames.Count); i++)
                {
                    int frameId = completedFrames[i];
                    m_frameTraces.Remove(frameId);
                    Debug.Log($"[CLEANUP] Removed old frame {frameId} from trace dictionary");
                }

                if (toRemove > 0)
                {
                    Debug.Log($"[CLEANUP] Cleaned up {Mathf.Min(toRemove, completedFrames.Count)} old frames. Remaining: {m_frameTraces.Count}");
                }
            }
        }

        /// <summary>
        /// Check for frames that have been pending too long and mark them as failed
        /// </summary>
        private void CheckFrameTimeouts()
        {
            long currentTimeMs = TimestampUtil.GetUnixTimestampMs();

            lock (m_frameTracesLock)
            {
                foreach (var trace in m_frameTraces.Values)
                {
                    // Only check pending frames
                    if (trace.state != FrameState.Pending)
                    {
                        continue;
                    }

                    // Check if timeout exceeded
                    long timeSinceSendMs = currentTimeMs - trace.unity_send_ts;
                    float timeSinceSendSec = timeSinceSendMs / 1000f;
                    if (timeSinceSendSec > FRAME_TIMEOUT_SECONDS)
                    {
                        trace.MarkFailed($"Timeout after {timeSinceSendSec:F1}s (limit: {FRAME_TIMEOUT_SECONDS}s)");
                        Debug.LogWarning($"[TIMEOUT] Frame {trace.frame_id} timed out after {timeSinceSendSec:F1}s");

                        // PRIORITY 1: Enqueue timeout failed frame for telemetry
                        m_completedFramesQueue.Enqueue(trace);
                        Debug.Log($"[TELEMETRY QUEUE] Frame {trace.frame_id} TIMEOUT ??queued (queue depth: {m_completedFramesQueue.Count})");

                        // Remove from pending requests if still there
                        if (m_pendingRequests.ContainsKey(trace.frame_id))
                        {
                            var request = m_pendingRequests[trace.frame_id];
                            request.Abort();  // Cancel the network request
                            m_pendingRequests.Remove(trace.frame_id);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Get current performance metrics for debugging
        /// </summary>
        private string GetPerformanceMetrics()
        {
            lock (m_frameTracesLock)
            {
                int pendingCount = 0;
                int completedCount = 0;
                int displayedCount = 0;
                int droppedCount = 0;
                int failedCount = 0;

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
                                  $"final_state={prevTrace.state}");
                    }
                    else if (!isFinalState)
                    {
                        Debug.Log($"[UNITY TELEMETRY] Frame {prevFrameId} not final yet (state={prevTrace.state})");
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
            // Build response polling URL - use /latest endpoint
            string serverUrl = m_inferenceConfig.BuildUrl();
            System.Uri uri = new System.Uri(serverUrl);
            string responseUrl = $"http://{uri.Host}:{uri.Port}/response/{m_sessionId}/latest";

            float timeout = 5f;  // 5 second timeout
            float elapsed = 0f;
            float pollInterval = 0.1f;  // Poll every 100ms
            int lastReceivedFrameId = -1;

            // Get trace reference for the expected frame
            FrameTrace trace = null;
            lock (m_frameTracesLock)
            {
                if (!m_frameTraces.TryGetValue(expectedFrameId, out trace))
                {
                    Debug.LogWarning($"[LATEST POLL] Frame {expectedFrameId} not found in traces!");
                    yield break;
                }
            }

            Debug.Log($"[LATEST POLL] Starting polling for session {m_sessionId} (expecting frame {expectedFrameId})");

            // Poll until result available or timeout
            while (elapsed < timeout)
            {
                using (UnityWebRequest request = UnityWebRequest.Get(responseUrl))
                {
                    yield return request.SendWebRequest();

                    if (request.result == UnityWebRequest.Result.Success)
                    {
                        string jsonResponse = request.downloadHandler.text;

                        // Extract frame_id from response
                        int receivedFrameId = ExtractFrameIdFromJson(jsonResponse);

                        if (receivedFrameId < 0)
                        {
                            Debug.LogWarning($"[LATEST POLL] Could not extract frame_id from response");
                            yield return new WaitForSeconds(pollInterval);
                            elapsed += pollInterval;
                            continue;
                        }

                        // Only process if this is a NEW result we haven't seen yet
                        if (receivedFrameId > lastReceivedFrameId)
                        {
                            lastReceivedFrameId = receivedFrameId;
                            long receiveTs = TimestampUtil.GetUnixTimestampMs();

                            // Find the trace for the received frame
                            FrameTrace receivedTrace = null;
                            lock (m_frameTracesLock)
                            {
                                m_frameTraces.TryGetValue(receivedFrameId, out receivedTrace);
                            }

                            if (receivedTrace != null)
                            {
                                // Process response for the frame that actually completed
                                ProcessServerResponse(receivedTrace, jsonResponse, receiveTs);
                                Debug.Log($"[LATEST POLL] ??frame={receivedFrameId} received after {elapsed:F2}s");

                                // If this is the frame we were waiting for, we're done
                                if (receivedFrameId >= expectedFrameId)
                                {
                                    yield break;
                                }
                            }
                            else
                            {
                                Debug.LogWarning($"[LATEST POLL] Received frame {receivedFrameId} but trace not found");
                            }
                        }
                        // else: Same frame as before, server hasn't completed new frame yet
                    }
                    else if (request.responseCode == 404)
                    {
                        // No results yet - this is expected during first few frames
                        if (elapsed > 1.0f)
                        {
                            Debug.LogWarning($"[LATEST POLL] No results available yet (404) after {elapsed:F1}s");
                        }
                    }
                    else
                    {
                        // Actual error (not just "not ready")
                        Debug.LogError($"[LATEST POLL] Error polling: {request.error}");
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
            Debug.LogWarning($"[LATEST POLL] Timeout waiting for frame {expectedFrameId} after {timeout}s");
            trace.MarkFailed("Response timeout (5s)");

            // Enqueue timeout failed frame for telemetry
            lock (m_frameTracesLock)
            {
                m_completedFramesQueue.Enqueue(trace);
            }
            Debug.Log($"[TELEMETRY QUEUE] Frame {trace.frame_id} TIMEOUT ??queued (queue depth: {m_completedFramesQueue.Count})");
        }

        /// <summary>
        /// Extract frame_id from JSON response (for /latest endpoint)
        /// </summary>
        private int ExtractFrameIdFromJson(string json)
        {
            try
            {
                // Simple regex to extract "frame_id":123
                var match = System.Text.RegularExpressions.Regex.Match(json, @"""frame_id""\s*:\s*(\d+)");
                if (match.Success)
                {
                    return int.Parse(match.Groups[1].Value);
                }
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[LATEST POLL] Error extracting frame_id: {e.Message}");
            }
            return -1;
        }

        /// <summary>
        /// Process server response JSON and update frame trace
        /// (Extracted to be reusable by both HTTP and UDP paths)
        /// </summary>
        private void ProcessServerResponse(FrameTrace trace, string jsonResponse, long receiveTs)
        {
            // Parse JSON response using Newtonsoft.Json (already used in this file)
            Debug.Log($"[UDP RESPONSE] Response length: {jsonResponse.Length} bytes");

            // Use the existing response parsing logic
            var response = JsonConvert.DeserializeObject<PoseServerResponse>(jsonResponse);

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

            // Store server timestamps
            trace.server_receive_ts = (long)(response.t_server_recv * 1000);
            trace.server_process_start_ts = (long)(response.server_process_start_ts * 1000);
            trace.server_send_ts = (long)(response.t_server_send * 1000);
            trace.server_proc_ms = response.processing_time_ms;

            // Calculate latency breakdown (METHOD B: Residual estimation)
            // E2E latency (Unity-only, avoids clock skew)
            float e2eMs = 0f;
            if (receiveTs > 0 && trace.unity_send_ts > 0)
            {
                e2eMs = (float)(receiveTs - trace.unity_send_ts);
            }

            // Server-side metrics (from response, server-only timing)
            float serverProcMs = response.processing_time_ms;
            float queueWaitMs = (float)((response.server_process_start_ts - response.t_server_recv) * 1000);

            // Network total time (residual: E2E - server_time)
            float networkTotalMs = Mathf.Max(0f, e2eMs - serverProcMs - queueWaitMs);

            // Split network time by compressed data size ratio (METHOD B)
            int uploadBytesCompressed = trace.upload_bytes_compressed > 0 ? trace.upload_bytes_compressed : 10000;
            int downloadBytesUncompressed = jsonResponse.Length;

            int totalBytes = uploadBytesCompressed + downloadBytesUncompressed;
            float uploadRatio = totalBytes > 0 ? (float)uploadBytesCompressed / totalBytes : 0.5f;

            float uploadMs = networkTotalMs * uploadRatio;
            float downloadMs = networkTotalMs * (1.0f - uploadRatio);
            float parseMs = 5.0f;  // Small estimate

            Debug.Log($"[LATENCY METHOD B] Frame {trace.frame_id}: E2E={e2eMs:F1}ms, " +
                      $"network_total={networkTotalMs:F1}ms (upload={uploadMs:F1}ms, download={downloadMs:F1}ms), " +
                      $"server={serverProcMs:F1}ms, queue={queueWaitMs:F1}ms, " +
                      $"upload_ratio={uploadRatio:F2} ({uploadBytesCompressed}/{totalBytes} bytes)");

            // Store in trace for later HUD update
            trace.e2e_ms = e2eMs;
            trace.upload_ms = uploadMs;
            trace.download_ms = downloadMs;
            trace.parse_ms = parseMs;
            trace.download_bytes_uncompressed = downloadBytesUncompressed;
            trace.download_bytes_compressed = downloadBytesUncompressed;

            // Extract detection metrics from response (for Excel telemetry)
            int detectionCount = response.detections?.detections?.Length ?? 0;
            float avgConfidence = 0f;
            float keypointAvgConf = 0f;

            // Calculate average detection confidence
            if (response.detections != null && response.detections.detections != null && response.detections.detections.Length > 0)
            {
                float sum = 0f;
                foreach (var det in response.detections.detections)
                {
                    sum += det.confidence;
                }
                avgConfidence = sum / response.detections.detections.Length;
            }

            // Calculate average keypoint confidence from pose
            if (response.skeleton != null && response.skeleton.persons != null && response.skeleton.persons.Count > 0)
            {
                int totalKeypoints = 0;
                float totalKeypointConf = 0f;

                foreach (var person in response.skeleton.persons)
                {
                    if (person.keypoints != null)
                    {
                        foreach (var kp in person.keypoints)
                        {
                            totalKeypointConf += kp.score;
                            totalKeypoints++;
                        }
                    }
                }

                if (totalKeypoints > 0)
                {
                    keypointAvgConf = totalKeypointConf / totalKeypoints;
                }
            }

            trace.detection_count = detectionCount;
            trace.avg_confidence = avgConfidence;
            // Note: keypoint_avg_conf field doesn't exist in FrameTrace yet, would need to be added

            Debug.Log($"[TIMING CALC] Frame {trace.frame_id}: e2e={e2eMs:F0}ms, upload={uploadMs:F0}ms, server={serverProcMs:F0}ms, download={downloadMs:F0}ms");
            Debug.Log($"[METRICS] Frame {trace.frame_id}: detection_count={detectionCount}, avg_conf={avgConfidence:F2}, keypoint_avg_conf={keypointAvgConf:F2}");

            // Store response
            trace.response = response;

            // Mark as completed
            trace.MarkCompleted(receiveTs);
            Debug.LogWarning($"[TELEMETRY DEBUG] MarkCompleted frame {trace.frame_id}, state={trace.state}");

            // Enqueue for delayed telemetry
            lock (m_frameTracesLock)
            {
                m_completedFramesQueue.Enqueue(trace);
            }
            Debug.Log($"[TELEMETRY QUEUE] Frame {trace.frame_id} COMPLETED ??queued (queue depth: {m_completedFramesQueue.Count})");

            Debug.Log($"[UDP RESPONSE] Frame {trace.frame_id} processed successfully");
        }

        /// <summary>
        /// Build telemetry JSON for a specific FrameTrace (N+1 delayed telemetry).
        /// </summary>
        private string BuildTelemetryJson(FrameTrace trace)
        {
            // Build telemetry dictionary and serialize with Newtonsoft.Json
            var telemetry = new
            {
                scene = "PoseEstimation",
                session_id = trace.session_id,
                frame_id = trace.frame_id,
                mode = "both",  // CRITICAL: Server reads this to determine inference type (pose + detection)

                // Unity-side timing
                unity_send_ts = trace.unity_send_ts,
                unity_receive_ts = trace.unity_receive_ts,
                unity_display_ts = trace.unity_display_ts ?? 0,
                unity_drop_ts = trace.unity_drop_ts ?? 0,

                // Server-side timing
                server_receive_ts = trace.server_receive_ts,
                server_process_start_ts = trace.server_process_start_ts,
                server_send_ts = trace.server_send_ts,

                // Latency breakdown
                latency_ms = trace.e2e_ms,
                upload_ms = trace.upload_ms,
                queue_wait_ms = trace.server_process_start_ts - trace.server_receive_ts,
                server_proc_ms = trace.server_proc_ms,
                download_ms = trace.download_ms,
                parse_ms = trace.parse_ms,
                udp_send_ms = trace.udp_send_ms,

                // NEW: Improved Freeze Metrics
                freeze_duration_ms = trace.freeze_duration_ms,
                cumulative_freeze_frames = trace.cumulative_freeze_frames,
                freeze_ratio = trace.freeze_ratio,

                // NEW: Improved Drop Metrics
                frame_gap = trace.frame_gap,
                cumulative_dropped = trace.cumulative_dropped,
                cumulative_displayed = trace.cumulative_displayed,
                drop_rate = trace.drop_rate,

                // NEW: Session Context
                session_frame_index = trace.session_frame_index,

                // Payload sizes
                upload_bytes_uncompressed = trace.upload_bytes_uncompressed,
                upload_bytes_compressed = trace.upload_bytes_compressed,
                download_bytes_uncompressed = trace.download_bytes_uncompressed,
                download_bytes_compressed = trace.download_bytes_compressed,

                // State and results
                final_state = trace.state.ToString(),
                drop_reason = trace.drop_reason ?? "",
                error_reason = trace.error_reason ?? "",
                detection_count = trace.detection_count ?? 0,
                avg_confidence = trace.avg_confidence,
                freeze_frames_per_frame = trace.freeze_frames,
                target_fps = m_inferenceConfig.targetFPS
            };

            return JsonConvert.SerializeObject(telemetry);
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
                Debug.Log($"[POSE DOWNSAMPLE] {tex2D.width}x{tex2D.height} ??{downsampledWidth}x{downsampledHeight} (factor={downsampleFactor})");
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

    }
}






