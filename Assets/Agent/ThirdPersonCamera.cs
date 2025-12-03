using UnityEngine;

public class ThirdPersonCamera : MonoBehaviour
{
    public Transform target;
    public Vector3 offset = new Vector3(0, 8, -10);
    public float smoothSpeed = 0.125f;

    void LateUpdate()
    {
        if(target == null) return;

        Vector3 desiredPosition = target.TransformPoint(offset);

        Vector3 smoothedPosition = Vector3.Lerp(transform.position, desiredPosition, smoothSpeed);
        transform.position = smoothedPosition;

        transform.LookAt(target.position + target.forward * 3f);
    }
}

