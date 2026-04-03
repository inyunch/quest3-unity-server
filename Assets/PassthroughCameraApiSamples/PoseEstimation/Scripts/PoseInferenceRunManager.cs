// Copyright (c) Meta Platforms, Inc. and affiliates.

using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Meta.XR;
using Meta.XR.Samples;
using UnityEngine;
using UnityEngine.Networking;
using Newtonsoft.Json;

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

        [Header("Server Inference")]
        [SerializeField] private string m_serverUrl = "http://192.168.0.135:8001/infer_human?mode=both";
        [SerializeField] private float m_minKeypointScore = 0.3f;

        private int m_frameId = 0;

        // Store timing data from previous frame to send as headers in next request
        private float m_lastE2eMs = 0f;
        private float m_lastUploadMs = 0f;
        private float m_lastDownloadMs = 0f;
        private float m_lastParseMs = 0f;
        private int m_lastUploadBytes = 0;
        private int m_lastDownloadBytes = 0;

        private IEnumerator Start()
        {
            Debug.Log("[POSE INF] PoseInferenceRunManager started");

            // Reference checks
            Debug.Log($"[POSE REF] cameraAccess={m_cameraAccess != null}");
            Debug.Log($"[POSE REF] uiMenuManager={m_uiMenuManager != null}");
            Debug.Log($"[POSE REF] poseManager={m_poseManager != null}");
            Debug.Log($"[POSE REF] uiPose={m_uiPose != null}");

            // Test server connection at startup
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

            [DllImport("OVRPlugin", CallingConvention = CallingConvention.Cdecl)]
            static extern OVRPlugin.Result ovrp_GetNodePoseStateAtTime(double time, OVRPlugin.Node nodeId, out OVRPlugin.PoseStatef nodePoseState);
            if (!ovrp_GetNodePoseStateAtTime(OVRPlugin.GetTimeInSeconds(), OVRPlugin.Node.Head, out _).IsSuccess())
            {
                Debug.Log("ovrp_GetNodePoseStateAtTime failed, which means 'm_cameraAccess.GetCameraPose()' is not reliable, skipping.");
                yield break;
            }

            var cachedCameraPose = m_cameraAccess.GetCameraPose();

            // Update Capture data
            Texture targetTexture = m_cameraAccess.GetTexture();

            // Run server inference
            yield return RunServerInference(targetTexture);

            // Checking if spatial anchor is tracked ensures skeleton is placed at correct world space positions
            if (!m_cameraAccess.IsPlaying || m_poseManager.m_spatialAnchor == null || !m_poseManager.m_spatialAnchor.IsTracked)
            {
                yield break;
            }

            // m_uiPose.DrawPoseSkeletons() is called from within RunServerInference after parsing
        }

        // ============================================================================
        // SERVER INFERENCE - JSON Response Classes
        // ============================================================================

        [System.Serializable]
        private class PoseServerResponse
        {
            public DetectionResultData detections;
            public SkeletonData skeleton;
            public int model_input_width;
            public int model_input_height;
            public int input_image_width;
            public int input_image_height;
            public float processing_time_ms;
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
            public List<PersonSkeleton> persons;  // persons is an ARRAY of person objects, not an int!
        }

        [System.Serializable]
        public class PersonSkeleton
        {
            public List<Keypoint> keypoints;  // 17 keypoints in COCO order
            public float[] bbox;              // [x1_norm, y1_norm, x2_norm, y2_norm]
        }

        [System.Serializable]
        public class Keypoint
        {
            public string name;
            public float x;      // normalized 0-1
            public float y;      // normalized 0-1
            public float score;  // confidence 0-1
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
                    Debug.Log($"[POSE SERVER TEST] ✓ Connection OK! Response: {req.downloadHandler.text}");
                }
                else
                {
                    Debug.LogError($"[POSE SERVER TEST] ✗ Connection FAILED: {req.error}");
                    Debug.LogError($"[POSE SERVER TEST] Result: {req.result}");
                    Debug.LogError($"[POSE SERVER TEST] Response Code: {req.responseCode}");
                }
            }
        }

        // ============================================================================
        // SERVER INFERENCE - HTTP Request Method
        // ============================================================================

        private IEnumerator RunServerInference(Texture texture)
        {
            // Increment frame counter and start E2E timing
            m_frameId++;
            float e2eStartTime = Time.realtimeSinceStartup;

            // 1. Convert texture to Texture2D if needed
            Texture2D tex2D = texture as Texture2D;
            if (tex2D == null)
            {
                // Handle RenderTexture case
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
                    Debug.LogError("[POSE SERVER] Unsupported texture type for server inference");
                    yield break;
                }
            }

            // 2. Encode texture as JPEG
            byte[] jpegBytes = tex2D.EncodeToJPG(90);
            int uploadBytes = jpegBytes.Length;
            Debug.Log($"[POSE SERVER] Encoded JPEG: {uploadBytes} bytes ({tex2D.width}x{tex2D.height})");

            // 3. Create multipart form POST
            List<IMultipartFormSection> formData = new List<IMultipartFormSection>();
            formData.Add(new MultipartFormFileSection("image", jpegBytes, "frame.jpg", "image/jpeg"));

            UnityWebRequest request = UnityWebRequest.Post(m_serverUrl, formData);

            // Add HTTP headers (including timing data from previous frame)
            request.SetRequestHeader("X-Scene-Name", "PoseEstimation");
            request.SetRequestHeader("X-Frame-Id", m_frameId.ToString());

            // Send timing data from PREVIOUS frame (frame N-1) for Excel logging
            // These values are 0 for the first frame, which is expected
            request.SetRequestHeader("X-E2E-Ms", m_lastE2eMs.ToString("F1"));
            request.SetRequestHeader("X-Upload-Ms", m_lastUploadMs.ToString("F1"));
            request.SetRequestHeader("X-Download-Ms", m_lastDownloadMs.ToString("F1"));
            request.SetRequestHeader("X-Parse-Ms", m_lastParseMs.ToString("F1"));
            request.SetRequestHeader("X-Upload-Bytes", m_lastUploadBytes.ToString());
            request.SetRequestHeader("X-Download-Bytes", m_lastDownloadBytes.ToString());

            Debug.Log($"[POSE SEND] Sending frame {m_frameId} to server...");

            // 4. Send request and measure UPLOAD time
            float uploadStartTime = Time.realtimeSinceStartup;

            // Start the request
            UnityWebRequestAsyncOperation asyncOp = request.SendWebRequest();

            // Poll until upload completes
            while (!asyncOp.isDone && request.uploadProgress < 1.0f)
            {
                yield return null;
            }

            float uploadDoneTime = Time.realtimeSinceStartup;
            float uploadMs = (uploadDoneTime - uploadStartTime) * 1000f;

            // Wait for response to complete
            yield return asyncOp;

            Debug.Log($"[POSE SERVER SEND] <<< Request completed. Result: {request.result}");

            if (request.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError($"[POSE SERVER] Inference failed: {request.error}");
                Debug.LogError($"[POSE SERVER] Result type: {request.result}");
                Debug.LogError($"[POSE SERVER] Response code: {request.responseCode}");
                Debug.LogError($"[POSE SERVER] URL was: {m_serverUrl}");
                yield break;
            }

            // 5. Parse JSON response and measure PARSE time
            float parseStartTime = Time.realtimeSinceStartup;

            string jsonResponse = request.downloadHandler.text;
            Debug.Log($"[POSE RECV] Response received, length={jsonResponse.Length}");
            Debug.Log($"[POSE RECV] First 200 chars: {jsonResponse.Substring(0, Mathf.Min(200, jsonResponse.Length))}");

            // WORKAROUND: Extract skeleton and detections BEFORE parsing full JSON
            // This bypasses the huge segmentation mask that causes parser to fail
            PoseServerResponse response = new PoseServerResponse();

            // Extract skeleton portion using string parsing
            Debug.Log($"[POSE JSON] Extracting skeleton from large JSON (size={jsonResponse.Length})...");
            string skeletonJson = ExtractJsonField(jsonResponse, "skeleton");
            if (!string.IsNullOrEmpty(skeletonJson))
            {
                // CRITICAL: Show actual skeleton JSON structure
                Debug.Log($"[SKELETON RAW] Full skeleton JSON ({skeletonJson.Length} chars): {skeletonJson}");
                Debug.Log($"[SKELETON RAW] First 300 chars: {skeletonJson.Substring(0, Mathf.Min(300, skeletonJson.Length))}");

                try
                {
                    response.skeleton = JsonConvert.DeserializeObject<SkeletonData>(skeletonJson);
                    Debug.Log($"[POSE JSON] Skeleton extracted successfully");
                }
                catch (System.Exception e)
                {
                    Debug.LogError($"[POSE JSON] Failed to parse extracted skeleton: {e.Message}");
                    Debug.LogError($"[POSE JSON] Error details: {e}");
                }
            }
            else
            {
                Debug.LogError($"[POSE JSON] Failed to extract skeleton field from JSON");
            }

            // Extract detections portion
            string detectionsJson = ExtractJsonField(jsonResponse, "detections");
            if (!string.IsNullOrEmpty(detectionsJson))
            {
                // Also log detections for comparison
                Debug.Log($"[DETECTIONS RAW] First 200 chars: {detectionsJson.Substring(0, Mathf.Min(200, detectionsJson.Length))}");

                try
                {
                    response.detections = JsonConvert.DeserializeObject<DetectionResultData>(detectionsJson);
                    Debug.Log($"[POSE JSON] Detections extracted successfully");
                }
                catch (System.Exception e)
                {
                    Debug.LogError($"[POSE JSON] Failed to parse extracted detections: {e.Message}");
                    Debug.LogError($"[POSE JSON] Error details: {e}");
                }
            }

            // Extract processing_time_ms
            string procTimeStr = ExtractSimpleJsonValue(jsonResponse, "processing_time_ms");
            if (!string.IsNullOrEmpty(procTimeStr) && float.TryParse(procTimeStr, out float procTime))
            {
                response.processing_time_ms = procTime;
                Debug.Log($"[POSE JSON] Processing time: {procTime:F1}ms");
            }

            if (response == null || response.skeleton == null)
            {
                Debug.LogError("[POSE SERVER] Failed to parse JSON response");
                yield break;
            }

            // Parse time measurement complete
            float parseMs = (Time.realtimeSinceStartup - parseStartTime) * 1000f;

            // Calculate E2E time and derive download time
            float e2eMs = (Time.realtimeSinceStartup - e2eStartTime) * 1000f;
            int downloadBytes = (int)request.downloadedBytes;
            float serverProcMs = response.processing_time_ms;
            float downloadMs = Mathf.Max(0f, e2eMs - uploadMs - serverProcMs - parseMs);

            Debug.Log($"[TIMING] E2E={e2eMs:F0}ms (upload={uploadMs:F0}ms server={serverProcMs:F0}ms download={downloadMs:F0}ms parse={parseMs:F0}ms)");
            Debug.Log($"[BYTES] Upload={uploadBytes} Download={downloadBytes}");

            // Detailed parse verification logs
            Debug.Log($"[POSE PARSE] skeleton null={response.skeleton == null}");
            int personsCount = response.skeleton?.persons?.Count ?? 0;
            Debug.Log($"[POSE PARSE] persons count={personsCount}");
            Debug.Log($"[POSE PARSE] detections={response.detections?.detections?.Length ?? 0}");

            if (response.skeleton?.persons != null && response.skeleton.persons.Count > 0)
            {
                Debug.Log($"[POSE PARSE] persons array has {response.skeleton.persons.Count} person(s)");
                var p = response.skeleton.persons[0];
                Debug.Log($"[POSE PARSE] person 0: {p.keypoints?.Count ?? 0} keypoints");

                if (p.keypoints != null && p.keypoints.Count > 0)
                {
                    // Find nose keypoint
                    var nose = p.keypoints.Find(k => k.name == "nose");
                    if (nose != null)
                    {
                        Debug.Log($"[POSE PARSE] nose: x={nose.x:F3} y={nose.y:F3} score={nose.score:F2}");
                    }

                    // Log first 3 keypoints
                    for (int i = 0; i < Mathf.Min(3, p.keypoints.Count); i++)
                    {
                        var kp = p.keypoints[i];
                        Debug.Log($"[POSE PARSE] kp={kp.name} pos=({kp.x:F3},{kp.y:F3}) score={kp.score:F2}");
                    }
                }
            }
            else
            {
                Debug.LogWarning($"[POSE PARSE] skeleton.persons is NULL or empty!");
            }

            // 6. Draw pose skeletons on UI
            var cachedCameraPose = m_cameraAccess.GetCameraPose();

            if (response.skeleton != null && response.skeleton.persons != null && response.skeleton.persons.Count > 0)
            {
                Debug.Log($"[POSE SERVER] Received {response.skeleton.persons.Count} person(s) with pose data");
                m_uiPose.DrawPoseSkeletons(response.skeleton.persons.ToArray(), cachedCameraPose, m_minKeypointScore);
            }
            else
            {
                Debug.Log("[POSE SERVER] No pose data in response");
                m_uiPose.ClearSkeletons();
            }

            // 7. Update HUD with inference metrics
            if (m_inferenceHUD != null)
            {
                // Compute average detection confidence
                float avgConfidence = 0f;
                if (response.detections != null && response.detections.detections != null && response.detections.detections.Length > 0)
                {
                    float sum = 0f;
                    foreach (var det in response.detections.detections)
                    {
                        sum += det.confidence;
                    }
                    avgConfidence = sum / response.detections.detections.Length;
                }

                // Compute average keypoint confidence
                float keypointAvgConf = 0f;
                if (response.skeleton != null && response.skeleton.persons != null && response.skeleton.persons.Count > 0)
                {
                    List<float> allScores = new List<float>();
                    foreach (var person in response.skeleton.persons)
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

                // Get detection count (number of persons)
                int detectionCount = response.skeleton?.persons?.Count ?? 0;

                // Update HUD with detailed breakdown
                m_inferenceHUD.UpdateHUD(
                    e2eMs,
                    uploadMs,
                    serverProcMs,
                    downloadMs,
                    parseMs,
                    uploadBytes,
                    downloadBytes,
                    detectionCount,
                    avgConfidence,
                    keypointAvgConf
                );
            }

            // Store timing data for next frame's HTTP headers (to log in Excel)
            m_lastE2eMs = e2eMs;
            m_lastUploadMs = uploadMs;
            m_lastDownloadMs = downloadMs;
            m_lastParseMs = parseMs;
            m_lastUploadBytes = uploadBytes;
            m_lastDownloadBytes = downloadBytes;
        }

        // ============================================================================
        // JSON FIELD EXTRACTION - Bypasses large segmentation mask
        // ============================================================================

        /// <summary>
        /// Extracts a specific field from JSON without parsing the entire document.
        /// This bypasses the huge segmentation mask that causes full parsing to fail.
        /// </summary>
        private string ExtractJsonField(string json, string fieldName)
        {
            string searchPattern = $"\"{fieldName}\":";
            int fieldStart = json.IndexOf(searchPattern);

            if (fieldStart < 0)
            {
                Debug.LogError($"[POSE JSON] Field '{fieldName}' not found in JSON");
                return null;
            }

            // Skip past the field name and colon
            int valueStart = fieldStart + searchPattern.Length;

            // Skip whitespace
            while (valueStart < json.Length && char.IsWhiteSpace(json[valueStart]))
            {
                valueStart++;
            }

            if (valueStart >= json.Length)
            {
                Debug.LogError($"[POSE JSON] Unexpected end of JSON after field '{fieldName}'");
                return null;
            }

            // Determine if value is object {...} or array [...]
            char startChar = json[valueStart];
            char endChar;
            if (startChar == '{')
            {
                endChar = '}';
            }
            else if (startChar == '[')
            {
                endChar = ']';
            }
            else
            {
                Debug.LogError($"[POSE JSON] Field '{fieldName}' does not start with {{ or [");
                return null;
            }

            // Find matching closing bracket/brace
            int depth = 0;
            int valueEnd = valueStart;
            bool inString = false;
            bool escaped = false;

            for (int i = valueStart; i < json.Length; i++)
            {
                char c = json[i];

                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (c == '\\')
                {
                    escaped = true;
                    continue;
                }

                if (c == '"')
                {
                    inString = !inString;
                    continue;
                }

                if (!inString)
                {
                    if (c == startChar)
                    {
                        depth++;
                    }
                    else if (c == endChar)
                    {
                        depth--;
                        if (depth == 0)
                        {
                            valueEnd = i + 1;
                            break;
                        }
                    }
                }
            }

            if (depth != 0)
            {
                Debug.LogError($"[POSE JSON] Mismatched brackets for field '{fieldName}'");
                return null;
            }

            string extracted = json.Substring(valueStart, valueEnd - valueStart);
            Debug.Log($"[POSE JSON] Extracted '{fieldName}': {extracted.Length} chars");
            return extracted;
        }

        /// <summary>
        /// Extracts a simple numeric or string value from JSON.
        /// </summary>
        private string ExtractSimpleJsonValue(string json, string fieldName)
        {
            string searchPattern = $"\"{fieldName}\":";
            int fieldStart = json.IndexOf(searchPattern);

            if (fieldStart < 0)
            {
                return null;
            }

            // Skip past the field name and colon
            int valueStart = fieldStart + searchPattern.Length;

            // Skip whitespace
            while (valueStart < json.Length && char.IsWhiteSpace(json[valueStart]))
            {
                valueStart++;
            }

            if (valueStart >= json.Length)
            {
                return null;
            }

            // Find end of value (comma, closing brace, or end of string)
            int valueEnd = valueStart;
            bool inString = json[valueStart] == '"';

            if (inString)
            {
                // Skip opening quote
                valueStart++;
                valueEnd = valueStart;
                // Find closing quote
                while (valueEnd < json.Length && json[valueEnd] != '"')
                {
                    if (json[valueEnd] == '\\') valueEnd++; // Skip escaped characters
                    valueEnd++;
                }
            }
            else
            {
                // Find end of number
                while (valueEnd < json.Length && json[valueEnd] != ',' && json[valueEnd] != '}' && json[valueEnd] != ']' && !char.IsWhiteSpace(json[valueEnd]))
                {
                    valueEnd++;
                }
            }

            if (valueEnd > valueStart)
            {
                return json.Substring(valueStart, valueEnd - valueStart).Trim();
            }

            return null;
        }
    }
}
