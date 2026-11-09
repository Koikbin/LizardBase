using UnityEngine;

// 石頭階梯旁邊的坑壁:比最近那塊石頭的頂面低的地方,手抓不住、站上去會往下滑(沒跳準 → 滑進深淵)。
// 只影響「地洞底」標記的物件(坑壁/洞底),石頭本身的側面還是可以用 Grip 抓。
// 由 Lizard > Setup Game 依「石頭階梯」和「地洞底」標記自動產生。
public class SlipperySlope : MonoBehaviour
{
    public static SlipperySlope Instance { get; private set; }

    [Tooltip("會滑的表面(地洞底標記的物件,含底下的碰撞體)")]
    public Transform[] surfaces;
    [Tooltip("石頭階梯的範圍(世界座標)")]
    public Bounds[] stones;
    [Tooltip("離石頭水平距離多少公尺以內才會滑")]
    public float range = 3f;
    [Tooltip("比石頭頂面低多少以上才算(避免石頭邊緣剛好一樣高的地方也滑)")]
    public float topMargin = 0.05f;
    [Tooltip("下滑加速度(公尺/秒²,世界座標,乘上坡度的 sin)")]
    public float slideAccel = 9.8f;
    [Tooltip("最快下滑速度(公尺/秒,世界座標)")]
    public float maxSlideSpeed = 2.5f;

    void OnEnable() => Instance = this;
    void OnDisable() { if (Instance == this) Instance = null; }

    public bool IsSlippery(Collider c, Vector3 point)
    {
        if (c == null || surfaces == null || stones == null || stones.Length == 0) return false;
        bool onSurface = false;
        foreach (var s in surfaces)
            if (s != null && c.transform.IsChildOf(s)) { onSurface = true; break; }
        if (!onSurface) return false;

        float best = float.MaxValue, top = 0f;
        foreach (var b in stones)
        {
            float dx = Mathf.Max(0f, Mathf.Abs(point.x - b.center.x) - b.extents.x);
            float dz = Mathf.Max(0f, Mathf.Abs(point.z - b.center.z) - b.extents.z);
            float d = dx * dx + dz * dz;
            if (d < best) { best = d; top = b.max.y; }
        }
        return best <= range * range && point.y < top - topMargin;
    }

    void OnDrawGizmosSelected()
    {
        if (stones == null) return;
        Gizmos.color = new Color(0.4f, 0.8f, 1f, 0.6f);
        foreach (var b in stones)
            Gizmos.DrawWireCube(new Vector3(b.center.x, b.max.y, b.center.z), new Vector3(b.size.x + 2f * range, 0.02f, b.size.z + 2f * range));
    }
}
