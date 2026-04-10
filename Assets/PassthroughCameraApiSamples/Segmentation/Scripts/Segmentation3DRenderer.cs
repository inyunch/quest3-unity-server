// Copyright (c) Meta Platforms, Inc. and affiliates.

using System;
using UnityEngine;

namespace PassthroughCameraSamples.Segmentation
{
    /// <summary>
    /// Renders segmentation mask as a 3D quad overlay in world space.
    ///
    /// Similar to PoseSkeletonUiManager, this uses 3D rendering instead of 2D Canvas,
    /// allowing the overlay to properly integrate with VR passthrough.
    /// </summary>
    public class Segmentation3DRenderer : MonoBehaviour
    {
        [Header("3D Quad Settings")]
        [SerializeField] private float m_quadDistance = 1.5f;  // Distance from camera
        [SerializeField] private float m_quadScale = 1.0f;     // Scale multiplier
        [SerializeField, Range(0f, 1f)] private float m_overlayAlpha = 0.6f;

        [Header("Rendering")]
        [SerializeField] private bool m_useInstanceColors = true;
        [SerializeField] private Color m_segmentationColor = new Color(0f, 1f, 0f, 0.6f);

        [Header("Debug")]
        [SerializeField] private bool m_verboseLogging = true;

        // 3D quad and rendering
        private GameObject m_quadObject;
        private MeshRenderer m_quadRenderer;
        private Material m_quadMaterial;
        private Texture2D m_maskTexture;

        // Camera reference
        private Camera m_vrCamera;

        // Instance color palette
        private static readonly Color[] INSTANCE_COLORS = new Color[]
        {
            new Color(1f, 0f, 0f, 1f),    // Red
            new Color(0f, 1f, 0f, 1f),    // Green
            new Color(0f, 0f, 1f, 1f),    // Blue
            new Color(1f, 1f, 0f, 1f),    // Yellow
            new Color(1f, 0f, 1f, 1f),    // Magenta
            new Color(0f, 1f, 1f, 1f),    // Cyan
            new Color(1f, 0.5f, 0f, 1f),  // Orange
            new Color(0.5f, 0f, 1f, 1f),  // Purple
        };

        private void Start()
        {
            Debug.Log("[SEG3D] Segmentation3DRenderer initialized");

            // Find VR camera
            m_vrCamera = FindVRCamera();
            if (m_vrCamera == null)
            {
                Debug.LogError("[SEG3D] Could not find VR camera!");
                return;
            }

            Debug.Log($"[SEG3D] Using camera: {m_vrCamera.name}");

            // Create 3D quad for rendering mask
            CreateQuad();

            Debug.Log("[SEG3D] 3D quad created successfully");
        }

        private Camera FindVRCamera()
        {
            // Try to find OVR CenterEyeAnchor first
            var ovrCameraRig = FindObjectOfType<OVRCameraRig>();
            if (ovrCameraRig != null && ovrCameraRig.centerEyeAnchor != null)
            {
                Camera cam = ovrCameraRig.centerEyeAnchor.GetComponent<Camera>();
                if (cam != null) return cam;
            }

            // Fallback to main camera
            return Camera.main;
        }

        private void CreateQuad()
        {
            // Create quad GameObject
            m_quadObject = GameObject.CreatePrimitive(PrimitiveType.Quad);
            m_quadObject.name = "SegmentationQuad3D";
            m_quadObject.transform.SetParent(transform);

            // Remove collider (not needed)
            Destroy(m_quadObject.GetComponent<Collider>());

            // Get renderer
            m_quadRenderer = m_quadObject.GetComponent<MeshRenderer>();

            // Create unlit transparent material
            Shader unlitShader = Shader.Find("Unlit/Transparent");
            if (unlitShader == null)
            {
                Debug.LogWarning("[SEG3D] Unlit/Transparent shader not found, using Standard");
                unlitShader = Shader.Find("Standard");
            }

            m_quadMaterial = new Material(unlitShader);
            m_quadMaterial.color = new Color(1, 1, 1, m_overlayAlpha);
            m_quadRenderer.material = m_quadMaterial;

            // Set rendering order (render on top of passthrough)
            m_quadRenderer.sortingOrder = 100;

            // Start hidden
            m_quadObject.SetActive(false);

            Debug.Log($"[SEG3D] Quad created with shader: {unlitShader.name}");
        }

        private void Update()
        {
            // Update quad position to follow camera
            if (m_quadObject != null && m_vrCamera != null && m_quadObject.activeSelf)
            {
                UpdateQuadPosition();
            }
        }

        private void UpdateQuadPosition()
        {
            // Position quad in front of camera
            Vector3 forward = m_vrCamera.transform.forward;
            Vector3 position = m_vrCamera.transform.position + forward * m_quadDistance;

            m_quadObject.transform.position = position;
            m_quadObject.transform.rotation = m_vrCamera.transform.rotation;

            // Scale quad to match camera FOV
            float fov = m_vrCamera.fieldOfView;
            float aspect = m_vrCamera.aspect;
            float height = 2f * m_quadDistance * Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad);
            float width = height * aspect;

            m_quadObject.transform.localScale = new Vector3(width, height, 1f) * m_quadScale;
        }

        /// <summary>
        /// Render a segmentation result on the 3D quad.
        /// </summary>
        public void RenderSegmentation(SegmentationResponse response)
        {
            if (m_verboseLogging)
            {
                Debug.Log("[SEG3D] RenderSegmentation called");
            }

            if (response == null || !response.success)
            {
                Debug.LogError($"[SEG3D] Invalid response: {response?.error}");
                return;
            }

            if (string.IsNullOrEmpty(response.segmentation_mask))
            {
                Debug.LogError("[SEG3D] Empty segmentation mask");
                return;
            }

            try
            {
                // Decode mask from base64
                byte[] maskBytes = Convert.FromBase64String(response.segmentation_mask);

                // Load as texture
                if (m_maskTexture == null || m_maskTexture.width != response.mask_width || m_maskTexture.height != response.mask_height)
                {
                    m_maskTexture = new Texture2D(response.mask_width, response.mask_height, TextureFormat.RGBA32, false);
                    m_maskTexture.filterMode = FilterMode.Bilinear;
                }

                // Load image data
                if (response.mask_encoding == "png")
                {
                    m_maskTexture.LoadImage(maskBytes);
                }
                else
                {
                    Debug.LogError($"[SEG3D] Unsupported encoding: {response.mask_encoding}");
                    return;
                }

                // Apply tint if enabled
                if (m_useInstanceColors && response.num_instances > 0)
                {
                    ApplyInstanceColors(m_maskTexture, response.num_instances);
                }
                else
                {
                    ApplyUniformTint(m_maskTexture, m_segmentationColor);
                }

                // Apply texture to quad material
                m_quadMaterial.mainTexture = m_maskTexture;

                // Show quad
                if (!m_quadObject.activeSelf)
                {
                    m_quadObject.SetActive(true);
                    UpdateQuadPosition();
                }

                // Count visible pixels for debugging
                Color[] allPixels = m_maskTexture.GetPixels();
                int totalVisible = 0;
                foreach (var pixel in allPixels)
                {
                    if (pixel.a > 0.01f) totalVisible++;
                }
                float visiblePercent = (totalVisible / (float)allPixels.Length) * 100f;

                if (m_verboseLogging)
                {
                    Debug.Log($"[SEG3D] Rendered: {response.mask_width}x{response.mask_height}, instances={response.num_instances}");
                    Debug.Log($"[SEG3D] Coverage: {visiblePercent:F1}% ({totalVisible}/{allPixels.Length} pixels)");
                    Debug.Log($"[SEG3D] Quad position: {m_quadObject.transform.position}, scale: {m_quadObject.transform.localScale}");
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[SEG3D] Render failed: {e.Message}\n{e.StackTrace}");
            }
        }

        private void ApplyUniformTint(Texture2D texture, Color tintColor)
        {
            Color[] pixels = texture.GetPixels();

            for (int i = 0; i < pixels.Length; i++)
            {
                float maskAlpha = pixels[i].a;

                if (maskAlpha > 0.01f)
                {
                    pixels[i] = new Color(
                        tintColor.r,
                        tintColor.g,
                        tintColor.b,
                        maskAlpha * m_overlayAlpha
                    );
                }
                else
                {
                    pixels[i] = Color.clear;
                }
            }

            texture.SetPixels(pixels);
            texture.Apply();
        }

        private void ApplyInstanceColors(Texture2D texture, int numInstances)
        {
            Color[] pixels = texture.GetPixels();

            for (int i = 0; i < pixels.Length; i++)
            {
                // Decode instance ID from pixel value
                int instanceId = Mathf.RoundToInt(pixels[i].r * 255);

                if (instanceId > 0)
                {
                    Color instanceColor = INSTANCE_COLORS[(instanceId - 1) % INSTANCE_COLORS.Length];

                    pixels[i] = new Color(
                        instanceColor.r,
                        instanceColor.g,
                        instanceColor.b,
                        m_overlayAlpha
                    );
                }
                else
                {
                    pixels[i] = Color.clear;
                }
            }

            texture.SetPixels(pixels);
            texture.Apply();
        }

        /// <summary>
        /// Clear the segmentation overlay.
        /// </summary>
        public void ClearOverlay()
        {
            if (m_quadObject != null)
            {
                m_quadObject.SetActive(false);
            }

            if (m_verboseLogging)
            {
                Debug.Log("[SEG3D] Overlay cleared");
            }
        }

        /// <summary>
        /// Set overlay alpha.
        /// </summary>
        public void SetAlpha(float alpha)
        {
            m_overlayAlpha = Mathf.Clamp01(alpha);

            if (m_quadMaterial != null)
            {
                Color color = m_quadMaterial.color;
                color.a = m_overlayAlpha;
                m_quadMaterial.color = color;
            }
        }

        private void OnDestroy()
        {
            if (m_maskTexture != null)
            {
                Destroy(m_maskTexture);
            }

            if (m_quadMaterial != null)
            {
                Destroy(m_quadMaterial);
            }

            if (m_quadObject != null)
            {
                Destroy(m_quadObject);
            }
        }
    }
}

