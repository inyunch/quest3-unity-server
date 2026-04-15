using UnityEngine;
using UnityEditor;
using PassthroughCameraSamples.Segmentation;
using PassthroughCameraSamples.Shared;

/// <summary>
/// Debug tool to inspect Segmentation telemetry state
/// </summary>
public class DebugSegmentationTelemetry : EditorWindow
{
    [MenuItem("Tools/Debug Segmentation Telemetry")]
    public static void ShowWindow()
    {
        GetWindow<DebugSegmentationTelemetry>("Segmentation Telemetry Debug");
    }

    private void OnGUI()
    {
        GUILayout.Label("Segmentation Telemetry Debug", EditorStyles.boldLabel);

        if (GUILayout.Button("Check SegmentationInferenceRunManager State"))
        {
            CheckSegmentationState();
        }

        if (GUILayout.Button("Force Unity Reimport All"))
        {
            AssetDatabase.ImportAsset("Assets", ImportAssetOptions.ImportRecursive | ImportAssetOptions.ForceUpdate);
            Debug.Log("[DEBUG] Forced reimport of all assets");
        }
    }

    private void CheckSegmentationState()
    {
        // Find SegmentationInferenceRunManager in the scene
        SegmentationInferenceRunManager manager = FindObjectOfType<SegmentationInferenceRunManager>();

        if (manager == null)
        {
            Debug.LogError("[DEBUG] SegmentationInferenceRunManager NOT FOUND in current scene!");
            Debug.LogError("[DEBUG] Make sure Segmentation.unity scene is loaded and active");
            return;
        }

        Debug.Log($"[DEBUG] ===== SegmentationInferenceRunManager Found =====");
        Debug.Log($"[DEBUG] GameObject: {manager.gameObject.name}");
        Debug.Log($"[DEBUG] Enabled: {manager.enabled}");
        Debug.Log($"[DEBUG] IsActiveAndEnabled: {manager.isActiveAndEnabled}");

        // Use reflection to inspect private fields
        var type = typeof(SegmentationInferenceRunManager);

        // Check m_lastCompletedTrace
        var lastTraceField = type.GetField("m_lastCompletedTrace",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        if (lastTraceField != null)
        {
            var lastTrace = lastTraceField.GetValue(manager) as FrameTrace;
            Debug.Log($"[DEBUG] m_lastCompletedTrace: {(lastTrace == null ? "NULL" : "NOT NULL")}");
            if (lastTrace != null)
            {
                Debug.Log($"[DEBUG]   - frame_id: {lastTrace.frame_id}");
                Debug.Log($"[DEBUG]   - session_id: {lastTrace.session_id}");
                Debug.Log($"[DEBUG]   - state: {lastTrace.state}");
                Debug.Log($"[DEBUG]   - unity_send_ts: {lastTrace.unity_send_ts}");
                Debug.Log($"[DEBUG]   - unity_receive_ts: {lastTrace.unity_receive_ts}");
                Debug.Log($"[DEBUG]   - unity_display_ts: {lastTrace.unity_display_ts}");
                Debug.Log($"[DEBUG]   - server_receive_ts: {lastTrace.server_receive_ts}");
                Debug.Log($"[DEBUG]   - server_send_ts: {lastTrace.server_send_ts}");
            }
        }
        else
        {
            Debug.LogError("[DEBUG] Could not find m_lastCompletedTrace field via reflection!");
        }

        // Check m_frameId
        var frameIdField = type.GetField("m_frameId",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        if (frameIdField != null)
        {
            var frameId = (int)frameIdField.GetValue(manager);
            Debug.Log($"[DEBUG] m_frameId: {frameId}");
        }

        // Check m_sessionId
        var sessionIdField = type.GetField("m_sessionId",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        if (sessionIdField != null)
        {
            var sessionId = (string)sessionIdField.GetValue(manager);
            Debug.Log($"[DEBUG] m_sessionId: {sessionId}");
        }

        // Check m_frameTraces collection
        var frameTracesField = type.GetField("m_frameTraces",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        if (frameTracesField != null)
        {
            var frameTraces = frameTracesField.GetValue(manager);
            if (frameTraces is System.Collections.ICollection collection)
            {
                Debug.Log($"[DEBUG] m_frameTraces count: {collection.Count}");
            }
        }

        Debug.Log($"[DEBUG] ===== End Debug =====");
    }
}
