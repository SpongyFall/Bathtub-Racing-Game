using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class RaceManager : MonoBehaviour, IOrderedScript
{
    public const int MaxPlayers = 10;

    public static RaceManager Instance { get; private set; }

    public int CallOrder => 0;

    void Awake()
    {
    }

    public void OrderedAwake()
    {
        Instance = this;
    }
    public void OrderedStart()
    {
    }

    public void StartGame()
    {

    }
}