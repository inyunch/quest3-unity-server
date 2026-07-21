// Copyright (c) Meta Platforms, Inc. and affiliates.

using UnityEngine;

namespace PassthroughCameraSamples.Shared.ControlPlane
{
    /// <summary>
    /// ScriptableObject that holds all tuneable control-plane constants.
    ///
    /// CALIBRATION WORKFLOW:
    ///   1. Run a 2-minute baseline session with StaticPolicy(P3) in a clean environment.
    ///   2. Pull telemetry and compute baseline_p95 and baseline_meanLatency.
    ///   3. Set D95  = 1.5 × baseline_p95
    ///      Set D99  = 2.0 × baseline_p95
    ///      Set AMax = 1.5 × (baseline_meanLatency + 1000 / targetFps_baseline)
    ///   4. These bounds are then frozen; do NOT change them between experiment conditions.
    ///
    /// Create via: Right-click Assets → Create → Passthrough Camera Samples → Control Plane Config
    /// </summary>
    [CreateAssetMenu(
        fileName = "ControlPlaneConfig",
        menuName  = "Passthrough Camera Samples/Control Plane Config")]
    public class ControlPlaneConfig : ScriptableObject
    {
        [Header("Safety Guard Bounds  (set from baseline run — see class comment)")]
        [Tooltip("D95 = 1.5 × baseline p95 E2E latency (ms). Hard violation trips at p95L > D95.")]
        public float D95 = 450f;

        [Tooltip("D99 = 2.0 × baseline p95 E2E latency (ms). Logged but not used for forced-P5.")]
        public float D99 = 600f;

        [Tooltip("AMax = 1.5 × (baseline_meanLatency + 1000/targetFps_baseline) (ms). Hard violation trips at meanA > AMax.")]
        public float AMax = 600f;

        [Header("Ladder Policy Thresholds")]
        [Tooltip("Step-down trigger: p95L > StepDownFrac*D95 OR meanA > StepDownFrac*AMax")]
        [Range(0.5f, 1.0f)]
        public float StepDownFrac = 0.80f;

        [Tooltip("Step-up trigger: p95L < StepUpFrac*D95 AND meanA < StepUpFrac*AMax for QuietEpochsRequired consecutive epochs")]
        [Range(0.3f, 0.8f)]
        public float StepUpFrac = 0.60f;

        [Tooltip("Number of consecutive quiet epochs required before stepping up")]
        [Range(1, 5)]
        public int QuietEpochsRequired = 2;

        [Header("General")]
        [Tooltip("Epoch duration in seconds")]
        [Range(0.5f, 5f)]
        public float EpochSeconds = 1.0f;

        [Tooltip("Minimum latency samples before policy fires (warmup guard)")]
        [Range(10, 100)]
        public int WarmupMinSamples = 30;

        [Tooltip("Epochs to hold current profile after any switch (prevents thrashing)")]
        [Range(1, 10)]
        public int CooldownEpochs = 2;
    }
}
