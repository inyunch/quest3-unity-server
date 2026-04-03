// Copyright (c) Meta Platforms, Inc. and affiliates.

using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Meta.XR;
using Meta.XR.Samples;
using Unity.Collections;
using Unity.InferenceEngine;
using UnityEngine;
using UnityEngine.Networking;

namespace PassthroughCameraSamples.MultiObjectDetection
{
    [MetaCodeSample("PassthroughCameraApiSamples-MultiObjectDetection")]
    public class SentisInferenceRunManager : MonoBehaviour
    {
        [SerializeField] private PassthroughCameraAccess m_cameraAccess;
        [SerializeField] private DetectionUiMenuManager m_uiMenuManager;
        [SerializeField] private DetectionManager m_detectionManager;

        [Header("Sentis Model config")]
        [SerializeField] private BackendType m_backend = BackendType.CPU;
        [SerializeField] private ModelAsset m_sentisModel;
        [SerializeField] private TextAsset m_labelsAsset;
        [SerializeField, Range(0, 1)] private float m_iouThreshold = 0.6f;
        [SerializeField, Range(0, 1)] private float m_scoreThreshold = 0.23f;

        [Header("UI display references")]
        [SerializeField] private SentisInferenceUiManager m_uiInference;
        [SerializeField] private InferenceHUD m_inferenceHUD;

        [Header("Server Inference")]
        [SerializeField] private bool m_useServerInference = false;
        [SerializeField] private string m_serverUrl = "http://192.168.0.135:8001/infer_human?mode=detection";

        [Header("[Editor Only] Convert to Sentis")]
        public ModelAsset OnnxModel;
        [Space(40)]

        private Worker m_engine;
        private Vector2Int m_inputSize;
        private readonly List<(int classId, Vector4 boundingBox)> m_detections = new List<(int classId, Vector4 boundingBox)>();
        private int m_frameId = 0;

        // Store timing data from previous frame to send as headers in next request
        private float m_lastE2eMs = 0f;
        private float m_lastUploadMs = 0f;
        private float m_lastDownloadMs = 0f;
        private float m_lastParseMs = 0f;
        private int m_lastUploadBytes = 0;
        private int m_lastDownloadBytes = 0;

        private void Awake()
        {
            var model = ModelLoader.Load(m_sentisModel);
            var inputShape = model.inputs[0].shape;
            m_inputSize = new Vector2Int(inputShape.Get(2), inputShape.Get(3));
            m_engine = new Worker(model, m_backend);
        }

        private IEnumerator Start()
        {
            m_uiInference.SetLabels(m_labelsAsset);

            // Test server connection at startup
            if (m_useServerInference)
            {
                Debug.Log("[SERVER TEST] Testing connection to server...");
                yield return TestServerConnection();
            }

            while (true)
            {
                while (m_uiMenuManager.IsPaused)
                {
                    yield return null;
                }
                yield return RunInference();
            }
        }

        private void OnDestroy()
        {
            m_engine.PeekOutput(0)?.CompleteAllPendingOperations();
            m_engine.PeekOutput(1)?.CompleteAllPendingOperations();
            m_engine.PeekOutput(2)?.CompleteAllPendingOperations();
            m_engine.Dispose();
        }

        internal static void PreloadModel(ModelAsset modelAsset)
        {
            // Load model
            var model = ModelLoader.Load(modelAsset);
            var inputShape = model.inputs[0].shape;

            // Create engine to run model
            using var worker = new Worker(model, BackendType.CPU);

            // Run inference with an empty image to load the model in the memory. The first inference blocks the main thread for a long time, so we're doing it on the app launch
            Texture tempTexture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            var textureTransform = new TextureTransform().SetDimensions(tempTexture.width, tempTexture.height, 3);
            using var input = new Tensor<float>(new TensorShape(1, 3, inputShape.Get(2), inputShape.Get(3)));
            TextureConverter.ToTensor(tempTexture, input, textureTransform);
            worker.Schedule(input);

            // Complete the inference immediately and destroy the temporary texture
            worker.PeekOutput(0).CompleteAllPendingOperations();
            worker.PeekOutput(1).CompleteAllPendingOperations();
            worker.PeekOutput(2).CompleteAllPendingOperations();
            Destroy(tempTexture);
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

            // ============================================================================
            // INFERENCE: Choose between Server or Local (Sentis)
            // ============================================================================

            if (m_useServerInference)
            {
                // SERVER INFERENCE PATH
                Debug.Log("[INFERENCE] Using SERVER inference");
                yield return RunServerInference(targetTexture);
                // m_detections is now populated by RunServerInference
            }
            else
            {
                // LOCAL SENTIS INFERENCE PATH (ORIGINAL)
                Debug.Log("[INFERENCE] Using LOCAL Sentis inference");

                // Convert the texture to a Tensor and schedule the inference
                var textureTransform = new TextureTransform().SetDimensions(targetTexture.width, targetTexture.height, 3);
                using var input = new Tensor<float>(new TensorShape(1, 3, m_inputSize.x, m_inputSize.y));
                TextureConverter.ToTensor(targetTexture, input, textureTransform);

                // Schedule all model layers
                m_engine.Schedule(input);

                // Get the results. ReadbackAndCloneAsync waits for all layers to complete before returning the result
                var boxesAwaiter = (m_engine.PeekOutput(0) as Tensor<float>).ReadbackAndCloneAsync().GetAwaiter();
                while (!boxesAwaiter.IsCompleted)
                {
                    yield return null;
                }
                using var boxes = boxesAwaiter.GetResult();
                if (boxes.shape[0] == 0)
                {
                    yield break;
                }

                var classIDsAwaiter = (m_engine.PeekOutput(1) as Tensor<int>).ReadbackAndCloneAsync().GetAwaiter();
                while (!classIDsAwaiter.IsCompleted)
                {
                    yield return null;
                }
                using var classIDs = classIDsAwaiter.GetResult();
                if (classIDs.shape[0] == 0)
                {
                    Debug.LogError("classIDs.shape[0] == 0");
                    yield break;
                }

                var scoresAwaiter = (m_engine.PeekOutput(2) as Tensor<float>).ReadbackAndCloneAsync().GetAwaiter();
                while (!scoresAwaiter.IsCompleted)
                {
                    yield return null;
                }
                using var scores = scoresAwaiter.GetResult();
                if (scores.shape[0] == 0)
                {
                    Debug.LogError("scores.shape[0] == 0");
                    yield break;
                }

                NonMaxSuppression(m_detections, boxes, classIDs, scores, m_iouThreshold, m_scoreThreshold);
            }

            // Checking if spatial anchor is tracked ensures bounding boxes are placed at correct world space positIons.
            if (!m_cameraAccess.IsPlaying || m_detectionManager.m_spatialAnchor == null || !m_detectionManager.m_spatialAnchor.IsTracked)
            {
                yield break;
            }

            // Update UI.
            m_uiInference.DrawUIBoxes(m_detections, m_inputSize, cachedCameraPose);
        }

        private static void NonMaxSuppression(List<(int classId, Vector4 boundingBox)> outDetections, Tensor<float> boxes, Tensor<int> classIDs, Tensor<float> scores, float iouThreshold, float scoreThreshold)
        {
            outDetections.Clear();

            // Filter by score threshold first
            List<int> filteredIndices = new List<int>();
            NativeArray<float>.ReadOnly scoresArray = scores.AsReadOnlyNativeArray();
            for (int i = 0; i < scoresArray.Length; i++)
            {
                if (scoresArray[i] >= scoreThreshold)
                {
                    filteredIndices.Add(i);
                }
            }

            if (filteredIndices.Count == 0)
            {
                return;
            }

            // Sort filtered indices by scores in descending order
            filteredIndices.Sort((a, b) => scoresArray[b].CompareTo(scoresArray[a]));

            // Apply NMS algorithm
            bool[] suppressed = new bool[filteredIndices.Count];
            for (int i = 0; i < filteredIndices.Count; i++)
            {
                if (suppressed[i])
                    continue;

                int idx = filteredIndices[i];

                // Add this detection to results
                outDetections.Add((classIDs[idx], GetBox(idx)));

                // Suppress overlapping boxes regardless of class
                for (int j = i + 1; j < filteredIndices.Count; j++)
                {
                    if (suppressed[j])
                        continue;

                    int jdx = filteredIndices[j];

                    float iou = CalculateIoU(GetBox(idx), GetBox(jdx));
                    if (iou > iouThreshold)
                    {
                        suppressed[j] = true;
                    }
                }
            }

            Vector4 GetBox(int i) => new Vector4(boxes[i, 0], boxes[i, 1], boxes[i, 2], boxes[i, 3]);
        }

        internal static float CalculateIoU(Vector4 boxA, Vector4 boxB)
        {
            // Boxes are in format (topLeftX, topLeftY, bottomRightX, bottomRightY)
            // Calculate intersection coordinates
            float x1 = Mathf.Max(boxA.x, boxB.x);
            float y1 = Mathf.Max(boxA.y, boxB.y);
            float x2 = Mathf.Min(boxA.z, boxB.z);
            float y2 = Mathf.Min(boxA.w, boxB.w);

            // Calculate intersection area
            float intersectionWidth = Mathf.Max(0, x2 - x1);
            float intersectionHeight = Mathf.Max(0, y2 - y1);
            float intersectionArea = intersectionWidth * intersectionHeight;

            // Calculate individual box areas
            float boxAArea = (boxA.z - boxA.x) * (boxA.w - boxA.y);
            float boxBArea = (boxB.z - boxB.x) * (boxB.w - boxB.y);

            // Calculate union area
            float unionArea = boxAArea + boxBArea - intersectionArea;

            // Return IoU (Intersection over Union)
            if (unionArea == 0)
                return 0;

            return intersectionArea / unionArea;
        }

        // ============================================================================
        // SERVER INFERENCE - JSON Response Classes
        // ============================================================================

        [System.Serializable]
        private class ServerResponse
        {
            public DetectionResultData detections;
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

        // ============================================================================
        // SERVER INFERENCE - Connection Test
        // ============================================================================

        private IEnumerator TestServerConnection()
        {
            string testUrl = "http://192.168.0.135:8001/";
            Debug.Log($"[SERVER TEST] Connecting to {testUrl}");

            using (UnityWebRequest req = UnityWebRequest.Get(testUrl))
            {
                req.timeout = 5;
                yield return req.SendWebRequest();

                if (req.result == UnityWebRequest.Result.Success)
                {
                    Debug.Log($"[SERVER TEST] ✓ Connection OK! Response: {req.downloadHandler.text}");
                }
                else
                {
                    Debug.LogError($"[SERVER TEST] ✗ Connection FAILED: {req.error}");
                    Debug.LogError($"[SERVER TEST] Result: {req.result}");
                    Debug.LogError($"[SERVER TEST] Response Code: {req.responseCode}");
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
                    Debug.LogError("Unsupported texture type for server inference");
                    yield break;
                }
            }

            // 2. Encode texture as JPEG
            byte[] jpegBytes = tex2D.EncodeToJPG(90);
            int uploadBytes = jpegBytes.Length;
            Debug.Log($"[SERVER] Encoded JPEG: {uploadBytes} bytes ({tex2D.width}x{tex2D.height})");

            // 3. Create multipart form POST
            List<IMultipartFormSection> formData = new List<IMultipartFormSection>();
            formData.Add(new MultipartFormFileSection("image", jpegBytes, "frame.jpg", "image/jpeg"));

            UnityWebRequest request = UnityWebRequest.Post(m_serverUrl, formData);

            // Add HTTP headers (including timing data from previous frame)
            request.SetRequestHeader("X-Scene-Name", "MultiObjectDetection");
            request.SetRequestHeader("X-Frame-Id", m_frameId.ToString());

            // Send timing data from PREVIOUS frame (frame N-1) for Excel logging
            // These values are 0 for the first frame, which is expected
            request.SetRequestHeader("X-E2E-Ms", m_lastE2eMs.ToString("F1"));
            request.SetRequestHeader("X-Upload-Ms", m_lastUploadMs.ToString("F1"));
            request.SetRequestHeader("X-Download-Ms", m_lastDownloadMs.ToString("F1"));
            request.SetRequestHeader("X-Parse-Ms", m_lastParseMs.ToString("F1"));
            request.SetRequestHeader("X-Upload-Bytes", m_lastUploadBytes.ToString());
            request.SetRequestHeader("X-Download-Bytes", m_lastDownloadBytes.ToString());

            Debug.Log($"[SERVER SEND] >>> Sending frame {m_frameId} to: {m_serverUrl}");

            // 4. Send request and measure UPLOAD time
            float uploadStartTime = Time.realtimeSinceStartup;

            Debug.Log($"[LATENCY] requestStart={e2eStartTime:F4}, uploadStart={uploadStartTime:F4}");

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

            Debug.Log($"[SERVER SEND] <<< Request completed. Result: {request.result}");

            if (request.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError($"[SERVER] Inference failed: {request.error}");
                Debug.LogError($"[SERVER] Result type: {request.result}");
                Debug.LogError($"[SERVER] Response code: {request.responseCode}");
                Debug.LogError($"[SERVER] URL was: {m_serverUrl}");
                yield break;
            }

            // 5. Parse JSON response and measure PARSE time
            float parseStartTime = Time.realtimeSinceStartup;

            string jsonResponse = request.downloadHandler.text;
            Debug.Log($"[SERVER] Response: {jsonResponse.Substring(0, Mathf.Min(200, jsonResponse.Length))}...");

            ServerResponse response = JsonUtility.FromJson<ServerResponse>(jsonResponse);

            if (response == null)
            {
                Debug.LogError("[SERVER] Failed to parse JSON response");
                yield break;
            }

            // Parse time measurement complete
            float parseMs = (Time.realtimeSinceStartup - parseStartTime) * 1000f;

            // Calculate E2E time and derive download time
            float e2eMs = (Time.realtimeSinceStartup - e2eStartTime) * 1000f;
            int downloadBytes = (int)request.downloadedBytes;
            float serverProcMs = response.processing_time_ms;
            float downloadMs = Mathf.Max(0f, e2eMs - uploadMs - serverProcMs - parseMs);

            Debug.Log($"[LATENCY] responseReceived={Time.realtimeSinceStartup:F4}");
            Debug.Log($"[LATENCY] e2eMs={e2eMs:F1}ms");
            Debug.Log($"[LATENCY] parseMs={parseMs:F1}ms");
            Debug.Log($"[LATENCY] serverProcMs={serverProcMs:F1}ms");
            Debug.Log($"[LATENCY] uploadMs={uploadMs:F1}ms");
            Debug.Log($"[LATENCY] downloadMs={downloadMs:F1}ms");

            Debug.Log($"[TIMING] E2E={e2eMs:F0}ms (upload={uploadMs:F0}ms server={serverProcMs:F0}ms download={downloadMs:F0}ms parse={parseMs:F0}ms)");
            Debug.Log($"[BYTES] Upload={uploadBytes} Download={downloadBytes}");

            // 6. Convert server detections to Unity format
            m_detections.Clear();

            if (response.detections != null && response.detections.detections != null)
            {
                // Calculate scale factors to convert from camera resolution to model input resolution
                float scaleX = response.model_input_width / (float)response.input_image_width;
                float scaleY = response.model_input_height / (float)response.input_image_height;

                Debug.Log($"[SERVER] Scale: {scaleX}x{scaleY} (model:{response.model_input_width}x{response.model_input_height}, image:{response.input_image_width}x{response.input_image_height})");

                foreach (var det in response.detections.detections)
                {
                    // Convert bbox from image space (1280x960) to model input space (640x640)
                    Vector4 bboxUnity = new Vector4(
                        det.bbox_pixels[0] * scaleX,  // x1
                        det.bbox_pixels[1] * scaleY,  // y1
                        det.bbox_pixels[2] * scaleX,  // x2
                        det.bbox_pixels[3] * scaleY   // y2
                    );

                    m_detections.Add((det.class_id, bboxUnity));

                    Debug.Log($"[SERVER] Detection: class={det.class_id} ({det.class_name}), conf={det.confidence:F2}, bbox={bboxUnity}");
                }
            }

            Debug.Log($"[SERVER] Converted {m_detections.Count} detections, processing time: {response.processing_time_ms:F1}ms");

            // 7. Update metrics label with inference statistics
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

            // Get detection count
            int detectionCount = response.detections?.detections?.Length ?? 0;

            Debug.Log($"[LATENCY] UpdateMetrics: e2e={e2eMs:F1}ms, upload={uploadMs:F1}ms, server={serverProcMs:F1}ms, download={downloadMs:F1}ms, parse={parseMs:F1}ms");

            // Update metrics in the main info panel (grey bottom panel)
            if (m_uiMenuManager != null)
            {
                m_uiMenuManager.UpdateMetrics(
                    e2eMs,
                    uploadMs,
                    serverProcMs,
                    downloadMs,
                    parseMs,
                    uploadBytes,
                    downloadBytes,
                    avgConfidence
                );
            }

            // Update real-time HUD overlay
            if (m_inferenceHUD != null)
            {
                m_inferenceHUD.UpdateHUD(
                    e2eMs,
                    uploadMs,
                    serverProcMs,
                    downloadMs,
                    parseMs,
                    uploadBytes,
                    downloadBytes,
                    detectionCount,
                    avgConfidence
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
    }
}
