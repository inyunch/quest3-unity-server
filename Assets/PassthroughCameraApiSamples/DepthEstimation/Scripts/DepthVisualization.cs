// Copyright (c) Meta Platforms, Inc. and affiliates.

using UnityEngine;
using UnityEngine.UI;
using System.Collections.Generic;
using TMPro;

namespace PassthroughCameraSamples.DepthEstimation
{
    /// <summary>
    /// Visualizes depth estimation results using color mapping.
    /// Supports multiple color schemes: Grayscale, Inferno, Viridis, Turbo.
    /// </summary>
    public class DepthVisualization : MonoBehaviour
    {
        [Header("Display Settings")]
        [SerializeField] private RawImage m_depthDisplay;
        [SerializeField] private TextMeshProUGUI m_centerDepthText;

        [Header("Visualization Settings")]
        [SerializeField] private DepthColormap m_colormap = DepthColormap.Inferno;
        [SerializeField, Range(0f, 1f)] private float m_minDepthClip = 0f;
        [SerializeField, Range(0f, 1f)] private float m_maxDepthClip = 1f;

        public enum DepthColormap
        {
            Grayscale,  // Black (near) to White (far)
            Inferno,    // Black-Red-Yellow (near to far)
            Viridis,    // Purple-Green-Yellow (near to far)
            Turbo       // Blue-Green-Yellow-Red (near to far)
        }

        private Texture2D m_depthTexture;
        private int m_currentWidth = 0;
        private int m_currentHeight = 0;

        // Depth map data
        private float m_centerDepthValue = 0f;
        private float m_minDepth = 0f;
        private float m_maxDepth = 1f;
        private float m_avgDepth = 0f;

        private void Start()
        {
            Debug.Log("[DepthVis] DepthVisualization started");

            if (m_depthDisplay == null)
            {
                Debug.LogWarning("[DepthVis] RawImage reference is null. Depth visualization will not be displayed.");
            }
        }

        /// <summary>
        /// Update the depth visualization with new depth data from server.
        /// </summary>
        public void UpdateDepthMap(DepthInferenceRunManager.DepthData depthData)
        {
            if (depthData == null || depthData.values == null)
            {
                Debug.LogWarning("[DepthVis] Received null depth data");
                return;
            }

            int width = depthData.width;
            int height = depthData.height;

            Debug.Log($"[DepthVis] Updating depth map: {width}x{height}");

            // Recreate texture if size changed
            if (m_depthTexture == null || m_currentWidth != width || m_currentHeight != height)
            {
                m_depthTexture = new Texture2D(width, height, TextureFormat.RGB24, false);
                m_depthTexture.filterMode = FilterMode.Bilinear;
                m_currentWidth = width;
                m_currentHeight = height;
                Debug.Log($"[DepthVis] Created new depth texture: {width}x{height}");
            }

            // Convert depth values to colors
            Color[] pixels = new Color[width * height];
            float minDepth = float.MaxValue;
            float maxDepth = float.MinValue;
            float sumDepth = 0f;
            int pixelCount = 0;

            // Iterate through depth values (row-major order from server)
            for (int y = 0; y < height; y++)
            {
                if (y >= depthData.values.Count)
                {
                    Debug.LogWarning($"[DepthVis] Row {y} missing in depth data");
                    break;
                }

                List<float> row = depthData.values[y];
                for (int x = 0; x < width; x++)
                {
                    if (x >= row.Count)
                    {
                        Debug.LogWarning($"[DepthVis] Column {x} missing in row {y}");
                        break;
                    }

                    float depthValue = row[x];  // 0 = near, 1 = far

                    // Clamp to configured range
                    depthValue = Mathf.Clamp(depthValue, m_minDepthClip, m_maxDepthClip);

                    // Renormalize to [0, 1] after clipping
                    float normalizedDepth = (depthValue - m_minDepthClip) / (m_maxDepthClip - m_minDepthClip + 0.0001f);

                    // Convert to color based on selected colormap
                    Color color = GetColorForDepth(normalizedDepth);

                    // Set pixel (flip Y coordinate since Unity textures are bottom-up)
                    int pixelIndex = (height - 1 - y) * width + x;
                    pixels[pixelIndex] = color;

                    // Track statistics
                    minDepth = Mathf.Min(minDepth, depthValue);
                    maxDepth = Mathf.Max(maxDepth, depthValue);
                    sumDepth += depthValue;
                    pixelCount++;
                }
            }

            // Update statistics
            m_minDepth = minDepth;
            m_maxDepth = maxDepth;
            m_avgDepth = pixelCount > 0 ? sumDepth / pixelCount : 0f;

            // Get center pixel depth value
            int centerY = height / 2;
            int centerX = width / 2;
            if (centerY < depthData.values.Count && centerX < depthData.values[centerY].Count)
            {
                m_centerDepthValue = depthData.values[centerY][centerX];
            }

            // Apply pixels to texture
            m_depthTexture.SetPixels(pixels);
            m_depthTexture.Apply();

            // Update display
            if (m_depthDisplay != null)
            {
                m_depthDisplay.texture = m_depthTexture;
            }

            // Update center depth text
            UpdateCenterDepthDisplay();

            Debug.Log($"[DepthVis] Updated texture: min={minDepth:F3}, max={maxDepth:F3}, avg={m_avgDepth:F3}, center={m_centerDepthValue:F3}");
        }

        /// <summary>
        /// Get color for a normalized depth value (0=near, 1=far) based on current colormap.
        /// </summary>
        private Color GetColorForDepth(float normalizedDepth)
        {
            switch (m_colormap)
            {
                case DepthColormap.Grayscale:
                    return new Color(normalizedDepth, normalizedDepth, normalizedDepth);

                case DepthColormap.Inferno:
                    return GetInfernoColor(normalizedDepth);

                case DepthColormap.Viridis:
                    return GetViridisColor(normalizedDepth);

                case DepthColormap.Turbo:
                    return GetTurboColor(normalizedDepth);

                default:
                    return new Color(normalizedDepth, normalizedDepth, normalizedDepth);
            }
        }

        /// <summary>
        /// Inferno colormap: Black → Red → Yellow (near to far).
        /// </summary>
        private Color GetInfernoColor(float t)
        {
            // Simplified inferno colormap approximation
            float r = Mathf.Clamp01(Mathf.Pow(t, 0.5f));
            float g = Mathf.Clamp01(Mathf.Pow(Mathf.Max(0f, t - 0.3f) / 0.7f, 2f));
            float b = Mathf.Clamp01(Mathf.Pow(Mathf.Max(0f, t - 0.7f) / 0.3f, 3f) * 0.3f);
            return new Color(r, g, b);
        }

        /// <summary>
        /// Viridis colormap: Purple → Green → Yellow (near to far).
        /// </summary>
        private Color GetViridisColor(float t)
        {
            // Simplified viridis colormap approximation
            float r = Mathf.Clamp01(0.3f + 0.7f * Mathf.Pow(t, 1.5f));
            float g = Mathf.Clamp01(Mathf.Pow(t, 0.8f));
            float b = Mathf.Clamp01(0.5f - 0.5f * t);
            return new Color(r, g, b);
        }

        /// <summary>
        /// Turbo colormap: Blue → Green → Yellow → Red (near to far).
        /// </summary>
        private Color GetTurboColor(float t)
        {
            // Simplified turbo colormap approximation
            float r, g, b;

            if (t < 0.25f)
            {
                // Blue to Cyan
                r = 0f;
                g = t * 4f;
                b = 1f;
            }
            else if (t < 0.5f)
            {
                // Cyan to Green
                r = 0f;
                g = 1f;
                b = 1f - (t - 0.25f) * 4f;
            }
            else if (t < 0.75f)
            {
                // Green to Yellow
                r = (t - 0.5f) * 4f;
                g = 1f;
                b = 0f;
            }
            else
            {
                // Yellow to Red
                r = 1f;
                g = 1f - (t - 0.75f) * 4f;
                b = 0f;
            }

            return new Color(r, g, b);
        }

        /// <summary>
        /// Update the text display showing depth value at center pixel.
        /// </summary>
        private void UpdateCenterDepthDisplay()
        {
            if (m_centerDepthText == null)
                return;

            string depthInfo = $"<b>Center Depth</b>\n";
            depthInfo += $"Value: {m_centerDepthValue:F3}\n";
            depthInfo += $"<size=80%>Min: {m_minDepth:F3}\n";
            depthInfo += $"Max: {m_maxDepth:F3}\n";
            depthInfo += $"Avg: {m_avgDepth:F3}</size>";

            m_centerDepthText.text = depthInfo;
        }

        /// <summary>
        /// Clear the depth visualization.
        /// </summary>
        public void ClearDepth()
        {
            if (m_depthTexture != null)
            {
                // Fill with black
                Color[] pixels = new Color[m_currentWidth * m_currentHeight];
                for (int i = 0; i < pixels.Length; i++)
                {
                    pixels[i] = Color.black;
                }
                m_depthTexture.SetPixels(pixels);
                m_depthTexture.Apply();
            }

            m_centerDepthValue = 0f;
            m_minDepth = 0f;
            m_maxDepth = 1f;
            m_avgDepth = 0f;

            UpdateCenterDepthDisplay();

            Debug.Log("[DepthVis] Cleared depth visualization");
        }

        /// <summary>
        /// Change the colormap at runtime.
        /// </summary>
        public void SetColormap(DepthColormap colormap)
        {
            m_colormap = colormap;
            Debug.Log($"[DepthVis] Colormap changed to: {colormap}");
        }

        /// <summary>
        /// Get the current center depth value (normalized 0-1).
        /// </summary>
        public float GetCenterDepth()
        {
            return m_centerDepthValue;
        }
    }
}
