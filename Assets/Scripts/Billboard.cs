using UnityEngine;

public class Billboard : MonoBehaviour
{
    private Transform cam;

    void Update()
    {
        if (cam == null || !cam.gameObject.activeInHierarchy)
        {
            Camera mainCam = Camera.main;
            if (mainCam != null)
                cam = mainCam.transform;
            else
                return;
        }
        
        Vector3 rotation = cam.transform.eulerAngles;

        rotation.x = 0;
        rotation.z = 0;

        transform.eulerAngles = rotation;
    }
}