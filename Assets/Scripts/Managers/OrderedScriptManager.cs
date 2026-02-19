using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

/// <summary>
/// This class finds all IOrderedScript components attached to this object and calls InitAwake and InitStart on them based 
/// on their Order value. This ensures initialization occurs in a predictable sequence, since Unity's default Awake and Start 
/// execution order is not guaranteed. IOrderedScript components that share the same Order value will be executed in their 
/// attached component order.
/// </summary>
public class OrderedScriptManager : MonoBehaviour
{
    [Tooltip("Already includes this GameObject.")]
    public List<GameObject> OrderedObjs;

    void Awake()
    {
        foreach (var obj in GetSortedOrderScripts())
        {
            try { obj.OrderedAwake(); }
            catch (System.Exception ex)
            {
                Debug.LogError($"Error in object: {obj}'s {nameof(obj.OrderedAwake)}!");
                Debug.LogException(ex);
            }
        }
    }

    void Start()
    {
        foreach (var obj in GetSortedOrderScripts())
        {
            try { obj.OrderedStart(); }
            catch (System.Exception ex)
            {
                Debug.LogError($"Error in object: {obj}'s {nameof(obj.OrderedStart)}!");
                Debug.LogException(ex);
            }
        }
    }

    public List<IOrderedScript> GetSortedOrderScripts()
    {
        List<IOrderedScript> scripts = new();
        //Look for any attached to us.
        scripts.AddRange(GetComponentsInChildren<IOrderedScript>(true));

        //Then look through listed objects to find any scripts.
        foreach (var obj in OrderedObjs)
        {
            if (obj == null)
                continue;
            scripts.AddRange(obj.GetComponentsInChildren<IOrderedScript>(true));
        }

        scripts =
            //Sort by order. Highest order first.
            scripts.OrderByDescending(x => x.CallOrder)
            //Avoid dupes.
            .Distinct()
            .ToList();
        
        return scripts;
    }
}

public interface IOrderedScript
{
    public int CallOrder { get; }

    public void OrderedAwake();
    public void OrderedStart();
}