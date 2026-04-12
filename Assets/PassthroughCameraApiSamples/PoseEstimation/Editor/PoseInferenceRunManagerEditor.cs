// Copyright (c) Meta Platforms, Inc. and affiliates.

#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;

namespace PassthroughCameraSamples.PoseEstimation.Editor
{
    /// <summary>
    /// Custom editor for PoseInferenceRunManager to improve Inspector spacing.
    /// </summary>
    [CustomEditor(typeof(PoseInferenceRunManager))]
    public class PoseInferenceRunManagerEditor : UnityEditor.Editor
    {
        private SerializedProperty m_cameraAccess;
        private SerializedProperty m_uiMenuManager;
        private SerializedProperty m_poseManager;
        private SerializedProperty m_uiPose;
        private SerializedProperty m_inferenceHUD;
        private SerializedProperty m_sharedHUD;
        private SerializedProperty m_inferenceConfig;
        private SerializedProperty m_minKeypointScore;
        private SerializedProperty m_serverUrl;
        private SerializedProperty m_jpegQuality;

        private void OnEnable()
        {
            m_cameraAccess = serializedObject.FindProperty("m_cameraAccess");
            m_uiMenuManager = serializedObject.FindProperty("m_uiMenuManager");
            m_poseManager = serializedObject.FindProperty("m_poseManager");
            m_uiPose = serializedObject.FindProperty("m_uiPose");
            m_inferenceHUD = serializedObject.FindProperty("m_inferenceHUD");
            m_sharedHUD = serializedObject.FindProperty("m_sharedHUD");
            m_inferenceConfig = serializedObject.FindProperty("m_inferenceConfig");
            m_minKeypointScore = serializedObject.FindProperty("m_minKeypointScore");
            m_serverUrl = serializedObject.FindProperty("m_serverUrl");
            m_jpegQuality = serializedObject.FindProperty("m_jpegQuality");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            // Basic references
            EditorGUILayout.Space(5);
            EditorGUILayout.LabelField("Core References", EditorStyles.boldLabel);
            EditorGUILayout.Space(8);
            EditorGUILayout.PropertyField(m_cameraAccess);
            EditorGUILayout.Space(5);
            EditorGUILayout.PropertyField(m_uiMenuManager);
            EditorGUILayout.Space(5);
            EditorGUILayout.PropertyField(m_poseManager);

            EditorGUILayout.Space(20);

            // UI display references
            EditorGUILayout.LabelField("UI Display References", EditorStyles.boldLabel);
            EditorGUILayout.Space(8);
            EditorGUILayout.PropertyField(m_uiPose);
            EditorGUILayout.Space(5);
            EditorGUILayout.PropertyField(m_inferenceHUD);
            EditorGUILayout.Space(5);
            EditorGUILayout.PropertyField(m_sharedHUD);

            EditorGUILayout.Space(20);

            // Server Inference (NEW)
            EditorGUILayout.LabelField("Server Inference (NEW)", EditorStyles.boldLabel);
            EditorGUILayout.Space(8);
            EditorGUILayout.PropertyField(m_inferenceConfig);
            EditorGUILayout.Space(10);
            EditorGUILayout.PropertyField(m_minKeypointScore, new GUIContent("Min Keypoint Score"));

            EditorGUILayout.Space(20);

            // Legacy Server Inference (DEPRECATED)
            EditorGUILayout.LabelField("Legacy Server Inference (DEPRECATED)", EditorStyles.boldLabel);
            EditorGUILayout.Space(8);
            EditorGUI.BeginDisabledGroup(true);
            EditorGUILayout.HelpBox("These fields are deprecated. Please use 'Inference Config' above.", MessageType.Warning);
            EditorGUILayout.Space(5);
            EditorGUILayout.PropertyField(m_serverUrl, new GUIContent("Server URL (deprecated)"));
            EditorGUILayout.Space(5);
            EditorGUILayout.PropertyField(m_jpegQuality, new GUIContent("JPEG Quality (deprecated)"));
            EditorGUI.EndDisabledGroup();

            serializedObject.ApplyModifiedProperties();
        }
    }
}
#endif

