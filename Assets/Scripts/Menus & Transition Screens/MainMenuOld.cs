using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

public class MainMenuOld : MonoBehaviour
{
    public void SelectTrack()
    {
        SceneManager.LoadScene("TrackSelection");
    }

    public void LoadCustomizationPage()
    {
        SceneManager.LoadScene("KartCustomization");
    }

    public void About()
    {
        Application.OpenURL("https://team-22-bathtub-racing-game.github.io/Website/");
    }

    public void LoadMainMenu()
    {
        SceneManager.LoadScene("MainMenu");
    }

    public void QuitGame()
    {
        Application.Quit();
    }
}
