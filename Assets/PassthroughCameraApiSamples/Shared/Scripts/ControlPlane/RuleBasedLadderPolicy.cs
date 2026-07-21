// Copyright (c) Meta Platforms, Inc. and affiliates.

using UnityEngine;

namespace PassthroughCameraSamples.Shared.ControlPlane
{
    /// <summary>
    /// Rule-based ladder policy that steps up/down one profile tier per epoch.
    ///
    /// Algorithm (per epoch, after warmup):
    ///   if p95L > 0.80·D95  OR  meanA > 0.80·AMax  → step down (immediate), reset quiet counter
    ///   elif p95L &lt; 0.60·D95 AND meanA &lt; 0.60·AMax → increment quiet counter;
    ///        if quiet counter ≥ QuietEpochsRequired → step up, reset counter
    ///   else → hold (reset quiet counter)
    ///
    /// The guard runs after Decide() and may override the proposal (VIOLATION_FORCE_P5 /
    /// COOLDOWN_HOLD).  This policy is unaware of the guard — they are independent.
    /// </summary>
    public class RuleBasedLadderPolicy : IPolicy
    {
        private readonly ControlPlaneConfig m_config;
        private int m_quietCount = 0;

        public string Id => "rule_ladder_v1";

        public RuleBasedLadderPolicy(ControlPlaneConfig config)
        {
            m_config = config;
        }

        public string Decide(MetricsSnapshot snapshot, string currentProfileId)
        {
            float stepDownThreshL = m_config.StepDownFrac * m_config.D95;
            float stepDownThreshA = m_config.StepDownFrac * m_config.AMax;
            float stepUpThreshL   = m_config.StepUpFrac   * m_config.D95;
            float stepUpThreshA   = m_config.StepUpFrac   * m_config.AMax;

            // Step-down condition
            if (snapshot.P95L > stepDownThreshL || snapshot.MeanA > stepDownThreshA)
            {
                m_quietCount = 0;
                var down = OperatingProfile.StepDown(currentProfileId);
                if (down.Id != currentProfileId)
                    Debug.Log($"[LADDER] Step down: {currentProfileId}→{down.Id}  " +
                              $"(p95L={snapshot.P95L:F0}>{stepDownThreshL:F0} OR " +
                              $"meanA={snapshot.MeanA:F0}>{stepDownThreshA:F0})");
                return down.Id;
            }

            // Step-up condition (requires consecutive quiet epochs)
            if (snapshot.P95L < stepUpThreshL && snapshot.MeanA < stepUpThreshA)
            {
                m_quietCount++;
                if (m_quietCount >= m_config.QuietEpochsRequired)
                {
                    m_quietCount = 0;
                    var up = OperatingProfile.StepUp(currentProfileId);
                    if (up.Id != currentProfileId)
                        Debug.Log($"[LADDER] Step up: {currentProfileId}→{up.Id}  " +
                                  $"(quiet for {m_config.QuietEpochsRequired} epochs)");
                    return up.Id;
                }
                return currentProfileId;
            }

            // Hold
            m_quietCount = 0;
            return currentProfileId;
        }
    }
}
