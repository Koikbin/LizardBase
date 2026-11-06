using UnityEngine;

// 舌頭:吹龍(深度)或右手扳機或鍵盤假輸入 → 舌頭伸出 → 舌尖靠近螞蟻就黏住 → 縮回就吃掉。
// 掛在 TonguePivot。tongue 指向 Tongue 立方體,tip 指向 Tip。
// 注意:這份腳本會自己設定 tongue 的 Scale Z 與 Local Position Z,tip 的 Local Position Z,
// 假設 Tongue 是 TonguePivot 底下的單位立方體、朝 +Z 伸出。
public class TongueTrigger : MonoBehaviour
{
    public Transform tongue;
    public Transform tip;
    public BlowoutDepthProbe depthInput;
    public bool useDepth = true;

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

    public float Extension { get; private set; }    // 0~1,平滑後
    public float RawInput { get; private set; }     // 0~1,平滑前
    public AntWander Caught { get; private set; }
    public bool StrikeWindowOpen => Time.time < windowEnd;

    bool armed = true;
    float prevExtension;
    float windowEnd = -1f;

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
            Caught.Eat();
            Caught = null;
        }
    }

    float ReadInput()
    {
        if (DebugInput.Active) return DebugInput.Blow01;
        if (useDepth && depthInput != null) return Mathf.Clamp01(depthInput.Length01);
        return OVRInput.Get(OVRInput.Axis1D.PrimaryIndexTrigger, OVRInput.Controller.RTouch);
    }

    // 點在螞蟻的橢圓體範圍內時小於 1;橢圓體的長軸沿著身體方向。
    public float CatchValue(AntWander ant, Vector3 point)
    {
        Vector3 off = point - ant.transform.position;
        Vector3 forward = ant.BodyForward;
        Vector3 side = Vector3.Cross(Vector3.up, forward);

        float x = Vector3.Dot(off, forward) / (catchLength * 0.5f);
        float y = off.y / (catchHeight * 0.5f);
        float z = Vector3.Dot(off, side) / (catchWidth * 0.5f);
        return x * x + y * y + z * z;
    }

    void TryCatch()
    {
        AntWander best = null;
        float bestD = 1f;
        foreach (var a in AntWander.All)
        {
            if (a.IsCaught || a.IsEaten) continue;
            float d = CatchValue(a, tip.position);
            if (d < bestD) { bestD = d; best = a; }
        }
        if (best != null)
        {
            best.SetCaught(tip);
            Caught = best;
        }
    }
}