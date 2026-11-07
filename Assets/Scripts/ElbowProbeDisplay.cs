using System.Collections.Generic;
using UnityEngine;
using TMPro;

// 量測用：在身前顯示身體追蹤和手肘外展的數值。勾掉 show 就隱藏。
// 自己在執行時建立一個世界空間的 TextMeshPro，不需要另外拉 Canvas。
// 統計欄位都是最近 window 秒內的數字。
public class ElbowProbeDisplay : MonoBehaviour
{
    public bool show = true;
    public ExpansionInput expansion;  // 留空會自動找
    public ElbowTracker elbow;        // 留空會自動找
    public Transform head;            // 留空用 expansion.head
    public TMP_FontAsset font;        // 留空用 TMP 預設字型

    public float distanceAhead = 1.3f; // 離眼睛多遠（公尺），不要貼臉
    public float offsetRight = 0.45f;  // 往右偏，避開原本的膨脹文字
    public float offsetDown = 0.1f;
    public float fontSize = 0.25f;     // 沒實測過，太大太小就在 Inspector 調
    public float followSpeed = 4f;
    public float window = 3f;

    struct Sample { public float time, dt, l, r, dT, eT; public bool valid; }
    readonly Queue<Sample> samples = new Queue<Sample>();

    TextMeshPro text;
    bool placed;

    void Start()
    {
        if (expansion == null) expansion = FindAnyObjectByType<ExpansionInput>();
        if (elbow == null) elbow = FindAnyObjectByType<ElbowTracker>();
        if (head == null && expansion != null) head = expansion.head;

        var go = new GameObject("ElbowProbeText");
        text = go.AddComponent<TextMeshPro>();
        if (font != null) text.font = font;
        text.fontSize = fontSize;
        text.alignment = TextAlignmentOptions.TopLeft;
        text.textWrappingMode = TextWrappingModes.NoWrap;
        text.rectTransform.sizeDelta = new Vector2(0.8f, 0.6f);
    }

    void OnDestroy()
    {
        if (text != null) Destroy(text.gameObject);
    }

    void LateUpdate()
    {
        if (text == null) return;
        text.gameObject.SetActive(show);
        if (!show || head == null) return;

        Collect();
        Place();
        text.fontSize = fontSize;
        text.text = Build();
    }

    void Collect()
    {
        float now = Time.unscaledTime;
        var s = new Sample { time = now, dt = Time.unscaledDeltaTime };
        if (elbow != null)
        {
            s.valid = elbow.isValid;
            s.l = elbow.leftAngle;
            s.r = elbow.rightAngle;
        }
        if (expansion != null)
        {
            s.dT = expansion.distanceT;
            s.eT = expansion.elbowT;
        }
        samples.Enqueue(s);
        while (samples.Count > 0 && now - samples.Peek().time > window) samples.Dequeue();
    }

    void Place()
    {
        Vector3 goal = head.position + head.forward * distanceAhead
                     + head.right * offsetRight - Vector3.up * offsetDown;
        Quaternion rot = Quaternion.LookRotation(goal - head.position, Vector3.up);
        Transform t = text.transform;
        if (!placed) { t.SetPositionAndRotation(goal, rot); placed = true; return; }
        float k = 1f - Mathf.Exp(-followSpeed * Time.unscaledDeltaTime);
        t.SetPositionAndRotation(Vector3.Lerp(t.position, goal, k), Quaternion.Slerp(t.rotation, rot, k));
    }

    string Build()
    {
        // 統計：有效比例、左右角標準差（只算有效幀）、手肘和手把 0~1 的平均差、FPS
        int n = 0, nv = 0;
        float sumDt = 0f, sl = 0f, sl2 = 0f, sr = 0f, sr2 = 0f, sdiff = 0f;
        foreach (var s in samples)
        {
            n++; sumDt += s.dt;
            if (!s.valid) continue;
            nv++;
            sl += s.l; sl2 += s.l * s.l;
            sr += s.r; sr2 += s.r * s.r;
            sdiff += Mathf.Abs(s.dT - s.eT);
        }
        float validPct = n > 0 ? 100f * nv / n : 0f;
        float sdL = nv > 1 ? Mathf.Sqrt(Mathf.Max(0f, sl2 / nv - (sl / nv) * (sl / nv))) : 0f;
        float sdR = nv > 1 ? Mathf.Sqrt(Mathf.Max(0f, sr2 / nv - (sr / nv) * (sr / nv))) : 0f;
        float diff = nv > 0 ? sdiff / nv : 0f;
        float fps = sumDt > 0f ? n / sumDt : 0f;

        var sb = new System.Text.StringBuilder(256);
        if (elbow == null)
        {
            sb.Append("BODY: no ElbowTracker\n");
        }
        else
        {
            sb.Append("BODY perm ").Append(YN(elbow.permissionGranted))
              .Append("  run ").Append(YN(elbow.started))
              .Append("  data ").Append(YN(elbow.hasData))
              .Append("  simul ").Append(elbow.simultaneousOn ? "ON" : "off")
              .Append(elbow.disableSimultaneousHands ? (elbow.simultaneousSwitchOk ? " (sw ok)" : " (sw ?)") : "")
              .Append('\n');
            sb.Append("calib ").Append(elbow.calibration)
              .Append("  conf ").Append(elbow.confidence.ToString("F2"))
              .Append("  joints ").Append(YN(elbow.jointsValid)).Append('\n');
            sb.Append("elbow L ").Append(elbow.leftAngle.ToString("F0"))
              .Append("  R ").Append(elbow.rightAngle.ToString("F0"))
              .Append("  min ").Append(elbow.Angle.ToString("F0")).Append('\n');
        }
        if (expansion != null)
        {
            sb.Append("dist ").Append(expansion.distance.ToString("F2")).Append(" m")
              .Append("  ctrlT ").Append(expansion.distanceT.ToString("F2"))
              .Append("  elbT ").Append(expansion.elbowT.ToString("F2")).Append('\n');
            string mode = !expansion.useElbow ? "OFF (controller only)"
                        : expansion.elbowWeight >= 0.999f ? "ELBOW+CTRL (min)"
                        : expansion.elbowWeight <= 0.001f ? "CTRL (elbow invalid)"
                        : "blending";
            sb.Append("mode ").Append(mode).Append("  w ").Append(expansion.elbowWeight.ToString("F2")).Append('\n');
            sb.Append("elbow cal ").Append(expansion.elbowCalibrated
                    ? expansion.minA.ToString("F0") + " -> " + expansion.maxA.ToString("F0")
                    : "none").Append('\n');
        }
        sb.Append("last ").Append(window.ToString("F0")).Append("s: valid ").Append(validPct.ToString("F0")).Append('%')
          .Append("  sdL ").Append(sdL.ToString("F1"))
          .Append("  sdR ").Append(sdR.ToString("F1")).Append('\n');
        sb.Append("|ctrlT-elbT| ").Append(diff.ToString("F2"))
          .Append("  fps ").Append(fps.ToString("F0"));
        return sb.ToString();
    }

    static string YN(bool b) => b ? "Y" : "N";
}
