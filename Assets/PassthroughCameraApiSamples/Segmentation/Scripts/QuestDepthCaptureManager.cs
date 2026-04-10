// Copyright (c) Meta Platforms, Inc. and affiliates.

using System;
using System.Collections;
using UnityEngine;

namespace PassthroughCameraSamples.Segmentation
{
    /// <summary>
    /// Manages Quest 3 Environment Depth API for RGB-D segmentation pipeline.
    ///
    /// INTEGRATION NOTE:
    /// This is a stub implementation. To use real Quest 3 depth:
    /// 1. Install Meta XR Environment Depth package
    /// 2. Uncomment the ACTUAL_DEPTH_API #define below
    /// 3. Replace stub methods with actual EnvironmentDepthManager API calls
    ///
    /// For now, this provides the interface without requiring Meta SDK.
    /// </summary>
    public class QuestDepthCaptureManager : MonoBehaviour
    {
        // TODO: Uncomment this when Meta XR SDK is properly configured
        // #define ACTUAL_DEPTH_API

        [Header("Depth Settings")]
        [SerializeField] private bool m_enableOnStart = false;
        [SerializeField] private float m_depthAvailableTimeout = 2.0f;  // seconds

        [Header("Debug")]
        [SerializeField] private bool m_verboseLogging = true;

        // Depth state
        private bool m_depthEnabled = false;
        private bool m_depthAvailable = false;
        private bool m_depthSupported = false;
        private float m_lastDepthCaptureTime = 0f;

        // Depth data
        private Texture2D m_latestDepthTexture = null;
        private float[] m_latestDepthData = null;
        private int m_depthWidth = 0;
        private int m_depthHeight = 0;

        // Scene model tracking
        private bool m_sceneModelAvailable = false;

        // Events
        public event Action OnDepthBecameAvailable;
        public event Action OnDepthBecameUnavailable;

        // Properties
        public bool IsSupported => m_depthSupported;
        public bool IsEnabled => m_depthEnabled;
        public bool IsAvailable => m_depthAvailable;
        public bool SceneModelAvailable => m_sceneModelAvailable;
        public float LastCaptureTime => m_lastDepthCaptureTime;
        public int DepthWidth => m_depthWidth;
        public int DepthHeight => m_depthHeight;

        private void Start()
        {
            CheckDepthSupport();

            if (m_enableOnStart && m_depthSupported)
            {
                EnableDepth();
            }

            CheckSceneModelAvailability();
        }

        /// <summary>
        /// Check if Environment Depth API is supported on this device.
        /// </summary>
        private void CheckDepthSupport()
        {
#if ACTUAL_DEPTH_API && UNITY_ANDROID && !UNITY_EDITOR
            // TODO: Replace with actual Meta XR API
            // m_depthSupported = EnvironmentDepthManager.IsSupported;
            m_depthSupported = true;
#else
            // Stub implementation - depth not available without Meta SDK
            m_depthSupported = false;

            if (m_verboseLogging)
            {
                Debug.LogWarning("[QUEST DEPTH] Using STUB implementation - Meta XR Environment Depth SDK not configured");
                Debug.LogWarning("[QUEST DEPTH] To enable real depth: Install Meta XR SDK and define ACTUAL_DEPTH_API");
            }
#endif

            if (m_verboseLogging)
            {
                if (m_depthSupported)
                {
                    Debug.Log("[QUEST DEPTH] Environment Depth API is SUPPORTED on this device");
                }
                else
                {
                    Debug.LogWarning("[QUEST DEPTH] Environment Depth API is NOT SUPPORTED on this device");
                }
            }
        }

        /// <summary>
        /// Check if scene model / space setup is available.
        /// </summary>
        private void CheckSceneModelAvailability()
        {
            // Stub - always false unless Meta SDK is integrated
            m_sceneModelAvailable = false;

            if (m_verboseLogging)
            {
                Debug.Log($"[QUEST DEPTH] Scene model available: {m_sceneModelAvailable}");
            }
        }

        /// <summary>
        /// Enable Environment Depth capture.
        /// </summary>
        public void EnableDepth()
        {
            if (!m_depthSupported)
            {
                Debug.LogError("[QUEST DEPTH] Cannot enable depth - API not supported (using stub implementation)");
                return;
            }

            if (m_depthEnabled)
            {
                if (m_verboseLogging)
                {
                    Debug.Log("[QUEST DEPTH] Depth already enabled, skipping");
                }
                return;
            }

            Debug.Log("[QUEST DEPTH] Enabling Environment Depth...");

#if ACTUAL_DEPTH_API && UNITY_ANDROID && !UNITY_EDITOR
            // TODO: Replace with actual Meta XR API
            // EnvironmentDepthManager.enabled = true;
#endif

            m_depthEnabled = true;

            // Start coroutine to wait for depth availability
            StartCoroutine(WaitForDepthAvailable());
        }

        /// <summary>
        /// Disable Environment Depth capture to save resources.
        /// </summary>
        public void DisableDepth()
        {
            if (!m_depthEnabled)
            {
                return;
            }

            Debug.Log("[QUEST DEPTH] Disabling Environment Depth...");

#if ACTUAL_DEPTH_API && UNITY_ANDROID && !UNITY_EDITOR
            // TODO: Replace with actual Meta XR API
            // EnvironmentDepthManager.enabled = false;
#endif

            m_depthEnabled = false;
            m_depthAvailable = false;

            OnDepthBecameUnavailable?.Invoke();
        }

        /// <summary>
        /// Wait for depth to become available after enabling.
        /// </summary>
        private IEnumerator WaitForDepthAvailable()
        {
            float startTime = Time.realtimeSinceStartup;
            bool wasAvailable = false;

            Debug.Log($"[QUEST DEPTH] Waiting for depth availability (timeout: {m_depthAvailableTimeout}s)...");

            while (Time.realtimeSinceStartup - startTime < m_depthAvailableTimeout)
            {
#if ACTUAL_DEPTH_API && UNITY_ANDROID && !UNITY_EDITOR
                // TODO: Replace with actual Meta XR API
                // bool isAvailable = EnvironmentDepthManager.IsDepthAvailable;
                bool isAvailable = false;
#else
                // Stub - never becomes available
                bool isAvailable = false;
#endif

                if (isAvailable && !wasAvailable)
                {
                    m_depthAvailable = true;
                    wasAvailable = true;

                    float elapsedMs = (Time.realtimeSinceStartup - startTime) * 1000f;
                    Debug.Log($"[QUEST DEPTH] Depth became AVAILABLE after {elapsedMs:F1}ms");

                    OnDepthBecameAvailable?.Invoke();
                    yield break;
                }

                yield return null;
            }

            // Timeout
            if (!wasAvailable)
            {
                Debug.LogError($"[QUEST DEPTH] Depth did NOT become available within {m_depthAvailableTimeout}s timeout (using stub implementation)");
                m_depthAvailable = false;
            }
        }

        /// <summary>
        /// Capture the current depth frame.
        /// Returns true if successful.
        /// </summary>
        public bool CaptureDepthFrame()
        {
            if (!m_depthEnabled || !m_depthAvailable)
            {
                if (m_verboseLogging)
                {
                    Debug.LogWarning($"[QUEST DEPTH] Cannot capture - enabled={m_depthEnabled}, available={m_depthAvailable}");
                }
                return false;
            }

            try
            {
#if ACTUAL_DEPTH_API && UNITY_ANDROID && !UNITY_EDITOR
                // TODO: Replace with actual Meta XR API
                // m_latestDepthTexture = EnvironmentDepthManager.GetDepthTexture();

                // For now, return false (stub)
                Debug.LogWarning("[QUEST DEPTH] CaptureDepthFrame() called but using stub implementation");
                return false;
#else
                // Stub implementation
                if (m_verboseLogging)
                {
                    Debug.LogWarning("[QUEST DEPTH] CaptureDepthFrame() - stub implementation, no real depth captured");
                }
                return false;
#endif
            }
            catch (Exception e)
            {
                Debug.LogError($"[QUEST DEPTH] Capture failed: {e.Message}\n{e.StackTrace}");
                return false;
            }
        }

        /// <summary>
        /// Get the latest captured depth data.
        /// Returns null if no depth available.
        /// </summary>
        public float[] GetLatestDepthData()
        {
            return m_latestDepthData;
        }

        /// <summary>
        /// Get the latest depth texture.
        /// Returns null if no depth available.
        /// </summary>
        public Texture2D GetLatestDepthTexture()
        {
            return m_latestDepthTexture;
        }

        /// <summary>
        /// Get depth metadata for logging.
        /// </summary>
        public DepthMetadata GetDepthMetadata()
        {
            return new DepthMetadata
            {
                enabled = m_depthEnabled,
                available = m_depthAvailable,
                supported = m_depthSupported,
                sceneModelAvailable = m_sceneModelAvailable,
                width = m_depthWidth,
                height = m_depthHeight,
                timestamp = m_lastDepthCaptureTime
            };
        }

        private void Update()
        {
            // Stub - no monitoring in stub implementation
        }

        private void OnDestroy()
        {
            if (m_depthEnabled)
            {
                DisableDepth();
            }
        }

        /// <summary>
        /// Depth metadata structure for logging.
        /// </summary>
        [Serializable]
        public struct DepthMetadata
        {
            public bool enabled;
            public bool available;
            public bool supported;
            public bool sceneModelAvailable;
            public int width;
            public int height;
            public float timestamp;
        }
    }
}
