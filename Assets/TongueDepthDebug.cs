using System;
using System.IO;
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

    [Header("連續紀錄")]
    [Tooltip("每隔 Grid Log Interval 寫一行摘要到 DepthShots/continuous_時間.txt;按住標記鍵時再多寫完整格子圖")]
    public bool saveContinuous = true;

    private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    private static readonly int DepthZBufferParamsId = Shader.PropertyToID("_EnvironmentDepthZBufferParams");
    private StreamWriter continuousLog;
    private DepthStripDetector stripDetector;

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

        // 文字永遠畫在最前面,不會被地面或其他物體擋住。
        // TextMeshPro 的 isOverlay 在目前的版本沒有作用,所以直接改材質。
        Material hudMaterial = hud.fontMaterial;
        hudMaterial.SetFloat(ShaderUtilities.ShaderTag_ZTestMode, (float)UnityEngine.Rendering.CompareFunction.Always);
        hudMaterial.renderQueue = 4000;

        tipMarker.transform.localScale = Vector3.one * tipDotSize;
        SetColor(tipMarker, tipColor);
        tipMarker.gameObject.SetActive(false);

        OpenContinuousLog();
    }

    private void OpenContinuousLog()
    {
        if (!saveContinuous) return;
#if UNITY_EDITOR
        string folder = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "DepthShots"));
#else
        string folder = Path.Combine(Application.persistentDataPath, "DepthShots");
#endif
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "continuous_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt");
        continuousLog = new StreamWriter(path, false, new UTF8Encoding(false)) { AutoFlush = true };
        Debug.Log("[TongueDebug] 連續紀錄存到 " + path);
    }

    private void OnDestroy()
    {
        if (continuousLog != null) continuousLog.Dispose();
        continuousLog = null;
    }

    // 深度圖細長條偵測的結果,寫進連續紀錄。只記錄,不影響舌頭
    private string StripSummary()
    {
        if (stripDetector == null) stripDetector = FindFirstObjectByType<DepthStripDetector>();
        if (stripDetector == null) return "strip=0 rstrip=0 down=0";
        // strip = 左眼深度、rstrip = 右眼深度;down=1 表示低頭,這一輪沒有偵測
        return StripText("strip", "s", stripDetector.Left) + " " + StripText("rstrip", "r", stripDetector.Right)
            + (stripDetector.LookingDown ? " down=1" : " down=0");
    }

    private static string StripText(string name, string prefix, DepthStripDetector.Strip strip)
    {
        if (!strip.found) return name + "=0";
        return $"{name}=1 {prefix}a={strip.area} {prefix}w={strip.width} {prefix}h={strip.height} {prefix}x={strip.x} {prefix}y={strip.y}";
    }

    // 深度相機的最近距離(公尺):原始深度值 0 換算出來就是這個數字
    private static float DepthNearPlane()
    {
        Vector4 p = Shader.GetGlobalVector(DepthZBufferParamsId);
        return Mathf.Approximately(p.y, 1f) ? 0f : p.x / (p.y - 1f);
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
                 + $"Ray {RaycastModeHud}  from {(probe.measureFromMouth ? "mouth" : "eye")}\n"
                 + $"Hits  {probe.NearHitCount} / {probe.RayCount}  Line {probe.LineCellCount}\n"
                 + $"RO {probe.OccludedRatio * 100f:F0}%{(probe.DepthInvalid ? "  DEPTH INVALID" : "")}\n"
                 + $"{probe.DescribeResult()}\n"
                 + $"Far   {farText}\n"
                 + $"Raw   {rawText}\n"
                 + $"Input {tongue.RawInput:F2}   HeadY {head.localPosition.y:F2} m\n"
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
        string report = GridReport(mark);
        Debug.Log(report);

        if (continuousLog == null) return;
        continuousLog.WriteLine(
            $"t={Time.unscaledTime:F2} mark={mark ?? "none"} ro={probe.OccludedRatio * 100f:F0}% fr={probe.OutOfFrustumRatio * 100f:F0}%"
            + $" invalid={(probe.DepthInvalid ? 1 : 0)} near={DepthNearPlane() * 100f:F1}cm nearblock={probe.NearBlockCount} hits={probe.NearHitCount}"
            + $" tip={probe.TipDistance * 100f:F1}cm input={tongue.RawInput:F2}"
            + $" pitch={Mathf.Asin(Mathf.Clamp(-head.forward.y, -1f, 1f)) * Mathf.Rad2Deg:F1} worn={(OVRPlugin.userPresent ? 1 : 0)} {StripSummary()}");
        if (mark != null) continuousLog.Write(report);
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
                .Append("  手骨架=").Append(probe.TrackedHandSkeletons)
                .Append("  far=").Append(probe.TipDistance > 0f ? (probe.TipDistance * 100f).ToString("F1") + "cm" : "--")
                .Append("  input=").Append(tongue.RawInput.ToString("F2"))
                .Append("  龍尾到嘴=").Append(probe.TipDistance > 0f ? (Vector3.Distance(probe.MouthPoint, probe.TipPoint) * 100f).ToString("F1") + "cm" : "--")
                .Append("\n射線:").Append(RaycastMode)
                .Append("  距離從:").Append(probe.measureFromMouth ? "嘴巴" : "眼睛")
                .Append("  ro=").Append((probe.OccludedRatio * 100f).ToString("F0")).Append('%')
                .Append("  最近距離=").Append((DepthNearPlane() * 100f).ToString("F1")).Append("cm")
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

        // 同一輪、同樣的命中點,改成從嘴巴量的距離(公分)。只是記錄,不影響判定(除非勾了 measureFromMouth)
        // 字母 = 屬於第幾塊;* = 有打到但不算(距離外或手把);. = 沒打到
        gridText.Append("從嘴巴量(cm):\n");
        for (int row = 0; row < rows; row++)
        {
            Pad(row + "排", 8);
            for (int column = 0; column < columns; column++)
            {
                int cell = column + row * columns;
                Pad(MouthCellText(cell), 6);
            }
            gridText.Append('\n');
        }

        AppendHitGrid("從眼睛量(cm):", probe.CellEyeDistances);
        AppendHitGrid("眼睛正前方(cm):", probe.CellForwardDistances);
        AppendOcGrid("oc離嘴巴(cm):", probe.CellOcMouthDistances);
        AppendOcGrid("oc眼睛正前方(cm):", probe.CellOcForwardDistances);

        return gridText.ToString();
    }

    // 只列出 oc(Meta 判定不可靠)的格子(公分),其他 = .
    private void AppendOcGrid(string title, float[] values)
    {
        gridText.Append(title).Append('\n');
        for (int row = 0; row < probe.rows; row++)
        {
            Pad(row + "排", 8);
            for (int column = 0; column < probe.columns; column++)
            {
                int cell = column + row * probe.columns;
                bool oc = probe.CellStatuses[cell] == EnvironmentRaycastHitStatus.HitPointOccluded;
                Pad(oc ? Mathf.RoundToInt(values[cell] * 100f).ToString() : ".", 6);
            }
            gridText.Append('\n');
        }
    }

    // 只列出有打到的格子(公分),沒打到 = .
    private void AppendHitGrid(string title, float[] values)
    {
        gridText.Append(title).Append('\n');
        for (int row = 0; row < probe.rows; row++)
        {
            Pad(row + "排", 8);
            for (int column = 0; column < probe.columns; column++)
            {
                int cell = column + row * probe.columns;
                bool hit = probe.CellStatuses[cell] == EnvironmentRaycastHitStatus.Hit;
                Pad(hit ? Mathf.RoundToInt(values[cell] * 100f).ToString() : ".", 6);
            }
            gridText.Append('\n');
        }
    }

    // 編譯開關決定 EnvironmentRaycastManager 用哪一種射線
    private const string RaycastMode =
#if META_USE_DEPTH_MANAGER_RAYCASTING
        "深度圖版";
#else
        "系統原生版";
#endif

    // 頭顯內文字的字型沒有中文,用英文
    private const string RaycastModeHud =
#if META_USE_DEPTH_MANAGER_RAYCASTING
        "DEPTHMAP";
#else
        "NATIVE";
#endif

    private string MouthCellText(int cell)
    {
        if (probe.CellStatuses[cell] != EnvironmentRaycastHitStatus.Hit) return ".";
        string cm = Mathf.RoundToInt(probe.CellMouthDistances[cell] * 100f).ToString();
        int cluster = probe.CellClusters[cell];
        return cluster >= 0 ? (char)('A' + cluster) + cm : "*" + cm;
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
