// Copyright (c) Meta Platforms, Inc. and affiliates.

#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;
using System.IO;

namespace PassthroughCameraSamples.Shared.Editor
{
    /// <summary>
    /// Editor window for managing server configuration across all scenes.
    ///
    /// Access via: Tools > Passthrough Camera > Server Config Editor
    /// </summary>
    public class ServerConfigEditor : EditorWindow
    {
        private string m_serverIP = "192.168.0.135";
        private int m_serverPort = 8001;
        private float m_timeout = 10.0f;

        private ServerConfig m_config;
        private Vector2 m_scrollPosition;

        [MenuItem("Tools/Passthrough Camera/Server Config Editor")]
        public static void ShowWindow()
        {
            var window = GetWindow<ServerConfigEditor>("Server Config");
            window.minSize = new Vector2(500, 400);
            window.Show();
            window.Focus();
        }

        private void OnEnable()
        {
            LoadConfig();
        }

        private void LoadConfig()
        {
            m_config = ServerConfig.Instance;

            if (m_config != null)
            {
                m_serverIP = m_config.ServerIP;
                m_serverPort = m_config.ServerPort;
                m_timeout = m_config.RequestTimeoutSeconds;
            }
        }

        private void OnGUI()
        {
            m_scrollPosition = EditorGUILayout.BeginScrollView(m_scrollPosition);

            // Header
            EditorGUILayout.LabelField("Server Configuration Manager", EditorStyles.boldLabel);
            EditorGUILayout.Space();

            // Current Config Status
            DrawCurrentConfigSection();
            EditorGUILayout.Space();

            // Edit Section
            DrawEditSection();
            EditorGUILayout.Space();

            // Quick Presets
            DrawPresetsSection();
            EditorGUILayout.Space();

            // Update Scenes Section
            DrawUpdateScenesSection();

            EditorGUILayout.EndScrollView();
        }

        private void DrawCurrentConfigSection()
        {
            EditorGUILayout.LabelField("Current Configuration", EditorStyles.boldLabel);

            if (m_config == null)
            {
                EditorGUILayout.HelpBox(
                    "No ServerConfig asset found!\n\n" +
                    "Click 'Create ServerConfig Asset' below to create one.",
                    MessageType.Warning);

                if (GUILayout.Button("Create ServerConfig Asset", GUILayout.Height(30)))
                {
                    CreateServerConfigAsset();
                }
                return;
            }

            EditorGUI.BeginDisabledGroup(true);
            EditorGUILayout.TextField("Base URL", m_config.BaseUrl);
            EditorGUILayout.TextField("Inference URL", m_config.InferenceUrl);
            EditorGUILayout.TextField("Segmentation URL", m_config.SegmentationUrl);
            EditorGUILayout.TextField("ROI Depth URL", m_config.RoiDepthUrl);
            EditorGUI.EndDisabledGroup();

            // Validation warnings
            var warnings = m_config.Validate();
            if (warnings.Length > 0)
            {
                EditorGUILayout.HelpBox(string.Join("\n", warnings), MessageType.Warning);
            }
        }

        private void DrawEditSection()
        {
            EditorGUILayout.LabelField("Edit Server Settings", EditorStyles.boldLabel);

            // Server IP
            m_serverIP = EditorGUILayout.TextField("Server IP", m_serverIP);

            // Server Port
            m_serverPort = EditorGUILayout.IntField("Server Port", m_serverPort);

            // Timeout
            m_timeout = EditorGUILayout.Slider("Request Timeout (s)", m_timeout, 5f, 60f);

            EditorGUILayout.Space();

            // Save button
            if (GUILayout.Button("Save Configuration", GUILayout.Height(30)))
            {
                SaveConfig();
            }

            // Test connection button
            if (GUILayout.Button("Test Server Connection", GUILayout.Height(30)))
            {
                TestServerConnection();
            }
        }

        private void DrawPresetsSection()
        {
            EditorGUILayout.LabelField("Quick Presets", EditorStyles.boldLabel);

            EditorGUILayout.BeginHorizontal();

            if (GUILayout.Button("Local (localhost)"))
            {
                m_serverIP = "127.0.0.1";
                m_serverPort = 8001;
            }

            if (GUILayout.Button("Default (192.168.0.135)"))
            {
                m_serverIP = "192.168.0.135";
                m_serverPort = 8001;
            }

            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();

            if (GUILayout.Button("Port 8000"))
            {
                m_serverPort = 8000;
            }

            if (GUILayout.Button("Port 8001"))
            {
                m_serverPort = 8001;
            }

            EditorGUILayout.EndHorizontal();
        }

        private void DrawUpdateScenesSection()
        {
            EditorGUILayout.LabelField("Update Scenes", EditorStyles.boldLabel);

            EditorGUILayout.HelpBox(
                "Note: ServerConfig is automatically used by new code.\n\n" +
                "Legacy scenes may still have hardcoded URLs. Use the buttons below to find and update them.",
                MessageType.Info);

            if (GUILayout.Button("Find Scenes with Old IP Configuration", GUILayout.Height(30)))
            {
                FindScenesWithOldConfig();
            }

            EditorGUILayout.Space();

            EditorGUILayout.LabelField("Preview URLs", EditorStyles.boldLabel);
            EditorGUI.BeginDisabledGroup(true);
            EditorGUILayout.TextField("Base", $"http://{m_serverIP}:{m_serverPort}");
            EditorGUILayout.TextField("Inference", $"http://{m_serverIP}:{m_serverPort}/infer_human");
            EditorGUILayout.TextField("Segmentation", $"http://{m_serverIP}:{m_serverPort}/segmentation");
            EditorGUI.EndDisabledGroup();
        }

        private void SaveConfig()
        {
            if (m_config == null)
            {
                EditorUtility.DisplayDialog("Error", "No ServerConfig asset found. Create one first.", "OK");
                return;
            }

            Undo.RecordObject(m_config, "Update Server Config");

            // Use SerializedObject to properly update the asset
            var serializedConfig = new SerializedObject(m_config);
            serializedConfig.FindProperty("m_serverIP").stringValue = m_serverIP;
            serializedConfig.FindProperty("m_serverPort").intValue = m_serverPort;
            serializedConfig.FindProperty("m_requestTimeoutSeconds").floatValue = m_timeout;
            serializedConfig.ApplyModifiedProperties();

            EditorUtility.SetDirty(m_config);
            AssetDatabase.SaveAssets();

            Debug.Log($"[ServerConfig] Configuration saved: {m_config.BaseUrl}");
            EditorUtility.DisplayDialog("Success", $"Server configuration updated!\n\nNew URL: {m_config.BaseUrl}", "OK");

            LoadConfig(); // Reload to reflect changes
        }

        private void CreateServerConfigAsset()
        {
            string resourcesPath = "Assets/PassthroughCameraApiSamples/Shared/Resources";

            // Create Resources folder if it doesn't exist
            if (!Directory.Exists(resourcesPath))
            {
                Directory.CreateDirectory(resourcesPath);
                AssetDatabase.Refresh();
            }

            string assetPath = $"{resourcesPath}/ServerConfig.asset";

            // Check if already exists
            if (File.Exists(assetPath))
            {
                EditorUtility.DisplayDialog("Already Exists", $"ServerConfig asset already exists at:\n{assetPath}", "OK");
                m_config = AssetDatabase.LoadAssetAtPath<ServerConfig>(assetPath);
                LoadConfig();
                return;
            }

            // Create new asset
            var newConfig = CreateInstance<ServerConfig>();

            AssetDatabase.CreateAsset(newConfig, assetPath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            m_config = newConfig;
            LoadConfig();

            Debug.Log($"[ServerConfig] Created new ServerConfig asset at: {assetPath}");
            EditorUtility.DisplayDialog("Success", $"ServerConfig asset created at:\n{assetPath}\n\nYou can now edit the configuration above.", "OK");

            // Select the new asset
            Selection.activeObject = newConfig;
            EditorGUIUtility.PingObject(newConfig);
        }

        private void TestServerConnection()
        {
            string testUrl = $"http://{m_serverIP}:{m_serverPort}/";
            Debug.Log($"[ServerConfig] Testing connection to {testUrl}");

            EditorUtility.DisplayDialog(
                "Test Connection",
                $"Testing connection to:\n{testUrl}\n\n" +
                "Check the Console for results.\n\n" +
                "Note: This is a simple test. For full testing, build and run on Quest.",
                "OK");

            // Note: Can't do actual HTTP request in Editor without async
            // User should use curl or build to Quest for real testing
            Debug.Log($"[ServerConfig] To test connection, run:\ncurl {testUrl}");
        }

        private void FindScenesWithOldConfig()
        {
            Debug.Log("[ServerConfig] Searching for scenes with hardcoded IP addresses...");

            var sceneGuids = AssetDatabase.FindAssets("t:Scene", new[] { "Assets/PassthroughCameraApiSamples" });

            int foundCount = 0;

            foreach (var guid in sceneGuids)
            {
                var scenePath = AssetDatabase.GUIDToAssetPath(guid);
                var sceneContent = File.ReadAllText(scenePath);

                // Search for common IP patterns
                if (sceneContent.Contains("192.168.0.135") ||
                    sceneContent.Contains("127.0.0.1") ||
                    sceneContent.Contains("localhost:8001") ||
                    sceneContent.Contains("localhost:8000"))
                {
                    Debug.LogWarning($"[ServerConfig] Found hardcoded IP in scene: {scenePath}");
                    foundCount++;
                }
            }

            if (foundCount == 0)
            {
                EditorUtility.DisplayDialog(
                    "Search Complete",
                    "No scenes with hardcoded IPs found.\n\nAll scenes are using ServerConfig or no server configuration.",
                    "OK");
            }
            else
            {
                EditorUtility.DisplayDialog(
                    "Search Complete",
                    $"Found {foundCount} scene(s) with hardcoded IPs.\n\nCheck the Console for details.\n\n" +
                    "Recommendation: Update these scenes to use ServerConfig.Instance instead of hardcoded URLs.",
                    "OK");
            }

            Debug.Log($"[ServerConfig] Search complete. Found {foundCount} scene(s) with hardcoded IPs.");
        }
    }
}
#endif
