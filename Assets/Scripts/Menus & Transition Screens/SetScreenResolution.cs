using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class SetScreenResolution : MonoBehaviour
{
    public Texture2D cursor;
    void Start()
    {
        Screen.SetResolution(1920, 1080, Screen.fullScreen);
        Cursor.SetCursor(cursor, Vector3.zero, CursorMode.ForceSoftware);
    }
}
