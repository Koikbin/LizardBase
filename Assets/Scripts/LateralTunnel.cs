using UnityEngine;

// 橫向移動時的隧道效果(視野周圍變暗,減少暈眩)。
// 遮罩是一顆包住頭的小球(Resources/LateralTunnel.shader),從裡面看:正前方透明、周圍變暗。
// 用球而不用眼前的平面:兩隻眼睛的視野邊緣都一定蓋得到,洞的位置也是各眼自己算,頭盔雙眼渲染不會偏。
// 量的是 Player(身體)的移動速度,不含真實走動或轉頭造成的頭部移動;速度換算成玩家的真實大小。
// 掛在場景最上層的物件上(不要放在會縮放的 rig 底下)。
public class LateralTunnel : MonoBehaviour
{
    [Tooltip("被移動的身體(有 CharacterController 的 Player)。")]
    public Transform body;
    [Tooltip("頭(CenterEyeAnchor),用來決定哪個方向算「橫向」,也用它的縮放把速度換算成真實大小。留空用主攝影機。")]
    public Transform head;

    [Header("觸發")]
    [Tooltip("勾選 = 只有往左右(相對於臉的方向)移動才觸發;不勾 = 任何水平移動都觸發。")]
    public bool lateralOnly = true;
    [Tooltip("爬牆時的上下移動也算。")]
    public bool includeVertical = false;
    [Tooltip("橫向速度超過這個值(公尺/秒,真實大小)開始出現。")]
    public float startSpeed = 0.15f;
    [Tooltip("橫向速度到這個值(公尺/秒,真實大小)時最濃。")]
    public float fullSpeed = 0.8f;
    [Tooltip("用搖桿轉向時也出現。")]
    public bool alsoWhenTurning = false;
    [Tooltip("轉向速度到這個值(度/秒)時最濃。")]
    public float fullTurnSpeed = 90f;

    [Header("外觀")]
    [Tooltip("最濃時中間完全清楚的視野(度,整個圓錐的角度)。")]
    [Range(5f, 170f)] public float clearFov = 50f;
    [Tooltip("從清楚漸變到全暗的寬度(度)。")]
    [Range(1f, 90f)] public float feather = 25f;
    public Color color = Color.black;
    [Tooltip("最濃時的不透明度。")]
    [Range(0f, 1f)] public float maxAlpha = 1f;
    [Tooltip("出現的速度(每秒),越大越快變濃。")]
    public float fadeInSpeed = 6f;
    [Tooltip("消失的速度(每秒)。")]
    public float fadeOutSpeed = 3f;
    [Tooltip("遮罩球的半徑(以玩家真實大小計,會乘上玩家縮放)。要比相機的 Near Clip 遠。")]
    public float sphereRadius = 0.4f;

    [Header("除錯")]
    [Tooltip("勾選 = 不管有沒有移動,一律以最濃顯示(用來看效果長怎樣)。")]
    public bool forceOn = false;
    public float lateralSpeed;   // 真實大小 公尺/秒(唯讀)
    public float strength;       // 0~1(唯讀)

    static readonly int ColorId = Shader.PropertyToID("_Color");
    static readonly int AlphaId = Shader.PropertyToID("_Alpha");
    static readonly int ClearCosId = Shader.PropertyToID("_ClearCos");
    static readonly int FullCosId = Shader.PropertyToID("_FullCos");

    GameObject sphere;
    MeshRenderer sphereRenderer;
    Material material;
    Vector3 lastPos;
    float lastYaw;
    bool hasLast;

    void Start()
    {
        if (head == null && Camera.main != null) head = Camera.main.transform;
        var shader = Shader.Find("Lizard/LateralTunnel");
        if (body == null || head == null || shader == null)
        {
            Debug.LogError($"[LateralTunnel] 缺少 {(body == null ? "Body " : "")}{(head == null ? "Head " : "")}{(shader == null ? "Shader(Resources/LateralTunnel.shader)" : "")},隧道效果不會出現。", this);
            enabled = false;
            return;
        }

        sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        sphere.name = "LateralTunnelSphere";
        sphere.layer = 2;   // Ignore Raycast
        Destroy(sphere.GetComponent<Collider>());
        sphere.transform.SetParent(transform, false);
        sphereRenderer = sphere.GetComponent<MeshRenderer>();
        sphereRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        sphereRenderer.receiveShadows = false;
        material = new Material(shader);
        sphereRenderer.sharedMaterial = material;
        sphereRenderer.enabled = false;
    }

    void OnDestroy()
    {
        if (material != null) Destroy(material);
    }

    void LateUpdate()
    {
        float dt = Time.deltaTime;
        if (dt <= 0f || sphere == null) return;

        float scale = Mathf.Max(0.0001f, head.lossyScale.x);
        Vector3 pos = body.position;
        float yaw = body.eulerAngles.y;
        if (!hasLast) { lastPos = pos; lastYaw = yaw; hasLast = true; return; }

        Vector3 v = (pos - lastPos) / dt / scale;
        float turn = Mathf.Abs(Mathf.DeltaAngle(lastYaw, yaw)) / dt;
        lastPos = pos;
        lastYaw = yaw;

        // 一幀跳很遠(被推回、重設位置)不算移動
        if (v.magnitude > 20f) v = Vector3.zero;

        Vector3 flat = new Vector3(v.x, 0f, v.z);
        float horizontal;
        if (lateralOnly)
        {
            Vector3 right = head.right; right.y = 0f;
            horizontal = right.sqrMagnitude > 0.0001f ? Mathf.Abs(Vector3.Dot(flat, right.normalized)) : 0f;
        }
        else horizontal = flat.magnitude;
        float vertical = includeVertical ? Mathf.Abs(v.y) : 0f;
        lateralSpeed = Mathf.Lerp(lateralSpeed, Mathf.Max(horizontal, vertical), 1f - Mathf.Exp(-15f * dt));

        float target = Mathf.InverseLerp(startSpeed, fullSpeed, lateralSpeed);
        if (alsoWhenTurning && fullTurnSpeed > 0f) target = Mathf.Max(target, Mathf.Clamp01(turn / fullTurnSpeed));
        if (forceOn) target = 1f;

        strength = Mathf.MoveTowards(strength, target, (target > strength ? fadeInSpeed : fadeOutSpeed) * dt);

        bool show = strength > 0.001f;
        if (sphereRenderer.enabled != show) sphereRenderer.enabled = show;
        if (!show) return;

        sphere.transform.SetPositionAndRotation(head.position, Quaternion.identity);
        sphere.transform.localScale = Vector3.one * (sphereRadius * 2f * scale);   // Unity 的球直徑 = 1

        float clearHalf = clearFov * 0.5f;
        material.SetColor(ColorId, color);
        material.SetFloat(AlphaId, strength * maxAlpha);
        material.SetFloat(ClearCosId, Mathf.Cos(clearHalf * Mathf.Deg2Rad));
        material.SetFloat(FullCosId, Mathf.Cos(Mathf.Min(179f, clearHalf + feather) * Mathf.Deg2Rad));
    }
}
