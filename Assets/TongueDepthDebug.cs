using System.Text;
using Meta.XR;
using TMPro;
using UnityEngine;

// 吹龍偵錯:在頭顯裡顯示深度射線打到的位置、量到的距離,以及算出來的舌頭長度。
// 青色小點 = 命中的射線;紅色大點 = 決定距離的點。Raw = 未經過濾的最遠距離。
// 舌頭輸入跨過 Trigger Above 時,會立刻在 Console 記一筆,方便查誤觸。
// 射線網量測:每隔 Grid Log Interval 在 Console 記一筆 [DepthGrid],列出每條射線的距離(公分)。
// 測試時按住對應的鍵,該筆會加上標記,方便分開比較:
//   右手 B = BLOW(吹龍)、右手 A = TILT(翹起吹龍沒吹)、左手 X = CTRL(手把)、左手 Y = FINGER(手指)
// 不需要時把這個元件關掉即可。
public class TongueDepthDebug : MonoBehaviour
{
    public BlowoutDepthProbe probe;
    public TongueTrigger tongue;
    public Transform head;
    public Renderer tipMarker;

    [Header("頭顯內文字")]
    public Vector3 hudOffset = new Vector3(-0.16f, -0.1f, 0.6f);
    public float hudFontSize = 0.28f;

    [Header("標記")]
    public float hitDotSize = 0.012f;
    public float tipDotSize = 0.03f;
    public Color hitColor = Color.cyan;
    public Color tipColor = Color.red;

    [Header("Console")]
    public float logInterval = 0.5f;
    public float triggerAbove = 0.5f;
    public float gridLogInterval = 0.2f;

    private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");

    private TextMeshPro hud;
    private Renderer[] hitDots = new Renderer[0];
    private MaterialPropertyBlock colorBlock;
    private float nextLogTime;
    private float previousRawInput;
    private float nextGridLogTime;
    private readonly StringBuilder gridText = new StringBuilder();

    private void Start()
    {
        colorBlock = new MaterialPropertyBlock();

        var hudObject = new GameObject("TongueDebugHud");
        hudObject.transform.SetParent(head, false);
        hudObject.transform.localPosition = hudOffset;
        hud = hudObject.AddComponent<TextMeshPro>();
        hud.fontSize = hudFontSize;
        hud.alignment = TextAlignmentOptions.TopLeft;
        hud.textWrappingMode = TextWrappingModes.NoWrap;
        hud.rectTransform.sizeDelta = new Vector2(0.4f, 0.2f);

        tipMarker.transform.localScale = Vector3.one * tipDotSize;
        SetColor(tipMarker, tipColor);
        tipMarker.gameObject.SetActive(false);
    }

    private void LateUpdate()
    {
        UpdateMarkers();

        float maxLength = tongue.maxLength;
        float length = tongue.Extension * maxLength;
        bool hasHit = probe.TipDistance > 0f;
        string farText = hasHit ? (probe.TipDistance * 100f).ToString("F1") + " cm" : "--";
        string rawText = probe.NearHitCount > 0 ? (probe.RawFarDistance * 100f).ToString("F1") + " cm" : "--";

        string mark = CurrentMark();
        LogGrid(mark);

        hud.text = (mark != null ? "== " + mark + " ==\n" : "")
                 + $"Hits  {probe.NearHitCount} / {probe.RayCount}  Line {probe.LineCellCount}\n"
                 + $"{probe.DescribeResult()}\n"
                 + $"Far   {farText}\n"
                 + $"Raw   {rawText}\n"
                 + $"Input {tongue.RawInput:F2}\n"
                 + $"Tongue {length:F2} / {maxLength:F2} m";

        bool crossedUp = previousRawInput < triggerAbove && tongue.RawInput >= triggerAbove;
        previousRawInput = tongue.RawInput;

        if (crossedUp || Time.unscaledTime >= nextLogTime)
        {
            nextLogTime = Time.unscaledTime + logInterval;
            string label = crossedUp ? "[TongueDebug] TRIGGER" : "[TongueDebug]";
            string tipFromHead = hasHit ? head.InverseTransformPoint(probe.TipPoint).ToString("F2") : "--";
            Debug.Log($"{label} hits={probe.NearHitCount}/{probe.RayCount} far={farText} raw={rawText} tipFromHead={tipFromHead} input={tongue.RawInput:F2} tongue={length:F2}m");
        }
    }

    // 格子圖,從你的眼睛看出去:上面 = 平視,下面 = 往下看(嘴巴),左右 = 臉的左右。
    // A24 = 第 A 塊、24 公分(在有效距離內,會參與細線判定)
    // (45) = 有打到,但不在有效距離內(太近或太遠),不算
    // H24 = 有打到,但離手把太近,當成手把排除,不算
    // -- 沒打中;ro 整條被遮住;oc 命中點被遮住;fr 超出深度相機範圍;nr 尚未就緒
    // 圖下面列出每一塊有沒有通過細線判定,沒過的話是哪一條規則、數字多少。
    // 測試標記:按住哪個鍵,就代表現在在測哪一種情況。沒按 = null。
    // B = 吹龍;A = 翹起吹龍但沒吹;X = 拿手把在臉前比;Y = 手指(沒拿手把)在臉前比
    public static string CurrentMark()
    {
        if (OVRInput.Get(OVRInput.Button.Two, OVRInput.Controller.RTouch)) return "BLOW";
        if (OVRInput.Get(OVRInput.Button.One, OVRInput.Controller.RTouch)) return "TILT";
        if (OVRInput.Get(OVRInput.Button.One, OVRInput.Controller.LTouch)) return "CTRL";
        if (OVRInput.Get(OVRInput.Button.Two, OVRInput.Controller.LTouch)) return "FINGER";
        return null;
    }

    private void LogGrid(string mark)
    {
        if (Time.unscaledTime < nextGridLogTime || probe.CellDistances.Length == 0) return;
        nextGridLogTime = Time.unscaledTime + gridLogInterval;
        Debug.Log(GridReport(mark));
    }

    // 目前這一輪的格子圖文字(和 Console 的 [DepthGrid] 同一個格式)
    public string GridReport(string mark)
    {
        if (probe.CellDistances.Length == 0) return "";

        int columns = probe.columns;
        int rows = probe.rows;
        gridText.Clear();
        gridText.Append("[DepthGrid] t=").Append(Time.unscaledTime.ToString("F2"))
                .Append(mark != null ? "  == " + mark + " ==" : "")
                .Append("  hits=").Append(probe.NearHitCount)
                .Append("  hand=").Append(probe.HandIgnoredCount)
                .Append("  far=").Append(probe.TipDistance > 0f ? (probe.TipDistance * 100f).ToString("F1") + "cm" : "--")
                .Append("  input=").Append(tongue.RawInput.ToString("F2"))
                .Append("\n結果: ").Append(probe.DescribeResult())
                .Append('\n');

        // 欄標題:每欄的左右角度
        gridText.Append("        ");
        for (int column = 0; column < columns; column++)
        {
            float yaw = probe.YawOfColumn(column);
            string label = Mathf.Abs(yaw) < 0.5f ? "中" : (yaw < 0 ? "左" : "右") + Mathf.RoundToInt(Mathf.Abs(yaw));
            Pad(label, 6);
        }
        gridText.Append('\n');

        for (int row = 0; row < rows; row++)
        {
            float pitch = probe.PitchOfRow(row);
            Pad(row + "排 " + Mathf.RoundToInt(pitch) + "°", 8);
            for (int column = 0; column < columns; column++)
            {
                int cell = column + row * columns;
                Pad(CellText(cell), 6);
            }
            gridText.Append('\n');
        }

        for (int i = 0; i < probe.ClusterCount; i++)
            gridText.Append("  ").Append(probe.DescribeCluster(i)).Append('\n');

        return gridText.ToString();
    }

    private void Pad(string text, int width)
    {
        gridText.Append(text);
        for (int i = text.Length; i < width; i++) gridText.Append(' ');
    }

    private string CellText(int cell)
    {
        int cluster = probe.CellClusters[cell];
        if (cluster >= 0)
            return (char)('A' + cluster) + Mathf.RoundToInt(probe.CellDistances[cell] * 100f).ToString();

        float distance = probe.CellDistances[cell];
        if (probe.CellNearHand[cell])
            return "H" + Mathf.RoundToInt(distance * 100f);

        switch (probe.CellStatuses[cell])
        {
            case EnvironmentRaycastHitStatus.Hit: return "(" + Mathf.RoundToInt(distance * 100f) + ")";
            case EnvironmentRaycastHitStatus.NoHit: return "--";
            case EnvironmentRaycastHitStatus.RayOccluded: return "ro";
            case EnvironmentRaycastHitStatus.HitPointOccluded: return "oc";
            case EnvironmentRaycastHitStatus.HitPointOutsideOfCameraFrustum: return "fr";
            case EnvironmentRaycastHitStatus.NotReady: return "nr";
            default: return "??";
        }
    }

    private void UpdateMarkers()
    {
        int count = probe.NearHitCount;
        EnsureHitDots(count);

        for (int i = 0; i < hitDots.Length; i++)
        {
            bool visible = i < count;
            if (hitDots[i].gameObject.activeSelf != visible)
                hitDots[i].gameObject.SetActive(visible);
            if (visible)
                hitDots[i].transform.position = probe.HitPoints[i];
        }

        bool hasHit = probe.TipDistance > 0f;
        if (tipMarker.gameObject.activeSelf != hasHit)
            tipMarker.gameObject.SetActive(hasHit);
        if (hasHit)
            tipMarker.transform.position = probe.TipPoint;
    }

    private void EnsureHitDots(int count)
    {
        if (count <= hitDots.Length) return;

        var dots = new Renderer[count];
        hitDots.CopyTo(dots, 0);
        for (int i = hitDots.Length; i < count; i++)
        {
            Renderer dot = Instantiate(tipMarker, transform);
            dot.name = "TongueDebugHit";
            dot.transform.localScale = Vector3.one * hitDotSize;
            SetColor(dot, hitColor);
            dots[i] = dot;
        }
        hitDots = dots;
    }

    private void SetColor(Renderer target, Color color)
    {
        colorBlock.Clear();
        colorBlock.SetColor(BaseColorId, color);
        target.SetPropertyBlock(colorBlock);
    }
}
