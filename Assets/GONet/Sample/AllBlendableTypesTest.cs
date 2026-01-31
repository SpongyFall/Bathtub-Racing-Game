/* GONet (TM, serial number 88592370), Copyright (c) 2019-2025 Galore Interactive LLC - All Rights Reserved
 * Unauthorized copying of this file, via any medium is strictly prohibited
 * Proprietary and confidential, email: contactus@unitygo.net
 */

using GONet;
using UnityEngine;

/// <summary>
/// Integration test for ALL blendable value types in the unified SoA blending system.
/// This script synchronizes Vector3, Quaternion, Vector2, and Vector4 fields to verify
/// that the generalized blending pipeline works correctly for all supported types.
///
/// Usage:
/// 1. Attach to a prefab with GONetParticipant
/// 2. Run server + client
/// 3. Observe that all synced values update smoothly on non-authority clients
/// 4. Check logs for "[SoA]" registration messages
///
/// Expected behavior:
/// - Authority: Updates all values based on time/animation
/// - Non-authority: Values blend smoothly via unified SoA pipeline
/// </summary>
/// <summary>
/// Easing functions for non-linear interpolation testing.
/// Each cycle switches to a different easing type.
/// </summary>
public enum EasingType
{
    Linear,
    QuadraticIn,
    QuadraticOut,
    QuadraticInOut,
    CubicIn,
    CubicOut,
    CubicInOut,
    ElasticOut,
    BounceOut,
    BackIn,
    BackOut
}

public class AllBlendableTypesTest : GONetParticipantCompanionBehaviour
{
    #region Synced Values - ALL Blendable Types

    /// <summary>
    /// Vector3 position - moves in a figure-8 pattern.
    /// Should use existing position stream blending.
    /// </summary>
    [GONetAutoMagicalSync("_GONet_Transform_Position")]
    public Vector3 syncedPosition;

    /// <summary>
    /// Quaternion rotation - rotates continuously.
    /// Should use existing rotation stream blending.
    /// </summary>
    [GONetAutoMagicalSync("_GONet_Transform_Rotation")]
    public Quaternion syncedRotation = Quaternion.identity;

    /// <summary>
    /// Vector2 - simulates UV offset or 2D cursor position.
    /// Tests the NEW Vector2 stream blending.
    /// </summary>
    [GONetAutoMagicalSync(QuantizeDownToBitCount = 0)]
    public Vector2 syncedVector2 = Vector2.zero;

    /// <summary>
    /// Vector4 - simulates color (RGBA) or custom 4D data.
    /// Tests the NEW Vector4 stream blending.
    /// </summary>
    [GONetAutoMagicalSync(QuantizeDownToBitCount = 0)]
    public Vector4 syncedVector4 = new Vector4(1, 1, 1, 1);

    /// <summary>
    /// Additional Vector2 for testing multiple Vector2 members.
    /// </summary>
    [GONetAutoMagicalSync(QuantizeDownToBitCount = 0)]
    public Vector2 syncedVector2_B = Vector2.one;

    /// <summary>
    /// Additional Vector4 for testing multiple Vector4 members.
    /// </summary>
    [GONetAutoMagicalSync(QuantizeDownToBitCount = 0)]
    public Vector4 syncedVector4_B = Vector4.zero;

    /// <summary>
    /// Float scale - applied to MotionIndicator with various easing functions.
    /// Cycles between scaleMin and scaleMax using different easing each cycle.
    /// Tests float blending in the SoA system.
    /// </summary>
    [GONetAutoMagicalSync(QuantizeDownToBitCount = 0)]
    public float syncedScale = 1f;

    #endregion

    #region Configuration

    [Header("Animation Settings")]
    [Tooltip("Speed of the figure-8 position animation (radians/sec)")]
    public float positionSpeed = 1f;

    [Tooltip("Speed of continuous rotation (degrees/sec)")]
    public float rotationSpeed = 45f;

    [Tooltip("Speed of Vector2 circular animation")]
    public float vector2Speed = 0.5f;

    [Tooltip("Speed of Vector4 color cycling")]
    public float vector4Speed = 0.25f;

    [Tooltip("Duration of one scale ease cycle (seconds)")]
    public float scaleCycleDuration = 2f;

    [Header("Animation Amplitude")]
    [Tooltip("Size of the figure-8 pattern")]
    public float positionAmplitude = 3f;

    [Tooltip("Radius of Vector2 circular motion")]
    public float vector2Amplitude = 5f;

    [Header("Scale Easing")]
    [Tooltip("Minimum scale for MotionIndicator")]
    public float scaleMin = 0.5f;

    [Tooltip("Maximum scale for MotionIndicator")]
    public float scaleMax = 2f;

    [Header("Debug")]
    [Tooltip("Log value updates to console")]
    public bool debugLogging = false;

    #endregion

    #region Private State

    private float elapsedTime = 0f;
    private bool isInitialized = false;
    private Vector3 startPosition;
    private Renderer[] childRenderers;
    private Transform motionIndicatorTransform;

    // Easing state
    private int currentEasingIndex = 0;
    private float scaleCycleTime = 0f;
    private bool scalingUp = true;

    // Diagnostic timer for Update() logging
    private float diagnosticTimer = 0f;
    private static readonly EasingType[] easingTypes = (EasingType[])System.Enum.GetValues(typeof(EasingType));

    // Failover transition logging
    private float failoverLogTimer = 0f;
    private const float FAILOVER_LOG_INTERVAL = 1.0f; // Log every second during failover testing
    private ushort lastKnownOwnerAuthority = 0;
    private bool lastKnownIsMine = false;
    private bool hasLoggedTransition = false;

    #endregion

    #region Lifecycle

    protected override void Start()
    {
        base.Start();

        startPosition = transform.position;
        
        // Cache child renderers for color application
        childRenderers = GetComponentsInChildren<Renderer>();

        // Cache motion indicator transform for scale animation
        Transform motionIndicator = transform.Find("MotionIndicator");
        if (motionIndicator != null)
        {
            motionIndicatorTransform = motionIndicator;
        }
        
        // Diagnostic: Log what was cached and GONetParticipant state
        bool gnpNull = GONetParticipant == null;
        string gnpState = gnpNull ? "NULL" : $"id={GONetParticipant.GONetId}, owner={GONetParticipant.OwnerAuthorityId}, isMine={GONetParticipant.IsMine}, isConfigured={GONetParticipant.IsInternallyConfigured}";
        if (debugLogging) GONetLog.Info($"[AllBlendableTypesTest] Start() on '{gameObject.name}': childRenderers={childRenderers?.Length ?? 0}, motionIndicator={(motionIndicatorTransform != null ? "found" : "NOT FOUND")}, GNP={gnpState}");
    }

    /// <summary>
    /// Diagnostic: Simple Update to check if ANY updates are running on this object.
    /// Logs GONetParticipant state and IsGONetReady status every 2 seconds.
    /// TEMPORARY FAILOVER LOGGING: Logs state changes during host transitions.
    /// </summary>
    private void Update()
    {
        // FAILOVER TRANSITION LOGGING - Always active for debugging failover
        LogFailoverState();

        diagnosticTimer += Time.deltaTime;
        if (diagnosticTimer >= 2f)
        {
            diagnosticTimer = 0f;
            bool gnpNull = GONetParticipant == null;
            if (gnpNull)
            {
                GONetLog.Warning($"[AllBlendableTypesTest] UPDATE DIAG: GONetParticipant is NULL on '{gameObject.name}'");
            }
            else
            {
                bool isReady = GONetMain.IsGONetReady(GONetParticipant);
                string blockReason = isReady ? "READY" : GONetMain.GetIsGONetReadyBlockingReason(GONetParticipant);
                if (debugLogging) GONetLog.Info($"[AllBlendableTypesTest] UPDATE DIAG: '{gameObject.name}' id={GONetParticipant.GONetId}, isMine={GONetParticipant.IsMine}, isReady={isReady}, blockReason={blockReason}, v4=({syncedVector4.x:F2},{syncedVector4.y:F2},{syncedVector4.z:F2},{syncedVector4.w:F2}), scale={syncedScale:F2}");
            }
        }
    }

    /// <summary>
    /// TEMPORARY: Comprehensive failover state logging for debugging hot standby transitions.
    /// Logs every second AND immediately when ownership/authority changes.
    /// </summary>
    private void LogFailoverState()
    {
        if (GONetParticipant == null) return;

        ushort currentOwner = GONetParticipant.OwnerAuthorityId;
        bool currentIsMine = GONetParticipant.IsMine;
        bool ownerChanged = currentOwner != lastKnownOwnerAuthority && lastKnownOwnerAuthority != 0;
        bool isMineChanged = currentIsMine != lastKnownIsMine && hasLoggedTransition;

        // Detect and log transitions immediately
        if (ownerChanged || isMineChanged)
        {
            GONetLog.Warning($"[GNP-FAILOVER] === OWNERSHIP TRANSITION === " +
                $"GONetId={GONetParticipant.GONetId}, " +
                $"Owner: {lastKnownOwnerAuthority} -> {currentOwner}, " +
                $"IsMine: {lastKnownIsMine} -> {currentIsMine}, " +
                $"MyAuthority={GONetMain.MyAuthorityId}, " +
                $"IsServer={GONetMain.IsServer}, IsClient={GONetMain.IsClient}, " +
                $"pos={transform.position:F2}");
        }

        // Update tracking
        lastKnownOwnerAuthority = currentOwner;
        lastKnownIsMine = currentIsMine;
        hasLoggedTransition = true;

        // Periodic logging every FAILOVER_LOG_INTERVAL seconds (commented out - development diagnostic)
        // failoverLogTimer += Time.deltaTime;
        // if (failoverLogTimer >= FAILOVER_LOG_INTERVAL)
        // {
        //     failoverLogTimer = 0f;
        //     bool isReady = GONetMain.IsGONetReady(GONetParticipant);
        //     string clientState = GONetMain.GONetClient != null
        //         ? $"Connected={GONetMain.GONetClient.IsConnectedToServer}, Init={GONetMain.GONetClient.IsInitializedWithServer}"
        //         : "NULL";
        //     GONetLog.Debug($"[GNP-FAILOVER] Periodic: " +
        //         $"GONetId={GONetParticipant.GONetId}, " +
        //         $"Owner={currentOwner}, " +
        //         $"IsMine={currentIsMine}, " +
        //         $"IsReady={isReady}, " +
        //         $"MyAuth={GONetMain.MyAuthorityId}, " +
        //         $"IsServer={GONetMain.IsServer}, " +
        //         $"Client={clientState}, " +
        //         $"pos=({transform.position.x:F1},{transform.position.y:F1},{transform.position.z:F1}), " +
        //         $"syncPos=({syncedPosition.x:F1},{syncedPosition.y:F1},{syncedPosition.z:F1})");
        // }
    }

    /// <summary>
    /// Called after GONet is ready. Authority starts animating values.
    /// </summary>
    internal override void UpdateAfterGONetReady()
    {
        base.UpdateAfterGONetReady();

        if (!isInitialized)
        {
            isInitialized = true;

            if (GONetParticipant.IsMine)
            {
                if (debugLogging) GONetLog.Info($"[AllBlendableTypesTest] Authority initialized on '{gameObject.name}' (GONetId: {GONetParticipant.GONetId})");
            }
            else
            {
                if (debugLogging) GONetLog.Info($"[AllBlendableTypesTest] Non-authority observing '{gameObject.name}' (GONetId: {GONetParticipant.GONetId})");
            }
        }

        // Only authority updates values
        if (GONetParticipant.IsMine)
        {
            elapsedTime += Time.deltaTime;
            UpdateAllSyncedValues();
        }

        // Apply visual state (both authority and non-authority)
        ApplyVisualState();
    }

#endregion

#region Value Updates (Authority Only)

    private void UpdateAllSyncedValues()
    {
        // 1. Vector3 Position: Figure-8 pattern (Lissajous curve)
        // NOTE: We update transform.position directly since the GNP syncs Transform.position.
        // The syncedPosition/syncedRotation fields are DUPLICATES using the same "_GONet_Transform_Position"
        // profile - they exist to test Vector3/Quaternion SoA blending but should mirror the transform.
        float t = elapsedTime * positionSpeed;
        Vector3 newPosition = startPosition + new Vector3(
            Mathf.Sin(t) * positionAmplitude,
            Mathf.Sin(t * 2) * positionAmplitude * 0.5f,
            Mathf.Cos(t) * positionAmplitude
        );
        transform.position = newPosition;
        syncedPosition = newPosition; // Mirror for observation

        // 2. Quaternion Rotation: Continuous rotation around Y axis
        Quaternion newRotation = Quaternion.Euler(0, elapsedTime * rotationSpeed, 0);
        transform.rotation = newRotation;
        syncedRotation = newRotation; // Mirror for observation

        // 3. Vector2: Circular motion (simulates UV offset or cursor)
        float v2Angle = elapsedTime * vector2Speed;
        syncedVector2 = new Vector2(
            Mathf.Cos(v2Angle) * vector2Amplitude,
            Mathf.Sin(v2Angle) * vector2Amplitude
        );

        // 4. Vector4: Color cycling (RGBA)
        float v4Phase = elapsedTime * vector4Speed;
        syncedVector4 = new Vector4(
            (Mathf.Sin(v4Phase) + 1) * 0.5f,           // R: 0-1
            (Mathf.Sin(v4Phase + 2.094f) + 1) * 0.5f,  // G: 0-1 (120° offset)
            (Mathf.Sin(v4Phase + 4.189f) + 1) * 0.5f,  // B: 0-1 (240° offset)
            1.0f                                        // A: always 1
        );

        // 5. Vector2_B: Opposite direction circular motion
        syncedVector2_B = new Vector2(
            Mathf.Cos(-v2Angle * 1.5f) * vector2Amplitude * 0.7f,
            Mathf.Sin(-v2Angle * 1.5f) * vector2Amplitude * 0.7f
        );

        // 6. Vector4_B: Different color pattern
        syncedVector4_B = new Vector4(
            (Mathf.Cos(v4Phase * 2) + 1) * 0.5f,
            0.5f,
            (Mathf.Sin(v4Phase * 2) + 1) * 0.5f,
            (Mathf.Sin(v4Phase) + 1) * 0.5f
        );

        // 7. Float Scale: Eased scaling with cycling easing functions
        UpdateScaleWithEasing();

        if (debugLogging && Time.frameCount % 60 == 0)
        {
            GONetLog.Info($"[AllBlendableTypesTest] Authority values: pos={syncedPosition:F2}, rot={syncedRotation.eulerAngles:F1}, v2={syncedVector2:F2}, v4=({syncedVector4.x:F2},{syncedVector4.y:F2},{syncedVector4.z:F2},{syncedVector4.w:F2})");
        }
    }

    private void UpdateScaleWithEasing()
    {
        scaleCycleTime += Time.deltaTime;
        float cycleDuration = scaleCycleDuration;

        // Calculate normalized time (0-1) within current half-cycle
        float halfCycle = cycleDuration / 2f;
        float normalizedTime = (scaleCycleTime % halfCycle) / halfCycle;

        // Check if we completed a half-cycle (direction change)
        int currentHalfCycle = Mathf.FloorToInt(scaleCycleTime / halfCycle);
        bool wasScalingUp = scalingUp;
        scalingUp = (currentHalfCycle % 2) == 0;

        // When direction changes, advance to next easing type
        if (wasScalingUp != scalingUp)
        {
            currentEasingIndex = (currentEasingIndex + 1) % easingTypes.Length;
            if (debugLogging) GONetLog.Info($"[AllBlendableTypesTest] Switching to easing: {easingTypes[currentEasingIndex]}");
        }

        // Apply current easing function
        float easedT = ApplyEasing(normalizedTime, easingTypes[currentEasingIndex]);

        // Calculate scale based on direction
        if (scalingUp)
        {
            syncedScale = Mathf.Lerp(scaleMin, scaleMax, easedT);
        }
        else
        {
            syncedScale = Mathf.Lerp(scaleMax, scaleMin, easedT);
        }
    }

    private float ApplyEasing(float t, EasingType easing)
    {
        switch (easing)
        {
            case EasingType.Linear:
                return t;

            case EasingType.QuadraticIn:
                return t * t;

            case EasingType.QuadraticOut:
                return t * (2f - t);

            case EasingType.QuadraticInOut:
                return t < 0.5f ? 2f * t * t : 1f - Mathf.Pow(-2f * t + 2f, 2f) / 2f;

            case EasingType.CubicIn:
                return t * t * t;

            case EasingType.CubicOut:
                return 1f - Mathf.Pow(1f - t, 3f);

            case EasingType.CubicInOut:
                return t < 0.5f ? 4f * t * t * t : 1f - Mathf.Pow(-2f * t + 2f, 3f) / 2f;

            case EasingType.ElasticOut:
                if (t == 0f) return 0f;
                if (t == 1f) return 1f;
                return Mathf.Pow(2f, -10f * t) * Mathf.Sin((t * 10f - 0.75f) * (2f * Mathf.PI) / 3f) + 1f;

            case EasingType.BounceOut:
                return EaseBounceOut(t);

            case EasingType.BackIn:
                const float c1 = 1.70158f;
                const float c3 = c1 + 1f;
                return c3 * t * t * t - c1 * t * t;

            case EasingType.BackOut:
                const float c1b = 1.70158f;
                const float c3b = c1b + 1f;
                return 1f + c3b * Mathf.Pow(t - 1f, 3f) + c1b * Mathf.Pow(t - 1f, 2f);

            default:
                return t;
        }
    }

    private float EaseBounceOut(float t)
    {
        const float n1 = 7.5625f;
        const float d1 = 2.75f;

        if (t < 1f / d1)
        {
            return n1 * t * t;
        }
        else if (t < 2f / d1)
        {
            t -= 1.5f / d1;
            return n1 * t * t + 0.75f;
        }
        else if (t < 2.5f / d1)
        {
            t -= 2.25f / d1;
            return n1 * t * t + 0.9375f;
        }
        else
        {
            t -= 2.625f / d1;
            return n1 * t * t + 0.984375f;
        }
    }

    #endregion

    #region Visual State (Both Authority and Non-Authority)

    private void ApplyVisualState()
    {
        // NOTE: Do NOT apply syncedPosition/syncedRotation to transform here!
        // The GONetParticipant already syncs Transform.position and Transform.rotation via core implementation.
        // The syncedPosition and syncedRotation fields exist only to TEST Vector3/Quaternion SoA blending
        // as standalone blendable values - they should NOT also be applied to the transform.
        // Applying them here would cause double-application and fighting/jitter.

        // Apply color to ALL child renderers (VisualCube, MotionIndicator, etc.)
        if (childRenderers != null)
        {
            // Use syncedVector4 as color (RGBA)
            Color color = new Color(syncedVector4.x, syncedVector4.y, syncedVector4.z, syncedVector4.w);
            
            for (int i = 0; i < childRenderers.Length; i++)
            {
                if (childRenderers[i] != null && childRenderers[i].material != null)
                {
                    childRenderers[i].material.color = color;
                }
            }
        }

        // Apply synced scale to MotionIndicator
        if (motionIndicatorTransform != null)
        {
            motionIndicatorTransform.localScale = Vector3.one * syncedScale;
        }

        // Diagnostic: Log every 120 frames to see actual values (ALWAYS - for debugging) - DISABLED
        if (debugLogging)
        {
            if (Time.frameCount % 120 == 0)
            {
                GONetLog.Info($"[AllBlendableTypesTest] DIAG isMine={GONetParticipant.IsMine}: v4=({syncedVector4.x:F2},{syncedVector4.y:F2},{syncedVector4.z:F2},{syncedVector4.w:F2}), scale={syncedScale:F2}, renderers={childRenderers?.Length ?? 0}, motionXform={(motionIndicatorTransform != null ? "OK" : "NULL")}");
            }

            if (debugLogging && !GONetParticipant.IsMine && Time.frameCount % 60 == 0)
            {
                GONetLog.Info($"[AllBlendableTypesTest] Non-authority blended: pos={syncedPosition:F2}, v2={syncedVector2:F2}, v4=({syncedVector4.x:F2},{syncedVector4.y:F2},{syncedVector4.z:F2},{syncedVector4.w:F2}), scale={syncedScale:F2}");
            }
        }
    }

    #endregion

    #region Editor Visualization

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        // Draw figure-8 path preview
        Gizmos.color = Color.cyan;
        Vector3 center = Application.isPlaying ? startPosition : transform.position;

        Vector3 prev = center + new Vector3(0, 0, positionAmplitude);
        for (float t = 0; t <= Mathf.PI * 2; t += 0.1f)
        {
            Vector3 pos = center + new Vector3(
                Mathf.Sin(t) * positionAmplitude,
                Mathf.Sin(t * 2) * positionAmplitude * 0.5f,
                Mathf.Cos(t) * positionAmplitude
            );
            Gizmos.DrawLine(prev, pos);
            prev = pos;
        }

        // Draw Vector2 as 2D circle in XZ plane (offset above object)
        Gizmos.color = Color.yellow;
        Vector3 v2Center = center + Vector3.up * 2;
        prev = v2Center + new Vector3(vector2Amplitude, 0, 0);
        for (float a = 0; a <= Mathf.PI * 2; a += 0.2f)
        {
            Vector3 pos = v2Center + new Vector3(Mathf.Cos(a) * vector2Amplitude, 0, Mathf.Sin(a) * vector2Amplitude);
            Gizmos.DrawLine(prev, pos);
            prev = pos;
        }

        // Draw current Vector2 position
        if (Application.isPlaying)
        {
            Gizmos.color = Color.red;
            Gizmos.DrawSphere(v2Center + new Vector3(syncedVector2.x, 0, syncedVector2.y), 0.2f);
        }
    }
#endif

    #endregion
}
