// Copyright (c) Meta Platforms, Inc. and affiliates.

namespace PassthroughCameraSamples.Shared.ControlPlane
{
    /// <summary>
    /// Control policy interface.  Implementations decide which OperatingProfile to use
    /// given current window metrics and the current profile id.
    ///
    /// Decide() is called once per epoch by RuntimeController AFTER the warmup guard.
    /// It must be pure (no side effects beyond internal state) and fast (called on main thread).
    /// </summary>
    public interface IPolicy
    {
        string Id { get; }

        /// <summary>
        /// Returns the proposed profile id for the next epoch.
        /// May return currentProfileId to hold the current profile.
        /// </summary>
        string Decide(MetricsSnapshot snapshot, string currentProfileId);
    }
}
