// Copyright (c) Meta Platforms, Inc. and affiliates.

using System;
using UnityEngine;

namespace PassthroughCameraSamples.Shared.ControlPlane
{
    /// <summary>
    /// Thread-safe holder for the current OperatingProfile.
    ///
    /// This is the ONLY writer of runtime quality knobs. Any component that needs to read
    /// the current quality settings (encoder, scheduler, renderer) reads from here.
    /// RuntimeController is the only caller of Apply().
    /// </summary>
    public class ControlKnobs
    {
        private readonly object m_lock = new object();
        private OperatingProfile m_current;

        /// <summary>Current profile — safe to read from any thread.</summary>
        public OperatingProfile CurrentProfile
        {
            get { lock (m_lock) { return m_current; } }
        }

        /// <summary>Raised on the Unity main thread when the profile changes.</summary>
        public event Action<OperatingProfile> OnProfileChanged;

        public ControlKnobs(OperatingProfile initial)
        {
            m_current = initial ?? OperatingProfile.All[2]; // default P3
        }

        /// <summary>
        /// Apply a new profile. Raises OnProfileChanged if the profile id differs.
        /// Safe to call from any thread; the event fires synchronously on the calling thread.
        /// </summary>
        public void Apply(OperatingProfile profile)
        {
            if (profile == null) return;
            OperatingProfile previous;
            lock (m_lock)
            {
                previous = m_current;
                m_current = profile;
            }
            if (previous.Id != profile.Id)
            {
                Debug.Log($"[CONTROL KNOBS] {previous.Id} → {profile.Id}  " +
                          $"(res={profile.ResWidth}×{profile.ResHeight}, " +
                          $"q={profile.JpegQuality}, fps={profile.TargetFps:F1}, cap={profile.InflightCap})");
                OnProfileChanged?.Invoke(profile);
            }
        }
    }
}
