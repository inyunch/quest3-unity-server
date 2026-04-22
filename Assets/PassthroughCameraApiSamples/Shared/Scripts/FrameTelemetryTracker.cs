// Copyright (c) Meta Platforms, Inc. and affiliates.

using System.Collections.Generic;
using UnityEngine;

namespace PassthroughCameraSamples.Shared
{
    /// <summary>
    /// Centralized frame state tracking and telemetry management.
    ///
    /// V3.0 Architecture:
    /// - Manages frame traces (Pending ??Completed ??Displayed/Dropped/Failed)
    /// - Tracks last displayed frame for drop detection
    /// - Writes telemetry to local CSV via LocalTelemetryWriter
    /// - NO server-side telemetry - Unity-only tracking
    /// - Thread-safe for UDP background operations
    ///
    /// Responsibilities:
    /// 1. Create new frame traces
    /// 2. Update frame state transitions
    /// 3. Detect dropped frames (superseded by newer)
    /// 4. Write telemetry to local CSV at final state
    /// 5. Cleanup old traces to prevent memory leaks
    /// </summary>
    public class FrameTelemetryTracker
    {
        // ====================================================================
        // Configuration
        // ====================================================================
        private readonly string m_sessionId;
        private readonly string m_sceneName;
        private readonly bool m_enableLocalTelemetry;

        // ====================================================================
        // Frame Tracking
        // ====================================================================
        private readonly Dictionary<int, FrameTrace> m_frameTraces = new Dictionary<int, FrameTrace>();
        private readonly object m_frameTracesLock = new object();
        private int m_lastDisplayedFrameId = -1;

        // ====================================================================
        // Local Telemetry Writer
        // ====================================================================
        private LocalTelemetryWriter m_localTelemetry;

        // ====================================================================
        // Statistics
        // ====================================================================
        private int m_totalFramesSent = 0;
        private int m_displayedFrames = 0;
        private int m_droppedFrames = 0;
        private int m_failedFrames = 0;

        // ====================================================================
        // Cleanup Configuration
        // ====================================================================
        private const int MAX_FRAME_TRACES = 100;
        private const float FRAME_TIMEOUT_SECONDS = 5.0f;

        /// <summary>
        /// Constructor - Initialize telemetry tracker.
        /// </summary>
        /// <param name="sessionId">Session GUID</param>
        /// <param name="sceneName">Scene name for telemetry (e.g., "Segmentation")</param>
        /// <param name="enableLocalTelemetry">Enable local CSV logging</param>
        public FrameTelemetryTracker(string sessionId, string sceneName, bool enableLocalTelemetry = true)
        {
            m_sessionId = sessionId;
            m_sceneName = sceneName;
            m_enableLocalTelemetry = enableLocalTelemetry;

            // Initialize local telemetry writer
            if (m_enableLocalTelemetry)
            {
                m_localTelemetry = new LocalTelemetryWriter();
                if (m_localTelemetry.Initialize(m_sessionId))
                {
                    Debug.Log($"[TELEMETRY] Local telemetry initialized: {m_localTelemetry.GetFilePath()}");
                }
                else
                {
                    Debug.LogWarning($"[TELEMETRY] Failed to initialize local telemetry");
                }
            }
        }

        /// <summary>
        /// Create a new frame trace for sending.
        /// </summary>
        /// <param name="frameId">Frame number</param>
        /// <param name="jpegBytes">JPEG payload size for upload tracking</param>
        /// <returns>New frame trace</returns>
        public FrameTrace CreateFrame(int frameId, int jpegBytes)
        {
            FrameTrace trace = new FrameTrace(frameId)
            {
                session_id = m_sessionId,
                upload_bytes_compressed = jpegBytes,
                upload_bytes_uncompressed = 0  // Will be filled if needed
            };

            lock (m_frameTracesLock)
            {
                m_frameTraces[frameId] = trace;
                m_totalFramesSent++;
            }

            Debug.Log($"[TELEMETRY] Created frame {frameId}, state=Pending");
            return trace;
        }

        /// <summary>
        /// Mark frame as completed when response is received.
        ///
        /// IMPORTANT: If response arrives after frame was already dropped, this will update
        /// server timing fields and rewrite the CSV row to preserve the timing data.
        /// </summary>
        /// <param name="frameId">Frame number</param>
        /// <param name="response">Server response</param>
        public void MarkFrameCompleted(int frameId, FrameResponse response)
        {
            lock (m_frameTracesLock)
            {
                if (!m_frameTraces.TryGetValue(frameId, out FrameTrace trace))
                {
                    Debug.LogWarning($"[TELEMETRY] Frame {frameId} not found in traces");
                    return;
                }

                // Remember original state to detect late responses
                FrameState originalState = trace.state;

                // Update frame trace with response data
                long receiveTime = TimestampUtil.GetUnixTimestampMs();
                trace.MarkCompleted(receiveTime);  // Won't overwrite Dropped state (FrameTrace.cs line 108)

                // Copy server timing
                trace.server_receive_ts = response.server_receive_ts;
                trace.server_process_start_ts = response.server_process_start_ts;
                trace.server_send_ts = response.server_send_ts;
                trace.server_proc_ms = response.processing_time_ms;
                trace.queue_wait_ms = response.queue_wait_ms;  // Time waiting in admission queue

                // Calculate upload/download times (residual method)
                float totalMs = trace.e2e_ms;
                float serverMs = response.server_e2e_ms;
                float networkMs = totalMs - serverMs;
                trace.upload_ms = networkMs / 2;  // Approximate split
                trace.download_ms = networkMs / 2;

                // Extract detection count for telemetry
                if (response.HasDetections())
                {
                    trace.detection_count = response.detections.Length;
                }
                else if (response.HasPose())
                {
                    trace.detection_count = response.persons.Length;
                }

                // If this is a late response for an already-dropped frame, rewrite CSV
                if (originalState == FrameState.Dropped)
                {
                    Debug.Log($"[TELEMETRY] Late response for dropped frame {frameId}, " +
                              $"server_receive_ts={trace.server_receive_ts}, " +
                              $"server_proc_ms={trace.server_proc_ms:F1}ms, rewriting CSV");
                    WriteLocalTelemetry(trace);
                }
                else
                {
                    Debug.Log($"[TELEMETRY] Frame {frameId} completed, e2e={trace.e2e_ms:F1}ms, state={trace.state}");
                }
            }
        }

        /// <summary>
        /// Mark frame as displayed and write to local telemetry.
        /// Also checks for and marks any older pending frames as dropped.
        /// </summary>
        /// <param name="frameId">Frame number</param>
        public void MarkFrameDisplayed(int frameId)
        {
            lock (m_frameTracesLock)
            {
                if (!m_frameTraces.TryGetValue(frameId, out FrameTrace trace))
                {
                    Debug.LogWarning($"[TELEMETRY] Frame {frameId} not found in traces");
                    return;
                }

                // Mark frame as displayed
                long displayTime = TimestampUtil.GetUnixTimestampMs();
                trace.MarkDisplayed(displayTime);
                m_displayedFrames++;

                // Calculate freeze metrics
                if (m_lastDisplayedFrameId >= 0)
                {
                    trace.freeze_frames = frameId - m_lastDisplayedFrameId - 1;
                    trace.freeze_duration_ms = trace.freeze_frames * 16.67f;  // Assuming 60Hz
                }

                m_lastDisplayedFrameId = frameId;

                Debug.Log($"[TELEMETRY] Frame {frameId} displayed, freeze_frames={trace.freeze_frames}");

                // Write to local telemetry immediately (final state reached)
                WriteLocalTelemetry(trace);

                // Check for dropped frames (older frames that are still pending/completed but not displayed)
                DropSupersededFrames(frameId);
            }
        }

        /// <summary>
        /// Mark frame as failed with error reason and write to local telemetry.
        /// </summary>
        /// <param name="frameId">Frame number</param>
        /// <param name="errorReason">Error message</param>
        public void MarkFrameFailed(int frameId, string errorReason)
        {
            lock (m_frameTracesLock)
            {
                if (!m_frameTraces.TryGetValue(frameId, out FrameTrace trace))
                {
                    Debug.LogWarning($"[TELEMETRY] Frame {frameId} not found in traces");
                    return;
                }

                trace.MarkFailed(errorReason);
                m_failedFrames++;

                Debug.Log($"[TELEMETRY] Frame {frameId} failed: {errorReason}");

                // Write to local telemetry immediately (final state reached)
                WriteLocalTelemetry(trace);
            }
        }

        /// <summary>
        /// Drop all frames older than displayedFrameId that are still in Pending/Completed state.
        /// These frames were superseded by the newly displayed frame.
        /// </summary>
        private void DropSupersededFrames(int displayedFrameId)
        {
            List<int> framesToDrop = new List<int>();

            foreach (var kvp in m_frameTraces)
            {
                int frameId = kvp.Key;
                FrameTrace trace = kvp.Value;

                // Skip frames newer than or equal to displayed frame
                if (frameId >= displayedFrameId)
                    continue;

                // Drop only frames that haven't reached final state yet
                if (trace.state == FrameState.Pending || trace.state == FrameState.Completed)
                {
                    framesToDrop.Add(frameId);
                }
            }

            // Mark frames as dropped
            long dropTime = TimestampUtil.GetUnixTimestampMs();
            foreach (int frameId in framesToDrop)
            {
                FrameTrace trace = m_frameTraces[frameId];
                trace.MarkDropped(dropTime, $"superseded_by_newer_{displayedFrameId}");
                m_droppedFrames++;

                Debug.Log($"[TELEMETRY] Frame {frameId} dropped (superseded by {displayedFrameId})");

                // Write to local telemetry immediately (final state reached)
                WriteLocalTelemetry(trace);
            }
        }

        /// <summary>
        /// Write frame trace to local CSV if enabled.
        /// </summary>
        private void WriteLocalTelemetry(FrameTrace trace)
        {
            if (m_localTelemetry != null)
            {
                m_localTelemetry.WriteFrameTrace(trace, m_sceneName);
            }
        }

        /// <summary>
        /// Cleanup old frame traces to prevent memory leaks.
        /// Call this periodically (e.g., every Update or every few seconds).
        /// </summary>
        public void CleanupOldTraces()
        {
            lock (m_frameTracesLock)
            {
                if (m_frameTraces.Count <= MAX_FRAME_TRACES)
                    return;

                long nowMs = TimestampUtil.GetUnixTimestampMs();
                List<int> tracesToRemove = new List<int>();

                foreach (var kvp in m_frameTraces)
                {
                    FrameTrace trace = kvp.Value;

                    // Remove traces that are in final state and old enough
                    bool isFinalState = trace.state == FrameState.Displayed ||
                                        trace.state == FrameState.Dropped ||
                                        trace.state == FrameState.Failed;

                    bool isOld = (nowMs - trace.unity_send_ts) > (FRAME_TIMEOUT_SECONDS * 1000);

                    if (isFinalState && isOld)
                    {
                        tracesToRemove.Add(kvp.Key);
                    }
                }

                foreach (int frameId in tracesToRemove)
                {
                    m_frameTraces.Remove(frameId);
                }

                if (tracesToRemove.Count > 0)
                {
                    Debug.Log($"[TELEMETRY] Cleaned up {tracesToRemove.Count} old traces, " +
                              $"remaining={m_frameTraces.Count}");
                }
            }
        }

        /// <summary>
        /// Get current statistics.
        /// </summary>
        public string GetStats()
        {
            lock (m_frameTracesLock)
            {
                return $"Sent={m_totalFramesSent}, Displayed={m_displayedFrames}, " +
                       $"Dropped={m_droppedFrames}, Failed={m_failedFrames}, " +
                       $"Pending={m_frameTraces.Count}";
            }
        }

        /// <summary>
        /// Shutdown telemetry tracker and close local CSV file.
        /// Call this in OnDestroy().
        /// </summary>
        public void Shutdown()
        {
            if (m_localTelemetry != null)
            {
                m_localTelemetry.Close();
                Debug.Log($"[TELEMETRY] Session ended, {m_localTelemetry.GetRowCount()} rows written");
                Debug.Log($"[TELEMETRY] Final stats: {GetStats()}");
            }
        }
    }
}


