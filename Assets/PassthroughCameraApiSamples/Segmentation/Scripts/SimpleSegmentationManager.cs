// Copyright (c) Meta Platforms, Inc. and affiliates.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Meta.XR;
using UnityEngine;
using UnityEngine.Networking;
using Newtonsoft.Json;
using PassthroughCameraSamples.Shared;

namespace PassthroughCameraSamples.Segmentation
{
    /// <summary>
    /// Simplified segmentation manager using unified InferenceConfig and /infer_human endpoint.
    /// Supports both Segmentation and SegmentationWithDepth modes for unified metrics comparison.
    /// </summary>
    public class SimpleSegmentationManager : MonoBehaviour
    {
        [Header("Core References")]
        [SerializeField] private PassthroughCameraAccess m_cameraAccess;
        [SerializeField] private PassthroughCameraSamples.MultiObjectDetection.DetectionUiMenuManager m_uiMenuManager;

        [Header("Rendering")]
        [SerializeField] private Segmentation3DRenderer m_renderer3D;

        [Header("UI Display")]
        [SerializeField] private SharedInferenceHUD m_sharedHUD;

        [Header("Unified Server Inference")]
        [SerializeField] private InferenceConfig m_inferenceConfig = new InferenceConfig
        {
            mode = InferenceMode.SegmentationWithDepth,  // or InferenceMode.Segmentation
            targetFPS = 5f,
            jpegQuality = 80,
            includeMask = false,  // Will be forced to true by mode
            includeDepth = false  // Will be forced to true if SegmentationWithDepth
        };

        private int m_frameId = 0;

        // Store timing data from previous frame to send as headers in next request
        private float m_lastE2eMs = 0f;
        private float m_lastUploadMs = 0f;
        private float m_lastDownloadMs = 0f;
        private float m_lastParseMs = 0f;
        private int m_lastUploadBytes = 0;
        private int m_lastDownloadBytes = 0;
        private int m_lastDownloadBytesCompressed = 0;

        // FPS throttling
        private float m_lastInferenceTime = 0f;
        private bool m_inferenceInProgress = false;

        // Frame statistics
        private int m_totalFrames = 0;
        private int m_droppedFrames = 0;
        private int m_frozenFrames = 0;

        private IEnumerator Start()
        {
            Debug.Log("[SEG SIMPLE] SimpleSegmentationManager started");

            // Reference checks
            Debug.Log($"[SEG REF] cameraAccess={m_cameraAccess != null}");
            Debug.Log($"[SEG REF] uiMenuManager={m_uiMenuManager != null}");
            Debug.Log($"[SEG REF] renderer3D={m_renderer3D != null}");
            Debug.Log($"[SEG REF] sharedHUD={m_sharedHUD != null}");

            // Validate and log config
            m_inferenceConfig.Validate();
            m_inferenceConfig.LogSummary();

            // Initialize SharedInferenceHUD
            if (m_sharedHUD != null)
            {
                m_sharedHUD.SetMode(m_inferenceConfig.mode, m_inferenceConfig.targetFPS);
            }

            // Test server connection
            Debug.Log("[SERVER TEST] Testing connection to server...");
            yield return TestServerConnection();

            while (true)
            {
                while (m_uiMenuManager != null && m_uiMenuManager.IsPaused)
                {
                    yield return null;
                }
                yield return RunInference();
            }
        }

        private IEnumerator RunInference()
        {
            if (m_cameraAccess == null || !m_cameraAccess.IsPlaying)
            {
                yield break;
            }

            // FPS THROTTLING
            float currentTime = Time.time;
            float targetInterval = m_inferenceConfig.GetInferenceInterval();
            float timeSinceLastInference = currentTime - m_lastInferenceTime;

            // Check if we should drop this frame (too soon since last inference)
            if (timeSinceLastInference < targetInterval)
            {
                m_droppedFrames++;
                if (m_sharedHUD != null)
                {
                    m_sharedHUD.ReportDroppedFrame();
                }
                yield break;
            }

            // Check if previous inference is still in progress (freeze frame)
            if (m_inferenceInProgress)
            {
                m_frozenFrames++;
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
                Debug.Log("[SEG] ovrp_GetNodePoseStateAtTime failed, skipping frame.");
                m_inferenceInProgress = false;
                yield break;
            }

            // Get camera texture
            Texture targetTexture = m_cameraAccess.GetTexture();

            // Run server inference
            yield return RunServerInference(targetTexture);

            // Mark inference as complete
            m_inferenceInProgress = false;
            m_totalFrames++;
        }

        // ============================================================================
        // SERVER INFERENCE - JSON Response Classes
        // ============================================================================

        [System.Serializable]
        private class SegmentationServerResponse
        {
            public SegmentationResultData segmentation;
            public DepthResultData depth;
            public SkeletonData skeleton;
            public int model_input_width;
            public int model_input_height;
            public int input_image_width;
            public int input_image_height;
            public float processing_time_ms;
            public string mode;
        }

        [System.Serializable]
        private class SegmentationResultData
        {
            public int mask_height;
            public int mask_width;
            public int downsample_factor;
            public string mask_png_base64;  // NEW: Base64 encoded PNG RGBA
            public int num_instances;
            public string[] classes;
        }

        [System.Serializable]
        private class DepthResultData
        {
            public int height;
            public int width;
            public int downsample_factor;
            public float[][] values;  // 2D depth map
        }

        [System.Serializable]
        private class SkeletonData
        {
            public object[] persons;  // Empty for segmentation modes
        }

        // ============================================================================
        // SERVER INFERENCE - Connection Test
        // ============================================================================

        private IEnumerator TestServerConnection()
        {
            // Use ServerConfig to get base URL
            string testUrl = ServerConfig.Instance.BaseUrl;
            Debug.Log($"[SEG SERVER TEST] Connecting to {testUrl}");

            using (UnityWebRequest req = UnityWebRequest.Get(testUrl))
            {
                req.timeout = 5;
                yield return req.SendWebRequest();

                if (req.result == UnityWebRequest.Result.Success)
                {
                    Debug.Log($"[SEG SERVER TEST] ??Connection OK! Response: {req.downloadHandler.text}");
                }
                else
                {
                    Debug.LogError($"[SEG SERVER TEST] ??Connection FAILED: {req.error}");
                    Debug.LogError($"[SEG SERVER TEST] Result: {req.result}");
                    Debug.LogError($"[SEG SERVER TEST] Response Code: {req.responseCode}");
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
                    Debug.LogError("[SEG] Unsupported texture type for server inference");
                    m_inferenceInProgress = false;
                    yield break;
                }
            }

            // 2. Encode texture as JPEG
            int jpegQuality = m_inferenceConfig.jpegQuality;
            byte[] jpegBytes = tex2D.EncodeToJPG(jpegQuality);
            int uploadBytes = jpegBytes.Length;
            Debug.Log($"[SEG] Encoded JPEG (quality={jpegQuality}): {uploadBytes} bytes ({tex2D.width}x{tex2D.height})");

            // 3. Create multipart form POST
            List<IMultipartFormSection> formData = new List<IMultipartFormSection>();
            formData.Add(new MultipartFormFileSection("image", jpegBytes, "frame.jpg", "image/jpeg"));

            // Build URL from InferenceConfig
            string serverUrl = m_inferenceConfig.BuildUrl();
            Debug.Log($"[SEG] Server URL: {serverUrl}");

            UnityWebRequest request = UnityWebRequest.Post(serverUrl, formData);

            // Add HTTP headers
            string sceneName = m_inferenceConfig.mode == InferenceMode.Segmentation ? "Segmentation" : "SegmentationWithDepth";
            request.SetRequestHeader("X-Scene-Name", sceneName);
            request.SetRequestHeader("X-Frame-Id", m_frameId.ToString());

            // Send timing data from PREVIOUS frame
            request.SetRequestHeader("X-E2E-Ms", m_lastE2eMs.ToString("F1"));
            request.SetRequestHeader("X-Upload-Ms", m_lastUploadMs.ToString("F1"));
            request.SetRequestHeader("X-Download-Ms", m_lastDownloadMs.ToString("F1"));
            request.SetRequestHeader("X-Parse-Ms", m_lastParseMs.ToString("F1"));
            request.SetRequestHeader("X-Upload-Bytes", m_lastUploadBytes.ToString());
            request.SetRequestHeader("X-Download-Bytes", m_lastDownloadBytes.ToString());
            request.SetRequestHeader("X-Download-Bytes-Compressed", m_lastDownloadBytesCompressed.ToString());

            // Performance metrics headers
            float freezeRatio = m_totalFrames > 0 ? (float)m_frozenFrames / m_totalFrames : 0f;
            request.SetRequestHeader("X-Target-FPS", m_inferenceConfig.targetFPS.ToString("F1"));
            request.SetRequestHeader("X-Dropped-Frames", m_droppedFrames.ToString());
            request.SetRequestHeader("X-Freeze-Frames", m_frozenFrames.ToString());
            request.SetRequestHeader("X-Freeze-Ratio", freezeRatio.ToString("F4"));

            Debug.Log($"[SEG SEND] Sending frame {m_frameId} to server...");

            // 4. Send request and measure UPLOAD time
            float uploadStartTime = Time.realtimeSinceStartup;

            // Start the request
            UnityWebRequestAsyncOperation asyncOp = request.SendWebRequest();

            // Poll until upload completes
            while (!asyncOp.isDone && request.uploadProgress < 1.0f)
            {
                yield return null;
            }

            float uploadDoneTime = Time.realtimeSinceStartup;
            float uploadMs = (uploadDoneTime - uploadStartTime) * 1000f;

            // Wait for response to complete
            yield return asyncOp;

            Debug.Log($"[SEG] Request completed. Result: {request.result}");

            if (request.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError($"[SEG] Inference failed: {request.error}");
                Debug.LogError($"[SEG] Result type: {request.result}");
                Debug.LogError($"[SEG] Response code: {request.responseCode}");
                Debug.LogError($"[SEG] URL was: {serverUrl}");
                m_inferenceInProgress = false;
                yield break;
            }

            // 5. Parse JSON response and measure PARSE time
            float parseStartTime = Time.realtimeSinceStartup;

            string jsonResponse = request.downloadHandler.text;
            Debug.Log($"[SEG RECV] Response received, length={jsonResponse.Length}");
            Debug.Log($"[SEG RECV] First 200 chars: {jsonResponse.Substring(0, Mathf.Min(200, jsonResponse.Length))}");

            // Extract segmentation field using string parsing (bypasses large depth/mask arrays)
            SegmentationServerResponse response = new SegmentationServerResponse();

            // Extract segmentation portion
            string segmentationJson = ExtractJsonField(jsonResponse, "segmentation");
            if (!string.IsNullOrEmpty(segmentationJson))
            {
                Debug.Log($"[SEG JSON] Extracted segmentation: {segmentationJson.Length} chars");
                Debug.Log($"[SEG JSON] First 300 chars: {segmentationJson.Substring(0, Mathf.Min(300, segmentationJson.Length))}");

                try
                {
                    response.segmentation = JsonConvert.DeserializeObject<SegmentationResultData>(segmentationJson);
                    Debug.Log($"[SEG JSON] Segmentation parsed successfully");
                    Debug.Log($"[SEG JSON] num_instances={response.segmentation.num_instances}");
                    Debug.Log($"[SEG JSON] mask_width={response.segmentation.mask_width}");
                    Debug.Log($"[SEG JSON] mask_height={response.segmentation.mask_height}");
                    Debug.Log($"[SEG JSON] mask_png_base64 length={response.segmentation.mask_png_base64?.Length ?? 0}");
                }
                catch (Exception e)
                {
                    Debug.LogError($"[SEG JSON] Failed to parse segmentation: {e.Message}");
                    Debug.LogError($"[SEG JSON] Error details: {e}");
                }
            }
            else
            {
                Debug.LogError($"[SEG JSON] Failed to extract segmentation field from JSON");
            }

            // Extract processing_time_ms
            string procTimeStr = ExtractSimpleJsonValue(jsonResponse, "processing_time_ms");
            if (!string.IsNullOrEmpty(procTimeStr) && float.TryParse(procTimeStr, out float procTime))
            {
                response.processing_time_ms = procTime;
                Debug.Log($"[SEG JSON] Processing time: {procTime:F1}ms");
            }

            // Extract mode
            string modeStr = ExtractSimpleJsonValue(jsonResponse, "mode");
            if (!string.IsNullOrEmpty(modeStr))
            {
                response.mode = modeStr;
                Debug.Log($"[SEG JSON] Mode: {modeStr}");
            }

            if (response == null || response.segmentation == null)
            {
                Debug.LogError("[SEG] Failed to parse JSON response");
                m_inferenceInProgress = false;
                yield break;
            }

            // Parse time measurement complete
            float parseMs = (Time.realtimeSinceStartup - parseStartTime) * 1000f;

            // Calculate E2E time and derive download time
            float e2eMs = (Time.realtimeSinceStartup - e2eStartTime) * 1000f;
            int downloadBytes = (int)request.downloadedBytes;
            float serverProcMs = response.processing_time_ms;
            float downloadMs = Mathf.Max(0f, e2eMs - uploadMs - serverProcMs - parseMs);

            Debug.Log($"[SEG TIMING] E2E={e2eMs:F0}ms (upload={uploadMs:F0}ms server={serverProcMs:F0}ms download={downloadMs:F0}ms parse={parseMs:F0}ms)");

            // Log both compressed and uncompressed sizes
            int uncompressedBytes = System.Text.Encoding.UTF8.GetByteCount(request.downloadHandler.text);
            float compressionRatio = downloadBytes > 0 ? (float)uncompressedBytes / downloadBytes : 1f;
            Debug.Log($"[SEG BYTES] Upload={uploadBytes} Download={downloadBytes}B (compressed), {uncompressedBytes}B (uncompressed), {compressionRatio:F2}x compression");

            // 6. Render segmentation mask
            if (response.segmentation != null && !string.IsNullOrEmpty(response.segmentation.mask_png_base64))
            {
                Debug.Log($"[SEG RENDER] Rendering segmentation mask: {response.segmentation.num_instances} instances");

                if (m_renderer3D != null)
                {
                    // Convert to SegmentationResponse format for renderer
                    var segResponse = new PassthroughCameraSamples.Segmentation.SegmentationResponse
                    {
                        frame_id = m_frameId,
                        mode = response.mode ?? "segmentation",
                        success = true,
                        segmentation_mask = response.segmentation.mask_png_base64,
                        mask_width = response.segmentation.mask_width,
                        mask_height = response.segmentation.mask_height,
                        mask_encoding = "png",
                        classes = response.segmentation.classes ?? new string[0],
                        num_instances = response.segmentation.num_instances
                    };

                    m_renderer3D.RenderSegmentation(segResponse);
                }
                else
                {
                    Debug.LogWarning("[SEG RENDER] No 3D renderer assigned!");
                }
            }
            else
            {
                Debug.Log("[SEG] No segmentation mask in response");
            }

            // 7. Update SharedInferenceHUD with metrics
            int detectionCount = response.segmentation?.num_instances ?? 0;

            if (m_sharedHUD != null)
            {
                m_sharedHUD.UpdateMetrics(
                    e2eMs,
                    uploadMs,
                    serverProcMs,
                    downloadMs,
                    parseMs,
                    uploadBytes,
                    uncompressedBytes,
                    downloadBytes,
                    detectionCount,
                    0f,  // avgConfidence (segmentation doesn't have detection confidence)
                    0f   // keypointAvgConf (segmentation doesn't have keypoints)
                );
            }

            // Store timing data for next frame's HTTP headers
            m_lastE2eMs = e2eMs;
            m_lastUploadMs = uploadMs;
            m_lastDownloadMs = downloadMs;
            m_lastParseMs = parseMs;
            m_lastUploadBytes = uploadBytes;
            m_lastDownloadBytes = uncompressedBytes;
            m_lastDownloadBytesCompressed = downloadBytes;
        }

        // ============================================================================
        // JSON FIELD EXTRACTION - Bypasses large arrays
        // ============================================================================

        /// <summary>
        /// Extracts a specific field from JSON without parsing the entire document.
        /// </summary>
        private string ExtractJsonField(string json, string fieldName)
        {
            string searchPattern = $"\"{fieldName}\":";
            int fieldStart = json.IndexOf(searchPattern);

            if (fieldStart < 0)
            {
                Debug.LogError($"[SEG JSON] Field '{fieldName}' not found in JSON");
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
                Debug.LogError($"[SEG JSON] Unexpected end of JSON after field '{fieldName}'");
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
            else if (startChar == 'n')  // null
            {
                return "null";
            }
            else
            {
                Debug.LogError($"[SEG JSON] Field '{fieldName}' does not start with {{ or [ or null");
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
                Debug.LogError($"[SEG JSON] Mismatched brackets for field '{fieldName}'");
                return null;
            }

            string extracted = json.Substring(valueStart, valueEnd - valueStart);
            Debug.Log($"[SEG JSON] Extracted '{fieldName}': {extracted.Length} chars");
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

            // Find end of value
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
                    if (json[valueEnd] == '\\') valueEnd++;
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


