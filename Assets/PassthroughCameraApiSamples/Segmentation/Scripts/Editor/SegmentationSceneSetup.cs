// Copyright (c) Meta Platforms, Inc. and affiliates.

using UnityEngine;
using UnityEditor;
using UnityEngine.UI;
using PassthroughCameraSamples.Segmentation;

namespace PassthroughCameraSamples.Segmentation.Editor
{
    /// <summary>
    /// Editor utility to automatically configure the Segmentation scene.
    /// Usage: Open Segmentation.unity scene, then go to Tools > Segmentation > Setup Scene
    /// </summary>
    public class SegmentationSceneSetup : EditorWindow
    {
        [MenuItem("Tools/Segmentation/Setup Scene")]
        public static void SetupSegmentationScene()
        {
            if (!EditorUtility.DisplayDialog(
                "Setup Segmentation Scene",
                "This will configure the current scene for RGB-D Segmentation.\n\n" +
                "Make sure you have the Segmentation.unity scene open.\n\n" +
                "This will:\n" +
                "- Remove old PoseEstimation components\n" +
                "- Add SegmentationInferenceManager\n" +
                "- Add QuestDepthCaptureManager\n" +
                "- Add SegmentationOverlayRenderer\n" +
                "- Configure all references\n\n" +
                "Continue?",
                "Yes, Setup Scene",
                "Cancel"))
            {
                return;
            }

            Debug.Log("[SETUP] Starting Segmentation scene setup...");

            // Step 1: Remove old PoseEstimation components
            RemovePoseEstimationComponents();

            // Step 2: Find or create required GameObjects
            GameObject segmentationManager = SetupSegmentationManager();
            GameObject depthCaptureManager = SetupDepthCaptureManager();
            GameObject overlayRenderer = SetupOverlayRenderer();

            // Step 3: Find or create PassthroughCameraAccess
            GameObject cameraAccess = SetupPassthroughCameraAccess();

            // Step 4: Configure references
            ConfigureReferences(segmentationManager, depthCaptureManager, overlayRenderer, cameraAccess);

            // Step 5: Save scene
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(
                UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene());

            Debug.Log("[SETUP] ✓ Segmentation scene setup complete!");
            Debug.Log("[SETUP] Please review the configuration in the Inspector and save the scene.");

            EditorUtility.DisplayDialog(
                "Setup Complete",
                "Segmentation scene has been configured!\n\n" +
                "Next steps:\n" +
                "1. Review component settings in Inspector\n" +
                "2. Update Server URL if needed\n" +
                "3. Save scene (Ctrl+S)\n" +
                "4. Build and test on Quest 3",
                "OK");
        }

        private static void RemovePoseEstimationComponents()
        {
            Debug.Log("[SETUP] Removing old PoseEstimation components...");

            // Find and remove PoseEstimationManager
            var poseManagers = GameObject.FindObjectsOfType<MonoBehaviour>();
            foreach (var component in poseManagers)
            {
                if (component.GetType().Name.Contains("PoseEstimation") ||
                    component.GetType().Name.Contains("PoseVisualizer"))
                {
                    Debug.Log($"[SETUP] Removing {component.GetType().Name} from {component.gameObject.name}");
                    GameObject.DestroyImmediate(component);
                }
            }

            // Remove any GameObjects with "Pose" in the name (except OVRCameraRig children)
            var allObjects = GameObject.FindObjectsOfType<GameObject>();
            foreach (var obj in allObjects)
            {
                if (obj.name.Contains("Pose") && !obj.name.Contains("OVR") && obj.transform.parent == null)
                {
                    Debug.Log($"[SETUP] Removing GameObject: {obj.name}");
                    GameObject.DestroyImmediate(obj);
                }
            }
        }

        private static GameObject SetupSegmentationManager()
        {
            Debug.Log("[SETUP] Setting up SegmentationInferenceManager...");

            GameObject obj = GameObject.Find("SegmentationManager");
            if (obj == null)
            {
                obj = new GameObject("SegmentationManager");
                Debug.Log("[SETUP] Created SegmentationManager GameObject");
            }

            var manager = obj.GetComponent<SegmentationInferenceManager>();
            if (manager == null)
            {
                manager = obj.AddComponent<SegmentationInferenceManager>();
                Debug.Log("[SETUP] Added SegmentationInferenceManager component");
            }

            // Configure default settings via SerializedObject
            SerializedObject so = new SerializedObject(manager);

            var serverUrlProp = so.FindProperty("m_serverUrl");
            if (serverUrlProp != null) serverUrlProp.stringValue = "http://192.168.0.135:8001/segmentation";

            var autoStartProp = so.FindProperty("m_autoStart");
            if (autoStartProp != null) autoStartProp.boolValue = true;

            var intervalProp = so.FindProperty("m_inferenceIntervalSeconds");
            if (intervalProp != null) intervalProp.floatValue = 0.2f;  // 5 FPS = 0.2s interval

            var verboseProp = so.FindProperty("m_verboseLogging");
            if (verboseProp != null) verboseProp.boolValue = true;

            so.ApplyModifiedProperties();

            Debug.Log("[SETUP] ✓ SegmentationManager configured");
            return obj;
        }

        private static GameObject SetupDepthCaptureManager()
        {
            Debug.Log("[SETUP] Setting up QuestDepthCaptureManager...");

            GameObject obj = GameObject.Find("QuestDepthCapture");
            if (obj == null)
            {
                obj = new GameObject("QuestDepthCapture");
                Debug.Log("[SETUP] Created QuestDepthCapture GameObject");
            }

            var depthCapture = obj.GetComponent<QuestDepthCaptureManager>();
            if (depthCapture == null)
            {
                depthCapture = obj.AddComponent<QuestDepthCaptureManager>();
                Debug.Log("[SETUP] Added QuestDepthCaptureManager component");
            }

            // Configure default settings via SerializedObject
            SerializedObject so = new SerializedObject(depthCapture);
            so.FindProperty("m_enableOnStart").boolValue = false; // Start disabled, manager will enable
            so.FindProperty("m_depthAvailableTimeout").floatValue = 2.0f;
            so.FindProperty("m_verboseLogging").boolValue = true;
            so.ApplyModifiedProperties();

            Debug.Log("[SETUP] ✓ QuestDepthCapture configured");
            return obj;
        }

        private static GameObject SetupPassthroughCameraAccess()
        {
            Debug.Log("[SETUP] Setting up PassthroughCameraAccess...");

            // First try to find existing PassthroughCameraAccess in scene
            GameObject obj = GameObject.Find("PassthroughCameraAccess");
            if (obj != null)
            {
                Debug.Log("[SETUP] ✓ Found existing PassthroughCameraAccess");
                return obj;
            }

            // Try to find by prefab instance
            var allObjects = GameObject.FindObjectsOfType<GameObject>();
            foreach (var go in allObjects)
            {
                if (go.name.Contains("PassthroughCameraAccess"))
                {
                    Debug.Log($"[SETUP] ✓ Found PassthroughCameraAccess: {go.name}");
                    return go;
                }
            }

            // Load and instantiate the prefab
            string prefabPath = "Assets/PassthroughCameraApiSamples/PassthroughCamera/Prefabs/PassthroughCameraAccessPrefab.prefab";
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);

            if (prefab != null)
            {
                obj = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                obj.name = "PassthroughCameraAccess";
                Debug.Log("[SETUP] ✓ Instantiated PassthroughCameraAccessPrefab");
                return obj;
            }
            else
            {
                Debug.LogError($"[SETUP] ✗ Could not load PassthroughCameraAccessPrefab at: {prefabPath}");
                Debug.LogWarning("[SETUP] You may need to add PassthroughCameraAccess manually.");
                return null;
            }
        }

        private static GameObject SetupOverlayRenderer()
        {
            Debug.Log("[SETUP] Setting up SegmentationOverlayRenderer...");

            // Find or create Canvas
            Canvas canvas = GameObject.FindObjectOfType<Canvas>();
            if (canvas == null || canvas.name.Contains("Pose"))
            {
                GameObject canvasObj = new GameObject("SegmentationOverlayCanvas");
                canvas = canvasObj.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.WorldSpace;
                canvasObj.AddComponent<UnityEngine.UI.CanvasScaler>();
                canvasObj.AddComponent<UnityEngine.UI.GraphicRaycaster>();

                // Position canvas 2 meters in front of origin
                canvasObj.transform.position = new Vector3(0, 1.5f, 2.0f);
                canvasObj.transform.localScale = new Vector3(0.01f, 0.01f, 0.01f);

                Debug.Log("[SETUP] Created SegmentationOverlayCanvas");
            }

            // Find or create RawImage for overlay
            GameObject overlayObj = null;
            foreach (Transform child in canvas.transform)
            {
                if (child.name.Contains("Overlay") || child.GetComponent<RawImage>() != null)
                {
                    overlayObj = child.gameObject;
                    break;
                }
            }

            if (overlayObj == null)
            {
                overlayObj = new GameObject("SegmentationOverlay");
                overlayObj.transform.SetParent(canvas.transform, false);
                Debug.Log("[SETUP] Created SegmentationOverlay GameObject");
            }

            // Add RawImage component
            RawImage rawImage = overlayObj.GetComponent<RawImage>();
            if (rawImage == null)
            {
                rawImage = overlayObj.AddComponent<RawImage>();
                Debug.Log("[SETUP] Added RawImage component");
            }

            // Configure RawImage
            RectTransform rect = overlayObj.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.sizeDelta = Vector2.zero;
            rect.anchoredPosition = Vector2.zero;

            // Add SegmentationOverlayRenderer
            var renderer = overlayObj.GetComponent<SegmentationOverlayRenderer>();
            if (renderer == null)
            {
                renderer = overlayObj.AddComponent<SegmentationOverlayRenderer>();
                Debug.Log("[SETUP] Added SegmentationOverlayRenderer component");
            }

            // Configure renderer via SerializedObject
            SerializedObject so = new SerializedObject(renderer);

            var overlayImageProp = so.FindProperty("m_overlayImage");
            if (overlayImageProp != null) overlayImageProp.objectReferenceValue = rawImage;

            var alphaProp = so.FindProperty("m_overlayAlpha");
            if (alphaProp != null) alphaProp.floatValue = 0.6f;

            var instanceColorsProp = so.FindProperty("m_useInstanceColors");
            if (instanceColorsProp != null) instanceColorsProp.boolValue = true;

            var fadeInProp = so.FindProperty("m_fadeInDuration");
            if (fadeInProp != null) fadeInProp.floatValue = 0.2f;

            var verboseLogProp = so.FindProperty("m_verboseLogging");
            if (verboseLogProp != null) verboseLogProp.boolValue = true;

            so.ApplyModifiedProperties();

            Debug.Log("[SETUP] ✓ SegmentationOverlay configured");
            return overlayObj;
        }

        private static void ConfigureReferences(
            GameObject segmentationManager,
            GameObject depthCaptureManager,
            GameObject overlayRenderer,
            GameObject cameraAccess)
        {
            Debug.Log("[SETUP] Configuring component references...");

            var manager = segmentationManager.GetComponent<SegmentationInferenceManager>();
            var depthCapture = depthCaptureManager.GetComponent<QuestDepthCaptureManager>();
            var renderer = overlayRenderer.GetComponent<SegmentationOverlayRenderer>();

            SerializedObject so = new SerializedObject(manager);

            var depthCaptureProp = so.FindProperty("m_depthCapture");
            if (depthCaptureProp != null) depthCaptureProp.objectReferenceValue = depthCapture;

            var rendererProp = so.FindProperty("m_overlayRenderer");
            if (rendererProp != null) rendererProp.objectReferenceValue = renderer;

            if (cameraAccess != null)
            {
                var cameraAccessComponent = cameraAccess.GetComponent<MonoBehaviour>();
                var cameraAccessProp = so.FindProperty("m_cameraAccess");
                if (cameraAccessProp != null) cameraAccessProp.objectReferenceValue = cameraAccessComponent;
            }

            so.ApplyModifiedProperties();

            Debug.Log("[SETUP] ✓ References configured");
        }

        [MenuItem("Tools/Segmentation/Validate Scene")]
        public static void ValidateScene()
        {
            Debug.Log("=== Segmentation Scene Validation ===");

            // Check for required components
            var manager = GameObject.FindObjectOfType<SegmentationInferenceManager>();
            var depthCapture = GameObject.FindObjectOfType<QuestDepthCaptureManager>();
            var renderer = GameObject.FindObjectOfType<SegmentationOverlayRenderer>();

            // Find PassthroughCameraAccess (may have different names)
            GameObject cameraAccess = GameObject.Find("PassthroughCameraAccess");
            if (cameraAccess == null)
            {
                var allObjects = GameObject.FindObjectsOfType<GameObject>();
                foreach (var go in allObjects)
                {
                    if (go.name.Contains("PassthroughCameraAccess"))
                    {
                        cameraAccess = go;
                        break;
                    }
                }
            }

            Debug.Log($"SegmentationInferenceManager: {(manager != null ? "✓ Found" : "✗ Missing")}");
            Debug.Log($"QuestDepthCaptureManager: {(depthCapture != null ? "✓ Found" : "✗ Missing")}");
            Debug.Log($"SegmentationOverlayRenderer: {(renderer != null ? "✓ Found" : "✗ Missing")}");
            Debug.Log($"PassthroughCameraAccess: {(cameraAccess != null ? $"✓ Found ({cameraAccess.name})" : "✗ Missing")}");

            if (manager != null)
            {
                SerializedObject so = new SerializedObject(manager);

                var serverUrlProp = so.FindProperty("m_serverUrl");
                if (serverUrlProp != null)
                    Debug.Log($"  Server URL: {serverUrlProp.stringValue}");

                var depthCaptureProp = so.FindProperty("m_depthCapture");
                if (depthCaptureProp != null)
                    Debug.Log($"  Depth Capture: {(depthCaptureProp.objectReferenceValue != null ? "✓ Connected" : "✗ Not connected")}");

                var rendererProp = so.FindProperty("m_overlayRenderer");
                if (rendererProp != null)
                    Debug.Log($"  Overlay Renderer: {(rendererProp.objectReferenceValue != null ? "✓ Connected" : "✗ Not connected")}");
            }

            Debug.Log("=== Validation Complete ===");
        }
    }
}
