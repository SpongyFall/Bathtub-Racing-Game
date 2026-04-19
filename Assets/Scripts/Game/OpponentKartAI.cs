using UnityEngine;
using UnityEngine.Splines;
using Unity.Mathematics;
using Random = UnityEngine.Random;
using System.Collections;
using GONet;
using System;

[RequireComponent(typeof(Rigidbody))]
public class OpponentKartAI : MonoBehaviour
{
    public KartAgent Agent;

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
    
    [Header("Boost")]
    public float boostMultiplier = 1.5f;
    public float boostDuration = 3f;
    public float boostRandomCooldown = 9f;

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
    [Space]
    [Header("Runtime Set Props")]
    public bool IsBoosting = false;
    public float RemainingBoostCooldown = 0f;

    [NonSerialized] public Rigidbody Rigid;

    public float ScaledMaxSpeed => maxSpeed * (IsBoosting ? boostMultiplier : 1f);

    GONetParticipant participant;
    float currentT;

    void Start()
    {
        Rigid = GetComponent<Rigidbody>();
        Rigid.constraints = RigidbodyConstraints.FreezeRotationX | RigidbodyConstraints.FreezeRotationZ;
        Rigid.interpolation = RigidbodyInterpolation.Interpolate;
        Rigid.collisionDetectionMode = CollisionDetectionMode.Continuous;

        participant = GetComponent<GONetParticipant>();

        AIWeight = Random.Range(0.8f, 1.3f);
        RandomizeBodyColor();

        SetSplineTrack(RaceManager.Instance.TrackSpline);

        //Start with an initial random cooldown.
        RemainingBoostCooldown = Random.Range(0, boostRandomCooldown);
    }

    void OnEnable()
    {
        Debug.Log($"Enabling AI Kart: {gameObject.name}");
        RaceManager.Instance.AddKartAI(this, true);
    }
    void OnDisable()
    {
        RaceManager.Instance.AddKartAI(this, false);
    }

    void Update()
    {
        if (!canDrive || !participant.IsMine)
            return;

        RemainingBoostCooldown = Mathf.Max(0f, RemainingBoostCooldown - Time.deltaTime);

        if (!IsBoosting && RemainingBoostCooldown == 0)
            Boost();
    }

    void FixedUpdate()
    {
        if (!canDrive)
        {
            Rigid.velocity = Vector3.Lerp(Rigid.velocity, Vector3.zero, Time.deltaTime);
            return;
        }

        if (Agent)
            return;

        if (trackSpline != null)
            HandleSplineMovement();
        else
            HandleSlopeMovement();
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

    public void SetSplineTrack(SplineContainer container)
    {
        trackSpline = container;
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

        float speedFraction = Rigid.velocity.magnitude / Mathf.Max(ScaledMaxSpeed, 0.01f);
        float totalLookAhead = lookAheadDistance + speedFraction * speedLookAheadScale * lookAheadDistance;
        float tOffset = totalLookAhead / splineLength;
        float lookAheadT = (currentT + tOffset) % 1f;

        float3 lookAheadLocal = spline.EvaluatePosition(lookAheadT);
        Vector3 targetPos = trackSpline.transform.TransformPoint(lookAheadLocal);

        // Curvature-based speed control; sample at look-ahead point to anticipate turns
        float curvature = spline.EvaluateCurvature(lookAheadT);
        float curveSpeedFactor = 1f / (1f + curvature * curvatureBrakeStrength);
        float targetSpeed = Mathf.Lerp(minCurveSpeed, ScaledMaxSpeed, curveSpeedFactor);

        // Account for vertical slopes
        RaycastHit hit;
        bool grounded = Physics.Raycast(transform.position + Vector3.up * 0.2f, Vector3.down, out hit, 1.5f);

        Vector3 desiredDirection;

        if (grounded)
        {
            Quaternion slopeRotation = Quaternion.FromToRotation(transform.up, hit.normal) * transform.rotation;
            Rigid.MoveRotation(Quaternion.Slerp(Rigid.rotation, slopeRotation, Time.fixedDeltaTime * 8f));

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
            Quaternion finalRotation = Quaternion.Slerp(Rigid.rotation, directionalRotation, Time.fixedDeltaTime * 4f);
            Rigid.MoveRotation(finalRotation);
        }

        // Acceleration
        float currentAcceleration = acceleration * (1f / AIWeight);
        Rigid.AddForce(desiredDirection * currentAcceleration, ForceMode.Acceleration);

        // Speed limiting based on ai scene settings
        if (Rigid.velocity.magnitude > targetSpeed)
            Rigid.velocity = Rigid.velocity.normalized * targetSpeed;
    }

    public void Boost()
    {
        if (IsBoosting || RemainingBoostCooldown > 0)
            return;

        RemainingBoostCooldown = Random.Range(boostRandomCooldown * 0.5f, boostRandomCooldown);
        StartCoroutine(BoostCoroutine());
    }
    IEnumerator BoostCoroutine()
    {
        IsBoosting = true;
        yield return new WaitForSeconds(boostDuration);
        IsBoosting = false;
    }

    void HandleSlopeMovement()
    {
        RaycastHit hit;
        bool grounded = Physics.Raycast(transform.position + Vector3.up * 0.2f, Vector3.down, out hit, 1.5f);

        if (grounded)
        {
            Quaternion slopeRotation =
                Quaternion.FromToRotation(transform.up, hit.normal) * transform.rotation;
            Rigid.MoveRotation(Quaternion.Slerp(Rigid.rotation, slopeRotation, Time.fixedDeltaTime * 8f));

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
            Rigid.AddForce(force, ForceMode.Acceleration);

            if (Rigid.velocity.magnitude > ScaledMaxSpeed)
                Rigid.velocity = Rigid.velocity.normalized * ScaledMaxSpeed;
        }
        else
        {
            Rigid.AddForce(Vector3.down * groundForce * 0.5f, ForceMode.Acceleration);
        }
    }
}
