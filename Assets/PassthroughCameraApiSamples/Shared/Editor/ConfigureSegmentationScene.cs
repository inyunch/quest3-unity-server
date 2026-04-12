// Copyright (c) Meta Platforms, Inc. and affiliates.

using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using PassthroughCameraSamples.Segmentation;
using PassthroughCameraSamples.Shared;

namespace PassthroughCameraSamples.Editor
{
    /// <summary>
    /// Automatically configure the Segmentation scene with correct InferenceConfig settings.
    /// </summary>
    public static class ConfigureSegmentationScene
    {
        [MenuItem("Tools/Passthrough Camera/Configure Segmentation Scene")]
        public static void Configure()
        {
            // Load the scene
            string scenePath = "Assets/PassthroughCameraApiSamples/Segmentation/Segmentation.unity";
            var scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);

            if (!scene.IsValid())
            {
                Debug.LogError($"[AUTO-CONFIG] Failed to load scene: {scenePath}");
                return;
            }

            Debug.Log("[AUTO-CONFIG] ====== Configuring Segmentation Scene ======");

            // Find SegmentationInferenceRunManager
            var runManager = GameObject.FindObjectOfType<SegmentationInferenceRunManager>();
            if (runManager == null)
            {
                Debug.LogError("[AUTO-CONFIG] SegmentationInferenceRunManager not found in scene!");
                return;
            }

            Debug.Log($"[AUTO-CONFIG] Found SegmentationInferenceRunManager on GameObject: {runManager.gameObject.name}");

            // Use SerializedObject to modify private fields
            var so = new SerializedObject(runManager);

            // Find m_inferenceConfig field
            var inferenceConfigProp = so.FindProperty("m_inferenceConfig");
            if (inferenceConfigProp == null)
            {
                Debug.LogError("[AUTO-CONFIG] m_inferenceConfig property not found!");
                return;
            }

            // Configure InferenceConfig
            var modeProp = inferenceConfigProp.FindPropertyRelative("mode");
            var useServerConfigProp = inferenceConfigProp.FindPropertyRelative("useServerConfig");
            var targetFPSProp = inferenceConfigProp.FindPropertyRelative("targetFPS");
            var jpegQualityProp = inferenceConfigProp.FindPropertyRelative("jpegQuality");
            var includeMaskProp = inferenceConfigProp.FindPropertyRelative("includeMask");
            var includeDepthProp = inferenceConfigProp.FindPropertyRelative("includeDepth");

            if (modeProp != null)
            {
                // Set mode to Segmentation (enum value 4)
                modeProp.enumValueIndex = 4; // InferenceMode.Segmentation
                Debug.Log("[AUTO-CONFIG] ✓ Set mode = InferenceMode.Segmentation");
            }

            if (useServerConfigProp != null)
            {
                useServerConfigProp.boolValue = true;
                Debug.Log("[AUTO-CONFIG] ✓ Set useServerConfig = true");
            }

            if (targetFPSProp != null)
            {
                targetFPSProp.floatValue = 10f;
                Debug.Log("[AUTO-CONFIG] ✓ Set targetFPS = 10");
            }

            if (jpegQualityProp != null)
            {
                jpegQualityProp.intValue = 80;
                Debug.Log("[AUTO-CONFIG] ✓ Set jpegQuality = 80");
            }

            if (includeMaskProp != null)
            {
                includeMaskProp.boolValue = false;
                Debug.Log("[AUTO-CONFIG] ✓ Set includeMask = false (Phase 1: bounding boxes only)");
            }

            if (includeDepthProp != null)
            {
                includeDepthProp.boolValue = false;
                Debug.Log("[AUTO-CONFIG] ✓ Set includeDepth = false");
            }

            // Find m_useServerInference field
            var useServerInferenceProp = so.FindProperty("m_useServerInference");
            if (useServerInferenceProp != null)
            {
                useServerInferenceProp.boolValue = true;
                Debug.Log("[AUTO-CONFIG] ✓ Set m_useServerInference = true");
            }

            // Apply changes
            so.ApplyModifiedProperties();

            // Mark scene as dirty and save
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);

            Debug.Log("[AUTO-CONFIG] ====== Configuration Complete ======");
            Debug.Log($"[AUTO-CONFIG] Scene saved: {scenePath}");
            Debug.Log("[AUTO-CONFIG]");
            Debug.Log("[AUTO-CONFIG] Expected behavior:");
            Debug.Log("[AUTO-CONFIG] - URL: http://192.168.0.135:8001/segmentation");
            Debug.Log("[AUTO-CONFIG] - Mode: Segmentation (10 FPS)");
            Debug.Log("[AUTO-CONFIG] - Response: ObjectDetection-compatible format (bounding boxes only)");
            Debug.Log("[AUTO-CONFIG]");
            Debug.Log("[AUTO-CONFIG] ✅ Ready to build and test!");
        }
    }
}
