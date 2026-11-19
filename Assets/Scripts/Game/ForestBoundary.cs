using System.Collections.Generic;
using UnityEngine;

// 森林範圍:由「森林邊界」標記(最外圍的葉子/草)連成的多邊形,只看水平位置(XZ),高度不限。
// 由 Lizard > Setup Game 依標記自動產生。點依「繞中心的角度」排序後連起來,再往外推 Margin 公尺,
// 所以邊界上的葉子本身也在範圍內。形狀很凹(像 U 字形)時,凹進去的地方多標幾個點。
public class ForestBoundary : MonoBehaviour
{
    public Vector2[] polygon;   // 世界座標 (x, z)
    public float drawY;         // Gizmo 畫線的高度
    public Color gizmoColor = new Color(0.3f, 1f, 0.3f, 1f);

    public bool Contains(Vector3 p)
    {
        if (polygon == null || polygon.Length < 3) return false;
        bool inside = false;
        for (int i = 0, j = polygon.Length - 1; i < polygon.Length; j = i++)
        {
            Vector2 a = polygon[i], b = polygon[j];
            if ((a.y > p.z) != (b.y > p.z) && p.x < (b.x - a.x) * (p.z - a.y) / (b.y - a.y) + a.x)
                inside = !inside;
        }
        return inside;
    }

    // 標記點 → 多邊形(依角度排序,從中心往外推 margin)
    public static Vector2[] BuildPolygon(IList<Vector3> points, float margin)
    {
        if (points == null || points.Count < 3) return null;
        Vector2 c = Vector2.zero;
        foreach (var p in points) c += new Vector2(p.x, p.z);
        c /= points.Count;
        var list = new List<Vector2>();
        foreach (var p in points)
        {
            var q = new Vector2(p.x, p.z);
            Vector2 d = q - c;
            list.Add(d.sqrMagnitude > 1e-6f ? q + d.normalized * margin : q);
        }
        list.Sort((a, b) => Mathf.Atan2(a.y - c.y, a.x - c.x).CompareTo(Mathf.Atan2(b.y - c.y, b.x - c.x)));
        return list.ToArray();
    }

    // 標記物件 → 把每個物件整個包進去的外框(不會切到物件),再收回到地面上。
    //   1. 每個物件的水平範圍往外擴 margin,取外框上的點
    //   2. 從中心往外分成 bins 個方向,每個方向取最遠的點(和左右鄰格取最大,兩點之間也不會切到物件)
    //   3. onGround 不是 null 時:每個頂點往中心收,直到它和再往外 groundInset 公尺都還在地面上
    public static Vector2[] BuildEnvelope(IList<Bounds> objects, float margin, int bins = 48,
        System.Func<Vector2, bool> onGround = null, float groundInset = 0.15f)
    {
        if (objects == null || objects.Count < 3) return null;
        Vector2 c = Vector2.zero;
        foreach (var b in objects) c += new Vector2(b.center.x, b.center.z);
        c /= objects.Count;

        var r = new float[bins];
        for (int k = 0; k < bins; k++) r[k] = -1f;
        foreach (var b in objects)
        {
            float ex = b.extents.x + margin, ez = b.extents.z + margin;
            for (int i = 0; i <= 4; i++)
                for (int j = 0; j <= 4; j++)
                {
                    if (i != 0 && i != 4 && j != 0 && j != 4) continue;   // 只取外框
                    var p = new Vector2(b.center.x + (i / 2f - 1f) * ex, b.center.z + (j / 2f - 1f) * ez);
                    Vector2 d = p - c;
                    int k = ((int)((Mathf.Atan2(d.y, d.x) + Mathf.PI) / (2f * Mathf.PI) * bins) % bins + bins) % bins;
                    r[k] = Mathf.Max(r[k], d.magnitude);
                }
        }
        // 沒有點的方向:用左右有值的方向內插
        for (int k = 0; k < bins; k++)
        {
            if (r[k] >= 0f) continue;
            int a = k, b2 = k, da = 0, db = 0;
            do { a = (a - 1 + bins) % bins; da++; } while (r[a] < 0f && da < bins);
            do { b2 = (b2 + 1) % bins; db++; } while (r[b2] < 0f && db < bins);
            r[k] = Mathf.Lerp(r[a], r[b2], da / (float)(da + db));
        }
        var smooth = new float[bins];
        for (int k = 0; k < bins; k++) smooth[k] = Mathf.Max(r[k], Mathf.Max(r[(k + 1) % bins], r[(k - 1 + bins) % bins]));

        var poly = new Vector2[bins];
        for (int k = 0; k < bins; k++)
        {
            float ang = -Mathf.PI + (k + 0.5f) * 2f * Mathf.PI / bins;
            var u = new Vector2(Mathf.Cos(ang), Mathf.Sin(ang));
            float t = smooth[k];
            if (onGround != null)
                while (t > 0f && !(onGround(c + u * t) && onGround(c + u * (t + groundInset)))) t -= 0.05f;
            poly[k] = c + u * Mathf.Max(0f, t);
        }
        return poly;
    }

    public static float Area(Vector2[] poly)
    {
        if (poly == null) return 0f;
        float s = 0f;
        for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++) s += poly[j].x * poly[i].y - poly[i].x * poly[j].y;
        return Mathf.Abs(s) * 0.5f;
    }

    void OnDrawGizmos()
    {
        if (polygon == null || polygon.Length < 3) return;
        Gizmos.color = gizmoColor;
        for (int i = 0, j = polygon.Length - 1; i < polygon.Length; j = i++)
        {
            var a = new Vector3(polygon[j].x, drawY, polygon[j].y);
            var b = new Vector3(polygon[i].x, drawY, polygon[i].y);
            Gizmos.DrawLine(a, b);
            Gizmos.DrawLine(a, a + Vector3.up * 2f);
        }
    }
}
