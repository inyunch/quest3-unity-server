// Copyright (c) Meta Platforms, Inc. and affiliates.

using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;

namespace PassthroughCameraSamples.Segmentation
{
    /// <summary>
    /// ?¨ç???Server ???æ¸¬è©¦å·¥å…·
    /// ?¨ä?è¨ºæ–· Quest 3 ??Python Server ä¹‹é??„ç¶²è·¯é€??
    /// </summary>
    public class ServerConnectionTester : MonoBehaviour
    {
        [Header("Server Configuration")]
        [SerializeField] private string m_serverIp = "192.168.0.135";
        [SerializeField] private int m_serverPort = 8000;

        [Header("Test Settings")]
        [SerializeField] private bool m_testOnStart = true;
        [SerializeField] private float m_repeatTestInterval = 0f; // 0 = no repeat, >0 = repeat every N seconds

        [Header("UI Display (Optional)")]
        [SerializeField] private TMPro.TextMeshPro m_statusText;

        private string ServerRootUrl => $"http://{m_serverIp}:{m_serverPort}";
        private string ServerHealthUrl => $"http://{m_serverIp}:{m_serverPort}/health";
        private string ServerInferUrl => $"http://{m_serverIp}:{m_serverPort}/infer_human?mode=detection";

        private void Start()
        {
            Debug.Log("=".PadRight(80, '='));
            Debug.Log("[CONNECTION TESTER] Server Connection Tester Started!");
            Debug.Log($"[CONNECTION TESTER] Server IP: {m_serverIp}:{m_serverPort}");
            Debug.Log("[CONNECTION TESTER] Call RunTest() to manually trigger test");
            Debug.Log("=".PadRight(80, '='));

            if (m_testOnStart)
            {
                StartCoroutine(RunAllTests());
            }

            // If repeat interval is set, start repeating tests
            if (m_repeatTestInterval > 0f)
            {
                InvokeRepeating(nameof(RunTest), m_repeatTestInterval, m_repeatTestInterval);
            }
        }

        /// <summary>
        /// Public method to manually trigger a test (can be called from Inspector or other scripts)
        /// </summary>
        public void RunTest()
        {
            Debug.Log("[CONNECTION TESTER] Manual test triggered");
            StartCoroutine(RunAllTests());
        }

        public IEnumerator RunAllTests()
        {
            Debug.Log("\n" + "=".PadRight(80, '='));
            Debug.Log("[TEST START] Running connection tests...");
            Debug.Log("=".PadRight(80, '='));

            UpdateStatus("Testing server connection...");

            // Test 1: Basic GET to root
            yield return Test1_GetRoot();
            yield return new WaitForSeconds(1f);

            // Test 2: Health check endpoint
            yield return Test2_HealthCheck();
            yield return new WaitForSeconds(1f);

            // Test 3: POST fake image to inference endpoint
            yield return Test3_PostFakeImage();

            Debug.Log("=".PadRight(80, '='));
            Debug.Log("[TEST END] All tests completed!");
            Debug.Log("=".PadRight(80, '=') + "\n");

            UpdateStatus("Tests completed! Check logs.");
        }

        private IEnumerator Test1_GetRoot()
        {
            Debug.Log($"\n[TEST 1] GET {ServerRootUrl}");
            Debug.Log("[TEST 1] This tests basic HTTP connectivity...");

            using (UnityWebRequest request = UnityWebRequest.Get(ServerRootUrl))
            {
                request.timeout = 10;

                float startTime = Time.realtimeSinceStartup;
                yield return request.SendWebRequest();
                float elapsedTime = Time.realtimeSinceStartup - startTime;

                LogRequestResult(request, elapsedTime, "TEST 1");
            }
        }

        private IEnumerator Test2_HealthCheck()
        {
            Debug.Log($"\n[TEST 2] GET {ServerHealthUrl}");
            Debug.Log("[TEST 2] This tests the /health endpoint...");

            using (UnityWebRequest request = UnityWebRequest.Get(ServerHealthUrl))
            {
                request.timeout = 10;

                float startTime = Time.realtimeSinceStartup;
                yield return request.SendWebRequest();
                float elapsedTime = Time.realtimeSinceStartup - startTime;

                LogRequestResult(request, elapsedTime, "TEST 2");
            }
        }

        private IEnumerator Test3_PostFakeImage()
        {
            Debug.Log($"\n[TEST 3] POST {ServerInferUrl}");
            Debug.Log("[TEST 3] This tests the /infer_human endpoint with a fake image...");

            // Create a fake 64x64 red image
            Texture2D fakeImage = new Texture2D(64, 64, TextureFormat.RGB24, false);
            Color[] pixels = new Color[64 * 64];
            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i] = Color.red;
            }
            fakeImage.SetPixels(pixels);
            fakeImage.Apply();

            // Encode as JPEG
            byte[] jpegBytes = fakeImage.EncodeToJPG(90);
            Debug.Log($"[TEST 3] Created fake image: 64x64, {jpegBytes.Length} bytes");

            // Create multipart form
            List<IMultipartFormSection> formData = new List<IMultipartFormSection>();
            formData.Add(new MultipartFormFileSection("image", jpegBytes, "test.jpg", "image/jpeg"));

            using (UnityWebRequest request = UnityWebRequest.Post(ServerInferUrl, formData))
            {
                request.timeout = 30;

                float startTime = Time.realtimeSinceStartup;
                yield return request.SendWebRequest();
                float elapsedTime = Time.realtimeSinceStartup - startTime;

                LogRequestResult(request, elapsedTime, "TEST 3");
            }

            Destroy(fakeImage);
        }

        private void LogRequestResult(UnityWebRequest request, float elapsedTime, string testName)
        {
            Debug.Log($"[{testName}] ----------------------------------------");
            Debug.Log($"[{testName}] Time elapsed: {elapsedTime:F3}s");
            Debug.Log($"[{testName}] Result: {request.result}");
            Debug.Log($"[{testName}] Response Code: {request.responseCode}");

            if (request.result == UnityWebRequest.Result.Success)
            {
                Debug.Log($"[{testName}] ??SUCCESS!");
                Debug.Log($"[{testName}] Response length: {request.downloadHandler.text.Length} chars");

                // Log first 500 chars of response
                string response = request.downloadHandler.text;
                int previewLength = Mathf.Min(500, response.Length);
                Debug.Log($"[{testName}] Response preview:\n{response.Substring(0, previewLength)}");

                if (response.Length > 500)
                {
                    Debug.Log($"[{testName}] ... (truncated, total {response.Length} chars)");
                }

                UpdateStatus($"{testName} SUCCESS!");
            }
            else
            {
                Debug.LogError($"[{testName}] ??FAILED!");
                Debug.LogError($"[{testName}] Error: {request.error}");
                Debug.LogError($"[{testName}] Result type: {request.result}");

                // Additional diagnostics
                if (request.result == UnityWebRequest.Result.ConnectionError)
                {
                    Debug.LogError($"[{testName}] CONNECTION ERROR - Check:");
                    Debug.LogError($"[{testName}]   1. Quest 3 and server on same WiFi?");
                    Debug.LogError($"[{testName}]   2. Server IP correct? ({m_serverIp})");
                    Debug.LogError($"[{testName}]   3. Server running on port {m_serverPort}?");
                    Debug.LogError($"[{testName}]   4. Firewall blocking?");
                }
                else if (request.result == UnityWebRequest.Result.ProtocolError)
                {
                    Debug.LogError($"[{testName}] PROTOCOL ERROR - Server returned error");
                    Debug.LogError($"[{testName}] Response: {request.downloadHandler.text}");
                }

                UpdateStatus($"{testName} FAILED: {request.error}");
            }

            Debug.Log($"[{testName}] ----------------------------------------\n");
        }

        private void UpdateStatus(string message)
        {
            if (m_statusText != null)
            {
                m_statusText.text = $"[Server Test]\n{message}";
            }
        }
    }
}
