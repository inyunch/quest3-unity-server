// Copyright (c) Meta Platforms, Inc. and affiliates.

using System.Collections;
using Meta.XR;
using Meta.XR.Samples;
using UnityEngine;
using PassthroughCameraSamples.MultiObjectDetection;

namespace PassthroughCameraSamples.DepthEstimation
{
    [MetaCodeSample("PassthroughCameraApiSamples-DepthEstimation")]
    public class DepthEstimationManager : MonoBehaviour
    {
        [SerializeField] private PassthroughCameraAccess m_cameraAccess;
        [SerializeField] private DepthVisualizationManager m_depthVisualization;

        internal OVRSpatialAnchor m_spatialAnchor;
        private bool m_isStarted;
        private bool m_isHeadsetTracking;

        private void Start()
        {
            Debug.Log("[DEPTH MGR] DepthEstimationManager started");

            // Reference checks
            Debug.Log($"[DEPTH MGR] cameraAccess={m_cameraAccess != null}");
            Debug.Log($"[DEPTH MGR] depthVisualization={m_depthVisualization != null}");
        }

        private void Awake()
        {
            Debug.Log("[DEPTH MGR] Awake - starting spatial anchor coroutine");
            StartCoroutine(UpdateSpatialAnchor());
            OVRManager.TrackingLost += OnTrackingLost;
            OVRManager.TrackingAcquired += OnTrackingAcquired;
        }

        private void OnDestroy()
        {
            Debug.Log("[DEPTH MGR] OnDestroy - erasing spatial anchor");
            EraseSpatialAnchor();
            OVRManager.TrackingLost -= OnTrackingLost;
            OVRManager.TrackingAcquired -= OnTrackingAcquired;
        }

        private void OnTrackingLost()
        {
            Debug.Log("[DEPTH MGR] Tracking lost");
            m_isHeadsetTracking = false;
        }

        private void OnTrackingAcquired()
        {
            Debug.Log("[DEPTH MGR] Tracking acquired");
            m_isHeadsetTracking = true;
        }

        private void Update()
        {
            if (!m_isStarted)
            {
                // Wait for camera to start
                if (m_cameraAccess != null && m_cameraAccess.IsPlaying)
                {
                    Debug.Log("[DEPTH MGR] Camera is now playing, scene ready");
                    m_isStarted = true;
                }
            }
        }

        private void OnDisable()
        {
            Debug.Log("[DEPTH MGR] OnDisable - clearing depth visualization");
            if (m_depthVisualization != null)
            {
                m_depthVisualization.ClearDepthPoints();
            }
        }

        private IEnumerator UpdateSpatialAnchor()
        {
            while (true)
            {
                yield return null;
                if (m_spatialAnchor == null)
                {
                    yield return CreateSpatialAnchorAndSave();
                    if (m_spatialAnchor == null)
                    {
                        continue;
                    }
                }

                if (!m_spatialAnchor.IsTracked)
                {
                    yield return RestoreSpatialAnchorTracking();
                }
            }

            IEnumerator CreateSpatialAnchorAndSave()
            {
                if (m_depthVisualization == null)
                {
                    Debug.LogError("[DEPTH MGR] Cannot create spatial anchor: m_depthVisualization is null!");
                    yield break;
                }

                m_spatialAnchor = m_depthVisualization.ContentParent.gameObject.AddComponent<OVRSpatialAnchor>();

                // Wait for localization because SaveAnchorAsync() requires the anchor to be localized first.
                while (true)
                {
                    if (m_spatialAnchor == null)
                    {
                        // Spatial Anchor destroys itself when creation fails.
                        yield break;
                    }
                    if (m_spatialAnchor.Localized)
                    {
                        break;
                    }
                    yield return null;
                }

                // Save the anchor.
                var awaiter = m_spatialAnchor.SaveAnchorAsync().GetAwaiter();
                while (!awaiter.IsCompleted)
                {
                    yield return null;
                }
                var saveAnchorResult = awaiter.GetResult();
                if (!saveAnchorResult.Success)
                {
                    LogSpatialAnchor($"SaveAnchorAsync() failed {saveAnchorResult}", LogType.Error);
                    EraseSpatialAnchor();
                    yield break;
                }
                LogSpatialAnchor("created");
            }

            IEnumerator RestoreSpatialAnchorTracking()
            {
                // Try to restore spatial anchor tracking. If restoration fails, erase it.
                LogSpatialAnchor("tracking was lost, restoring...");
                const int numRetries = 20;
                for (int i = 0; i < numRetries; i++)
                {
                    yield return new WaitForSeconds(1f);
                    if (!m_isHeadsetTracking)
                    {
                        LogSpatialAnchor($"{nameof(m_isHeadsetTracking)} is false, retrying ({i})");
                        continue;
                    }

                    var unboundAnchors = new System.Collections.Generic.List<OVRSpatialAnchor.UnboundAnchor>(1);
                    var awaiter = OVRSpatialAnchor.LoadUnboundAnchorsAsync(new[]
                    {
                        m_spatialAnchor.Uuid
                    }, unboundAnchors).GetAwaiter();
                    while (!awaiter.IsCompleted)
                    {
                        yield return null;
                    }
                    var loadResult = awaiter.GetResult();
                    if (!loadResult.Success)
                    {
                        LogSpatialAnchor($"LoadUnboundAnchorsAsync() failed {loadResult.Status}, retrying ({i})", LogType.Error);
                        continue;
                    }
                    if (unboundAnchors.Count != 0)
                    {
                        LogSpatialAnchor($"LoadUnboundAnchorsAsync() unexpected count:{unboundAnchors.Count}, retrying ({i})", LogType.Error);
                        continue;
                    }
                    yield return null;
                    if (!m_spatialAnchor.IsTracked)
                    {
                        LogSpatialAnchor($"tracking is not restored, retrying ({i})");
                        continue;
                    }

                    LogSpatialAnchor("tracking was restored successfully");
                    yield break;
                }

                LogSpatialAnchor($"tracking restoration failed after {numRetries} retries", LogType.Warning);
                EraseSpatialAnchor();
            }
        }

        private void EraseSpatialAnchor()
        {
            if (m_spatialAnchor != null)
            {
                LogSpatialAnchor("EraseSpatialAnchor");
                m_spatialAnchor.EraseAnchorAsync();
                DestroyImmediate(m_spatialAnchor);
                m_spatialAnchor = null;

                // Clear depth visualization when anchor is erased
                if (m_depthVisualization != null)
                {
                    Debug.Log("[DEPTH MGR] Clearing depth points due to anchor erasure");
                    m_depthVisualization.ClearDepthPoints();
                }
            }
        }

        private static void LogSpatialAnchor(string message, LogType logType = LogType.Log)
        {
            Debug.unityLogger.Log(logType, $"[DEPTH MGR] {nameof(OVRSpatialAnchor)}: {message}");
        }
    }
}
