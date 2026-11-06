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
            // 只往上修正，不往下推，避免一路累積下沉
            float targetY = hit.point.y + offset;
            Vector3 p = transform.position;
            p.y = targetY;
            transform.position = p;
        }
    }
}