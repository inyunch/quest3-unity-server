// Copyright (c) Meta Platforms, Inc. and affiliates.

using System.Collections;
using PassthroughCameraSamples.Shared;
using UnityEngine;
using UnityEngine.UI;

namespace PassthroughCameraSamples.Demo
{
    /// <summary>
    /// V3.0 Architecture Demo: Simplified Inference Manager
    ///
    /// This is a minimal example showing how to use the new OOP components:
    /// - UDPTransportManager: Bidirectional UDP communication
    /// - FrameTelemetryTracker: Frame state tracking + CSV logging
    /// - FrameResponse: Unified response format
    ///
    /// Benefits over old architecture:
    /// - NO HTTP polling (pure UDP)
    /// - NO N+1 delayed telemetry (instant CSV writes)
    /// - ~150 lines vs 1000+ lines
    /// - Clean separation of concerns
    /// - Zero blocking operations
    ///
    /// Usage:
    /// 1. Attach to GameObject in scene
    /// 2. Assign PassthroughCameraAccess reference
    /// 3. Configure server IP in ServerConfig asset
    /// 4. Run and watch console logs
    /// </summary>
    public class V3Demo_SimplifiedInferenceManager : MonoBehaviour
    {
        // ====================================================================
        // Configuration
        // ====================================================================
        [Header("Camera")]
        [SerializeField] private PassthroughCameraAccess m_cameraAccess;

        [Header("Inference Settings")]
        [SerializeField] private float m_targetFPS = 5f;
        [SerializeField] private int m_jpegQuality = 80;
        [SerializeField] private InferenceMode m_mode = InferenceMode.Segmentation;

        [Header("Debug UI (Optional)")]
        [SerializeField] private Text m_statusText;

        // ====================================================================
        // V3.0 OOP Components
        // ====================================================================
        private UDPTransportManager m_transport;
        private FrameTelemetryTracker m_telemetry;

        // ====================================================================
        // State
        // ====================================================================
        private string m_sessionId;
        private int m_frameId = 0;
        private bool m_isInitialized = false;

        // ====================================================================
        // Statistics
        // ====================================================================
        private int m_framesSent = 0;
        private int m_responsesReceived = 0;

        /// <summary>
        /// Initialize V3.0 components
        /// </summary>
        private IEnumerator Start()
        {
            Debug.Log("[V3 DEMO] ========================================");
            Debug.Log("[V3 DEMO] Simplified Inference Manager (V3.0)");
            Debug.Log("[V3 DEMO] ========================================");

            // 1. Generate session ID
            m_sessionId = System.Guid.NewGuid().ToString();
            Debug.Log($"[V3 DEMO] Session ID: {m_sessionId}");

            // 2. Initialize UDP Transport Manager
            try
            {
                m_transport = new UDPTransportManager(
                    serverIP: ServerConfig.Instance.ServerIP,
                    sendPort: 8002,
                    receivePort: 8003
                );
                m_transport.Initialize();
                Debug.Log($"[V3 DEMO] UDP Transport initialized");
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[V3 DEMO] Failed to initialize UDP transport: {e.Message}");
                yield break;
            }

            // 3. Initialize Telemetry Tracker
            m_telemetry = new FrameTelemetryTracker(
                sessionId: m_sessionId,
                sceneName: "V3Demo",
                enableLocalTelemetry: true
            );
            Debug.Log($"[V3 DEMO] Telemetry tracker initialized");

            // 4. Wait for camera to be ready
            Debug.Log($"[V3 DEMO] Waiting for camera...");
            float startTime = Time.time;
            while (!m_cameraAccess.IsPlaying && (Time.time - startTime) < 10f)
            {
                yield return new WaitForSeconds(0.5f);
            }

            if (!m_cameraAccess.IsPlaying)
            {
                Debug.LogError($"[V3 DEMO] Camera failed to start after 10 seconds");
                yield break;
            }

            Debug.Log($"[V3 DEMO] Camera ready after {Time.time - startTime:F1}s");

            // 5. Start inference loop
            m_isInitialized = true;
            InvokeRepeating(nameof(SendNextFrame), 0f, 1f / m_targetFPS);

            Debug.Log($"[V3 DEMO] Initialization complete, sending at {m_targetFPS} FPS");
        }

        /// <summary>
        /// Main update loop - poll for UDP responses
        /// </summary>
        private void Update()
        {
            if (!m_isInitialized)
                return;

            // Poll for UDP responses (non-blocking!)
            while (m_transport.TryGetResponse(out FrameResponse response))
            {
                HandleResponse(response);
            }

            // Periodic telemetry cleanup
            if (Time.frameCount % 300 == 0)
            {
                m_telemetry.CleanupOldTraces();
            }

            // Update debug UI
            UpdateStatusUI();
        }

        /// <summary>
        /// Send next inference frame (called via InvokeRepeating)
        /// </summary>
        private void SendNextFrame()
        {
            if (!m_isInitialized || !m_cameraAccess.IsPlaying)
                return;

            try
            {
                // 1. Capture frame from camera
                Texture2D frame = CaptureFrame();
                if (frame == null)
                {
                    Debug.LogWarning($"[V3 DEMO] Failed to capture frame");
                    return;
                }

                // 2. Encode to JPEG
                byte[] jpegData = frame.EncodeToJPG(m_jpegQuality);
                Destroy(frame);  // Clean up texture

                // 3. Create frame trace
                FrameTrace trace = m_telemetry.CreateFrame(m_frameId, jpegData.Length);
                trace.upload_bytes_uncompressed = m_cameraAccess.ImageWidth * m_cameraAccess.ImageHeight * 3;

                // 4. Send via UDP (NON-BLOCKING!)
                m_transport.SendFrame(trace, jpegData, telemetryJson: null);

                m_framesSent++;
                m_frameId++;

                Debug.Log($"[V3 DEMO] Sent frame {trace.frame_id}, " +
                          $"size={jpegData.Length / 1024}KB, " +
                          $"total_sent={m_framesSent}");
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[V3 DEMO] Error sending frame {m_frameId}: {e.Message}");
            }
        }

        /// <summary>
        /// Handle received inference response
        /// </summary>
        private void HandleResponse(FrameResponse response)
        {
            m_responsesReceived++;

            Debug.Log($"[V3 DEMO] Received response for frame {response.frame_id}, " +
                      $"server_proc={response.processing_time_ms:F1}ms, " +
                      $"queue_wait={response.queue_wait_ms:F1}ms, " +
                      $"total_received={m_responsesReceived}");

            // 1. Update telemetry (mark completed)
            m_telemetry.MarkFrameCompleted(response.frame_id, response);

            // 2. Process results (example: just log detection count)
            if (response.HasDetections())
            {
                Debug.Log($"[V3 DEMO] Frame {response.frame_id}: {response.detections.Length} detections");
            }
            else if (response.HasPose())
            {
                Debug.Log($"[V3 DEMO] Frame {response.frame_id}: {response.persons.Length} persons");
            }
            else if (response.HasSegmentation())
            {
                Debug.Log($"[V3 DEMO] Frame {response.frame_id}: Segmentation mask {response.segmentation.mask_width}x{response.segmentation.mask_height}");
            }

            // 3. Mark as displayed (this automatically drops older frames and writes to CSV)
            m_telemetry.MarkFrameDisplayed(response.frame_id);
        }

        /// <summary>
        /// Capture frame from camera
        /// </summary>
        private Texture2D CaptureFrame()
        {
            if (!m_cameraAccess.TryGetTexture(out Texture2D cameraTexture, out _))
            {
                return null;
            }

            return cameraTexture;
        }

        /// <summary>
        /// Update debug UI (optional)
        /// </summary>
        private void UpdateStatusUI()
        {
            if (m_statusText == null)
                return;

            m_statusText.text = $"V3.0 Demo\n" +
                                $"Sent: {m_framesSent}\n" +
                                $"Received: {m_responsesReceived}\n" +
                                $"FPS: {m_targetFPS:F1}\n" +
                                $"Transport: {m_transport.GetStats()}\n" +
                                $"Telemetry: {m_telemetry.GetStats()}";
        }

        /// <summary>
        /// Cleanup on destroy
        /// </summary>
        private void OnDestroy()
        {
            Debug.Log($"[V3 DEMO] Shutting down...");

            // Stop sending frames
            CancelInvoke(nameof(SendNextFrame));

            // Shutdown OOP components
            m_transport?.Shutdown();
            m_telemetry?.Shutdown();

            Debug.Log($"[V3 DEMO] Final stats: Sent={m_framesSent}, Received={m_responsesReceived}");
        }
    }
}
