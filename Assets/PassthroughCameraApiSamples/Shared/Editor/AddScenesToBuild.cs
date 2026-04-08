// Copyright (c) Meta Platforms, Inc. and affiliates.

using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.Linq;

namespace PassthroughCameraSamples.Shared.Editor
{
    /// <summary>
    /// Tool to automatically add inference scenes to Build Settings.
    /// Menu: Tools > PassthroughCameraApiSamples > Add Scenes to Build Settings
    /// </summary>
    public static class AddScenesToBuild
    {
        private static readonly string[] REQUIRED_SCENES = new string[]
        {
            "Assets/PassthroughCameraApiSamples/StartScene/StartScene.unity",
            "Assets/PassthroughCameraApiSamples/MultiObjectDetection/MultiObjectDetection.unity",
            "Assets/PassthroughCameraApiSamples/PoseEstimation/PassthroughPoseEstimation.unity",
            "Assets/PassthroughCameraApiSamples/DepthEstimation/DepthEstimation.unity"
        };

        [MenuItem("Tools/PassthroughCameraApiSamples/Add Scenes to Build Settings")]
        public static void AddScenesToBuildSettings()
        {
            Debug.Log("========== Adding Scenes to Build Settings ==========");

            // Get current build scenes
            List<EditorBuildSettingsScene> buildScenes = EditorBuildSettings.scenes.ToList();

            int addedCount = 0;
            int alreadyPresentCount = 0;
            int missingCount = 0;

            foreach (string scenePath in REQUIRED_SCENES)
            {
                // Check if file exists
                if (!System.IO.File.Exists(scenePath))
                {
                    Debug.LogWarning($"[Add Scenes] ⚠️ Scene not found: {scenePath}");
                    missingCount++;
                    continue;
                }

                // Check if already in build settings
                bool alreadyAdded = buildScenes.Any(s => s.path == scenePath);

                if (alreadyAdded)
                {
                    Debug.Log($"[Add Scenes] ✅ Already in build: {System.IO.Path.GetFileNameWithoutExtension(scenePath)}");
                    alreadyPresentCount++;
                }
                else
                {
                    // Add to build settings
                    buildScenes.Add(new EditorBuildSettingsScene(scenePath, true));
                    Debug.Log($"[Add Scenes] ➕ Added to build: {System.IO.Path.GetFileNameWithoutExtension(scenePath)}");
                    addedCount++;
                }
            }

            // Update build settings
            if (addedCount > 0)
            {
                EditorBuildSettings.scenes = buildScenes.ToArray();
                Debug.Log($"[Add Scenes] ✅ Build settings updated!");
            }

            // Show summary
            string message = "Build Settings Updated!\n\n";
            message += $"Added: {addedCount} scenes\n";
            message += $"Already present: {alreadyPresentCount} scenes\n";

            if (missingCount > 0)
            {
                message += $"Missing files: {missingCount} scenes\n\n";
                message += "Some scene files were not found. Check Console for details.";
            }
            else
            {
                message += "\n✅ All required scenes are now in Build Settings!";
            }

            EditorUtility.DisplayDialog("Add Scenes Complete", message, "OK");
            Debug.Log($"[Add Scenes] Summary: Added={addedCount}, Present={alreadyPresentCount}, Missing={missingCount}");
        }

        [MenuItem("Tools/PassthroughCameraApiSamples/List Build Settings Scenes")]
        public static void ListBuildSettingsScenes()
        {
            Debug.Log("========== Build Settings Scenes ==========");

            var scenes = EditorBuildSettings.scenes;

            if (scenes.Length == 0)
            {
                Debug.LogWarning("[Build Settings] No scenes in build settings!");
                EditorUtility.DisplayDialog("Build Settings", "No scenes in Build Settings!", "OK");
                return;
            }

            string report = "Scenes in Build Settings:\n\n";

            for (int i = 0; i < scenes.Length; i++)
            {
                var scene = scenes[i];
                string sceneName = System.IO.Path.GetFileNameWithoutExtension(scene.path);
                string status = scene.enabled ? "✅" : "❌";
                string exists = System.IO.File.Exists(scene.path) ? "" : " [MISSING FILE]";

                report += $"{i}. {status} {sceneName}{exists}\n";
                Debug.Log($"[Build Settings] [{i}] {status} {sceneName} - {scene.path}{exists}");
            }

            EditorUtility.DisplayDialog("Build Settings Scenes", report, "OK");
        }

        [MenuItem("Tools/PassthroughCameraApiSamples/Refresh and Find DepthEstimation Scene")]
        public static void RefreshAndFindDepthScene()
        {
            Debug.Log("========== Searching for DepthEstimation.unity ==========");

            // Force refresh AssetDatabase
            AssetDatabase.Refresh();
            Debug.Log("[Refresh] AssetDatabase refreshed");

            string depthScenePath = "Assets/PassthroughCameraApiSamples/DepthEstimation/DepthEstimation.unity";

            // Check if file exists on disk
            bool fileExists = System.IO.File.Exists(depthScenePath);
            Debug.Log($"[Check] File exists on disk: {fileExists}");

            if (fileExists)
            {
                // Check file size
                var fileInfo = new System.IO.FileInfo(depthScenePath);
                Debug.Log($"[Check] File size: {fileInfo.Length} bytes");
                Debug.Log($"[Check] Last modified: {fileInfo.LastWriteTime}");
            }

            // Check if Unity recognizes it as an asset
            var asset = AssetDatabase.LoadAssetAtPath<SceneAsset>(depthScenePath);
            bool isValidAsset = asset != null;
            Debug.Log($"[Check] Valid Unity asset: {isValidAsset}");

            // Check if in Build Settings
            var buildScenes = EditorBuildSettings.scenes;
            bool inBuildSettings = buildScenes.Any(s => s.path == depthScenePath);
            Debug.Log($"[Check] In Build Settings: {inBuildSettings}");

            // Show detailed report
            string report = "DepthEstimation.unity Status:\n\n";
            report += $"File exists: {(fileExists ? "✅ Yes" : "❌ No")}\n";

            if (fileExists)
            {
                var fileInfo = new System.IO.FileInfo(depthScenePath);
                report += $"File size: {fileInfo.Length / 1024}KB\n";
            }

            report += $"Unity asset: {(isValidAsset ? "✅ Valid" : "❌ Invalid")}\n";
            report += $"Build Settings: {(inBuildSettings ? "✅ Added" : "❌ Not added")}\n";

            if (fileExists && !isValidAsset)
            {
                report += "\n⚠️ File exists but Unity doesn't recognize it.\n";
                report += "Try: Assets → Reimport All";
            }
            else if (!fileExists)
            {
                report += "\n❌ File is missing!\n";
                report += "Run: Tools → Create Depth Estimation Scene";
            }
            else if (!inBuildSettings)
            {
                report += "\n⚠️ Scene not in Build Settings.\n";
                report += "Click OK to add it now.";

                if (EditorUtility.DisplayDialog("Add to Build Settings?", report, "Add Now", "Cancel"))
                {
                    AddScenesToBuildSettings();
                }
                return;
            }

            EditorUtility.DisplayDialog("DepthEstimation Scene Status", report, "OK");
        }

        [MenuItem("Tools/PassthroughCameraApiSamples/Open DepthEstimation Scene")]
        public static void OpenDepthEstimationScene()
        {
            string depthScenePath = "Assets/PassthroughCameraApiSamples/DepthEstimation/DepthEstimation.unity";

            if (System.IO.File.Exists(depthScenePath))
            {
                // Save current scene if modified
                if (UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene().isDirty)
                {
                    bool save = EditorUtility.DisplayDialog(
                        "Save Current Scene?",
                        "Current scene has unsaved changes. Save before opening DepthEstimation?",
                        "Save",
                        "Don't Save"
                    );

                    if (save)
                    {
                        UnityEditor.SceneManagement.EditorSceneManager.SaveOpenScenes();
                    }
                }

                // Open DepthEstimation scene
                UnityEditor.SceneManagement.EditorSceneManager.OpenScene(depthScenePath);
                Debug.Log($"[Open Scene] Opened: DepthEstimation.unity");
            }
            else
            {
                EditorUtility.DisplayDialog(
                    "Scene Not Found",
                    "DepthEstimation.unity not found!\n\n" +
                    "Run: Tools → Create Depth Estimation Scene",
                    "OK"
                );
            }
        }
    }
}
