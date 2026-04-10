// Copyright (c) Meta Platforms, Inc. and affiliates.

using System;
using UnityEngine;
using UnityEngine.UI;

namespace PassthroughCameraSamples.Segmentation
{
    /// <summary>
    /// Renders segmentation mask as an overlay on the passthrough view.
    ///
    /// Displays the segmentation result returned from the server as a
    /// semi-transparent colored overlay aligned with the camera view.
    /// </summary>
    public class SegmentationOverlayRenderer : MonoBehaviour
    {
        [Header("Overlay Settings")]
        [SerializeField] private RawImage m_overlayImage;
        [SerializeField, Range(0f, 1f)] private float m_overlayAlpha = 0.6f;
        [SerializeField] private Color m_segmentationColor = new Color(0f, 1f, 0f, 0.6f);  // Green tint

        [Header("Rendering Options")]
        [SerializeField] private bool m_useInstanceColors = true;  // Different color per instance
        [SerializeField] private bool m_showConfidence = false;     // Modulate alpha by confidence
        [SerializeField] private float m_fadeInDuration = 0.2f;

        [Header("Debug")]
        [SerializeField] private bool m_verboseLogging = true;

        // Current state
        private Texture2D m_maskTexture = null;
        private Material m_overlayMaterial = null;
        private float m_currentAlpha = 0f;
        private bool m_isVisible = false;

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
            Debug.Log("[OVERLAY] SegmentationOverlayRenderer initialized");

            // Find overlay image if not assigned
            if (m_overlayImage == null)
            {
                m_overlayImage = GetComponent<RawImage>();

                if (m_overlayImage == null)
                {
                    Debug.LogError("[OVERLAY] RawImage component not found! Cannot render segmentation.");
                    return;
                }
            }

            // Find and configure parent Canvas for VR
            Canvas parentCanvas = m_overlayImage.GetComponentInParent<Canvas>();
            if (parentCanvas != null)
            {
                Camera vrCamera = FindVRCamera();
                Debug.Log($"[OVERLAY] Found parent Canvas, original RenderMode={parentCanvas.renderMode}");
                Debug.Log($"[OVERLAY] FindVRCamera result: {vrCamera?.name ?? "NULL"}");

                if (vrCamera != null)
                {
                    // BEST FIX: Use ScreenSpace-Camera mode for perfect alignment
                    // This makes the overlay render directly on screen, aligned with camera view
                    parentCanvas.renderMode = RenderMode.ScreenSpaceCamera;
                    parentCanvas.worldCamera = vrCamera;

                    // Set plane distance (how far from camera the Canvas renders)
                    parentCanvas.planeDistance = 1.5f;

                    // Configure Canvas Scaler for proper resolution
                    var canvasScaler = parentCanvas.GetComponent<UnityEngine.UI.CanvasScaler>();
                    if (canvasScaler != null)
                    {
                        canvasScaler.uiScaleMode = UnityEngine.UI.CanvasScaler.ScaleMode.ScaleWithScreenSize;
                        canvasScaler.referenceResolution = new Vector2(1280, 960); // Match camera resolution
                    }

                    // Reset transform to fill screen
                    RectTransform canvasRect = parentCanvas.GetComponent<RectTransform>();
                    if (canvasRect != null)
                    {
                        canvasRect.localPosition = Vector3.zero;
                        canvasRect.localRotation = Quaternion.identity;
                        canvasRect.localScale = Vector3.one;
                    }

                    Debug.Log($"[OVERLAY] Configured Canvas: mode=ScreenSpaceCamera, planeDistance={parentCanvas.planeDistance}");
                }
                else
                {
                    Debug.LogError("[OVERLAY] Could not find VR camera for Canvas!");
                }
            }
            else
            {
                Debug.LogError("[OVERLAY] No parent Canvas found!");
            }

            // Create overlay material with proper alpha blending
            Shader overlayShader = Shader.Find("UI/Default");
            if (overlayShader == null)
            {
                Debug.LogError("[OVERLAY] UI/Default shader not found! Trying Unlit/Transparent...");
                overlayShader = Shader.Find("Unlit/Transparent");
            }

            if (overlayShader != null)
            {
                m_overlayMaterial = new Material(overlayShader);
                m_overlayMaterial.color = Color.white;
                m_overlayImage.material = m_overlayMaterial;
                Debug.Log($"[OVERLAY] Created material with shader: {overlayShader.name}");
            }
            else
            {
                Debug.LogError("[OVERLAY] No suitable shader found!");
            }

            // Start hidden
            m_overlayImage.enabled = false;
            m_currentAlpha = 0f;

            Debug.Log($"[OVERLAY] Setup complete: Canvas={parentCanvas != null}, Material={m_overlayMaterial != null}");

            // Debug: Log Canvas and RawImage world positions
            if (parentCanvas != null)
            {
                Debug.Log($"[OVERLAY] Canvas Transform - Position: {parentCanvas.transform.position}, Rotation: {parentCanvas.transform.rotation.eulerAngles}, Scale: {parentCanvas.transform.lossyScale}");
                Debug.Log($"[OVERLAY] Canvas - sortingOrder={parentCanvas.sortingOrder}, overrideSorting={parentCanvas.overrideSorting}");
                Debug.Log($"[OVERLAY] RawImage Transform - Position: {m_overlayImage.transform.position}, Size: {m_overlayImage.rectTransform.rect.size}");
                Debug.Log($"[OVERLAY] RawImage - raycastTarget={m_overlayImage.raycastTarget}, maskable={m_overlayImage.maskable}");

                // Check RectTransform anchors
                var rectTransform = m_overlayImage.rectTransform;
                Debug.Log($"[OVERLAY] RectTransform - anchorMin={rectTransform.anchorMin}, anchorMax={rectTransform.anchorMax}, sizeDelta={rectTransform.sizeDelta}");

                if (parentCanvas.worldCamera != null)
                {
                    Debug.Log($"[OVERLAY] Camera Position: {parentCanvas.worldCamera.transform.position}, Forward: {parentCanvas.worldCamera.transform.forward}");
                    Debug.Log($"[OVERLAY] Camera cullingMask: {parentCanvas.worldCamera.cullingMask}, layerMask includes layer {m_overlayImage.gameObject.layer}: {((parentCanvas.worldCamera.cullingMask & (1 << m_overlayImage.gameObject.layer)) != 0)}");
                }
            }

            // DIAGNOSTIC: Show test overlay after 2 seconds to verify rendering works
            StartCoroutine(ShowTestOverlayAfterDelay());
        }

        private System.Collections.IEnumerator ShowTestOverlayAfterDelay()
        {
            yield return new WaitForSeconds(2f);
            Debug.Log("[OVERLAY] Auto-showing test overlay to verify rendering...");
            ShowTestOverlay();

            // Keep test overlay visible for 5 seconds
            yield return new WaitForSeconds(5f);
            Debug.Log("[OVERLAY] Test overlay timer expired, will be replaced by segmentation masks");
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

        /// <summary>
        /// Render a segmentation result.
        /// </summary>
        public void RenderSegmentation(SegmentationResponse response)
        {
            Debug.Log("[OVERLAY] RenderSegmentation called!");

            if (response == null)
            {
                Debug.LogError("[OVERLAY] response is null");
                return;
            }

            if (!response.success)
            {
                Debug.LogError($"[OVERLAY] response.success is false, error={response.error}");
                return;
            }

            if (string.IsNullOrEmpty(response.segmentation_mask))
            {
                Debug.LogError("[OVERLAY] Segmentation mask is empty!");
                return;
            }

            Debug.Log($"[OVERLAY] Received mask: {response.segmentation_mask.Length} chars, {response.mask_width}x{response.mask_height}, instances={response.num_instances}");

            try
            {
                // Decode mask from base64
                byte[] maskBytes = Convert.FromBase64String(response.segmentation_mask);

                // Load as texture
                if (m_maskTexture == null || m_maskTexture.width != response.mask_width || m_maskTexture.height != response.mask_height)
                {
                    m_maskTexture = new Texture2D(response.mask_width, response.mask_height, TextureFormat.RGBA32, false);
                }

                // Load image data
                if (response.mask_encoding == "png")
                {
                    m_maskTexture.LoadImage(maskBytes);
                }
                else
                {
                    Debug.LogError($"[OVERLAY] Unsupported mask encoding: {response.mask_encoding}");
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

                // Update overlay image
                m_overlayImage.texture = m_maskTexture;
                m_overlayImage.enabled = true;
                m_overlayImage.gameObject.SetActive(true);
                m_isVisible = true;

                // Set color to opaque white for visibility
                m_overlayImage.color = new Color(1, 1, 1, 1);

                // Set material alpha directly (no fade in for now)
                if (m_overlayMaterial != null)
                {
                    m_overlayMaterial.color = new Color(1, 1, 1, 1);
                }
                m_currentAlpha = m_overlayAlpha;

                // Sample texture to verify content
                Color[] allPixels = m_maskTexture.GetPixels();
                int totalVisible = 0;
                Color sampleColor = Color.clear;
                for (int i = 0; i < allPixels.Length; i++)
                {
                    if (allPixels[i].a > 0.01f)
                    {
                        totalVisible++;
                        if (sampleColor == Color.clear) sampleColor = allPixels[i]; // Get first visible pixel
                    }
                }
                float visiblePercent = (totalVisible / (float)allPixels.Length) * 100f;

                Debug.Log($"[OVERLAY] Rendered segmentation: {response.mask_width}x{response.mask_height}, instances={response.num_instances}");
                Debug.Log($"[OVERLAY] Texture check: format={m_maskTexture.format}, {totalVisible}/{allPixels.Length} visible pixels ({visiblePercent:F1}%)");
                Debug.Log($"[OVERLAY] Sample visible pixel color: {sampleColor}");
                Debug.Log($"[OVERLAY] RawImage enabled={m_overlayImage.enabled}, active={m_overlayImage.gameObject.activeSelf}, texture={m_maskTexture != null}");
                Debug.Log($"[OVERLAY] RawImage rectTransform size={m_overlayImage.rectTransform.rect.size}, position={m_overlayImage.rectTransform.position}");
                Debug.Log($"[OVERLAY] RawImage worldPos={m_overlayImage.transform.position}, layer={m_overlayImage.gameObject.layer}, color={m_overlayImage.color}");
                Debug.Log($"[OVERLAY] Material shader={m_overlayImage.material?.shader?.name ?? "NULL"}, color={m_overlayImage.material?.color}");
            }
            catch (Exception e)
            {
                Debug.LogError($"[OVERLAY] Render failed: {e.Message}\n{e.StackTrace}");
            }
        }

        /// <summary>
        /// Apply uniform color tint to mask.
        /// </summary>
        private void ApplyUniformTint(Texture2D texture, Color tintColor)
        {
            Color[] pixels = texture.GetPixels();

            for (int i = 0; i < pixels.Length; i++)
            {
                // Use alpha channel from mask as mask strength
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

        /// <summary>
        /// Apply different colors to different instances.
        /// Assumes mask pixel values encode instance ID.
        /// </summary>
        private void ApplyInstanceColors(Texture2D texture, int numInstances)
        {
            Color[] pixels = texture.GetPixels();

            for (int i = 0; i < pixels.Length; i++)
            {
                // Decode instance ID from pixel value
                // This assumes mask encodes instance ID in R channel (common format)
                int instanceId = Mathf.RoundToInt(pixels[i].r * 255);

                if (instanceId > 0)
                {
                    // Map instance ID to color
                    Color instanceColor = INSTANCE_COLORS[instanceId % INSTANCE_COLORS.Length];

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
        /// Fade in overlay smoothly.
        /// </summary>
        private System.Collections.IEnumerator FadeIn()
        {
            float elapsed = 0f;

            while (elapsed < m_fadeInDuration)
            {
                elapsed += Time.deltaTime;
                m_currentAlpha = Mathf.Lerp(0f, m_overlayAlpha, elapsed / m_fadeInDuration);

                // Update material alpha
                if (m_overlayMaterial != null)
                {
                    Color color = m_overlayMaterial.color;
                    color.a = m_currentAlpha;
                    m_overlayMaterial.color = color;
                }

                yield return null;
            }

            m_currentAlpha = m_overlayAlpha;
        }

        /// <summary>
        /// Clear overlay.
        /// </summary>
        public void ClearOverlay()
        {
            if (m_overlayImage != null)
            {
                m_overlayImage.enabled = false;
            }

            m_isVisible = false;
            m_currentAlpha = 0f;

            if (m_verboseLogging)
            {
                Debug.Log("[OVERLAY] Cleared segmentation overlay");
            }
        }

        /// <summary>
        /// Set overlay alpha.
        /// </summary>
        public void SetAlpha(float alpha)
        {
            m_overlayAlpha = Mathf.Clamp01(alpha);

            if (m_overlayMaterial != null && m_isVisible)
            {
                Color color = m_overlayMaterial.color;
                color.a = m_overlayAlpha;
                m_overlayMaterial.color = color;
            }
        }

        /// <summary>
        /// Toggle overlay visibility.
        /// </summary>
        public void ToggleVisibility()
        {
            if (m_overlayImage != null)
            {
                m_isVisible = !m_isVisible;
                m_overlayImage.enabled = m_isVisible;

                if (m_verboseLogging)
                {
                    Debug.Log($"[OVERLAY] Visibility: {m_isVisible}");
                }
            }
        }

        /// <summary>
        /// Test method: Show a solid color overlay to verify rendering works.
        /// Call this from Start() to test if overlay is visible at all.
        /// </summary>
        public void ShowTestOverlay()
        {
            Debug.Log("[OVERLAY] ShowTestOverlay - Creating test pattern");

            // Create test texture with bright red color
            if (m_maskTexture == null)
            {
                m_maskTexture = new Texture2D(256, 256, TextureFormat.RGBA32, false);
            }

            Color[] testPixels = new Color[256 * 256];
            for (int i = 0; i < testPixels.Length; i++)
            {
                // Bright red with 80% alpha
                testPixels[i] = new Color(1f, 0f, 0f, 0.8f);
            }

            m_maskTexture.SetPixels(testPixels);
            m_maskTexture.Apply();

            // Apply to RawImage
            m_overlayImage.texture = m_maskTexture;
            m_overlayImage.enabled = true;
            m_overlayImage.gameObject.SetActive(true);
            m_overlayImage.color = Color.white;

            // Set material to fully opaque
            if (m_overlayMaterial != null)
            {
                m_overlayMaterial.color = Color.white;
            }

            Debug.Log($"[OVERLAY] TEST OVERLAY APPLIED! RawImage: enabled={m_overlayImage.enabled}, texture={m_maskTexture.width}x{m_maskTexture.height}");
            Debug.Log($"[OVERLAY] TEST OVERLAY: You should see a bright RED rectangle covering the screen!");
            Debug.Log($"[OVERLAY] TEST OVERLAY: Material={m_overlayImage.material?.shader?.name}, color={m_overlayImage.color}");
        }

        private void OnDestroy()
        {
            if (m_maskTexture != null)
            {
                Destroy(m_maskTexture);
            }

            if (m_overlayMaterial != null)
            {
                Destroy(m_overlayMaterial);
            }
        }
    }
}
