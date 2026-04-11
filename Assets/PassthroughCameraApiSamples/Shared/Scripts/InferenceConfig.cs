// Copyright (c) Meta Platforms, Inc. and affiliates.

using UnityEngine;

namespace PassthroughCameraSamples.Shared
{
    /// <summary>
    /// Inference mode for server-side AI processing.
    /// Maps to server's /infer_human?mode= parameter.
    /// </summary>
    public enum InferenceMode
    {
        /// <summary>
        /// YOLO object detection only (mode=detection)
        /// - Detects objects and bounding boxes
        /// - Fastest mode (~220ms E2E)
        /// - Download: ~20KB
        /// </summary>
        ObjectDetection = 0,

        /// <summary>
        /// Pose estimation only (mode=pose)
        /// - Detects human keypoints/skeleton
        /// - Medium speed (~290ms E2E)
        /// - Download: ~20KB
        /// </summary>
        PoseEstimation = 1,

        /// <summary>
        /// Object detection + Pose estimation (mode=both)
        /// - Detects objects + human keypoints
        /// - Slower (~320ms E2E)
        /// - Download: ~20KB
        /// </summary>
        Both = 2,

        /// <summary>
        /// Monocular depth estimation only (mode=depth)
        /// - Estimates depth map using MiDaS model
        /// - Medium speed (~250-350ms E2E)
        /// - Download: ~300KB (depth map)
        /// </summary>
        DepthEstimation = 3,

        /// <summary>
        /// Segmentation only (mode=segmentation)
        /// - Semantic segmentation with SAM model
        /// - Medium speed (~150-250ms E2E)
        /// - Download: ~75KB (segmentation mask)
        /// </summary>
        Segmentation = 4,

        /// <summary>
        /// Segmentation + Depth (mode=seg_depth)
        /// - Semantic segmentation + depth estimation
        /// - Slower (~300-450ms E2E)
        /// - Download: ~375KB (mask + depth map)
        /// </summary>
        SegmentationWithDepth = 5
    }

    /// <summary>
    /// Shared configuration for server inference.
    /// Contains settings common to all inference modes.
    /// </summary>
    [System.Serializable]
    public class InferenceConfig
    {
        [Header("Server Connection")]
        [Tooltip("Use centralized ServerConfig asset for IP/port. If enabled, baseUrl is auto-generated from ServerConfig.")]
        public bool useServerConfig = true;

        [Tooltip("Base URL of the inference server (without query parameters). Only used if useServerConfig is false.")]
        public string baseUrl = "";

        [Header("Inference Mode")]
        [Tooltip("Type of inference to perform")]
        public InferenceMode mode = InferenceMode.ObjectDetection;

        [Header("Optional Features")]
        [Tooltip("Include segmentation mask in response (adds ~75KB download)")]
        public bool includeMask = false;

        [Tooltip("Include depth map in response (adds ~300KB download). Ignored if mode=DepthEstimation (always included).")]
        public bool includeDepth = false;

        [Header("Upload Optimization")]
        [Tooltip("JPEG quality for image upload (60-100). Lower = smaller upload, faster send, but may reduce accuracy.")]
        [Range(60, 100)]
        public int jpegQuality = 80;

        [Header("FPS Configuration")]
        [Tooltip("Target inference FPS for this mode. Lower FPS = less frequent inference, better performance.")]
        [Range(1f, 30f)]
        public float targetFPS = 10f;

        /// <summary>
        /// Get the mode parameter string for the server URL.
        /// </summary>
        public string GetModeString()
        {
            switch (mode)
            {
                case InferenceMode.ObjectDetection:
                    return "detection";
                case InferenceMode.PoseEstimation:
                    return "pose";
                case InferenceMode.Both:
                    return "both";
                case InferenceMode.DepthEstimation:
                    return "depth";
                case InferenceMode.Segmentation:
                    return "segmentation";
                case InferenceMode.SegmentationWithDepth:
                    return "seg_depth";
                default:
                    Debug.LogWarning($"[InferenceConfig] Unknown mode: {mode}, defaulting to 'detection'");
                    return "detection";
            }
        }

        /// <summary>
        /// Build the complete server URL with query parameters.
        /// Uses ServerConfig if useServerConfig is true, otherwise uses baseUrl.
        /// </summary>
        public string BuildUrl()
        {
            string modeParam = GetModeString();

            // For depth mode or seg_depth mode, depth is always included
            bool actualIncludeDepth = (mode == InferenceMode.DepthEstimation) ||
                                     (mode == InferenceMode.SegmentationWithDepth) ||
                                     includeDepth;

            // For segmentation modes, mask is always included
            bool actualIncludeMask = (mode == InferenceMode.Segmentation) ||
                                    (mode == InferenceMode.SegmentationWithDepth) ||
                                    includeMask;

            // Determine base URL
            string effectiveBaseUrl;
            if (useServerConfig)
            {
                // Use centralized ServerConfig
                // For Segmentation modes, use dedicated /segmentation endpoint
                if (mode == InferenceMode.Segmentation || mode == InferenceMode.SegmentationWithDepth)
                {
                    effectiveBaseUrl = ServerConfig.Instance.SegmentationUrl;
                }
                else
                {
                    effectiveBaseUrl = ServerConfig.Instance.InferenceUrl;
                }
            }
            else
            {
                // Use local baseUrl field
                if (string.IsNullOrEmpty(baseUrl))
                {
                    Debug.LogError("[InferenceConfig] baseUrl is empty and useServerConfig is false! " +
                                   "Either enable 'Use Server Config' or provide a baseUrl. " +
                                   "Falling back to ServerConfig.");
                    effectiveBaseUrl = ServerConfig.Instance.InferenceUrl;
                }
                else
                {
                    effectiveBaseUrl = baseUrl;
                }
            }

            // For Segmentation endpoint, don't add mode parameter (endpoint IS the mode)
            string url;
            if (mode == InferenceMode.Segmentation || mode == InferenceMode.SegmentationWithDepth)
            {
                // Segmentation endpoint doesn't need ?mode= parameter
                url = effectiveBaseUrl;
            }
            else
            {
                // Other modes use /infer_human?mode=X
                url = $"{effectiveBaseUrl}?mode={modeParam}&include_mask={actualIncludeMask.ToString().ToLower()}&include_depth={actualIncludeDepth.ToString().ToLower()}";
            }

            return url;
        }

        /// <summary>
        /// Get the time interval between inferences based on target FPS.
        /// </summary>
        public float GetInferenceInterval()
        {
            if (targetFPS <= 0f)
            {
                Debug.LogWarning($"[InferenceConfig] Invalid targetFPS: {targetFPS}, defaulting to 10 FPS");
                return 0.1f;
            }

            return 1f / targetFPS;
        }

        /// <summary>
        /// Get a display-friendly name for the current mode.
        /// </summary>
        public string GetModeDisplayName()
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
        /// Get expected download size for this configuration (approximate).
        /// </summary>
        public string GetExpectedDownloadSize()
        {
            int baseSize = 20; // KB - skeleton data

            // For segmentation modes, mask is always included
            if (mode == InferenceMode.Segmentation || mode == InferenceMode.SegmentationWithDepth || includeMask)
                baseSize += 75; // Segmentation mask

            // For depth modes, depth map is always included
            if (mode == InferenceMode.DepthEstimation || mode == InferenceMode.SegmentationWithDepth || includeDepth)
                baseSize += 300; // Depth map

            if (baseSize < 100)
                return $"~{baseSize}KB";
            else
                return $"~{baseSize / 1000f:F1}MB";
        }

        /// <summary>
        /// Validate configuration and log warnings for potential issues.
        /// FIXED: Now validates the actual effective URL from BuildUrl() instead of baseUrl directly.
        /// </summary>
        public void Validate()
        {
            // When useServerConfig is true, baseUrl can be empty - that's OK!
            // Validate the ACTUAL effective URL instead
            string effectiveUrl = BuildUrl();

            if (string.IsNullOrEmpty(effectiveUrl))
            {
                Debug.LogError("[InferenceConfig] Effective URL is empty! Check ServerConfig asset.");
                return;
            }

            if (!effectiveUrl.StartsWith("http://") && !effectiveUrl.StartsWith("https://"))
            {
                Debug.LogError($"[InferenceConfig] Effective URL should start with http:// or https://: {effectiveUrl}");
                return;
            }

            // Only check baseUrl if useServerConfig is false
            if (!useServerConfig)
            {
                if (string.IsNullOrEmpty(baseUrl))
                {
                    Debug.LogWarning("[InferenceConfig] Base URL is empty and useServerConfig is false! Using ServerConfig fallback.");
                }
                else if (!baseUrl.StartsWith("http://") && !baseUrl.StartsWith("https://"))
                {
                    Debug.LogWarning($"[InferenceConfig] Base URL should start with http:// or https://: {baseUrl}");
                }
            }

            if (jpegQuality < 60)
            {
                Debug.LogWarning($"[InferenceConfig] JPEG quality is very low ({jpegQuality}), may affect detection accuracy");
            }

            if (targetFPS > 20f)
            {
                Debug.LogWarning($"[InferenceConfig] Target FPS is high ({targetFPS}). Network latency may cause frame drops.");
            }

            if (mode == InferenceMode.DepthEstimation && targetFPS > 10f)
            {
                Debug.LogWarning($"[InferenceConfig] Depth mode with high FPS ({targetFPS}) will cause large bandwidth usage (~300KB per frame)");
            }

            if (mode == InferenceMode.SegmentationWithDepth && targetFPS > 10f)
            {
                Debug.LogWarning($"[InferenceConfig] Segmentation+Depth mode with high FPS ({targetFPS}) will cause large bandwidth usage (~375KB per frame)");
            }

            if (includeMask && includeDepth)
            {
                int totalSize = 20 + 75 + 300; // KB
                Debug.LogWarning($"[InferenceConfig] Both mask and depth enabled. Download size will be ~{totalSize}KB per frame!");
            }
        }

        /// <summary>
        /// Log configuration summary to console.
        /// </summary>
        public void LogSummary()
        {
            Debug.Log($"[InferenceConfig] === Configuration Summary ===");
            Debug.Log($"[InferenceConfig] Using ServerConfig: {useServerConfig} {(useServerConfig ? $"(IP: {ServerConfig.Instance.ServerIP})" : "")}");
            Debug.Log($"[InferenceConfig] URL: {BuildUrl()}");
            Debug.Log($"[InferenceConfig] Mode: {GetModeDisplayName()} ({GetModeString()})");
            Debug.Log($"[InferenceConfig] Target FPS: {targetFPS:F1} ({GetInferenceInterval() * 1000f:F0}ms interval)");
            Debug.Log($"[InferenceConfig] JPEG Quality: {jpegQuality}");
            Debug.Log($"[InferenceConfig] Expected Download: {GetExpectedDownloadSize()}");
            Debug.Log($"[InferenceConfig] ==================================");
        }
    }
}
