using GONet;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using System;

public class SceneLoader : MonoBehaviour, IOrderedScript
{
    public static event Action<SceneType, Scene, LoadSceneMode> OnSceneLoaded;

    public int CallOrder => 0;

    public void OrderedAwake()
    {
        SceneManager.sceneLoaded += SceneManager_sceneLoaded;
    }

    void SceneManager_sceneLoaded(Scene scene, LoadSceneMode mode)
    {
        SceneType type = SceneType.Bootstrap;
        //Find enum by name.
        foreach (SceneType sceneType in Enum.GetValues(typeof(SceneType)))
        {
            if (scene.name == sceneType.ToString())
                type = sceneType;
        }

        OnSceneLoaded.InvokeSafe(nameof(OnSceneLoaded), type, scene, mode);
        Debug.Log($"Loaded scene type: {type}");
    }

    public void OrderedStart()
    {
        LoadScene(SceneType.MainMenu);
    }

    public static void LoadScene(SceneType scene)
    {
        SceneManager.LoadScene(scene.ToString());
    }
}

public enum SceneType
{
    Bootstrap,
    MainMenu,
    KartCustomization,
    TrackSelection,
    Racetrack,
}