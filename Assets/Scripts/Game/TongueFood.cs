using System.Collections.Generic;
using UnityEngine;

// 舌頭可以吃的靜態物件:森林水珠(整顆吞)、地洞礦石(一次咬下一小塊)。
// 掛在物件上即可,TongueTrigger 會自動找到。
//   水珠:Kind = Water,Swallow Whole 打勾,Bites = 1 → 舌尖碰到就整顆黏住,縮回吃掉
//   礦石:Kind = OreEnergy,Swallow Whole 不勾,Bites = 3 → 每次咬下一小塊礦石,咬完變暗
public class TongueFood : MonoBehaviour, ITongueEdible
{
    public static readonly List<TongueFood> All = new List<TongueFood>();

    [Header("資源")]
    public ResourceKind kind = ResourceKind.Water;
    public float amountPerBite = 1f;
    [Min(1)] public int bites = 1;

    [Header("吃法")]
    [Tooltip("打勾 = 整個物件黏在舌尖上被吞掉(水珠);不勾 = 咬下一小塊,本體留在原地(礦石)")]
    public bool swallowWhole = true;
    [Tooltip("舌尖離物件表面多近算碰到(公尺)")]
    public float catchPadding = 0.1f;
    [Tooltip("吃完後幾秒重生,0 = 不重生")]
    public float respawnSeconds = 0f;
    [Tooltip("整顆吞的東西吃下去後慢慢縮小消失:秒數 = Base + 直徑(公尺) x Per Meter")]
    public float shrinkBaseSeconds = 0.5f;
    public float shrinkSecondsPerMeter = 2f;
    [Tooltip("打勾 = 舌頭碰到時,水珠留在原本的位置(葉子、藤蔓上)慢慢縮小消失,不會被舌頭帶走")]
    public bool shrinkInPlace = true;

    [Header("咬一口的外觀(Swallow Whole 不勾時)")]
    public Material chunkMaterial;
    public float chunkSize = 0.06f;
    public Color depletedTint = new Color(0.3f, 0.3f, 0.3f, 1f);

    static readonly Collider[] overlap = new Collider[32];
    static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    static readonly int ColorId = Shader.PropertyToID("_Color");

    Renderer[] renderers;
    Collider[] colliders;
    int bitesLeft;
    Transform holder;
    Vector3 holdOffset;
    Vector3 homePosition;
    Transform chunk;
    Vector3 homeScale;
    Coroutine shrinking;

    public int BitesLeft => bitesLeft;
    public float TotalAmount => bites * amountPerBite;
    public bool IsDepleted => bitesLeft <= 0;
    public string DisplayName => name;
    public bool CanBeCaught => isActiveAndEnabled && bitesLeft > 0 && holder == null;

    void Awake()
    {
        renderers = GetComponentsInChildren<Renderer>(true);
        colliders = GetComponentsInChildren<Collider>(true);
        bitesLeft = bites;
        homePosition = transform.position;
        homeScale = transform.localScale;
    }

    void OnEnable() { All.Add(this); }
    void OnDisable() { All.Remove(this); }

    void LateUpdate()
    {
        if (holder == null) return;
        if (swallowWhole) { if (!shrinkInPlace) transform.position = holder.position + holdOffset; }
        else if (chunk != null) chunk.position = holder.position;
    }

    public Bounds WorldBounds()
    {
        // 編輯器裡(沒進 Play)Awake 不會執行,這裡要自己抓
        if (renderers == null) renderers = GetComponentsInChildren<Renderer>(true);
        Bounds b = default;
        bool any = false;
        foreach (var r in renderers)
        {
            if (r == null || !r.enabled) continue;
            if (!any) { b = r.bounds; any = true; }
            else b.Encapsulate(r.bounds);
        }
        return any ? b : new Bounds(transform.position, Vector3.zero);
    }

    public float CatchValue(TongueTrigger tongue, Vector3 tip)
    {
        float pad = Mathf.Max(0.001f, catchPadding * (tongue != null ? tongue.SizeScale : 1f));
        float d = Mathf.Sqrt(WorldBounds().SqrDistance(tip));
        if (d > pad) return 2f;                        // 連外框都沒碰到

        // 有碰撞體的大物件(礦石):外框太粗略,要真的碰到碰撞體才算
        if (HasEnabledCollider())
        {
            int n = Physics.OverlapSphereNonAlloc(tip, pad, overlap, ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
                if (overlap[i].transform.IsChildOf(transform)) return 0.5f;
            return 2f;
        }
        return d / pad;
    }

    public void OnCaught(Transform tip)
    {
        holder = tip;
        if (swallowWhole)
        {
            holdOffset = transform.position - WorldBounds().center;
            SetColliders(false);
            // 留在原地:舌頭一碰到就開始在原本的位置縮小
            if (shrinkInPlace)
            {
                if (shrinking != null) StopCoroutine(shrinking);
                shrinking = StartCoroutine(ShrinkAway(null));
            }
        }
        else
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = $"{name}_Chunk";
            Destroy(go.GetComponent<Collider>());
            if (chunkMaterial != null) go.GetComponent<Renderer>().sharedMaterial = chunkMaterial;
            chunk = go.transform;
            chunk.localScale = Vector3.one * chunkSize;
            chunk.position = tip.position;
        }
    }

    public void OnEaten()
    {
        Vector3 where = holder != null ? holder.position : transform.position;
        Transform mouth = holder;
        holder = null;
        bitesLeft--;
        LizardResources.Add(kind, amountPerBite, where);

        if (swallowWhole && !shrinkInPlace)
        {
            if (shrinking != null) StopCoroutine(shrinking);
            shrinking = StartCoroutine(ShrinkAway(mouth));
        }
        // shrinkInPlace:碰到時已經開始在原地縮小,縮完自己消失
        else
        {
            if (chunk != null) Destroy(chunk.gameObject);
            chunk = null;
            if (bitesLeft <= 0) SetTint(depletedTint);
        }

        if (bitesLeft <= 0 && respawnSeconds > 0f) Invoke(nameof(Respawn), respawnSeconds);
    }

    // 慢慢縮小,縮完才消失;大顆的水珠縮比較久。mouth = null:繞著自己的中心在原地縮小;有 mouth:黏在嘴邊縮小
    System.Collections.IEnumerator ShrinkAway(Transform mouth)
    {
        Bounds b = WorldBounds();
        float size = Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z));
        float seconds = Mathf.Clamp(shrinkBaseSeconds + size * shrinkSecondsPerMeter, 0.3f, 3f);
        Vector3 startScale = transform.localScale;
        Vector3 pivotToCenter = b.center - transform.position;   // 模型原點不在水珠中心,縮放時要繞中心縮
        Vector3 anchor = b.center;
        for (float t = 0f; t < seconds; t += Time.deltaTime)
        {
            if (mouth != null) anchor = mouth.position;
            float k = 1f - Mathf.SmoothStep(0f, 1f, t / seconds);
            transform.localScale = startScale * k;
            transform.position = anchor - pivotToCenter * k;
            yield return null;
        }
        SetVisible(false);
        transform.localScale = homeScale;
        transform.position = homePosition;
        shrinking = null;
    }

    void Respawn()
    {
        bitesLeft = bites;
        transform.localScale = homeScale;
        transform.position = homePosition;
        SetVisible(true);
        SetColliders(true);
        ClearTint();
    }

    bool HasEnabledCollider()
    {
        if (colliders == null) colliders = GetComponentsInChildren<Collider>(true);
        foreach (var c in colliders) if (c != null && c.enabled) return true;
        return false;
    }

    void SetVisible(bool v) { foreach (var r in renderers) if (r != null) r.enabled = v; }
    void SetColliders(bool v) { foreach (var c in colliders) if (c != null) c.enabled = v; }

    void SetTint(Color c)
    {
        var mpb = new MaterialPropertyBlock();
        foreach (var r in renderers)
        {
            if (r == null) continue;
            r.GetPropertyBlock(mpb);
            mpb.SetColor(BaseColorId, c);
            mpb.SetColor(ColorId, c);
            r.SetPropertyBlock(mpb);
        }
    }

    void ClearTint() { foreach (var r in renderers) if (r != null) r.SetPropertyBlock(null); }

    void OnDrawGizmosSelected()
    {
        Gizmos.color = kind == ResourceKind.Water ? Color.cyan : kind == ResourceKind.OreEnergy ? Color.magenta : Color.green;
        Bounds b = WorldBounds();
        Gizmos.DrawWireCube(b.center, b.size + Vector3.one * catchPadding * 2f);
    }
}
