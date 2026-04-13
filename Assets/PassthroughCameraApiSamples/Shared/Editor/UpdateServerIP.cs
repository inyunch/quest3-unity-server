using UnityEngine;
using UnityEditor;
using System.Reflection;

namespace Meta.XR.MRUtilityKit.PassthroughCameraApiSamples
{
    public class UpdateServerIP : EditorWindow
    {
        private string newIP = "35.9.28.119";
        private int newPort = 8001;

        [MenuItem("Tools/Update Server IP")]
        public static void ShowWindow()
        {
            GetWindow<UpdateServerIP>("Update Server IP");
        }

        void OnGUI()
        {
            GUILayout.Label("Update Server IP Configuration", EditorStyles.boldLabel);
            EditorGUILayout.Space();

            newIP = EditorGUILayout.TextField("Server IP:", newIP);
            newPort = EditorGUILayout.IntField("Server Port:", newPort);

            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(
                "This will update the ServerConfig ScriptableObject.\n\n" +
                "All three scenes (MultiObjectDetection, PoseEstimation, Segmentation) " +
                "use the same ServerConfig resource.",
                MessageType.Info
            );

            EditorGUILayout.Space();

            if (GUILayout.Button("Update Server IP", GUILayout.Height(40)))
            {
                UpdateServerConfig();
            }
        }

        void UpdateServerConfig()
        {
            try
            {
                // Load ServerConfig asset from Resources
                Object config = Resources.Load("ServerConfig");

                // If not found, create it
                if (config == null)
                {
                    Debug.Log("[UpdateServerIP] ServerConfig not found. Creating new asset...");

                    // Create Resources folder if it doesn't exist
                    string resourcesPath = "Assets/Resources";
                    if (!AssetDatabase.IsValidFolder(resourcesPath))
                    {
                        AssetDatabase.CreateFolder("Assets", "Resources");
                    }

                    // Create new ServerConfig instance
                    var newConfig = ScriptableObject.CreateInstance("ServerConfig");
                    string assetPath = resourcesPath + "/ServerConfig.asset";
                    AssetDatabase.CreateAsset(newConfig, assetPath);
                    AssetDatabase.SaveAssets();
                    AssetDatabase.Refresh();

                    config = newConfig;
                    Debug.Log($"[UpdateServerIP] Created ServerConfig at: {assetPath}");
                }

                // Use reflection to set private fields
                System.Type configType = config.GetType();
                FieldInfo serverIPField = configType.GetField("m_serverIP", BindingFlags.NonPublic | BindingFlags.Instance);
                FieldInfo serverPortField = configType.GetField("m_serverPort", BindingFlags.NonPublic | BindingFlags.Instance);

                if (serverIPField != null && serverPortField != null)
                {
                    serverIPField.SetValue(config, newIP);
                    serverPortField.SetValue(config, newPort);

                    // Mark as dirty and save
                    EditorUtility.SetDirty(config);
                    AssetDatabase.SaveAssets();
                    AssetDatabase.Refresh();

                    Debug.Log($"[UpdateServerIP] Updated ServerConfig: {newIP}:{newPort}");

                    EditorUtility.DisplayDialog(
                        "Success",
                        $"Successfully updated ServerConfig to:\n" +
                        $"Server IP: {newIP}\n" +
                        $"Port: {newPort}",
                        "OK"
                    );
                }
                else
                {
                    EditorUtility.DisplayDialog("Error", "Failed to update fields", "OK");
                }
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[UpdateServerIP] Error: {e.Message}");
                EditorUtility.DisplayDialog("Error", $"Failed: {e.Message}", "OK");
            }
        }
    }
}

