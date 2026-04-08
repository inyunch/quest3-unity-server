// Copyright (c) Meta Platforms, Inc. and affiliates.

using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Meta.XR;
using Meta.XR.Samples;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;
using Newtonsoft.Json;
using PassthroughCameraSamples.Shared;

namespace PassthroughCameraSamples.DepthEstimation
{
    [MetaCodeSample("PassthroughCameraApiSamples-DepthEstimation")]
    public class DepthInferenceRunManager : MonoBehaviour
    {
        [SerializeField] private PassthroughCameraAccess m_cameraAccess;
        [SerializeField] private PassthroughCameraSamples.MultiObjectDetection.DetectionUiMenuManager m_uiMenuManager;

        [Header("UI display references")]
        [SerializeField] private SharedInferenceHUD m_sharedHUD;
        [SerializeField] private DepthVisualization m_depthVisualization;

        [Header("Server Inference")]
        [SerializeField] private InferenceConfig m_inferenceConfig = new InferenceConfig
        {
            mode = InferenceMode.DepthEstimation,
            targetFPS = 5f,  // Lower FPS for depth due to large download size
            jpegQuality = 80,
            includeMask = false,
            includeDepth = false  // Will be forced to true by mode=depth
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

        // FPS throttling (Part C)
        private float m_lastInferenceTime = 0f;
        private bool m_inferenceInProgress = false;

        // Frame statistics (Part D)
        private int m_totalFrames = 0;
        private int m_droppedFrames = 0;
        private int m_frozenFrames = 0;

        private IEnumerator Start()
        {
            Debug.Log("[DEPTH INF] DepthInferenceRunManager started");

            // Reference checks
            Debug.Log($"[DEPTH REF] cameraAccess={m_cameraAccess != null}");
            Debug.Log($"[DEPTH REF] uiMenuManager={m_uiMenuManager != null}");
            Debug.Log($"[DEPTH REF] sharedHUD={m_sharedHUD != null}");
            Debug.Log($"[DEPTH REF] depthVisualization={m_depthVisualization != null}");

            m_inferenceConfig.Validate();
            m_inferenceConfig.LogSummary();

            // Initialize SharedInferenceHUD
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
                Debug.Log("ovrp_GetNodePoseStateAtTime failed, which means 'm_cameraAccess.GetCameraPose()' is not reliable, skipping.");
                m_inferenceInProgress = false;
                yield break;
            }

            // Update Capture data
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
        private class DepthServerResponse
        {
            public DepthData depth;
            public int input_image_width;
            public int input_image_height;
            public float processing_time_ms;
        }

        [System.Serializable]
        public class DepthData
        {
            public int height;
            public int width;
            public int downsample_factor;
            public List<List<float>> values;  // 2D array of depth values [0-1]
        }

        // ============================================================================
        // SERVER INFERENCE - Connection Test
        // ============================================================================

        private IEnumerator TestServerConnection()
        {
            string testUrl = "http://192.168.0.135:8001/";
            Debug.Log($"[DEPTH SERVER TEST] Connecting to {testUrl}");

            using (UnityWebRequest req = UnityWebRequest.Get(testUrl))
            {
                req.timeout = 5;
                yield return req.SendWebRequest();

                if (req.result == UnityWebRequest.Result.Success)
                {
                    Debug.Log($"[DEPTH SERVER TEST] ✓ Connection OK! Response: {req.downloadHandler.text}");
                }
                else
                {
                    Debug.LogError($"[DEPTH SERVER TEST] ✗ Connection FAILED: {req.error}");
                    Debug.LogError($"[DEPTH SERVER TEST] Result: {req.result}");
                    Debug.LogError($"[DEPTH SERVER TEST] Response Code: {req.responseCode}");
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
                    Debug.LogError("[DEPTH SERVER] Unsupported texture type for server inference");
                    m_inferenceInProgress = false;
                    yield break;
                }
            }

            // 2. Encode texture as JPEG (use configurable quality from InferenceConfig)
            int jpegQuality = m_inferenceConfig.jpegQuality;
            byte[] jpegBytes = tex2D.EncodeToJPG(jpegQuality);
            int uploadBytes = jpegBytes.Length;
            Debug.Log($"[DEPTH SERVER] Encoded JPEG (quality={jpegQuality}): {uploadBytes} bytes ({tex2D.width}x{tex2D.height})");

            // 3. Create multipart form POST
            List<IMultipartFormSection> formData = new List<IMultipartFormSection>();
            formData.Add(new MultipartFormFileSection("image", jpegBytes, "frame.jpg", "image/jpeg"));

            string serverUrl = m_inferenceConfig.BuildUrl();
            UnityWebRequest request = UnityWebRequest.Post(serverUrl, formData);

            // Add HTTP headers (including timing data from previous frame)
            request.SetRequestHeader("X-Scene-Name", "DepthEstimation");
            request.SetRequestHeader("X-Frame-Id", m_frameId.ToString());

            // Send timing data from PREVIOUS frame (frame N-1) for Excel logging
            request.SetRequestHeader("X-E2E-Ms", m_lastE2eMs.ToString("F1"));
            request.SetRequestHeader("X-Upload-Ms", m_lastUploadMs.ToString("F1"));
            request.SetRequestHeader("X-Download-Ms", m_lastDownloadMs.ToString("F1"));
            request.SetRequestHeader("X-Parse-Ms", m_lastParseMs.ToString("F1"));
            request.SetRequestHeader("X-Upload-Bytes", m_lastUploadBytes.ToString());
            request.SetRequestHeader("X-Download-Bytes", m_lastDownloadBytes.ToString());
            request.SetRequestHeader("X-Download-Bytes-Compressed", m_lastDownloadBytesCompressed.ToString());

            Debug.Log($"[DEPTH SEND] Sending frame {m_frameId} to server: {serverUrl}");

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

            Debug.Log($"[DEPTH SERVER SEND] <<< Request completed. Result: {request.result}");

            if (request.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError($"[DEPTH SERVER] Inference failed: {request.error}");
                Debug.LogError($"[DEPTH SERVER] Result type: {request.result}");
                Debug.LogError($"[DEPTH SERVER] Response code: {request.responseCode}");
                Debug.LogError($"[DEPTH SERVER] URL was: {serverUrl}");
                m_inferenceInProgress = false;  // Release lock on error
                yield break;
            }

            // 5. Parse JSON response and measure PARSE time
            float parseStartTime = Time.realtimeSinceStartup;

            string jsonResponse = request.downloadHandler.text;
            Debug.Log($"[DEPTH RECV] Response received, length={jsonResponse.Length}");

            DepthServerResponse response = null;
            try
            {
                response = JsonConvert.DeserializeObject<DepthServerResponse>(jsonResponse);
                Debug.Log($"[DEPTH JSON] Depth data parsed successfully");
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[DEPTH JSON] Failed to parse depth response: {e.Message}");
                Debug.LogError($"[DEPTH JSON] Response preview: {jsonResponse.Substring(0, Mathf.Min(500, jsonResponse.Length))}");
                m_inferenceInProgress = false;
                yield break;
            }

            if (response == null || response.depth == null)
            {
                Debug.LogError("[DEPTH SERVER] Failed to parse JSON response or depth is null");
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

            Debug.Log($"[TIMING] E2E={e2eMs:F0}ms (upload={uploadMs:F0}ms server={serverProcMs:F0}ms download={downloadMs:F0}ms parse={parseMs:F0}ms)");

            // Log both compressed and uncompressed sizes
            int uncompressedBytes = System.Text.Encoding.UTF8.GetByteCount(request.downloadHandler.text);
            float compressionRatio = downloadBytes > 0 ? (float)uncompressedBytes / downloadBytes : 1f;
            Debug.Log($"[BYTES] Upload={uploadBytes} Download={downloadBytes}B (compressed), {uncompressedBytes}B (uncompressed), {compressionRatio:F2}x compression");

            // Log depth map details
            Debug.Log($"[DEPTH DATA] Map size: {response.depth.width}x{response.depth.height}, downsample={response.depth.downsample_factor}");

            // 6. Visualize depth map
            if (m_depthVisualization != null && response.depth.values != null)
            {
                m_depthVisualization.UpdateDepthMap(response.depth);
            }
            else
            {
                Debug.LogWarning("[DEPTH] No visualization component or depth values are null");
            }

            // 7. Update HUD with inference metrics
            // Compute depth statistics
            float minDepth = float.MaxValue;
            float maxDepth = float.MinValue;
            float avgDepth = 0f;
            int pixelCount = 0;

            if (response.depth.values != null)
            {
                foreach (var row in response.depth.values)
                {
                    foreach (var value in row)
                    {
                        minDepth = Mathf.Min(minDepth, value);
                        maxDepth = Mathf.Max(maxDepth, value);
                        avgDepth += value;
                        pixelCount++;
                    }
                }
                if (pixelCount > 0)
                {
                    avgDepth /= pixelCount;
                }
            }

            Debug.Log($"[DEPTH STATS] min={minDepth:F3}, max={maxDepth:F3}, avg={avgDepth:F3}");

            // Update SharedInferenceHUD with metrics
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
                    0,  // No detections for depth mode
                    avgDepth,  // Use average depth as "confidence"
                    0f  // No keypoint confidence
                );
            }

            // Store timing data for next frame's HTTP headers (to log in Excel)
            m_lastE2eMs = e2eMs;
            m_lastUploadMs = uploadMs;
            m_lastDownloadMs = downloadMs;
            m_lastParseMs = parseMs;
            m_lastUploadBytes = uploadBytes;
            m_lastDownloadBytes = uncompressedBytes;
            m_lastDownloadBytesCompressed = downloadBytes;
        }
    }
}
