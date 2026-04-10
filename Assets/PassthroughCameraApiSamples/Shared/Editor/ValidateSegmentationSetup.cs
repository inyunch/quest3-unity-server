// Copyright (c) Meta Platforms, Inc. and affiliates.

#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;

namespace PassthroughCameraSamples.Shared.Editor
{
    /// <summary>
    /// Validates Segmentation scene setup and reports any missing components.
    /// Run via menu: Tools → Validate Segmentation Setup
    /// </summary>
    public static class ValidateSegmentationSetup
    {
        [MenuItem("Tools/Validate Segmentation Setup")]
        public static void ValidateSetup()
        {
            Debug.Log("============================================");
            Debug.Log("[VALIDATE] Starting Segmentation scene validation...");
            Debug.Log("============================================");

            int errors = 0;
            int warnings = 0;

            // Check if correct scene is open
            var activeScene = UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene();
            Debug.Log($"[VALIDATE] Active scene: {activeScene.name}");

            if (!activeScene.name.Contains("Segmentation"))
            {
                Debug.LogWarning("[VALIDATE] ⚠️  Active scene is not 'Segmentation'. Please open Segmentation.unity");
                warnings++;
            }

            // Find SimpleSegmentationManager
            var simpleManager = GameObject.FindObjectOfType(System.Type.GetType("PassthroughCameraSamples.Segmentation.SimpleSegmentationManager, Assembly-CSharp"));

            if (simpleManager == null)
            {
                Debug.LogError("[VALIDATE] ❌ SimpleSegmentationManager not found in scene!");
                Debug.LogError("[VALIDATE]    → Create GameObject with SimpleSegmentationManager component");
                errors++;
            }
            else
            {
                Debug.Log("[VALIDATE] ✓ SimpleSegmentationManager found");

                // Use reflection to check references
                var managerType = simpleManager.GetType();

                // Check Camera Access
                var cameraAccessField = managerType.GetField("m_cameraAccess", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (cameraAccessField != null)
                {
                    var cameraAccess = cameraAccessField.GetValue(simpleManager);
                    if (cameraAccess == null)
                    {
                        Debug.LogError("[VALIDATE] ❌ Camera Access reference not set!");
                        errors++;
                    }
                    else
                    {
                        Debug.Log("[VALIDATE] ✓ Camera Access reference set");
                    }
                }

                // Check Renderer 3D
                var renderer3DField = managerType.GetField("m_renderer3D", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (renderer3DField != null)
                {
                    var renderer3D = renderer3DField.GetValue(simpleManager);
                    if (renderer3D == null)
                    {
                        Debug.LogWarning("[VALIDATE] ⚠️  Renderer 3D reference not set (optional but recommended)");
                        warnings++;
                    }
                    else
                    {
                        Debug.Log("[VALIDATE] ✓ Renderer 3D reference set");
                    }
                }

                // Check Shared HUD
                var sharedHUDField = managerType.GetField("m_sharedHUD", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (sharedHUDField != null)
                {
                    var sharedHUD = sharedHUDField.GetValue(simpleManager);
                    if (sharedHUD == null)
                    {
                        Debug.LogError("[VALIDATE] ❌ Shared HUD reference not set!");
                        errors++;
                    }
                    else
                    {
                        Debug.Log("[VALIDATE] ✓ Shared HUD reference set");

                        // Check if SharedInferenceHUD has TextMeshPro
                        var hudType = sharedHUD.GetType();
                        var metricsTextField = hudType.GetField("m_metricsText", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                        if (metricsTextField != null)
                        {
                            var metricsText = metricsTextField.GetValue(sharedHUD);
                            if (metricsText == null)
                            {
                                Debug.LogError("[VALIDATE] ❌ SharedInferenceHUD.MetricsText reference not set!");
                                errors++;
                            }
                            else
                            {
                                Debug.Log("[VALIDATE] ✓ SharedInferenceHUD.MetricsText reference set");
                            }
                        }
                    }
                }

                // Check InferenceConfig
                var configField = managerType.GetField("m_inferenceConfig", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (configField != null)
                {
                    var config = configField.GetValue(simpleManager);
                    if (config != null)
                    {
                        var configType = config.GetType();

                        // Check mode
                        var modeField = configType.GetField("mode", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                        if (modeField != null)
                        {
                            var mode = modeField.GetValue(config);
                            Debug.Log($"[VALIDATE] ✓ InferenceConfig.mode = {mode}");

                            // Check if mode is Segmentation or SegmentationWithDepth
                            int modeValue = (int)mode;
                            if (modeValue != 4 && modeValue != 5)  // 4=Segmentation, 5=SegmentationWithDepth
                            {
                                Debug.LogWarning($"[VALIDATE] ⚠️  Mode is {mode} (not Segmentation or SegmentationWithDepth)");
                                warnings++;
                            }
                        }

                        // Check targetFPS
                        var fpsField = configType.GetField("targetFPS", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                        if (fpsField != null)
                        {
                            var fps = fpsField.GetValue(config);
                            Debug.Log($"[VALIDATE] ✓ InferenceConfig.targetFPS = {fps}");
                        }

                        // Check useServerConfig
                        var useServerConfigField = configType.GetField("useServerConfig", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                        if (useServerConfigField != null)
                        {
                            var useServerConfig = (bool)useServerConfigField.GetValue(config);
                            if (!useServerConfig)
                            {
                                Debug.LogWarning("[VALIDATE] ⚠️  useServerConfig is false (recommend setting to true)");
                                warnings++;
                            }
                            else
                            {
                                Debug.Log("[VALIDATE] ✓ InferenceConfig.useServerConfig = true");
                            }
                        }
                    }
                }
            }

            // Check for PassthroughCameraAccess in scene
            var cameraAccessInScene = GameObject.FindObjectOfType(System.Type.GetType("PassthroughCameraAccess, Assembly-CSharp"));
            if (cameraAccessInScene == null)
            {
                Debug.LogError("[VALIDATE] ❌ PassthroughCameraAccess not found in scene!");
                errors++;
            }
            else
            {
                Debug.Log("[VALIDATE] ✓ PassthroughCameraAccess exists in scene");
            }

            // Check for Canvas
            var canvas = GameObject.FindObjectOfType<UnityEngine.Canvas>();
            if (canvas == null)
            {
                Debug.LogWarning("[VALIDATE] ⚠️  No Canvas found in scene (needed for HUD)");
                warnings++;
            }
            else
            {
                Debug.Log("[VALIDATE] ✓ Canvas exists in scene");
            }

            // Check for ServerConfig asset
            var serverConfig = UnityEngine.Resources.Load("ServerConfig");
            if (serverConfig == null)
            {
                Debug.LogWarning("[VALIDATE] ⚠️  ServerConfig asset not found in Resources folder");
                Debug.LogWarning("[VALIDATE]    Create at: Assets/PassthroughCameraApiSamples/Shared/Resources/ServerConfig.asset");
                warnings++;
            }
            else
            {
                Debug.Log("[VALIDATE] ✓ ServerConfig asset exists");
            }

            // Summary
            Debug.Log("============================================");
            if (errors == 0 && warnings == 0)
            {
                Debug.Log("[VALIDATE] ✅ VALIDATION PASSED - Scene is ready for testing!");
                Debug.Log("[VALIDATE] Next steps:");
                Debug.Log("[VALIDATE] 1. Save the scene (Ctrl+S)");
                Debug.Log("[VALIDATE] 2. Start server: python -m uvicorn app.main:app --host 0.0.0.0 --port 8001");
                Debug.Log("[VALIDATE] 3. Build and Run to Quest 3");

                EditorUtility.DisplayDialog(
                    "Validation Passed",
                    "Scene is ready for testing!\n\n" +
                    "Next steps:\n" +
                    "1. Save the scene\n" +
                    "2. Start the server\n" +
                    "3. Build and Run",
                    "OK"
                );
            }
            else
            {
                Debug.LogWarning($"[VALIDATE] ⚠️  VALIDATION COMPLETED WITH ISSUES");
                Debug.LogWarning($"[VALIDATE] Errors: {errors}, Warnings: {warnings}");
                Debug.LogWarning($"[VALIDATE] Please fix errors before testing!");

                EditorUtility.DisplayDialog(
                    "Validation Issues Found",
                    $"Found {errors} error(s) and {warnings} warning(s).\n\n" +
                    "Check the Console for details.",
                    "OK"
                );
            }
            Debug.Log("============================================");
        }
    }
}
#endif
