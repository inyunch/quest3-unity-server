// Copyright (c) Meta Platforms, Inc. and affiliates.

using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace PassthroughCameraSamples.Shared.ControlPlane
{
    /// <summary>
    /// One row written to the epoch CSV by RuntimeController at the end of each control epoch.
    /// </summary>
    public class EpochRecord
    {
        public long   Ts;            // Unix ms at epoch boundary
        public string ProfileId;     // profile active when epoch started
        public float  P50L;
        public float  P95L;
        public float  P99L;
        public float  MeanA;
        public float  P95A;
        public int    N;             // in-flight pending count at snapshot time
        public float  DropRate;
        public string PolicyId;
        public string ProposalId;    // what policy returned
        public string FinalId;       // what guard approved
        public string GuardEvent;    // "", "COOLDOWN_HOLD", "VIOLATION_FORCE_P5"
    }

    /// <summary>
    /// Writes one CSV row per control epoch to epoch_{session}_{timestamp}.csv.
    ///
    /// Kept separate from LocalTelemetryWriter (per-frame) to keep schemas clean.
    /// Pulled by Tools/pull_telemetry.py alongside telemetry_*.csv.
    /// </summary>
    public class EpochTelemetryWriter : IDisposable
    {
        private StreamWriter m_writer;
        private string m_filePath;
        private bool m_isInitialized;
        private int m_rowCount;

        public bool Initialize(string sessionId)
        {
            try
            {
                string timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
                string filename  = $"epoch_{sessionId}_{timestamp}.csv";
                m_filePath       = Path.Combine(Application.persistentDataPath, filename);

                m_writer = new StreamWriter(m_filePath, false, Encoding.UTF8);
                m_writer.WriteLine(string.Join(",",
                    "ts",
                    "profile_id",
                    "p50L", "p95L", "p99L",
                    "meanA", "p95A",
                    "N",
                    "drop_rate",
                    "policy_id",
                    "proposal_id",
                    "final_id",
                    "guard_event"
                ));
                m_writer.Flush();

                m_isInitialized = true;
                m_rowCount      = 0;
                Debug.Log($"[EPOCH TELEMETRY] Initialized: {m_filePath}");
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[EPOCH TELEMETRY] Init failed: {ex.Message}");
                return false;
            }
        }

        public void WriteEpoch(EpochRecord rec)
        {
            if (!m_isInitialized || m_writer == null) return;
            try
            {
                m_writer.WriteLine(string.Join(",",
                    rec.Ts,
                    rec.ProfileId ?? "",
                    rec.P50L.ToString("F2"),
                    rec.P95L.ToString("F2"),
                    rec.P99L.ToString("F2"),
                    rec.MeanA.ToString("F2"),
                    rec.P95A.ToString("F2"),
                    rec.N,
                    rec.DropRate.ToString("F4"),
                    rec.PolicyId  ?? "",
                    rec.ProposalId ?? "",
                    rec.FinalId   ?? "",
                    rec.GuardEvent ?? ""
                ));
                m_writer.Flush();
                m_rowCount++;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[EPOCH TELEMETRY] Write failed: {ex.Message}");
            }
        }

        public void Close()
        {
            if (m_writer != null)
            {
                try { m_writer.Flush(); m_writer.Close(); }
                catch { /* best-effort */ }
                finally
                {
                    m_writer        = null;
                    m_isInitialized = false;
                    Debug.Log($"[EPOCH TELEMETRY] Closed ({m_rowCount} rows): {m_filePath}");
                }
            }
        }

        public void Dispose() => Close();

        public string GetFilePath()  => m_filePath;
        public int    GetRowCount()  => m_rowCount;
    }
}
