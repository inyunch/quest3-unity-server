// Copyright (c) Meta Platforms, Inc. and affiliates.

using System;
using System.Collections;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

namespace PassthroughCameraSamples.Shared
{
    /// <summary>
    /// Manages bidirectional UDP communication for inference frames.
    ///
    /// V3.0 Architecture:
    /// - Sends frames to server via UDP (port 8002) - NON-BLOCKING
    /// - Receives responses from server via UDP (port 8003) - Background listener
    /// - Eliminates HTTP polling completely
    /// - Thread-safe response queue for Unity main thread consumption
    ///
    /// Lifecycle:
    /// 1. Initialize() - Setup UDP client and start background listener
    /// 2. SendFrame() - Non-blocking frame transmission
    /// 3. TryGetResponse() - Check if response ready (called from Update())
    /// 4. Shutdown() - Clean up resources
    /// </summary>
    public class UDPTransportManager : IDisposable
    {
        // ====================================================================
        // Configuration
        // ====================================================================
        private readonly string m_serverIP;
        private readonly int m_sendPort;      // Server UDP listener port (default: 8002)
        private readonly int m_receivePort;   // Unity UDP listener port (default: 8003)

        // ====================================================================
        // UDP Communication
        // ====================================================================
        private UdpClient m_sendClient;       // For sending frames to server
        private UdpClient m_receiveClient;    // For receiving responses from server
        private Thread m_receiveThread;       // Background thread for UDP listener
        private Thread m_heartbeatThread;     // Background thread for NAT hole punching
        private bool m_isRunning;             // Control flag for background threads

        // ====================================================================
        // Thread-Safe Response Queue
        // ====================================================================
        private readonly object m_responseLock = new object();
        private readonly System.Collections.Generic.Queue<FrameResponse> m_responseQueue =
            new System.Collections.Generic.Queue<FrameResponse>();

        // ====================================================================
        // Statistics
        // ====================================================================
        private int m_framesSent = 0;
        private int m_responsesReceived = 0;
        private int m_parseErrors = 0;
        private int m_droppedResponses = 0;

        // ====================================================================
        // Queue Management
        // ====================================================================
        private const int MAX_RESPONSE_QUEUE_SIZE = 10;  // Keep only last 10 responses to prevent memory growth

        /// <summary>
        /// Constructor - Initialize UDP transport manager.
        /// </summary>
        /// <param name="serverIP">Server IP address</param>
        /// <param name="sendPort">Server UDP listener port (default: 8002)</param>
        /// <param name="receivePort">Unity UDP listener port (default: 8003)</param>
        public UDPTransportManager(string serverIP, int sendPort = 8002, int receivePort = 8003)
        {
            m_serverIP = serverIP;
            m_sendPort = sendPort;
            m_receivePort = receivePort;
        }

        /// <summary>
        /// Initialize UDP clients and start background receiver.
        /// Call this once in Start() or Awake().
        /// </summary>
        public void Initialize()
        {
            try
            {
                // Create UDP client for sending frames
                m_sendClient = new UdpClient();
                Debug.Log($"[UDP TRANSPORT] Send client initialized (server: {m_serverIP}:{m_sendPort})");

                // Create UDP client for receiving responses
                m_receiveClient = new UdpClient(m_receivePort);
                Debug.Log($"[UDP TRANSPORT] Receive client initialized (listening on port {m_receivePort})");

                // Start background receiver thread
                m_isRunning = true;
                m_receiveThread = new Thread(ReceiveLoop)
                {
                    IsBackground = true,
                    Name = "UDP Response Receiver"
                };
                m_receiveThread.Start();
                Debug.Log($"[UDP TRANSPORT] Background receiver thread started");

                // Start background heartbeat thread (for NAT hole punching in 5G hotspot environments)
                m_heartbeatThread = new Thread(HeartbeatLoop)
                {
                    IsBackground = true,
                    Name = "UDP NAT Heartbeat"
                };
                m_heartbeatThread.Start();
                Debug.Log($"[UDP TRANSPORT] NAT heartbeat thread started (5G hotspot compatible)");
            }
            catch (Exception e)
            {
                Debug.LogError($"[UDP TRANSPORT] Failed to initialize: {e.Message}");
                throw;
            }
        }

        /// <summary>
        /// Send a frame to the server via UDP (NON-BLOCKING).
        /// Uses the existing UDPTransport utility for frame encoding.
        /// </summary>
        /// <param name="trace">Frame trace with metadata</param>
        /// <param name="jpegData">JPEG-encoded image bytes</param>
        /// <param name="telemetryJson">Optional telemetry JSON (unused in V3.0, kept for compatibility)</param>
        public void SendFrame(FrameTrace trace, byte[] jpegData, string telemetryJson = null)
        {
            try
            {
                // Use existing UDPTransport utility for frame encoding
                UDPTransport.SendFrame(
                    m_sendClient,
                    m_serverIP,
                    m_sendPort,
                    trace,
                    jpegData,
                    telemetryJson
                );

                m_framesSent++;
            }
            catch (Exception e)
            {
                Debug.LogError($"[UDP TRANSPORT] SendFrame failed for frame {trace.frame_id}: {e.Message}");
                trace.MarkFailed($"UDP send error: {e.Message}");
            }
        }

        /// <summary>
        /// Try to get next available response from queue (non-blocking).
        /// Call this from Update() in main thread.
        /// </summary>
        /// <param name="response">Output response if available</param>
        /// <returns>True if response was available</returns>
        public bool TryGetResponse(out FrameResponse response)
        {
            lock (m_responseLock)
            {
                if (m_responseQueue.Count > 0)
                {
                    response = m_responseQueue.Dequeue();
                    return true;
                }
            }

            response = null;
            return false;
        }

        /// <summary>
        /// Background thread loop for receiving UDP responses from server.
        /// Runs continuously until Shutdown() is called.
        /// </summary>
        private void ReceiveLoop()
        {
            Debug.Log($"[UDP TRANSPORT] Receive loop started on port {m_receivePort}");

            IPEndPoint remoteEndpoint = new IPEndPoint(IPAddress.Any, 0);

            while (m_isRunning)
            {
                try
                {
                    // Blocking receive (timeout handled by UdpClient)
                    byte[] data = m_receiveClient.Receive(ref remoteEndpoint);

                    if (data == null || data.Length == 0)
                    {
                        continue;
                    }

                    // Parse response
                    FrameResponse response = ParseResponse(data);
                    if (response != null)
                    {
                        // Add to thread-safe queue for main thread consumption
                        lock (m_responseLock)
                        {
                            // ✅ P0 FIX: Drop oldest response if queue full (prevents unbounded growth)
                            if (m_responseQueue.Count >= MAX_RESPONSE_QUEUE_SIZE)
                            {
                                var dropped = m_responseQueue.Dequeue();
                                m_droppedResponses++;
                                Debug.LogWarning($"[UDP TRANSPORT] Response queue full ({MAX_RESPONSE_QUEUE_SIZE}), dropped frame {dropped.frame_id}");
                            }

                            m_responseQueue.Enqueue(response);
                            m_responsesReceived++;
                        }

                        Debug.Log($"[UDP TRANSPORT] Received response for frame {response.frame_id}, " +
                                  $"queue_size={m_responseQueue.Count}, " +
                                  $"data_size={data.Length} bytes");
                    }
                }
                catch (SocketException e)
                {
                    // Socket closed or timeout - check if we're still running
                    if (m_isRunning)
                    {
                        Debug.LogWarning($"[UDP TRANSPORT] Socket exception in receive loop: {e.Message}");
                    }
                }
                catch (Exception e)
                {
                    Debug.LogError($"[UDP TRANSPORT] Error in receive loop: {e.Message}");
                }
            }

            Debug.Log($"[UDP TRANSPORT] Receive loop stopped");
        }

        /// <summary>
        /// Background thread loop for NAT hole punching heartbeat.
        /// Sends periodic heartbeat from receive socket to keep NAT mapping alive.
        /// Required for 5G hotspot environments where inbound UDP is blocked by NAT.
        ///
        /// How it works:
        /// 1. Uses receive socket (bound to port 8003) to send heartbeat to server
        /// 2. This creates NAT mapping: Quest:8003 ↔ Server:8002
        /// 3. When server sends response (Server:8002 → Quest:8003), NAT allows it through
        ///
        /// Note: Heartbeat packet is only 9 bytes, server will ignore it (requires >= 70 bytes)
        /// </summary>
        private void HeartbeatLoop()
        {
            byte[] heartbeat = Encoding.UTF8.GetBytes("HEARTBEAT");  // 9 bytes
            IPEndPoint serverEndpoint = new IPEndPoint(IPAddress.Parse(m_serverIP), m_sendPort);

            Debug.Log($"[UDP HEARTBEAT] Loop started, target: {m_serverIP}:{m_sendPort}");

            while (m_isRunning)
            {
                try
                {
                    // CRITICAL: Use receive socket to send heartbeat (creates NAT mapping)
                    m_receiveClient.Send(heartbeat, heartbeat.Length, serverEndpoint);

                    // Send every 2 seconds (NAT mappings typically expire after 30-60 seconds)
                    Thread.Sleep(2000);
                }
                catch (Exception e)
                {
                    // Continue even on errors (socket might not be ready yet)
                    if (m_isRunning)
                    {
                        Debug.LogWarning($"[UDP HEARTBEAT] Send failed: {e.Message}");
                    }
                    Thread.Sleep(2000);
                }
            }

            Debug.Log($"[UDP HEARTBEAT] Loop stopped");
        }

        /// <summary>
        /// Parse UDP response bytes into FrameResponse object.
        /// Expected format: UTF-8 JSON string
        /// </summary>
        private FrameResponse ParseResponse(byte[] data)
        {
            try
            {
                // Convert bytes to UTF-8 string
                string jsonResponse = Encoding.UTF8.GetString(data);

                // Parse JSON to FrameResponse
                FrameResponse response = JsonUtility.FromJson<FrameResponse>(jsonResponse);

                if (response == null)
                {
                    Debug.LogError($"[UDP TRANSPORT] Failed to parse response JSON");
                    m_parseErrors++;
                    return null;
                }

                return response;
            }
            catch (Exception e)
            {
                Debug.LogError($"[UDP TRANSPORT] Parse error: {e.Message}");
                m_parseErrors++;
                return null;
            }
        }

        /// <summary>
        /// Get current statistics.
        /// </summary>
        public string GetStats()
        {
            lock (m_responseLock)
            {
                return $"Sent={m_framesSent}, Received={m_responsesReceived}, " +
                       $"Dropped={m_droppedResponses}, QueueSize={m_responseQueue.Count}, ParseErrors={m_parseErrors}";
            }
        }

        /// <summary>
        /// Shutdown UDP transport and clean up resources.
        /// Call this in OnDestroy().
        /// </summary>
        public void Shutdown()
        {
            Debug.Log($"[UDP TRANSPORT] Shutting down... {GetStats()}");

            // Stop all background threads (receive + heartbeat)
            m_isRunning = false;

            // Close UDP clients
            try
            {
                m_sendClient?.Close();
                m_receiveClient?.Close();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[UDP TRANSPORT] Error closing UDP clients: {e.Message}");
            }

            // Wait for background threads to finish
            if (m_receiveThread != null && m_receiveThread.IsAlive)
            {
                m_receiveThread.Join(1000);  // Wait max 1 second
            }

            if (m_heartbeatThread != null && m_heartbeatThread.IsAlive)
            {
                m_heartbeatThread.Join(1000);  // Wait max 1 second
            }

            Debug.Log($"[UDP TRANSPORT] Shutdown complete");
        }

        /// <summary>
        /// IDisposable implementation
        /// </summary>
        public void Dispose()
        {
            Shutdown();
        }
    }
}


