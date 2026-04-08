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
        /// Update HUD with latest inference metrics including detailed latency breakdown.
        /// </summary>
        public void UpdateMetrics(
            float e2eMs,
            float uploadMs,
            float serverProcMs,
            float downloadMs,
            float parseMs,
            int uploadBytes,
            int downloadBytes,
            int downloadBytesCompressed,
            int detectionCount,
            float avgConfidence,
            float keypointAvgConf = 0f)
        {
            m_e2eMs = e2eMs;
            m_uploadMs = uploadMs;
            m_serverProcMs = serverProcMs;
            m_downloadMs = downloadMs;
            m_parseMs = parseMs;
            m_uploadBytes = uploadBytes;
            m_downloadBytes = downloadBytes;
            m_downloadBytesCompressed = downloadBytesCompressed;
            m_detectionCount = detectionCount;
            m_avgConfidence = avgConfidence;
            m_keypointAvgConf = keypointAvgConf;

            // Calculate actual inference FPS (NOT Unity rendering FPS!)
            float inferenceFPS = e2eMs > 0 ? 1000f / e2eMs : 0f;
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
                return;

            // Calculate average INFERENCE FPS (NOT rendering FPS!)
            float avgInferenceFPS = m_inferenceFpsHistory.Count > 0 ? m_inferenceFpsHistory.Average() : 0f;

            // Calculate session duration
            float sessionDuration = Time.time - m_sessionStartTime;

            // Build metrics string
            string metricsStr = $"<b>{m_currentMode}</b>\n";
            metricsStr += $"<color=#00FF00>Inference FPS: {avgInferenceFPS:F1}</color> (target: {m_targetFPS:F1})\n";
            metricsStr += $"E2E: {m_e2eMs:F0}ms\n";

            if (m_showDetailedMetrics)
            {
                // Calculate percentages
                float uploadPct = m_e2eMs > 0 ? (m_uploadMs / m_e2eMs) * 100f : 0f;
                float serverPct = m_e2eMs > 0 ? (m_serverProcMs / m_e2eMs) * 100f : 0f;
                float downloadPct = m_e2eMs > 0 ? (m_downloadMs / m_e2eMs) * 100f : 0f;
                float parsePct = m_e2eMs > 0 ? (m_parseMs / m_e2eMs) * 100f : 0f;

                metricsStr += $" ├Upload: {m_uploadMs:F0}ms ({uploadPct:F0}%)\n";
                metricsStr += $" ├Server: {m_serverProcMs:F0}ms ({serverPct:F0}%)\n";
                metricsStr += $" ├Download: {m_downloadMs:F0}ms ({downloadPct:F0}%)\n";
                metricsStr += $" └Parse: {m_parseMs:F0}ms ({parsePct:F0}%)\n";
            }

            // Detection metrics
            metricsStr += $"Detections: {m_detectionCount}\n";

            if (m_avgConfidence > 0f)
            {
                metricsStr += $"Avg Conf: {m_avgConfidence:F2}\n";
            }

            if (m_keypointAvgConf > 0f)
            {
                metricsStr += $"Keypoint Conf: {m_keypointAvgConf:F2}\n";
            }

            // Bandwidth metrics
            if (m_showDetailedMetrics)
            {
                metricsStr += $"<size=80%>Upload: {FormatBytes(m_uploadBytes)}\n";
                metricsStr += $"Download: {FormatBytes(m_downloadBytes)}</size>\n";
            }

            // Frame statistics (Part D)
            metricsStr += $"\n<b>Frame Stats</b> ({sessionDuration:F0}s)\n";
            metricsStr += $"Total: {m_totalFrames}\n";

            if (m_droppedFrames > 0 || m_frozenFrames > 0)
            {
                metricsStr += $"<color=#FFAA00>Dropped: {m_droppedFrames} ({GetDropFrameRatio() * 100f:F1}%)</color>\n";
                metricsStr += $"<color=#FFAA00>Frozen: {m_frozenFrames} ({GetFreezeFrameRatio() * 100f:F1}%)</color>";
            }
            else
            {
                metricsStr += $"Dropped: 0\nFrozen: 0";
            }

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
