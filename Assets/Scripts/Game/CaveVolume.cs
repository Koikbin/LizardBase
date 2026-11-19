using UnityEngine;

// 地洞範圍:由「地洞頂」和「地洞底」兩片物件上下夾出來的空間。
// 由 Lizard > Setup Game 依標記(Lizard > 標記工具)自動計算,存成水平格子:
//   每一格記錄 地洞底表面的高度(floorY)和 地洞頂下緣的高度(ceilingY),
//   點的 XZ 落在某一格、且高度在兩者之間 → 在地洞裡。
// 比一個大方塊精準:洞的形狀、高低起伏、洞口(上方沒有頂)都會照實際模型。
public class CaveVolume : MonoBehaviour
{
    [Tooltip("格子左下角(世界座標 X, Z)")]
    public Vector2 origin;
    public float cellSize = 0.25f;
    public int countX, countZ;
    [HideInInspector] public float[] floorY;     // ceilingY <= floorY 表示這格不是地洞
    [HideInInspector] public float[] ceilingY;

    [Tooltip("腳比地洞底低多少還算在洞裡(公尺)")]
    public float floorMargin = 0.5f;
    [Tooltip("腳要比地洞頂下緣低多少才算在洞裡(公尺)。地洞頂如果同時是地表(單層的面),站在地表上 = 腳剛好在這個高度,不能算洞裡")]
    public float ceilingClearance = 0.05f;

    public Bounds bounds;   // 所有地洞格子的範圍(Setup 時算好)

    // 有石頭階梯時,每一格再分成:可以走的洞底(出入口前面、礦石區)、深淵(坑)、不能走的側邊洞底
    public enum Zone : byte { Cave = 0, Landing = 1, Pit = 2, Side = 3 }
    [HideInInspector] public byte[] zone;      // 沒有 = 全部當一般地洞
    [Tooltip("比這個高度低、又在坑的上方 = 掉進深淵")]
    public float abyssY = float.NegativeInfinity;
    [Tooltip("腳離側邊洞底多近算「踩在上面」(公尺)")]
    public float standHeight = 0.6f;
    public Color gizmoColor = new Color(1f, 0.5f, 0f, 0.8f);

    public Vector3 Center => bounds.center;

    bool Cell(Vector3 p, out int i)
    {
        i = -1;
        if (floorY == null || floorY.Length == 0) return false;
        int ix = Mathf.FloorToInt((p.x - origin.x) / cellSize);
        int iz = Mathf.FloorToInt((p.z - origin.y) / cellSize);
        if (ix < 0 || iz < 0 || ix >= countX || iz >= countZ) return false;
        i = iz * countX + ix;
        return ceilingY[i] > floorY[i];
    }

    public Zone ZoneAt(Vector3 p) =>
        zone != null && zone.Length > 0 && Cell(p, out int i) ? (Zone)zone[i] : Zone.Cave;

    // 在坑的上方、而且已經掉到深淵高度以下
    public bool InAbyss(Vector3 p) => p.y < abyssY && ZoneAt(p) == Zone.Pit;

    // 站在不能走的側邊洞底上(要跳石頭過去,不能繞過去)
    public bool OnForbiddenFloor(Vector3 p) =>
        Cell(p, out int i) && zone != null && zone.Length > 0 && zone[i] == (byte)Zone.Side && p.y < floorY[i] + standHeight;

    public bool Contains(Vector3 p)
    {
        if (floorY == null || floorY.Length == 0) return false;
        int ix = Mathf.FloorToInt((p.x - origin.x) / cellSize);
        int iz = Mathf.FloorToInt((p.z - origin.y) / cellSize);
        if (ix < 0 || iz < 0 || ix >= countX || iz >= countZ) return false;
        int i = iz * countX + ix;
        float f = floorY[i], c = ceilingY[i];
        if (c <= f) return false;
        return p.y >= f - floorMargin && p.y <= c - ceilingClearance;
    }

    void OnDrawGizmos()
    {
        Gizmos.color = gizmoColor;
        Gizmos.DrawWireCube(bounds.center, bounds.size);
    }

    void OnDrawGizmosSelected()
    {
        if (floorY == null) return;
        Gizmos.color = gizmoColor;
        bool zones = zone != null && zone.Length > 0;
        int step = Mathf.Max(1, Mathf.CeilToInt(Mathf.Max(countX, countZ) / 60f));
        for (int z = 0; z < countZ; z += step)
            for (int x = 0; x < countX; x += step)
            {
                int i = z * countX + x;
                if (ceilingY[i] <= floorY[i]) continue;
                float wx = origin.x + (x + 0.5f) * cellSize, wz = origin.y + (z + 0.5f) * cellSize;
                if (zones)
                    Gizmos.color = zone[i] == (byte)Zone.Landing ? new Color(0.3f, 0.9f, 0.3f)
                                 : zone[i] == (byte)Zone.Pit ? new Color(0.5f, 0.1f, 0.7f)
                                 : zone[i] == (byte)Zone.Side ? new Color(0.6f, 0.6f, 0.6f) : gizmoColor;
                Gizmos.DrawLine(new Vector3(wx, floorY[i], wz), new Vector3(wx, ceilingY[i], wz));
            }
    }
}
