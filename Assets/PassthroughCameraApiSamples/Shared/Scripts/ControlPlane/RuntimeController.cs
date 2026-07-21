// Copyright (c) Meta Platforms, Inc. and affiliates.

using System.Collections;
using UnityEngine;

namespace PassthroughCameraSamples.Shared.ControlPlane
{
    /// <summary>
    /// MonoBehaviour that drives the 1-second control epoch loop.
    ///
    /// Add this to the SAME GameObject as V3Demo_SimplifiedInferenceManager.
    /// It obtains ControlKnobs and MetricsAggregator from that component at Start().
    ///
    /// Epoch logic:
    ///   1. Snapshot metrics
    ///   2. Warmup guard: if samples &lt; WarmupMinSamples → keep current profile
    ///   3. Policy.Decide(snapshot, currentId) → proposalId
    ///   4. (if GuardEnabled) SafetyGuard.Check(snapshot, proposalId, currentId) → finalId
    ///   5. knobs.Apply(final); guard.TickEpoch(); write epoch CSV row
    /// </summary>
    public class RuntimeController : MonoBehaviour
    {
        // ── Inspector ─────────────────────────────────────────────────────────
        [Header("Policy")]
        [SerializeField] private PolicySelector m_policySelector = PolicySelector.Static_P3;
        [SerializeField] private string m_initialProfileId = "P3";

        [Header("Epoch")]
        [SerializeField] private float m_epochSeconds = 1.0f;

        [Header("Safety Guard")]
        [SerializeField] private bool m_guardEnabled = true;
        [SerializeField] private ControlPlaneConfig m_config;

        // ── Runtime state ──────────────────────────────────────────────────────
        private ControlKnobs m_knobs;
        private MetricsAggregator m_metrics;
        private IPolicy m_policy;
        private SafetyGuard m_guard;
        private EpochTelemetryWriter m_epochWriter;
        private string m_sessionId;

        // ── Lifecycle ─────────────────────────────────────────────────────────

        private void Start()
        {
            var manager = GetComponent<V3Demo_SimplifiedInferenceManager>();
            if (manager == null)
            {
                Debug.LogError("[RUNTIME CTRL] V3Demo_SimplifiedInferenceManager not found on this GameObject");
                enabled = false;
                return;
            }

            m_knobs   = manager.Knobs;
            m_metrics = manager.Metrics;
            m_sessionId = manager.SessionId;

            if (m_knobs == null || m_metrics == null)
            {
                Debug.LogError("[RUNTIME CTRL] Manager not yet initialized (Knobs or Metrics null). " +
                               "Ensure RuntimeController.Start runs after the manager's Start.");
                enabled = false;
                return;
            }

            // Apply initial profile
            m_knobs.Apply(OperatingProfile.Get(m_initialProfileId));

            // Build policy
            m_policy = BuildPolicy();

            // Build guard (always constructed; enabled flag controls whether it runs)
            if (m_config != null)
            {
                m_guard = new SafetyGuard(m_config);
            }
            else
            {
                Debug.LogWarning("[RUNTIME CTRL] No ControlPlaneConfig assigned — guard disabled");
                m_guardEnabled = false;
            }

            // Epoch telemetry
            m_epochWriter = new EpochTelemetryWriter();
            m_epochWriter.Initialize(m_sessionId);

            Debug.Log($"[RUNTIME CTRL] Started. policy={m_policy.Id}, " +
                      $"guard={m_guardEnabled}, epoch={m_epochSeconds}s, " +
                      $"initial={m_initialProfileId}");

            StartCoroutine(EpochLoop());
        }

        private void OnDestroy()
        {
            m_epochWriter?.Close();
        }

        // ── Epoch loop ─────────────────────────────────────────────────────────

        private IEnumerator EpochLoop()
        {
            while (true)
            {
                yield return new WaitForSeconds(m_epochSeconds > 0f ? m_epochSeconds : 1f);
                RunEpoch();
            }
        }

        private void RunEpoch()
        {
            MetricsSnapshot snapshot = m_metrics.Snapshot();
            string currentId = m_knobs.CurrentProfile.Id;

            // Warmup guard
            if (m_config != null && snapshot.LatencySampleCount < m_config.WarmupMinSamples)
            {
                Debug.Log($"[RUNTIME CTRL] Warmup ({snapshot.LatencySampleCount}/{m_config.WarmupMinSamples} samples)");
                WriteEpochRow(snapshot, currentId, currentId, currentId, "WARMUP");
                return;
            }

            // Policy decision
            string proposalId = m_policy.Decide(snapshot, currentId);

            // Safety guard (optional)
            string finalId;
            string guardEvent;

            if (m_guardEnabled && m_guard != null)
            {
                (finalId, guardEvent) = m_guard.Check(snapshot, proposalId, currentId);
                m_guard.TickEpoch();
            }
            else
            {
                finalId    = proposalId;
                guardEvent = "";
            }

            // Apply
            m_knobs.Apply(OperatingProfile.Get(finalId));

            WriteEpochRow(snapshot, currentId, proposalId, finalId, guardEvent);
        }

        private void WriteEpochRow(
            MetricsSnapshot s, string currentId, string proposalId, string finalId, string guardEvent)
        {
            m_epochWriter?.WriteEpoch(new EpochRecord
            {
                Ts         = TimestampUtil.GetUnixTimestampMs(),
                ProfileId  = currentId,
                P50L       = s.P50L,
                P95L       = s.P95L,
                P99L       = s.P99L,
                MeanA      = s.MeanA,
                P95A       = s.P95A,
                N          = s.PendingN,
                DropRate   = s.DropRate,
                PolicyId   = m_policy.Id,
                ProposalId = proposalId,
                FinalId    = finalId,
                GuardEvent = guardEvent,
            });
        }

        private IPolicy BuildPolicy()
        {
            switch (m_policySelector)
            {
                case PolicySelector.Static_P1:      return new StaticPolicy("P1");
                case PolicySelector.Static_P2:      return new StaticPolicy("P2");
                case PolicySelector.Static_P3:      return new StaticPolicy("P3");
                case PolicySelector.Static_P4:      return new StaticPolicy("P4");
                case PolicySelector.Static_P5:      return new StaticPolicy("P5");
                case PolicySelector.RuleLadder:
                    if (m_config == null)
                    {
                        Debug.LogError("[RUNTIME CTRL] RuleLadder requires ControlPlaneConfig — falling back to Static_P3");
                        return new StaticPolicy("P3");
                    }
                    return new RuleBasedLadderPolicy(m_config);
                default:
                    return new StaticPolicy(m_initialProfileId);
            }
        }
    }

    public enum PolicySelector
    {
        Static_P1,
        Static_P2,
        Static_P3,
        Static_P4,
        Static_P5,
        RuleLadder,
    }
}
