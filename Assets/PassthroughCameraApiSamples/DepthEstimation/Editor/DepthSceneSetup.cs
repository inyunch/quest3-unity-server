// Copyright (c) Meta Platforms, Inc. and affiliates.

using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
using PassthroughCameraSamples.DepthEstimation;
using PassthroughCameraSamples.Shared;
using PassthroughCameraSamples.MultiObjectDetection;
using Meta.XR;
using UnityEngine.UI;
using TMPro;

namespace PassthroughCameraSamples.DepthEstimation.Editor
{
    /// <summary>
    /// Automated setup tool for creating the DepthEstimation scene.
    /// Menu: Tools > PassthroughCameraApiSamples > Create Depth Estimation Scene
    /// </summary>
    public static class DepthSceneSetup
    {
        private const string SCENE_PATH = "Assets/PassthroughCameraApiSamples/DepthEstimation/DepthEstimation.unity";
        private const string REFERENCE_SCENE = "Assets/PassthroughCameraApiSamples/MultiObjectDetection/MultiObjectDetection.unity";

        [MenuItem("Tools/PassthroughCameraApiSamples/Create Depth Estimation Scene")]
        public static void CreateDepthEstimationScene()
        {
            // Check if scene already exists
            if (System.IO.File.Exists(SCENE_PATH))
            {
                bool overwrite = EditorUtility.DisplayDialog(
                    "Scene Already Exists",
                    $"DepthEstimation.unity already exists at:\n{SCENE_PATH}\n\nDo you want to overwrite it?",
                    "Overwrite",
                    "Cancel"
                );

                if (!overwrite)
                {
                    Debug.Log("[Depth Scene Setup] Cancelled by user.");
                    return;
                }
            }

            Debug.Log("[Depth Scene Setup] Starting automated scene creation...");

            // Create new scene
            Scene newScene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
            Debug.Log($"[Depth Scene Setup] Created new scene: {newScene.name}");

            // Create root GameObjects structure
            CreateSceneStructure();

            // Save scene
            bool saved = EditorSceneManager.SaveScene(newScene, SCENE_PATH);
            if (saved)
            {
                Debug.Log($"[Depth Scene Setup] ✅ Scene saved successfully to: {SCENE_PATH}");
                EditorUtility.DisplayDialog(
                    "Success!",
                    "DepthEstimation scene created successfully!\n\n" +
                    "Scene Location: " + SCENE_PATH + "\n\n" +
                    "Next Steps:\n" +
                    "1. The scene is now open in the editor\n" +
                    "2. Configure the server IP if needed (currently: 192.168.0.135)\n" +
                    "3. Build and Run to Quest 3",
                    "OK"
                );
            }
            else
            {
                Debug.LogError("[Depth Scene Setup] ❌ Failed to save scene!");
            }
        }

        private static void CreateSceneStructure()
        {
            Debug.Log("[Depth Scene Setup] Creating scene structure...");

            // 1. Create Camera Rig (OVR Rig)
            GameObject cameraRig = CreateCameraRig();

            // 2. Create Passthrough
            GameObject passthrough = CreatePassthrough();

            // 3. Create Hand Tracking
            GameObject handTracking = CreateHandTracking();

            // 4. Create Depth Inference Manager
            GameObject depthManager = CreateDepthInferenceManager();

            // 5. Create UI Canvas
            GameObject canvas = CreateUICanvas(depthManager);

            Debug.Log("[Depth Scene Setup] ✅ Scene structure created successfully!");
        }

        private static GameObject CreateCameraRig()
        {
            GameObject cameraRig = new GameObject("[BuildingBlock] Camera Rig");

            // Add OVRCameraRig component
            var ovrRig = cameraRig.AddComponent<OVRCameraRig>();

            Debug.Log("[Depth Scene Setup] Created Camera Rig");
            return cameraRig;
        }

        private static GameObject CreatePassthrough()
        {
            GameObject passthrough = new GameObject("[BuildingBlock] Passthrough");

            // Add OVRPassthroughLayer
            var passthroughLayer = passthrough.AddComponent<OVRPassthroughLayer>();
            passthroughLayer.textureOpacity = 1f;
            passthroughLayer.overlayType = OVROverlay.OverlayType.Underlay;

            Debug.Log("[Depth Scene Setup] Created Passthrough");
            return passthrough;
        }

        private static GameObject CreateHandTracking()
        {
            GameObject handTracking = new GameObject("[BuildingBlock] Hand Tracking");

            // Add OVRHand components for left and right hands
            GameObject leftHand = new GameObject("LeftHand");
            leftHand.transform.SetParent(handTracking.transform);
            var leftOVRHand = leftHand.AddComponent<OVRHand>();

            GameObject rightHand = new GameObject("RightHand");
            rightHand.transform.SetParent(handTracking.transform);
            var rightOVRHand = rightHand.AddComponent<OVRHand>();

            Debug.Log("[Depth Scene Setup] Created Hand Tracking");
            return handTracking;
        }

        private static GameObject CreateDepthInferenceManager()
        {
            GameObject manager = new GameObject("DepthInferenceManager");

            // Add PassthroughCameraAccess
            var cameraAccess = manager.AddComponent<PassthroughCameraAccess>();

            // Add DetectionUiMenuManager
            var uiMenuManager = manager.AddComponent<DetectionUiMenuManager>();

            // Add DepthInferenceRunManager
            var depthRunManager = manager.AddComponent<DepthInferenceRunManager>();

            // Configure InferenceConfig via SerializedObject
            SerializedObject so = new SerializedObject(depthRunManager);
            SerializedProperty configProp = so.FindProperty("m_inferenceConfig");

            if (configProp != null)
            {
                SerializedProperty modeProp = configProp.FindPropertyRelative("mode");
                SerializedProperty targetFPSProp = configProp.FindPropertyRelative("targetFPS");
                SerializedProperty jpegQualityProp = configProp.FindPropertyRelative("jpegQuality");
                SerializedProperty baseUrlProp = configProp.FindPropertyRelative("baseUrl");

                if (modeProp != null) modeProp.enumValueIndex = 3; // DepthEstimation = 3
                if (targetFPSProp != null) targetFPSProp.floatValue = 5f;
                if (jpegQualityProp != null) jpegQualityProp.intValue = 80;
                if (baseUrlProp != null) baseUrlProp.stringValue = "http://192.168.0.135:8001/infer_human";

                so.ApplyModifiedProperties();
            }

            Debug.Log("[Depth Scene Setup] Created DepthInferenceManager with InferenceConfig (mode=DepthEstimation, targetFPS=5)");
            return manager;
        }

        private static GameObject CreateUICanvas(GameObject depthManager)
        {
            GameObject canvas = new GameObject("DepthVisualizationCanvas");

            // Add Canvas component
            var canvasComponent = canvas.AddComponent<Canvas>();
            canvasComponent.renderMode = RenderMode.WorldSpace;
            canvasComponent.worldCamera = Camera.main;

            // Set canvas transform
            canvas.transform.position = new Vector3(0, 1.5f, 2f);
            canvas.transform.localScale = new Vector3(0.01f, 0.01f, 0.01f);

            // Add CanvasScaler
            var scaler = canvas.AddComponent<UnityEngine.UI.CanvasScaler>();
            scaler.dynamicPixelsPerUnit = 100;

            // Add GraphicRaycaster
            canvas.AddComponent<UnityEngine.UI.GraphicRaycaster>();

            // Create Panel for layout
            GameObject panel = new GameObject("Panel");
            panel.transform.SetParent(canvas.transform);
            var panelRect = panel.AddComponent<RectTransform>();
            panelRect.anchorMin = Vector2.zero;
            panelRect.anchorMax = Vector2.one;
            panelRect.sizeDelta = Vector2.zero;

            // Create Depth Display (RawImage)
            GameObject depthDisplay = new GameObject("DepthDisplay");
            depthDisplay.transform.SetParent(panel.transform);
            var depthRect = depthDisplay.AddComponent<RectTransform>();
            depthRect.anchorMin = new Vector2(0.1f, 0.3f);
            depthRect.anchorMax = new Vector2(0.5f, 0.9f);
            depthRect.sizeDelta = Vector2.zero;
            var rawImage = depthDisplay.AddComponent<RawImage>();
            rawImage.color = Color.white;

            // Create Center Depth Text
            GameObject centerDepthText = new GameObject("CenterDepthText");
            centerDepthText.transform.SetParent(panel.transform);
            var textRect = centerDepthText.AddComponent<RectTransform>();
            textRect.anchorMin = new Vector2(0.55f, 0.7f);
            textRect.anchorMax = new Vector2(0.9f, 0.9f);
            textRect.sizeDelta = Vector2.zero;
            var tmpText = centerDepthText.AddComponent<TextMeshProUGUI>();
            tmpText.text = "Center Depth: --";
            tmpText.fontSize = 24;
            tmpText.alignment = TextAlignmentOptions.TopLeft;

            // Create HUD Text for metrics
            GameObject hudText = new GameObject("HUDText");
            hudText.transform.SetParent(panel.transform);
            var hudRect = hudText.AddComponent<RectTransform>();
            hudRect.anchorMin = new Vector2(0.55f, 0.3f);
            hudRect.anchorMax = new Vector2(0.9f, 0.65f);
            hudRect.sizeDelta = Vector2.zero;
            var hudTmpText = hudText.AddComponent<TextMeshProUGUI>();
            hudTmpText.text = "Metrics: --";
            hudTmpText.fontSize = 20;
            hudTmpText.alignment = TextAlignmentOptions.TopLeft;

            // Add DepthVisualization component to DepthManager
            var depthViz = depthManager.AddComponent<DepthVisualization>();
            SerializedObject vizSO = new SerializedObject(depthViz);
            vizSO.FindProperty("m_depthDisplay").objectReferenceValue = rawImage;
            vizSO.FindProperty("m_centerDepthText").objectReferenceValue = tmpText;
            vizSO.ApplyModifiedProperties();

            // Add SharedInferenceHUD to DepthManager
            var sharedHUD = depthManager.AddComponent<SharedInferenceHUD>();
            SerializedObject hudSO = new SerializedObject(sharedHUD);
            hudSO.FindProperty("m_metricsText").objectReferenceValue = hudTmpText;
            hudSO.ApplyModifiedProperties();

            // Link SharedInferenceHUD and DepthVisualization to DepthInferenceRunManager
            var depthRunManager = depthManager.GetComponent<DepthInferenceRunManager>();
            SerializedObject managerSO = new SerializedObject(depthRunManager);
            managerSO.FindProperty("m_sharedHUD").objectReferenceValue = sharedHUD;
            managerSO.FindProperty("m_depthVisualization").objectReferenceValue = depthViz;
            managerSO.FindProperty("m_cameraAccess").objectReferenceValue = depthManager.GetComponent<PassthroughCameraAccess>();
            managerSO.FindProperty("m_uiMenuManager").objectReferenceValue = depthManager.GetComponent<DetectionUiMenuManager>();
            managerSO.ApplyModifiedProperties();

            Debug.Log("[Depth Scene Setup] Created UI Canvas with DepthVisualization and SharedInferenceHUD");
            return canvas;
        }

        [MenuItem("Tools/PassthroughCameraApiSamples/Validate Depth Scene Setup")]
        public static void ValidateDepthScene()
        {
            Debug.Log("[Depth Scene Setup] Validating current scene...");

            var manager = GameObject.Find("DepthInferenceManager");
            if (manager == null)
            {
                Debug.LogError("[Depth Scene Setup] ❌ DepthInferenceManager not found!");
                return;
            }

            var depthRunManager = manager.GetComponent<DepthInferenceRunManager>();
            var depthViz = manager.GetComponent<DepthVisualization>();
            var sharedHUD = manager.GetComponent<SharedInferenceHUD>();
            var cameraAccess = manager.GetComponent<PassthroughCameraAccess>();
            var uiMenuManager = manager.GetComponent<DetectionUiMenuManager>();

            Debug.Log($"[Depth Scene Setup] DepthInferenceRunManager: {(depthRunManager != null ? "✅" : "❌")}");
            Debug.Log($"[Depth Scene Setup] DepthVisualization: {(depthViz != null ? "✅" : "❌")}");
            Debug.Log($"[Depth Scene Setup] SharedInferenceHUD: {(sharedHUD != null ? "✅" : "❌")}");
            Debug.Log($"[Depth Scene Setup] PassthroughCameraAccess: {(cameraAccess != null ? "✅" : "❌")}");
            Debug.Log($"[Depth Scene Setup] DetectionUiMenuManager: {(uiMenuManager != null ? "✅" : "❌")}");

            if (depthRunManager != null && depthViz != null && sharedHUD != null && cameraAccess != null)
            {
                Debug.Log("[Depth Scene Setup] ✅ All required components found!");
            }
            else
            {
                Debug.LogWarning("[Depth Scene Setup] ⚠️ Some components are missing!");
            }
        }
    }
}
