// Copyright (c) Meta Platforms, Inc. and affiliates.

using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using PassthroughCameraSamples.DepthEstimation;

namespace PassthroughCameraSamples.Shared.Editor
{
    /// <summary>
    /// Command-line executable script to configure DepthEstimation scene.
    /// Can be run with: Unity -quit -batchmode -executeMethod AutoConfigureDepthScene.Configure
    /// </summary>
    public class AutoConfigureDepthScene
    {
        public static void Configure()
        {
            Debug.Log("[AUTO CONFIG] Starting automatic configuration...");

            // 1. Open the DepthEstimation scene
            string scenePath = "Assets/PassthroughCameraApiSamples/DepthEstimation/DepthEstimation.unity";
            var scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
            Debug.Log($"[AUTO CONFIG] Opened scene: {scenePath}");

            // 2. Find DepthInferenceRunManager GameObject
            DepthInferenceRunManager runManager = GameObject.FindObjectOfType<DepthInferenceRunManager>();
            if (runManager == null)
            {
                Debug.LogError("[AUTO CONFIG] FAILED: DepthInferenceRunManager not found in scene!");
                EditorApplication.Exit(1);
                return;
            }
            Debug.Log($"[AUTO CONFIG] Found DepthInferenceRunManager: {runManager.gameObject.name}");

            // 3. Find or create DepthLabelManager
            GameObject labelManagerGO = runManager.gameObject;
            DepthLabelManager labelManager = labelManagerGO.GetComponentInChildren<DepthLabelManager>(true);

            if (labelManager == null)
            {
                // Create new GameObject for DepthLabelManager
                GameObject newLabelGO = new GameObject("DepthLabelManager");
                newLabelGO.transform.SetParent(labelManagerGO.transform);
                labelManager = newLabelGO.AddComponent<DepthLabelManager>();
                Debug.Log("[AUTO CONFIG] Created new DepthLabelManager GameObject");
            }
            else
            {
                Debug.Log($"[AUTO CONFIG] Found existing DepthLabelManager: {labelManager.gameObject.name}");
            }

            // 4. Find required references by searching all objects
            Component cameraAccess = null;
            Component environmentRaycast = null;

            // Search all GameObjects in scene
            GameObject[] allObjects = GameObject.FindObjectsOfType<GameObject>();
            foreach (var obj in allObjects)
            {
                if (cameraAccess == null)
                {
                    var components = obj.GetComponents<Component>();
                    foreach (var comp in components)
                    {
                        if (comp != null && comp.GetType().Name == "PassthroughCameraAccess")
                        {
                            cameraAccess = comp;
                            Debug.Log($"[AUTO CONFIG] Found PassthroughCameraAccess on: {obj.name}");
                            break;
                        }
                    }
                }

                if (environmentRaycast == null)
                {
                    var components = obj.GetComponents<Component>();
                    foreach (var comp in components)
                    {
                        if (comp != null && comp.GetType().Name == "EnvironmentRayCastSampleManager")
                        {
                            environmentRaycast = comp;
                            Debug.Log($"[AUTO CONFIG] Found EnvironmentRayCastSampleManager on: {obj.name}");
                            break;
                        }
                    }
                }

                if (cameraAccess != null && environmentRaycast != null)
                    break;
            }

            // 5. Configure DepthLabelManager using SerializedObject
            SerializedObject labelSO = new SerializedObject(labelManager);

            if (cameraAccess != null)
            {
                labelSO.FindProperty("m_cameraAccess").objectReferenceValue = cameraAccess;
                Debug.Log("[AUTO CONFIG] Assigned m_cameraAccess");
            }
            else
            {
                Debug.LogWarning("[AUTO CONFIG] PassthroughCameraAccess not found!");
            }

            if (environmentRaycast != null)
            {
                labelSO.FindProperty("m_environmentRaycast").objectReferenceValue = environmentRaycast;
                Debug.Log("[AUTO CONFIG] Assigned m_environmentRaycast");
            }
            else
            {
                Debug.LogWarning("[AUTO CONFIG] EnvironmentRayCastSampleManager not found!");
            }

            labelSO.FindProperty("m_labelOffsetY").floatValue = 0.15f;
            labelSO.FindProperty("m_labelScale").floatValue = 0.05f;
            labelSO.FindProperty("m_maxLabels").intValue = 20;
            labelSO.ApplyModifiedProperties();
            Debug.Log("[AUTO CONFIG] Configured DepthLabelManager properties");

            // 6. Configure DepthInferenceRunManager
            SerializedObject runManagerSO = new SerializedObject(runManager);

            runManagerSO.FindProperty("m_depthLabelManager").objectReferenceValue = labelManager;
            runManagerSO.FindProperty("m_useDebugPointCloud").boolValue = false;
            runManagerSO.FindProperty("m_minDetectionConfidence").floatValue = 0.5f;

            runManagerSO.ApplyModifiedProperties();
            Debug.Log("[AUTO CONFIG] Configured DepthInferenceRunManager:");
            Debug.Log("  - m_depthLabelManager: Assigned");
            Debug.Log("  - m_useDebugPointCloud: false");
            Debug.Log("  - m_minDetectionConfidence: 0.5");

            // 7. Mark scene as dirty and save
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);

            Debug.Log("[AUTO CONFIG] ✓✓✓ Configuration COMPLETE! Scene saved. ✓✓✓");
            Debug.Log("[AUTO CONFIG] Next step: Build for Quest 3");

            // Exit with success code
            EditorApplication.Exit(0);
        }
    }
}
