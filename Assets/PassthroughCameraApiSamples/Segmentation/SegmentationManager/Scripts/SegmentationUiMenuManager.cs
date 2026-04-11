// Copyright (c) Meta Platforms, Inc. and affiliates.

using System;
using System.Collections;
using Meta.XR.Samples;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace PassthroughCameraSamples.Segmentation
{
    [MetaCodeSample("PassthroughCameraApiSamples-Segmentation")]
    public class SegmentationUiMenuManager : MonoBehaviour
    {
        [Header("Ui elements ref.")]
        [SerializeField] private GameObject m_loadingPanel;
        [SerializeField] private GameObject m_initialPanel;
        [SerializeField] private GameObject m_noPermissionPanel;
        [SerializeField] private Text m_labelInformation;

        public bool IsInputActive { get; set; } = false;

        public UnityEvent<bool> OnPause;

        private bool m_initialMenu;

        // start menu
        private int m_objectsDetected = 0;
        private int m_objectsIdentified = 0;

        // pause menu
        public bool IsPaused { get; private set; } = true;

        // Latest inference metrics
        private float m_e2eMs = 0f;
        private float m_uploadMs = 0f;
        private float m_serverProcMs = 0f;
        private float m_downloadMs = 0f;
        private float m_parseMs = 0f;
        private int m_uploadBytes = 0;
        private int m_downloadBytes = 0;
        private float m_avgConfidence = 0f;

        #region Unity Functions
        private IEnumerator Start()
        {
            m_initialPanel.SetActive(false);
            m_noPermissionPanel.SetActive(false);
            m_loadingPanel.SetActive(false);

            // Wait for permissions
            OnNoPermissionMenu();
            while (!OVRPermissionsRequester.IsPermissionGranted(OVRPermissionsRequester.Permission.Scene) || !OVRPermissionsRequester.IsPermissionGranted(OVRPermissionsRequester.Permission.PassthroughCameraAccess))
            {
                yield return null;
            }
            OnInitialMenu();
        }

        private void Update()
        {
            if (!IsInputActive)
                return;

            if (m_initialMenu)
            {
                InitialMenuUpdate();
            }
        }
        #endregion

        #region Ui state: No permissions Menu
        private void OnNoPermissionMenu()
        {
            m_initialMenu = false;
            IsPaused = true;
            m_initialPanel.SetActive(false);
            m_noPermissionPanel.SetActive(true);
        }
        #endregion

        #region Ui state: Initial Menu

        private void OnInitialMenu()
        {
            m_initialMenu = true;
            IsPaused = true;
            m_initialPanel.SetActive(true);
            m_noPermissionPanel.SetActive(false);
        }

        private void InitialMenuUpdate()
        {
            if (InputManager.IsButtonADownOrPinchStarted())
            {
                OnPauseMenu(false);
            }
        }

        private void OnPauseMenu(bool visible)
        {
            m_initialMenu = false;
            IsPaused = visible;

            m_initialPanel.SetActive(false);
            m_noPermissionPanel.SetActive(false);

            OnPause?.Invoke(visible);
        }
        #endregion

        #region Ui state: detection information
        private void UpdateLabelInformation()
        {
            string infoText = $"Unity Sentis version: 2.1.3\nAI model: Yolo\nDetecting objects: {m_objectsDetected}\nObjects identified: {m_objectsIdentified}";

            // Append metrics if available
            if (m_e2eMs > 0f)
            {
                // Calculate percentages
                float uploadPct = m_e2eMs > 0 ? (m_uploadMs / m_e2eMs) * 100f : 0f;
                float serverPct = m_e2eMs > 0 ? (m_serverProcMs / m_e2eMs) * 100f : 0f;
                float downloadPct = m_e2eMs > 0 ? (m_downloadMs / m_e2eMs) * 100f : 0f;
                float parsePct = m_e2eMs > 0 ? (m_parseMs / m_e2eMs) * 100f : 0f;

                infoText += $"\n\n--- Inference Metrics ---";
                infoText += $"\nE2E Latency: {m_e2eMs:F0}ms";
                infoText += $"\n├ Upload: {m_uploadMs:F0}ms ({uploadPct:F0}%)";
                infoText += $"\n├ Server: {m_serverProcMs:F0}ms ({serverPct:F0}%)";
                infoText += $"\n├ Download: {m_downloadMs:F0}ms ({downloadPct:F0}%)";
                infoText += $"\n└ Parse: {m_parseMs:F0}ms ({parsePct:F0}%)";
                infoText += $"\nData: {FormatBytes(m_uploadBytes)}↑ {FormatBytes(m_downloadBytes)}↓";
                infoText += $"\nAvg Confidence: {m_avgConfidence:F2}";
            }

            m_labelInformation.text = infoText;
        }

        public void OnObjectsDetected(int objects)
        {
            m_objectsDetected = objects;
            UpdateLabelInformation();
        }

        public void OnObjectsIndentified(int objects)
        {
            if (objects < 0)
            {
                // reset the counter
                m_objectsIdentified = 0;
            }
            else
            {
                m_objectsIdentified += objects;
            }
            UpdateLabelInformation();
        }

        /// <summary>
        /// Update inference metrics display
        /// </summary>
        public void UpdateMetrics(
            float e2eMs,
            float uploadMs,
            float serverProcMs,
            float downloadMs,
            float parseMs,
            int uploadBytes,
            int downloadBytes,
            float avgConfidence)
        {
            m_e2eMs = e2eMs;
            m_uploadMs = uploadMs;
            m_serverProcMs = serverProcMs;
            m_downloadMs = downloadMs;
            m_parseMs = parseMs;
            m_uploadBytes = uploadBytes;
            m_downloadBytes = downloadBytes;
            m_avgConfidence = avgConfidence;

            UpdateLabelInformation();
        }

        private string FormatBytes(int bytes)
        {
            if (bytes < 1024)
                return $"{bytes}B";
            if (bytes < 1024 * 1024)
                return $"{bytes / 1024f:F1}KB";
            return $"{bytes / (1024f * 1024f):F2}MB";
        }
        #endregion
    }
}
