using GONet;
using UnityEngine;

/// <summary>
/// Test script for validating late-joiner synchronization of non-physics objects at rest.
///
/// Behavior:
/// - Moves from LEFT position with rotation A
/// - Comes to rest for 20 seconds
/// - Moves to RIGHT position with rotation B
/// - Comes to rest for 20 seconds
/// - Repeats cycle
///
/// This tests the fix for SoA seed staleness where late joiners would see objects
/// at their spawn position instead of their current at-rest position.
///
/// Test procedure:
/// 1. Start server
/// 2. Wait for object to move and come to rest (observe position)
/// 3. Connect a late-joining client
/// 4. Verify client sees object at the same position as server (not spawn position)
/// </summary>
public class AtRestTestor : GONetParticipantCompanionBehaviour
{
    [Header("Position Settings")]
    [Tooltip("X position for left rest point")]
    public float leftX = -5f;

    [Tooltip("X position for right rest point")]
    public float rightX = 5f;

    [Tooltip("Movement speed when transitioning between positions")]
    public float moveSpeed = 3f;

    [Header("Rotation Settings")]
    [Tooltip("Rotation (euler Y) at left position")]
    public float leftRotationY = -45f;

    [Tooltip("Rotation (euler Y) at right position")]
    public float rightRotationY = 45f;

    [Tooltip("Rotation speed (degrees per second) when transitioning")]
    public float rotationSpeed = 90f;

    [Header("Timing Settings")]
    [Tooltip("How long to stay at rest at each position (seconds)")]
    public float restDuration = 20f;

    [Header("Debug")]
    [Tooltip("Enable debug logging")]
    public bool enableLogging = true;

    private enum State
    {
        MovingToLeft,
        RestingAtLeft,
        MovingToRight,
        RestingAtRight
    }

    private State currentState = State.MovingToLeft;
    private float restTimer = 0f;
    private bool isInitialized = false;
    private Vector3 targetPosition;
    private Quaternion targetRotation;

    /// <summary>
    /// Called every frame after GONetReady. Only authority executes movement logic.
    /// </summary>
    internal override void UpdateAfterGONetReady()
    {
        base.UpdateAfterGONetReady();

        if (!GONetParticipant.IsMine)
            return;

        if (!isInitialized)
        {
            Initialize();
        }

        switch (currentState)
        {
            case State.MovingToLeft:
                MoveToTarget();
                if (HasReachedTarget())
                {
                    TransitionTo(State.RestingAtLeft);
                }
                break;

            case State.RestingAtLeft:
                restTimer += Time.deltaTime;
                if (restTimer >= restDuration)
                {
                    SetTargetRight();
                    TransitionTo(State.MovingToRight);
                }
                break;

            case State.MovingToRight:
                MoveToTarget();
                if (HasReachedTarget())
                {
                    TransitionTo(State.RestingAtRight);
                }
                break;

            case State.RestingAtRight:
                restTimer += Time.deltaTime;
                if (restTimer >= restDuration)
                {
                    SetTargetLeft();
                    TransitionTo(State.MovingToLeft);
                }
                break;
        }
    }

    private void Initialize()
    {
        isInitialized = true;

        // Start by moving to left position
        SetTargetLeft();
        currentState = State.MovingToLeft;

        Log($"Initialized. Starting position: {transform.position}, Target: LEFT ({leftX}, {leftRotationY}deg)");
    }

    private void SetTargetLeft()
    {
        targetPosition = new Vector3(leftX, transform.position.y, transform.position.z);
        targetRotation = Quaternion.Euler(0f, leftRotationY, 0f);
    }

    private void SetTargetRight()
    {
        targetPosition = new Vector3(rightX, transform.position.y, transform.position.z);
        targetRotation = Quaternion.Euler(0f, rightRotationY, 0f);
    }

    private void MoveToTarget()
    {
        // Move position
        transform.position = Vector3.MoveTowards(
            transform.position,
            targetPosition,
            moveSpeed * Time.deltaTime
        );

        // Rotate towards target
        transform.rotation = Quaternion.RotateTowards(
            transform.rotation,
            targetRotation,
            rotationSpeed * Time.deltaTime
        );
    }

    private bool HasReachedTarget()
    {
        float positionDistance = Vector3.Distance(transform.position, targetPosition);
        float rotationAngle = Quaternion.Angle(transform.rotation, targetRotation);

        return positionDistance < 0.01f && rotationAngle < 0.5f;
    }

    private void TransitionTo(State newState)
    {
        State previousState = currentState;
        currentState = newState;
        restTimer = 0f;

        // Snap to exact target when entering rest state
        if (newState == State.RestingAtLeft || newState == State.RestingAtRight)
        {
            transform.position = targetPosition;
            transform.rotation = targetRotation;
        }

        Log($"State: {previousState} -> {newState} | Pos: {transform.position} | Rot: {transform.rotation.eulerAngles}");
    }

    private void Log(string message)
    {
        if (enableLogging)
        {
            string role = GONetMain.IsServer ? "SERVER" : "CLIENT";
            GONetLog.Info($"[AtRestTestor][{role}] {message}");
        }
    }

    /// <summary>
    /// OnGUI for visual debugging - shows current state on screen.
    /// </summary>
    private void OnGUI()
    {
        if (!enableLogging)
            return;

        string role = GONetMain.IsServer ? "SERVER" : "CLIENT";
        string authority = GONetParticipant != null && GONetParticipant.IsMine ? "AUTHORITY" : "REMOTE";

        GUIStyle style = new GUIStyle(GUI.skin.box);
        style.fontSize = 14;
        style.alignment = TextAnchor.UpperLeft;

        string info = $"[AtRestTestor] {role} ({authority})\n" +
                     $"State: {currentState}\n" +
                     $"Position: {transform.position:F2}\n" +
                     $"Rotation Y: {transform.rotation.eulerAngles.y:F1}°\n";

        if (currentState == State.RestingAtLeft || currentState == State.RestingAtRight)
        {
            info += $"Rest Timer: {restTimer:F1}s / {restDuration}s";
        }

        GUI.Box(new Rect(10, 200, 280, 120), info, style);
    }
}
