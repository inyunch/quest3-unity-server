// Copyright (c) Meta Platforms, Inc. and affiliates.

using System.Collections;
using System.Collections.Generic;
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

        private IEnumerator Start()
        {
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

            while (true)
            {
                while (m_uiMenuManager.IsPaused)
                {
                    yield return null;
                }
                yield return RunInference();
            }
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
                // NOTE: m_droppedFrames++ is now handled in SharedInferenceHUD.ReportDroppedFrame()
                if (m_sharedHUD != null)
                {
                    m_sharedHUD.ReportDroppedFrame();
                }
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
                yield break;
            }

            // Mark inference as in progress
            m_inferenceInProgress = true;
            m_lastInferenceTime = currentTime;

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

            // Run server inference
            yield return RunServerInference(targetTexture);

            // Mark inference as complete
            m_inferenceInProgress = false;
            // NOTE: m_totalFrames++ is now handled in SharedInferenceHUD.UpdateMetrics()

            // Checking if spatial anchor is tracked ensures skeleton is placed at correct world space positions
            if (!m_cameraAccess.IsPlaying || m_poseManager.m_spatialAnchor == null || !m_poseManager.m_spatialAnchor.IsTracked)
            {
                yield break;
            }

            // m_uiPose.DrawPoseSkeletons() is called from within RunServerInference after parsing
        }

        // ============================================================================
        // SERVER INFERENCE - JSON Response Classes
        // ============================================================================

        [System.Serializable]
        private class PoseServerResponse
        {
            public DetectionResultData detections;
            public SkeletonData skeleton;
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
        }

        [System.Serializable]
        private class SkeletonData
        {
            public List<PersonSkeleton> persons;  // persons is an ARRAY of person objects, not an int!
        }

        [System.Serializable]
        public class PersonSkeleton
        {
            public List<Keypoint> keypoints;  // 17 keypoints in COCO order
            public float[] bbox;              // [x1_norm, y1_norm, x2_norm, y2_norm]
        }

        [System.Serializable]
        public class Keypoint
        {
            public string name;
            public float x;      // normalized 0-1
            public float y;      // normalized 0-1
            public float score;  // confidence 0-1
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
                Debug.Log($"[POSE DOWNSAMPLE] {originalWidth}x{originalHeight} → {downsampledWidth}x{downsampledHeight} (factor={downsampleFactor})");
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

            Debug.Log($"[POSE SEND] Sending frame {m_frameId} to server...");

            // 4. Send request (upload time will be calculated later based on network total and data size ratio)
            // Start the request
            UnityWebRequestAsyncOperation asyncOp = request.SendWebRequest();

            // Wait for response to complete
            yield return asyncOp;

            Debug.Log($"[POSE SERVER SEND] <<< Request completed. Result: {request.result}");

            if (request.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError($"[POSE SERVER] Inference failed: {request.error}");
                Debug.LogError($"[POSE SERVER] Result type: {request.result}");
                Debug.LogError($"[POSE SERVER] Response code: {request.responseCode}");
                Debug.LogError($"[POSE SERVER] URL was: {serverUrl}");
                m_inferenceInProgress = false;  // Release lock on error
                yield break;
            }

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

            // Extract detections portion
            string detectionsJson = ExtractJsonField(jsonResponse, "detections");
            if (!string.IsNullOrEmpty(detectionsJson))
            {
                // Also log detections for comparison
                Debug.Log($"[DETECTIONS RAW] First 200 chars: {detectionsJson.Substring(0, Mathf.Min(200, detectionsJson.Length))}");

                try
                {
                    response.detections = JsonConvert.DeserializeObject<DetectionResultData>(detectionsJson);
                    Debug.Log($"[POSE JSON] Detections extracted successfully");
                }
                catch (System.Exception e)
                {
                    Debug.LogError($"[POSE JSON] Failed to parse extracted detections: {e.Message}");
                    Debug.LogError($"[POSE JSON] Error details: {e}");
                }
            }

            // Extract processing_time_ms
            string procTimeStr = ExtractSimpleJsonValue(jsonResponse, "processing_time_ms");
            if (!string.IsNullOrEmpty(procTimeStr) && float.TryParse(procTimeStr, out float procTime))
            {
                response.processing_time_ms = procTime;
                Debug.Log($"[POSE JSON] Processing time: {procTime:F1}ms");
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

            // 6. Draw pose skeletons on UI
            var cachedCameraPose = m_cameraAccess.GetCameraPose();

            if (response.skeleton != null && response.skeleton.persons != null && response.skeleton.persons.Count > 0)
            {
                Debug.Log($"[POSE SERVER] Received {response.skeleton.persons.Count} person(s) with pose data");
                m_uiPose.DrawPoseSkeletons(response.skeleton.persons.ToArray(), cachedCameraPose, m_minKeypointScore);
            }
            else
            {
                Debug.Log("[POSE SERVER] No pose data in response");
                m_uiPose.ClearSkeletons();
            }

            // 7. Update HUD with inference metrics
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
    }
}


