// Copyright (c) Meta Platforms, Inc. and affiliates.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using PassthroughCameraSamples.Shared;
using Meta.XR;

namespace PassthroughCameraSamples.Segmentation
{
    /// <summary>
    /// Main manager for RGB-D segmentation pipeline.
    ///
    /// Orchestrates:
    /// - RGB frame capture from PassthroughCameraAccess
    /// - Depth frame capture from Quest Depth API
    /// - RGB-Depth synchronization validation
    /// - Request packaging and server communication
    /// - Response parsing and overlay rendering
    /// - Comprehensive latency and payload logging
    /// </summary>
    public class SegmentationInferenceManager : MonoBehaviour
    {
        [Header("Mode Configuration")]
        [SerializeField] private SegmentationMode m_mode = SegmentationMode.RGB_D;
        [SerializeField] private bool m_autoStart = false;
        [SerializeField, Range(0.5f, 10f)] private float m_inferenceIntervalSeconds = 2.0f;

        [Header("Server Configuration")]
        [SerializeField] private string m_serverUrl = "http://192.168.0.135:8001/segmentation";
        [SerializeField, Range(5f, 60f)] private float m_requestTimeoutSeconds = 10.0f;

        [Header("References")]
        [SerializeField] private PassthroughCameraAccess m_cameraAccess;
        [SerializeField] private QuestDepthCaptureManager m_depthCapture;
        [SerializeField] private Segmentation3DRenderer m_renderer3D;  // 3D world-space renderer (recommended)
        [SerializeField] private SegmentationOverlayRenderer m_overlayRenderer;  // Legacy 2D overlay
        [SerializeField] private Transform m_cameraRig;  // OVRCameraRig or similar

        [Header("Rendering Mode")]
        [SerializeField] private bool m_use3DRendering = true;  // Use 3D quad instead of 2D Canvas

        [Header("Sync Configuration")]
        [SerializeField] private float m_syncThresholdMs = 10.0f;
        [SerializeField] private bool m_strictSyncMode = false;  // Drop frames if sync fails

        [Header("Image Configuration")]
        [SerializeField, Range(0.1f, 1.0f)] private float m_rgbDownsampleFactor = 1.0f;  // 1.0 = full res
        [SerializeField, Range(0.1f, 1.0f)] private float m_depthDownsampleFactor = 0.5f;  // 0.5 = half res

        [Header("Debug")]
        [SerializeField] private bool m_verboseLogging = true;
        [SerializeField] private bool m_logToFile = false;

        // State
        private bool m_isRunning = false;
        private int m_frameId = 0;
        private Coroutine m_inferenceCoroutine = null;

        // Sync statistics
        private RGBDSyncValidator.SyncStats m_syncStats = new RGBDSyncValidator.SyncStats();

        // Latency tracking
        private FrameTimingData m_currentFrameTiming;

        // Events
        public event Action<SegmentationResponse> OnSegmentationReceived;
        public event Action<string> OnError;

        // Properties
        public bool IsRunning => m_isRunning;
        public SegmentationMode Mode => m_mode;
        public RGBDSyncValidator.SyncStats SyncStats => m_syncStats;
        public FrameTimingData LastFrameTiming => m_currentFrameTiming;

        private void Start()
        {
            Debug.Log("[SEGMENTATION] SegmentationInferenceManager initialized");
            Debug.Log($"[SEGMENTATION] Mode: {m_mode}, Server: {m_serverUrl}");

            // Auto-find references if not assigned
            if (m_cameraAccess == null)
            {
                m_cameraAccess = FindObjectOfType<PassthroughCameraAccess>();
                if (m_cameraAccess != null)
                {
                    Debug.Log("[SEGMENTATION] Auto-found PassthroughCameraAccess");
                }
            }

            if (m_depthCapture == null)
            {
                m_depthCapture = FindObjectOfType<QuestDepthCaptureManager>();
            }

            if (m_overlayRenderer == null)
            {
                m_overlayRenderer = FindObjectOfType<SegmentationOverlayRenderer>();
            }

            if (m_cameraRig == null)
            {
                var ovrCameraRig = FindObjectOfType<OVRCameraRig>();
                if (ovrCameraRig != null)
                {
                    m_cameraRig = ovrCameraRig.trackingSpace;
                }
            }

            // Enable depth if RGB-D mode
            if (m_mode == SegmentationMode.RGB_D && m_depthCapture != null)
            {
                m_depthCapture.EnableDepth();
            }

            if (m_autoStart)
            {
                StartInference();
            }
        }

        /// <summary>
        /// Start inference loop.
        /// </summary>
        public void StartInference()
        {
            if (m_isRunning)
            {
                Debug.LogWarning("[SEGMENTATION] Already running");
                return;
            }

            Debug.Log($"[SEGMENTATION] Starting inference in {m_mode} mode...");

            m_isRunning = true;
            m_frameId = 0;
            m_syncStats.Reset();

            m_inferenceCoroutine = StartCoroutine(InferenceLoop());
        }

        /// <summary>
        /// Stop inference loop.
        /// </summary>
        public void StopInference()
        {
            if (!m_isRunning)
            {
                return;
            }

            Debug.Log("[SEGMENTATION] Stopping inference...");

            m_isRunning = false;

            if (m_inferenceCoroutine != null)
            {
                StopCoroutine(m_inferenceCoroutine);
                m_inferenceCoroutine = null;
            }

            // Print sync statistics
            if (m_syncStats.TotalFrames > 0)
            {
                Debug.Log($"[SEGMENTATION] Final sync stats: {m_syncStats}");
            }
        }

        /// <summary>
        /// Change segmentation mode.
        /// </summary>
        public void SetMode(SegmentationMode newMode)
        {
            if (newMode == m_mode)
            {
                return;
            }

            Debug.Log($"[SEGMENTATION] Changing mode: {m_mode} → {newMode}");

            bool wasRunning = m_isRunning;

            if (wasRunning)
            {
                StopInference();
            }

            m_mode = newMode;

            // Enable/disable depth based on mode
            if (m_depthCapture != null)
            {
                if (newMode == SegmentationMode.RGB_D)
                {
                    m_depthCapture.EnableDepth();
                }
                else
                {
                    m_depthCapture.DisableDepth();
                }
            }

            if (wasRunning)
            {
                StartInference();
            }
        }

        /// <summary>
        /// Main inference loop.
        /// </summary>
        private IEnumerator InferenceLoop()
        {
            Debug.Log($"[SEGMENTATION] Inference loop started, interval={m_inferenceIntervalSeconds}s");

            while (m_isRunning)
            {
                yield return StartCoroutine(ProcessFrame());

                // Wait for next inference
                yield return new WaitForSeconds(m_inferenceIntervalSeconds);
            }
        }

        /// <summary>
        /// Process a single frame through the entire pipeline.
        /// </summary>
        private IEnumerator ProcessFrame()
        {
            m_frameId++;

            // Initialize timing data
            m_currentFrameTiming = new FrameTimingData
            {
                frameId = m_frameId,
                mode = m_mode.ToString(),
                deviceTimestamp = Time.realtimeSinceStartup
            };

            if (m_verboseLogging)
            {
                Debug.Log($"[SEGMENTATION] ========== Frame {m_frameId} START ==========");
            }

            // Step 1: Capture RGB
            yield return StartCoroutine(CaptureRGBFrame());

            if (m_currentFrameTiming.rgbCaptureSuccess == false)
            {
                Debug.LogError($"[SEGMENTATION] Frame {m_frameId}: RGB capture failed, skipping frame");
                yield break;
            }

            // Step 2: Capture Depth (if RGB-D mode)
            if (m_mode == SegmentationMode.RGB_D)
            {
                yield return StartCoroutine(CaptureDepthFrame());

                // Step 3: Validate sync
                if (m_currentFrameTiming.depthCaptureSuccess)
                {
                    ValidateRGBDepthSync();

                    // Drop frame if strict mode and sync failed
                    if (m_strictSyncMode && !m_currentFrameTiming.syncOk)
                    {
                        Debug.LogWarning($"[SEGMENTATION] Frame {m_frameId}: Sync failed in strict mode, dropping frame");
                        yield break;
                    }
                }
                else
                {
                    Debug.LogWarning($"[SEGMENTATION] Frame {m_frameId}: Depth capture failed, depth unavailable");
                }
            }

            // Step 4: Package and upload request
            yield return StartCoroutine(UploadRequest());

            if (!m_currentFrameTiming.uploadSuccess)
            {
                Debug.LogError($"[SEGMENTATION] Frame {m_frameId}: Upload failed");
                OnError?.Invoke("Upload failed");
                yield break;
            }

            // Step 5: Render result
            yield return StartCoroutine(RenderResult());

            // Step 6: Compute final metrics
            m_currentFrameTiming.tRenderDone = Time.realtimeSinceStartup;
            ComputeFinalMetrics();

            // Step 7: Log frame completion
            LogFrameCompletion();

            if (m_verboseLogging)
            {
                Debug.Log($"[SEGMENTATION] ========== Frame {m_frameId} END (E2E: {m_currentFrameTiming.e2eLatencyMs:F1}ms) ==========");
            }
        }

        /// <summary>
        /// Capture RGB frame from PassthroughCameraAccess.
        /// </summary>
        private IEnumerator CaptureRGBFrame()
        {
            m_currentFrameTiming.tRgbCapture = Time.realtimeSinceStartup;

            if (m_cameraAccess == null)
            {
                Debug.LogError("[SEGMENTATION] PassthroughCameraAccess is null");
                m_currentFrameTiming.rgbCaptureSuccess = false;
                yield break;
            }

            try
            {
                // Get texture directly from PassthroughCameraAccess
                Texture rgbTextureRaw = m_cameraAccess.GetTexture();

                if (rgbTextureRaw == null)
                {
                    Debug.LogError("[SEGMENTATION] GetTexture returned null");
                    m_currentFrameTiming.rgbCaptureSuccess = false;
                    yield break;
                }

                Texture2D rgbTexture = ConvertToTexture2D(rgbTextureRaw);

                if (rgbTexture != null)
                {
                    // Convert texture to JPEG bytes
                    m_currentFrameTiming.rgbWidth = rgbTexture.width;
                    m_currentFrameTiming.rgbHeight = rgbTexture.height;

                    // Downsample if needed
                    if (m_rgbDownsampleFactor < 1.0f)
                    {
                        int targetWidth = Mathf.RoundToInt(rgbTexture.width * m_rgbDownsampleFactor);
                        int targetHeight = Mathf.RoundToInt(rgbTexture.height * m_rgbDownsampleFactor);

                        rgbTexture = ResizeTexture(rgbTexture, targetWidth, targetHeight);

                        m_currentFrameTiming.rgbWidth = targetWidth;
                        m_currentFrameTiming.rgbHeight = targetHeight;
                    }

                    m_currentFrameTiming.rgbBytes = rgbTexture.EncodeToJPG(85);
                    m_currentFrameTiming.rgbBytesCount = m_currentFrameTiming.rgbBytes.Length;

                    m_currentFrameTiming.rgbCaptureSuccess = true;

                    if (m_verboseLogging)
                    {
                        Debug.Log($"[SEGMENTATION] RGB captured: {m_currentFrameTiming.rgbWidth}x{m_currentFrameTiming.rgbHeight}, " +
                                  $"{m_currentFrameTiming.rgbBytesCount} bytes, timestamp={m_currentFrameTiming.tRgbCapture:F3}");
                    }
                }
                else
                {
                    Debug.LogError("[SEGMENTATION] Conversion to Texture2D failed");
                    m_currentFrameTiming.rgbCaptureSuccess = false;
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[SEGMENTATION] RGB capture failed: {e.Message}\n{e.StackTrace}");
                m_currentFrameTiming.rgbCaptureSuccess = false;
            }

            yield return null;
        }

        /// <summary>
        /// Capture depth frame from Quest Depth API.
        /// </summary>
        private IEnumerator CaptureDepthFrame()
        {
            m_currentFrameTiming.tDepthAcquired = Time.realtimeSinceStartup;

            if (m_depthCapture == null)
            {
                Debug.LogWarning("[SEGMENTATION] QuestDepthCaptureManager is null");
                m_currentFrameTiming.depthEnabled = false;
                m_currentFrameTiming.depthAvailable = false;
                m_currentFrameTiming.depthCaptureSuccess = false;
                yield break;
            }

            // Get depth metadata
            var metadata = m_depthCapture.GetDepthMetadata();
            m_currentFrameTiming.depthEnabled = metadata.enabled;
            m_currentFrameTiming.depthAvailable = metadata.available;
            m_currentFrameTiming.sceneModelAvailable = metadata.sceneModelAvailable;

            if (!metadata.available)
            {
                Debug.LogWarning("[SEGMENTATION] Depth not available, skipping depth capture");
                m_currentFrameTiming.depthCaptureSuccess = false;
                yield break;
            }

            // Capture depth frame
            bool captured = m_depthCapture.CaptureDepthFrame();

            if (captured)
            {
                float[] depthData = m_depthCapture.GetLatestDepthData();

                if (depthData != null)
                {
                    m_currentFrameTiming.depthWidth = m_depthCapture.DepthWidth;
                    m_currentFrameTiming.depthHeight = m_depthCapture.DepthHeight;
                    m_currentFrameTiming.tDepthAcquired = m_depthCapture.LastCaptureTime;

                    // Downsample depth if needed
                    if (m_depthDownsampleFactor < 1.0f)
                    {
                        int targetWidth = Mathf.RoundToInt(m_currentFrameTiming.depthWidth * m_depthDownsampleFactor);
                        int targetHeight = Mathf.RoundToInt(m_currentFrameTiming.depthHeight * m_depthDownsampleFactor);

                        depthData = DownsampleDepth(depthData, m_currentFrameTiming.depthWidth, m_currentFrameTiming.depthHeight,
                                                     targetWidth, targetHeight);

                        m_currentFrameTiming.depthWidth = targetWidth;
                        m_currentFrameTiming.depthHeight = targetHeight;
                    }

                    // Serialize depth data
                    m_currentFrameTiming.depthBytes = SerializeDepthData(depthData, m_currentFrameTiming.depthWidth, m_currentFrameTiming.depthHeight);
                    m_currentFrameTiming.depthBytesCount = m_currentFrameTiming.depthBytes.Length;

                    m_currentFrameTiming.depthCaptureSuccess = true;

                    if (m_verboseLogging)
                    {
                        Debug.Log($"[SEGMENTATION] Depth captured: {m_currentFrameTiming.depthWidth}x{m_currentFrameTiming.depthHeight}, " +
                                  $"{m_currentFrameTiming.depthBytesCount} bytes, timestamp={m_currentFrameTiming.tDepthAcquired:F3}");
                    }
                }
                else
                {
                    Debug.LogError("[SEGMENTATION] Depth data is null");
                    m_currentFrameTiming.depthCaptureSuccess = false;
                }
            }
            else
            {
                Debug.LogError("[SEGMENTATION] Depth capture failed");
                m_currentFrameTiming.depthCaptureSuccess = false;
            }

            yield return null;
        }

        /// <summary>
        /// Validate RGB-Depth synchronization.
        /// </summary>
        private void ValidateRGBDepthSync()
        {
            var syncResult = RGBDSyncValidator.Validate(
                m_currentFrameTiming.tRgbCapture,
                m_currentFrameTiming.tDepthAcquired,
                m_syncThresholdMs);

            m_currentFrameTiming.syncOk = syncResult.syncOk;
            m_currentFrameTiming.rgbDepthSyncGapMs = syncResult.syncGapMs;

            // Update sync statistics
            m_syncStats.AddSample(syncResult);

            if (m_verboseLogging)
            {
                string status = syncResult.syncOk ? "OK" : "FAILED";
                Debug.Log($"[SEGMENTATION] RGB-Depth Sync {status}: gap={syncResult.syncGapMs:F2}ms (quality={syncResult.quality})");
            }

            if (!syncResult.syncOk)
            {
                Debug.LogWarning($"[SEGMENTATION] Sync gap {syncResult.syncGapMs:F2}ms exceeds threshold {m_syncThresholdMs}ms");
            }
        }

        /// <summary>
        /// Upload request to server.
        /// </summary>
        private IEnumerator UploadRequest()
        {
            m_currentFrameTiming.tUploadStart = Time.realtimeSinceStartup;

            // Build JSON request
            string jsonRequest = BuildJSONRequest();

            if (m_verboseLogging)
            {
                int totalBytes = m_currentFrameTiming.rgbBytesCount + m_currentFrameTiming.depthBytesCount;
                Debug.Log($"[SEGMENTATION] Uploading request: {totalBytes} bytes ({m_currentFrameTiming.rgbBytesCount} RGB + {m_currentFrameTiming.depthBytesCount} depth)");
            }

            // Send HTTP POST request
            using (UnityWebRequest request = new UnityWebRequest(m_serverUrl, "POST"))
            {
                byte[] bodyRaw = Encoding.UTF8.GetBytes(jsonRequest);
                request.uploadHandler = new UploadHandlerRaw(bodyRaw);
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");
                request.timeout = Mathf.RoundToInt(m_requestTimeoutSeconds);

                yield return request.SendWebRequest();

                m_currentFrameTiming.tDownloadEnd = Time.realtimeSinceStartup;

                if (request.result == UnityWebRequest.Result.Success)
                {
                    m_currentFrameTiming.uploadSuccess = true;
                    m_currentFrameTiming.responseBytes = request.downloadHandler.data;
                    m_currentFrameTiming.responseBytesCount = m_currentFrameTiming.responseBytes.Length;

                    if (m_verboseLogging)
                    {
                        Debug.Log($"[SEGMENTATION] Response received: {m_currentFrameTiming.responseBytesCount} bytes");
                    }

                    // Parse response
                    ParseResponse(request.downloadHandler.text);
                }
                else
                {
                    m_currentFrameTiming.uploadSuccess = false;
                    Debug.LogError($"[SEGMENTATION] Request failed: {request.error}");
                    OnError?.Invoke(request.error);
                }
            }
        }

        /// <summary>
        /// Build JSON request payload.
        /// </summary>
        private string BuildJSONRequest()
        {
            // Create JSON manually to avoid JsonUtility limitations with base64
            StringBuilder sb = new StringBuilder();
            sb.Append("{");

            // Frame metadata
            sb.Append($"\"frame_id\":{m_frameId},");
            sb.Append($"\"mode\":\"{m_mode.ToString().ToLower()}\",");
            sb.Append($"\"device_timestamp\":{m_currentFrameTiming.deviceTimestamp},");

            // RGB data
            sb.Append($"\"rgb_image\":\"{Convert.ToBase64String(m_currentFrameTiming.rgbBytes)}\",");
            sb.Append($"\"rgb_width\":{m_currentFrameTiming.rgbWidth},");
            sb.Append($"\"rgb_height\":{m_currentFrameTiming.rgbHeight},");
            sb.Append($"\"timestamp_rgb_capture\":{m_currentFrameTiming.tRgbCapture},");
            sb.Append($"\"rgb_bytes\":{m_currentFrameTiming.rgbBytesCount},");

            // Depth data
            sb.Append($"\"depth_enabled\":{m_currentFrameTiming.depthEnabled.ToString().ToLower()},");
            sb.Append($"\"depth_available\":{m_currentFrameTiming.depthAvailable.ToString().ToLower()},");

            if (m_currentFrameTiming.depthCaptureSuccess)
            {
                sb.Append($"\"depth_data\":\"{Convert.ToBase64String(m_currentFrameTiming.depthBytes)}\",");
                sb.Append($"\"depth_width\":{m_currentFrameTiming.depthWidth},");
                sb.Append($"\"depth_height\":{m_currentFrameTiming.depthHeight},");
                sb.Append($"\"timestamp_depth_acquired\":{m_currentFrameTiming.tDepthAcquired},");
                sb.Append($"\"depth_bytes\":{m_currentFrameTiming.depthBytesCount},");
            }
            else
            {
                sb.Append("\"depth_data\":null,");
                sb.Append("\"depth_width\":0,");
                sb.Append("\"depth_height\":0,");
                sb.Append("\"timestamp_depth_acquired\":0,");
                sb.Append("\"depth_bytes\":0,");
            }

            // Scene metadata
            sb.Append($"\"scene_model_available\":{m_currentFrameTiming.sceneModelAvailable.ToString().ToLower()}");

            sb.Append("}");

            return sb.ToString();
        }

        /// <summary>
        /// Parse server response.
        /// </summary>
        private void ParseResponse(string jsonResponse)
        {
            try
            {
                SegmentationResponse response = JsonUtility.FromJson<SegmentationResponse>(jsonResponse);

                if (response.success)
                {
                    m_currentFrameTiming.segmentationResponse = response;

                    // Extract server timing
                    m_currentFrameTiming.tServerRecv = response.t_server_recv;
                    m_currentFrameTiming.tInferStart = response.t_infer_start;
                    m_currentFrameTiming.tInferEnd = response.t_infer_end;
                    m_currentFrameTiming.tServerSend = response.t_server_send;

                    if (m_verboseLogging)
                    {
                        Debug.Log($"[SEGMENTATION] Server inference: {response.inference_latency_ms:F1}ms, " +
                                  $"mask={response.mask_width}x{response.mask_height}, " +
                                  $"classes={response.num_instances}");
                    }

                    OnSegmentationReceived?.Invoke(response);
                }
                else
                {
                    Debug.LogError($"[SEGMENTATION] Server returned error: {response.error}");
                    OnError?.Invoke(response.error);
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[SEGMENTATION] Response parsing failed: {e.Message}");
                OnError?.Invoke("Response parsing failed");
            }
        }

        /// <summary>
        /// Render segmentation result.
        /// </summary>
        private IEnumerator RenderResult()
        {
            bool has3DRenderer = m_renderer3D != null;
            bool has2DRenderer = m_overlayRenderer != null;

            Debug.Log($"[SEGMENTATION] RenderResult called, response={m_currentFrameTiming.segmentationResponse != null}, 3D={has3DRenderer}, 2D={has2DRenderer}");

            if (m_currentFrameTiming.segmentationResponse == null)
            {
                Debug.LogError("[SEGMENTATION] segmentationResponse is null!");
                yield break;
            }

            if (!m_currentFrameTiming.segmentationResponse.success)
            {
                Debug.LogError($"[SEGMENTATION] segmentationResponse.success is false, error={m_currentFrameTiming.segmentationResponse.error}");
                yield break;
            }

            // Use 3D renderer if available and enabled
            if (m_use3DRendering && has3DRenderer)
            {
                Debug.Log($"[SEGMENTATION] Using 3D renderer, mask length={m_currentFrameTiming.segmentationResponse.segmentation_mask?.Length}");
                m_renderer3D.RenderSegmentation(m_currentFrameTiming.segmentationResponse);
                yield break;
            }

            // Fallback to 2D overlay renderer
            if (m_overlayRenderer == null)
            {
                Debug.LogError("[SEGMENTATION] m_overlayRenderer is null!");
                yield break;
            }

            Debug.Log($"[SEGMENTATION] Calling RenderSegmentation with mask length={m_currentFrameTiming.segmentationResponse.segmentation_mask?.Length ?? 0}");
            m_overlayRenderer.RenderSegmentation(m_currentFrameTiming.segmentationResponse);

            yield return null;
        }

        /// <summary>
        /// Compute final timing metrics.
        /// </summary>
        private void ComputeFinalMetrics()
        {
            // Upload latency
            if (m_currentFrameTiming.tServerRecv > 0)
            {
                m_currentFrameTiming.uploadLatencyMs = (m_currentFrameTiming.tServerRecv - m_currentFrameTiming.tUploadStart) * 1000f;
            }

            // Queue latency
            if (m_currentFrameTiming.tInferStart > 0 && m_currentFrameTiming.tServerRecv > 0)
            {
                m_currentFrameTiming.queueLatencyMs = (m_currentFrameTiming.tInferStart - m_currentFrameTiming.tServerRecv) * 1000f;
            }

            // Inference latency (from server)
            if (m_currentFrameTiming.segmentationResponse != null)
            {
                m_currentFrameTiming.inferenceLatencyMs = m_currentFrameTiming.segmentationResponse.inference_latency_ms;
                m_currentFrameTiming.serverPostprocessMs = m_currentFrameTiming.segmentationResponse.server_postprocess_ms;
            }

            // Download latency
            if (m_currentFrameTiming.tDownloadEnd > 0 && m_currentFrameTiming.tServerSend > 0)
            {
                m_currentFrameTiming.downloadLatencyMs = (m_currentFrameTiming.tDownloadEnd - m_currentFrameTiming.tServerSend) * 1000f;
            }

            // Render latency
            if (m_currentFrameTiming.tRenderDone > 0 && m_currentFrameTiming.tDownloadEnd > 0)
            {
                m_currentFrameTiming.renderLatencyMs = (m_currentFrameTiming.tRenderDone - m_currentFrameTiming.tDownloadEnd) * 1000f;
            }

            // E2E latency
            if (m_currentFrameTiming.tRenderDone > 0 && m_currentFrameTiming.tRgbCapture > 0)
            {
                m_currentFrameTiming.e2eLatencyMs = (m_currentFrameTiming.tRenderDone - m_currentFrameTiming.tRgbCapture) * 1000f;
            }

            // Total request bytes
            m_currentFrameTiming.totalRequestBytes = m_currentFrameTiming.rgbBytesCount + m_currentFrameTiming.depthBytesCount;
        }

        /// <summary>
        /// Log frame completion.
        /// </summary>
        private void LogFrameCompletion()
        {
            if (m_verboseLogging)
            {
                Debug.Log($"[SEGMENTATION] Frame {m_frameId} completed:");
                Debug.Log($"  - E2E latency: {m_currentFrameTiming.e2eLatencyMs:F1}ms");
                Debug.Log($"  - Upload: {m_currentFrameTiming.uploadLatencyMs:F1}ms");
                Debug.Log($"  - Queue: {m_currentFrameTiming.queueLatencyMs:F1}ms");
                Debug.Log($"  - Inference: {m_currentFrameTiming.inferenceLatencyMs:F1}ms");
                Debug.Log($"  - Download: {m_currentFrameTiming.downloadLatencyMs:F1}ms");
                Debug.Log($"  - Render: {m_currentFrameTiming.renderLatencyMs:F1}ms");
                Debug.Log($"  - Payload: {m_currentFrameTiming.totalRequestBytes} bytes (RGB={m_currentFrameTiming.rgbBytesCount}, Depth={m_currentFrameTiming.depthBytesCount})");
                Debug.Log($"  - Sync gap: {m_currentFrameTiming.rgbDepthSyncGapMs:F2}ms (OK={m_currentFrameTiming.syncOk})");
            }

            // TODO: Send to server for CSV logging
        }

        /// <summary>
        /// Serialize depth data to bytes for upload.
        /// Format: Simple binary format with header.
        /// </summary>
        private byte[] SerializeDepthData(float[] depthData, int width, int height)
        {
            // Binary format:
            // [4 bytes: width]
            // [4 bytes: height]
            // [width*height*4 bytes: float32 array]

            int headerSize = 8;
            int dataSize = depthData.Length * 4;
            byte[] bytes = new byte[headerSize + dataSize];

            // Write header
            BitConverter.GetBytes(width).CopyTo(bytes, 0);
            BitConverter.GetBytes(height).CopyTo(bytes, 4);

            // Write data
            Buffer.BlockCopy(depthData, 0, bytes, headerSize, dataSize);

            // Encode to base64 (done in BuildJSONRequest)
            return bytes;
        }

        /// <summary>
        /// Downsample depth array.
        /// </summary>
        private float[] DownsampleDepth(float[] depthData, int srcWidth, int srcHeight, int dstWidth, int dstHeight)
        {
            float[] downsampled = new float[dstWidth * dstHeight];

            float scaleX = (float)srcWidth / dstWidth;
            float scaleY = (float)srcHeight / dstHeight;

            for (int y = 0; y < dstHeight; y++)
            {
                for (int x = 0; x < dstWidth; x++)
                {
                    int srcX = Mathf.RoundToInt(x * scaleX);
                    int srcY = Mathf.RoundToInt(y * scaleY);

                    srcX = Mathf.Clamp(srcX, 0, srcWidth - 1);
                    srcY = Mathf.Clamp(srcY, 0, srcHeight - 1);

                    downsampled[y * dstWidth + x] = depthData[srcY * srcWidth + srcX];
                }
            }

            if (m_verboseLogging)
            {
                Debug.Log($"[SEGMENTATION] Downsampled depth: {srcWidth}x{srcHeight} → {dstWidth}x{dstHeight}");
            }

            return downsampled;
        }

        /// <summary>
        /// Convert Texture to Texture2D by reading pixels from GPU.
        /// </summary>
        private Texture2D ConvertToTexture2D(Texture texture)
        {
            if (texture == null)
                return null;

            // If already Texture2D, return as is
            if (texture is Texture2D)
                return (Texture2D)texture;

            // Create RenderTexture and read pixels
            int width = texture.width;
            int height = texture.height;

            RenderTexture renderTexture = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32);
            RenderTexture previous = RenderTexture.active;

            Graphics.Blit(texture, renderTexture);
            RenderTexture.active = renderTexture;

            Texture2D texture2D = new Texture2D(width, height, TextureFormat.RGB24, false);
            texture2D.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            texture2D.Apply();

            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(renderTexture);

            return texture2D;
        }

        /// <summary>
        /// Resize RGB texture (simple nearest-neighbor).
        /// </summary>
        private Texture2D ResizeTexture(Texture2D source, int targetWidth, int targetHeight)
        {
            Texture2D result = new Texture2D(targetWidth, targetHeight, source.format, false);

            float scaleX = (float)source.width / targetWidth;
            float scaleY = (float)source.height / targetHeight;

            Color[] srcPixels = source.GetPixels();
            Color[] dstPixels = new Color[targetWidth * targetHeight];

            for (int y = 0; y < targetHeight; y++)
            {
                for (int x = 0; x < targetWidth; x++)
                {
                    int srcX = Mathf.RoundToInt(x * scaleX);
                    int srcY = Mathf.RoundToInt(y * scaleY);

                    srcX = Mathf.Clamp(srcX, 0, source.width - 1);
                    srcY = Mathf.Clamp(srcY, 0, source.height - 1);

                    dstPixels[y * targetWidth + x] = srcPixels[srcY * source.width + srcX];
                }
            }

            result.SetPixels(dstPixels);
            result.Apply();

            return result;
        }

        private void OnDestroy()
        {
            StopInference();

            // Disable depth when destroyed
            if (m_depthCapture != null && m_depthCapture.IsEnabled)
            {
                m_depthCapture.DisableDepth();
            }
        }
    }

    /// <summary>
    /// Segmentation mode enum.
    /// </summary>
    public enum SegmentationMode
    {
        RGB,      // RGB-only segmentation
        RGB_D     // RGB-D segmentation with depth
    }

    /// <summary>
    /// Frame timing data for logging and analysis.
    /// </summary>
    [Serializable]
    public class FrameTimingData
    {
        // Frame metadata
        public int frameId;
        public string mode;
        public float deviceTimestamp;

        // RGB capture
        public float tRgbCapture;
        public bool rgbCaptureSuccess;
        public int rgbWidth;
        public int rgbHeight;
        public byte[] rgbBytes;
        public int rgbBytesCount;

        // Depth capture
        public float tDepthAcquired;
        public bool depthEnabled;
        public bool depthAvailable;
        public bool sceneModelAvailable;
        public bool depthCaptureSuccess;
        public int depthWidth;
        public int depthHeight;
        public byte[] depthBytes;
        public int depthBytesCount;

        // Synchronization
        public bool syncOk;
        public float rgbDepthSyncGapMs;

        // Upload
        public float tUploadStart;
        public bool uploadSuccess;
        public int totalRequestBytes;

        // Server timing
        public float tServerRecv;
        public float tInferStart;
        public float tInferEnd;
        public float tServerSend;

        // Download
        public float tDownloadEnd;
        public byte[] responseBytes;
        public int responseBytesCount;

        // Render
        public float tRenderDone;

        // Response
        public SegmentationResponse segmentationResponse;

        // Computed metrics
        public float uploadLatencyMs;
        public float queueLatencyMs;
        public float inferenceLatencyMs;
        public float serverPostprocessMs;
        public float downloadLatencyMs;
        public float renderLatencyMs;
        public float e2eLatencyMs;

        /// <summary>
        /// Convert to CSV row format.
        /// </summary>
        public string ToCSVRow()
        {
            return $"{frameId},{mode},{depthEnabled},{depthAvailable},{sceneModelAvailable},{syncOk}," +
                   $"{tRgbCapture},{tDepthAcquired},{tUploadStart},{tServerRecv},{tInferStart},{tInferEnd},{tServerSend},{tDownloadEnd},{tRenderDone}," +
                   $"{rgbDepthSyncGapMs},{uploadLatencyMs},{queueLatencyMs},{inferenceLatencyMs},{serverPostprocessMs},{downloadLatencyMs},{renderLatencyMs},{e2eLatencyMs}," +
                   $"{rgbBytesCount},{depthBytesCount},{totalRequestBytes},{responseBytesCount}," +
                   $"{(segmentationResponse != null && segmentationResponse.success)},{(segmentationResponse != null ? segmentationResponse.error : "")}";
        }
    }

    /// <summary>
    /// Segmentation response from server.
    /// </summary>
    [Serializable]
    public class SegmentationResponse
    {
        public int frame_id;
        public string mode;
        public bool success;
        public string error;

        public string segmentation_mask;  // base64 encoded PNG
        public int mask_width;
        public int mask_height;
        public string mask_encoding;
        public string[] classes;
        public int num_instances;

        public bool depth_used;
        public int[] depth_input_size;
        public int[] rgb_input_size;

        public float t_server_recv;
        public float t_infer_start;
        public float t_infer_end;
        public float t_server_send;

        public float inference_latency_ms;
        public float server_postprocess_ms;

        public int response_bytes;
    }
}
