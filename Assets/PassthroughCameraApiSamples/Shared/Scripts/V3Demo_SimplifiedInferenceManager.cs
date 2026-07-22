// Copyright (c) Meta Platforms, Inc. and affiliates.

using System.Collections;
using PassthroughCameraSamples.Shared;
using PassthroughCameraSamples.Shared.ControlPlane;
using UnityEngine;
using UnityEngine.UI;

namespace PassthroughCameraSamples.Demo
{
    /// <summary>
    /// V3.0 Architecture Demo: Simplified Inference Manager
    ///
    /// Control-plane integration:
    ///   P1: InvokeRepeating → coroutine loop that re-reads TargetFps from ControlKnobs each cycle.
    ///   P2: N gate (InflightCap), MetricsAggregator, render-age sampler in Update().
    ///       Downsample via RenderTexture when profile.ResFactor &lt; 1.
    ///
    /// Standalone: runs at m_initialProfileId profile indefinitely.
    /// Add RuntimeController to this GameObject to enable adaptive policies.
    /// </summary>
    public class V3Demo_SimplifiedInferenceManager : MonoBehaviour, IControlPlaneTarget
    {
        // ====================================================================
        // Configuration
        // ====================================================================
        [Header("Camera")]
        [SerializeField] private PassthroughCameraAccess m_cameraAccess;

        [Header("Control Plane")]
        [Tooltip("Initial OperatingProfile id. Valid values: P1, P2, P3, P4, P5. " +
                 "The RuntimeController (if present) will override this at Start.")]
        [SerializeField] private string m_initialProfileId = "P3";

        [Header("Debug UI (Optional)")]
        [SerializeField] private Text m_statusText;
        [SerializeField] private SharedInferenceHUD m_sharedHUD;

        // ====================================================================
        // V3.0 OOP Components
        // ====================================================================
        private UDPTransportManager m_transport;
        private FrameTelemetryTracker m_telemetry;
        private ControlKnobs m_knobs;
        private MetricsAggregator m_metrics;

        // ====================================================================
        // State
        // ====================================================================
        private string m_sessionId;
        private int m_frameId = 0;
        private bool m_isInitialized = false;
        private Coroutine m_sendLoop;

        // ====================================================================
        // Statistics
        // ====================================================================
        private int m_framesSent      = 0;
        private int m_responsesReceived = 0;
        private int m_nGateDrops      = 0;

        // ====================================================================
        // Public API for RuntimeController (P4)
        // ====================================================================
        public ControlKnobs Knobs    => m_knobs;
        public MetricsAggregator Metrics => m_metrics;
        public string SessionId      => m_sessionId;

        // ── Lifecycle ─────────────────────────────────────────────────────────

        private IEnumerator Start()
        {
            Debug.Log("[V3 DEMO] ========================================");
            Debug.Log("[V3 DEMO] Simplified Inference Manager (V3.0 + Control Plane)");
            Debug.Log("[V3 DEMO] ========================================");

            m_sessionId = System.Guid.NewGuid().ToString();
            Debug.Log($"[V3 DEMO] Session ID: {m_sessionId}");

            // Initialize ControlKnobs from table profile; RuntimeController (if present) may override.
            var initialProfile = OperatingProfile.Get(m_initialProfileId);
            if (initialProfile == null)
            {
                Debug.LogWarning($"[V3 DEMO] Unknown profile id '{m_initialProfileId}', defaulting to P3");
                initialProfile = OperatingProfile.Get("P3");
            }
            m_knobs = new ControlKnobs(initialProfile);

            // MetricsAggregator queries pending count from the telemetry tracker (built below)
            m_metrics = new MetricsAggregator(() => m_telemetry?.GetPendingCount() ?? 0);

            // UDP Transport
            try
            {
                m_transport = new UDPTransportManager(
                    serverIP:    ServerConfig.Instance.ServerIP,
                    sendPort:    8002,
                    receivePort: 8003
                );
                m_transport.Initialize();
                Debug.Log("[V3 DEMO] UDP Transport initialized");
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[V3 DEMO] Failed to initialize UDP transport: {e.Message}");
                yield break;
            }

            // Telemetry Tracker
            m_telemetry = new FrameTelemetryTracker(
                sessionId: m_sessionId,
                sceneName: "V3Demo",
                enableLocalTelemetry: true
            );
            Debug.Log("[V3 DEMO] Telemetry tracker initialized");

            // Wait for camera
            Debug.Log("[V3 DEMO] Waiting for camera...");
            float startTime = Time.time;
            while (!m_cameraAccess.IsPlaying && (Time.time - startTime) < 10f)
                yield return new WaitForSeconds(0.5f);

            if (!m_cameraAccess.IsPlaying)
            {
                Debug.LogError("[V3 DEMO] Camera failed to start after 10 seconds");
                yield break;
            }
            Debug.Log($"[V3 DEMO] Camera ready after {Time.time - startTime:F1}s");

            m_isInitialized = true;
            m_sendLoop = StartCoroutine(SendLoop());

            Debug.Log($"[V3 DEMO] Initialized. profile={m_knobs.CurrentProfile.Id}, " +
                      $"fps={m_knobs.CurrentProfile.TargetFps}");
        }

        private void OnDestroy()
        {
            if (m_sendLoop != null) StopCoroutine(m_sendLoop);
            m_transport?.Shutdown();
            m_telemetry?.Shutdown();
            Debug.Log($"[V3 DEMO] Final stats: Sent={m_framesSent}, " +
                      $"Received={m_responsesReceived}, N-gate drops={m_nGateDrops}");
        }

        // ── Send loop (P1: dynamic pacing) ───────────────────────────────────

        private IEnumerator SendLoop()
        {
            while (true)
            {
                SendNextFrame();
                float interval = 1f / Mathf.Max(0.1f, m_knobs.CurrentProfile.TargetFps);
                yield return new WaitForSeconds(interval);
            }
        }

        // ── Update loop ───────────────────────────────────────────────────────

        private void Update()
        {
            if (!m_isInitialized) return;

            // Drain UDP response queue
            while (m_transport.TryGetResponse(out FrameResponse response))
                HandleResponse(response);

            // Age sampler (P2): A_r = now − unity_send_ts of currently displayed frame
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

            // HUD: update control-plane metrics every frame (cheap)
            if (m_sharedHUD != null)
            {
                var snap = m_metrics.Snapshot();
                m_sharedHUD.UpdateControlPlaneMetrics(snap, m_knobs.CurrentProfile.Id);
            }

            // Cleanup & debug UI
            if (Time.frameCount % 300 == 0)
                m_telemetry.CleanupOldTraces();

            UpdateStatusUI();

#if UNITY_EDITOR
            DebugProfileSwitch();
#endif
        }

        // ── Frame send (P1 + P2) ─────────────────────────────────────────────

        private void SendNextFrame()
        {
            if (!m_isInitialized || !m_cameraAccess.IsPlaying) return;

            OperatingProfile profile = m_knobs.CurrentProfile;

            // N gate (P2): skip if too many frames are already in-flight
            int pending = m_telemetry.GetPendingCount();
            if (pending >= profile.InflightCap)
            {
                m_nGateDrops++;
                m_metrics.RecordDropped();
                Debug.Log($"[V3 DEMO] N gate: pending={pending} >= cap={profile.InflightCap}, " +
                          $"total_gate_drops={m_nGateDrops}");
                return;
            }

            try
            {
                // 1. Capture
                Texture2D frame = CaptureFrame();
                if (frame == null)
                {
                    Debug.LogWarning("[V3 DEMO] Failed to capture frame");
                    return;
                }

                // 2. Resize (P1) — only if profile.ResFactor < 1
                Texture2D toEncode = frame;
                bool didResize = false;
                if (profile.ResFactor < 0.99f)
                {
                    toEncode  = ResizeTexture(frame, profile.ResFactor);
                    didResize = true;
                }

                // 3. JPEG encode at profile quality
                byte[] jpegData = toEncode.EncodeToJPG(profile.JpegQuality);
                int rawBytes = toEncode.width * toEncode.height * 3;  // capture before Destroy
                Destroy(frame);
                if (didResize) Destroy(toEncode);

                // 4. Create frame trace (stamped with profile + policy info)
                FrameTrace trace = m_telemetry.CreateFrame(
                    m_frameId, jpegData.Length, profile, policyId: "");
                trace.upload_bytes_uncompressed = rawBytes;

                // 5. Send via UDP (non-blocking)
                m_transport.SendFrame(trace, jpegData, telemetryJson: null);

                m_metrics.RecordSent();
                m_metrics.PushBytes(jpegData.Length);
                m_framesSent++;
                m_frameId++;

                Debug.Log($"[V3 DEMO] Sent frame {trace.frame_id} " +
                          $"({profile.ResWidth}×{profile.ResHeight}, " +
                          $"q={profile.JpegQuality}, size={jpegData.Length / 1024}KB)");
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[V3 DEMO] Error sending frame {m_frameId}: {e.Message}");
            }
        }

        // ── Response handling ─────────────────────────────────────────────────

        private void HandleResponse(FrameResponse response)
        {
            m_responsesReceived++;
            Debug.Log($"[V3 DEMO] Received frame {response.frame_id}, " +
                      $"server_proc={response.processing_time_ms:F1}ms, " +
                      $"queue_wait={response.queue_wait_ms:F1}ms");

            m_telemetry.MarkFrameCompleted(response.frame_id, response);

            // Push to metrics aggregator (P2)
            m_metrics.PushLatency(response.latency_ms);

            if (response.HasDetections())
                Debug.Log($"[V3 DEMO] Frame {response.frame_id}: {response.detections.Length} detections");
            else if (response.HasPose())
                Debug.Log($"[V3 DEMO] Frame {response.frame_id}: {response.persons.Length} persons");
            else if (response.HasSegmentation())
                Debug.Log($"[V3 DEMO] Frame {response.frame_id}: " +
                          $"Seg mask {response.segmentation.mask_width}×{response.segmentation.mask_height}");

            // HUD update
            if (m_sharedHUD != null)
                m_sharedHUD.UpdateMetrics(response);

            m_telemetry.MarkFrameDisplayed(response.frame_id);
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private Texture2D CaptureFrame()
        {
            if (!m_cameraAccess.TryGetTexture(out Texture2D cameraTexture, out _))
                return null;
            return cameraTexture;
        }

        /// <summary>
        /// Resize a texture by resFactor using Graphics.Blit into a temporary RenderTexture.
        /// Supports any fractional scale (0.75, 0.5, etc.) not just integer divisors.
        /// Returns a NEW Texture2D — caller is responsible for Destroy().
        /// </summary>
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

        private void UpdateStatusUI()
        {
            if (m_statusText == null) return;
            var p = m_knobs.CurrentProfile;
            m_statusText.text =
                $"V3.0 Demo | Profile: {p.Id}\n" +
                $"Sent: {m_framesSent}  Recv: {m_responsesReceived}  N-drops: {m_nGateDrops}\n" +
                $"FPS: {p.TargetFps:F1}  q={p.JpegQuality}  cap={p.InflightCap}\n" +
                $"Transport: {m_transport?.GetStats()}\n" +
                $"Telemetry: {m_telemetry?.GetStats()}";
        }

#if UNITY_EDITOR
        // Keyboard 1–5 in Editor: force-switch profile for quick testing
        private void DebugProfileSwitch()
        {
            if (Input.GetKeyDown(KeyCode.Alpha1)) m_knobs.Apply(OperatingProfile.Get("P1"));
            if (Input.GetKeyDown(KeyCode.Alpha2)) m_knobs.Apply(OperatingProfile.Get("P2"));
            if (Input.GetKeyDown(KeyCode.Alpha3)) m_knobs.Apply(OperatingProfile.Get("P3"));
            if (Input.GetKeyDown(KeyCode.Alpha4)) m_knobs.Apply(OperatingProfile.Get("P4"));
            if (Input.GetKeyDown(KeyCode.Alpha5)) m_knobs.Apply(OperatingProfile.Get("P5"));
        }
#endif
    }
}
