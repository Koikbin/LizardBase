using System;
using UnityEngine;

// 天敵脖子瞄準玩家頭部。掛在 HeronRoot(和 HeronThreat 同一個物件)。
// 脖子是蒙皮網格,只轉骨頭 Neck_1~Neck_4 的 localRotation,不移動、不改 HeronThreat 的行為。
//
// 瞄準方式(不猜軸向):
//   啟動時記錄「休息姿勢」下 Neck_1 → 嘴尖 的方向。
//   總彎曲量 = FromToRotation(休息方向, 目標方向),限制最大轉角、用 Slerp 平滑,
//   再把轉角依權重分給四根骨頭;每根只加自己那份,累積成整條弧線。
//   轉軸來自 FromToRotation,在執行時換到各骨頭的父座標系,不寫死骨頭的本地軸。
//
// 狀態對應:
//   Hidden / Retreat        → 休息姿勢
//   Approach / Attack 等待   → 慢慢轉向玩家(轉一部分,見 lookWeight)
//   Attack 預告(Windup)     → 往後上方收,幅度隨預告進度增加
//   Attack 撞開(Stagger)    → 前段快速朝頭伸出,後段快速縮回休息姿勢
//   被抓(IsCaught)          → 朝頭咬住,維持到 ResetRound
public class HeronNeckAim : MonoBehaviour
{
    const int BoneCount = 4;
    const int MaxSolveIterations = 4;
    const float ExpectedTipTolerance = 3f;   // HeronRoot 本地單位(HeronRoot 縮放 0.01,所以是公分)
    static readonly Vector3 ExpectedTipInRoot = new Vector3(66.45f, 275.82f, 0.27f);   // 腳縮到 70% 之後的位置

    [Header("參考")]
    [Tooltip("天敵主腳本。留空會抓同一個物件上的 HeronThreat。")]
    public HeronThreat heron;
    [Tooltip("有 SkinnedMeshRenderer 的脖子物件。留空會在 HeronRoot 底下用名字 Heron_Neck 找。")]
    public Transform neck;

    [Header("嘴尖(Neck_4 本地座標)")]
    [Tooltip("開始時用 BakeMesh 重新計算:脖子網格裡最靠 HeronRoot +X 的頂點,存成 Neck_4 的本地點。")]
    public Vector3 beakTipLocal;
    [Tooltip("勾起來表示 beakTipLocal 已經算好。BakeMesh 失敗時才會用這個存好的值,而且要通過位置檢查。")]
    public bool beakTipReady;

    [Header("可調")]
    [Tooltip("待命、接近、等待下一口時,脖子轉向玩家的速度(越大越快)。")]
    public float turnSpeed = 3f;
    [Tooltip("接近和等待時,脖子轉向玩家的比例。0 = 不轉,1 = 嘴尖直接對準頭。")]
    [Range(0f, 1f)] public float lookWeight = 0.5f;
    [Tooltip("預告時脖子往後上方收的角度(度),預告結束時到達這個角度。")]
    public float windupPullbackDeg = 35f;
    [Tooltip("脖子離開休息姿勢的最大總轉角(度)。")]
    public float maxAngleDeg = 120f;
    [Tooltip("伸出咬、縮回、預告後收時的速度(越大越快)。")]
    public float strikeSpeed = 20f;
    [Tooltip("總彎曲量分給 Neck_1~Neck_4 的比例,會自動換算成總和 1。")]
    public float[] boneWeights = { 0.15f, 0.30f, 0.30f, 0.25f };
    [Tooltip("每根骨頭每幀最多轉幾度,避免抖動。")]
    public float maxBoneDegPerFrame = 8f;
    [Tooltip("算咬擊前傾時,脖子最多分擔幾度。越小身體彎得越多、脖子根部越不容易露出來。")]
    public float biteNeckLimitDeg = 35f;

    [Header("除錯")]
    [Tooltip("每次預告開始時印一行距離資訊。")]
    public bool logOnWindup = true;
    [Tooltip("嘴尖離頭多近(公尺)以內算咬得到。只用在除錯訊息和提早結束迭代。")]
    public float reachTolerance = 0.05f;

    // ---- 啟動時記錄的休息姿勢(都是相對值,不受 HeronRoot 走路、轉身、後仰影響) ----
    readonly Transform[] bones = new Transform[BoneCount];
    SkinnedMeshRenderer skin;
    Transform chainParent;                                       // Neck_1 的父物件
    readonly Quaternion[] restLocalRot = new Quaternion[BoneCount];
    readonly Quaternion[] currentLocalRot = new Quaternion[BoneCount];
    readonly Quaternion[] parentRelChain = new Quaternion[BoneCount];   // 各骨頭父物件相對 chainParent 的旋轉
    readonly Vector3[] segments = new Vector3[BoneCount];        // 休息時各段向量(chainParent 方向、世界長度),最後一段到嘴尖
    readonly float[] normWeights = new float[BoneCount];
    Vector3 restTipVec;           // 休息時 Neck_1→嘴尖 向量(chainParent 方向、世界長度)
    Vector3 restDirParent;        // 休息時 Neck_1→嘴尖 方向,在 chainParent 的座標裡
    Quaternion parentRelRoot;     // chainParent 相對 HeronRoot 的旋轉
    Vector3 pivotInRoot;          // Neck_1 在 HeronRoot 本地座標的位置
    float neckLength;             // Neck_1→嘴尖 的長度(world 公尺)
    Quaternion smoothedDelta = Quaternion.identity;   // 平滑後的總彎曲量(chainParent 座標)
    bool ready;
    HeronHipLean hipLean;         // 只給除錯預測用(決定傾斜轉軸),不影響脖子實際動作
    float solveLimitDeg = -1f;    // 大於等於 0 時,解算暫時用這個上限(算咬擊前傾時讓身體優先)

    void Start()
    {
        if (heron == null) heron = GetComponent<HeronThreat>();
        if (heron == null)
        {
            Debug.LogError("[HeronNeckAim] 同一個物件上找不到 HeronThreat,脖子不會動。", this);
            enabled = false;
            return;
        }
        if (!FindRig(true)) { enabled = false; return; }
        if (!ResolveBeakTip()) { enabled = false; return; }

        if (boneWeights == null || boneWeights.Length != BoneCount)
        {
            Debug.LogWarning("[HeronNeckAim] boneWeights 必須有 4 個值,改用預設比例。", this);
            boneWeights = new[] { 0.15f, 0.30f, 0.30f, 0.25f };
        }
        UpdateWeights();

        // 記錄休息姿勢
        chainParent = bones[0].parent;
        Quaternion inverseParent = Quaternion.Inverse(chainParent.rotation);
        Vector3 tipW = bones[BoneCount - 1].TransformPoint(beakTipLocal);
        for (int i = 0; i < BoneCount; i++)
        {
            restLocalRot[i] = bones[i].localRotation;
            currentLocalRot[i] = restLocalRot[i];
            parentRelChain[i] = inverseParent * bones[i].parent.rotation;
            Vector3 next = i < BoneCount - 1 ? bones[i + 1].position : tipW;
            segments[i] = inverseParent * (next - bones[i].position);
        }
        restTipVec = inverseParent * (tipW - bones[0].position);
        neckLength = restTipVec.magnitude;
        restDirParent = neckLength > 0f ? restTipVec / neckLength : Vector3.up;
        parentRelRoot = Quaternion.Inverse(transform.rotation) * chainParent.rotation;
        pivotInRoot = transform.InverseTransformPoint(bones[0].position);
        smoothedDelta = Quaternion.identity;
        ready = neckLength > 0.0001f;
        if (!ready)
        {
            Debug.LogError("[HeronNeckAim] 嘴尖和 Neck_1 重疊,無法瞄準。請重新計算嘴尖位置。", this);
            enabled = false;
        }
    }

    void OnEnable()  { HeronThreat.OnBiteWindup += HandleWindup; }
    void OnDisable() { HeronThreat.OnBiteWindup -= HandleWindup; }

    // HeronThreat 在 Update 裡移動和轉 HeronRoot;脖子在 LateUpdate 才轉,用的是這一幀的身體姿勢
    void LateUpdate()
    {
        if (!ready) return;
        UpdateWeights();

        Transform head = heron.player;
        Quaternion parentRot = chainParent.rotation;
        Vector3 pivot = bones[0].position;
        Vector3 restDirW = parentRot * restDirParent;

        Vector3 targetDirW = restDirW;
        float speed = turnSpeed;
        bool strike = false;

        bool active = heron.heronEnabled && head != null;
        if (active)
        {
            Vector3 headDirW = (head.position - pivot).normalized;

            if (heron.IsCaught)
            {
                // 被抓:咬住頭,維持到 ResetRound
                targetDirW = headDirW;
                speed = strikeSpeed;
                strike = true;
            }
            else switch (heron.CurrentState)
            {
                case HeronThreat.State.Approach:
                    targetDirW = Vector3.Slerp(restDirW, headDirW, lookWeight);
                    break;

                case HeronThreat.State.Attack:
                    switch (heron.CurrentBitePhase)
                    {
                        case HeronThreat.BitePhase.Waiting:
                            targetDirW = Vector3.Slerp(restDirW, headDirW, lookWeight);
                            break;

                        case HeronThreat.BitePhase.Windup:
                        {
                            // 和 HeronThreat 的後仰用同樣的進度曲線
                            float s = Mathf.SmoothStep(0f, 1f, heron.WindupProgress01);
                            Vector3 look = Vector3.Slerp(restDirW, headDirW, lookWeight);
                            targetDirW = PullBackUp(look, headDirW, windupPullbackDeg * s);
                            speed = strikeSpeed;
                            break;
                        }

                        case HeronThreat.BitePhase.Stagger:
                            // 前段:撲出去咬;後段:被彈回,脖子縮回休息姿勢
                            strike = heron.StaggerProgress01 < HeronThreat.StaggerLungePart;
                            targetDirW = strike ? headDirW : restDirW;
                            speed = strikeSpeed;
                            break;
                    }
                    break;

                // Hidden(沒被抓)、Retreat:休息姿勢
                default:
                    targetDirW = restDirW;
                    break;
            }
        }

        // 休息方向 → 目標方向 的總彎曲量(換到 chainParent 座標),限制最大轉角
        Quaternion inverseParent = Quaternion.Inverse(parentRot);
        Quaternion targetDelta;
        if (strike)
        {
            float unusedDist;
            targetDelta = SolveTowards(inverseParent * (head.position - pivot), out unusedDist);
        }
        else
        {
            Quaternion deltaW = Quaternion.FromToRotation(restDirW, targetDirW);
            targetDelta = ClampDelta(inverseParent * deltaW * parentRot);
        }

        float k = 1f - Mathf.Exp(-Mathf.Max(0f, speed) * Time.deltaTime);
        smoothedDelta = Quaternion.Slerp(smoothedDelta, targetDelta, k);
        ApplyToBones(smoothedDelta);
    }

    // 把總彎曲量依權重分給各骨頭。繞同一根軸累積,所以父骨頭的轉動不會改變子骨頭要用的軸。
    void ApplyToBones(Quaternion delta)
    {
        float angle;
        Vector3 axis;
        ToSignedAngleAxis(delta, out angle, out axis);
        float limit = Mathf.Max(0f, maxBoneDegPerFrame);
        for (int i = 0; i < BoneCount; i++)
        {
            Vector3 axisInParent = Quaternion.Inverse(parentRelChain[i]) * axis;
            Quaternion desired = Quaternion.AngleAxis(angle * normWeights[i], axisInParent) * restLocalRot[i];
            currentLocalRot[i] = Quaternion.RotateTowards(currentLocalRot[i], desired, limit);
            bones[i].localRotation = currentLocalRot[i];
        }
    }

    // 找讓嘴尖最靠近目標的總彎曲量(最多迭代 4 次,不超過 maxAngleDeg)。
    // targetVec:Neck_1→目標 的向量(chainParent 方向、世界長度)。
    Quaternion SolveTowards(Vector3 targetVec, out float bestDist)
    {
        Quaternion delta = ClampDelta(Quaternion.FromToRotation(restTipVec, targetVec));
        Quaternion best = delta;
        bestDist = (TipVector(delta) - targetVec).magnitude;

        for (int i = 0; i < MaxSolveIterations && bestDist > reachTolerance; i++)
        {
            Quaternion correction = Quaternion.FromToRotation(TipVector(delta), targetVec);
            delta = ClampDelta(correction * delta);
            float dist = (TipVector(delta) - targetVec).magnitude;
            if (dist < bestDist) { bestDist = dist; best = delta; }
            if (Quaternion.Angle(correction, Quaternion.identity) < 0.05f) break;
        }
        return best;
    }

    // 給定總彎曲量,算 Neck_1→嘴尖 的向量(chainParent 方向、世界長度),不動 Transform
    Vector3 TipVector(Quaternion delta)
    {
        float angle;
        Vector3 axis;
        ToSignedAngleAxis(delta, out angle, out axis);
        Vector3 tip = Vector3.zero;
        float accumulated = 0f;
        for (int i = 0; i < BoneCount; i++)
        {
            accumulated += angle * normWeights[i];
            tip += Quaternion.AngleAxis(accumulated, axis) * segments[i];
        }
        return tip;
    }

    Quaternion ClampDelta(Quaternion delta)
    {
        float limit = solveLimitDeg >= 0f ? Mathf.Min(solveLimitDeg, maxAngleDeg) : maxAngleDeg;
        return Quaternion.RotateTowards(Quaternion.identity, delta, limit);
    }

    static void ToSignedAngleAxis(Quaternion q, out float angle, out Vector3 axis)
    {
        q.ToAngleAxis(out angle, out axis);
        if (angle > 180f) angle -= 360f;
        if (Mathf.Abs(angle) < 0.001f || float.IsNaN(axis.x) || float.IsInfinity(axis.x))
        {
            angle = 0f;
            axis = Vector3.up;
        }
    }

    void UpdateWeights()
    {
        float sum = 0f;
        for (int i = 0; i < BoneCount; i++)
        {
            normWeights[i] = boneWeights != null && i < boneWeights.Length ? Mathf.Max(0f, boneWeights[i]) : 0f;
            sum += normWeights[i];
        }
        for (int i = 0; i < BoneCount; i++)
            normWeights[i] = sum > 0f ? normWeights[i] / sum : 1f / BoneCount;
    }

    // 把方向 dir 往「上方、遠離玩家」轉 deg 度(繞 頭方向×上 的軸)
    static Vector3 PullBackUp(Vector3 dir, Vector3 headDir, float deg)
    {
        Vector3 axis = Vector3.Cross(headDir, Vector3.up);
        if (axis.sqrMagnitude < 1e-6f) axis = Vector3.Cross(dir, Vector3.up);
        if (axis.sqrMagnitude < 1e-6f) return dir;
        return Quaternion.AngleAxis(deg, axis.normalized) * dir;
    }

    // ================= 預告開始時的距離報告 =================

    void HandleWindup()
    {
        if (!logOnWindup || !ready || heron == null || heron.player == null) return;
        UpdateWeights();

        Vector3 head = heron.player.position;
        float pivotToHeadNow = Vector3.Distance(bones[0].position, head);

        PredictStrikePose(head, out Vector3 lungePos, out float yaw, out float lunge, out Vector3 pivotLocal, out bool aroundHip);
        float leanNow = heron.BiteLeanDeg;
        string axisText = aroundHip ? $"繞髖部{pivotLocal.ToString("F0")}" : "繞腳底";

        float tipDist = TipDistanceAt(lungePos, yaw, leanNow, pivotLocal, head, out float pivotToHead, out float needAngle);

        // 不夠的話,找要往前傾幾度才咬得到(其他條件不變)
        string verdict;
        if (tipDist <= reachTolerance)
            verdict = "咬得到,不需要更大的身體前傾";
        else if (heron.autoBiteLean)
            verdict = $"咬不到:自動前傾 {leanNow:F0}°(上限 {heron.maxBiteLeanDeg:F0}°)後嘴尖還差 {tipDist:F2}m,玩家的頭太低或太遠";
        else
        {
            float need = -1f;
            for (float lean = Mathf.Ceil(leanNow); lean <= 90f; lean += 1f)
            {
                if (TipDistanceAt(lungePos, yaw, lean, pivotLocal, head, out _, out _) <= reachTolerance) { need = lean; break; }
            }
            verdict = need >= 0f
                ? $"咬不到,需要更大的身體前傾:撲到底時約要前傾 {need:F0}°(現在 {leanNow:F1}°,也就是 windupTiltDeg 約要 {need * 2f:F0})"
                : $"咬不到,前傾到 90° 也不夠(需要靠更近、把鳥放低或改大小)";
        }

        Debug.Log($"[HeronNeck] t={Time.time:F1}s 預告開始:脖子pivot→玩家頭 現在={pivotToHeadNow:F3}m"
                + $",脖子+頭長度={neckLength:F3}m"
                + $",預測撲到底(往前 {lunge:F2}m、{axisText}前傾 {leanNow:F1}°)時 pivot→頭={pivotToHead:F3}m"
                + $"、需要轉角={needAngle:F0}°(上限 {maxAngleDeg:F0}°)、嘴尖離頭={tipDist:F3}m"
                + $" → {verdict}", this);
    }

    // 預測「撲到最前面」那一刻的身體姿勢(和 HeronThreat 撞開/被抓時同樣的公式)
    void PredictStrikePose(Vector3 head, out Vector3 lungePos, out float yaw, out float lunge, out Vector3 pivotLocal, out bool aroundHip)
    {
        Vector3 attackPos = heron.AttackPosition;
        yaw = transform.eulerAngles.y - heron.modelYawOffsetDeg;   // 預告開始時後仰是 0
        Vector3 fwd = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;
        Vector3 flat = head - attackPos; flat.y = 0f;
        lunge = heron.PredictLunge(flat.magnitude);
        lungePos = attackPos + fwd * lunge;

        // 傾斜轉軸:有 HeronHipLean 且啟用 → 髖部;否則 → HeronRoot 原點(腳底)
        if (hipLean == null) hipLean = GetComponent<HeronHipLean>();
        aroundHip = hipLean != null && hipLean.LeanActive;
        pivotLocal = aroundHip ? hipLean.hipLocalCm : Vector3.zero;
    }

    // 嘴尖要碰到玩家的頭,身體最少要往前傾幾度。碰不到時回傳最接近的角度,不超過 maxLeanDeg。
    public float RequiredBiteLean(float maxLeanDeg, float fallbackDeg)
    {
        if (!ready || heron == null || heron.player == null) return fallbackDeg;
        UpdateWeights();

        Vector3 head = heron.player.position;
        PredictStrikePose(head, out Vector3 lungePos, out float yaw, out _, out Vector3 pivotLocal, out _);

        // 脖子只能分擔 biteNeckLimitDeg,其餘都要靠身體往下彎
        solveLimitDeg = Mathf.Max(0f, biteNeckLimitDeg);
        try
        {
            float bestLean = 0f;
            float bestDist = float.PositiveInfinity;
            float limit = Mathf.Max(0f, maxLeanDeg);
            for (float lean = 0f; ; lean += 1f)
            {
                float current = Mathf.Min(lean, limit);
                float dist = TipDistanceAt(lungePos, yaw, current, pivotLocal, head, out _, out _);
                if (dist <= reachTolerance) return current;
                if (dist < bestDist) { bestDist = dist; bestLean = current; }
                if (lean >= limit) return bestLean;
            }
        }
        finally
        {
            solveLimitDeg = -1f;
        }
    }

    // 給定 HeronRoot 的位置(HeronThreat 設的)、面向、前傾角,算嘴尖最多能多靠近頭
    // leanPivotLocal:傾斜轉軸在 HeronRoot 本地座標的位置。(0,0,0) = 原點(腳底),和 HeronThreat 原本一樣;
    //                 髖部 = 和 HeronHipLean 一樣的算法(轉軸那一點的世界位置不因傾斜而移動)
    float TipDistanceAt(Vector3 rootPos, float yaw, float leanFwdDeg, Vector3 leanPivotLocal, Vector3 head, out float pivotToHead, out float needAngle)
    {
        Quaternion offRot = Quaternion.Euler(0f, heron.modelYawOffsetDeg, 0f);
        Quaternion uprightRot = Quaternion.Euler(0f, yaw, 0f) * offRot;
        Quaternion rootRot = Quaternion.Euler(0f, yaw, 0f)
                           * Quaternion.Euler(leanFwdDeg, 0f, 0f)
                           * offRot;
        Vector3 s = transform.lossyScale;
        Vector3 axisW = rootPos + uprightRot * Vector3.Scale(s, leanPivotLocal);
        Vector3 pivot = axisW + rootRot * Vector3.Scale(s, pivotInRoot - leanPivotLocal);
        Quaternion chainRot = rootRot * parentRelRoot;
        Vector3 toHead = head - pivot;
        pivotToHead = toHead.magnitude;
        needAngle = Vector3.Angle(chainRot * restDirParent, toHead);
        SolveTowards(Quaternion.Inverse(chainRot) * toHead, out float dist);
        return dist;
    }

    // ================= 找骨頭、算嘴尖 =================

    bool FindRig(bool logError)
    {
        for (int i = 0; i < BoneCount; i++)
        {
            string boneName = "Neck_" + (i + 1);
            bones[i] = FindDeep(transform, boneName);
            if (bones[i] == null || bones[i].parent == null)
            {
                if (logError) Debug.LogError($"[HeronNeckAim] 找不到 HeronRoot 底下的 {boneName},脖子不會動。", this);
                return false;
            }
        }

        skin = neck != null ? neck.GetComponent<SkinnedMeshRenderer>() : null;
        if (skin == null)
        {
            Transform found = FindDeep(transform, "Heron_Neck");
            if (found != null) skin = found.GetComponent<SkinnedMeshRenderer>();
        }
        if (skin == null) skin = GetComponentInChildren<SkinnedMeshRenderer>(true);
        if (skin == null || skin.sharedMesh == null)
        {
            if (logError) Debug.LogError("[HeronNeckAim] 找不到 HeronRoot 底下的 SkinnedMeshRenderer,脖子不會動。", this);
            return false;
        }
        neck = skin.transform;
        return true;
    }

    static Transform FindDeep(Transform t, string name)
    {
        if (t.name == name) return t;
        foreach (Transform c in t)
        {
            var r = FindDeep(c, name);
            if (r != null) return r;
        }
        return null;
    }

    // 嘴尖 = 目前姿勢的蒙皮網格裡最靠 HeronRoot +X 的頂點,存成 Neck_4 的本地點
    bool TryBakeTip(out Vector3 tipInNeck4)
    {
        tipInNeck4 = Vector3.zero;
        Mesh baked = new Mesh();
        try
        {
            skin.BakeMesh(baked, true);   // useScale:烘出的頂點可以直接用 skin.transform.TransformPoint
            Vector3[] vertices = baked.vertices;
            if (vertices == null || vertices.Length == 0) return false;

            Transform t = skin.transform;
            float best = float.NegativeInfinity;
            Vector3 bestWorld = Vector3.zero;
            foreach (Vector3 v in vertices)
            {
                Vector3 world = t.TransformPoint(v);
                float x = transform.InverseTransformPoint(world).x;
                if (x > best) { best = x; bestWorld = world; }
            }
            tipInNeck4 = bones[BoneCount - 1].InverseTransformPoint(bestWorld);
            return true;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[HeronNeckAim] BakeMesh 失敗:{e.Message}", this);
            return false;
        }
        finally
        {
            if (Application.isPlaying) Destroy(baked);
            else DestroyImmediate(baked);
        }
    }

    bool TipMatchesExpected(Vector3 tipInNeck4, out float offset)
    {
        Vector3 inRoot = transform.InverseTransformPoint(bones[BoneCount - 1].TransformPoint(tipInNeck4));
        offset = Vector3.Distance(inRoot, ExpectedTipInRoot);
        return offset <= ExpectedTipTolerance;
    }

    bool ResolveBeakTip()
    {
        float offset;
        if (TryBakeTip(out Vector3 tip))
        {
            beakTipLocal = tip;
            beakTipReady = true;
            if (!TipMatchesExpected(tip, out offset))
                Debug.LogWarning($"[HeronNeckAim] 嘴尖和預期位置差 {offset:F2} 單位,請確認 HeronModel 的位置、旋轉和縮放。", this);
            return true;
        }

        // 烘不出來時才用存好的值;場景裡可能還是舊模型的座標,所以一定要通過位置檢查
        if (beakTipReady && TipMatchesExpected(beakTipLocal, out offset))
        {
            Debug.LogWarning("[HeronNeckAim] BakeMesh 拿不到頂點,改用存好的嘴尖位置。", this);
            return true;
        }

        Debug.LogError("[HeronNeckAim] 算不出嘴尖,脖子不會動。請在編輯器裡按右鍵「重新計算嘴尖位置」後存檔。", this);
        return false;
    }

#if UNITY_EDITOR
    void Reset()
    {
        heron = GetComponent<HeronThreat>();
        ComputeBeakTipInEditor();
    }

    [ContextMenu("重新計算嘴尖位置")]
    void ComputeBeakTipInEditor()
    {
        if (!FindRig(true)) return;
        if (!TryBakeTip(out Vector3 tip)) { Debug.LogError("[HeronNeckAim] BakeMesh 讀不到脖子頂點。", this); return; }
        UnityEditor.Undo.RecordObject(this, "重新計算嘴尖位置");
        beakTipLocal = tip;
        beakTipReady = true;
        UnityEditor.EditorUtility.SetDirty(this);
        float len = Vector3.Distance(bones[BoneCount - 1].TransformPoint(tip), bones[0].position);
        TipMatchesExpected(tip, out float offset);
        Debug.Log($"[HeronNeckAim] 嘴尖(Neck_4 本地)={tip.ToString("F4")},Neck_1→嘴尖={len:F3}m,和預期位置差 {offset:F2} 單位", this);
    }
#endif
}
