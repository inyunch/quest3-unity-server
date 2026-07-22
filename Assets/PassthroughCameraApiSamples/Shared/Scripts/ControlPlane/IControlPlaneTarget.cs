// Copyright (c) Meta Platforms, Inc. and affiliates.

namespace PassthroughCameraSamples.Shared.ControlPlane
{
    /// <summary>
    /// Implemented by any inference manager that exposes control-plane hooks to RuntimeController.
    /// Decouples RuntimeController from concrete manager types so the same controller works with
    /// SentisInferenceRunManager, V3Demo_SimplifiedInferenceManager, and future managers.
    /// </summary>
    public interface IControlPlaneTarget
    {
        ControlKnobs Knobs { get; }
        MetricsAggregator Metrics { get; }
        string SessionId { get; }
    }
}
