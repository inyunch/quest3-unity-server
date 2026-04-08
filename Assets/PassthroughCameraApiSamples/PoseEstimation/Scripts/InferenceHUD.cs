// Copyright (c) Meta Platforms, Inc. and affiliates.

using UnityEngine;
using TMPro;
using System.Collections.Generic;
using System.Linq;

namespace PassthroughCameraSamples.PoseEstimation
{
    /// <summary>
    /// Real-time HUD overlay for inference metrics.
    /// Shows FPS, latency, processing time, detection count, and confidence.
    /// </summary>
    public class InferenceHUD : MonoBehaviour
    {
        [Header("UI References")]
        [SerializeField] private TextMeshProUGUI m_metricsText;

        [Header("FPS Settings")]
        [SerializeField] private int m_fpsAverageSamples = 30;

        private Queue<float> m_fpsHistory = new Queue<float>();
        private float m_lastUpdateTime;

        // Latest metrics
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
        private float m_keypointAvgConf = 0f;

        private void Start()
        {
            Debug.Log("[HUD] InferenceHUD started");

            if (m_metricsText == null)
            {
                Debug.LogError("[HUD] TextMeshProUGUI reference is null! Please assign in Inspector.");
            }

            m_lastUpdateTime = Time.time;
        }

        private void Update()
        {
            // Update FPS calculation every frame
            float deltaTime = Time.time - m_lastUpdateTime;
            if (deltaTime > 0f)
            {
                float currentFPS = 1f / deltaTime;
                m_fpsHistory.Enqueue(currentFPS);

                // Keep only the last N samples
                while (m_fpsHistory.Count > m_fpsAverageSamples)
                {
                    m_fpsHistory.Dequeue();
                }
            }
            m_lastUpdateTime = Time.time;

            // Update HUD display
            UpdateDisplay();
        }

        /// <summary>
        /// Update HUD with latest inference metrics including detailed latency breakdown.
        /// </summary>
        public void UpdateHUD(
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

            Debug.Log($"[HUD] E2E={e2eMs:F0}ms (up={uploadMs:F0}ms srv={serverProcMs:F0}ms " +
                     $"down={downloadMs:F0}ms parse={parseMs:F0}ms) count={detectionCount}");
        }

        private void UpdateDisplay()
        {
            if (m_metricsText == null)
                return;

            // Calculate average FPS
            float avgFPS = m_fpsHistory.Count > 0 ? m_fpsHistory.Average() : 0f;

            // Calculate percentages
            float uploadPct = m_e2eMs > 0 ? (m_uploadMs / m_e2eMs) * 100f : 0f;
            float serverPct = m_e2eMs > 0 ? (m_serverProcMs / m_e2eMs) * 100f : 0f;
            float downloadPct = m_e2eMs > 0 ? (m_downloadMs / m_e2eMs) * 100f : 0f;
            float parsePct = m_e2eMs > 0 ? (m_parseMs / m_e2eMs) * 100f : 0f;

            // Build metrics string with breakdown
            string metricsStr = $"FPS: {avgFPS:F1}\n";
            metricsStr += $"E2E: {m_e2eMs:F0}ms\n";
            metricsStr += $" ├Upload: {m_uploadMs:F0}ms ({uploadPct:F0}%)\n";
            metricsStr += $" ├Server: {m_serverProcMs:F0}ms ({serverPct:F0}%)\n";
            metricsStr += $" ├Download: {m_downloadMs:F0}ms ({downloadPct:F0}%)\n";
            metricsStr += $" └Parse: {m_parseMs:F0}ms ({parsePct:F0}%)\n";
            metricsStr += $"Persons: {m_detectionCount}\n";
            metricsStr += $"Avg Conf: {m_avgConfidence:F2}\n";

            // Add keypoint confidence if available
            if (m_keypointAvgConf > 0f)
            {
                metricsStr += $"Keypoint: {m_keypointAvgConf:F2}\n";
            }

            // Add upload/download sizes
            metricsStr += $"Upload: {FormatBytes(m_uploadBytes)}\n";
            metricsStr += $"Download: {FormatBytes(m_downloadBytes)}\n";
            metricsStr += $"DL Compressed: {FormatBytes(m_downloadBytesCompressed)}";

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
    }
}
