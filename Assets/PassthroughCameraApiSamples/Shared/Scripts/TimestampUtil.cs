// Copyright (c) Meta Platforms, Inc. and affiliates.

using System;

namespace PassthroughCameraSamples.Shared
{
    /// <summary>
    /// Utility class for Unix timestamp generation.
    /// Provides consistent Unix millisecond timestamps across Unity and Server.
    /// </summary>
    public static class TimestampUtil
    {
        private static readonly DateTime UnixEpoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        /// <summary>
        /// Get current Unix timestamp in milliseconds.
        /// Compatible with Python's int(time.time() * 1000).
        /// </summary>
        /// <returns>Unix timestamp in milliseconds since epoch (1970-01-01 00:00:00 UTC)</returns>
        public static long GetUnixTimestampMs()
        {
            return (long)(DateTime.UtcNow - UnixEpoch).TotalMilliseconds;
        }

        /// <summary>
        /// Convert Unity Time.realtimeSinceStartup to Unix timestamp.
        /// Used for legacy compatibility.
        /// </summary>
        /// <param name="unityTime">Time.realtimeSinceStartup value</param>
        /// <param name="sessionStartTs">Unix timestamp when session started</param>
        /// <returns>Approximate Unix timestamp in milliseconds</returns>
        public static long UnityTimeToUnixMs(float unityTime, long sessionStartTs)
        {
            return sessionStartTs + (long)(unityTime * 1000f);
        }
    }
}
