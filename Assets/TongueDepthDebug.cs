using System.Text;
using Meta.XR;
using TMPro;
using UnityEngine;

// 吹龍偵錯:在頭顯裡顯示深度射線打到的位置、量到的距離,以及算出來的舌頭長度。
// 青色小點 = 命中的射線;紅色大點 = 決定距離的點。Raw = 未經過濾的最遠距離。
// 舌頭輸入跨過 Trigger Above 時,會立刻在 Console 記一筆,方便查誤觸。
// 射線網量測:每隔 Grid Log Interval 在 Console 記一筆 [DepthGrid],列出每條射線的距離(公分)。
// 吹龍時按住右手 B 鍵,該筆會標記 mark=BLOW,方便和沒吹的資料比較。
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

        bool blowMarked = OVRInput.Get(OVRInput.Button.Two, OVRInput.Controller.RTouch);
        LogGrid(blowMarked);

        hud.text = (blowMarked ? "== BLOW ==\n" : "")
                 + $"Hits  {probe.NearHitCount} / {probe.RayCount}  Line {probe.LineCellCount}\n"
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

    // 一筆一行,方便從 Editor.log 分析。每列是一排射線(最上排在前),| 分隔,左到右。
    // 數字 = 公分;-- 沒打中;ro 整條被遮住;oc 命中點被遮住;fr 超出深度相機範圍;nr 尚未就緒。
    private void LogGrid(bool blowMarked)
    {
        if (Time.unscaledTime < nextGridLogTime || probe.CellDistances.Length == 0) return;
        nextGridLogTime = Time.unscaledTime + gridLogInterval;

        int grid = probe.grid;
        gridText.Clear();
        gridText.Append("[DepthGrid] t=").Append(Time.unscaledTime.ToString("F2"))
                .Append(" mark=").Append(blowMarked ? "BLOW" : "none")
                .Append(" hits=").Append(probe.NearHitCount)
                .Append(" line=").Append(probe.LineCellCount)
                .Append(" far=").Append((probe.TipDistance * 100f).ToString("F1"))
                .Append(" raw=").Append((probe.RawFarDistance * 100f).ToString("F1"))
                .Append(" input=").Append(tongue.RawInput.ToString("F2"))
                .Append(" cells=");

        for (int row = 0; row < grid; row++)
        {
            if (row > 0) gridText.Append('|');
            for (int column = 0; column < grid; column++)
            {
                if (column > 0) gridText.Append(' ');
                int cell = column + row * grid;
                gridText.Append(CellText(probe.CellStatuses[cell], probe.CellDistances[cell]));
            }
        }

        Debug.Log(gridText.ToString());
    }

    private static string CellText(EnvironmentRaycastHitStatus status, float distance)
    {
        switch (status)
        {
            case EnvironmentRaycastHitStatus.Hit: return Mathf.RoundToInt(distance * 100f).ToString();
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
