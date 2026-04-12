// Copyright (c) Meta Platforms, Inc. and affiliates.

using System.Collections.Generic;
using UnityEngine;
using PassthroughCameraSamples.Shared;
using PassthroughCameraSamples.MultiObjectDetection;
using Meta.XR;

namespace PassthroughCameraSamples.DepthEstimation
{
    /// <summary>
    /// [DEPRECATED] Legacy depth visualization manager - use DepthLabelManager instead.
    /// This class is kept for compatibility but all functionality has been moved to DepthLabelManager.
    /// </summary>
    [System.Obsolete("Use DepthLabelManager for text-based depth visualization instead of point clouds")]
    public class DepthVisualizationManager : MonoBehaviour
    {
        [Header("Visualization Settings - DEPRECATED")]
        [SerializeField] private GameObject m_depthPointPrefab;
        [SerializeField, Range(1, 50)] private int m_samplingRate = 15;
        [SerializeField, Range(0.001f, 0.1f)] private float m_pointSize = 0.015f;
        [SerializeField] private Gradient m_depthColormap;
        [SerializeField, Range(0f, 1f)] private float m_minDepthFilter = 0.0f;
        [SerializeField, Range(0f, 1f)] private float m_maxDepthFilter = 1.0f;

        [Header("ROI Settings - DEPRECATED")]
        [SerializeField] private bool m_useROI = true;
        [SerializeField] private float m_roiPadding = 0.1f;

        [Header("References")]
        [SerializeField] private Component m_cameraAccess;  // Was PassthroughCameraAccess - type removed for compatibility
        [SerializeField] private Component m_environmentRaycast;  // Was EnvironmentRayCastSampleManager - type removed for compatibility

        [Header("Performance")]
        [SerializeField] private bool m_useObjectPooling = true;
        [SerializeField] private int m_maxPointsPerFrame = 500;

        // Object pooling for performance
        private readonly List<GameObject> m_pointPool = new List<GameObject>();
        private readonly List<GameObject> m_activePoints = new List<GameObject>();

        private void Start()
        {
            Debug.LogWarning("[DEPTH VIZ] DepthVisualizationManager is DEPRECATED! Use DepthLabelManager instead.");
            Debug.LogWarning("[DEPTH VIZ] Point cloud visualization is no longer supported with the new API.");
        }

        /// <summary>
        /// [DEPRECATED] This method is no longer functional.
        /// Use DepthLabelManager.DrawDepthLabels() instead.
        /// </summary>
        [System.Obsolete("Use DepthLabelManager.DrawDepthLabels() instead")]
        public void DrawDepthMapROI(object depth, object[] detections, Pose cameraPose)
        {
            Debug.LogWarning("[DEPTH VIZ] DrawDepthMapROI is deprecated! Use DepthLabelManager.DrawDepthLabels()");
            ClearDepthPoints();
        }

        /// <summary>
        /// Clears all active depth points.
        /// </summary>
        public void ClearDepthPoints()
        {
            if (m_useObjectPooling)
            {
                foreach (var point in m_activePoints)
                {
                    if (point != null)
                    {
                        point.SetActive(false);
                        m_pointPool.Add(point);
                    }
                }
            }
            else
            {
                foreach (var point in m_activePoints)
                {
                    if (point != null)
                    {
                        Destroy(point);
                    }
                }
            }

            m_activePoints.Clear();
        }

        private void OnDisable()
        {
            ClearDepthPoints();
        }

        private void OnDestroy()
        {
            foreach (var point in m_pointPool)
            {
                if (point != null) Destroy(point);
            }
            m_pointPool.Clear();

            foreach (var point in m_activePoints)
            {
                if (point != null) Destroy(point);
            }
            m_activePoints.Clear();
        }

        internal Transform ContentParent => transform;
    }
}
