// Copyright (c) Meta Platforms, Inc. and affiliates.

using System;
using UnityEngine;

namespace PassthroughCameraSamples.Shared
{
    /// <summary>
    /// Tracks the complete lifecycle of a single inference frame from send to final state.
    /// Used for parallel processing architecture where multiple frames may be in-flight simultaneously.
    /// UPDATED: Now uses Unix millisecond timestamps for cross-system consistency.
    /// </summary>
    [Serializable]
    public class FrameTrace
    {
        // === Identity (PRIMARY KEY: session_id + frame_id) ===
        public string session_id;        // REQUIRED - recording session GUID (distinguishes across runs)
        public int frame_id;             // REQUIRED - sequential frame number within session

        // === Unity Timestamps (Unix milliseconds since epoch) ===
        public long unity_send_ts;       // REQUIRED - when HTTP request sent
        public long unity_receive_ts;    // CONDITIONAL - when response received (0 if pending/failed)
        public long? unity_display_ts;   // OPTIONAL - when displayed (null if not displayed)
        public long? unity_drop_ts;      // OPTIONAL - when dropped (null if not dropped)

        // === Server Timestamps (Unix milliseconds since epoch) ===
        public long server_receive_ts;   // CONDITIONAL - from response (0 if request didn't reach server)
        public long server_send_ts;      // CONDITIONAL - from response (0 if server didn't respond)

        // === Derived Timing (calculated) ===
        public float e2e_ms;             // REQUIRED - unity_receive_ts - unity_send_ts
        public float server_proc_ms;     // OPTIONAL - from response.processing_time_ms
        public float upload_ms;          // OPTIONAL - estimated
        public float download_ms;        // OPTIONAL - estimated
        public float parse_ms;           // OPTIONAL - measured in Unity

        // === State Tracking ===
        public FrameState state;         // REQUIRED - current lifecycle state
        public string drop_reason;       // OPTIONAL - why dropped (if Dropped)
        public string error_reason;      // OPTIONAL - error message (if Failed)
        public int freeze_frames;        // PRIORITY 3 - Unity frames between this display and previous display

        // === Payload Size Tracking ===
        public int upload_bytes_uncompressed;  // Original RGB24 size
        public int upload_bytes_compressed;    // JPEG compressed size
        public int download_bytes_uncompressed; // JSON text size
        public int download_bytes_compressed;   // Gzip compressed size

        // === Results (generic object to avoid tight coupling) ===
        public object response;          // OPTIONAL - cached response (null if pending/failed)
        public int? detection_count;     // OPTIONAL - from response (nullable, can be 0 for no detections)
        public float avg_confidence;     // OPTIONAL - from response

        /// <summary>
        /// Constructor - creates a new frame trace at send time.
        /// session_id must be provided by caller.
        /// </summary>
        public FrameTrace(int frameId)
        {
            frame_id = frameId;
            unity_send_ts = TimestampUtil.GetUnixTimestampMs();
            state = FrameState.Pending;

            // Initialize nullable fields to null (not 0)
            unity_display_ts = null;
            unity_drop_ts = null;
            detection_count = null;
        }

        /// <summary>
        /// Mark frame as completed when response is received.
        /// receiveTime should be Unix milliseconds.
        /// </summary>
        public void MarkCompleted(long receiveTime)
        {
            unity_receive_ts = receiveTime;
            e2e_ms = unity_receive_ts - unity_send_ts;  // Already in ms
            state = FrameState.Completed;
        }

        /// <summary>
        /// Mark frame as displayed.
        /// displayTime should be Unix milliseconds.
        /// </summary>
        public void MarkDisplayed(long displayTime)
        {
            unity_display_ts = displayTime;
            state = FrameState.Displayed;
        }

        /// <summary>
        /// Mark frame as dropped with reason.
        /// dropTime should be Unix milliseconds.
        /// </summary>
        public void MarkDropped(long dropTime, string reason)
        {
            unity_drop_ts = dropTime;
            drop_reason = reason;
            state = FrameState.Dropped;
        }

        /// <summary>
        /// Mark frame as failed with error message
        /// </summary>
        public void MarkFailed(string error)
        {
            error_reason = error;
            state = FrameState.Failed;
        }

        /// <summary>
        /// Get a debug string for logging
        /// </summary>
        public override string ToString()
        {
            return $"[FRAME {frame_id}] State={state} " +
                   $"send={unity_send_ts:F3} recv={unity_receive_ts:F3} " +
                   $"display={unity_display_ts:F3} drop={unity_drop_ts:F3} " +
                   $"e2e={e2e_ms:F1}ms server={server_proc_ms:F1}ms";
        }
    }

    /// <summary>
    /// Frame lifecycle states for parallel processing
    /// </summary>
    public enum FrameState
    {
        Pending,    // Request sent, waiting for response
        Completed,  // Response received, not yet displayed
        Displayed,  // Successfully displayed to user
        Dropped,    // Received but superseded before display (NEW definition)
        Failed      // Network error, timeout, or parse failure
    }
}
