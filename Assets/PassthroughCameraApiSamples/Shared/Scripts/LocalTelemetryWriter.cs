using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace PassthroughCameraSamples.Shared
{
    /// <summary>
    /// Writes telemetry data directly to a local CSV file on Quest device.
    /// Bypasses N+1 delayed telemetry for immediate, complete frame tracking.
    ///
    /// File location: /sdcard/Android/data/{package_name}/files/telemetry_{session_id}.csv
    /// Access via: adb pull /sdcard/Android/data/com.YourCompany.YourApp/files/telemetry_*.csv
    /// </summary>
    public class LocalTelemetryWriter : IDisposable
    {
        private StreamWriter m_writer;
        private string m_filePath;
        private bool m_isInitialized;
        private int m_rowCount;

        /// <summary>
        /// Initialize the CSV writer with session ID
        /// </summary>
        public bool Initialize(string sessionId)
        {
            try
            {
                // Use Application.persistentDataPath which maps to /sdcard/Android/data/{package}/files/
                string directory = Application.persistentDataPath;

                // Create unique filename with timestamp
                string timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
                string filename = $"telemetry_{sessionId}_{timestamp}.csv";
                m_filePath = Path.Combine(directory, filename);

                Debug.Log($"[LOCAL TELEMETRY] Initializing CSV writer: {m_filePath}");

                // Create StreamWriter with UTF-8 encoding
                m_writer = new StreamWriter(m_filePath, false, Encoding.UTF8);

                // Write CSV header (matches Excel schema)
                WriteHeader();

                m_isInitialized = true;
                m_rowCount = 0;

                Debug.Log($"[LOCAL TELEMETRY] CSV writer ready: {m_filePath}");
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[LOCAL TELEMETRY] Failed to initialize: {ex.Message}");
                m_isInitialized = false;
                return false;
            }
        }

        /// <summary>
        /// Write CSV header matching the Excel schema
        /// </summary>
        private void WriteHeader()
        {
            var header = string.Join(",",
                "timestamp",
                "scene",
                "session_id",
                "frame_id",
                "unity_send_ts",
                "unity_receive_ts",
                "unity_display_ts",
                "unity_drop_ts",
                "server_receive_ts",
                "server_process_start_ts",
                "server_send_ts",
                "latency_ms",
                "upload_ms",
                "queue_wait_ms",
                "server_proc_ms",
                "download_ms",
                "parse_ms",
                "udp_send_ms",
                "server_pct",
                "upload_pct",
                "download_pct",
                "detection_count",
                "avg_confidence",
                "keypoint_avg_conf",
                "image_width",
                "image_height",
                "upload_bytes_uncompressed",
                "upload_bytes_compressed",
                "download_bytes_uncompressed",
                "download_bytes_compressed",
                "final_state",
                "drop_reason",
                "error_reason",
                "freeze_frames_per_frame",
                "freeze_duration_ms",
                "cumulative_freeze_frames",
                "freeze_ratio",
                "frame_gap",
                "cumulative_dropped"
            );

            m_writer.WriteLine(header);
            m_writer.Flush();  // Immediate write
        }

        /// <summary>
        /// Write a telemetry row for a frame trace
        /// </summary>
        public void WriteFrameTrace(FrameTrace trace, string sceneName)
        {
            if (!m_isInitialized || m_writer == null)
            {
                Debug.LogWarning("[LOCAL TELEMETRY] Writer not initialized, skipping write");
                return;
            }

            try
            {
                // Calculate percentages
                float totalMs = trace.e2e_ms;
                float serverPct = totalMs > 0 ? (trace.server_proc_ms / totalMs) * 100f : 0f;
                float uploadPct = totalMs > 0 ? (trace.upload_ms / totalMs) * 100f : 0f;
                float downloadPct = totalMs > 0 ? (trace.download_ms / totalMs) * 100f : 0f;

                var row = string.Join(",",
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"),  // timestamp
                    EscapeCsv(sceneName),                               // scene
                    EscapeCsv(trace.session_id),                        // session_id
                    trace.frame_id,                                     // frame_id
                    trace.unity_send_ts,                                // unity_send_ts
                    trace.unity_receive_ts,                             // unity_receive_ts
                    trace.unity_display_ts ?? 0L,                       // unity_display_ts
                    trace.unity_drop_ts ?? 0L,                          // unity_drop_ts
                    trace.server_receive_ts,                            // server_receive_ts
                    trace.server_process_start_ts,                      // server_process_start_ts
                    trace.server_send_ts,                               // server_send_ts
                    trace.e2e_ms.ToString("F2"),                        // latency_ms
                    trace.upload_ms.ToString("F2"),                     // upload_ms
                    trace.queue_wait_ms.ToString("F2"),                 // queue_wait_ms (from server response)
                    trace.server_proc_ms.ToString("F2"),                // server_proc_ms
                    trace.download_ms.ToString("F2"),                   // download_ms
                    trace.parse_ms.ToString("F2"),                      // parse_ms
                    trace.udp_send_ms.ToString("F2"),                   // udp_send_ms
                    serverPct.ToString("F1"),                           // server_pct
                    uploadPct.ToString("F1"),                           // upload_pct
                    downloadPct.ToString("F1"),                         // download_pct
                    trace.detection_count ?? 0,                         // detection_count
                    trace.avg_confidence.ToString("F4"),                // avg_confidence
                    "0",                                                // keypoint_avg_conf (TODO)
                    trace.image_width,                                  // image_width
                    trace.image_height,                                 // image_height
                    trace.upload_bytes_uncompressed,                    // upload_bytes_uncompressed
                    trace.upload_bytes_compressed,                      // upload_bytes_compressed
                    trace.download_bytes_uncompressed,                  // download_bytes_uncompressed
                    trace.download_bytes_compressed,                    // download_bytes_compressed
                    trace.state.ToString(),                             // final_state
                    EscapeCsv(trace.drop_reason ?? ""),                 // drop_reason
                    EscapeCsv(trace.error_reason ?? ""),                // error_reason
                    trace.freeze_frames,                                // freeze_frames_per_frame
                    trace.freeze_duration_ms.ToString("F2"),            // freeze_duration_ms
                    trace.cumulative_freeze_frames,                     // cumulative_freeze_frames
                    trace.freeze_ratio.ToString("F3"),                  // freeze_ratio
                    trace.frame_gap,                                    // frame_gap
                    trace.cumulative_dropped                            // cumulative_dropped
                );

                m_writer.WriteLine(row);
                m_writer.Flush();  // Immediate write for crash safety
                m_rowCount++;

                Debug.Log($"[LOCAL TELEMETRY] Wrote frame {trace.frame_id} (state={trace.state}, row={m_rowCount})");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[LOCAL TELEMETRY] Failed to write row: {ex.Message}");
            }
        }

        /// <summary>
        /// Escape CSV field (handle commas, quotes, newlines)
        /// </summary>
        private string EscapeCsv(string value)
        {
            if (string.IsNullOrEmpty(value))
                return "";

            // If contains comma, quote, or newline, wrap in quotes and escape internal quotes
            if (value.Contains(",") || value.Contains("\"") || value.Contains("\n"))
            {
                return "\"" + value.Replace("\"", "\"\"") + "\"";
            }

            return value;
        }

        /// <summary>
        /// Close the CSV writer and finalize file
        /// </summary>
        public void Close()
        {
            if (m_writer != null)
            {
                try
                {
                    m_writer.Flush();
                    m_writer.Close();
                    Debug.Log($"[LOCAL TELEMETRY] CSV closed: {m_filePath} ({m_rowCount} rows written)");
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[LOCAL TELEMETRY] Error closing writer: {ex.Message}");
                }
                finally
                {
                    m_writer = null;
                    m_isInitialized = false;
                }
            }
        }

        /// <summary>
        /// Dispose pattern for IDisposable
        /// </summary>
        public void Dispose()
        {
            Close();
        }

        /// <summary>
        /// Get the current file path (for display/debugging)
        /// </summary>
        public string GetFilePath()
        {
            return m_filePath;
        }

        /// <summary>
        /// Get the number of rows written
        /// </summary>
        public int GetRowCount()
        {
            return m_rowCount;
        }
    }
}

