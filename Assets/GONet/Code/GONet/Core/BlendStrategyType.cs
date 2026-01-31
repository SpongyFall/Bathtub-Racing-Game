/* GONet (TM, serial number 88592370), Copyright (c) 2019-2025 Galore Interactive LLC - All Rights Reserved
 * Unauthorized copying of this file, via any medium is strictly prohibited
 * Proprietary and confidential, email: contactus@unitygo.net
 */

namespace GONet.Core
{
    /// <summary>
    /// Blending strategies for SoA value blending - Burst-compatible enum (no virtual dispatch).
    /// Each strategy handles temporal extrapolation/interpolation differently.
    /// Selected via enum switch in Burst jobs rather than virtual dispatch.
    /// </summary>
    public enum BlendStrategyType : byte
    {
        /// <summary>
        /// Linear extrapolation from two most recent samples.
        /// Good for: Constant velocity movement (non-physics objects).
        /// Formula: pos = newest + velocity * deltaTime
        /// </summary>
        LinearExtrapolation = 0,

        /// <summary>
        /// Hermite spline interpolation using velocity metadata.
        /// Good for: Smooth curves, acceleration-aware blending.
        /// Uses both position and velocity at sample points.
        /// </summary>
        HermiteSpline = 1,

        /// <summary>
        /// Velocity-augmented extrapolation (uses explicit velocity from VELOCITY bundles).
        /// Good for: Physics objects with known velocity (Rigidbody).
        /// Directly uses network-synced velocity rather than deriving it from positions.
        /// </summary>
        VelocityAugmented = 2,

        /// <summary>
        /// Quadratic Bezier extrapolation with acceleration estimation.
        /// Good for: Non-uniform acceleration (projectiles, gravity-affected objects).
        /// Uses 3+ samples to estimate acceleration.
        /// </summary>
        AccelerationBased = 3,

        /// <summary>
        /// Smoothed extrapolation with low-pass filter.
        /// Good for: Jittery data or at-rest transitions.
        /// Dampens rapid changes to prevent visual jitter.
        /// </summary>
        SmoothedLowPass = 4,

        /// <summary>
        /// Custom user-provided blending (requires managed callback).
        /// Use sparingly - NOT Burst-compatible, runs on main thread after jobs.
        /// </summary>
        Custom = 255
    }

    /// <summary>
    /// Feature flags for unified SoA blending migration.
    /// Allows gradual rollout with instant rollback capability.
    /// </summary>
    public static class GONetFeatureFlags
    {
        /// <summary>
        /// When true, Transform (position/rotation) uses the unified SoA blending path.
        /// When false, falls back to legacy v1/v2 hybrid path.
        /// Default: false (safe, existing behavior).
        /// </summary>
        public static bool UseUnifiedSoABlending = false;

        /// <summary>
        /// When true, ALL blendable value types (float, Vector2, etc.) go through unified SoA.
        /// When false, only Transform uses SoA (others use v1 mostRecentChanges queue).
        /// Requires UseUnifiedSoABlending = true.
        /// </summary>
        public static bool SoAForAllBlendableTypes = false;

        /// <summary>
        /// Enable verbose debug logging for unified SoA path.
        /// Warning: Very chatty, use only for debugging.
        /// </summary>
        public static bool DebugUnifiedSoABlending = false;
    }
}
