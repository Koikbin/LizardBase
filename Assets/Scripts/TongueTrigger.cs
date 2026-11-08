using UnityEngine;

// 舌頭:吹龍(深度)或右手扳機或鍵盤假輸入 → 舌頭伸出 → 舌尖碰到能吃的東西就黏住 → 縮回就吃掉。
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

    void TryCatch()
    {
        ITongueEdible best = null;
        float bestD = 1f;
        Vector3 p = tip.position;
        foreach (var a in AntWander.All) Consider(a, p, ref best, ref bestD);
        foreach (var f in TongueFood.All) Consider(f, p, ref best, ref bestD);
        if (best != null)
        {
            best.OnCaught(tip);
            Caught = best;
        }
    }

    void Consider(ITongueEdible e, Vector3 tipPos, ref ITongueEdible best, ref float bestD)
    {
        if (!e.CanBeCaught) return;
        float d = e.CatchValue(this, tipPos);
        if (d < bestD) { bestD = d; best = e; }
    }
}