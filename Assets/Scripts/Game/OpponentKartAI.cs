using UnityEngine;
using UnityEngine.Splines;
using Unity.Mathematics;
using Random = UnityEngine.Random;

[RequireComponent(typeof(Rigidbody))]
public class OpponentKartAI : MonoBehaviour
{
    [Header("Spline Track")]
    public SplineContainer trackSpline;

    [Header("Look-Ahead Settings")]
    [Tooltip("Base distance (meters) ahead on the spline the AI steers toward.")]
    public float lookAheadDistance = 15f;

    [Tooltip("Extra look-ahead multiplier applied at full speed (scales with current speed ratio).")]
    public float speedLookAheadScale = 0.5f;

    [Header("AI Movement Settings")]
    public float acceleration = 20f;
    public float maxSpeed = 15f;

    [Header("Curvature Braking")]
    [Tooltip("How aggressively the AI brakes for curves. Higher = more braking.")]
    public float curvatureBrakeStrength = 5f;

    [Tooltip("Minimum speed the AI will maintain in tight curves.")]
    public float minCurveSpeed = 6f;

    [Header("Slope Settings (match player)")]
    public float slopeFactor = 1f;
    public float groundForce = 50f;

    [Header("AI Weight")]
    public float AIWeight;

    [Header("Kart Visuals")]
    public MeshRenderer bathtubRenderer;

    [Header("Race Control")]
    public bool canDrive = false;

    private Rigidbody rb;
    private float currentT;

    void Start()
    {
        rb = GetComponent<Rigidbody>();
        rb.constraints = RigidbodyConstraints.FreezeRotationX | RigidbodyConstraints.FreezeRotationZ;
        rb.interpolation = RigidbodyInterpolation.Interpolate;
        rb.collisionDetectionMode = CollisionDetectionMode.Continuous;

        AIWeight = Random.Range(0.8f, 1.3f);
        RandomizeBodyColor();
    }

    void RandomizeBodyColor()
    {
        if (bathtubRenderer != null)
        {
            Color randomColor = new Color(Random.Range(0.2f, 1f), Random.Range(0.2f, 1f), Random.Range(0.2f, 1f));
            bathtubRenderer.material = new Material(bathtubRenderer.material);
            bathtubRenderer.material.color = randomColor;
        }
    }

    void FixedUpdate()
    {
        if (!canDrive)
        {
            rb.velocity = Vector3.zero;
            return;
        }

        if (trackSpline != null)
            HandleSplineMovement();
        else
            HandleSlopeMovement();
    }

    // Generic AI racer movement algorithm that follows a spline path defined in the scene using Unity's spline utility
    void HandleSplineMovement()
    {
        var spline = trackSpline.Spline;

        // Project current position onto spline
        float3 localPos = trackSpline.transform.InverseTransformPoint(transform.position);
        SplineUtility.GetNearestPoint(spline, localPos, out float3 nearestLocal, out float t);
        currentT = t;

        // Compute look-ahead target
        float splineLength = spline.GetLength();
        if (splineLength < 0.01f) return;

        float speedFraction = rb.velocity.magnitude / Mathf.Max(maxSpeed, 0.01f);
        float totalLookAhead = lookAheadDistance + speedFraction * speedLookAheadScale * lookAheadDistance;
        float tOffset = totalLookAhead / splineLength;
        float lookAheadT = (currentT + tOffset) % 1f;

        float3 lookAheadLocal = spline.EvaluatePosition(lookAheadT);
        Vector3 targetPos = trackSpline.transform.TransformPoint(lookAheadLocal);

        // Curvature-based speed control; sample at look-ahead point to anticipate turns
        float curvature = spline.EvaluateCurvature(lookAheadT);
        float curveSpeedFactor = 1f / (1f + curvature * curvatureBrakeStrength);
        float targetSpeed = Mathf.Lerp(minCurveSpeed, maxSpeed, curveSpeedFactor);

        // Account for vertical slopes
        RaycastHit hit;
        bool grounded = Physics.Raycast(transform.position + Vector3.up * 0.2f, Vector3.down, out hit, 1.5f);

        Vector3 desiredDirection;

        if (grounded)
        {
            Quaternion slopeRotation = Quaternion.FromToRotation(transform.up, hit.normal) * transform.rotation;
            rb.MoveRotation(Quaternion.Slerp(rb.rotation, slopeRotation, Time.fixedDeltaTime * 8f));

            Vector3 slopeForward = Vector3.ProjectOnPlane((targetPos - transform.position).normalized, hit.normal).normalized;
            desiredDirection = slopeForward;
        }
        else
        {
            desiredDirection = (targetPos - transform.position).normalized;
        }

        if (desiredDirection.sqrMagnitude > 0.01f && grounded)
        {
            Quaternion directionalRotation = Quaternion.LookRotation(desiredDirection, hit.normal);
            Quaternion finalRotation = Quaternion.Slerp(rb.rotation, directionalRotation, Time.fixedDeltaTime * 4f);
            rb.MoveRotation(finalRotation);
        }

        // Acceleration
        float currentAcceleration = acceleration * (1f / AIWeight);
        rb.AddForce(desiredDirection * currentAcceleration, ForceMode.Acceleration);

        // Speed limiting based on ai scene settings
        if (rb.velocity.magnitude > targetSpeed)
            rb.velocity = rb.velocity.normalized * targetSpeed;
    }

    void HandleSlopeMovement()
    {
        RaycastHit hit;
        bool grounded = Physics.Raycast(transform.position + Vector3.up * 0.2f, Vector3.down, out hit, 1.5f);

        if (grounded)
        {
            Quaternion slopeRotation =
                Quaternion.FromToRotation(transform.up, hit.normal) * transform.rotation;
            rb.MoveRotation(Quaternion.Slerp(rb.rotation, slopeRotation, Time.fixedDeltaTime * 8f));

            Vector3 slopeForward = Vector3.ProjectOnPlane(transform.forward, hit.normal).normalized;

            float slopeAngle = Vector3.Angle(hit.normal, Vector3.up);
            float adjustment = 1f;

            if (slopeAngle > 0.1f)
            {
                if (Vector3.Dot(slopeForward, Vector3.up) < 0)
                    adjustment = 1f / slopeFactor;
                else
                    adjustment = slopeFactor;
            }

            float currentAcceleration = acceleration * (1f / AIWeight);
            Vector3 force = slopeForward * currentAcceleration * adjustment;
            rb.AddForce(force, ForceMode.Acceleration);

            if (rb.velocity.magnitude > maxSpeed)
                rb.velocity = rb.velocity.normalized * maxSpeed;
        }
        else
        {
            rb.AddForce(Vector3.down * groundForce * 0.5f, ForceMode.Acceleration);
        }
    }
}
