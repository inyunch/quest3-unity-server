// Copyright (c) Meta Platforms, Inc. and affiliates.

#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;

namespace PassthroughCameraSamples.Shared.Editor
{
    /// <summary>
    /// Custom property drawer for InferenceConfig.
    /// Shows the effective URL when ServerConfig is enabled.
    /// </summary>
    [CustomPropertyDrawer(typeof(InferenceConfig))]
    public class InferenceConfigDrawer : PropertyDrawer
    {
        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            EditorGUI.BeginProperty(position, label, property);

            // Get properties
            var useServerConfigProp = property.FindPropertyRelative("useServerConfig");
            var baseUrlProp = property.FindPropertyRelative("baseUrl");
            var modeProp = property.FindPropertyRelative("mode");
            var includeMaskProp = property.FindPropertyRelative("includeMask");
            var includeDepthProp = property.FindPropertyRelative("includeDepth");
            var jpegQualityProp = property.FindPropertyRelative("jpegQuality");
            var targetFPSProp = property.FindPropertyRelative("targetFPS");

            // Start with foldout
            property.isExpanded = EditorGUI.Foldout(new Rect(position.x, position.y, position.width, EditorGUIUtility.singleLineHeight),
                property.isExpanded, label, true);

            if (!property.isExpanded)
            {
                EditorGUI.EndProperty();
                return;
            }

            EditorGUI.indentLevel++;

            float y = position.y + EditorGUIUtility.singleLineHeight + 8;
            float lineHeight = EditorGUIUtility.singleLineHeight + 16;  // Large spacing to prevent text overlap

            // Server Connection Header
            EditorGUI.LabelField(new Rect(position.x, y, position.width, EditorGUIUtility.singleLineHeight),
                "Server Connection", EditorStyles.boldLabel);
            y += lineHeight;

            // Use Server Config checkbox
            EditorGUI.PropertyField(new Rect(position.x, y, position.width, EditorGUIUtility.singleLineHeight),
                useServerConfigProp, new GUIContent("Use Server Config", "Use centralized ServerConfig for IP/port"));
            y += lineHeight;

            // Show effective URL box
            if (useServerConfigProp.boolValue)
            {
                // Show info box with effective URL
                var serverConfig = ServerConfig.Instance;
                if (serverConfig != null)
                {
                    // Calculate effective URL
                    string modeStr = GetModeString(modeProp.enumValueIndex);
                    bool actualIncludeDepth = (modeProp.enumValueIndex == 3) || includeDepthProp.boolValue;
                    string effectiveUrl = $"{serverConfig.InferenceUrl}?mode={modeStr}&include_mask={includeMaskProp.boolValue.ToString().ToLower()}&include_depth={actualIncludeDepth.ToString().ToLower()}";

                    // Info box
                    float boxHeight = EditorGUIUtility.singleLineHeight * 3 + 10;
                    Rect boxRect = new Rect(position.x, y, position.width, boxHeight);
                    EditorGUI.HelpBox(boxRect,
                        $"✓ Using ServerConfig\n" +
                        $"IP: {serverConfig.ServerIP}:{serverConfig.ServerPort}\n" +
                        $"Effective URL: {effectiveUrl}",
                        MessageType.Info);
                    y += boxHeight + 5;

                    // Button to open ServerConfig editor
                    if (GUI.Button(new Rect(position.x, y, position.width, EditorGUIUtility.singleLineHeight),
                        "Open Server Config Editor"))
                    {
                        ServerConfigEditor.ShowWindow();
                    }
                    y += lineHeight;
                }
                else
                {
                    EditorGUI.HelpBox(new Rect(position.x, y, position.width, EditorGUIUtility.singleLineHeight * 2),
                        "⚠ ServerConfig asset not found!\nCreate one: Tools → Server Config Editor",
                        MessageType.Warning);
                    y += lineHeight * 2 + 5;
                }

                // Gray out baseUrl when using ServerConfig
                EditorGUI.BeginDisabledGroup(true);
                EditorGUI.PropertyField(new Rect(position.x, y, position.width, EditorGUIUtility.singleLineHeight),
                    baseUrlProp, new GUIContent("Base Url (ignored)", "This field is ignored when Use Server Config is enabled"));
                EditorGUI.EndDisabledGroup();
                y += lineHeight;
            }
            else
            {
                // Show baseUrl normally when NOT using ServerConfig
                EditorGUI.PropertyField(new Rect(position.x, y, position.width, EditorGUIUtility.singleLineHeight),
                    baseUrlProp, new GUIContent("Base Url", "Manual URL (only used when Use Server Config is disabled)"));
                y += lineHeight;

                // Warning if still using old hardcoded IP
                if (baseUrlProp.stringValue.Contains("192.168.0.135") || baseUrlProp.stringValue.Contains("127.0.0.1"))
                {
                    EditorGUI.HelpBox(new Rect(position.x, y, position.width, EditorGUIUtility.singleLineHeight * 2),
                        "💡 Tip: Enable 'Use Server Config' above to use centralized IP management",
                        MessageType.Info);
                    y += lineHeight * 2 + 5;
                }
            }

            // Inference Mode Header
            y += 20; // Large spacing before section
            EditorGUI.LabelField(new Rect(position.x, y, position.width, EditorGUIUtility.singleLineHeight),
                "Inference Mode", EditorStyles.boldLabel);
            y += lineHeight;

            EditorGUI.PropertyField(new Rect(position.x, y, position.width, EditorGUIUtility.singleLineHeight),
                modeProp);
            y += lineHeight + 10; // Extra spacing after field

            // Optional Features Header
            y += 20; // Large spacing before section
            EditorGUI.LabelField(new Rect(position.x, y, position.width, EditorGUIUtility.singleLineHeight),
                "Optional Features", EditorStyles.boldLabel);
            y += lineHeight;

            EditorGUI.PropertyField(new Rect(position.x, y, position.width, EditorGUIUtility.singleLineHeight),
                includeMaskProp, new GUIContent("Include Mask", "Include segmentation mask in response (adds ~75KB download)"));
            y += lineHeight + 8;  // Extra spacing between fields

            EditorGUI.PropertyField(new Rect(position.x, y, position.width, EditorGUIUtility.singleLineHeight),
                includeDepthProp, new GUIContent("Include Depth", "Include depth map in response (adds ~300KB download)"));
            y += lineHeight + 10; // Extra spacing after field

            // Upload Optimization Header
            y += 20; // Large spacing before section
            EditorGUI.LabelField(new Rect(position.x, y, position.width, EditorGUIUtility.singleLineHeight),
                "Upload Optimization", EditorStyles.boldLabel);
            y += lineHeight;

            EditorGUI.PropertyField(new Rect(position.x, y, position.width, EditorGUIUtility.singleLineHeight),
                jpegQualityProp, new GUIContent("JPEG Quality", "JPEG quality for image upload (60-100)"));
            y += lineHeight + 10; // Extra spacing after field

            // FPS Configuration Header
            y += 20; // Large spacing before section
            EditorGUI.LabelField(new Rect(position.x, y, position.width, EditorGUIUtility.singleLineHeight),
                "FPS Configuration", EditorStyles.boldLabel);
            y += lineHeight;

            EditorGUI.PropertyField(new Rect(position.x, y, position.width, EditorGUIUtility.singleLineHeight),
                targetFPSProp, new GUIContent("Target FPS", "Target inference FPS for this mode (1-30)"));
            y += lineHeight + 10; // Extra spacing after field

            EditorGUI.indentLevel--;
            EditorGUI.EndProperty();
        }

        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
        {
            if (!property.isExpanded)
                return EditorGUIUtility.singleLineHeight;

            float height = EditorGUIUtility.singleLineHeight; // Foldout
            float lineHeight = EditorGUIUtility.singleLineHeight + 16;  // Match OnGUI spacing

            // Server Connection Header + field
            height += lineHeight; // Header
            height += lineHeight; // Use Server Config checkbox

            var useServerConfigProp = property.FindPropertyRelative("useServerConfig");
            if (useServerConfigProp.boolValue)
            {
                // Add extra space for ServerConfig info box and button
                height += (EditorGUIUtility.singleLineHeight * 3 + 10 + 5); // Info box
                height += lineHeight; // Button
                height += lineHeight; // Base URL (grayed out)
            }
            else
            {
                height += lineHeight; // Base URL field

                var baseUrlProp = property.FindPropertyRelative("baseUrl");
                if (baseUrlProp.stringValue.Contains("192.168.0.135") || baseUrlProp.stringValue.Contains("127.0.0.1"))
                {
                    height += (EditorGUIUtility.singleLineHeight * 2 + 5); // Warning box
                }
            }

            // Inference Mode section (header + field + spacing)
            height += 20; // Spacing before section
            height += lineHeight; // Header
            height += lineHeight + 10; // Mode field + spacing

            // Optional Features section (header + 2 fields + spacing)
            height += 20; // Spacing before section
            height += lineHeight; // Header
            height += lineHeight + 8; // Include Mask + extra spacing
            height += lineHeight + 10; // Include Depth + spacing

            // Upload Optimization section (header + field + spacing)
            height += 20; // Spacing before section
            height += lineHeight; // Header
            height += lineHeight + 10; // JPEG Quality + spacing

            // FPS Configuration section (header + field + spacing)
            height += 20; // Spacing before section
            height += lineHeight; // Header
            height += lineHeight + 10; // Target FPS + spacing

            return height;
        }

        private string GetModeString(int modeIndex)
        {
            switch (modeIndex)
            {
                case 0: return "detection";
                case 1: return "pose";
                case 2: return "both";
                case 3: return "depth";
                case 4: return "segmentation";
                case 5: return "seg_depth";
                default: return "detection";
            }
        }
    }
}
#endif
