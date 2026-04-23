// Copyright (c) Meta Platforms, Inc. and affiliates.
// Fixed variable naming conflicts

using System.Collections.Generic;
using Meta.XR;
using Meta.XR.Samples;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace PassthroughCameraSamples.Segmentation
{
    [MetaCodeSample("PassthroughCameraApiSamples-Segmentation")]
    public class SegmentationInferenceUiManager : MonoBehaviour
    {
        [Header("Placement configuration")]
        [SerializeField] private EnvironmentRayCastSampleManager m_environmentRaycast;
        [SerializeField] private PassthroughCameraAccess m_cameraAccess;

        [SerializeField] private RectTransform m_detectionBoxPrefab;
        [SerializeField] private RectTransform m_maskOverlayPrefab;  // Prefab for mask rendering
        [Space(10)]
        public UnityEvent<int> OnObjectsDetected;

        internal readonly List<BoundingBoxData> m_boxDrawn = new();
        private string[] m_labels;
        private readonly List<BoundingBoxData> m_boxPool = new();

        // Mask rendering support
        private readonly List<MaskData> m_masksDrawn = new();
        private readonly List<MaskData> m_maskPool = new();

        internal class BoundingBoxData
        {
            public string ClassName;
            public int ClassId;
            public RectTransform BoxRectTransform;
            public float lastUpdateTime;
        }

        internal class MaskData
        {
            public int MaskId;
            public RectTransform MaskRectTransform;  // For single-quad rendering
            public UnityEngine.UI.RawImage MaskImage;
            public Texture2D MaskTexture;
            public float lastUpdateTime;

            // Multi-sample mode for better depth conformance
            public List<GameObject> SamplePoints;  // Multiple depth-sampled quads
            public bool UseMultiSample;
        }

        private void Awake()
        {
            m_detectionBoxPrefab.gameObject.SetActive(false);
            if (m_maskOverlayPrefab != null)
            {
                m_maskOverlayPrefab.gameObject.SetActive(false);
            }
        }

        private void Update()
        {
            // Remove boxes that haven't been updated recently
            for (int i = m_boxDrawn.Count - 1; i >= 0; i--)
            {
                var box = m_boxDrawn[i];
                const float timeToPersistBoxes = 3f;
                if (Time.time - box.lastUpdateTime > timeToPersistBoxes)
                {
                    ReturnToPool(box);
                    m_boxDrawn.RemoveAt(i);
                }
            }

            // Remove masks that haven't been updated recently
            for (int i = m_masksDrawn.Count - 1; i >= 0; i--)
            {
                var mask = m_masksDrawn[i];
                const float timeToPersistMasks = 0.2f;  // Very aggressive cleanup for point clouds
                if (Time.time - mask.lastUpdateTime > timeToPersistMasks)
                {
                    Debug.LogError($"[MASK CLEANUP] Removing old mask {mask.MaskId}, age={(Time.time - mask.lastUpdateTime):F2}s");
                    ReturnMaskToPool(mask);
                    m_masksDrawn.RemoveAt(i);
                }
            }
        }

        public void SetLabels(TextAsset labelsAsset)
        {
            // Parse neural net labels
            m_labels = labelsAsset.text.Split('\n');
        }

        public void DrawUIBoxes(List<(int classId, Vector4 boundingBox)> detections, Vector2 inputSize, Pose cameraPose)
        {
            Vector2 currentResolution = m_cameraAccess.CurrentResolution;

            if (detections.Count == 0)
            {
                OnObjectsDetected?.Invoke(0);
                return;
            }

            OnObjectsDetected?.Invoke(detections.Count);

            // Draw the bounding boxes
            for (var i = 0; i < detections.Count; i++)
            {
                var detection = detections[i];
                float x1 = detection.boundingBox[0];
                float y1 = detection.boundingBox[1];
                float x2 = detection.boundingBox[2];
                float y2 = detection.boundingBox[3];
                Rect rect = new Rect(x1, y1, x2 - x1, y2 - y1);
                // Rect rect = Rect.MinMaxRect(x1, y1, x2, y2); // todo

                Vector2 normalizedCenter = rect.center / inputSize;
                Vector2 center = currentResolution * (normalizedCenter - Vector2.one * 0.5f);

                // Get the object class name
                string classname = "person";  // Default
                if (m_labels != null && detection.classId >= 0 && detection.classId < m_labels.Length)
                {
                    classname = m_labels[detection.classId].Replace(" ", "_");
                }
                else
                {
                    Debug.LogWarning($"[SEGMENTATION UI] m_labels not initialized or classId {detection.classId} out of range, using default 'person'");
                }

                // Get the 3D marker world position using Depth Raycast
                var ray = m_cameraAccess.ViewportPointToRay(new Vector2(normalizedCenter.x, 1.0f - normalizedCenter.y), cameraPose);
                var worldPos = m_environmentRaycast?.Raycast(ray);

                // If raycast fails, use a default distance of 2 meters
                Vector3 actualWorldPos;
                if (!worldPos.HasValue)
                {
                    Debug.LogWarning($"[SEGMENTATION UI] Raycast failed for detection {i}, using default distance of 2m");
                    actualWorldPos = ray.GetPoint(2.0f);  // Default 2 meters in front of camera
                }
                else
                {
                    actualWorldPos = worldPos.Value;
                }
                var normRect = new Rect(
                    rect.x / inputSize.x,
                    1f - rect.yMax / inputSize.y,
                    rect.width / inputSize.x,
                    rect.height / inputSize.y
                );

                // Calculate distance and center point first
                float distance = Vector3.Distance(cameraPose.position, actualWorldPos);
                var worldSpaceCenter = m_cameraAccess.ViewportPointToRay(normRect.center, cameraPose).GetPoint(distance);
                var normal = (worldSpaceCenter - cameraPose.position).normalized;

                // Intersect corner rays with the plane perpendicular to the camera view
                var plane = new Plane(normal, worldSpaceCenter);
                var minRay = m_cameraAccess.ViewportPointToRay(normRect.min, cameraPose);
                var maxRay = m_cameraAccess.ViewportPointToRay(normRect.max, cameraPose);
                plane.Raycast(minRay, out float intersectionDistanceMin);
                plane.Raycast(maxRay, out float intersectionDistanceMax);
                var min = minRay.GetPoint(intersectionDistanceMin);
                var max = maxRay.GetPoint(intersectionDistanceMax);

                // Transform world-space positions to camera's local space to get 2D size
                var topLeftLocal = Quaternion.Inverse(cameraPose.rotation) * (min - cameraPose.position);
                var bottomRightLocal = Quaternion.Inverse(cameraPose.rotation) * (max - cameraPose.position);
                var size = new Vector2(
                    Mathf.Abs(bottomRightLocal.x - topLeftLocal.x),
                    Mathf.Abs(bottomRightLocal.y - topLeftLocal.y));

                var boxData = GetOrCreateBoundingBoxData(detection.classId, worldSpaceCenter, size);
                var boxRectTransform = boxData.BoxRectTransform;
                boxRectTransform.GetComponentInChildren<Text>().text = $"Id: {detection.classId} Class: {classname} Center (px): {center:0.0} Center (%): {normalizedCenter:0.0}";
                boxRectTransform.SetPositionAndRotation(worldSpaceCenter, Quaternion.LookRotation(normal));
                boxRectTransform.sizeDelta = size;
                boxData.lastUpdateTime = Time.time;
            }
        }

        private BoundingBoxData GetOrCreateBoundingBoxData(int classId, Vector3 worldSpaceCenter, Vector2 worldSpaceSize)
        {
            BoundingBoxData reusedBox = null;
            for (int i = m_boxDrawn.Count - 1; i >= 0; i--)
            {
                var box = m_boxDrawn[i];
                var localPos = box.BoxRectTransform.InverseTransformPoint(worldSpaceCenter);
                var newBox = new Vector4(
                    localPos.x - worldSpaceSize.x * 0.5f,
                    localPos.y - worldSpaceSize.y * 0.5f,
                    localPos.x + worldSpaceSize.x * 0.5f,
                    localPos.y + worldSpaceSize.y * 0.5f
                );

                var sizeDelta = box.BoxRectTransform.sizeDelta;
                var currentBox = new Vector4(
                    -sizeDelta.x * 0.5f,
                    -sizeDelta.y * 0.5f,
                    sizeDelta.x * 0.5f,
                    sizeDelta.y * 0.5f);

                if (box.ClassId == classId)
                {
                    // If the new box overlaps with an existing one of the same class, reuse it
                    if (SegmentationInferenceRunManager.CalculateIoU(newBox, currentBox) > 0f)
                    {
                        if (reusedBox == null)
                        {
                            reusedBox = box;
                        }
                        else
                        {
                            // Same overlapping class - remove the existing box
                            ReturnToPool(box);
                            m_boxDrawn.RemoveAt(i);
                        }
                    }
                }
                // If the new box's IoU with another class is significant, remove the existing box
                else if (SegmentationInferenceRunManager.CalculateIoU(newBox, currentBox) > 0.1f)
                {
                    // Different overlapping class - remove the existing box
                    ReturnToPool(box);
                    m_boxDrawn.RemoveAt(i);
                }
            }

            if (reusedBox != null)
            {
                return reusedBox;
            }

            // Create a new box
            var newData = GetBoxFromPoolOrCreate();
            newData.ClassId = classId;
            newData.ClassName = m_labels[classId].Replace(" ", "_");
            m_boxDrawn.Add(newData);
            return newData;
        }

        private BoundingBoxData GetBoxFromPoolOrCreate()
        {
            if (m_boxPool.Count > 0)
            {
                var pooled = m_boxPool[m_boxPool.Count - 1];
                pooled.BoxRectTransform.gameObject.SetActive(true);
                m_boxPool.RemoveAt(m_boxPool.Count - 1);
                return pooled;
            }

            var boxRectTransform = Instantiate(m_detectionBoxPrefab, ContentParent);
            boxRectTransform.gameObject.SetActive(true);
            return new BoundingBoxData
            {
                BoxRectTransform = boxRectTransform
            };
        }

        internal Transform ContentParent => m_detectionBoxPrefab.parent;

        private void ReturnToPool(BoundingBoxData box)
        {
            box.BoxRectTransform.gameObject.SetActive(false);
            m_boxPool.Add(box);
        }

        internal void ClearAnnotations()
        {
            foreach (var box in m_boxDrawn)
            {
                ReturnToPool(box);
            }
            m_boxDrawn.Clear();

            foreach (var mask in m_masksDrawn)
            {
                ReturnMaskToPool(mask);
            }
            m_masksDrawn.Clear();
        }

        // ============================================================================
        // MASK RENDERING
        // ============================================================================

        public void RenderMask(int maskId, Texture2D maskTexture, int[] bboxPixels, Pose cameraPose)
        {
            Debug.LogError($"[MASK RENDER] RenderMask called for maskId={maskId}");

            if (maskTexture == null || bboxPixels == null || bboxPixels.Length < 4)
            {
                Debug.LogError($"[MASK RENDER] Invalid mask data: texture={maskTexture != null}, bbox={bboxPixels != null}, bboxLen={bboxPixels?.Length ?? 0}");
                return;
            }

            Debug.LogError($"[MASK RENDER] Data valid, getting or creating mask overlay...");

            // Get or create mask overlay
            MaskData maskData = GetOrCreateMask(maskId);
            if (maskData == null)
            {
                Debug.LogError($"[MASK] Failed to create mask overlay for maskId={maskId}");
                return;
            }

            Debug.LogError($"[MASK RENDER] Using TEXTURED MESH approach for solid mask...");

            // Clean up old mesh objects
            if (maskData.SamplePoints != null)
            {
                foreach (var obj in maskData.SamplePoints)
                {
                    if (obj != null) Destroy(obj);
                }
                maskData.SamplePoints.Clear();
            }
            else
            {
                maskData.SamplePoints = new List<GameObject>();
            }

            // POINT CLOUD APPROACH: Sample mask texture and create 3D points (like pose estimation)
            int x1 = bboxPixels[0];
            int y1 = bboxPixels[1];
            int x2 = bboxPixels[2];
            int y2 = bboxPixels[3];

            Vector2 currentResolution = m_cameraAccess.CurrentResolution;

            // USE SAME METHOD AS BOUNDING BOXES for accurate positioning

            // Create normalized rect from bbox pixels (EXACTLY like bounding boxes)
            var normRect = new Rect(
                x1 / currentResolution.x,
                1f - (y2 / currentResolution.y),  // yMax -> min (flipped Y)
                (x2 - x1) / currentResolution.x,
                (y2 - y1) / currentResolution.y
            );

            // Raycast center to get depth (EXACTLY like bounding boxes)
            Vector2 normalizedCenter = normRect.center;
            var centerRay = m_cameraAccess.ViewportPointToRay(normalizedCenter, cameraPose);
            var worldPos = m_environmentRaycast?.Raycast(centerRay);
            Vector3 actualWorldPos = worldPos.HasValue ? worldPos.Value : centerRay.GetPoint(2.0f);

            // Calculate distance and world space center (EXACTLY like bounding boxes)
            float distance = Vector3.Distance(cameraPose.position, actualWorldPos);
            var worldSpaceCenter = m_cameraAccess.ViewportPointToRay(normRect.center, cameraPose).GetPoint(distance);
            var normal = (worldSpaceCenter - cameraPose.position).normalized;

            // Create plane perpendicular to camera view (EXACTLY like bounding boxes)
            var plane = new Plane(normal, worldSpaceCenter);

            // Raycast bbox corners to the plane (EXACTLY like bounding boxes)
            var minRay = m_cameraAccess.ViewportPointToRay(normRect.min, cameraPose);
            var maxRay = m_cameraAccess.ViewportPointToRay(normRect.max, cameraPose);
            plane.Raycast(minRay, out float intersectionDistanceMin);
            plane.Raycast(maxRay, out float intersectionDistanceMax);
            var min = minRay.GetPoint(intersectionDistanceMin);
            var max = maxRay.GetPoint(intersectionDistanceMax);

            // Transform to camera local space to get 2D size (EXACTLY like bounding boxes)
            var topLeftLocal = Quaternion.Inverse(cameraPose.rotation) * (min - cameraPose.position);
            var bottomRightLocal = Quaternion.Inverse(cameraPose.rotation) * (max - cameraPose.position);
            var size = new Vector2(
                Mathf.Abs(bottomRightLocal.x - topLeftLocal.x),
                Mathf.Abs(bottomRightLocal.y - topLeftLocal.y)
            );

            // Create quad mesh
            GameObject quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quad.transform.SetParent(ContentParent);

            // Position and orient EXACTLY like bounding boxes
            quad.transform.SetPositionAndRotation(worldSpaceCenter, Quaternion.LookRotation(normal));
            quad.transform.localScale = new Vector3(size.x, size.y, 1f);

            // ✅ FIXED: Create NEW material instance for each quad (not shared cached material)
            // This allows each quad to have its own texture reference
            var renderer = quad.GetComponent<Renderer>();
            Material quadMaterial = new Material(Shader.Find("Unlit/Transparent"));
            quadMaterial.mainTexture = maskTexture;
            quadMaterial.color = new Color(0f, 1f, 0f, 0.7f); // Green tint with transparency
            renderer.material = quadMaterial;

            // ✅ FIXED: Store texture in MaskData so it stays alive until ClearAllMasks()
            maskData.MaskTexture = maskTexture;
            maskData.SamplePoints.Add(quad);

            Debug.LogError($"[MASK QUAD] Created quad at {worldSpaceCenter}, size={size.x:F2}x{size.y:F2}m, dist={distance:F2}m, bbox=({x1},{y1})-({x2},{y2})");
            maskData.lastUpdateTime = Time.time;
        }

        private MaskData GetOrCreateMask(int maskId)
        {
            // Try to reuse existing mask
            foreach (var mask in m_masksDrawn)
            {
                if (mask.MaskId == maskId)
                {
                    return mask;
                }
            }

            // Create new mask data (point cloud mode - no UI elements needed)
            MaskData maskData = new MaskData
            {
                MaskId = maskId,
                MaskRectTransform = null,
                MaskImage = null,
                MaskTexture = null,
                SamplePoints = new List<GameObject>(),
                UseMultiSample = true
            };

            m_masksDrawn.Add(maskData);
            Debug.LogError($"[MASK] Created new mask data for maskId={maskId} (point cloud mode)");
            return maskData;
        }

        private void ReturnMaskToPool(MaskData mask)
        {
            // ✅ CRITICAL: Clean up quads AND their materials to prevent memory leak
            if (mask.SamplePoints != null)
            {
                foreach (var point in mask.SamplePoints)
                {
                    if (point != null)
                    {
                        // Destroy the material first (Unity doesn't auto-destroy materials)
                        var renderer = point.GetComponent<Renderer>();
                        if (renderer != null && renderer.material != null)
                        {
                            Destroy(renderer.material);
                        }

                        // Then destroy the GameObject
                        Destroy(point);
                    }
                }
                mask.SamplePoints.Clear();
            }

            // ✅ Clean up mask texture (now stored in MaskData)
            if (mask.MaskTexture != null)
            {
                Destroy(mask.MaskTexture);
                mask.MaskTexture = null;
            }

            // Clear texture reference
            if (mask.MaskImage != null)
            {
                mask.MaskImage.texture = null;
            }

            // Deactivate GameObject
            if (mask.MaskRectTransform != null)
            {
                mask.MaskRectTransform.gameObject.SetActive(false);
            }

            // Don't pool - just let it be garbage collected since we're using point clouds
            // m_maskPool.Add(mask);
        }

        // Clear all active masks (used when no response or error)
        public void ClearAllMasks()
        {
            // Clear all drawn masks
            for (int i = m_masksDrawn.Count - 1; i >= 0; i--)
            {
                ReturnMaskToPool(m_masksDrawn[i]);
            }
            m_masksDrawn.Clear();
        }
    }
}


