using GONet;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public class GameUI : MonoBehaviour
{
    public Button QuitBtn;

    void Awake()
    {
        QuitBtn.onClick.AddListener(OnQuitClick);
    }

    void OnQuitClick()
    {
        RaceManager.Instance.LeaveGame();
    }
}
