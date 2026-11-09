using UnityEngine;

// 舌頭:吹龍(深度)或右手扳機或鍵盤假輸入 → 舌頭伸出 → 舌頭任何一段碰到能吃的東西就黏住
// (最靠近嘴巴的優先,舌頭停在那裡不穿過去)→ 縮回就吃掉。
// 能吃的東西:螞蟻(AntWander → 毒素)、TongueFood(水珠 → 水分、礦石 → 礦石能量),都實作 ITongueEdible。
// 掛在 TonguePivot。tongue 指向 Tongue 立方體,tip 指向 Tip。
// 注意:這份腳本會自己設定 tongue 的 Scale Z 與 Local Position Z,tip 的 Local Position Z,
// 假設 Tongue 是 TonguePivot 底下的單位立方體、朝 +Z 伸出。
public class TongueTrigger : MonoBehaviour
{
    public Transform tongue;
    public Transform tip;
    public BlowoutDepthProbe depthInput;
    public bool useDepth = true;
    [Tooltip("膨脹校準時右扳機是確認鍵,指定後校準期間扳機不會伸舌頭")]
    public ExpansionInput expansion;

    public float maxLength = 1.5f;
    public float catchDistance = 0.3f;  // 已不使用,改看下面的 Catch Length / Width / Height
    public float smoothing = 12f;       // 越大越跟手,延遲越小
    public float catchAbove = 0.5f;     // t 超過這個才抓
    public float eatBelow = 0.1f;       // t 低於這個才吃
    public float threatAbove = 0.15f;   // t 超過這個,螞蟻才開始怕舌尖

    [Tooltip("舌頭伸過 Catch Above 之後,有幾秒可以黏螞蟻。時間一到就不能再黏,即使舌頭還伸著。")]
    public float strikeWindow = 0.25f;
    [Tooltip("舌頭要縮到比這個值短,下一次伸出才能再出擊。必須小於 Catch Above。")]
    public float rearmBelow = 0.3f;

    [Tooltip("抓取範圍沿螞蟻身體方向的總長度(公尺)。")]
    public float catchLength = 0.6f;
    [Tooltip("抓取範圍的左右總寬度(公尺)。")]
    public float catchWidth = 0.3f;
    [Tooltip("抓取範圍的上下總高度(公尺)。")]
    public float catchHeight = 0.3f;

    [Tooltip("沿著舌頭檢查的間隔(公尺,真實大小)。整條舌頭碰到都算,越小越準但越耗效能。")]
    public float catchStep = 0.03f;

    // 舌頭跟著玩家大小縮放(TonguePivot 在相機底下);抓取範圍也跟著縮放
    public float SizeScale => transform.lossyScale.x;

    public float Extension { get; private set; }    // 0~1,平滑後
    public float RawInput { get; private set; }     // 0~1,平滑前
    public ITongueEdible Caught { get; private set; }
    public string CaughtName => Caught != null ? Caught.DisplayName : null;
    public bool StrikeWindowOpen => Time.time < windowEnd;

    bool armed = true;
    float prevExtension;
    float windowEnd = -1f;
    float caughtLength;   // 黏住時的舌頭長度
    readonly System.Collections.Generic.List<ITongueEdible> candidates = new System.Collections.Generic.List<ITongueEdible>();

    void Awake()
    {
        // 舌頭方塊的碰撞體沒有用到(抓東西是用距離判斷),而且舌頭伸出時會擋住玩家的身體(CharacterController 會撞到自己子物件的碰撞體)
        if (tongue != null)
            foreach (var c in tongue.GetComponentsInChildren<Collider>(true)) c.enabled = false;
    }

    void OnValidate()
    {
        strikeWindow = Mathf.Max(0f, strikeWindow);
        rearmBelow = Mathf.Clamp(rearmBelow, 0f, Mathf.Max(0f, catchAbove - 0.01f));
    }

    void Update()
    {
        RawInput = ReadInput();
        float k = 1f - Mathf.Exp(-smoothing * Time.deltaTime);
        Extension = Mathf.Lerp(Extension, RawInput, k);

        float len = Extension * maxLength;
        // 黏住東西後,舌頭停在黏到的位置,不會穿過去繼續伸長(縮回照常)
        if (Caught != null) len = Mathf.Min(len, caughtLength);
        if (tongue != null)
        {
            var s = tongue.localScale; s.z = Mathf.Max(0.001f, len); tongue.localScale = s;
            var p = tongue.localPosition; p.z = len * 0.5f; tongue.localPosition = p;
        }
        if (tip != null)
        {
            var tp = tip.localPosition; tp.z = len; tip.localPosition = tp;
        }

        AntWander.Threat = (Extension > threatAbove && tip != null) ? tip : null;

        if (Extension < rearmBelow) armed = true;

        // 由下往上跨過 catchAbove 的那一幀才開窗口,每次伸出只開一次
        if (armed && prevExtension <= catchAbove && Extension > catchAbove)
        {
            windowEnd = Time.time + strikeWindow;
            armed = false;
        }
        prevExtension = Extension;

        if (Caught == null && StrikeWindowOpen && Extension > catchAbove && tip != null)
            TryCatch();
        else if (Caught != null && Extension < eatBelow)
        {
            var eaten = Caught;
            Caught = null;
            eaten.OnEaten();
        }
    }

    float ReadInput()
    {
        if (DebugInput.Active) return DebugInput.Blow01;
        if (useDepth && depthInput != null) return Mathf.Clamp01(depthInput.Length01);
        if (expansion != null && expansion.IsCalibrating) return 0f;
        return OVRInput.Get(OVRInput.Axis1D.PrimaryIndexTrigger, OVRInput.Controller.RTouch);
    }

    // 點在螞蟻的橢圓體範圍內時小於 1;橢圓體的長軸沿著身體方向。
    public float CatchValue(AntWander ant, Vector3 point)
    {
        Vector3 off = point - ant.transform.position;
        Vector3 forward = ant.BodyForward;
        Vector3 side = Vector3.Cross(Vector3.up, forward);

        float k = SizeScale;
        float x = Vector3.Dot(off, forward) / (catchLength * 0.5f * k);
        float y = off.y / (catchHeight * 0.5f * k);
        float z = Vector3.Dot(off, side) / (catchWidth * 0.5f * k);
        return x * x + y * y + z * z;
    }

    // 整條舌頭都算:從嘴巴往舌尖每隔 catchStep 取一個點,第一個碰到東西的點就抓那個東西
    // (最靠近嘴巴的優先),舌頭停在那個長度,不會穿過去。
    void TryCatch()
    {
        Transform space = tip.parent != null ? tip.parent : transform;
        Vector3 tipLocal = tip.localPosition;
        float len = tipLocal.z;
        float step = Mathf.Max(0.005f, catchStep * SizeScale / Mathf.Max(0.0001f, space.lossyScale.z));   // 換成 TonguePivot 座標
        int count = Mathf.Max(1, Mathf.CeilToInt(len / step));

        // 先篩出舌頭附近的東西,避免每個點都把全部螞蟻、水珠算一遍
        var segment = new Bounds(space.TransformPoint(new Vector3(tipLocal.x, tipLocal.y, 0f)), Vector3.zero);
        segment.Encapsulate(tip.position);
        segment.Expand(2f * Mathf.Max(catchLength, catchWidth, catchHeight) * SizeScale);
        candidates.Clear();
        foreach (var a in AntWander.All) if (segment.Contains(a.transform.position)) candidates.Add(a);
        foreach (var f in TongueFood.All) if (segment.Intersects(f.WorldBounds())) candidates.Add(f);
        if (candidates.Count == 0) return;

        for (int i = 1; i <= count; i++)
        {
            float z = Mathf.Min(len, i * step);
            Vector3 p = space.TransformPoint(new Vector3(tipLocal.x, tipLocal.y, z));
            ITongueEdible best = null;
            float bestD = 1f;
            foreach (var e in candidates) Consider(e, p, ref best, ref bestD);
            if (best == null) continue;

            caughtLength = z;
            // 舌尖先移到抓到的位置,讓 OnCaught 黏在正確的地方
            tip.localPosition = new Vector3(tipLocal.x, tipLocal.y, z);
            best.OnCaught(tip);
            Caught = best;
            return;
        }
    }

    void Consider(ITongueEdible e, Vector3 tipPos, ref ITongueEdible best, ref float bestD)
    {
        if (!e.CanBeCaught) return;
        float d = e.CatchValue(this, tipPos);
        if (d < bestD) { bestD = d; best = e; }
    }
}