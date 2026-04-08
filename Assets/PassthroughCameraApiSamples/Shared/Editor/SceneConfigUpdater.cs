// Copyright (c) Meta Platforms, Inc. and affiliates.

using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
using PassthroughCameraSamples.Shared;
using PassthroughCameraSamples.MultiObjectDetection;
using PassthroughCameraSamples.PoseEstimation;

namespace PassthroughCameraSamples.Shared.Editor
{
    /// <summary>
    /// Tool to update all scenes to use the new InferenceConfig system.
    /// Menu: Tools > PassthroughCameraApiSamples > Update All Scene Configs
    /// </summary>
    public static class SceneConfigUpdater
    {
        private const string MULTI_OBJECT_SCENE = "Assets/PassthroughCameraApiSamples/MultiObjectDetection/MultiObjectDetection.unity";
        private const string POSE_SCENE = "Assets/PassthroughCameraApiSamples/PoseEstimation/PassthroughPoseEstimation.unity";
        private const string DEPTH_SCENE = "Assets/PassthroughCameraApiSamples/DepthEstimation/DepthEstimation.unity";

        private const string CORRECT_IP = "192.168.0.135";
        private const string CORRECT_BASE_URL = "http://192.168.0.135:8001/infer_human";

        [MenuItem("Tools/PassthroughCameraApiSamples/Update All Scene Configs")]
        public static void UpdateAllSceneConfigs()
        {
            bool proceed = EditorUtility.DisplayDialog(
                "Update Scene Configurations",
                "This will update all three scenes to use:\n\n" +
                "• Correct IP: " + CORRECT_IP + "\n" +
                "• New InferenceConfig system\n" +
                "• Proper mode settings\n\n" +
                "Scenes to update:\n" +
                "1. MultiObjectDetection (mode=ObjectDetection, 10 FPS)\n" +
                "2. PassthroughPoseEstimation (mode=PoseEstimation, 5 FPS)\n" +
                "3. DepthEstimation (verify only)\n\n" +
                "Continue?",
                "Update All",
                "Cancel"
            );

            if (!proceed)
            {
                Debug.Log("[Scene Config Updater] Cancelled by user.");
                return;
            }

            // Save current scene
            string currentScenePath = EditorSceneManager.GetActiveScene().path;
            bool currentSceneDirty = EditorSceneManager.GetActiveScene().isDirty;

            if (currentSceneDirty)
            {
                bool save = EditorUtility.DisplayDialog(
                    "Save Current Scene?",
                    "Current scene has unsaved changes. Save before proceeding?",
                    "Save",
                    "Don't Save"
                );

                if (save)
                {
                    EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene());
                }
            }

            int updatedCount = 0;
            int errorCount = 0;

            // Update MultiObjectDetection scene
            if (UpdateMultiObjectDetectionScene())
                updatedCount++;
            else
                errorCount++;

            // Update PoseEstimation scene
            if (UpdatePoseEstimationScene())
                updatedCount++;
            else
                errorCount++;

            // Verify DepthEstimation scene
            if (VerifyDepthEstimationScene())
                updatedCount++;
            else
                errorCount++;

            // Restore original scene
            if (!string.IsNullOrEmpty(currentScenePath))
            {
                EditorSceneManager.OpenScene(currentScenePath);
            }

            // Show results
            string message = $"Scene Update Complete!\n\n" +
                           $"✅ Updated: {updatedCount} scenes\n" +
                           $"❌ Errors: {errorCount} scenes\n\n" +
                           $"Check Console for details.";

            EditorUtility.DisplayDialog("Update Complete", message, "OK");
        }

        private static bool UpdateMultiObjectDetectionScene()
        {
            Debug.Log("[Scene Config Updater] ===== Updating MultiObjectDetection Scene =====");

            try
            {
                // Open scene
                Scene scene = EditorSceneManager.OpenScene(MULTI_OBJECT_SCENE, OpenSceneMode.Single);
                Debug.Log($"[Scene Config Updater] Opened scene: {scene.name}");

                // Find all SentisInferenceRunManager components
                var managers = GameObject.FindObjectsOfType<SentisInferenceRunManager>();
                Debug.Log($"[Scene Config Updater] Found {managers.Length} SentisInferenceRunManager(s)");

                foreach (var manager in managers)
                {
                    SerializedObject so = new SerializedObject(manager);

                    // Update InferenceConfig
                    SerializedProperty configProp = so.FindProperty("m_inferenceConfig");
                    if (configProp != null)
                    {
                        SerializedProperty baseUrlProp = configProp.FindPropertyRelative("baseUrl");
                        SerializedProperty modeProp = configProp.FindPropertyRelative("mode");
                        SerializedProperty targetFPSProp = configProp.FindPropertyRelative("targetFPS");
                        SerializedProperty jpegQualityProp = configProp.FindPropertyRelative("jpegQuality");
                        SerializedProperty includeMaskProp = configProp.FindPropertyRelative("includeMask");
                        SerializedProperty includeDepthProp = configProp.FindPropertyRelative("includeDepth");

                        if (baseUrlProp != null) baseUrlProp.stringValue = CORRECT_BASE_URL;
                        if (modeProp != null) modeProp.enumValueIndex = 0; // ObjectDetection
                        if (targetFPSProp != null) targetFPSProp.floatValue = 10f;
                        if (jpegQualityProp != null) jpegQualityProp.intValue = 80;
                        if (includeMaskProp != null) includeMaskProp.boolValue = false;
                        if (includeDepthProp != null) includeDepthProp.boolValue = false;

                        Debug.Log($"[Scene Config Updater] ✅ Updated InferenceConfig for {manager.gameObject.name}");
                        Debug.Log($"  - baseUrl: {CORRECT_BASE_URL}");
                        Debug.Log($"  - mode: ObjectDetection (0)");
                        Debug.Log($"  - targetFPS: 10");
                    }

                    // Clear old m_serverUrl (if it exists)
                    SerializedProperty serverUrlProp = so.FindProperty("m_serverUrl");
                    if (serverUrlProp != null)
                    {
                        string oldUrl = serverUrlProp.stringValue;
                        serverUrlProp.stringValue = "";
                        Debug.Log($"[Scene Config Updater] Cleared old m_serverUrl: {oldUrl}");
                    }

                    so.ApplyModifiedProperties();
                }

                // Save scene
                EditorSceneManager.SaveScene(scene);
                Debug.Log($"[Scene Config Updater] ✅ MultiObjectDetection scene saved successfully!");
                return true;
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[Scene Config Updater] ❌ Error updating MultiObjectDetection scene: {e.Message}");
                return false;
            }
        }

        private static bool UpdatePoseEstimationScene()
        {
            Debug.Log("[Scene Config Updater] ===== Updating PoseEstimation Scene =====");

            try
            {
                // Open scene
                Scene scene = EditorSceneManager.OpenScene(POSE_SCENE, OpenSceneMode.Single);
                Debug.Log($"[Scene Config Updater] Opened scene: {scene.name}");

                // Find all PoseInferenceRunManager components
                var managers = GameObject.FindObjectsOfType<PoseInferenceRunManager>();
                Debug.Log($"[Scene Config Updater] Found {managers.Length} PoseInferenceRunManager(s)");

                foreach (var manager in managers)
                {
                    SerializedObject so = new SerializedObject(manager);

                    // Update InferenceConfig
                    SerializedProperty configProp = so.FindProperty("m_inferenceConfig");
                    if (configProp != null)
                    {
                        SerializedProperty baseUrlProp = configProp.FindPropertyRelative("baseUrl");
                        SerializedProperty modeProp = configProp.FindPropertyRelative("mode");
                        SerializedProperty targetFPSProp = configProp.FindPropertyRelative("targetFPS");
                        SerializedProperty jpegQualityProp = configProp.FindPropertyRelative("jpegQuality");
                        SerializedProperty includeMaskProp = configProp.FindPropertyRelative("includeMask");
                        SerializedProperty includeDepthProp = configProp.FindPropertyRelative("includeDepth");

                        if (baseUrlProp != null) baseUrlProp.stringValue = CORRECT_BASE_URL;
                        if (modeProp != null) modeProp.enumValueIndex = 2; // Both (pose estimation)
                        if (targetFPSProp != null) targetFPSProp.floatValue = 5f;
                        if (jpegQualityProp != null) jpegQualityProp.intValue = 80;
                        if (includeMaskProp != null) includeMaskProp.boolValue = false;
                        if (includeDepthProp != null) includeDepthProp.boolValue = false;

                        Debug.Log($"[Scene Config Updater] ✅ Updated InferenceConfig for {manager.gameObject.name}");
                        Debug.Log($"  - baseUrl: {CORRECT_BASE_URL}");
                        Debug.Log($"  - mode: Both/PoseEstimation (2)");
                        Debug.Log($"  - targetFPS: 5");
                    }

                    // Clear old m_serverUrl (if it exists)
                    SerializedProperty serverUrlProp = so.FindProperty("m_serverUrl");
                    if (serverUrlProp != null)
                    {
                        string oldUrl = serverUrlProp.stringValue;
                        serverUrlProp.stringValue = "";
                        Debug.Log($"[Scene Config Updater] Cleared old m_serverUrl: {oldUrl}");
                    }

                    so.ApplyModifiedProperties();
                }

                // Save scene
                EditorSceneManager.SaveScene(scene);
                Debug.Log($"[Scene Config Updater] ✅ PoseEstimation scene saved successfully!");
                return true;
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[Scene Config Updater] ❌ Error updating PoseEstimation scene: {e.Message}");
                return false;
            }
        }

        private static bool VerifyDepthEstimationScene()
        {
            Debug.Log("[Scene Config Updater] ===== Verifying DepthEstimation Scene =====");

            try
            {
                // Open scene
                Scene scene = EditorSceneManager.OpenScene(DEPTH_SCENE, OpenSceneMode.Single);
                Debug.Log($"[Scene Config Updater] Opened scene: {scene.name}");

                // Find DepthInferenceRunManager
                var managers = GameObject.FindObjectsOfType<PassthroughCameraSamples.DepthEstimation.DepthInferenceRunManager>();
                Debug.Log($"[Scene Config Updater] Found {managers.Length} DepthInferenceRunManager(s)");

                if (managers.Length == 0)
                {
                    Debug.LogWarning("[Scene Config Updater] ⚠️ No DepthInferenceRunManager found!");
                    return false;
                }

                foreach (var manager in managers)
                {
                    SerializedObject so = new SerializedObject(manager);
                    SerializedProperty configProp = so.FindProperty("m_inferenceConfig");

                    if (configProp != null)
                    {
                        string baseUrl = configProp.FindPropertyRelative("baseUrl")?.stringValue ?? "";
                        int mode = configProp.FindPropertyRelative("mode")?.enumValueIndex ?? -1;
                        float targetFPS = configProp.FindPropertyRelative("targetFPS")?.floatValue ?? 0f;

                        Debug.Log($"[Scene Config Updater] Current config for {manager.gameObject.name}:");
                        Debug.Log($"  - baseUrl: {baseUrl} {(baseUrl == CORRECT_BASE_URL ? "✅" : "❌")}");
                        Debug.Log($"  - mode: {mode} (should be 3 for DepthEstimation) {(mode == 3 ? "✅" : "❌")}");
                        Debug.Log($"  - targetFPS: {targetFPS} {(targetFPS == 5f ? "✅" : "⚠️")}");

                        if (baseUrl == CORRECT_BASE_URL && mode == 3)
                        {
                            Debug.Log("[Scene Config Updater] ✅ DepthEstimation scene config is correct!");
                        }
                        else
                        {
                            Debug.LogWarning("[Scene Config Updater] ⚠️ DepthEstimation scene config needs manual review!");
                        }
                    }
                }

                return true;
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[Scene Config Updater] ❌ Error verifying DepthEstimation scene: {e.Message}");
                return false;
            }
        }

        [MenuItem("Tools/PassthroughCameraApiSamples/Verify All Scene Configs")]
        public static void VerifyAllSceneConfigs()
        {
            Debug.Log("========== Scene Configuration Verification ==========");

            string report = "Scene Configuration Report:\n\n";

            // Check each scene
            report += CheckSceneFile(MULTI_OBJECT_SCENE, "MultiObjectDetection", "ObjectDetection", 10f);
            report += CheckSceneFile(POSE_SCENE, "PoseEstimation", "Both", 5f);
            report += CheckSceneFile(DEPTH_SCENE, "DepthEstimation", "DepthEstimation", 5f);

            Debug.Log(report);
            EditorUtility.DisplayDialog("Scene Verification", report, "OK");
        }

        private static string CheckSceneFile(string scenePath, string sceneName, string expectedMode, float expectedFPS)
        {
            if (!System.IO.File.Exists(scenePath))
            {
                return $"❌ {sceneName}: Scene file not found!\n";
            }

            string content = System.IO.File.ReadAllText(scenePath);

            bool hasCorrectIP = content.Contains(CORRECT_IP);
            bool hasOldIP = content.Contains("35.9.28.119");

            string result = $"{sceneName}:\n";
            result += $"  IP {CORRECT_IP}: {(hasCorrectIP ? "✅" : "❌")}\n";

            if (hasOldIP)
            {
                result += $"  ⚠️ WARNING: Still contains old IP (35.9.28.119)\n";
            }

            result += $"  Expected: mode={expectedMode}, targetFPS={expectedFPS}\n\n";

            return result;
        }
    }
}
