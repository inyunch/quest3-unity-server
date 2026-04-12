using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;

/// <summary>
/// Removes SentisInferenceManagerPrefab from Segmentation scene
/// Menu: Tools/Segmentation/Remove Old Sentis Prefab
/// </summary>
public class RemoveSentisInferencePrefab : MonoBehaviour
{
    [MenuItem("Tools/Segmentation/Remove Old Sentis Prefab")]
    public static void RemovePrefab()
    {
        Debug.Log("[REMOVE PREFAB] Starting removal of SentisInferenceManagerPrefab...");

        // Verify we're in the Segmentation scene
        var activeScene = EditorSceneManager.GetActiveScene();
        if (!activeScene.name.Contains("Segmentation"))
        {
            EditorUtility.DisplayDialog(
                "Wrong Scene",
                $"This tool only works in the Segmentation scene.\nCurrent scene: {activeScene.name}",
                "OK"
            );
            Debug.LogError($"[REMOVE PREFAB] Wrong scene: {activeScene.name}");
            return;
        }

        Debug.Log($"[REMOVE PREFAB] Active scene: {activeScene.name}");

        // Find all GameObjects in scene
        var allObjects = Object.FindObjectsOfType<GameObject>(true); // Include inactive
        int removedCount = 0;

        foreach (var obj in allObjects)
        {
            // Check if this object is from SentisInferenceManagerPrefab
            var prefabInstance = PrefabUtility.GetPrefabInstanceHandle(obj);
            if (prefabInstance != null)
            {
                var prefabAsset = PrefabUtility.GetCorrespondingObjectFromSource(obj);
                if (prefabAsset != null)
                {
                    string prefabPath = AssetDatabase.GetAssetPath(prefabAsset);
                    if (prefabPath.Contains("SentisInferenceManagerPrefab"))
                    {
                        Debug.Log($"[REMOVE PREFAB] Found SentisInferenceManagerPrefab instance: {obj.name}");
                        Debug.Log($"[REMOVE PREFAB] Prefab path: {prefabPath}");
                        Debug.Log($"[REMOVE PREFAB] Deleting GameObject...");

                        // Delete the GameObject
                        Object.DestroyImmediate(obj);
                        removedCount++;
                    }
                }
            }
        }

        if (removedCount > 0)
        {
            Debug.Log($"[REMOVE PREFAB] Removed {removedCount} SentisInferenceManagerPrefab instance(s)");

            // Mark scene dirty and save
            EditorSceneManager.MarkSceneDirty(activeScene);
            EditorSceneManager.SaveScene(activeScene);

            Debug.Log("[REMOVE PREFAB] ✅ Scene saved successfully!");

            EditorUtility.DisplayDialog(
                "Prefab Removed",
                $"Successfully removed {removedCount} SentisInferenceManagerPrefab instance(s).\n\n" +
                "Scene has been saved.\n\n" +
                "Please rebuild and deploy to Quest 3.",
                "OK"
            );
        }
        else
        {
            Debug.LogWarning("[REMOVE PREFAB] No SentisInferenceManagerPrefab instances found in scene");
            EditorUtility.DisplayDialog(
                "Not Found",
                "No SentisInferenceManagerPrefab instances found in the scene.",
                "OK"
            );
        }
    }
}
