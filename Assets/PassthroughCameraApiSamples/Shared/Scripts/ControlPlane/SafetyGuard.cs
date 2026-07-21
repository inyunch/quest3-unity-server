// Copyright (c) Meta Platforms, Inc. and affiliates.

using UnityEngine;

namespace PassthroughCameraSamples.Shared.ControlPlane
{
    /// <summary>
    /// Safety guard that runs after IPolicy.Decide() every epoch.
    ///
    /// Priority order:
    ///   1. Hard violation  (p95L > D95 OR meanA > AMax) → force P5, start cooldown
    ///   2. Cooldown hold   (profile switch within cooldown window) → hold current
    ///   3. Accept proposal (starts cooldown only on actual switch)
    ///
    /// The guard is independent of the policy — it can be disabled for RQ3 ablation
    /// by setting GuardEnabled = false on the RuntimeController.
    /// </summary>
    public class SafetyGuard
    {
        private readonly ControlPlaneConfig m_config;
        private int m_cooldownRemaining = 0;

        public int CooldownRemaining => m_cooldownRemaining;

        public SafetyGuard(ControlPlaneConfig config)
        {
            m_config = config;
        }

        /// <summary>
        /// Evaluate the guard for this epoch and return the final profile id + event string.
        /// Call TickEpoch() once per epoch AFTER this method to advance the cooldown counter.
        /// </summary>
        /// <param name="snapshot">Current window metrics</param>
        /// <param name="proposalId">Profile id proposed by the policy</param>
        /// <param name="currentId">Profile id currently active</param>
        /// <returns>(finalProfileId, guardEvent) — guardEvent is empty string when guard is quiet</returns>
        public (string finalId, string guardEvent) Check(
            MetricsSnapshot snapshot, string proposalId, string currentId)
        {
            // 1. Hard violation → force minimum profile
            if (snapshot.P95L > m_config.D95 || snapshot.MeanA > m_config.AMax)
            {
                m_cooldownRemaining = m_config.CooldownEpochs;
                Debug.LogWarning(
                    $"[SAFETY GUARD] VIOLATION p95L={snapshot.P95L:F0}ms (D95={m_config.D95:F0}), " +
                    $"meanA={snapshot.MeanA:F0}ms (AMax={m_config.AMax:F0}) → forcing P5");
                return ("P5", "VIOLATION_FORCE_P5");
            }

            // 2. Cooldown: block any switch
            if (m_cooldownRemaining > 0 && proposalId != currentId)
            {
                return (currentId, "COOLDOWN_HOLD");
            }

            // 3. Accept proposal; if it's a switch, start cooldown
            if (proposalId != currentId)
                m_cooldownRemaining = m_config.CooldownEpochs;

            return (proposalId, "");
        }

        /// <summary>Advance the cooldown counter by one epoch. Call once per epoch after Check().</summary>
        public void TickEpoch()
        {
            if (m_cooldownRemaining > 0) m_cooldownRemaining--;
        }
    }
}
