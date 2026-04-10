// Copyright (c) Meta Platforms, Inc. and affiliates.

#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;

namespace PassthroughCameraSamples.Shared.Editor
{
    /// <summary>
    /// Configures Segmentation scene to use SimpleSegmentationManager with unified /infer_human endpoint.
    /// Removes old SegmentationInferenceManager that uses legacy /segmentation endpoint.
    /// </summary>
    public static class ConfigureSimpleSegmentationManager
    {
        [MenuItem("Tools/Configure Simple Segmentation Manager")]
        public static void Configure()
        {
            Debug.Log("==============================================");
            Debug.Log("[CONFIG] Configuring SimpleSegmentationManager...");
            Debug.Log("==============================================");

            var activeScene = EditorSceneManager.GetActiveScene();
            Debug.Log($"[CONFIG] Active scene: {activeScene.name}");

            if (!activeScene.name.Contains("Segmentation"))
            {
                EditorUtility.DisplayDialog(
                    "Wrong Scene",
                    "Please open the Segmentation scene first!",
                    "OK"
                );
                return;
            }

            int changes = 0;

            // Step 1: Disable old SegmentationInferenceManager
            Debug.Log("[CONFIG] Step 1: Looking for old SegmentationInferenceManager...");
            var allGameObjects = GameObject.FindObjectsOfType<GameObject>(true); // Include inactive

            foreach (var go in allGameObjects)
            {
                var oldComponent = go.GetComponent("SegmentationInferenceManager");
                if (oldComponent != null)
                {
                    Debug.Log($"[CONFIG] Found old SegmentationInferenceManager on: {go.name}");
                    Debug.LogWarning($"[CONFIG] Disabling GameObject: {go.name}");
                    go.SetActive(false);
                    EditorUtility.SetDirty(go);
                    changes++;
                }
            }

            // Step 2: Find or create SimpleSegmentationManager
            Debug.Log("[CONFIG] Step 2: Setting up SimpleSegmentationManager...");

            GameObject simpleManagerGO = GameObject.Find("SimpleSegmentationManager");
            Component simpleManager = null;

            if (simpleManagerGO != null)
            {
                Debug.Log("[CONFIG] Found existing SimpleSegmentationManager GameObject");
                simpleManager = simpleManagerGO.GetComponent("SimpleSegmentationManager");

                if (simpleManager == null)
                {
                    Debug.Log("[CONFIG] Adding SimpleSegmentationManager component...");
                    var managerType = System.Type.GetType("PassthroughCameraSamples.Segmentation.SimpleSegmentationManager, Assembly-CSharp");
                    if (managerType != null)
                    {
                        simpleManager = simpleManagerGO.AddComponent(managerType);
                        changes++;
                    }
                }
            }
            else
            {
                Debug.Log("[CONFIG] Creating new SimpleSegmentationManager GameObject...");
                simpleManagerGO = new GameObject("SimpleSegmentationManager");

                var managerType = System.Type.GetType("PassthroughCameraSamples.Segmentation.SimpleSegmentationManager, Assembly-CSharp");
                if (managerType != null)
                {
                    simpleManager = simpleManagerGO.AddComponent(managerType);
                    changes++;
                }
                else
                {
                    Debug.LogError("[CONFIG] SimpleSegmentationManager type not found! Make sure script is compiled.");
                    UnityEngine.Object.DestroyImmediate(simpleManagerGO);
                    return;
                }
            }

            if (simpleManager == null)
            {
                Debug.LogError("[CONFIG] Failed to create SimpleSegmentationManager!");
                return;
            }

            // Step 3: Auto-wire references
            Debug.Log("[CONFIG] Step 3: Auto-wiring component references...");

            var managerType2 = simpleManager.GetType();

            // Find PassthroughCameraAccess
            var cameraAccessGO = GameObject.Find("PassthroughCameraAccess");
            if (cameraAccessGO != null)
            {
                var cameraAccess = cameraAccessGO.GetComponent("PassthroughCameraAccess");
                if (cameraAccess != null)
                {
                    var field = managerType2.GetField("m_cameraAccess",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    if (field != null)
                    {
                        field.SetValue(simpleManager, cameraAccess);
                        Debug.Log($"[CONFIG] ✓ Set Camera Access → {cameraAccessGO.name}");
                        changes++;
                    }
                }
            }
            else
            {
                Debug.LogWarning("[CONFIG] ⚠ PassthroughCameraAccess GameObject not found");
            }

            // Find Segmentation3DRenderer
            var renderer3DGO = GameObject.Find("Segmentation3DRenderer");
            if (renderer3DGO != null)
            {
                var renderer3D = renderer3DGO.GetComponent("Segmentation3DRenderer");
                if (renderer3D != null)
                {
                    var field = managerType2.GetField("m_renderer3D",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    if (field != null)
                    {
                        field.SetValue(simpleManager, renderer3D);
                        Debug.Log($"[CONFIG] ✓ Set Renderer 3D → {renderer3DGO.name}");
                        changes++;
                    }
                }
            }
            else
            {
                Debug.LogWarning("[CONFIG] ⚠ Segmentation3DRenderer GameObject not found");
            }

            // Find SharedInferenceHUD
            var hudGO = GameObject.Find("SharedInferenceHUD");
            if (hudGO != null)
            {
                var hud = hudGO.GetComponent("SharedInferenceHUD");
                if (hud != null)
                {
                    var field = managerType2.GetField("m_sharedHUD",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    if (field != null)
                    {
                        field.SetValue(simpleManager, hud);
                        Debug.Log($"[CONFIG] ✓ Set Shared HUD → {hudGO.name}");
                        changes++;
                    }
                }
            }
            else
            {
                Debug.LogWarning("[CONFIG] ⚠ SharedInferenceHUD GameObject not found");
            }

            // Step 4: Configure InferenceConfig
            Debug.Log("[CONFIG] Step 4: Configuring InferenceConfig...");

            var configField = managerType2.GetField("m_inferenceConfig",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            if (configField != null)
            {
                var config = configField.GetValue(simpleManager);
                if (config != null)
                {
                    var configType = config.GetType();

                    // Set mode to SegmentationWithDepth (value = 5)
                    SetField(config, configType, "mode", 5, "SegmentationWithDepth");

                    // Set targetFPS
                    SetField(config, configType, "targetFPS", 5f, "5.0");

                    // Set jpegQuality
                    SetField(config, configType, "jpegQuality", 80, "80");

                    // Set useServerConfig
                    SetField(config, configType, "useServerConfig", true, "true");

                    configField.SetValue(simpleManager, config);
                    changes += 4;
                }
            }

            // Mark dirty and save
            EditorUtility.SetDirty(simpleManagerGO);
            EditorSceneManager.MarkSceneDirty(activeScene);

            // Summary
            Debug.Log("==============================================");
            Debug.Log($"[CONFIG] ✅ Configuration complete! ({changes} changes)");
            Debug.Log("[CONFIG] Next steps:");
            Debug.Log("[CONFIG] 1. Save scene (Ctrl+S)");
            Debug.Log("[CONFIG] 2. Run 'Tools → Validate Segmentation Setup'");
            Debug.Log("[CONFIG] 3. Build and Run");
            Debug.Log("==============================================");

            EditorUtility.DisplayDialog(
                "Configuration Complete",
                $"SimpleSegmentationManager configured!\n\n" +
                $"Changes: {changes}\n\n" +
                "Mode: SegmentationWithDepth\n" +
                "Target FPS: 5\n" +
                "JPEG Quality: 80\n" +
                "Using: /infer_human endpoint\n\n" +
                "Remember to save the scene!",
                "OK"
            );
        }

        private static void SetField(object obj, System.Type type, string fieldName, object value, string displayValue)
        {
            var field = type.GetField(fieldName, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            if (field != null)
            {
                field.SetValue(obj, value);
                Debug.Log($"[CONFIG] ✓ Set {fieldName} = {displayValue}");
            }
        }
    }
}
#endif
