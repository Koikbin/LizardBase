using UnityEngine;

public class SnapToGround : MonoBehaviour
{
    public LayerMask groundMask = ~0;
    public float offset = 0f;
    public float castHeight = 0.5f;
    public float maxDistance = 20f;

    void LateUpdate()
    {
        Vector3 origin = transform.position + Vector3.up * castHeight;
        if (Physics.Raycast(origin, Vector3.down, out RaycastHit hit, maxDistance, groundMask, QueryTriggerInteraction.Ignore))
        {
            // 直接貼到射線打到的地面高度(上下都會修正);射線起點在物件上方 castHeight,所以陷進地面也找得回來
            float targetY = hit.point.y + offset;
            Vector3 p = transform.position;
            p.y = targetY;
            transform.position = p;
        }
    }
}