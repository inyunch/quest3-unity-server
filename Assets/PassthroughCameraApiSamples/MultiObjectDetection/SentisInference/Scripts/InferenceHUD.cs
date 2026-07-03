// Copyright (c) Meta Platforms, Inc. and affiliates.

using UnityEngine;
using TMPro;
using System.Collections.Generic;
using System.Linq;

namespace PassthroughCameraSamples.MultiObjectDetection
{
    /// <summary>
    /// Real-time HUD overlay for inference metrics.
    /// Shows FPS, latency breakdown, detection count, and confidence.
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

        private void Start()
        {
            Debug.Log("[HUD] InferenceHUD started");
            Debug.Log($"[HUD] GameObject: {gameObject.name}, Parent: {transform.parent?.name}");

            if (m_metricsText == null)
            {
                Debug.LogError("[HUD] TextMeshProUGUI reference is null! Please assign in Inspector.");
            }
            else
            {
                Debug.Log($"[HUD] TextMeshProUGUI connected: {m_metricsText.name}");
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
            float avgConfidence)
        {
            Debug.Log($"[HUD] UpdateHUD called: e2e={e2eMs:F1}ms, stored_before={m_e2eMs:F1}ms");

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

            Debug.Log($"[HUD] UpdateHUD stored: e2e={m_e2eMs:F1}ms, upload={m_uploadMs:F1}ms, server={m_serverProcMs:F1}ms, download={m_downloadMs:F1}ms, parse={m_parseMs:F1}ms");
            Debug.Log($"[HUD] E2E={e2eMs:F0}ms (up={uploadMs:F0}ms srv={serverProcMs:F0}ms " +
                     $"down={downloadMs:F0}ms parse={parseMs:F0}ms) count={detectionCount}");
        }

        private void UpdateDisplay()
        {
            if (m_metricsText == null)
            {
                Debug.LogWarning("[HUD DISPLAY] m_metricsText is NULL!");
                return;
            }

            // Get current timestamp with milliseconds
            System.DateTime now = System.DateTime.Now;
            string timestamp = now.ToString("yyyy-MM-dd HH:mm:ss.fff");

            // Build simplified metrics string - ONLY TIMESTAMP AND E2E LATENCY
            float avgFPS = m_fpsHistory.Count > 0 ? m_fpsHistory.Average() : 0f;
            float networkMs = Mathf.Max(0f, m_e2eMs - m_serverProcMs);
            string metricsStr = $"<mark=#000000A0><size=150%><b>FPS:</b> <color=#FFFFFF>{avgFPS:F1}</color></size></mark>\n";
            metricsStr += $"<mark=#000000A0><size=150%><b>E2E Latency:</b> <color=#00FF00>{m_e2eMs:F0}ms</color></size></mark>\n";
            metricsStr += $"<mark=#000000A0><size=150%><b>Server:</b> <color=#FFFF00>{m_serverProcMs:F0}ms</color></size></mark>\n";
            metricsStr += $"<mark=#000000A0><size=150%><b>Network:</b> <color=#00FFFF>{networkMs:F0}ms</color></size></mark>";

            // string metricsStr = $"<b>Time:</b> {timestamp}\n\n";
            // metricsStr += $"<b>E2E Latency:</b> <color=#00FF00>{m_e2eMs:F0}ms</color>";

            m_metricsText.text = metricsStr;

            // Debug: Log every 30 frames to verify display is updating
            if (Time.frameCount % 30 == 0)
            {
                Debug.Log($"[HUD DISPLAY] Showing E2E={m_e2eMs:F0}ms at {timestamp}");
            }

            // All other metrics commented out below:

            // Calculate average FPS
            // float avgFPS = m_fpsHistory.Count > 0 ? m_fpsHistory.Average() : 0f;

            // Debug.Log($"[HUD DISPLAY] UpdateDisplay using: e2e={m_e2eMs:F1}ms, upload={m_uploadMs:F1}ms, server={m_serverProcMs:F1}ms, download={m_downloadMs:F1}ms, parse={m_parseMs:F1}ms");

            // Calculate percentages
            // float uploadPct = m_e2eMs > 0 ? (m_uploadMs / m_e2eMs) * 100f : 0f;
            // float serverPct = m_e2eMs > 0 ? (m_serverProcMs / m_e2eMs) * 100f : 0f;
            // float downloadPct = m_e2eMs > 0 ? (m_downloadMs / m_e2eMs) * 100f : 0f;
            // float parsePct = m_e2eMs > 0 ? (m_parseMs / m_e2eMs) * 100f : 0f;

            // Build metrics string with breakdown
            // string metricsStr = $"FPS: {avgFPS:F1}\n";
            // metricsStr += $"E2E: {m_e2eMs:F0}ms\n";
            // metricsStr += $" ├Upload: {m_uploadMs:F0}ms ({uploadPct:F0}%)\n";
            // metricsStr += $" ├Server: {m_serverProcMs:F0}ms ({serverPct:F0}%)\n";
            // metricsStr += $" ├Download: {m_downloadMs:F0}ms ({downloadPct:F0}%)\n";
            // metricsStr += $" └Parse: {m_parseMs:F0}ms ({parsePct:F0}%)\n";
            // metricsStr += $"Objects: {m_detectionCount}\n";
            // metricsStr += $"Avg Conf: {m_avgConfidence:F2}\n";

            // Add upload/download sizes
            // metricsStr += $"Upload: {FormatBytes(m_uploadBytes)}\n";
            // metricsStr += $"Download: {FormatBytes(m_downloadBytes)}\n";
            // metricsStr += $"DL Compressed: {FormatBytes(m_downloadBytesCompressed)}";
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
