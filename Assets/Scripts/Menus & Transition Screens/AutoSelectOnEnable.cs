using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;

public class AutoSelectOnEnable : MonoBehaviour
{
    public GameObject SelectObj;

    void Reset()
    {
        SelectObj = gameObject;
    }

    void OnEnable()
    {
        if (EventSystem.current && SelectObj && InputManager.IsUsingController)
            EventSystem.current.SetSelectedGameObject(SelectObj);
    }
}
