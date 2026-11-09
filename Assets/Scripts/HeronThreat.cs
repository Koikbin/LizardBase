using TMPro;
using UnityEngine;

// 天敵(大笨鳥)回合,最小版。掛在 HeronRoot。
// Hidden(待命) → Approach(走向玩家) → Attack(一口一口咬) → 擋下夠多口就 Retreat(走回起點) → Hidden。
// 咬的時候膨脹不夠大 → 玩家被抓,天敵原地待命,等外部呼叫 ResetRound() 或 BeginApproach()。
// 不碰鏡頭、不移動玩家。天敵本身不應帶碰撞體。
public class HeronThreat : MonoBehaviour
{
    public enum State { Hidden, Approach, Attack, Retreat }
    public enum BitePhase { Waiting, Windup, Stagger }

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
    public float StaggerProgress01 => (CurrentState == State.Attack && CurrentBitePhase == BitePhase.Stagger)
        ? (staggerSeconds > 0f ? Mathf.Clamp01((Time.time - staggerStart) / staggerSeconds) : 1f) : 0f;
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
        }

        UpdateLabel();
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

        if (useTimer && Time.time >= hiddenUntil) BeginApproach();
    }

    void TickApproach()
    {
        if (player == null) return;

        Vector3 pos = transform.position;
        Vector3 toPlayer = Flat(player.position - pos);
        float dist = toPlayer.magnitude;

        if (dist > 0.001f) TurnToward(toPlayer);

        if (dist <= attackDistance)
        {
            EnterAttack();
            return;
        }

        float step = Mathf.Min(walkSpeed * Time.deltaTime, dist - attackDistance);
        transform.position = pos + toPlayer / dist * step;
        ApplyRotation(0f);

        if (dist - step <= attackDistance) EnterAttack();
    }

    void EnterAttack()
    {
        CurrentState = State.Attack;
        attackPos = transform.position;
        CurrentBitePhase = BitePhase.Waiting;
        phaseEnd = Time.time + firstBiteDelay;
        LogPhase("停下,準備咬");
    }

    void TickAttack()
    {
        if (player == null) return;

        Vector3 toPlayer = Flat(player.position - attackPos);
        if (toPlayer.sqrMagnitude > 0.000001f) TurnToward(toPlayer);

        float offset = 0f;   // 沿面向方向的位移;負 = 往後
        float tilt = 0f;     // 後仰角度

        switch (CurrentBitePhase)
        {
            case BitePhase.Waiting:
                if (Time.time >= phaseEnd) StartWindup();
                break;

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
                + $"(現在實際={RawExpand():F2}{(expandLocked ? ",尚未收手所以不算" : "")}) → {(blocked ? "擋下" : "被抓")}", this);

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
        else
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
            caughtStart = Time.time;
        }
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
            transform.position = step >= dist ? home : pos + toHome / dist * step;
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
