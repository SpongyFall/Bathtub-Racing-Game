using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class Pause : MonoBehaviour
{
    private bool _isPaused;
    
    public GameObject PauseMenu;

    void Start()
    {
        _isPaused = false;
        Cursor.visible = false;
        Cursor.lockState = CursorLockMode.Locked;
    }

    void Update()
    {
        //if (Input.GetKeyDown(KeyCode.Escape))
        if (InputManager.InputActions.UI.Menu.WasPressedThisFrame() && !RaceManager.Instance.GameUI.RaceEndPanel.gameObject.activeInHierarchy)
            TogglePause();
    }

    public void TogglePause()
    {
        _isPaused = !_isPaused;
        
        if (_isPaused)
        {
            //Only affect time scale in singleplayer.
            if (!NetworkManager.IsConnectedGONet)
                Time.timeScale = 0;

            PauseMenu.SetActive(true);
            Cursor.visible = true;
            Cursor.lockState = CursorLockMode.None;
        }
        else
        {
            if (!NetworkManager.IsConnectedGONet)
                Time.timeScale = 1;

            PauseMenu.SetActive(false);
            Cursor.visible = false;
            Cursor.lockState = CursorLockMode.Locked;
        }
    }
}
