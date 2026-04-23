// Copyright (c) Meta Platforms, Inc. and affiliates.

using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Meta.XR;
using Meta.XR.Samples;
using UnityEngine;
using UnityEngine.Networking;
using Newtonsoft.Json;
using PassthroughCameraSamples.Shared;

namespace PassthroughCameraSamples.PoseEstimation
{
    [MetaCodeSample("PassthroughCameraApiSamples-PoseEstimation")]
    public class PoseInferenceRunManager : MonoBehaviour
    {
        [SerializeField] private PassthroughCameraAccess m_cameraAccess;
        [SerializeField] private PassthroughCameraSamples.MultiObjectDetection.DetectionUiMenuManager m_uiMenuManager;
        [SerializeField] private PoseEstimationManager m_poseManager;

        [Header("UI display references")]
        [SerializeField] private PoseSkeletonUiManager m_uiPose;
        [SerializeField] private InferenceHUD m_inferenceHUD;
        [SerializeField] private SharedInferenceHUD m_sharedHUD;

        [Header("Server Inference (NEW)")]
        [SerializeField] private InferenceConfig m_inferenceConfig = new InferenceConfig
        {
            mode = InferenceMode.Both,
            targetFPS = 5f,
            jpegQuality = 80,
            includeMask = false,
            includeDepth = false
        };
        [SerializeField] private float m_minKeypointScore = 0.3f;

        [Header("Legacy Server Inference (DEPRECATED - use m_inferenceConfig instead)")]
        [SerializeField] private string m_serverUrl = "";  // Left for backward compatibility
        [SerializeField, Range(60, 100)] private int m_jpegQuality = 80;  // DEPRECATED

        private int m_frameId = 0;
        private string m_sessionId;  // GUID to uniquely identify this recording session

        // ============================================================================
        // V3.0 OOP COMPONENTS - Replaces inline UDP + telemetry code
        // ============================================================================
        private UDPTransportManager m_transport;
        private FrameTelemetryTracker m_telemetry;
        [SerializeField] private bool m_useUDPTransport = true;  // Feature flag for safe rollout

        // Phase 3: Fixed cadence non-blocking send
        private float m_nextInferenceTime = 0f;  // Next time to send inference request
        private bool m_cameraReady = false;  // Camera initialization complete

        private IEnumerator Start()
        {
            // Generate unique session ID for this recording session
            m_sessionId = System.Guid.NewGuid().ToString();
            Debug.Log($"[SESSION] Started session: {m_sessionId}");

            // V3.0: Initialize OOP components
            if (m_inferenceConfig.useServerConfig && m_useUDPTransport)
            {
                try
                {
                    // Initialize UDP Transport Manager
                    m_transport = new UDPTransportManager(
                        serverIP: ServerConfig.Instance.ServerIP,
                        sendPort: 8002,
                        receivePort: 8003
                    );
                    m_transport.Initialize();
                    Debug.Log($"[V3 POSE] UDP Transport initialized");

                    // Initialize Telemetry Tracker
                    m_telemetry = new FrameTelemetryTracker(
                        sessionId: m_sessionId,
                        sceneName: "PoseEstimation",
                        enableLocalTelemetry: true
                    );
                    Debug.Log($"[V3 POSE] Telemetry tracker initialized");
                }
                catch (System.Exception e)
                {
                    Debug.LogError($"[V3 POSE] Failed to initialize V3 components: {e.Message}");
                    m_useUDPTransport = false;  // Fall back to HTTP mode
                }
            }

            Debug.Log("[POSE INF] PoseInferenceRunManager started");

            // Reference checks
            Debug.Log($"[POSE REF] cameraAccess={m_cameraAccess != null}");
            Debug.Log($"[POSE REF] uiMenuManager={m_uiMenuManager != null}");
            Debug.Log($"[POSE REF] poseManager={m_poseManager != null}");
            Debug.Log($"[POSE REF] uiPose={m_uiPose != null}");

            // Migrate from legacy settings if needed
            if (!string.IsNullOrEmpty(m_serverUrl) && m_inferenceConfig.baseUrl == "http://192.168.0.135:8001/infer_human")
            {
                Debug.LogWarning("[POSE] m_serverUrl is deprecated. Please use m_inferenceConfig instead.");
            }

            if (m_jpegQuality != 80 && m_inferenceConfig.jpegQuality == 80)
            {
                Debug.LogWarning($"[POSE] Migrating legacy m_jpegQuality ({m_jpegQuality}) to m_inferenceConfig");
                m_inferenceConfig.jpegQuality = m_jpegQuality;
            }

            m_inferenceConfig.Validate();
            m_inferenceConfig.LogSummary();

            // Initialize SharedInferenceHUD if available
            if (m_sharedHUD != null)
            {
                m_sharedHUD.SetMode(m_inferenceConfig.mode, m_inferenceConfig.targetFPS);
            }

            // Test server connection at startup
            Debug.Log("[SERVER TEST] Testing connection to server...");
            yield return TestServerConnection();

            // Set camera ready flag and initialize timing
            m_cameraReady = true;
            m_nextInferenceTime = Time.time;

            Debug.Log("[V3 POSE] Start() complete - inference now driven by Update() at fixed cadence");
        }

        private void OnDestroy()
        {
            // V3.0: Shutdown OOP components
            m_transport?.Shutdown();
            m_telemetry?.Shutdown();

            Debug.Log("[V3 POSE] Cleanup complete");
        }

        // ============================================================================
        // SERVER INFERENCE - JSON Response Classes (OLD nested format - RESTORED)
        // ============================================================================

        [System.Serializable]
        private class PoseServerResponse
        {
            // OLD format - restore compatibility with old renderer
            public DetectionResultData detections;  // Nested structure
            public SkeletonData skeleton;

            public int input_image_width;
            public int input_image_height;
            public float processing_time_ms;
            public float server_queue_ms;
            public float server_postprocess_ms;
            public double t_server_recv;
            public double t_server_send;
            public double server_receive_ts;
            public double server_process_start_ts;
            public double server_process_end_ts;
        }

        [System.Serializable]
        private class DetectionResultData
        {
            public DetectionData[] detections;
            public int num_detections;
        }

        [System.Serializable]
        private class DetectionData
        {
            public int class_id;
            public string class_name;
            public float confidence;
            public float[] bbox;           // normalized [0-1]
            public int[] bbox_pixels;      // absolute pixels
        }

        [System.Serializable]
        private class SkeletonData
        {
            public List<PersonSkeleton> persons;
        }

        [System.Serializable]
        public class PersonSkeleton
        {
            public List<Keypoint> keypoints;
            public float[] bbox;
        }

        [System.Serializable]
        public class Keypoint
        {
            public string name;
            public float x;      // Normalized coordinates [0-1]
            public float y;
            public float score;  // confidence 0-1
        }

        // ============================================================================
        // V3.0: NON-BLOCKING INFERENCE (Fixed cadence send, async response)
        // ============================================================================

        /// <summary>
        /// V3.0: Non-blocking inference runner using UDPTransportManager.
        /// Simplified version that delegates UDP/telemetry to OOP components.
        /// </summary>
        private IEnumerator RunInferenceNonBlocking()
        {
            // Quick checks
            if (!m_cameraAccess.IsPlaying)
            {
                Debug.Log("[V3 POSE] Camera not playing, skipping inference");
                yield break;
            }

            [DllImport("OVRPlugin", CallingConvention = CallingConvention.Cdecl)]
            static extern OVRPlugin.Result ovrp_GetNodePoseStateAtTime(double time, OVRPlugin.Node nodeId, out OVRPlugin.PoseStatef nodePoseState);
            if (!ovrp_GetNodePoseStateAtTime(OVRPlugin.GetTimeInSeconds(), OVRPlugin.Node.Head, out _).IsSuccess())
            {
                Debug.Log("[V3 POSE] ovrp_GetNodePoseStateAtTime failed, skipping");
                yield break;
            }

            // Get current frame texture
            Texture targetTexture = m_cameraAccess.GetTexture();

            // 1. Encode JPEG
            byte[] jpegData = EncodeTextureToJPEG(targetTexture);
            if (jpegData == null)
            {
                Debug.LogError("[V3 POSE] Failed to encode texture to JPEG");
                yield break;
            }

            // 2. Create frame trace via FrameTelemetryTracker
            m_frameId++;
            FrameTrace trace = m_telemetry.CreateFrame(m_frameId, jpegData.Length);
            trace.upload_bytes_uncompressed = targetTexture.width * targetTexture.height * 3;

            Debug.Log($"[V3 POSE] Frame {trace.frame_id} created, size={jpegData.Length} bytes");

            // 3. Build minimal telemetry JSON for server (mode + scene)
            string telemetryJson = "{\"mode\":\"both\",\"scene\":\"PoseEstimation\"}";

            // 4. Send via UDPTransportManager (NON-BLOCKING!)
            m_transport.SendFrame(trace, jpegData, telemetryJson);

            Debug.Log($"[V3 POSE] Frame {trace.frame_id} sent via UDP (mode=both)");

            // V3.0: NO polling coroutine needed - UDPTransportManager handles responses in background
            // Responses will be processed in Update() via TryGetResponse()
        }

        // ============================================================================
        // SERVER INFERENCE - Connection Test
        // ============================================================================

        private IEnumerator TestServerConnection()
        {
            string testUrl = "http://192.168.0.135:8001/";
            Debug.Log($"[POSE SERVER TEST] Connecting to {testUrl}");

            using (UnityWebRequest req = UnityWebRequest.Get(testUrl))
            {
                req.timeout = 5;
                yield return req.SendWebRequest();

                if (req.result == UnityWebRequest.Result.Success)
                {
                    Debug.Log($"[POSE SERVER TEST] Connection OK! Response: {req.downloadHandler.text}");
                }
                else
                {
                    Debug.LogError($"[POSE SERVER TEST] Connection FAILED: {req.error}");
                    Debug.LogError($"[POSE SERVER TEST] Result: {req.result}");
                    Debug.LogError($"[POSE SERVER TEST] Response Code: {req.responseCode}");
                }
            }
        }

        // ============================================================================
        // V3.0: UPDATE LOOP (Inference triggering + response processing)
        // ============================================================================

        private void Update()
        {
            // V3.0: ALWAYS poll for UDP responses (even if camera not ready)
            // Responses may arrive for frames sent before camera stopped
            if (m_inferenceConfig.useServerConfig && m_useUDPTransport && m_transport != null)
            {
                while (m_transport.TryGetResponse(out FrameResponse response))
                {
                    HandleV3Response(response);
                }
            }

            // V3.0: Fixed cadence inference triggering (UDP mode only)
            if (m_inferenceConfig.useServerConfig && m_useUDPTransport && m_cameraReady)
            {
                // Check if paused
                if (m_uiMenuManager != null && m_uiMenuManager.IsPaused)
                {
                    return;  // Don't send inference requests while paused
                }

                // Check if it's time for next inference
                float currentTime = Time.time;
                if (currentTime >= m_nextInferenceTime)
                {
                    // Calculate next inference time BEFORE starting inference (fixed cadence)
                    float targetInterval = m_inferenceConfig.GetInferenceInterval();
                    m_nextInferenceTime = currentTime + targetInterval;

                    // Start inference without blocking (fire and forget)
                    StartCoroutine(RunInferenceNonBlocking());

                    Debug.Log($"[V3 POSE] Triggered inference at fixed cadence (interval={targetInterval * 1000f:F0}ms)");
                }

                // V3.0: Periodic telemetry cleanup (every 60 frames = ~1 second at 60fps)
                // ✅ P0 FIX: Null-safe operator in case telemetry init failed
                if (Time.frameCount % 60 == 0)
                {
                    m_telemetry?.CleanupOldTraces();
                }
            }
        }

        // ============================================================================
        // V3.0: RESPONSE HANDLING
        // ============================================================================

        /// <summary>
        /// V3.0: Handle received inference response from UDPTransportManager.
        /// Updates telemetry and displays pose/detection results.
        /// </summary>
        private void HandleV3Response(FrameResponse response)
        {
            Debug.Log($"[V3 POSE] Received response for frame {response.frame_id}, " +
                      $"server_proc={response.processing_time_ms:F1}ms, " +
                      $"queue_wait={response.queue_wait_ms:F1}ms");

            // 1. Update telemetry (mark completed)
            m_telemetry.MarkFrameCompleted(response.frame_id, response);

            // 2. Display pose results
            DisplayV3Frame(response);

            // 3. Mark as displayed (this automatically writes to CSV)
            m_telemetry.MarkFrameDisplayed(response.frame_id);
        }

        /// <summary>
        /// V3.0: Display pose frame using FrameResponse.
        /// Replaces old DisplayFrame() that used PoseServerResponse.
        /// </summary>
        private void DisplayV3Frame(FrameResponse response)
        {
            // Get cached camera pose
            var cachedCameraPose = m_cameraAccess.GetCameraPose();

            // Check if spatial anchor is tracked
            if (!m_cameraAccess.IsPlaying || m_poseManager.m_spatialAnchor == null || !m_poseManager.m_spatialAnchor.IsTracked)
            {
                m_uiPose.ClearSkeletons();
                return;
            }

            // Check if response has pose data
            if (!response.HasPose())
            {
                m_uiPose.ClearSkeletons();
                Debug.LogWarning($"[V3 POSE] Frame {response.frame_id} has no pose data");
                return;
            }

            var pose = response.pose;

            // Convert FrameResponse.PoseData to PersonSkeleton[] for rendering
            List<PersonSkeleton> persons = new List<PersonSkeleton>();

            if (pose.persons != null && pose.persons.Length > 0)
            {
                Debug.Log($"[V3 POSE] Frame {response.frame_id}: {pose.persons.Length} person(s)");

                foreach (var p in pose.persons)
                {
                    PersonSkeleton person = new PersonSkeleton
                    {
                        keypoints = new List<Keypoint>(),
                        bbox = p.bbox
                    };

                    if (p.keypoints != null)
                    {
                        foreach (var kp in p.keypoints)
                        {
                            person.keypoints.Add(new Keypoint
                            {
                                name = kp.name,
                                x = kp.x,
                                y = kp.y,
                                score = kp.score
                            });
                        }
                    }

                    persons.Add(person);
                }

                // Draw pose skeletons
                m_uiPose.DrawPoseSkeletons(persons.ToArray(), cachedCameraPose, m_minKeypointScore);
                Debug.Log($"[V3 POSE] Displayed frame {response.frame_id} with {persons.Count} person(s)");
            }
            else
            {
                m_uiPose.ClearSkeletons();
                Debug.Log($"[V3 POSE] Frame {response.frame_id} has no persons, clearing skeletons");
            }

            // Update UI metrics
            UpdateUIMetrics(response);
        }

        /// <summary>
        /// Update UI components with inference metrics from FrameResponse.
        /// </summary>
        private void UpdateUIMetrics(FrameResponse response)
        {
            float e2eMs = response.server_e2e_ms;
            float uploadMs = 0f;  // Not tracked separately in V3
            float serverProcMs = response.processing_time_ms;
            float downloadMs = 0f;  // Not tracked separately in V3
            float parseMs = 0f;  // Not tracked separately in V3
            int uploadBytesCompressed = 0;  // Not available in response
            int downloadBytesCompressed = 0;  // Not available in response

            // Detection metrics
            int detectionCount = 0;
            float avgConfidence = 0f;
            if (response.HasDetections() && response.detections != null && response.detections.Length > 0)
            {
                detectionCount = response.detections.Length;
                float sum = 0f;
                foreach (var det in response.detections)
                {
                    sum += det.confidence;
                }
                avgConfidence = sum / detectionCount;
            }

            // Keypoint average confidence
            float keypointAvgConf = 0f;
            if (response.HasPose() && response.pose.persons != null && response.pose.persons.Length > 0)
            {
                List<float> allScores = new List<float>();
                foreach (var person in response.pose.persons)
                {
                    if (person != null && person.keypoints != null)
                    {
                        foreach (var kp in person.keypoints)
                        {
                            if (kp.score > 0f)
                            {
                                allScores.Add(kp.score);
                            }
                        }
                    }
                }
                if (allScores.Count > 0)
                {
                    float sum = 0f;
                    foreach (var score in allScores)
                    {
                        sum += score;
                    }
                    keypointAvgConf = sum / allScores.Count;
                }
            }

            // Update legacy HUD
            if (m_inferenceHUD != null)
            {
                m_inferenceHUD.UpdateHUD(
                    e2eMs,
                    uploadMs,
                    serverProcMs,
                    downloadMs,
                    parseMs,
                    uploadBytesCompressed,
                    0,  // downloadBytesUncompressed
                    downloadBytesCompressed,
                    detectionCount,
                    avgConfidence,
                    keypointAvgConf
                );
            }

            // Update SharedInferenceHUD
            if (m_sharedHUD != null)
            {
                m_sharedHUD.UpdateMetrics(
                    e2eMs,
                    uploadMs,
                    serverProcMs,
                    downloadMs,
                    parseMs,
                    uploadBytesCompressed,
                    0,  // downloadBytesUncompressed
                    downloadBytesCompressed,
                    detectionCount,
                    avgConfidence,
                    keypointAvgConf
                );
            }
        }

        // ============================================================================
        // TEXTURE ENCODING UTILITY
        // ============================================================================

        /// <summary>
        /// Encode texture to JPEG bytes (extracted from old RunServerInference)
        /// </summary>
        private byte[] EncodeTextureToJPEG(Texture texture)
        {
            // 1. Convert texture to Texture2D if needed
            Texture2D tex2D = texture as Texture2D;
            bool createdTex2D = false;  // Track if we created tex2D (for cleanup)

            if (tex2D == null)
            {
                // Handle RenderTexture case
                RenderTexture rt = texture as RenderTexture;
                if (rt != null)
                {
                    tex2D = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
                    createdTex2D = true;  // Mark for cleanup
                    RenderTexture.active = rt;
                    tex2D.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                    tex2D.Apply();
                    RenderTexture.active = null;
                }
                else
                {
                    Debug.LogError("Unsupported texture type for server inference");
                    return null;
                }
            }

            // 2. Downsample texture if configured (reduces upload size significantly)
            Texture2D textureToEncode = tex2D;
            int downsampleFactor = m_inferenceConfig.downsampleFactor;

            if (downsampleFactor > 1)
            {
                int downsampledWidth = tex2D.width / downsampleFactor;
                int downsampledHeight = tex2D.height / downsampleFactor;

                // Create temporary RenderTexture for downsampling
                RenderTexture rt = RenderTexture.GetTemporary(downsampledWidth, downsampledHeight, 0, RenderTextureFormat.ARGB32);
                rt.filterMode = FilterMode.Bilinear;

                // Blit original texture to downsampled RenderTexture
                Graphics.Blit(tex2D, rt);

                // Read back to Texture2D
                RenderTexture previous = RenderTexture.active;
                RenderTexture.active = rt;
                Texture2D downsampledTex = new Texture2D(downsampledWidth, downsampledHeight, TextureFormat.RGB24, false);
                downsampledTex.ReadPixels(new Rect(0, 0, downsampledWidth, downsampledHeight), 0, 0);
                downsampledTex.Apply();
                RenderTexture.active = previous;

                // Release temporary RenderTexture
                RenderTexture.ReleaseTemporary(rt);

                textureToEncode = downsampledTex;
                Debug.Log($"[POSE DOWNSAMPLE] {tex2D.width}x{tex2D.height} → {downsampledWidth}x{downsampledHeight} (factor={downsampleFactor})");
            }

            // 3. Encode texture as JPEG
            int jpegQuality = m_inferenceConfig.jpegQuality;
            byte[] jpegBytes = textureToEncode.EncodeToJPG(jpegQuality);

            // ✅ FIXED: Defer texture cleanup to next frame to ensure encoding is complete
            // This prevents potential timing issues with texture destruction
            if (downsampleFactor > 1 && textureToEncode != tex2D)
            {
                Texture2D toDestroy = textureToEncode;
                StartCoroutine(DestroyNextFrame(toDestroy));
            }

            if (createdTex2D && tex2D != null)
            {
                Texture2D toDestroy = tex2D;
                StartCoroutine(DestroyNextFrame(toDestroy));
            }

            return jpegBytes;
        }

        /// <summary>
        /// Destroy a texture on the next frame to ensure all operations are complete.
        /// </summary>
        private System.Collections.IEnumerator DestroyNextFrame(UnityEngine.Object obj)
        {
            yield return null;  // Wait one frame
            if (obj != null)
            {
                Destroy(obj);
            }
        }
    }
}
