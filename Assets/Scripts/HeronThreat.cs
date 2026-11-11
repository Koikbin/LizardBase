using TMPro;
using UnityEngine;

// 天敵(大笨鳥)回合,最小版。掛在 HeronRoot。
// Hidden(待命) → Approach(走向玩家) → Attack(一口一口咬) → 擋下夠多口就 Retreat(走回起點) → Hidden。
// 咬的時候膨脹不夠大 → 玩家被抓,天敵原地待命,等外部呼叫 ResetRound() 或 BeginApproach()。
// 不碰鏡頭、不移動玩家。天敵本身不應帶碰撞體。
public class HeronThreat : MonoBehaviour
{
    public enum State { Hidden, Approach, Attack, Retreat }
    // Stagger = 被擋下撞開;Strike = 沒擋下,撲出去咬(嘴碰到才算被抓,沒碰到就是咬空)
    public enum BitePhase { Waiting, Windup, Stagger, Strike }

    // ---- 事件與計數(每次按 Play 都會清空) ----
    public static event System.Action OnBiteWindup;
    public static event System.Action OnBiteBlocked;
    public static event System.Action OnPlayerCaught;
    public static event System.Action OnHeronGaveUp;
    public static int CaughtCount;
    public static int GaveUpCount;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        OnBiteWindup = null;
        OnBiteBlocked = null;
        OnPlayerCaught = null;
        OnHeronGaveUp = null;
        CaughtCount = 0;
        GaveUpCount = 0;
    }

    [Header("參考")]
    [Tooltip("玩家的頭。請拖 OVRCameraRig/TrackingSpace/CenterEyeAnchor。沒拖就用 Main Camera。")]
    public Transform player;
    [Tooltip("膨脹輸入。請拖 Systems/Expansion。這個物件要是啟用的,真實膨脹才會有值。")]
    public ExpansionInput expansion;
    [Tooltip("模型的臉不是朝 HeronRoot 的前方(+Z)時,用這個角度修正(度)。走起來側著或倒著就調這個。")]
    public float modelYawOffsetDeg = 0f;

    [Header("開關")]
    [Tooltip("關掉 = 天敵不顯示、不計時、不發事件。重新打開會自動回到起點待命。")]
    public bool heronEnabled = true;
    [Tooltip("待命時要不要把天敵藏起來(看不到)。")]
    public bool hideWhenIdle = false;

    [Header("待命")]
    [Tooltip("勾選 = 待命一段隨機時間後自己出發;不勾 = 一直等,直到別的腳本呼叫 BeginApproach()。")]
    public bool useTimer = false;
    [Tooltip("勾選 Use Timer 時,待命幾秒後出發(在最小值與最大值之間隨機)。")]
    public Vector2 hiddenSeconds = new Vector2(8f, 15f);

    [Header("咬中判定")]
    [Tooltip("勾選 = 沒擋下時鳥會撲出去咬,嘴尖真的碰到玩家的頭才算被抓;沒碰到就是咬空,過一下再咬下一口。不勾 = 舊行為(沒擋下就直接算被抓)。需要 HeronNeckAim。")]
    public bool requireBeakContact = true;
    [Tooltip("嘴尖離玩家頭(眼睛)多近算咬到。以玩家的真實大小計(公尺),會自動乘上玩家目前的縮放。")]
    public float biteHitRadius = 0.15f;
    [Tooltip("沒擋下時,撲出去咬再縮回來的總時間(秒)。前 30% 是撲出去。")]
    public float strikeSeconds = 0.5f;
    [Tooltip("撲咬時間的前多少比例內碰到才算咬到(0~1)。脖子轉動有一點延遲,所以比撲出去的 30% 長一些。")]
    [Range(0f, 1f)] public float strikeContactPart = 0.6f;
    [Tooltip("勾選 = 咬空後往玩家走近「差的距離」再咬下一口,避免一直在原地咬空。")]
    public bool stepCloserAfterMiss = true;
    [Tooltip("咬空後一次最多往前走多遠。以玩家的真實大小計(公尺),會乘上玩家縮放。")]
    public float missStepMax = 0.4f;
    [Tooltip("等下一口時,玩家離開攻擊距離超過這麼多(公尺,世界座標)就重新走過去。")]
    public float reengageMargin = 0.1f;

    [Header("牆壁")]
    [Tooltip("勾選 = 鳥走路會被牆(陡的面)擋住,而且隔著牆不會咬。")]
    public bool blockByWalls = true;
    [Tooltip("哪些 Layer 算牆。")]
    public LayerMask wallMask = 1;   // Default
    [Tooltip("鳥身體的半徑(公尺,世界座標)。")]
    public float bodyRadius = 0.05f;
    [Tooltip("鳥身體的高度(公尺,世界座標,不含脖子往上伸的部分)。")]
    public float bodyHeight = 0.45f;
    [Tooltip("腳底多高以下的東西不擋(公尺,世界座標),小台階和地面起伏可以直接走上去。")]
    public float stepHeight = 0.05f;
    [Tooltip("撞到的面法線 y 大於這個值 = 地面或緩坡,不擋;小於等於 = 牆。0.6 約等於 53° 的坡。")]
    [Range(0f, 1f)] public float walkableNormalY = 0.6f;

    [Header("貼地")]
    [Tooltip("勾選 = 走路時腳底跟著地面高低。")]
    public bool snapToGround = true;
    [Tooltip("哪些 Layer 算地面。")]
    public LayerMask groundMask = 1;   // Default
    [Tooltip("從腳底往上多高開始往下量地面(公尺)。能爬上的最大台階;太大會站到頭上方的東西上。")]
    public float groundCastHeight = 0.15f;
    [Tooltip("往腳底下量多深(公尺)。")]
    public float groundCastDepth = 3f;
    [Tooltip("模型浮起來或陷進地面時調這個(公尺)。")]
    public float groundOffset = 0f;
    [Tooltip("高度跟上地面的速度,越大越貼、越小越平滑。")]
    public float groundFollowSpeed = 12f;

    [Header("觸發區域")]
    [Tooltip("勾選 = 玩家進到區域裡天敵才會出發。區域優先用 StageManager 的「天敵區域邊界」,沒標就用「廢棄實驗室邊界」;兩個都沒有 = 不限制。")]
    public bool requireZone = true;
    [Tooltip("留空會自動找場景裡的 StageManager。")]
    public StageManager stages;
    [Tooltip("玩家剛進區域後,等幾秒出發(在最小值與最大值之間隨機)。之後每一輪仍用 Hidden Seconds。")]
    public Vector2 enterDelaySeconds = new Vector2(1.5f, 3f);
    [Tooltip("勾選 = 玩家在天敵走過來或咬的途中離開區域,天敵就走回起點(不算放棄)。")]
    public bool retreatWhenPlayerLeaves = true;

    [Header("移動")]
    [Tooltip("走路速度(公尺/秒)。走向玩家和走回起點都用這個。")]
    public float walkSpeed = 1.2f;
    [Tooltip("轉身速度(度/秒)。")]
    public float turnSpeedDeg = 360f;
    [Tooltip("離玩家頭部的水平距離剩多少公尺時停下來開始咬。")]
    public float attackDistance = 0.9f;

    [Header("防禦判定")]
    [Tooltip("膨脹程度(0~1)要到多少才擋得住一口。")]
    public float expandThreshold = 0.6f;
    [Tooltip("擋下一口後,雙手要先收回到膨脹程度低於這個值,下一口才算數。逼玩家每一口都重新撐開。")]
    public float rearmBelow = 0.3f;
    [Tooltip("膨脹會隨時間衰退,逼玩家抓時機撐開。開啟時擋下後由衰退機制要求收手,rearmBelow 只在鍵盤測試模式使用。")]
    public bool enableExpansionDecay = true;
    [Tooltip("擋下一口後,膨脹在幾秒內洩掉。")]
    public float blockDeflateSeconds = 0.4f;
    [Tooltip("擋得下時在玩家周圍顯示防禦網。")]
    public bool showDefenseNet = true;
    [Tooltip("判定時看「預告結束前這麼多秒」內膨脹的最大值,避免手抖一下就失敗。")]
    public float biteGraceSeconds = 0.15f;

    [Header("咬的節奏")]
    [Tooltip("停下來之後,等幾秒才咬第一口。")]
    public float firstBiteDelay = 1.0f;
    [Tooltip("每一口咬下前的預告動作(後仰、後退)持續幾秒。這段時間就是玩家反應的時間。")]
    public float windupSeconds = 0.6f;
    [Tooltip("預告時往後退幾公尺。")]
    public float windupBackDistance = 0.25f;
    [Tooltip("預告時身體往後仰幾度。")]
    public float windupTiltDeg = 25f;
    [Tooltip("往前撲幾公尺。擋下時撲出去會被彈回原位;被抓時撲出去就停在那裡。")]
    public float lungeDistance = 0.3f;
    [Tooltip("咬下去時身體依玩家頭的高度自動往前傾,讓嘴碰得到。關掉 = 固定前傾 windupTiltDeg 的一半。")]
    public bool autoBiteLean = true;
    [Tooltip("自動前傾的上限(度),可以超過 90,身體會繞髖部往下彎。")]
    public float maxBiteLeanDeg = 140f;
    [Tooltip("被抓時往前撲出去要花幾秒。")]
    public float caughtLungeSeconds = 0.12f;
    [Tooltip("往前撲時,和玩家頭部的水平距離至少要留幾公尺,不會撲到臉上。")]
    public float lungeMinHeadDistance = 0.5f;
    [Tooltip("被擋下後被撞開、站不穩的時間(秒)。")]
    public float staggerSeconds = 0.4f;
    [Tooltip("被撞開結束後,隔幾秒再咬下一口。")]
    public float biteInterval = 3f;
    [Tooltip("被擋下幾口後放棄、走回起點。")]
    public int bitesToGiveUp = 3;

    [Header("聲音與震動")]
    [Tooltip("擋下一口時讓兩隻手把震動。")]
    public bool hapticOnBlocked = true;
    [Tooltip("震動強度(0~1)。")]
    public float hapticAmplitude = 0.7f;
    [Tooltip("震動持續幾秒。")]
    public float hapticSeconds = 0.2f;
    [Tooltip("被咬中時讓兩隻手把震動。")]
    public bool hapticOnCaught = true;
    [Tooltip("被咬中的震動強度(0~1)。")]
    public float caughtHapticAmplitude = 1f;
    [Tooltip("被咬中的震動持續幾秒。")]
    public float caughtHapticSeconds = 0.5f;
    [Tooltip("預告(準備咬)開始時播放的音效(可不填)。")]
    public AudioClip windupClip;
    [Tooltip("擋下時播放的音效(可不填)。")]
    public AudioClip blockedClip;
    [Tooltip("用哪個 AudioSource 播音效(可不填,沒填就不播)。")]
    public AudioSource audioSource;

    [Header("除錯文字(測試用)")]
    [Tooltip("在天敵頭上顯示目前階段的文字。正式版關掉。")]
    public bool showDebugLabel = true;
    [Tooltip("文字離天敵腳底多高(公尺)。")]
    public float labelHeight = 0.8f;
    [Tooltip("文字大小。1 大約是 10 公分高。")]
    public float labelFontSize = 1f;

    // ---- 給 IntegrationCheck 看 ----
    public State CurrentState { get; private set; } = State.Hidden;
    public BitePhase CurrentBitePhase { get; private set; } = BitePhase.Waiting;
    public int BlockedCount => blockedCount;

    // ---- 給 DefenseNet 看 ----
    // 現在被咬的話擋不擋得下
    public bool WouldBlock => heronEnabled && ReadExpand() >= expandThreshold;
    // 目前的防禦還能維持幾秒
    public float DefenseSecondsLeft => (expansion != null && !DebugInput.Active)
        ? expansion.SecondsAboveThreshold(expandThreshold) : float.PositiveInfinity;

    // 還沒校準完就不出發;鍵盤測試模式不需要校準
    bool CalibrationPending => expansion != null && expansion.isActiveAndEnabled
        && !DebugInput.Active && !ExpansionCalibration.IsDone;
    public float DefenseProgress01 => bitesToGiveUp > 0 ? Mathf.Clamp01(blockedCount / (float)bitesToGiveUp) : 0f;

    // ---- 給 HeronNeckAim 看(唯讀,不影響原本行為) ----
    // 撞開(Stagger)時間裡,前面這一段比例是往前撲,後面是被彈回
    public const float StaggerLungePart = 0.3f;
    // 被抓後原地咬住待命中(直到 ResetRound / BeginApproach)
    public bool IsCaught => waitingAfterCatch;
    // 這一口撲到底時身體往前傾的角度(度)
    public float BiteLeanDeg => biteLeanDeg;
    // 沒有傾斜時的旋轉 = 面向 × 模型修正(給 HeronHipLean 用)
    public Quaternion UprightRotation => Quaternion.Euler(0f, yaw, 0f) * Quaternion.Euler(0f, modelYawOffsetDeg, 0f);
    // 預告進度 0~1(不在預告時是 0)
    public float WindupProgress01 => (CurrentState == State.Attack && CurrentBitePhase == BitePhase.Windup)
        ? (windupSeconds > 0f ? Mathf.Clamp01((Time.time - windupStart) / windupSeconds) : 1f) : 0f;
    // 撞開進度 0~1(不在撞開時是 0)
    public float StaggerProgress01 =>
        (CurrentState == State.Attack && CurrentBitePhase == BitePhase.Stagger)
            ? (staggerSeconds > 0f ? Mathf.Clamp01((Time.time - staggerStart) / staggerSeconds) : 1f)
        : (CurrentState == State.Attack && CurrentBitePhase == BitePhase.Strike)
            ? (strikeSeconds > 0f ? Mathf.Clamp01((Time.time - staggerStart) / strikeSeconds) : 1f)
        : 0f;
    // 停下來咬的位置
    public Vector3 AttackPosition => attackPos;
    // 給定「停下位置到玩家頭的水平距離」,這一口會往前撲幾公尺(ResolveBite 用同一個公式)
    public float PredictLunge(float flatDistToHead)
    {
        return Mathf.Clamp(Mathf.Min(lungeDistance, flatDistToHead - lungeMinHeadDistance), 0f, lungeDistance);
    }

    Vector3 home;
    Quaternion homeRotation;
    float yaw;                  // 天敵面向(不含模型修正與後仰)
    Vector3 attackPos;          // 停下來咬的位置
    float phaseEnd;             // 目前這段(待命/等待/預告/撞開)結束的時間
    float hiddenUntil = float.PositiveInfinity;
    int blockedCount;
    float judgeMax;             // 預告最後一小段時間內的膨脹最大值
    float lungeActual;          // 這一口實際往前撲的距離
    float biteLeanDeg;          // 這一口撲到底時身體往前傾的角度
    HeronNeckAim neckAim;       // 用來算嘴要碰到頭需要多少前傾(可以沒有)
    float windupStart;
    float staggerStart;
    bool calibrationHeld;       // 曾因校準而暫停出發,校準完要重新計時
    bool expandLocked;          // 擋下後,等玩家先收手才恢復讀膨脹
    bool waitingAfterCatch;     // 被抓後原地待命,不自己計時出發
    float caughtStart;          // 被抓的時間(撲出去的動作從這裡開始)
    float hapticStopAt = -1f;
    bool lastEnabled;
    bool warnedNoExpansion;

    Renderer[] renderers;
    bool[] rendererWasEnabled;
    TextMeshPro label;

    void Awake()
    {
        home = transform.position;
        homeRotation = transform.rotation;
        neckAim = GetComponent<HeronNeckAim>();
        if (expansion != null) expansion.useDecay = enableExpansionDecay;
        if (showDefenseNet && GetComponent<DefenseNet>() == null) gameObject.AddComponent<DefenseNet>();
        yaw = transform.eulerAngles.y - modelYawOffsetDeg;

        renderers = GetComponentsInChildren<Renderer>(true);
        rendererWasEnabled = new bool[renderers.Length];
        for (int i = 0; i < renderers.Length; i++) rendererWasEnabled[i] = renderers[i].enabled;

        var cols = GetComponentsInChildren<Collider>(true);
        if (cols.Length > 0)
            Debug.LogWarning($"[HeronThreat] HeronRoot 底下有 {cols.Length} 個碰撞體(例如 {cols[0].name}),可能擋到舌頭或爬行。請移除,或放到 Ignore Raycast 層。", this);
    }

    void Start()
    {
        if (player == null && Camera.main != null)
        {
            player = Camera.main.transform;
            Debug.LogWarning("[HeronThreat] 沒有指定 Player,改用 Main Camera:" + player.name, this);
        }
        if (stages == null) stages = FindAnyObjectByType<StageManager>();
        if (requireZone && !ZoneActive)
            Debug.LogWarning("[HeronThreat] 勾了 Require Zone,但 StageManager 沒有天敵區域或廢棄實驗室邊界(要跑 Lizard > Setup Game),所以不限制區域。", this);
        lastEnabled = heronEnabled;
        EnterHidden(true);
    }

    void OnDisable()
    {
        StopHaptics();
        if (label != null) label.gameObject.SetActive(false);
    }

    void OnDestroy()
    {
        if (label != null) Destroy(label.gameObject);
    }

    void Update()
    {
        if (hapticStopAt > 0f && Time.time >= hapticStopAt) StopHaptics();

        if (heronEnabled != lastEnabled)
        {
            lastEnabled = heronEnabled;
            if (heronEnabled) ResetRound();
            else { SetVisible(false); StopHaptics(); }
        }

        if (heronEnabled)
        {
            // 擋下一口後,玩家的真實膨脹要先掉到 rearmBelow 以下一次才解鎖
            if (expandLocked && RawExpand() < rearmBelow) expandLocked = false;

            switch (CurrentState)
            {
                case State.Hidden:   TickHidden(); break;
                case State.Approach: TickApproach(); break;
                case State.Attack:   TickAttack(); break;
                case State.Retreat:  TickRetreat(); break;
            }
            SnapToGround();
        }

        UpdateLabel();
    }

    // ================= 貼地 =================
    // 各狀態只管水平位置;這裡每幀往下量地面高度,把腳底(HeronRoot 原點)貼上去。
    // 在 Update 最後做,HeronHipLean / HeronNeckAim 的 LateUpdate 才會用到貼地後的位置。

    static readonly RaycastHit[] groundHits = new RaycastHit[16];
    float groundY;
    bool hasGroundY;

    void SnapToGround()
    {
        if (!snapToGround || groundMask.value == 0) return;

        Vector3 p = transform.position;
        float reference = hasGroundY ? groundY : p.y;
        Vector3 origin = new Vector3(p.x, reference + groundCastHeight, p.z);
        int count = Physics.RaycastNonAlloc(origin, Vector3.down, groundHits, groundCastHeight + groundCastDepth,
                                            groundMask, QueryTriggerInteraction.Ignore);
        float best = float.MaxValue;
        float targetY = 0f;
        for (int i = 0; i < count; i++)
        {
            var c = groundHits[i].collider;
            if (c.transform.IsChildOf(transform)) continue;
            if (player != null && c.transform.IsChildOf(player.root)) continue;
            if (groundHits[i].distance < best) { best = groundHits[i].distance; targetY = groundHits[i].point.y; }
        }
        if (best == float.MaxValue) return;   // 下面沒有地面:維持原本高度

        targetY += groundOffset;
        if (!hasGroundY) { groundY = targetY; hasGroundY = true; }
        else groundY = Mathf.Lerp(groundY, targetY, 1f - Mathf.Exp(-groundFollowSpeed * Time.deltaTime));

        p.y = groundY;
        transform.position = p;
    }

    // ================= 公開操作 =================

    [ContextMenu("BeginApproach(開始接近)")]
    public void BeginApproach()
    {
        if (!Application.isPlaying) { Debug.LogWarning("[HeronThreat] 請在 Play 時使用。", this); return; }
        if (!heronEnabled) return;
        if (player == null) { Debug.LogWarning("[HeronThreat] 沒有 Player,無法接近。", this); return; }

        ClearRoundValues();
        SetVisible(true);
        CurrentState = State.Approach;
        LogPhase("出發,走向玩家");
    }

    [ContextMenu("ResetRound(回起點待命)")]
    public void ResetRound()
    {
        if (!Application.isPlaying) { Debug.LogWarning("[HeronThreat] 請在 Play 時使用。", this); return; }
        StopHaptics();
        transform.SetPositionAndRotation(home, homeRotation);
        yaw = homeRotation.eulerAngles.y - modelYawOffsetDeg;
        EnterHidden(true);
    }

    public void SetHome(Vector3 pos)
    {
        home = pos;
        transform.position = pos;
    }

    // ================= 各狀態 =================

    void EnterHidden(bool startTimer)
    {
        CurrentState = State.Hidden;
        ClearRoundValues();
        hiddenUntil = (startTimer && useTimer)
            ? Time.time + Random.Range(hiddenSeconds.x, hiddenSeconds.y)
            : float.PositiveInfinity;
        SetVisible(heronEnabled && !hideWhenIdle);
    }

    void TickHidden()
    {
        // 中途才勾 useTimer 也能開始計時(被抓後的待命除外)
        if (useTimer && float.IsPositiveInfinity(hiddenUntil) && !waitingAfterCatch)
            hiddenUntil = Time.time + Random.Range(hiddenSeconds.x, hiddenSeconds.y);

        SetVisible(!hideWhenIdle);

        // 被抓:從預告的後仰姿勢往前撲出去,撲到底就停住(不彈回)
        if (waitingAfterCatch)
        {
            float t = caughtLungeSeconds > 0f ? Mathf.Clamp01((Time.time - caughtStart) / caughtLungeSeconds) : 1f;
            float offset = Mathf.Lerp(-windupBackDistance, lungeActual, t * t);
            float tilt = Mathf.Lerp(windupTiltDeg, -biteLeanDeg, t);   // 撲到底時往前傾
            transform.position = attackPos + Forward() * offset;
            ApplyRotation(tilt);
            return;
        }

        // 校準完成前不出發,校準完才開始計時
        if (CalibrationPending) { calibrationHeld = true; return; }
        if (calibrationHeld)
        {
            calibrationHeld = false;
            if (useTimer) hiddenUntil = Time.time + Random.Range(hiddenSeconds.x, hiddenSeconds.y);
        }

        // 玩家不在區域裡就不出發;一進區域就重新計時(短的 Enter Delay)
        if (ZoneActive)
        {
            bool inside = PlayerInZone;
            if (!inside) { playerWasInZone = false; return; }
            if (!playerWasInZone)
            {
                playerWasInZone = true;
                LogPhase($"玩家進入{ZoneName}");
                if (useTimer) hiddenUntil = Time.time + Random.Range(enterDelaySeconds.x, enterDelaySeconds.y);
            }
        }

        if (useTimer && Time.time >= hiddenUntil) BeginApproach();
    }

    // ---- 觸發區域 ----
    ForestBoundary Zone
    {
        get
        {
            if (stages == null) return null;
            if (stages.predatorBoundary != null && stages.predatorBoundary.polygon != null && stages.predatorBoundary.polygon.Length >= 3)
                return stages.predatorBoundary;
            return stages.labBoundary;
        }
    }
    string ZoneName => Zone != null && stages != null && Zone == stages.predatorBoundary ? "天敵區域" : "廢棄實驗室";
    bool ZoneActive => requireZone && Zone != null && Zone.polygon != null && Zone.polygon.Length >= 3;
    bool PlayerInZone => player != null && Zone.Contains(player.position);
    bool playerWasInZone;

    // 走過來或咬的途中玩家離開區域:走回起點,不算放棄
    bool CheckPlayerLeft()
    {
        if (!retreatWhenPlayerLeaves || !ZoneActive || PlayerInZone) return false;
        playerWasInZone = false;
        LogRoundEnd($"玩家離開{ZoneName},天敵回去");
        if (CurrentState == State.Attack) transform.position = attackPos;
        CurrentState = State.Retreat;
        return true;
    }

    void TickApproach()
    {
        if (player == null) return;
        if (CheckPlayerLeft()) return;

        Vector3 pos = transform.position;
        Vector3 toPlayer = Flat(player.position - pos);
        float dist = toPlayer.magnitude;

        if (dist > 0.001f) TurnToward(toPlayer);

        // 夠近而且中間沒有牆才咬;隔著牆就在牆邊等(繼續往玩家方向貼著牆滑)
        bool clear = ClearLineToPlayer();
        if (dist <= attackDistance && clear)
        {
            EnterAttack();
            return;
        }

        float step = Mathf.Min(walkSpeed * Time.deltaTime, Mathf.Max(0f, dist - attackDistance));
        Vector3 next = step > 0f ? MoveWithWalls(pos, toPlayer / dist * step) : pos;
        bool blocked = step > 0.00001f && Flat(next - pos).magnitude < step * 0.2f;
        if (blocked != wallBlocked)
        {
            wallBlocked = blocked;
            if (blocked) LogPhase("被牆擋住,在牆邊等");
        }
        transform.position = next;
        ApplyRotation(0f);

        if (Flat(player.position - next).magnitude <= attackDistance && ClearLineToPlayer()) EnterAttack();
    }

    void EnterAttack()
    {
        CurrentState = State.Attack;
        attackPos = transform.position;
        stepPending = false;
        CurrentBitePhase = BitePhase.Waiting;
        phaseEnd = Time.time + firstBiteDelay;
        LogPhase("停下,準備咬");
    }

    void TickAttack()
    {
        if (player == null) return;
        if (CheckPlayerLeft()) return;

        Vector3 toPlayer = Flat(player.position - attackPos);
        if (toPlayer.sqrMagnitude > 0.000001f) TurnToward(toPlayer);

        float offset = 0f;   // 沿面向方向的位移;負 = 往後
        float tilt = 0f;     // 後仰角度

        switch (CurrentBitePhase)
        {
            case BitePhase.Waiting:
            {
                // 玩家走遠了:重新走過去,不要在原地對空氣(地板)一直咬
                if (toPlayer.magnitude > attackDistance + reengageMargin)
                {
                    LogPhase($"玩家離開攻擊距離({toPlayer.magnitude:F2}m),重新走過去");
                    CurrentState = State.Approach;
                    return;
                }
                // 玩家躲到牆後面:不能隔牆咬,回去接近(會在牆邊等)
                if (!ClearLineToPlayer())
                {
                    LogPhase("玩家躲到牆後面,不咬");
                    CurrentState = State.Approach;
                    return;
                }

                // 上一口咬空:走近一點再咬(一樣會被牆擋)
                if (stepPending)
                {
                    Vector3 want = Vector3.MoveTowards(attackPos, stepTarget, walkSpeed * Time.deltaTime) - attackPos;
                    Vector3 moved = MoveWithWalls(attackPos, want);
                    bool stuck = Flat(moved - attackPos).magnitude < want.magnitude * 0.2f;
                    attackPos = moved;
                    if (stuck || Flat(attackPos - stepTarget).sqrMagnitude < 0.000001f) stepPending = false;
                }

                if (Time.time >= phaseEnd && !stepPending) StartWindup();
                break;
            }

            case BitePhase.Windup:
            {
                float t = windupSeconds > 0f ? Mathf.Clamp01((Time.time - windupStart) / windupSeconds) : 1f;
                float s = Mathf.SmoothStep(0f, 1f, t);
                offset = -windupBackDistance * s;
                tilt = windupTiltDeg * s;

                // 預告結束前 biteGraceSeconds 內的最大膨脹值(含結束那一幀)
                if (Time.time >= phaseEnd - biteGraceSeconds)
                    judgeMax = Mathf.Max(judgeMax, ReadExpand());

                if (Time.time >= phaseEnd)
                {
                    ResolveBite(toPlayer.magnitude);
                    if (CurrentState != State.Attack) return;   // 被抓了,已經停下
                    offset = -windupBackDistance;               // 撞開動畫從預告的位置開始
                    tilt = windupTiltDeg;
                }
                break;
            }

            case BitePhase.Stagger:
            {
                float t = staggerSeconds > 0f ? Mathf.Clamp01((Time.time - staggerStart) / staggerSeconds) : 1f;
                // 前 30%:從後退的位置撲到最前面;後 70%:被彈回原位
                const float LungePart = StaggerLungePart;
                if (t < LungePart)
                {
                    float u = t / LungePart;
                    offset = Mathf.Lerp(-windupBackDistance, lungeActual, u * u);
                    tilt = Mathf.Lerp(windupTiltDeg, -biteLeanDeg, u);   // 從後仰撲到前傾
                }
                else
                {
                    float u = Mathf.SmoothStep(0f, 1f, (t - LungePart) / (1f - LungePart));
                    offset = Mathf.Lerp(lungeActual, 0f, u);
                    tilt = Mathf.Lerp(-biteLeanDeg, 0f, u);              // 被彈回時慢慢站直
                }

                if (Time.time >= phaseEnd)
                {
                    offset = 0f; tilt = 0f;
                    if (blockedCount >= bitesToGiveUp)
                    {
                        GiveUp();
                        return;
                    }
                    CurrentBitePhase = BitePhase.Waiting;
                    phaseEnd = Time.time + biteInterval;
                }
                break;
            }

            case BitePhase.Strike:
            {
                // 動作和撞開一樣:前 30% 撲出去,後面縮回來;差別是這裡要看嘴有沒有真的碰到
                float t = strikeSeconds > 0f ? Mathf.Clamp01((Time.time - staggerStart) / strikeSeconds) : 1f;
                const float LungePart = StaggerLungePart;
                if (t < LungePart)
                {
                    float u = t / LungePart;
                    offset = Mathf.Lerp(-windupBackDistance, lungeActual, u * u);
                    tilt = Mathf.Lerp(windupTiltDeg, -biteLeanDeg, u);
                }
                else
                {
                    float u = Mathf.SmoothStep(0f, 1f, (t - LungePart) / (1f - LungePart));
                    offset = Mathf.Lerp(lungeActual, 0f, u);
                    tilt = Mathf.Lerp(-biteLeanDeg, 0f, u);
                }

                // 有沒有咬到由 HeronNeckAim 在 LateUpdate 擺好姿勢後呼叫 CheckBeakContact 判定
                if (Time.time >= phaseEnd)
                {
                    MissCount++;
                    Debug.Log($"[Heron] t={Time.time:F1}s 咬空:嘴尖最近離玩家頭 {strikeClosest:F3}m,要 ≤ {HitRadius:F3}m 才算咬到"
                            + $"(換算玩家真實大小 {strikeClosest / RigScaleNow:F2}m / {biteHitRadius:F2}m)", this);
                    offset = 0f; tilt = 0f;
                    CurrentBitePhase = BitePhase.Waiting;
                    phaseEnd = Time.time + biteInterval;
                    PlanStepAfterMiss();
                }
                break;
            }
        }

        transform.position = attackPos + Forward() * offset;
        ApplyRotation(tilt);
    }

    void UpdateBiteLean()
    {
        float fixedLean = windupTiltDeg * 0.5f;
        biteLeanDeg = autoBiteLean && neckAim != null && neckAim.isActiveAndEnabled
            ? neckAim.RequiredBiteLean(maxBiteLeanDeg, fixedLean)
            : fixedLean;
    }

    void StartWindup()
    {
        CurrentBitePhase = BitePhase.Windup;
        windupStart = Time.time;
        phaseEnd = Time.time + windupSeconds;
        judgeMax = 0f;
        PlayClip(windupClip);
        LogPhase($"第 {blockedCount + 1} 口 預告開始");
        UpdateBiteLean();
        OnBiteWindup?.Invoke();
    }

    void ResolveBite(float distToHead)
    {
        // 擋下或被抓都會往前撲,但不得撲進玩家頭部 lungeMinHeadDistance 以內
        lungeActual = PredictLunge(distToHead);
        UpdateBiteLean();   // 玩家可能在預告時移動,用最新的頭位置再算一次
        bool blocked = judgeMax >= expandThreshold;
        Debug.Log($"[Heron] t={Time.time:F1}s 第 {blockedCount + 1} 口判定:膨脹最大={judgeMax:F2} 需要={expandThreshold:F2}"
                + $"(現在實際={RawExpand():F2}{(expandLocked ? ",尚未收手所以不算" : "")}) → {(blocked ? "擋下" : (ContactCheckReady ? "沒擋下,撲咬" : "被抓"))}", this);

        if (blocked)
        {
            blockedCount++;
            bool realDecay = expansion != null && expansion.useDecay && !DebugInput.Active;
            expandLocked = !realDecay;
            if (realDecay) expansion.ConsumeCharge(blockDeflateSeconds);
            CurrentBitePhase = BitePhase.Stagger;
            staggerStart = Time.time;
            phaseEnd = Time.time + staggerSeconds;
            PlayBlockedFeedback();
            OnBiteBlocked?.Invoke();
        }
        else if (ContactCheckReady)
        {
            // 沒擋下:撲出去咬,嘴尖碰到才算被抓
            CurrentBitePhase = BitePhase.Strike;
            staggerStart = Time.time;
            phaseEnd = Time.time + strikeSeconds;
            strikeClosest = float.PositiveInfinity;
        }
        else
        {
            if (requireBeakContact && !warnedNoContactCheck)
            {
                warnedNoContactCheck = true;
                Debug.LogWarning("[HeronThreat] 勾了 Require Beak Contact,但沒有可用的 HeronNeckAim(找不到嘴尖),改用舊行為:沒擋下就算被抓。", this);
            }
            Caught(holdAtFullLunge: false);
        }
    }

    // ---- 咬中判定 ----
    public int MissCount { get; private set; }
    float strikeClosest;
    bool warnedNoContactCheck;
    bool ContactCheckReady => requireBeakContact && neckAim != null && neckAim.isActiveAndEnabled && neckAim.HasBeakTip;
    float RigScaleNow => expansion != null ? Mathf.Max(0.0001f, expansion.RigScale) : 1f;
    float HitRadius => biteHitRadius * RigScaleNow;

    bool stepPending;
    Vector3 stepTarget;

    // ================= 牆壁 =================
    // 鳥用 Transform 直接移動,沒有 CharacterController;這裡用膠囊往前掃,撞到陡的面(牆、峭壁)就停下並沿著牆滑。
    // 法線朝上的面(地面、緩坡)不擋,交給貼地處理。

    static readonly RaycastHit[] wallHits = new RaycastHit[32];
    bool wallBlocked;
    float retreatStuckTime;
    const float WallSkin = 0.005f;

    Vector3 ChestPoint => transform.position + Vector3.up * (bodyHeight * 0.8f);

    bool IgnoredCollider(Collider c)
    {
        if (c.transform.IsChildOf(transform)) return true;
        if (player != null && c.transform.IsChildOf(player.root)) return true;
        return false;
    }

    bool IsWall(RaycastHit h) => h.normal.y <= walkableNormalY;

    // 從 feet 往水平方向 delta 移動,被牆擋住就停在牆前,剩下的距離沿牆滑(最多滑一次)
    Vector3 MoveWithWalls(Vector3 feet, Vector3 delta)
    {
        delta.y = 0f;
        if (!blockByWalls || delta.sqrMagnitude < 1e-12f) return feet + delta;

        Vector3 pos = feet;
        for (int iter = 0; iter < 2; iter++)
        {
            float len = delta.magnitude;
            if (len < 1e-6f) break;
            Vector3 dir = delta / len;
            if (!CastWall(pos, dir, len + WallSkin, out RaycastHit hit)) { pos += delta; break; }

            float allowed = Mathf.Max(0f, hit.distance - WallSkin);
            pos += dir * allowed;
            Vector3 n = hit.normal; n.y = 0f;
            if (n.sqrMagnitude < 1e-6f) break;
            n.Normalize();
            Vector3 remaining = dir * (len - allowed);
            delta = remaining - Vector3.Dot(remaining, n) * n;   // 沿牆滑
        }
        return pos;
    }

    bool CastWall(Vector3 feet, Vector3 dir, float dist, out RaycastHit best)
    {
        float bottom = stepHeight + bodyRadius;
        float top = Mathf.Max(bottom, bodyHeight - bodyRadius);
        int n = Physics.CapsuleCastNonAlloc(feet + Vector3.up * bottom, feet + Vector3.up * top, bodyRadius, dir,
                                            wallHits, dist, wallMask, QueryTriggerInteraction.Ignore);
        best = default;
        float bd = float.MaxValue;
        for (int i = 0; i < n; i++)
        {
            var h = wallHits[i];
            if (h.distance <= 0f) continue;          // 一開始就重疊的不算,讓鳥可以離開
            if (IgnoredCollider(h.collider)) continue;
            if (!IsWall(h)) continue;
            if (h.distance < bd) { bd = h.distance; best = h; }
        }
        return bd < float.MaxValue;
    }

    // from → to 之間有沒有牆(法線朝上的地面不算)
    bool ClearLine(Vector3 from, Vector3 to)
    {
        if (!blockByWalls) return true;
        Vector3 d = to - from;
        float len = d.magnitude;
        if (len < 1e-5f) return true;
        int n = Physics.RaycastNonAlloc(from, d / len, wallHits, len, wallMask, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < n; i++)
        {
            if (IgnoredCollider(wallHits[i].collider)) continue;
            if (IsWall(wallHits[i])) return false;
        }
        return true;
    }

    bool ClearLineToPlayer() => player == null || ClearLine(ChestPoint, player.position);

    // 咬空後往玩家靠近「差的距離」(上限 missStepMax),但不靠到比 lungeMinHeadDistance 更近
    void PlanStepAfterMiss()
    {
        stepPending = false;
        if (!stepCloserAfterMiss || player == null || float.IsInfinity(strikeClosest)) return;

        Vector3 toPlayer = Flat(player.position - attackPos);
        float dist = toPlayer.magnitude;
        if (dist < 0.0001f) return;

        float shortfall = strikeClosest - HitRadius * 0.5f;   // 多走一點,讓嘴尖落在判定範圍中間
        float step = Mathf.Min(shortfall, missStepMax * RigScaleNow, dist - lungeMinHeadDistance);
        if (step <= 0.001f)
        {
            LogPhase("咬空,但已經靠到最近了,不再往前");
            return;
        }
        stepTarget = attackPos + toPlayer / dist * step;
        stepPending = true;
        LogPhase($"咬空,往前走 {step:F3}m 再咬");
    }

    // 由 HeronNeckAim 每幀 LateUpdate 最後呼叫,beakTip = 畫面上真正看到的嘴尖位置
    // (已含 HeronHipLean 的前傾;在 Update 裡量會量到還原成直立的姿勢)
    public void CheckBeakContact(Vector3 beakTip)
    {
        if (!heronEnabled || player == null) return;
        if (CurrentState != State.Attack || CurrentBitePhase != BitePhase.Strike) return;
        if (StaggerProgress01 > strikeContactPart) return;

        // 嘴穿過牆碰到的不算(鳥在牆外、玩家在牆內)
        if (!ClearLine(ChestPoint, beakTip)) return;

        float d = Vector3.Distance(beakTip, player.position);
        strikeClosest = Mathf.Min(strikeClosest, d);
        if (d <= HitRadius)
        {
            Debug.Log($"[Heron] t={Time.time:F1}s 嘴尖碰到玩家(距離 {d:F3}m ≤ {HitRadius:F3}m)→ 被抓", this);
            Caught(holdAtFullLunge: true);
        }
    }

    // holdAtFullLunge:已經撲到底才咬到(撲咬中),被抓的姿勢直接停在撲到底,不再從後仰重撲一次
    void Caught(bool holdAtFullLunge)
    {
        CaughtCount++;
        LogRoundEnd("被抓");
        PlayCaughtFeedback();
        OnPlayerCaught?.Invoke();

        // 撲出去後停在原地待命,不自己再出發;等 ResetRound() 或 BeginApproach()
        float lunge = lungeActual;      // EnterHidden 會清掉,先存起來
        EnterHidden(false);
        lungeActual = lunge;
        waitingAfterCatch = true;
        caughtStart = holdAtFullLunge ? Time.time - caughtLungeSeconds : Time.time;
    }

    void GiveUp()
    {
        GaveUpCount++;
        LogRoundEnd("天敵放棄");
        OnHeronGaveUp?.Invoke();
        transform.position = attackPos;
        CurrentState = State.Retreat;
    }

    void TickRetreat()
    {
        Vector3 pos = transform.position;
        Vector3 toHome = Flat(home - pos);
        float dist = toHome.magnitude;
        float step = walkSpeed * Time.deltaTime;

        // 還沒到:邊轉身邊走回去
        if (dist > 0.001f)
        {
            TurnToward(toHome);
            Vector3 next = step >= dist ? new Vector3(home.x, pos.y, home.z) : pos + toHome / dist * step;
            Vector3 moved = MoveWithWalls(pos, next - pos);
            // 被牆卡住回不去太久:直接回到起點(避免永遠卡在牆邊)
            if (Flat(moved - pos).magnitude < Mathf.Min(step, dist) * 0.2f)
            {
                retreatStuckTime += Time.deltaTime;
                if (retreatStuckTime > 3f)
                {
                    LogPhase("回起點的路被牆擋住,直接回到起點");
                    moved = home;
                    retreatStuckTime = 0f;
                }
            }
            else retreatStuckTime = 0f;
            transform.position = moved;
            ApplyRotation(0f);
            return;
        }

        // 到了:轉回起點原本的方向,轉好才開始待命
        transform.position = home;
        float homeYaw = homeRotation.eulerAngles.y - modelYawOffsetDeg;
        yaw = Mathf.MoveTowardsAngle(yaw, homeYaw, turnSpeedDeg * Time.deltaTime);
        ApplyRotation(0f);
        if (Mathf.Abs(Mathf.DeltaAngle(yaw, homeYaw)) < 0.5f)
        {
            transform.rotation = homeRotation;
            EnterHidden(true);
            LogPhase("回到起點,待命");
        }
    }

    // ================= 膨脹輸入 =================

    // 判定用:擋下一口後到玩家先收手之前,以及被撞開期間,一律回傳 0
    private float ReadExpand()
    {
        if (expandLocked) return 0f;
        if (CurrentState == State.Attack && CurrentBitePhase == BitePhase.Stagger) return 0f;
        return RawExpand();
    }

    // 玩家的真實膨脹程度 0~1
    float RawExpand()
    {
        if (DebugInput.Active) return DebugInput.Expand01;

        if (expansion == null)
        {
            if (!warnedNoExpansion) { warnedNoExpansion = true; Debug.LogWarning("[HeronThreat] 沒有指定 Expansion,膨脹一律當 0。", this); }
            return 0f;
        }
        if (!expansion.isActiveAndEnabled && !warnedNoExpansion)
        {
            warnedNoExpansion = true;
            Debug.LogWarning("[HeronThreat] Systems/Expansion 是停用的,scale 不會變,膨脹一律是 0。", this);
        }

        // scale 範圍 1 ~ maxScale,換成 0 ~ 1
        return Mathf.Clamp01(Mathf.InverseLerp(1f, expansion.maxScale, expansion.scale));
    }

    // ================= 回饋 =================

    void PlayBlockedFeedback()
    {
        if (hapticOnBlocked)
        {
            OVRInput.SetControllerVibration(1f, hapticAmplitude, OVRInput.Controller.LTouch);
            OVRInput.SetControllerVibration(1f, hapticAmplitude, OVRInput.Controller.RTouch);
            hapticStopAt = Time.time + hapticSeconds;
        }
        PlayClip(blockedClip);
    }

    void PlayCaughtFeedback()
    {
        if (!hapticOnCaught) return;
        OVRInput.SetControllerVibration(1f, caughtHapticAmplitude, OVRInput.Controller.LTouch);
        OVRInput.SetControllerVibration(1f, caughtHapticAmplitude, OVRInput.Controller.RTouch);
        hapticStopAt = Time.time + caughtHapticSeconds;
    }

    void PlayClip(AudioClip clip)
    {
        if (clip != null && audioSource != null) audioSource.PlayOneShot(clip);
    }

    void StopHaptics()
    {
        if (hapticStopAt < 0f) return;
        hapticStopAt = -1f;
        OVRInput.SetControllerVibration(0f, 0f, OVRInput.Controller.LTouch);
        OVRInput.SetControllerVibration(0f, 0f, OVRInput.Controller.RTouch);
    }

    // ================= 小工具 =================

    void ClearRoundValues()
    {
        blockedCount = 0;
        judgeMax = 0f;
        lungeActual = 0f;
        expandLocked = false;
        waitingAfterCatch = false;
        CurrentBitePhase = BitePhase.Waiting;
    }

    void LogRoundEnd(string result)
    {
        Debug.Log($"[Heron] t={Time.time:F1}s 結果={result} 擋下={blockedCount}/{bitesToGiveUp}", this);
    }

    void LogPhase(string what)
    {
        Debug.Log($"[Heron] t={Time.time:F1}s {what}", this);
    }

    // ================= 除錯文字 =================
    // 字型只有英文(LiberationSans),所以文字用英文;中文會變成方框。
    // 文字是獨立物件,不放在 HeronRoot 底下,才不會跟著縮放 0.01 和後仰。

    void UpdateLabel()
    {
        bool show = showDebugLabel && heronEnabled && isActiveAndEnabled;
        if (!show)
        {
            if (label != null && label.gameObject.activeSelf) label.gameObject.SetActive(false);
            return;
        }

        if (label == null)
        {
            var go = new GameObject("HeronDebugLabel");
            go.layer = 2;   // Ignore Raycast
            label = go.AddComponent<TextMeshPro>();
            label.alignment = TextAlignmentOptions.Center;
            label.rectTransform.sizeDelta = new Vector2(4f, 1f);
            label.richText = true;
        }
        if (!label.gameObject.activeSelf) label.gameObject.SetActive(true);

        label.fontSize = labelFontSize;
        Vector3 p = transform.position;
        label.transform.position = new Vector3(p.x, p.y + labelHeight, p.z);
        if (player != null)
        {
            Vector3 away = label.transform.position - player.position;
            if (away.sqrMagnitude > 0.0001f) label.transform.rotation = Quaternion.LookRotation(away, Vector3.up);
        }

        label.text = LabelText();
    }

    string LabelText()
    {
        float now = Time.time;
        string line1;
        switch (CurrentState)
        {
            case State.Hidden:
                if (waitingAfterCatch)
                    line1 = "<color=#FF4040>CAUGHT!</color>\n<size=60%>right-click ResetRound to retry</size>";
                else if (ZoneActive && !PlayerInZone)
                    line1 = "IDLE  (waiting player in zone)";
                else if (useTimer && !float.IsPositiveInfinity(hiddenUntil))
                    line1 = $"IDLE  go in {Mathf.Max(0f, hiddenUntil - now):F1}s";
                else
                    line1 = "IDLE  (waiting BeginApproach)";
                return line1;

            case State.Approach:
            {
                float d = player != null ? Flat(player.position - transform.position).magnitude : 0f;
                line1 = $"APPROACH  {d:F1}m";
                break;
            }

            case State.Attack:
                switch (CurrentBitePhase)
                {
                    case BitePhase.Windup:
                        line1 = $"<color=#FFE040>WINDUP! EXPAND NOW  {Mathf.Max(0f, phaseEnd - now):F1}s</color>";
                        break;
                    case BitePhase.Stagger:
                        line1 = $"<color=#40FF60>BLOCKED! {blockedCount}/{bitesToGiveUp}</color>";
                        break;
                    case BitePhase.Strike:
                        line1 = "<color=#FF8040>STRIKE!</color>";
                        break;
                    default:
                        line1 = $"WAIT  next bite in {Mathf.Max(0f, phaseEnd - now):F1}s";
                        break;
                }
                break;

            case State.Retreat:
                return $"<color=#80C0FF>GAVE UP  going home</color>\n<size=60%>blocked {blockedCount}/{bitesToGiveUp}</size>";

            default:
                line1 = CurrentState.ToString();
                break;
        }

        // 第二行:現在的膨脹值與門檻;第三行:擋下幾口、要不要先收手
        float raw = RawExpand();
        string expColor = raw >= expandThreshold ? "#40FF60" : "#FFFFFF";
        string line2 = $"<size=70%>expand <color={expColor}>{raw:F2}</color> / need {expandThreshold:F2}</size>";
        string line3 = expandLocked
            ? "<size=60%><color=#FFA040>close hands first</color></size>"
            : $"<size=60%>blocked {blockedCount}/{bitesToGiveUp}</size>";
        return line1 + "\n" + line2 + "\n" + line3;
    }

    void TurnToward(Vector3 flatDir)
    {
        float target = Mathf.Atan2(flatDir.x, flatDir.z) * Mathf.Rad2Deg;
        yaw = Mathf.MoveTowardsAngle(yaw, target, turnSpeedDeg * Time.deltaTime);
    }

    Vector3 Forward()
    {
        return Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;
    }

    // 只轉 Y 軸;tilt > 0 = 往後仰(只在預告/撞開時使用)
    void ApplyRotation(float tilt)
    {
        transform.rotation = Quaternion.Euler(0f, yaw, 0f)
                           * Quaternion.Euler(-tilt, 0f, 0f)
                           * Quaternion.Euler(0f, modelYawOffsetDeg, 0f);
    }

    void SetVisible(bool v)
    {
        if (renderers == null) return;
        for (int i = 0; i < renderers.Length; i++)
            if (renderers[i] != null) renderers[i].enabled = v && rendererWasEnabled[i];
    }

    static Vector3 Flat(Vector3 v) { return new Vector3(v.x, 0f, v.z); }
}
