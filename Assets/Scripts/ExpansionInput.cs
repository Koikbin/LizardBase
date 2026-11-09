using UnityEngine;
using TMPro;

public class ExpansionInput : MonoBehaviour
{
    public Transform head;            // CenterEyeAnchor
    public Transform leftHand;        // LeftHandAnchor
    public Transform rightHand;       // RightHandAnchor
    public TextMeshProUGUI display;   // 視野內的文字
    public Transform displayRoot;     // Canvas
    public Transform rig;             // OVRCameraRig
    public Camera cam;                // CenterEyeAnchor 上的 Camera

    public float maxScale = 2f;
    public float deadZone = 0.05f;
    public float smoothTime = 0.08f;
    public float followSpeed = 6f;

    public float distance;
    public float scale = 1f;

    // ---- 手肘外展（取消勾選 useElbow 就完全等於原本只用手把距離）----
    public bool useElbow = true;
    [Tooltip("手肘為必要條件:沒校準或追蹤中斷時膨脹一律為 0,不退回只看手把。")]
    public bool requireElbow = true;
    public ElbowTracker elbow;        // 留空會自動找場景裡的 ElbowTracker
    public float blendTime = 0.25f;   // 手肘有效/無效切換的淡入淡出時間
    public float minElbowRange = 20f; // 校準時併攏和張開的角度差至少要這麼多度，否則不存手肘校準

    // ---- 靜止才能膨脹（走動或爬行時膨脹歸零）----
    [Tooltip("勾選後只有靜止時才能膨脹,走動或爬行時膨脹會歸零。")]
    public bool requireStill = true;
    [Tooltip("頭部水平速度超過這個值(公尺/秒)就算在走動。太容易被取消就調高。")]
    public float moveSpeedLimit = 0.35f;
    [Tooltip("速度降下來後,要維持這麼多秒才算靜止。")]
    public float stillSeconds = 0.2f;
    public float headSpeed;           // 目前頭部水平速度,可在 Inspector 看著調門檻
    public bool isStill;              // 目前是否算靜止

    // ---- 膨脹衰退（撐太久會自己縮小,要收手再張開才能重新膨脹）----
    public enum ChargeState { Ready, Holding, Decaying, Spent }
    [Tooltip("膨脹是否會隨時間衰退。預設關閉,由 HeronThreat 在有天敵的場景開啟。")]
    public bool useDecay = false;
    [Tooltip("手勢達到 decayStartAt 後,維持幾秒才開始縮小。")]
    public float holdSeconds = 2.5f;
    [Tooltip("開始縮小後,幾秒縮到 0。")]
    public float decaySeconds = 2.5f;
    [Tooltip("手勢量(0~1)達到這個值時開始計時。")]
    public float decayStartAt = 0.6f;
    [Tooltip("手勢量低於這個值才算收手,才能重新膨脹。")]
    public float rearmGestureBelow = 0.3f;
    public ChargeState charge = ChargeState.Ready;
    public float gestureT;            // 雙手與手肘的手勢量 0~1,不含衰退和靜止條件
    public float capacity = 1f;       // 剩餘容量 0~1,實際膨脹不會超過它

    // 量測用，給顯示腳本讀
    public float distanceT;           // 手把距離換算的 0~1
    public float elbowT;              // 手肘角換算的 0~1（最後一次有效值）
    public float elbowWeight;         // 0 = 只用手把，1 = 取兩者較小值
    public bool elbowCalibrated;
    public float minA, maxA;          // 手肘校準角度（度）

    ExpansionCalibration calibration;
    float velocity;
    bool placed;
    bool displayWasActive;            // 場景原本文字是否顯示;校準時一律顯示,校準完還原
    float chargeTimer;
    float decayRate;

    void Start()
    {
        displayWasActive = display != null && display.gameObject.activeSelf;

        if (useElbow && elbow == null) elbow = FindAnyObjectByType<ElbowTracker>();

        calibration = GetComponent<ExpansionCalibration>();
        if (calibration == null) calibration = gameObject.AddComponent<ExpansionCalibration>();
        calibration.Init(this);
        SyncCalibration();
    }

    public bool ElbowReady => useElbow && elbow != null && elbow.isValid;
    public bool Required => useElbow && requireElbow;

    // 收手判定用:手肘必要時,追蹤中斷造成的手勢量下降不算收手
    bool Tracked => !Required || (ElbowReady && elbowCalibrated);

    void SyncCalibration()
    {
        elbowCalibrated = ExpansionCalibration.IsDone && ExpansionCalibration.HasElbow;
        minA = ExpansionCalibration.MinA;
        maxA = ExpansionCalibration.MaxA;
    }

    string ElbowStatus()
    {
        if (!useElbow) return "";
        return ElbowReady ? "\nelbow " + elbow.Angle.ToString("F0") : "\nelbow lost";
    }

    // 手把 0~1 和手肘 0~1 取較小值；手肘無效時在 blendTime 內淡回只用手把
    float Combine(float tCtrl)
    {
        if (!useElbow)
        {
            elbowWeight = 0f;
            return tCtrl;
        }

        bool ok = ElbowReady && elbowCalibrated;
        if (ok) elbowT = Mathf.Clamp01(Mathf.InverseLerp(minA, maxA, elbow.Angle));

        float step = blendTime > 0f ? Time.deltaTime / blendTime : 1f;

        // 必要條件:追蹤中斷時手肘值淡到 0,不退回只看手把
        if (Required)
        {
            if (!ok) elbowT = Mathf.MoveTowards(elbowT, 0f, step);
            elbowWeight = 1f;
            return Mathf.Min(tCtrl, elbowT);
        }

        elbowWeight = Mathf.MoveTowards(elbowWeight, ok ? 1f : 0f, step);

        // 淡出期間用最後一次有效的 elbowT，不讀無效的角度
        return Mathf.Lerp(tCtrl, Mathf.Min(tCtrl, elbowT), elbowWeight);
    }

    // ================= 膨脹衰退 =================

    void BeginDecay(float seconds)
    {
        charge = ChargeState.Decaying;
        decayRate = seconds > 0f ? 1f / seconds : float.MaxValue;
    }

    void ResetCharge()
    {
        charge = ChargeState.Ready;
        capacity = 1f;
        chargeTimer = 0f;
    }

    // 回傳目前的容量 0~1。gesture 不含靜止條件,所以走動造成的歸零不算收手
    float UpdateCharge(float gesture, float dt)
    {
        if (charge != ChargeState.Ready && gesture < rearmGestureBelow && Tracked)
        {
            ResetCharge();
            return capacity;
        }

        switch (charge)
        {
            case ChargeState.Ready:
                capacity = 1f;
                if (gesture >= decayStartAt)
                {
                    charge = ChargeState.Holding;
                    chargeTimer = 0f;
                }
                break;

            case ChargeState.Holding:
                chargeTimer += dt;
                if (chargeTimer >= holdSeconds) BeginDecay(decaySeconds);
                break;

            case ChargeState.Decaying:
                capacity = Mathf.MoveTowards(capacity, 0f, decayRate * dt);
                if (capacity <= 0f) charge = ChargeState.Spent;
                break;

            case ChargeState.Spent:
                capacity = 0f;
                break;
        }
        return capacity;
    }

    // 擋下一口後把膨脹快速洩掉,玩家要收手再張開才能再次防禦
    public void ConsumeCharge(float seconds)
    {
        BeginDecay(seconds);
    }

    // 目前的容量還能維持在 threshold 以上幾秒。沒有衰退時回傳無限大
    public float SecondsAboveThreshold(float threshold)
    {
        if (!useDecay) return float.PositiveInfinity;

        float full = Mathf.Max(0f, 1f - threshold) * decaySeconds;
        switch (charge)
        {
            case ChargeState.Ready:    return holdSeconds + full;
            case ChargeState.Holding:  return Mathf.Max(0f, holdSeconds - chargeTimer) + full;
            case ChargeState.Decaying: return decayRate > 0f ? Mathf.Max(0f, capacity - threshold) / decayRate : 0f;
            default:                   return 0f;
        }
    }

    // ================= 靜止判定 =================

    Vector3 lastHeadPos;
    bool hasLastHeadPos;
    float stillTimer;

    // 用世界座標量,真實走路和爬行(移動 rig)都算;除以 rig 縮放,膨脹時速度才不會被放大
    void UpdateStillness()
    {
        Vector3 p = head.position;
        p.y = 0f;
        float dt = Time.deltaTime;
        if (!hasLastHeadPos || dt <= 0f)
        {
            lastHeadPos = p;
            hasLastHeadPos = true;
            return;
        }

        float rigScale = rig != null ? Mathf.Max(rig.localScale.x, 0.0001f) : 1f;
        float instant = Vector3.Distance(p, lastHeadPos) / dt / rigScale;
        lastHeadPos = p;
        headSpeed = Mathf.Lerp(headSpeed, instant, 1f - Mathf.Exp(-20f * dt));

        if (headSpeed > moveSpeedLimit)
        {
            stillTimer = 0f;
            isStill = false;
        }
        else
        {
            stillTimer += dt;
            if (stillTimer >= stillSeconds) isStill = true;
        }
    }

    void Update()
    {
        // 用手相對於追蹤空間的本地座標，不受 rig 縮放影響
        distance = Vector3.Distance(leftHand.localPosition, rightHand.localPosition);
        UpdateStillness();

        // 文字跟著頭走
        if (displayRoot != null && Time.time > 2f)
        {
            Vector3 goalPos = head.position + head.forward * 1.0f - head.up * 0.25f;
            if (!placed)
            {
                displayRoot.position = goalPos;
                displayRoot.rotation = head.rotation;
                placed = true;
            }
            else
            {
                float k = 1f - Mathf.Exp(-followSpeed * Time.deltaTime);
                displayRoot.position = Vector3.Lerp(displayRoot.position, goalPos, k);
                displayRoot.rotation = Quaternion.Slerp(displayRoot.rotation, head.rotation, k);
            }
        }

        bool trigger = OVRInput.GetDown(OVRInput.Button.PrimaryIndexTrigger, OVRInput.Controller.RTouch);
        bool recal = OVRInput.GetDown(OVRInput.Button.One, OVRInput.Controller.LTouch);
        if (recal) calibration.Restart();

        if (!ExpansionCalibration.IsDone)
        {
            calibration.Tick(distance, trigger);
            SyncCalibration();
            ResetCharge();

            // 校準中身體回到原本大小,不留著上一次的膨脹
            scale = Mathf.SmoothDamp(scale, 1f, ref velocity, smoothTime);
            ApplyRigScale(scale);

            if (!ExpansionCalibration.IsDone)
                Show(calibration.Prompt + "\nd " + distance.ToString("F2") + ElbowStatus() + calibration.Hint);
            return;
        }

        distanceT = Mathf.Clamp01(Mathf.InverseLerp(ExpansionCalibration.MinD, ExpansionCalibration.MaxD, distance));
        gestureT = Combine(distanceT);

        float t = gestureT;
        if (useDecay) t = Mathf.Min(t, UpdateCharge(gestureT, Time.deltaTime));
        if (requireStill && !isStill) t = 0f;
        if (t < deadZone) t = 0f;

        float targetScale = Mathf.Lerp(1f, maxScale, t);
        scale = Mathf.SmoothDamp(scale, targetScale, ref velocity, smoothTime);

        ApplyRigScale(scale);

        Show("d " + distance.ToString("F2")
            + "\nx" + scale.ToString("F2")
            + "\neyes " + RenderedSeparation().ToString("F3")
            + ElbowStatus());
    }

    float RenderedSeparation()
    {
        if (cam == null) return -1f;
        Matrix4x4 l = cam.GetStereoViewMatrix(Camera.StereoscopicEye.Left).inverse;
        Matrix4x4 r = cam.GetStereoViewMatrix(Camera.StereoscopicEye.Right).inverse;
        Vector3 pl = l.GetColumn(3);
        Vector3 pr = r.GetColumn(3);
        return Vector3.Distance(pl, pr);
    }

    void ApplyRigScale(float s)
    {
        if (rig == null) return;

        // 放大前後讓頭的水平位置不變，你才不會被推開
        Vector3 before = head.position;
        rig.localScale = Vector3.one * s;
        Vector3 after = head.position;

        Vector3 p = rig.position;
        p.x += before.x - after.x;
        p.z += before.z - after.z;
        rig.position = p;
    }

    void Show(string s)
    {
        if (display == null) return;

        bool visible = !ExpansionCalibration.IsDone || displayWasActive;
        if (display.gameObject.activeSelf != visible) display.gameObject.SetActive(visible);
        display.text = s;
    }
}
