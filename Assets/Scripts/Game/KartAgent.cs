using System.Collections;
using System.Collections.Generic;
using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Sensors;
using UnityEngine;

public class KartAgent : Agent
{
    public float TurnSpeed = 5f;
    [Space]
    public OpponentKartAI KartAI;
    public RacerInfo RacerInfo;

    Rigidbody rigid => KartAI.Rigid;

    float prevFrameProgress = 0f;
    Vector3 startPos;
    Quaternion startRot;

    void Awake()
    {
        RacerInfo.OnWaypointReached += RacerInfo_OnWaypointReached;
    }

    void Start()
    {
        startPos = transform.position;
        startRot = transform.rotation;
    }

    void Update()
    {
        //RequestDecision();

        float currentProgress = RacerInfo.RaceProgress;
        float progressChange = currentProgress - prevFrameProgress;
        float reward = progressChange + (-0.001f * Time.deltaTime);
        prevFrameProgress = currentProgress;

        Debug.Log(reward);
        AddReward(reward);
    }

    public override void OnEpisodeBegin()
    {
        base.OnEpisodeBegin();

        KartAI.canDrive = true;

        transform.position = startPos;
        transform.rotation = startRot;
        rigid.velocity = Vector3.zero;
    }

    public override void CollectObservations(VectorSensor sensor)
    {
        base.CollectObservations(sensor);

        sensor.AddObservation(transform.forward);
        sensor.AddObservation(rigid.velocity);

        RaycastHit hit;
        //Forward
        float raycastDist = 20f;
        if (Physics.Raycast(transform.position, transform.forward, out hit, raycastDist))
            sensor.AddObservation(hit.distance / raycastDist);
        //Right
        if (Physics.Raycast(transform.position, transform.right, out hit, raycastDist))
            sensor.AddObservation(hit.distance / raycastDist);
        //Left
        if (Physics.Raycast(transform.position, transform.right * -1, out hit, raycastDist))
            sensor.AddObservation(hit.distance / raycastDist);
    }

    public override void OnActionReceived(ActionBuffers actions)
    {
        base.OnActionReceived(actions);

        int steer = actions.DiscreteActions[0];
        int accel = actions.DiscreteActions[1];

        //None
        float steerAmount = 0;
        //Left
        if (steer == 1) 
            steerAmount = -1f;
        //Right
        if (steer == 2) 
            steerAmount = 1f;

        float accelAmount = accel == 1 ? 1f : 0f;

        transform.Rotate(steerAmount * TurnSpeed * Vector3.up);
        rigid.AddForce(accelAmount * KartAI.ScaledMaxSpeed * transform.forward);

        Debug.Log($"Action received: {steer}, {accel}");
    }

    void RacerInfo_OnWaypointReached(Waypoint prev, Waypoint current)
    {
        float dist = (current.position - prev.position).magnitude;
        AddReward(dist);

        if (!RaceManager.Instance.RaceActive)
            EndEpisode();
    }
}
