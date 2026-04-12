// Copyright (c) Meta Platforms, Inc. and affiliates.

using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using PassthroughCameraSamples.Segmentation;

namespace PassthroughCameraSamples.Editor
{
    /// <summary>
    /// Automatically configure Segmentation scene when it's opened.
    /// This ensures the InferenceConfig is always correctly set.
    /// </summary>
    [InitializeOnLoad]
    public static class AutoConfigureSegmentationOnLoad
    {
        private const string SCENE_PATH = "Assets/PassthroughCameraApiSamples/Segmentation/Segmentation.unity";
        private const string PREF_KEY = "SegmentationSceneConfigured_v1";

        static AutoConfigureSegmentationOnLoad()
        {
            // Subscribe to scene opened event
            EditorSceneManager.sceneOpened += OnSceneOpened;
        }

        private static void OnSceneOpened(UnityEngine.SceneManagement.Scene scene, OpenSceneMode mode)
        {
            // Check if this is the Segmentation scene
            if (!scene.path.EndsWith("Segmentation.unity"))
                return;

            // Check if already configured (to avoid repeated config)
            string prefKey = $"{PREF_KEY}_{scene.path}";
            if (EditorPrefs.GetBool(prefKey, false))
            {
                Debug.Log("[AUTO-CONFIG] Segmentation scene already configured, skipping.");
                return;
            }

            Debug.Log("[AUTO-CONFIG] ====== Auto-Configuring Segmentation Scene ======");

            // Find SegmentationInferenceRunManager
            var runManager = GameObject.FindObjectOfType<SegmentationInferenceRunManager>();
            if (runManager == null)
            {
                Debug.LogWarning("[AUTO-CONFIG] SegmentationInferenceRunManager not found, skipping auto-config.");
                return;
            }

            // Use SerializedObject to modify
            var so = new SerializedObject(runManager);
            var inferenceConfigProp = so.FindProperty("m_inferenceConfig");

            if (inferenceConfigProp != null)
            {
                var modeProp = inferenceConfigProp.FindPropertyRelative("mode");
                var useServerConfigProp = inferenceConfigProp.FindPropertyRelative("useServerConfig");
                var targetFPSProp = inferenceConfigProp.FindPropertyRelative("targetFPS");
                var jpegQualityProp = inferenceConfigProp.FindPropertyRelative("jpegQuality");

                if (modeProp != null) modeProp.enumValueIndex = 4; // InferenceMode.Segmentation
                if (useServerConfigProp != null) useServerConfigProp.boolValue = true;
                if (targetFPSProp != null) targetFPSProp.floatValue = 10f;
                if (jpegQualityProp != null) jpegQualityProp.intValue = 80;

                so.ApplyModifiedProperties();

                Debug.Log("[AUTO-CONFIG] ✓ Configured InferenceConfig:");
                Debug.Log("[AUTO-CONFIG]   - mode = Segmentation");
                Debug.Log("[AUTO-CONFIG]   - useServerConfig = true");
                Debug.Log("[AUTO-CONFIG]   - targetFPS = 10");
                Debug.Log("[AUTO-CONFIG]   - URL will be: http://192.168.0.135:8001/segmentation");
            }

            // Find m_useServerInference
            var useServerInferenceProp = so.FindProperty("m_useServerInference");
            if (useServerInferenceProp != null)
            {
                useServerInferenceProp.boolValue = true;
                so.ApplyModifiedProperties();
                Debug.Log("[AUTO-CONFIG] ✓ Set m_useServerInference = true");
            }

            // Mark scene as dirty
            EditorSceneManager.MarkSceneDirty(scene);

            // Mark as configured
            EditorPrefs.SetBool(prefKey, true);

            Debug.Log("[AUTO-CONFIG] ====== Configuration Complete ======");
            Debug.Log("[AUTO-CONFIG] Scene will be saved on next save/build.");
        }

        [MenuItem("Tools/Passthrough Camera/Reset Segmentation Auto-Config")]
        private static void ResetAutoConfig()
        {
            string prefKey = $"{PREF_KEY}_{SCENE_PATH}";
            EditorPrefs.DeleteKey(prefKey);
            Debug.Log("[AUTO-CONFIG] Reset auto-config flag. Scene will be reconfigured on next open.");
        }
    }
}
