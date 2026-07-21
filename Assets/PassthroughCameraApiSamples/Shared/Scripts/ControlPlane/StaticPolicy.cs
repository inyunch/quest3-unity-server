// Copyright (c) Meta Platforms, Inc. and affiliates.

namespace PassthroughCameraSamples.Shared.ControlPlane
{
    /// <summary>
    /// Policy that always returns a fixed profile — used for the RQ1 static sweep.
    /// Each of the five profiles (P1–P5) gets its own StaticPolicy instance.
    /// </summary>
    public class StaticPolicy : IPolicy
    {
        private readonly string m_profileId;

        public string Id => $"static_{m_profileId}";

        public StaticPolicy(string profileId)
        {
            m_profileId = profileId;
        }

        public string Decide(MetricsSnapshot snapshot, string currentProfileId) => m_profileId;
    }
}
