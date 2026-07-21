// Copyright (c) Meta Platforms, Inc. and affiliates.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace PassthroughCameraSamples.Shared.ControlPlane
{
    /// <summary>Snapshot of window metrics returned by MetricsAggregator.Snapshot().</summary>
    public class MetricsSnapshot
    {
        public float P50L;              // p50 E2E latency (ms)
        public float P95L;              // p95 E2E latency (ms)
        public float P99L;              // p99 E2E latency (ms)
        public float MeanA;             // mean render-age (ms) — age of currently displayed frame
        public float P95A;              // p95 render-age (ms)
        public float ThroughputBps;     // upload throughput (bits/s)
        public float DropRate;          // N-gate + server drops / total sent [0,1]
        public int   PendingN;          // in-flight frame count (Pending state)
        public int   LatencySampleCount;// number of latency samples in current window
    }

    /// <summary>
    /// Sliding-window metrics aggregator for the control plane.
    ///
    /// Window: last 100 latency samples OR last 10 s, whichever fills first.
    /// Percentiles computed on Snapshot() via sorted copy — negligible at 1 Hz poll rate.
    ///
    /// Thread-safe: PushLatency / PushAge may be called from background receive thread;
    /// PushBytes / RecordSent / RecordDropped from the Unity main thread.
    /// </summary>
    public class MetricsAggregator
    {
        private const int   MAX_SAMPLES     = 100;
        private const float WINDOW_SECONDS  = 10f;

        private readonly object m_lock = new object();

        // (value, realtimeSinceStartup at push)
        private readonly Queue<(float v, float t)> m_latency = new Queue<(float, float)>();
        private readonly Queue<(float v, float t)> m_age     = new Queue<(float, float)>();

        // Throughput
        private long  m_bytesAccum         = 0;
        private float m_throughputWindowStart = 0f;
        private float m_lastThroughputBps  = 0f;

        // Drop rate
        private int m_totalSent    = 0;
        private int m_totalDropped = 0;

        // Delegate to query pending count without coupling to FrameTelemetryTracker
        private readonly Func<int> m_getPendingCount;

        public MetricsAggregator(Func<int> getPendingCount)
        {
            m_getPendingCount      = getPendingCount;
            m_throughputWindowStart = Time.realtimeSinceStartup;
        }

        // ── Push methods ─────────────────────────────────────────────────────

        /// <summary>Call from HandleResponse path with the frame's E2E latency (ms).</summary>
        public void PushLatency(float e2eMs)
        {
            lock (m_lock) { PushSample(m_latency, e2eMs); }
        }

        /// <summary>Call every rendered frame with age = now − unity_send_ts of displayed frame (ms).</summary>
        public void PushAge(float ageMs)
        {
            lock (m_lock) { PushSample(m_age, ageMs); }
        }

        /// <summary>Call after each UDP send with the compressed JPEG byte count.</summary>
        public void PushBytes(int bytes)
        {
            lock (m_lock)
            {
                m_bytesAccum += bytes;
                float now     = Time.realtimeSinceStartup;
                float elapsed  = now - m_throughputWindowStart;
                if (elapsed >= 1f)
                {
                    m_lastThroughputBps    = (m_bytesAccum * 8f) / elapsed;
                    m_bytesAccum           = 0;
                    m_throughputWindowStart = now;
                }
            }
        }

        public void RecordSent()    { lock (m_lock) { m_totalSent++;    } }
        public void RecordDropped() { lock (m_lock) { m_totalDropped++; } }

        // ── Snapshot ─────────────────────────────────────────────────────────

        /// <summary>Returns a consistent snapshot of all window metrics. Safe to call from any thread.</summary>
        public MetricsSnapshot Snapshot()
        {
            lock (m_lock)
            {
                float[] lat  = ExtractValues(m_latency);
                float[] ages = ExtractValues(m_age);
                Array.Sort(lat);
                Array.Sort(ages);

                return new MetricsSnapshot
                {
                    P50L               = Percentile(lat,  0.50f),
                    P95L               = Percentile(lat,  0.95f),
                    P99L               = Percentile(lat,  0.99f),
                    MeanA              = Mean(ages),
                    P95A               = Percentile(ages, 0.95f),
                    ThroughputBps      = m_lastThroughputBps,
                    DropRate           = m_totalSent > 0 ? (float)m_totalDropped / m_totalSent : 0f,
                    PendingN           = m_getPendingCount?.Invoke() ?? 0,
                    LatencySampleCount = lat.Length,
                };
            }
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        private void PushSample(Queue<(float v, float t)> q, float value)
        {
            float now = Time.realtimeSinceStartup;
            q.Enqueue((value, now));
            // Evict by age
            while (q.Count > 0 && (now - q.Peek().t) > WINDOW_SECONDS)
                q.Dequeue();
            // Evict by count
            while (q.Count > MAX_SAMPLES)
                q.Dequeue();
        }

        private static float[] ExtractValues(Queue<(float v, float t)> q)
        {
            float[] arr = new float[q.Count];
            int i = 0;
            foreach (var (v, _) in q) arr[i++] = v;
            return arr;
        }

        private static float Percentile(float[] sorted, float p)
        {
            if (sorted.Length == 0) return 0f;
            float rank = p * (sorted.Length - 1);
            int   lo   = (int)rank;
            int   hi   = Math.Min(lo + 1, sorted.Length - 1);
            return sorted[lo] * (1f - (rank - lo)) + sorted[hi] * (rank - lo);
        }

        private static float Mean(float[] arr)
        {
            if (arr.Length == 0) return 0f;
            float s = 0f;
            foreach (float v in arr) s += v;
            return s / arr.Length;
        }
    }
}
