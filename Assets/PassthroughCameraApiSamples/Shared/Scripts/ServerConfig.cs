// Copyright (c) Meta Platforms, Inc. and affiliates.

using UnityEngine;

namespace PassthroughCameraSamples.Shared
{
    /// <summary>
    /// Centralized server configuration (ScriptableObject singleton).
    ///
    /// This allows you to change the server IP in one place and have it
    /// automatically apply to all scenes.
    ///
    /// To create: Assets > Create > Passthrough Camera Samples > Server Config
    /// Recommended location: Assets/PassthroughCameraApiSamples/Shared/Resources/ServerConfig.asset
    ///
    /// To modify: Use the Server Config Editor (Tools > Passthrough Camera > Server Config Editor)
    /// </summary>
    [CreateAssetMenu(fileName = "ServerConfig", menuName = "Passthrough Camera Samples/Server Config", order = 1)]
    public class ServerConfig : ScriptableObject
    {
        [Header("Server Connection")]
        [Tooltip("Server IP address (without port)")]
        [SerializeField] private string m_serverIP = "192.168.0.135";

        [Tooltip("Server port number")]
        [SerializeField] private int m_serverPort = 8001;

        [Header("Endpoints")]
        [Tooltip("Inference endpoint path")]
        [SerializeField] private string m_inferenceEndpoint = "/infer_human";

        [Tooltip("Segmentation endpoint path")]
        [SerializeField] private string m_segmentationEndpoint = "/segmentation";

        [Tooltip("ROI Depth endpoint path")]
        [SerializeField] private string m_roiDepthEndpoint = "/infer_roi_depth";

        [Header("Connection Settings")]
        [Tooltip("Request timeout in seconds")]
        [SerializeField, Range(5f, 60f)] private float m_requestTimeoutSeconds = 10.0f;

        // Singleton instance
        private static ServerConfig s_instance;

        /// <summary>
        /// Get the singleton instance of ServerConfig.
        /// Loads from Resources/ServerConfig.asset
        /// </summary>
        public static ServerConfig Instance
        {
            get
            {
                if (s_instance == null)
                {
                    s_instance = Resources.Load<ServerConfig>("ServerConfig");

                    if (s_instance == null)
                    {
                        Debug.LogWarning("[ServerConfig] No ServerConfig asset found in Resources folder. " +
                                         "Create one at: Assets/PassthroughCameraApiSamples/Shared/Resources/ServerConfig.asset\n" +
                                         "Using default values (192.168.0.135:8001)");

                        // Create a temporary instance with default values
                        s_instance = CreateInstance<ServerConfig>();
                    }
                }
                return s_instance;
            }
        }

        // Public properties
        public string ServerIP => m_serverIP;
        public int ServerPort => m_serverPort;
        public float RequestTimeoutSeconds => m_requestTimeoutSeconds;

        /// <summary>
        /// Get the base URL (http://IP:PORT)
        /// </summary>
        public string BaseUrl => $"http://{m_serverIP}:{m_serverPort}";

        /// <summary>
        /// Get the full inference endpoint URL
        /// </summary>
        public string InferenceUrl => $"{BaseUrl}{m_inferenceEndpoint}";

        /// <summary>
        /// Get the full segmentation endpoint URL
        /// </summary>
        public string SegmentationUrl => $"{BaseUrl}{m_segmentationEndpoint}";

        /// <summary>
        /// Get the full ROI depth endpoint URL
        /// </summary>
        public string RoiDepthUrl => $"{BaseUrl}{m_roiDepthEndpoint}";

        /// <summary>
        /// Build inference URL with query parameters
        /// </summary>
        public string BuildInferenceUrl(string mode, bool includeMask = false, bool includeDepth = false)
        {
            return $"{InferenceUrl}?mode={mode}&include_mask={includeMask.ToString().ToLower()}&include_depth={includeDepth.ToString().ToLower()}";
        }

        /// <summary>
        /// Log current configuration
        /// </summary>
        public void LogConfiguration()
        {
            Debug.Log($"[ServerConfig] === Server Configuration ===");
            Debug.Log($"[ServerConfig] Base URL: {BaseUrl}");
            Debug.Log($"[ServerConfig] Inference URL: {InferenceUrl}");
            Debug.Log($"[ServerConfig] Segmentation URL: {SegmentationUrl}");
            Debug.Log($"[ServerConfig] ROI Depth URL: {RoiDepthUrl}");
            Debug.Log($"[ServerConfig] Timeout: {m_requestTimeoutSeconds}s");
        }

        /// <summary>
        /// Validate configuration and return warnings
        /// </summary>
        public string[] Validate()
        {
            var warnings = new System.Collections.Generic.List<string>();

            if (string.IsNullOrEmpty(m_serverIP))
            {
                warnings.Add("Server IP is empty!");
            }

            if (m_serverPort < 1 || m_serverPort > 65535)
            {
                warnings.Add($"Server port {m_serverPort} is invalid (must be 1-65535)");
            }

            if (m_requestTimeoutSeconds < 5f)
            {
                warnings.Add($"Timeout {m_requestTimeoutSeconds}s is very low, may cause frequent timeouts");
            }

            return warnings.ToArray();
        }

        private void OnValidate()
        {
            // Clamp port to valid range
            if (m_serverPort < 1) m_serverPort = 1;
            if (m_serverPort > 65535) m_serverPort = 65535;
        }
    }
}
