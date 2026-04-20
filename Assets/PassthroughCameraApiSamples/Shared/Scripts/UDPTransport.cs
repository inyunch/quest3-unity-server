// Copyright (c) Meta Platforms, Inc. and affiliates.

using System;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace PassthroughCameraSamples.Shared
{
    /// <summary>
    /// UDP Transport Utility for Non-blocking Frame Transmission
    ///
    /// Provides shared functionality for sending inference frames via UDP with:
    /// - Fixed-size header (70 bytes)
    /// - SHA256 payload verification
    /// - JSON telemetry embedding
    /// - Non-blocking send operation
    ///
    /// Frame Format:
    /// [magic:4][session_id:16][frame_id:4][unity_send_ts:8][payload_length:4][hash:32][telemetry_length:2][telemetry:N][jpeg:M]
    /// </summary>
    public static class UDPTransport
    {
        // Magic number for frame identification (0xF2AE1234)
        private const uint FRAME_MAGIC = 0xF2AE1234;

        // Header size: 70 bytes fixed
        // magic(4) + session_id(16) + frame_id(4) + unity_send_ts(8) + payload_length(4) + hash(32) + telemetry_length(2)
        private const int HEADER_SIZE = 70;

        /// <summary>
        /// Send a frame via UDP to the server.
        ///
        /// This is a NON-BLOCKING operation - it sends the packet and returns immediately
        /// without waiting for server acknowledgment.
        /// </summary>
        /// <param name="udpClient">Initialized UDP client</param>
        /// <param name="serverIP">Server IP address</param>
        /// <param name="serverPort">Server UDP port (usually 8002)</param>
        /// <param name="trace">Frame trace with metadata</param>
        /// <param name="jpegData">JPEG-encoded image bytes</param>
        /// <param name="telemetryJson">Optional JSON telemetry from previous frame (can be null)</param>
        public static void SendFrame(
            UdpClient udpClient,
            string serverIP,
            int serverPort,
            FrameTrace trace,
            byte[] jpegData,
            string telemetryJson = null)
        {
            try
            {
                // 1. Compute SHA256 hash of JPEG payload
                string payloadHashB64 = ComputeSHA256Base64(jpegData);
                trace.payload_hash = payloadHashB64;

                // 2. Convert telemetry JSON to bytes (or empty if null)
                byte[] telemetryBytes = string.IsNullOrEmpty(telemetryJson)
                    ? new byte[0]
                    : Encoding.UTF8.GetBytes(telemetryJson);

                // 3. Build frame header
                byte[] header = BuildFrameHeader(
                    trace.session_id,
                    trace.frame_id,
                    trace.unity_send_ts,
                    jpegData.Length,
                    payloadHashB64,
                    telemetryBytes.Length
                );

                // 4. Combine: header + telemetry + jpeg
                int totalSize = header.Length + telemetryBytes.Length + jpegData.Length;
                byte[] framePacket = new byte[totalSize];

                Buffer.BlockCopy(header, 0, framePacket, 0, header.Length);
                Buffer.BlockCopy(telemetryBytes, 0, framePacket, header.Length, telemetryBytes.Length);
                Buffer.BlockCopy(jpegData, 0, framePacket, header.Length + telemetryBytes.Length, jpegData.Length);

                // 5. Send UDP packet (NON-BLOCKING) and measure send time
                IPEndPoint serverEndpoint = new IPEndPoint(IPAddress.Parse(serverIP), serverPort);

                // METHOD A: Measure actual UDP send() call time
                long sendStartMs = TimestampUtil.GetUnixTimestampMs();
                udpClient.Send(framePacket, framePacket.Length, serverEndpoint);
                long sendEndMs = TimestampUtil.GetUnixTimestampMs();

                trace.udp_send_ms = sendEndMs - sendStartMs;

                Debug.Log($"[UDP SEND] Frame {trace.session_id.Substring(0, 8)}_{trace.frame_id} sent, " +
                          $"size={totalSize} bytes (header={header.Length}, telemetry={telemetryBytes.Length}, jpeg={jpegData.Length}), " +
                          $"udp_send_time={trace.udp_send_ms}ms");
            }
            catch (Exception e)
            {
                Debug.LogError($"[UDP SEND] Failed to send frame {trace.frame_id}: {e.Message}");
            }
        }

        /// <summary>
        /// Build UDP frame header (70 bytes fixed size)
        ///
        /// Format:
        /// - magic: 4 bytes (uint32, network order)
        /// - session_id: 16 bytes (GUID bytes, network order)
        /// - frame_id: 4 bytes (int32, network order)
        /// - unity_send_ts: 8 bytes (int64, network order)
        /// - payload_length: 4 bytes (uint32, network order)
        /// - payload_hash: 32 bytes (SHA256 raw bytes)
        /// - telemetry_length: 2 bytes (uint16, network order)
        /// </summary>
        private static byte[] BuildFrameHeader(
            string sessionId,
            int frameId,
            long unitySendTs,
            int payloadLength,
            string payloadHashB64,
            int telemetryLength)
        {
            byte[] header = new byte[HEADER_SIZE];
            int offset = 0;

            // Magic number (4 bytes)
            WriteUInt32NetworkOrder(header, offset, FRAME_MAGIC);
            offset += 4;

            // Session ID (16 bytes) - parse GUID and write bytes
            Guid sessionGuid = Guid.Parse(sessionId);
            byte[] guidBytes = sessionGuid.ToByteArray();
            Buffer.BlockCopy(guidBytes, 0, header, offset, 16);
            offset += 16;

            // Frame ID (4 bytes)
            WriteInt32NetworkOrder(header, offset, frameId);
            offset += 4;

            // Unity send timestamp (8 bytes)
            WriteInt64NetworkOrder(header, offset, unitySendTs);
            offset += 8;

            // Payload length (4 bytes)
            WriteUInt32NetworkOrder(header, offset, (uint)payloadLength);
            offset += 4;

            // Payload hash (32 bytes) - decode Base64 to raw bytes
            byte[] hashBytes = Convert.FromBase64String(payloadHashB64);
            if (hashBytes.Length != 32)
            {
                Debug.LogError($"[UDP HEADER] SHA256 hash should be 32 bytes, got {hashBytes.Length}");
            }
            Buffer.BlockCopy(hashBytes, 0, header, offset, 32);
            offset += 32;

            // Telemetry length (2 bytes)
            WriteUInt16NetworkOrder(header, offset, (ushort)telemetryLength);
            offset += 2;

            if (offset != HEADER_SIZE)
            {
                Debug.LogError($"[UDP HEADER] Header size mismatch! Expected {HEADER_SIZE}, got {offset}");
            }

            return header;
        }

        /// <summary>
        /// Compute SHA256 hash of byte array and return as Base64 string
        /// </summary>
        public static string ComputeSHA256Base64(byte[] data)
        {
            using (SHA256 sha256 = SHA256.Create())
            {
                byte[] hash = sha256.ComputeHash(data);
                return Convert.ToBase64String(hash);
            }
        }

        // ========================================================================
        // Network Byte Order Helpers (Big-Endian)
        // ========================================================================

        private static void WriteUInt32NetworkOrder(byte[] buffer, int offset, uint value)
        {
            buffer[offset + 0] = (byte)((value >> 24) & 0xFF);
            buffer[offset + 1] = (byte)((value >> 16) & 0xFF);
            buffer[offset + 2] = (byte)((value >> 8) & 0xFF);
            buffer[offset + 3] = (byte)(value & 0xFF);
        }

        private static void WriteInt32NetworkOrder(byte[] buffer, int offset, int value)
        {
            WriteUInt32NetworkOrder(buffer, offset, (uint)value);
        }

        private static void WriteInt64NetworkOrder(byte[] buffer, int offset, long value)
        {
            buffer[offset + 0] = (byte)((value >> 56) & 0xFF);
            buffer[offset + 1] = (byte)((value >> 48) & 0xFF);
            buffer[offset + 2] = (byte)((value >> 40) & 0xFF);
            buffer[offset + 3] = (byte)((value >> 32) & 0xFF);
            buffer[offset + 4] = (byte)((value >> 24) & 0xFF);
            buffer[offset + 5] = (byte)((value >> 16) & 0xFF);
            buffer[offset + 6] = (byte)((value >> 8) & 0xFF);
            buffer[offset + 7] = (byte)(value & 0xFF);
        }

        private static void WriteUInt16NetworkOrder(byte[] buffer, int offset, ushort value)
        {
            buffer[offset + 0] = (byte)((value >> 8) & 0xFF);
            buffer[offset + 1] = (byte)(value & 0xFF);
        }
    }
}
