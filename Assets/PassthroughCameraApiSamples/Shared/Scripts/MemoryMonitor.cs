// Copyright (c) Meta Platforms, Inc. and affiliates.

using UnityEngine;
using UnityEngine.Profiling;

namespace PassthroughCameraSamples.Shared
{
    /// <summary>
    /// Monitors memory usage and logs warnings when thresholds exceeded.
    /// Attach to any scene GameObject for automatic monitoring.
    ///
    /// Usage:
    /// 1. Attach to any GameObject in scene (e.g., InferenceManager)
    /// 2. Configure log interval and warning threshold in Inspector
    /// 3. Run app and monitor Unity logs for [MEMORY] entries
    ///
    /// Memory leak detection:
    /// - Monitors Total Reserved, Total Allocated, and GPU memory
    /// - Warns when usage exceeds configured threshold
    /// - Optionally triggers GC when memory is very high
    /// </summary>
    public class MemoryMonitor : MonoBehaviour
    {
        [Header("Monitoring Configuration")]
        [SerializeField]
        [Tooltip("Log interval in seconds (default: 5s)")]
        private float m_logInterval = 5.0f;

        [SerializeField]
        [Tooltip("Warning threshold in MB (default: 200 MB)")]
        private long m_warningThresholdMB = 200;

        [Header("Auto GC Configuration")]
        [SerializeField]
        [Tooltip("Enable automatic GC when memory exceeds 1.5x threshold (default: true)")]
        private bool m_autoGC = true;

        private float m_nextLogTime = 0f;

        private void Update()
        {
            float currentTime = Time.time;
            if (currentTime >= m_nextLogTime)
            {
                m_nextLogTime = currentTime + m_logInterval;

                // Get memory stats from Unity Profiler
                long totalMemoryMB = Profiler.GetTotalReservedMemoryLong() / 1048576;
                long usedMemoryMB = Profiler.GetTotalAllocatedMemoryLong() / 1048576;
                long textureMemoryMB = Profiler.GetAllocatedMemoryForGraphicsDriver() / 1048576;

                // Log current memory usage
                Debug.Log($"[MEMORY] Total: {totalMemoryMB} MB, Used: {usedMemoryMB} MB, GPU: {textureMemoryMB} MB");

                // Check if memory usage exceeds warning threshold
                if (usedMemoryMB > m_warningThresholdMB)
                {
                    Debug.LogWarning($"[MEMORY] High memory usage detected: {usedMemoryMB} MB (threshold: {m_warningThresholdMB} MB)");

                    // Force GC if very high and auto GC is enabled
                    if (m_autoGC && usedMemoryMB > m_warningThresholdMB * 1.5f)
                    {
                        Debug.LogWarning($"[MEMORY] Memory usage critical ({usedMemoryMB} MB), forcing garbage collection...");
                        System.GC.Collect();
                        Resources.UnloadUnusedAssets();

                        // Log memory after GC
                        long usedAfterGC = Profiler.GetTotalAllocatedMemoryLong() / 1048576;
                        Debug.Log($"[MEMORY] GC complete. Used memory: {usedMemoryMB} MB → {usedAfterGC} MB (freed: {usedMemoryMB - usedAfterGC} MB)");
                    }
                }
            }
        }

        /// <summary>
        /// Get current memory statistics.
        /// Useful for external systems to query memory state.
        /// </summary>
        public MemoryStats GetMemoryStats()
        {
            return new MemoryStats
            {
                totalReservedMB = Profiler.GetTotalReservedMemoryLong() / 1048576,
                totalAllocatedMB = Profiler.GetTotalAllocatedMemoryLong() / 1048576,
                textureMemoryMB = Profiler.GetAllocatedMemoryForGraphicsDriver() / 1048576
            };
        }
    }

    /// <summary>
    /// Memory statistics data structure.
    /// </summary>
    public struct MemoryStats
    {
        public long totalReservedMB;
        public long totalAllocatedMB;
        public long textureMemoryMB;

        public override string ToString()
        {
            return $"Total: {totalReservedMB} MB, Used: {totalAllocatedMB} MB, GPU: {textureMemoryMB} MB";
        }
    }
}
