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
        DepthEstimation = 3
    }

    /// <summary>
    /// Shared configuration for server inference.
    /// Contains settings common to all inference modes.
    /// </summary>
    [System.Serializable]
    public class InferenceConfig
    {
        [Header("Server Connection")]
        [Tooltip("Base URL of the inference server (without query parameters)")]
        public string baseUrl = "http://192.168.0.135:8001/infer_human";

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
                default:
                    Debug.LogWarning($"[InferenceConfig] Unknown mode: {mode}, defaulting to 'detection'");
                    return "detection";
            }
        }

        /// <summary>
        /// Build the complete server URL with query parameters.
        /// </summary>
        public string BuildUrl()
        {
            string modeParam = GetModeString();

            // For depth mode, depth is always included regardless of includeDepth setting
            bool actualIncludeDepth = (mode == InferenceMode.DepthEstimation) || includeDepth;

            string url = $"{baseUrl}?mode={modeParam}&include_mask={includeMask.ToString().ToLower()}&include_depth={actualIncludeDepth.ToString().ToLower()}";

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

            if (includeMask)
                baseSize += 75; // Segmentation mask

            if (mode == InferenceMode.DepthEstimation || includeDepth)
                baseSize += 300; // Depth map

            if (baseSize < 100)
                return $"~{baseSize}KB";
            else
                return $"~{baseSize / 1000f:F1}MB";
        }

        /// <summary>
        /// Validate configuration and log warnings for potential issues.
        /// </summary>
        public void Validate()
        {
            if (string.IsNullOrEmpty(baseUrl))
            {
                Debug.LogError("[InferenceConfig] Base URL is empty!");
            }

            if (!baseUrl.StartsWith("http://") && !baseUrl.StartsWith("https://"))
            {
                Debug.LogWarning($"[InferenceConfig] Base URL should start with http:// or https://: {baseUrl}");
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
            Debug.Log($"[InferenceConfig] URL: {BuildUrl()}");
            Debug.Log($"[InferenceConfig] Mode: {GetModeDisplayName()} ({GetModeString()})");
            Debug.Log($"[InferenceConfig] Target FPS: {targetFPS:F1} ({GetInferenceInterval() * 1000f:F0}ms interval)");
            Debug.Log($"[InferenceConfig] JPEG Quality: {jpegQuality}");
            Debug.Log($"[InferenceConfig] Expected Download: {GetExpectedDownloadSize()}");
            Debug.Log($"[InferenceConfig] Include Mask: {includeMask}");
            Debug.Log($"[InferenceConfig] Include Depth: {(mode == InferenceMode.DepthEstimation ? "true (forced)" : includeDepth.ToString().ToLower())}");
        }
    }

    /// <summary>
    /// Helper to create InferenceConfig presets for common use cases.
    /// </summary>
    public static class InferenceConfigPresets
    {
        public static InferenceConfig FastObjectDetection()
        {
            return new InferenceConfig
            {
                mode = InferenceMode.ObjectDetection,
                targetFPS = 10f,
                jpegQuality = 80,
                includeMask = false,
                includeDepth = false
            };
        }

        public static InferenceConfig PoseEstimation()
        {
            return new InferenceConfig
            {
                mode = InferenceMode.PoseEstimation,
                targetFPS = 5f,
                jpegQuality = 85,
                includeMask = false,
                includeDepth = false
            };
        }

        public static InferenceConfig DepthEstimation()
        {
            return new InferenceConfig
            {
                mode = InferenceMode.DepthEstimation,
                targetFPS = 5f,  // Lower FPS due to large download
                jpegQuality = 80,
                includeMask = false,
                includeDepth = false  // Will be forced to true by mode
            };
        }

        public static InferenceConfig FullPipeline()
        {
            return new InferenceConfig
            {
                mode = InferenceMode.Both,
                targetFPS = 3f,  // Very low FPS for full pipeline
                jpegQuality = 90,
                includeMask = true,
                includeDepth = true
            };
        }
    }
}
