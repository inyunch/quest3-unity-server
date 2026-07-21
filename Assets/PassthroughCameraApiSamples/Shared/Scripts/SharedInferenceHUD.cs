// Copyright (c) Meta Platforms, Inc. and affiliates.

using UnityEngine;
using TMPro;
using System.Collections.Generic;
using System.Linq;

namespace PassthroughCameraSamples.Shared
{
    /// <summary>
    /// Enhanced real-time HUD overlay for inference metrics.
    /// Shows mode, actual inference FPS, latency breakdown, detection count, and frame statistics.
    ///
    /// IMPORTANT: FPS shown is ACTUAL INFERENCE FPS (1000/E2E_ms), not Unity rendering FPS.
    /// </summary>
    public class SharedInferenceHUD : MonoBehaviour
    {
        [Header("UI References")]
        [SerializeField] private TextMeshProUGUI m_metricsText;
        [SerializeField] private bool m_showDetailedMetrics = true;

        [Header("FPS Calculation")]
        [SerializeField] private int m_fpsAverageSamples = 10;  // Use fewer samples for inference FPS

        // FPS history for averaging
        private Queue<float> m_inferenceFpsHistory = new Queue<float>();

        // Latest metrics
        private string m_currentMode = "Unknown";
        private float m_e2eMs = 0f;
        private float m_uploadMs = 0f;
        private float m_serverProcMs = 0f;
        private float m_downloadMs = 0f;
        private float m_parseMs = 0f;
        private int m_uploadBytes = 0;
        private int m_downloadBytes = 0;
        private int m_downloadBytesCompressed = 0;
        private int m_detectionCount = 0;
        private float m_avgConfidence = 0f;
        private float m_keypointAvgConf = 0f;  // For pose mode

        // Frame statistics (Part D)
        private int m_totalFrames = 0;
        private int m_droppedFrames = 0;
        private int m_frozenFrames = 0;
        private float m_sessionStartTime = 0f;

        // Target FPS for reference
        private float m_targetFPS = 10f;

        // Control-plane metrics (P2)
        private float  m_p95Latency   = 0f;
        private float  m_meanAge      = 0f;
        private int    m_pendingN     = 0;
        private string m_profileId    = "";

        private void Start()
        {
            Debug.Log("[SharedHUD] SharedInferenceHUD started");

            if (m_metricsText == null)
            {
                Debug.LogError("[SharedHUD] TextMeshProUGUI reference is null! Please assign in Inspector.");
            }

            m_sessionStartTime = Time.time;
        }

        private void Update()
        {
            // Update HUD display every frame (lightweight)
            UpdateDisplay();
        }

        /// <summary>
        /// Set the current inference mode for display.
        /// </summary>
        public void SetMode(InferenceMode mode, float targetFPS)
        {
            m_currentMode = GetModeDisplayName(mode);
            m_targetFPS = targetFPS;
        }

        /// <summary>
        /// Update HUD with latest inference metrics from FrameResponse.
        /// Network time is calculated as: E2E - Server - Parse (directly from server timing).
        /// </summary>
        public void UpdateMetrics(FrameResponse response)
        {
            // Use server-provided timing breakdown
            m_e2eMs = response.latency_ms;  // Total E2E (Unity → Server → Unity)
            float serverE2eMs = response.server_e2e_ms;  // Queue + Processing
            m_parseMs = response.parse_ms;  // JSON parse time

            // Calculate network time = E2E - Server - Parse
            // This gives us Upload + Download time directly from server timestamps
            float networkMs = m_e2eMs - serverE2eMs - m_parseMs;
            m_uploadMs = networkMs / 2;  // Approximate split (not used in display)
            m_downloadMs = networkMs / 2;  // Approximate split (not used in display)

            m_serverProcMs = response.processing_time_ms;  // Actual inference time

            // Detection metrics
            m_detectionCount = response.detections?.Length ?? 0;

            // Calculate average confidence
            if (response.detections != null && response.detections.Length > 0)
            {
                float sum = 0f;
                foreach (var det in response.detections)
                {
                    sum += det.confidence;
                }
                m_avgConfidence = sum / response.detections.Length;
            }
            else
            {
                m_avgConfidence = 0f;
            }

            // Keypoint confidence (for pose mode)
            if (response.persons != null && response.persons.Length > 0)
            {
                float sum = 0f;
                int count = 0;
                foreach (var person in response.persons)
                {
                    if (person.keypoints != null)
                    {
                        foreach (var kp in person.keypoints)
                        {
                            sum += kp.score;
                            count++;
                        }
                    }
                }
                m_keypointAvgConf = count > 0 ? sum / count : 0f;
            }
            else
            {
                m_keypointAvgConf = 0f;
            }

            // Calculate actual inference FPS (NOT Unity rendering FPS!)
            float inferenceFPS = m_e2eMs > 0 ? 1000f / m_e2eMs : 0f;
            m_inferenceFpsHistory.Enqueue(inferenceFPS);

            // Keep only the last N samples
            while (m_inferenceFpsHistory.Count > m_fpsAverageSamples)
            {
                m_inferenceFpsHistory.Dequeue();
            }

            m_totalFrames++;
        }

        /// <summary>
        /// Report a dropped frame (frame skipped due to timing constraints).
        /// </summary>
        public void ReportDroppedFrame()
        {
            m_droppedFrames++;
            Debug.Log($"[SharedHUD] Dropped frame reported. Total: {m_droppedFrames}/{m_totalFrames}");
        }

        /// <summary>
        /// Report a frozen frame (no fresh inference result, old visualization displayed).
        /// </summary>
        public void ReportFrozenFrame()
        {
            m_frozenFrames++;
        }

        /// <summary>
        /// Update the HUD with the latest control-plane window metrics.
        /// Call from RuntimeController or the manager's Update() every epoch (or every frame is fine).
        /// </summary>
        public void UpdateControlPlaneMetrics(
            ControlPlane.MetricsSnapshot snapshot, string profileId)
        {
            m_p95Latency = snapshot.P95L;
            m_meanAge    = snapshot.MeanA;
            m_pendingN   = snapshot.PendingN;
            m_profileId  = profileId ?? "";
        }

        /// <summary>
        /// Reset frame statistics.
        /// </summary>
        public void ResetStatistics()
        {
            m_totalFrames = 0;
            m_droppedFrames = 0;
            m_frozenFrames = 0;
            m_sessionStartTime = Time.time;
            m_inferenceFpsHistory.Clear();
            Debug.Log("[SharedHUD] Statistics reset");
        }

        /// <summary>
        /// Get the current drop frame ratio.
        /// </summary>
        public float GetDropFrameRatio()
        {
            return m_totalFrames > 0 ? (float)m_droppedFrames / m_totalFrames : 0f;
        }

        /// <summary>
        /// Get the current freeze frame ratio.
        /// </summary>
        public float GetFreezeFrameRatio()
        {
            return m_totalFrames > 0 ? (float)m_frozenFrames / m_totalFrames : 0f;
        }

        private void UpdateDisplay()
        {
            if (m_metricsText == null)
            {
                Debug.LogWarning("[SharedHUD DISPLAY] m_metricsText is NULL!");
                return;
            }

            // Get current timestamp with milliseconds
            System.DateTime now = System.DateTime.Now;
            string timestamp = now.ToString("yyyy-MM-dd HH:mm:ss.fff");

            // Build simplified metrics string - ONLY TIMESTAMP AND E2E LATENCY
            string metricsStr = $"<b>Time:</b> {timestamp}\n\n";
            metricsStr += $"<b>E2E Latency:</b> <color=#00FF00>{m_e2eMs:F0}ms</color>";

            // Control-plane overlay (shown when a profile is active)
            if (!string.IsNullOrEmpty(m_profileId))
            {
                metricsStr += $"\n<b>Profile:</b> {m_profileId}  N={m_pendingN}";
                metricsStr += $"\n<b>p95:</b> {m_p95Latency:F0}ms  <b>Age:</b> {m_meanAge:F0}ms";
            }

            m_metricsText.text = metricsStr;

            // Debug: Log every 30 frames to verify display is updating
            if (Time.frameCount % 30 == 0)
            {
                Debug.Log($"[SharedHUD DISPLAY] Showing E2E={m_e2eMs:F0}ms at {timestamp}");
            }

            // All other metrics commented out below:

            // Calculate average INFERENCE FPS (NOT rendering FPS!)
            // float avgInferenceFPS = m_inferenceFpsHistory.Count > 0 ? m_inferenceFpsHistory.Average() : 0f;

            // Calculate session duration
            // float sessionDuration = Time.time - m_sessionStartTime;

            // Calculate network time (upload + download)
            // float networkMs = m_uploadMs + m_downloadMs;

            // Mode name
            // string metricsStr = $"<b>{m_currentMode}</b>\n";
            // metricsStr += $"<color=#00FF00>FPS: {avgInferenceFPS:F1}</color> (target: {m_targetFPS:F1})\n\n";

            // Network time
            // metricsStr += $"Network: {networkMs:F0}ms\n\n";

            // Detection metrics (commented out)
            // metricsStr += $"<b>Detection</b>\n";
            // metricsStr += $"Count: {m_detectionCount}\n";
            // if (m_avgConfidence > 0f)
            // {
            //     metricsStr += $"Conf: {m_avgConfidence:F2}\n";
            // }
            // if (m_keypointAvgConf > 0f)
            // {
            //     metricsStr += $"Keypoint: {m_keypointAvgConf:F2}\n";
            // }

            // Frame statistics (commented out)
            // metricsStr += $"\n<b>Frames</b> ({sessionDuration:F0}s)\n";
            // metricsStr += $"Total: {m_totalFrames}\n";
            // if (m_droppedFrames > 0 || m_frozenFrames > 0)
            // {
            //     metricsStr += $"<color=#FFAA00>Dropped: {m_droppedFrames}</color>\n";
            //     metricsStr += $"<color=#FFAA00>Frozen: {m_frozenFrames}</color>";
            // }
            // else
            // {
            //     metricsStr += $"Dropped: 0\nFrozen: 0";
            // }

            m_metricsText.text = metricsStr;
        }

        private string FormatBytes(int bytes)
        {
            if (bytes < 1024)
                return $"{bytes}B";
            if (bytes < 1024 * 1024)
                return $"{bytes / 1024f:F1}KB";
            return $"{bytes / (1024f * 1024f):F2}MB";
        }

        private string GetModeDisplayName(InferenceMode mode)
        {
            switch (mode)
            {
                case InferenceMode.ObjectDetection:
                    return "Object Detection";
                case InferenceMode.PoseEstimation:
                    return "Pose Estimation";
                case InferenceMode.Both:
                    return "Detection + Pose";
                case InferenceMode.DepthEstimation:
                    return "Depth Estimation";
                case InferenceMode.Segmentation:
                    return "Segmentation";
                case InferenceMode.SegmentationWithDepth:
                    return "Segmentation + Depth";
                default:
                    return "Unknown";
            }
        }

        /// <summary>
        /// Get a summary of session statistics for logging.
        /// </summary>
        public string GetSessionSummary()
        {
            float sessionDuration = Time.time - m_sessionStartTime;
            float avgInferenceFPS = m_inferenceFpsHistory.Count > 0 ? m_inferenceFpsHistory.Average() : 0f;

            string summary = $"=== Session Summary ({m_currentMode}) ===\n";
            summary += $"Duration: {sessionDuration:F1}s\n";
            summary += $"Total Frames: {m_totalFrames}\n";
            summary += $"Avg Inference FPS: {avgInferenceFPS:F2}\n";
            summary += $"Avg E2E Latency: {m_e2eMs:F1}ms\n";
            summary += $"Dropped Frames: {m_droppedFrames} ({GetDropFrameRatio() * 100f:F1}%)\n";
            summary += $"Frozen Frames: {m_frozenFrames} ({GetFreezeFrameRatio() * 100f:F1}%)\n";
            summary += $"Avg Upload: {FormatBytes(m_uploadBytes)}\n";
            summary += $"Avg Download: {FormatBytes(m_downloadBytes)}";

            return summary;
        }

        private void OnDestroy()
        {
            // Log session summary when HUD is destroyed
            if (m_totalFrames > 0)
            {
                Debug.Log($"[SharedHUD]\n{GetSessionSummary()}");
            }
        }
    }
}
