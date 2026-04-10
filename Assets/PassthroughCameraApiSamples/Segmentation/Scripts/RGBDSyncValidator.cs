// Copyright (c) Meta Platforms, Inc. and affiliates.

using System;
using UnityEngine;

namespace PassthroughCameraSamples.Segmentation
{
    /// <summary>
    /// Validates synchronization between RGB and Depth captures.
    ///
    /// Ensures RGB and depth frames are captured close enough in time
    /// to be considered synchronized for RGB-D processing.
    /// </summary>
    public static class RGBDSyncValidator
    {
        /// <summary>
        /// Default sync threshold - frames within 10ms are considered synchronized.
        /// </summary>
        public const float DEFAULT_SYNC_THRESHOLD_MS = 10.0f;

        /// <summary>
        /// Strict sync threshold - frames within 5ms are considered well-synchronized.
        /// </summary>
        public const float STRICT_SYNC_THRESHOLD_MS = 5.0f;

        /// <summary>
        /// Loose sync threshold - frames within 50ms are acceptable for debugging.
        /// </summary>
        public const float LOOSE_SYNC_THRESHOLD_MS = 50.0f;

        /// <summary>
        /// Validate RGB-Depth synchronization.
        /// </summary>
        /// <param name="rgbTimestamp">RGB capture timestamp (in seconds)</param>
        /// <param name="depthTimestamp">Depth capture timestamp (in seconds)</param>
        /// <param name="thresholdMs">Sync threshold in milliseconds</param>
        /// <returns>Sync validation result</returns>
        public static SyncValidationResult Validate(
            float rgbTimestamp,
            float depthTimestamp,
            float thresholdMs = DEFAULT_SYNC_THRESHOLD_MS)
        {
            // Calculate absolute time gap
            float syncGapMs = Mathf.Abs(rgbTimestamp - depthTimestamp) * 1000f;

            // Determine sync quality
            SyncQuality quality;
            if (syncGapMs <= STRICT_SYNC_THRESHOLD_MS)
            {
                quality = SyncQuality.Excellent;
            }
            else if (syncGapMs <= DEFAULT_SYNC_THRESHOLD_MS)
            {
                quality = SyncQuality.Good;
            }
            else if (syncGapMs <= LOOSE_SYNC_THRESHOLD_MS)
            {
                quality = SyncQuality.Acceptable;
            }
            else
            {
                quality = SyncQuality.Poor;
            }

            bool syncOk = syncGapMs <= thresholdMs;

            return new SyncValidationResult
            {
                syncOk = syncOk,
                syncGapMs = syncGapMs,
                quality = quality,
                rgbTimestamp = rgbTimestamp,
                depthTimestamp = depthTimestamp,
                thresholdMs = thresholdMs
            };
        }

        /// <summary>
        /// Check if RGB captured before depth (as expected in typical flow).
        /// </summary>
        public static bool RGBCapturedFirst(float rgbTimestamp, float depthTimestamp)
        {
            return rgbTimestamp <= depthTimestamp;
        }

        /// <summary>
        /// Sync quality levels.
        /// </summary>
        public enum SyncQuality
        {
            Excellent,  // < 5ms
            Good,       // 5-10ms
            Acceptable, // 10-50ms
            Poor        // > 50ms
        }

        /// <summary>
        /// Sync validation result.
        /// </summary>
        [Serializable]
        public struct SyncValidationResult
        {
            public bool syncOk;
            public float syncGapMs;
            public SyncQuality quality;
            public float rgbTimestamp;
            public float depthTimestamp;
            public float thresholdMs;

            public override string ToString()
            {
                string status = syncOk ? "OK" : "FAILED";
                return $"Sync {status}: gap={syncGapMs:F2}ms (threshold={thresholdMs:F1}ms), quality={quality}";
            }

            /// <summary>
            /// Get a compact log string for CSV export.
            /// </summary>
            public string ToLogString()
            {
                return $"{syncOk},{syncGapMs:F3},{quality}";
            }
        }

        /// <summary>
        /// Sync statistics tracker for experiment analysis.
        /// </summary>
        public class SyncStats
        {
            private int m_totalFrames = 0;
            private int m_syncOkCount = 0;
            private float m_minGapMs = float.MaxValue;
            private float m_maxGapMs = float.MinValue;
            private float m_totalGapMs = 0f;

            public void AddSample(SyncValidationResult result)
            {
                m_totalFrames++;

                if (result.syncOk)
                {
                    m_syncOkCount++;
                }

                m_minGapMs = Mathf.Min(m_minGapMs, result.syncGapMs);
                m_maxGapMs = Mathf.Max(m_maxGapMs, result.syncGapMs);
                m_totalGapMs += result.syncGapMs;
            }

            public float SyncSuccessRate => m_totalFrames > 0 ? (float)m_syncOkCount / m_totalFrames : 0f;
            public float AverageSyncGapMs => m_totalFrames > 0 ? m_totalGapMs / m_totalFrames : 0f;
            public float MinSyncGapMs => m_minGapMs != float.MaxValue ? m_minGapMs : 0f;
            public float MaxSyncGapMs => m_maxGapMs != float.MinValue ? m_maxGapMs : 0f;
            public int TotalFrames => m_totalFrames;
            public int SyncOkCount => m_syncOkCount;

            public void Reset()
            {
                m_totalFrames = 0;
                m_syncOkCount = 0;
                m_minGapMs = float.MaxValue;
                m_maxGapMs = float.MinValue;
                m_totalGapMs = 0f;
            }

            public override string ToString()
            {
                return $"Sync Stats: {m_syncOkCount}/{m_totalFrames} OK ({SyncSuccessRate * 100:F1}%), " +
                       $"avg={AverageSyncGapMs:F2}ms, min={MinSyncGapMs:F2}ms, max={MaxSyncGapMs:F2}ms";
            }
        }
    }
}
