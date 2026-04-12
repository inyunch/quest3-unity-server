// Copyright (c) Meta Platforms, Inc. and affiliates.

using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using PassthroughCameraSamples.DepthEstimation;
using PassthroughCameraSamples.Shared;
using PassthroughCameraSamples.MultiObjectDetection;

namespace PassthroughCameraSamples.Shared.Editor
{
    /// <summary>
    /// Editor script to configure the DepthEstimation scene with DepthLabelManager.
    /// </summary>
    public class ConfigureDepthScene : EditorWindow
    {
        [MenuItem("Tools/Configure Depth Estimation Scene")]
        public static void ConfigureScene()
        {
            // 1. Open the DepthEstimation scene
            string scenePath = "Assets/PassthroughCameraApiSamples/DepthEstimation/DepthEstimation.unity";
            var scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);

            Debug.Log("[CONFIG] Opened DepthEstimation scene");

            // 2. Find DepthInferenceRunManager GameObject
            DepthInferenceRunManager runManager = GameObject.FindObjectOfType<DepthInferenceRunManager>();
            if (runManager == null)
            {
                Debug.LogError("[CONFIG] DepthInferenceRunManager not found in scene!");
                EditorUtility.DisplayDialog("Error", "DepthInferenceRunManager not found in scene!", "OK");
                return;
            }

            Debug.Log($"[CONFIG] Found DepthInferenceRunManager on GameObject: {runManager.gameObject.name}");

            // 3. Find or create DepthLabelManager on the same GameObject or a child
            GameObject labelManagerGO = runManager.gameObject;
            DepthLabelManager labelManager = labelManagerGO.GetComponent<DepthLabelManager>();

            if (labelManager == null)
            {
                // Check if it exists as a child
                labelManager = labelManagerGO.GetComponentInChildren<DepthLabelManager>();

                if (labelManager == null)
                {
                    // Create new GameObject for DepthLabelManager
                    GameObject newLabelGO = new GameObject("DepthLabelManager");
                    newLabelGO.transform.SetParent(labelManagerGO.transform);
                    labelManager = newLabelGO.AddComponent<DepthLabelManager>();
                    Debug.Log("[CONFIG] Created new DepthLabelManager GameObject");
                }
            }

            Debug.Log($"[CONFIG] DepthLabelManager found/created on: {labelManager.gameObject.name}");

            // 4. Get required references from the scene by name (avoid type references)
            GameObject cameraAccessGO = GameObject.Find("PassthroughCameraAccess");
            GameObject environmentRaycastGO = GameObject.Find("EnvironmentRayCastSampleManager");

            // 5. Configure DepthLabelManager using SerializedObject
            SerializedObject labelSO = new SerializedObject(labelManager);

            // Find components by name if GameObjects exist
            if (cameraAccessGO != null)
            {
                var components = cameraAccessGO.GetComponents<Component>();
                foreach (var comp in components)
                {
                    if (comp.GetType().Name == "PassthroughCameraAccess")
                    {
                        labelSO.FindProperty("m_cameraAccess").objectReferenceValue = comp;
                        Debug.Log("[CONFIG] Found and assigned PassthroughCameraAccess");
                        break;
                    }
                }
            }
            else
            {
                Debug.LogWarning("[CONFIG] PassthroughCameraAccess GameObject not found by name");
            }

            if (environmentRaycastGO != null)
            {
                var components = environmentRaycastGO.GetComponents<Component>();
                foreach (var comp in components)
                {
                    if (comp.GetType().Name == "EnvironmentRayCastSampleManager")
                    {
                        labelSO.FindProperty("m_environmentRaycast").objectReferenceValue = comp;
                        Debug.Log("[CONFIG] Found and assigned EnvironmentRayCastSampleManager");
                        break;
                    }
                }
            }
            else
            {
                Debug.LogWarning("[CONFIG] EnvironmentRayCastSampleManager GameObject not found by name");
            }

            labelSO.FindProperty("m_labelOffsetY").floatValue = 0.15f;
            labelSO.FindProperty("m_labelScale").floatValue = 0.05f;
            labelSO.FindProperty("m_maxLabels").intValue = 20;

            labelSO.ApplyModifiedProperties();
            Debug.Log("[CONFIG] Configured DepthLabelManager properties");

            // 6. Configure DepthInferenceRunManager
            SerializedObject runManagerSO = new SerializedObject(runManager);

            runManagerSO.FindProperty("m_depthLabelManager").objectReferenceValue = labelManager;
            runManagerSO.FindProperty("m_useDebugPointCloud").boolValue = false;
            runManagerSO.FindProperty("m_minDetectionConfidence").floatValue = 0.5f;

            runManagerSO.ApplyModifiedProperties();
            Debug.Log("[CONFIG] Configured DepthInferenceRunManager properties");

            // 7. Mark scene as dirty and save
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);

            Debug.Log("[CONFIG] ✓ Configuration complete! Scene saved.");
            EditorUtility.DisplayDialog("Success",
                "DepthEstimation scene configured successfully!\n\n" +
                "- DepthLabelManager assigned\n" +
                "- Debug point cloud disabled\n" +
                "- Min detection confidence set to 0.5\n\n" +
                "Ready to build for Quest 3!",
                "OK");
        }

        [MenuItem("Tools/Verify Depth Scene Configuration")]
        public static void VerifyConfiguration()
        {
            string scenePath = "Assets/PassthroughCameraApiSamples/DepthEstimation/DepthEstimation.unity";
            var scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);

            var runManager = GameObject.FindObjectOfType<DepthInferenceRunManager>();
            if (runManager == null)
            {
                Debug.LogError("[VERIFY] DepthInferenceRunManager not found!");
                return;
            }

            SerializedObject runManagerSO = new SerializedObject(runManager);

            var labelManagerProp = runManagerSO.FindProperty("m_depthLabelManager");
            var debugModeProp = runManagerSO.FindProperty("m_useDebugPointCloud");
            var minConfProp = runManagerSO.FindProperty("m_minDetectionConfidence");

            bool isConfigured = labelManagerProp.objectReferenceValue != null &&
                               debugModeProp.boolValue == false &&
                               Mathf.Approximately(minConfProp.floatValue, 0.5f);

            string status = "Configuration Status:\n\n";
            status += $"DepthLabelManager: {(labelManagerProp.objectReferenceValue != null ? "✓ Assigned" : "✗ Not assigned")}\n";
            status += $"Debug Point Cloud: {(debugModeProp.boolValue ? "✗ Enabled (should be disabled)" : "✓ Disabled")}\n";
            status += $"Min Detection Confidence: {minConfProp.floatValue:F1} {(Mathf.Approximately(minConfProp.floatValue, 0.5f) ? "✓" : "(!= 0.5)")}\n";

            Debug.Log($"[VERIFY] {status}");
            EditorUtility.DisplayDialog(isConfigured ? "Configuration Valid" : "Configuration Issues", status, "OK");
        }
    }
}
