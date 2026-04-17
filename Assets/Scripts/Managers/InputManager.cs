using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

public class InputManager : MonoBehaviour, IOrderedScript
{
    public static InputManager Instance = null;
    public static InputActions InputActions = null;

    public static Vector2 MousePosition;

    public static Vector2 PlayerMoveInput;
    public static Vector2 PlayerLookInput;
    public static InputAction BoostAction;
    public static InputAction DriftAction;

    public static bool IsUsingController = false;

    [Tooltip("The first selected object in the scene. This is retrieved from the FirstSelectedObject field of a scene's EventSystem BEFORE the EV selects the obj.")]
    public GameObject FirstSelectedObjThisScene;
    public GameObject LastSelectedObj;

    /// <summary>InputActions is a class created by the InputActions asset, which defines universal input actions between
    /// multiple types of input, like keyboard and mouse. It maps actions like Enter (keyboard) and A (xbox controller) to 
    /// action Submit (uses the currently selected Selectable from the EventSystem).</summary>

    public int CallOrder => 0;

    public void OrderedAwake()
    {
        Instance = this;

        InputActions = new();
        InputActions.Enable();
        BoostAction = InputActions.Player.Boost;
        DriftAction = InputActions.Player.Drift;

        //Set using controller on awake to allow the first selected object to be selected.
        IsUsingController = Gamepad.current != null;

        SceneLoader.OnSceneLoaded += SceneLoader_OnSceneLoaded;
    }
    public void OrderedStart()
    {
    }

    void Start()
    {
        
    }

    void Update()
    {
        PlayerMoveInput = InputActions.Player.Move.ReadValue<Vector2>();
        PlayerLookInput = InputActions.Player.Look.ReadValue<Vector2>();

        //Each scene should an EventSystem.
        var eventSystem = EventSystem.current;
        if (eventSystem && eventSystem.currentSelectedGameObject)
            LastSelectedObj = eventSystem.currentSelectedGameObject;

        MousePosition = IsUsingController && LastSelectedObj ? LastSelectedObj.transform.position : Input.mousePosition;

        //If we press south (A on xbox controller) this frame, change to using a controller.
        var gamepad = Gamepad.current;
        if (gamepad != null && gamepad.buttonSouth.wasPressedThisFrame)
        {
            IsUsingController = true;

            //If we press south and we don't have anything selected, reselect the last selected gameobject.
            if (eventSystem && (eventSystem.currentSelectedGameObject == null || !eventSystem.currentSelectedGameObject.activeInHierarchy))
            {
                //If the last selected is null or disabled, select the first selected for the scene.
                if (LastSelectedObj == null || !LastSelectedObj.activeInHierarchy)
                    eventSystem.SetSelectedGameObject(FirstSelectedObjThisScene);
                else
                    eventSystem.SetSelectedGameObject(LastSelectedObj);
            }
        }
        //If we click, change to using mouse and keyboard.
        if (Mouse.current.leftButton.wasPressedThisFrame || Keyboard.current.anyKey.wasPressedThisFrame)
            IsUsingController = false;
    }

    void OnDestroy()
    {
        SceneLoader.OnSceneLoaded -= SceneLoader_OnSceneLoaded;
    }

    void SceneLoader_OnSceneLoaded(SceneType type, Scene scene, LoadSceneMode mode)
    {
        LastSelectedObj = null;

        //When we load a new scene, cache the EventSystem's first selected gameobject set for this scene.
        var eventSystem = EventSystem.current;
        FirstSelectedObjThisScene = eventSystem ? eventSystem.firstSelectedGameObject : null;
        //Null it so we control it instead.
        if (eventSystem)
            eventSystem.firstSelectedGameObject = null;

        Debug.Log($"Using a controller on scene load: {IsUsingController}");
        //We only select that first selected object of the scene if we are using a controller.
        //Otherwise, we don't want to force select and have a goofy highlight until we click.
        eventSystem.SetSelectedGameObject(IsUsingController ? FirstSelectedObjThisScene : null);
    }
}
