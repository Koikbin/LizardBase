using UnityEngine;

// 一個方塊區域(洞穴範圍、出口、森林範圍、地洞出入口)。用 BoxCollider(Is Trigger)來編輯大小,
// 在 Scene 視窗選取後可以直接拖 Box Collider 的控制點調整。
[RequireComponent(typeof(BoxCollider))]
public class GameZone : MonoBehaviour
{
    // Forest / CaveEntrance 是用 Lizard > 標記工具 放的範圍框;Other = Setup Game 自動產生的(CaveZone、ExitZone)
    public enum ZoneRole { Other, Forest, CaveEntrance }
    public ZoneRole role = ZoneRole.Other;

    public Color gizmoColor = new Color(1f, 0.6f, 0f, 0.25f);

    BoxCollider box;

    void Awake()
    {
        box = GetComponent<BoxCollider>();
        box.isTrigger = true;   // 爬行、螞蟻貼地都忽略 Trigger,不會被當成地面
    }

    public Vector3 Center => GetBox().bounds.center;

    public bool Contains(Vector3 worldPoint)
    {
        var b = GetBox();
        Vector3 p = transform.InverseTransformPoint(worldPoint) - b.center;
        Vector3 h = b.size * 0.5f;
        return Mathf.Abs(p.x) <= h.x && Mathf.Abs(p.y) <= h.y && Mathf.Abs(p.z) <= h.z;
    }

    BoxCollider GetBox() { if (box == null) box = GetComponent<BoxCollider>(); return box; }

    void OnDrawGizmos()
    {
        var b = GetBox();
        Gizmos.matrix = transform.localToWorldMatrix;
        Gizmos.color = gizmoColor;
        Gizmos.DrawCube(b.center, b.size);
        Gizmos.color = new Color(gizmoColor.r, gizmoColor.g, gizmoColor.b, 1f);
        Gizmos.DrawWireCube(b.center, b.size);
    }
}
