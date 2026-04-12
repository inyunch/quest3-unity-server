// Copyright (c) Meta Platforms, Inc. and affiliates.

using System.Collections.Generic;
using UnityEngine;
using PassthroughCameraSamples.Shared;
using PassthroughCameraSamples.MultiObjectDetection;
using Meta.XR;

namespace PassthroughCameraSamples.DepthEstimation
{
    /// <summary>
    /// Displays text labels above detected objects showing depth information.
    /// Format: "class_name | depth: 0.73m"
    /// </summary>
    public class DepthLabelManager : MonoBehaviour
    {
        [Header("Label Settings")]
        [SerializeField, Range(0.01f, 0.5f)] private float m_labelOffsetY = 0.15f;  // Offset above bbox center
        [SerializeField, Range(0.01f, 0.2f)] private float m_labelScale = 0.05f;
        [SerializeField] private Color m_labelColor = Color.white;
        [SerializeField] private Font m_labelFont;

        [Header("References")]
        [SerializeField] private Component m_cameraAccess;  // PassthroughCameraAccess - using Component for cross-assembly
        [SerializeField] private Component m_environmentRaycast;  // EnvironmentRayCastSampleManager - optional

        [Header("Performance")]
        [SerializeField] private bool m_useObjectPooling = true;
        [SerializeField] private int m_maxLabels = 20;

        // Object pooling
        private readonly List<GameObject> m_labelPool = new();
        private readonly List<GameObject> m_activeLabels = new();

        private void Start()
        {
            Debug.Log("[DEPTH LABELS] DepthLabelManager initialized");
            Debug.Log($"[DEPTH LABELS] Settings: labelOffset={m_labelOffsetY}, scale={m_labelScale}, maxLabels={m_maxLabels}");

            // Auto-find PassthroughCameraAccess if not assigned
            if (m_cameraAccess == null)
            {
                Debug.LogWarning("[DEPTH LABELS] PassthroughCameraAccess not assigned, attempting to find...");

                // Try to find in parent's siblings
                var parent = transform.parent;
                if (parent != null)
                {
                    // Search parent and siblings
                    foreach (Transform sibling in parent)
                    {
                        var comp = sibling.GetComponent<Component>();
                        if (comp != null && comp.GetType().Name == "PassthroughCameraAccess")
                        {
                            m_cameraAccess = comp;
                            Debug.Log($"[DEPTH LABELS] Auto-found PassthroughCameraAccess on: {sibling.name}");
                            break;
                        }
                    }
                }

                // If still not found, search entire scene
                if (m_cameraAccess == null)
                {
                    var allObjects = GameObject.FindObjectsOfType<Component>();
                    foreach (var comp in allObjects)
                    {
                        if (comp != null && comp.GetType().Name == "PassthroughCameraAccess")
                        {
                            m_cameraAccess = comp;
                            Debug.Log($"[DEPTH LABELS] Auto-found PassthroughCameraAccess on: {comp.gameObject.name}");
                            break;
                        }
                    }
                }

                if (m_cameraAccess == null)
                {
                    Debug.LogError("[DEPTH LABELS] PassthroughCameraAccess reference is missing and could not be found!");
                }
            }
            else
            {
                Debug.Log("[DEPTH LABELS] PassthroughCameraAccess reference OK");
            }

            // Note: EnvironmentRayCastSampleManager is no longer required - we use server depth directly
            if (m_environmentRaycast == null)
            {
                Debug.LogWarning("[DEPTH LABELS] EnvironmentRayCastSampleManager not assigned (not required for depth mode)");
            }
        }

        /// <summary>
        /// Draws text labels above detected objects with depth information.
        /// </summary>
        public void DrawDepthLabels(
            DepthInferenceRunManager.Detection[] detections,
            Pose cameraPose)
        {
            if (detections == null || detections.Length == 0)
            {
                Debug.Log("[DEPTH LABELS] No detections to display");
                ClearLabels();
                return;
            }

            Debug.Log($"[DEPTH LABELS] Drawing labels for {detections.Length} objects");

            // Clear previous labels
            ClearLabels();

            // Validate camera access reference
            if (m_cameraAccess == null)
            {
                Debug.LogError("[DEPTH LABELS] PassthroughCameraAccess is missing! Cannot create labels.");
                return;
            }

            int labelsCreated = 0;

            foreach (var det in detections)
            {
                if (labelsCreated >= m_maxLabels)
                {
                    Debug.LogWarning($"[DEPTH LABELS] Reached max labels ({m_maxLabels}), skipping remaining");
                    break;
                }

                // Skip detections without depth estimate
                if (det.depth_estimate == null)
                {
                    Debug.LogWarning($"[DEPTH LABELS] {det.class_name} has no depth estimate, skipping");
                    continue;
                }

                // Calculate bbox center in normalized coordinates [0-1]
                float centerX = (det.bbox[0] + det.bbox[2]) / 2.0f;
                float centerY = (det.bbox[1] + det.bbox[3]) / 2.0f;

                Vector2 normalizedPos = new Vector2(centerX, 1.0f - centerY);  // Flip Y

                // Use server-provided depth estimate
                float depth = det.depth_estimate.depth_m;
                string depthSource = det.depth_estimate.depth_source;

                Debug.Log($"[DEPTH LABELS] {det.class_name}: bbox_center=({centerX:F2},{centerY:F2}), depth={depth:F3}m, source={depthSource}");

                // Project from camera space to world space using server depth
                Ray ray;
                try
                {
                    // Use reflection to call ViewportPointToRay since m_cameraAccess is Component type
                    var method = m_cameraAccess.GetType().GetMethod("ViewportPointToRay");
                    if (method != null)
                    {
                        ray = (Ray)method.Invoke(m_cameraAccess, new object[] { normalizedPos, cameraPose });
                    }
                    else
                    {
                        Debug.LogError($"[DEPTH LABELS] ViewportPointToRay method not found on {m_cameraAccess.GetType().Name}");
                        continue;
                    }
                }
                catch (System.Exception e)
                {
                    Debug.LogError($"[DEPTH LABELS] Failed to call ViewportPointToRay: {e.Message}");
                    continue;
                }

                Vector3 worldPos = ray.origin + ray.direction * depth;

                Debug.Log($"[DEPTH LABELS] {det.class_name}: ray_origin={ray.origin}, ray_dir={ray.direction}, world_pos={worldPos}");

                // Offset label above the detected object
                Vector3 labelPos = worldPos + Vector3.up * m_labelOffsetY;

                // Create label text with depth source
                string labelText = $"{det.class_name} | {depth:F2}m ({depthSource})";

                // Get or create label GameObject
                GameObject labelObj = GetLabelFromPool();
                labelObj.transform.position = labelPos;
                labelObj.transform.localScale = Vector3.one * m_labelScale;

                // Make label face camera
                labelObj.transform.LookAt(cameraPose.position);
                labelObj.transform.Rotate(0, 180, 0);  // Flip to face camera

                // Update text
                TextMesh textMesh = labelObj.GetComponent<TextMesh>();
                if (textMesh != null)
                {
                    textMesh.text = labelText;
                    textMesh.color = m_labelColor;
                    textMesh.anchor = TextAnchor.MiddleCenter;
                    if (m_labelFont != null)
                    {
                        textMesh.font = m_labelFont;
                    }
                }

                labelObj.SetActive(true);
                m_activeLabels.Add(labelObj);
                labelsCreated++;

                Debug.Log($"[DEPTH LABELS] {det.class_name}: depth={depth:F2}m, source={depthSource}, confidence={det.depth_estimate.depth_confidence:F2}, pos={labelPos}");
            }

            Debug.Log($"[DEPTH LABELS] Created {labelsCreated} labels");
        }

        /// <summary>
        /// Clears all active labels.
        /// </summary>
        public void ClearLabels()
        {
            int clearedCount = m_activeLabels.Count;

            if (m_useObjectPooling)
            {
                foreach (var label in m_activeLabels)
                {
                    if (label != null)
                    {
                        label.SetActive(false);
                        m_labelPool.Add(label);
                    }
                }
            }
            else
            {
                foreach (var label in m_activeLabels)
                {
                    if (label != null)
                    {
                        Destroy(label);
                    }
                }
            }

            m_activeLabels.Clear();

            if (clearedCount > 0)
            {
                Debug.Log($"[DEPTH LABELS CLEANUP] Cleared {clearedCount} labels");
            }
        }

        private GameObject GetLabelFromPool()
        {
            if (m_useObjectPooling && m_labelPool.Count > 0)
            {
                var label = m_labelPool[m_labelPool.Count - 1];
                m_labelPool.RemoveAt(m_labelPool.Count - 1);
                return label;
            }

            // Create new label GameObject with TextMesh
            GameObject labelObj = new GameObject("DepthLabel");
            labelObj.transform.SetParent(transform);

            TextMesh textMesh = labelObj.AddComponent<TextMesh>();
            textMesh.fontSize = 80;
            textMesh.characterSize = 0.5f;  // Increased from 0.1f to prevent text crowding
            textMesh.lineSpacing = 1.2f;    // Add line spacing for multi-line text
            textMesh.anchor = TextAnchor.MiddleCenter;
            textMesh.alignment = TextAlignment.Center;
            textMesh.color = m_labelColor;

            if (m_labelFont != null)
            {
                textMesh.font = m_labelFont;
            }

            // Add MeshRenderer for visibility
            MeshRenderer renderer = labelObj.GetComponent<MeshRenderer>();
            if (renderer != null && textMesh.font != null)
            {
                renderer.material = textMesh.font.material;
            }

            return labelObj;
        }

        private void OnDisable()
        {
            ClearLabels();
        }

        private void OnDestroy()
        {
            // Cleanup pools
            foreach (var label in m_labelPool)
            {
                if (label != null) Destroy(label);
            }
            m_labelPool.Clear();

            foreach (var label in m_activeLabels)
            {
                if (label != null) Destroy(label);
            }
            m_activeLabels.Clear();
        }

        internal Transform ContentParent => transform;
    }
}
