using UnityEngine;

// 螞蟻路線:掛在路線的父物件上,子物件(依 Hierarchy 順序)就是路徑點。
// 把這個物件拖到 AntWander 的 Path 欄位,螞蟻就會沿路線走。
// 勾選 strip = 帶狀通道:第一個點是入口、最後一個點是出口,螞蟻在寬度內單向前進,走到底從入口補進來。
public class AntPath : MonoBehaviour
{
    public bool loop = false;   // 勾選 = 繞圈,不勾 = 來回(strip 模式不使用)
    public bool strip = false;
    public float stripWidth = 1f;

    Transform[] points;

    public bool IsStrip => strip && Points.Length >= 2 && StripLength > 0.01f;
    public Vector3 StripStart => Points[0].position;
    public float StripLength => Flat(Points[Points.Length - 1].position - StripStart).magnitude;
    public Vector3 StripDir => Flat(Points[Points.Length - 1].position - StripStart).normalized;
    public Vector3 StripSide => Vector3.Cross(Vector3.up, StripDir);

    // along = 從入口往出口走了多遠;lateral = 離中線多遠
    public Vector3 StripPoint(float along, float lateral)
    {
        return StripStart + StripDir * along + StripSide * lateral;
    }

    public void StripCoords(Vector3 p, out float along, out float lateral)
    {
        Vector3 off = Flat(p - StripStart);
        along = Vector3.Dot(off, StripDir);
        lateral = Vector3.Dot(off, StripSide);
    }

    static Vector3 Flat(Vector3 v) { return new Vector3(v.x, 0, v.z); }

    public Transform[] Points
    {
        get
        {
            if (points == null || !Application.isPlaying) points = CollectPoints();
            return points;
        }
    }

    Transform[] CollectPoints()
    {
        var result = new Transform[transform.childCount];
        for (int i = 0; i < result.Length; i++) result[i] = transform.GetChild(i);
        return result;
    }

    void OnDrawGizmos()
    {
        var pts = CollectPoints();
        if (strip && pts.Length >= 2)
        {
            Vector3 start = pts[0].position;
            Vector3 end = pts[pts.Length - 1].position;
            end.y = start.y;
            Vector3 side = Vector3.Cross(Vector3.up, (end - start).normalized) * stripWidth * 0.5f;

            Gizmos.color = Color.cyan;
            Gizmos.DrawLine(start + side, end + side);
            Gizmos.DrawLine(start - side, end - side);
            Gizmos.DrawLine(end + side, end - side);
            Gizmos.DrawLine(start, end);    // 中線 = 前進方向
            Gizmos.color = Color.green;     // 綠色 = 入口
            Gizmos.DrawLine(start + side, start - side);
            Gizmos.DrawSphere(start, 0.05f);
            return;
        }

        for (int i = 0; i < pts.Length; i++)
        {
            Gizmos.color = i == 0 ? Color.green : Color.cyan;   // 綠色 = 第一個點
            Gizmos.DrawSphere(pts[i].position, 0.05f);
            Gizmos.color = Color.cyan;
            if (i + 1 < pts.Length) Gizmos.DrawLine(pts[i].position, pts[i + 1].position);
        }
        if (loop && pts.Length > 2) Gizmos.DrawLine(pts[pts.Length - 1].position, pts[0].position);
    }
}
