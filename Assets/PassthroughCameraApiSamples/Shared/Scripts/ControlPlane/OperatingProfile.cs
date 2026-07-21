// Copyright (c) Meta Platforms, Inc. and affiliates.

using UnityEngine;

namespace PassthroughCameraSamples.Shared.ControlPlane
{
    /// <summary>
    /// Immutable operating profile defining all control knobs for one quality tier.
    ///
    /// Resolution mapping (base camera: 1280×960):
    ///   ResFactor=1.0  → 1280×960  (full)
    ///   ResFactor=0.75 → 960×720   (3/4 linear — implemented via RenderTexture Blit)
    ///   ResFactor=0.5  → 640×480   (half linear — implemented via RenderTexture Blit)
    ///
    /// InferenceConfig.downsampleFactor is an integer divisor (1, 2, 4) and cannot express
    /// 0.75×. This class uses a float ResFactor and the inference manager applies it with
    /// Graphics.Blit so the actual encoded resolution matches the paper spec exactly.
    /// </summary>
    public sealed class OperatingProfile
    {
        public readonly string Id;
        public readonly float ResFactor;   // linear scale [0..1] applied to capture texture
        public readonly int JpegQuality;
        public readonly float TargetFps;
        public readonly int InflightCap;

        // Derived from 1280×960 base resolution
        public int ResWidth  => Mathf.RoundToInt(1280 * ResFactor);
        public int ResHeight => Mathf.RoundToInt(960  * ResFactor);

        public OperatingProfile(string id, float resFactor, int jpegQuality, float targetFps, int inflightCap)
        {
            Id           = id;
            ResFactor    = resFactor;
            JpegQuality  = jpegQuality;
            TargetFps    = targetFps;
            InflightCap  = inflightCap;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Profile table  (paper §V Table I)
        // ─────────────────────────────────────────────────────────────────────
        // P1: full 1280×960, q=80, 15 fps, cap=4  — highest quality
        // P2: full 1280×960, q=60, 10 fps, cap=4
        // P3: 960×720 (0.75×),  q=60, 10 fps, cap=4
        // P4: 960×720 (0.75×),  q=40,  8 fps, cap=3
        // P5: 640×480 (0.50×),  q=40,  5 fps, cap=2  — guard fallback
        // ─────────────────────────────────────────────────────────────────────
        public static readonly OperatingProfile[] All =
        {
            new OperatingProfile("P1", 1.00f, 80, 15f, 4),
            new OperatingProfile("P2", 1.00f, 60, 10f, 4),
            new OperatingProfile("P3", 0.75f, 60, 10f, 4),
            new OperatingProfile("P4", 0.75f, 40,  8f, 3),
            new OperatingProfile("P5", 0.50f, 40,  5f, 2),
        };

        public static OperatingProfile Get(string id)
        {
            foreach (var p in All)
                if (p.Id == id) return p;
            Debug.LogWarning($"[OperatingProfile] Unknown id '{id}', defaulting to P3");
            return All[2];
        }

        /// <summary>One step toward lower quality (P1→P2→…→P5), clamped at P5.</summary>
        public static OperatingProfile StepDown(string currentId)
        {
            int idx = System.Array.FindIndex(All, p => p.Id == currentId);
            if (idx < 0) idx = 2; // default P3
            return All[Mathf.Min(idx + 1, All.Length - 1)];
        }

        /// <summary>One step toward higher quality (P5→P4→…→P1), clamped at P1.</summary>
        public static OperatingProfile StepUp(string currentId)
        {
            int idx = System.Array.FindIndex(All, p => p.Id == currentId);
            if (idx < 0) idx = 2;
            return All[Mathf.Max(idx - 1, 0)];
        }

        public override string ToString() =>
            $"{Id}(res={ResWidth}×{ResHeight}, q={JpegQuality}, fps={TargetFps}, cap={InflightCap})";
    }
}
