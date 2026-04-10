// Copyright (c) Meta Platforms, Inc. and affiliates.

using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Meta.XR;
using Meta.XR.Samples;
using UnityEngine;
using UnityEngine.Networking;
using Newtonsoft.Json;
using PassthroughCameraSamples.Shared;
using PassthroughCameraSamples.MultiObjectDetection;

namespace PassthroughCameraSamples.DepthEstimation
{
    [MetaCodeSample("PassthroughCameraApiSamples-DepthEstimation")]
    public class DepthInferenceRunManager : MonoBehaviour
    {
        [SerializeField] private PassthroughCameraAccess m_cameraAccess;
        [SerializeField] private DetectionUiMenuManager m_uiMenuManager;
        [SerializeField] private DepthEstimationManager m_depthManager;

        [Header("UI display references")]
        [SerializeField] private DepthLabelManager m_depthLabelManager;  // NEW: Text labels
        [SerializeField] private DepthVisualizationManager m_depthVisualizationManager;  // DEBUG: Point cloud
        [SerializeField] private SharedInferenceHUD m_sharedHUD;
        [SerializeField] private bool m_useDebugPointCloud = false;  // Toggle for debug visualization

        // Depth point cloud visualization
        private List<GameObject> m_depthPoints = new List<GameObject>();
        private GameObject m_depthPointsParent;

        [Header("Server Inference - ROI Depth")]
        [SerializeField] private InferenceConfig m_inferenceConfig = new InferenceConfig
        {
            mode = InferenceMode.DepthEstimation,
            targetFPS = 5f,  // Lower FPS for ROI depth (detection + depth)
            jpegQuality = 80,
            includeMask = false,
            includeDepth = true
        };

        [Header("Detection Settings")]
        [SerializeField] private float m_minDetectionConfidence = 0.5f;

        private int m_frameId = 0;

        // Timing data
        private float m_lastE2eMs = 0f;
        private float m_lastUploadMs = 0f;
        private float m_lastDownloadMs = 0f;
        private float m_lastParseMs = 0f;
        private int m_lastUploadBytes = 0;
        private int m_lastDownloadBytes = 0;
        private int m_lastDownloadBytesCompressed = 0;

        // FPS throttling
        private float m_lastInferenceTime = 0f;
        private bool m_inferenceInProgress = false;

        // Frame statistics
        private int m_totalFrames = 0;
        private int m_droppedFrames = 0;
        private int m_frozenFrames = 0;

        private IEnumerator Start()
        {
            Debug.Log("[DEPTH INF] DepthInferenceRunManager started (ROI Mode)");

            Debug.Log($"[DEPTH REF] cameraAccess={m_cameraAccess != null}");
            Debug.Log($"[DEPTH REF] uiMenuManager={m_uiMenuManager != null}");
            Debug.Log($"[DEPTH REF] depthManager={m_depthManager != null}");
            Debug.Log($"[DEPTH REF] depthLabelManager={m_depthLabelManager != null}");
            Debug.Log($"[DEPTH REF] depthVisualizationManager={m_depthVisualizationManager != null} (debug mode)");
            Debug.Log($"[DEPTH REF] sharedHUD={m_sharedHUD != null}");
            Debug.Log($"[DEPTH REF] debugPointCloud={m_useDebugPointCloud}");

            m_inferenceConfig.Validate();
            m_inferenceConfig.LogSummary();

            if (m_sharedHUD != null)
            {
                m_sharedHUD.SetMode(m_inferenceConfig.mode, m_inferenceConfig.targetFPS);
            }

            Debug.Log("[SERVER TEST] Testing connection to server...");
            yield return TestServerConnection();

            while (true)
            {
                while (m_uiMenuManager.IsPaused)
                {
                    yield return null;
                }
                yield return RunInference();
            }
        }

        private IEnumerator RunInference()
        {
            if (!m_cameraAccess.IsPlaying)
            {
                yield break;
            }

            // FPS throttling
            float currentTime = Time.time;
            float targetInterval = m_inferenceConfig.GetInferenceInterval();
            float timeSinceLastInference = currentTime - m_lastInferenceTime;

            if (timeSinceLastInference < targetInterval)
            {
                m_droppedFrames++;
                if (m_sharedHUD != null)
                {
                    m_sharedHUD.ReportDroppedFrame();
                }
                yield break;
            }

            if (m_inferenceInProgress)
            {
                m_frozenFrames++;
                if (m_sharedHUD != null)
                {
                    m_sharedHUD.ReportFrozenFrame();
                }
                yield break;
            }

            m_inferenceInProgress = true;
            m_lastInferenceTime = currentTime;

            [DllImport("OVRPlugin", CallingConvention = CallingConvention.Cdecl)]
            static extern OVRPlugin.Result ovrp_GetNodePoseStateAtTime(double time, OVRPlugin.Node nodeId, out OVRPlugin.PoseStatef nodePoseState);
            if (!ovrp_GetNodePoseStateAtTime(OVRPlugin.GetTimeInSeconds(), OVRPlugin.Node.Head, out _).IsSuccess())
            {
                m_inferenceInProgress = false;
                yield break;
            }

            Texture targetTexture = m_cameraAccess.GetTexture();

            // Run server inference (detection + depth)
            yield return RunServerInference(targetTexture);

            m_inferenceInProgress = false;
            m_totalFrames++;
        }

        // JSON Response Classes - Updated for /infer_human with full depth map
        [System.Serializable]
        public class HumanInferenceResponse
        {
            public DetectionResult detections;
            public SkeletonResult skeleton;
            public DepthResult depth;  // Full depth map
            public float processing_time_ms;
            public int input_image_width;
            public int input_image_height;
        }

        [System.Serializable]
        public class DetectionResult
        {
            public Detection[] detections;
            public int num_detections;
        }

        [System.Serializable]
        public class Detection
        {
            public int class_id;
            public string class_name;
            public float confidence;
            public float[] bbox;  // normalized [0-1]
            public int[] bbox_pixels;
            public PersonDepthEstimate depth_estimate;  // NEW: Per-person depth with source tracking
        }

        [System.Serializable]
        public class SkeletonResult
        {
            public PersonSkeleton[] persons;
        }

        [System.Serializable]
        public class PersonSkeleton
        {
            public Keypoint[] keypoints;
            public float[] bbox;
            public PersonDepthEstimate depth_estimate;  // NEW: Per-person depth with source tracking
        }

        [System.Serializable]
        public class Keypoint
        {
            public string name;
            public float x;
            public float y;
            public float score;
        }

        [System.Serializable]
        public class PersonDepthEstimate
        {
            public float depth_m;
            public string depth_source;
            public float depth_confidence;
            public bool used_fallback;
            public string fallback_reason;
            public DepthStats depth_stats;
            public float[][] sample_point_px;  // Can be array of points or single point
        }

        [System.Serializable]
        public class DepthStats
        {
            public float median;
            public float q25;
            public float q75;
            public int valid_pixels;
        }

        [System.Serializable]
        public class DepthResult
        {
            public int height;
            public int width;
            public int downsample_factor;
            public float[][] values;  // 2D depth map
        }

        private IEnumerator TestServerConnection()
        {
            string testUrl = "http://192.168.0.135:8001/";
            Debug.Log($"[DEPTH SERVER TEST] Connecting to {testUrl}");

            using (UnityWebRequest req = UnityWebRequest.Get(testUrl))
            {
                req.timeout = 5;
                yield return req.SendWebRequest();

                if (req.result == UnityWebRequest.Result.Success)
                {
                    Debug.Log($"[DEPTH SERVER TEST] ✓ Connection OK! Response: {req.downloadHandler.text}");
                }
                else
                {
                    Debug.LogError($"[DEPTH SERVER TEST] ✗ Connection FAILED: {req.error}");
                }
            }
        }

        private IEnumerator RunServerInference(Texture texture)
        {
            m_frameId++;
            float e2eStartTime = Time.realtimeSinceStartup;

            // Convert texture to Texture2D
            Texture2D tex2D = texture as Texture2D;
            if (tex2D == null)
            {
                RenderTexture rt = texture as RenderTexture;
                if (rt != null)
                {
                    tex2D = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
                    RenderTexture.active = rt;
                    tex2D.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                    tex2D.Apply();
                    RenderTexture.active = null;
                }
                else
                {
                    Debug.LogError("[DEPTH SERVER] Unsupported texture type");
                    m_inferenceInProgress = false;
                    yield break;
                }
            }

            // Encode as JPEG
            byte[] jpegBytes = tex2D.EncodeToJPG(m_inferenceConfig.jpegQuality);
            int uploadBytes = jpegBytes.Length;

            // Create POST request
            List<IMultipartFormSection> formData = new List<IMultipartFormSection>();
            formData.Add(new MultipartFormFileSection("image", jpegBytes, "frame.jpg", "image/jpeg"));

            // Use /infer_human endpoint with mode=both and include_depth=true to get full depth map
            string serverUrl = "http://192.168.0.135:8001/infer_human?mode=both&include_depth=true";

            UnityWebRequest request = UnityWebRequest.Post(serverUrl, formData);

            // Add headers
            request.SetRequestHeader("X-Scene-Name", "DepthEstimation");
            request.SetRequestHeader("X-Frame-Id", m_frameId.ToString());
            request.SetRequestHeader("X-Min-Confidence", m_minDetectionConfidence.ToString("F2"));

            // Timing headers (from last inference)
            request.SetRequestHeader("X-E2E-Ms", m_lastE2eMs.ToString("F1"));
            request.SetRequestHeader("X-Upload-Ms", m_lastUploadMs.ToString("F1"));
            request.SetRequestHeader("X-Download-Ms", m_lastDownloadMs.ToString("F1"));
            request.SetRequestHeader("X-Parse-Ms", m_lastParseMs.ToString("F1"));
            request.SetRequestHeader("X-Upload-Bytes", m_lastUploadBytes.ToString());
            request.SetRequestHeader("X-Download-Bytes", m_lastDownloadBytes.ToString());
            request.SetRequestHeader("X-Download-Bytes-Compressed", m_lastDownloadBytesCompressed.ToString());

            // Performance metrics headers
            float freezeRatio = m_totalFrames > 0 ? (float)m_frozenFrames / m_totalFrames : 0f;
            request.SetRequestHeader("X-Target-FPS", m_inferenceConfig.targetFPS.ToString("F1"));
            request.SetRequestHeader("X-Dropped-Frames", m_droppedFrames.ToString());
            request.SetRequestHeader("X-Freeze-Frames", m_frozenFrames.ToString());
            request.SetRequestHeader("X-Freeze-Ratio", freezeRatio.ToString("F4"));

            Debug.Log($"[DEPTH SEND] Frame {m_frameId} to per-object depth endpoint");

            // Send request
            float uploadStartTime = Time.realtimeSinceStartup;
            UnityWebRequestAsyncOperation asyncOp = request.SendWebRequest();

            while (!asyncOp.isDone && request.uploadProgress < 1.0f)
            {
                yield return null;
            }

            float uploadMs = (Time.realtimeSinceStartup - uploadStartTime) * 1000f;
            yield return asyncOp;

            if (request.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError($"[DEPTH SERVER] Request failed: {request.error}");
                Debug.LogError($"[DEPTH SERVER] URL: {serverUrl}");
                m_inferenceInProgress = false;
                yield break;
            }

            // Parse response
            float parseStartTime = Time.realtimeSinceStartup;
            string jsonResponse = request.downloadHandler.text;

            HumanInferenceResponse response = null;
            try
            {
                response = JsonConvert.DeserializeObject<HumanInferenceResponse>(jsonResponse);
                Debug.Log($"[DEPTH JSON] Parsed successfully");
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[DEPTH JSON] Parse failed: {e.Message}");
                Debug.LogError($"[DEPTH JSON] Response preview: {jsonResponse.Substring(0, Mathf.Min(500, jsonResponse.Length))}");
                m_inferenceInProgress = false;
                yield break;
            }

            if (response == null)
            {
                Debug.LogError("[DEPTH SERVER] Invalid response");
                m_inferenceInProgress = false;
                yield break;
            }

            float parseMs = (Time.realtimeSinceStartup - parseStartTime) * 1000f;

            // Calculate timing
            float e2eMs = (Time.realtimeSinceStartup - e2eStartTime) * 1000f;
            int downloadBytes = (int)request.downloadedBytes;
            float serverProcMs = response.processing_time_ms;
            float downloadMs = Mathf.Max(0f, e2eMs - uploadMs - serverProcMs - parseMs);

            // Count detections and persons
            int numDetections = response.detections != null ? response.detections.num_detections : 0;
            int numPersons = response.skeleton != null && response.skeleton.persons != null ? response.skeleton.persons.Length : 0;
            bool hasDepthMap = response.depth != null && response.depth.values != null;

            Debug.Log($"[DEPTH DATA] Detections={numDetections}, Persons={numPersons}, DepthMap={hasDepthMap}");
            Debug.Log($"[TIMING] E2E={e2eMs:F0}ms (upload={uploadMs:F0} server={serverProcMs:F0} download={downloadMs:F0} parse={parseMs:F0})");

            // Log depth estimates for detections
            if (response.detections != null && response.detections.detections != null)
            {
                foreach (var det in response.detections.detections)
                {
                    if (det.depth_estimate != null)
                    {
                        Debug.Log($"[DEPTH EST] {det.class_name}: " +
                                  $"depth={det.depth_estimate.depth_m:F3}m, " +
                                  $"source={det.depth_estimate.depth_source}, " +
                                  $"confidence={det.depth_estimate.depth_confidence:F2}, " +
                                  $"fallback={det.depth_estimate.used_fallback}");
                    }
                }
            }

            // Log depth map info
            if (hasDepthMap)
            {
                Debug.Log($"[DEPTH MAP] Size={response.depth.width}x{response.depth.height}, " +
                          $"downsample={response.depth.downsample_factor}");
            }

            // Display depth information
            var cameraPose = m_cameraAccess.GetCameraPose();

            Debug.Log($"[DEPTH RENDER] Starting visualization: useDebugPointCloud={m_useDebugPointCloud}, " +
                      $"labelManager={m_depthLabelManager != null}, " +
                      $"depthVisualization={m_depthVisualizationManager != null}, " +
                      $"hasDepthMap={hasDepthMap}");

            if (m_depthManager != null && m_depthManager.m_spatialAnchor != null && !m_depthManager.m_spatialAnchor.IsTracked)
            {
                Debug.LogWarning("[DEPTH RENDER] Spatial anchor not tracked, skipping visualization");
            }
            else
            {
                // Render depth map visualization if available
                if (hasDepthMap && m_depthVisualizationManager != null)
                {
                    Debug.Log($"[DEPTH RENDER] Rendering depth map visualization");
                    // Convert depth map to texture and render
                    RenderDepthMap(response.depth, cameraPose);
                }
                else if (hasDepthMap && m_depthVisualizationManager == null)
                {
                    Debug.LogWarning("[DEPTH RENDER] DepthVisualizationManager is NULL, cannot render depth map");
                }

                // Render text labels for detections if label manager is available
                if (!m_useDebugPointCloud && m_depthLabelManager != null && response.detections != null && response.detections.detections != null)
                {
                    // Default mode: Draw text labels above objects
                    Debug.Log($"[DEPTH RENDER] Calling DrawDepthLabels with {response.detections.detections.Length} detections");
                    // Note: DepthLabelManager expects the old format, we'll need to convert
                    // For now, skip labels if using new endpoint (focus on depth map visualization)
                    // m_depthLabelManager.DrawDepthLabels(response.detections.detections, cameraPose);
                    Debug.Log("[DEPTH RENDER] Label rendering temporarily disabled (incompatible format)");
                }
                else if (!m_useDebugPointCloud && m_depthLabelManager == null)
                {
                    Debug.LogWarning("[DEPTH RENDER] DepthLabelManager is NULL, no text labels will be shown");
                }
            }

            // Calculate uncompressed bytes
            int uncompressedBytes = System.Text.Encoding.UTF8.GetByteCount(jsonResponse);

            // Update HUD
            if (m_sharedHUD != null)
            {
                float avgConfidence = 0f;
                if (numDetections > 0 && response.detections != null && response.detections.detections != null)
                {
                    foreach (var det in response.detections.detections)
                    {
                        avgConfidence += det.confidence;
                    }
                    avgConfidence /= numDetections;
                }

                // Calculate average keypoint confidence
                float avgKeypointConf = 0f;
                if (numPersons > 0 && response.skeleton != null && response.skeleton.persons != null)
                {
                    int totalKeypoints = 0;
                    float totalScore = 0f;
                    foreach (var person in response.skeleton.persons)
                    {
                        if (person != null && person.keypoints != null)
                        {
                            foreach (var kp in person.keypoints)
                            {
                                if (kp.score > 0)
                                {
                                    totalScore += kp.score;
                                    totalKeypoints++;
                                }
                            }
                        }
                    }
                    if (totalKeypoints > 0)
                    {
                        avgKeypointConf = totalScore / totalKeypoints;
                    }
                }

                m_sharedHUD.UpdateMetrics(
                    e2eMs, uploadMs, serverProcMs, downloadMs, parseMs,
                    uploadBytes, uncompressedBytes, downloadBytes,
                    numDetections, avgConfidence, avgKeypointConf
                );
            }

            // Store timing
            m_lastE2eMs = e2eMs;
            m_lastUploadMs = uploadMs;
            m_lastDownloadMs = downloadMs;
            m_lastParseMs = parseMs;
            m_lastUploadBytes = uploadBytes;
            m_lastDownloadBytes = uncompressedBytes;
            m_lastDownloadBytesCompressed = downloadBytes;
        }

        private void RenderDepthMap(DepthResult depthResult, Pose cameraPose)
        {
            if (depthResult == null || depthResult.values == null)
            {
                Debug.LogWarning("[DEPTH RENDER] Invalid depth result");
                return;
            }

            Debug.Log($"[DEPTH RENDER] Converting depth map to point cloud: {depthResult.width}x{depthResult.height}");

            // Convert depth map to a list of 3D points for visualization
            List<Vector3> points = new List<Vector3>();
            List<Color> colors = new List<Color>();

            int width = depthResult.width;
            int height = depthResult.height;

            // Sample every N pixels to reduce point count (for performance)
            int sampleStep = 4;  // Sample every 4th pixel

            for (int y = 0; y < height; y += sampleStep)
            {
                if (depthResult.values[y] == null) continue;

                for (int x = 0; x < width; x += sampleStep)
                {
                    if (x >= depthResult.values[y].Length) continue;

                    float depthValue = depthResult.values[y][x];

                    // Skip invalid depth values
                    if (depthValue < 0.01f || depthValue > 0.99f) continue;

                    // Convert to normalized viewport coordinates [0, 1]
                    float normX = (float)x / width;
                    float normY = 1.0f - ((float)y / height);  // Flip Y

                    // Use PassthroughCameraAccess to convert from viewport to world space
                    // Note: This is a simplified version - you may need to use actual camera projection
                    Vector3 worldPos = ProjectDepthPoint(normX, normY, depthValue, cameraPose);

                    points.Add(worldPos);

                    // Colorize based on depth (heat map)
                    Color depthColor = GetDepthColor(depthValue);
                    colors.Add(depthColor);
                }
            }

            Debug.Log($"[DEPTH RENDER] Generated {points.Count} depth points");

            // Render depth points as small spheres
            RenderDepthPoints(points, colors);
        }

        private void RenderDepthPoints(List<Vector3> points, List<Color> colors)
        {
            // Clear previous points
            ClearDepthPoints();

            // Create parent if doesn't exist
            if (m_depthPointsParent == null)
            {
                m_depthPointsParent = new GameObject("DepthPointCloud");
            }

            // Limit number of points for performance (max 500 points)
            int maxPoints = Mathf.Min(500, points.Count);
            int step = Mathf.Max(1, points.Count / maxPoints);

            Debug.Log($"[DEPTH RENDER] Rendering {maxPoints} points (step={step})");

            for (int i = 0; i < points.Count; i += step)
            {
                // Create small sphere for each point
                GameObject point = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                point.transform.position = points[i];
                point.transform.localScale = Vector3.one * 0.02f;  // 2cm radius
                point.transform.SetParent(m_depthPointsParent.transform);

                // Set color
                Renderer renderer = point.GetComponent<Renderer>();
                if (renderer != null)
                {
                    renderer.material = new Material(Shader.Find("Unlit/Color"));
                    renderer.material.color = colors[i];
                }

                // Disable collider (not needed for visualization)
                Collider collider = point.GetComponent<Collider>();
                if (collider != null)
                {
                    Destroy(collider);
                }

                m_depthPoints.Add(point);

                // Break if we hit max
                if (m_depthPoints.Count >= maxPoints)
                {
                    break;
                }
            }

            Debug.Log($"[DEPTH RENDER] Rendered {m_depthPoints.Count} depth points in scene");
        }

        private void ClearDepthPoints()
        {
            foreach (var point in m_depthPoints)
            {
                if (point != null)
                {
                    Destroy(point);
                }
            }
            m_depthPoints.Clear();
        }

        private Vector3 ProjectDepthPoint(float normX, float normY, float depth, Pose cameraPose)
        {
            // Simple depth projection
            // In a real implementation, you would use camera intrinsics
            // For now, use a simple perspective projection

            // Convert normalized screen space to camera space
            float aspectRatio = (float)Screen.width / Screen.height;
            float fov = 60f;  // Approximate FOV
            float fovRad = fov * Mathf.Deg2Rad;
            float tanHalfFov = Mathf.Tan(fovRad * 0.5f);

            // Camera space coordinates
            float cameraX = (normX * 2f - 1f) * depth * tanHalfFov * aspectRatio;
            float cameraY = (normY * 2f - 1f) * depth * tanHalfFov;
            float cameraZ = -depth;  // Negative Z for forward

            Vector3 cameraSpacePos = new Vector3(cameraX, cameraY, cameraZ);

            // Transform to world space
            Vector3 worldPos = cameraPose.position + cameraPose.rotation * cameraSpacePos;

            return worldPos;
        }

        private Color GetDepthColor(float depthValue)
        {
            // Convert depth [0, 1] to heat map color
            // Red (near) -> Yellow -> Green -> Cyan -> Blue (far)

            if (depthValue < 0.2f)
            {
                // Red to Yellow
                float t = depthValue / 0.2f;
                return Color.Lerp(Color.red, Color.yellow, t);
            }
            else if (depthValue < 0.4f)
            {
                // Yellow to Green
                float t = (depthValue - 0.2f) / 0.2f;
                return Color.Lerp(Color.yellow, Color.green, t);
            }
            else if (depthValue < 0.6f)
            {
                // Green to Cyan
                float t = (depthValue - 0.4f) / 0.2f;
                return Color.Lerp(Color.green, Color.cyan, t);
            }
            else if (depthValue < 0.8f)
            {
                // Cyan to Blue
                float t = (depthValue - 0.6f) / 0.2f;
                return Color.Lerp(Color.cyan, Color.blue, t);
            }
            else
            {
                // Blue to Magenta
                float t = (depthValue - 0.8f) / 0.2f;
                return Color.Lerp(Color.blue, Color.magenta, t);
            }
        }
    }
}
