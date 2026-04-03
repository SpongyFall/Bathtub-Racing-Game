using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

public static class HelperClass
{
    public static System.Random Rand = new(Guid.NewGuid().GetHashCode());
    public static bool IsConnectedToInternet => Application.internetReachability == NetworkReachability.ReachableViaCarrierDataNetwork
        || Application.internetReachability == NetworkReachability.ReachableViaLocalAreaNetwork;


    /// <returns>If the active state of the obj changed.</returns>
    public static bool SetActiveSafe(this GameObject obj, bool active)
    {
        if (obj == null)
            return false;

        bool changed = obj.activeSelf != active;
        if (changed)
            obj.SetActive(active);
        return changed;
    }

    public static Sprite ToSprite(Texture2D text)
    {
        if (text == null)
            return null;
        else
            return Sprite.Create(text, new Rect(0, 0, text.width, text.height), new Vector2(0.5f, 0.5f), 100f);
    }

    public static void InvokeSafe(this Delegate @event, string eventName, params object[] args)
    {
        if (@event == null)
            return;

        foreach (var del in @event.GetInvocationList())
        {
            try { del.DynamicInvoke(args); }
            catch (TargetInvocationException tie) 
            {
                Debug.LogError($"Error invoking delegate from event '{eventName}'!");
                Debug.LogException(tie.InnerException ?? tie); 
            }
            catch (Exception ex) 
            {
                Debug.LogError($"Error invoking delegate from event '{eventName}'!");
                Debug.LogException(ex);
            }
        }
    }
}

public class ObjPair<T1, T2>
{
    public T1 First { get; set; }
    public T2 Second { get; set; }

    public ObjPair() { }
    public ObjPair(T1 first, T2 second)
    {
        First = first;
        Second = second;
    }
}