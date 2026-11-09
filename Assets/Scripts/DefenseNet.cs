using UnityEngine;

// 防禦網:玩家「現在被咬擋得下」時,周圍出現一圈六角形網格,快失效時閃爍。
// 由 HeronThreat 自動加上,物件和材質都在執行時建立,不需要改場景。
// 網格跟著頭的位置移動,但不跟著頭轉,避免暈眩。
public class DefenseNet : MonoBehaviour
{
    [Tooltip("網的半徑(公尺,身體沒膨脹時)。")]
    public float radius = 0.8f;
    public Color color = new Color(0.3f, 0.9f, 1f, 1f);
    [Tooltip("網格線最亮時的不透明度(0~1)。")]
    public float maxAlpha = 0.7f;
    [Tooltip("視線中央完全淨空的範圍(度,從正前方算起)。")]
    public float clearAngleDeg = 25f;
    [Tooltip("超過這個角度(度)格子才完全顯示,要比 clearAngleDeg 大。")]
    public float fullAngleDeg = 42f;
    [Tooltip("保留多少比例的格子(0~1),越小越零散。")]
    public float scatter = 0.45f;
    [Tooltip("六角格密度,越大格子越小。")]
    public float hexDensity = 7f;
    [Tooltip("網格線寬(0~0.5)。")]
    public float lineWidth = 0.05f;
    [Tooltip("淡入淡出時間(秒)。")]
    public float fadeSeconds = 0.15f;
    [Tooltip("防禦剩下幾秒時開始閃爍。")]
    public float warnSeconds = 1f;
    [Tooltip("閃爍頻率(次/秒)。")]
    public float flickerHz = 6f;

    static readonly int ColorId = Shader.PropertyToID("_Color");
    static readonly int AlphaId = Shader.PropertyToID("_Alpha");
    static readonly int DensityId = Shader.PropertyToID("_HexDensity");
    static readonly int LineWidthId = Shader.PropertyToID("_LineWidth");
    static readonly int ClearCosId = Shader.PropertyToID("_ClearCos");
    static readonly int FullCosId = Shader.PropertyToID("_FullCos");
    static readonly int ScatterId = Shader.PropertyToID("_Scatter");
    static readonly int HeadForwardId = Shader.PropertyToID("_HeadForward");

    HeronThreat heron;
    GameObject net;
    MeshRenderer netRenderer;
    Material material;
    float visibility;

    void Start()
    {
        heron = GetComponent<HeronThreat>();
        var shader = Resources.Load<Shader>("DefenseNet");
        var mesh = Resources.GetBuiltinResource<Mesh>("Sphere.fbx");
        if (heron == null || shader == null || mesh == null)
        {
            Debug.LogError($"[DefenseNet] 缺少必要資源:heron={heron != null} shader={shader != null} mesh={mesh != null}", this);
            enabled = false;
            return;
        }

        net = new GameObject("DefenseNet");
        net.layer = 2;   // Ignore Raycast
        net.AddComponent<MeshFilter>().sharedMesh = mesh;
        netRenderer = net.AddComponent<MeshRenderer>();
        netRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        netRenderer.receiveShadows = false;
        material = new Material(shader);
        netRenderer.sharedMaterial = material;
        netRenderer.enabled = false;
    }

    void OnDisable()
    {
        visibility = 0f;
        if (netRenderer != null) netRenderer.enabled = false;
    }

    void OnDestroy()
    {
        if (net != null) Destroy(net);
        if (material != null) Destroy(material);
    }

    void LateUpdate()
    {
        Transform head = heron.player;
        if (head == null || net == null) return;

        bool active = heron.WouldBlock;
        float step = fadeSeconds > 0f ? Time.deltaTime / fadeSeconds : 1f;
        visibility = Mathf.MoveTowards(visibility, active ? 1f : 0f, step);

        bool visible = visibility > 0.001f;
        if (netRenderer.enabled != visible) netRenderer.enabled = visible;
        if (!visible) return;

        float flicker = 1f;
        if (active && heron.DefenseSecondsLeft < warnSeconds)
            flicker = Mathf.Lerp(0.2f, 1f, 0.5f + 0.5f * Mathf.Sin(Time.time * flickerHz * 2f * Mathf.PI));

        // 玩家的 rig 放大時,網跟著放大,看起來的大小才一致
        float rigScale = heron.expansion != null ? heron.expansion.scale : 1f;
        net.transform.SetPositionAndRotation(head.position, Quaternion.identity);
        net.transform.localScale = Vector3.one * (radius * 2f * rigScale);

        material.SetColor(ColorId, color);
        material.SetFloat(AlphaId, maxAlpha * visibility * flicker);
        material.SetFloat(DensityId, hexDensity);
        material.SetFloat(LineWidthId, lineWidth);
        material.SetFloat(ClearCosId, Mathf.Cos(clearAngleDeg * Mathf.Deg2Rad));
        material.SetFloat(FullCosId, Mathf.Cos(Mathf.Max(fullAngleDeg, clearAngleDeg + 0.1f) * Mathf.Deg2Rad));
        material.SetFloat(ScatterId, scatter);
        material.SetVector(HeadForwardId, head.forward);
    }
}
