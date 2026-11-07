using UnityEngine;

// 讀 Quest 3 上半身追蹤（OVRBody），算手肘外展角。
// 外展角 = 上臂方向（肩關節 → 手肘）和軀幹往下方向（胸 → 髖）的夾角。
// 手垂在身側約 0 度，平舉約 90 度，高舉超過 90 度。往前平舉也會是 90 度。
// 只負責讀資料和判斷有不有效，不決定膨脹值；膨脹值由 ExpansionInput 合併。
//
// body 留空時會自己建一個 OVRBody，並在拿到 BODY_TRACKING 權限後才啟動它。
[DefaultExecutionOrder(-50)]
public class ElbowTracker : MonoBehaviour
{
    public OVRBody body;                 // 留空 = 自動建立
    [Range(0f, 1f)]
    public float minConfidence = 0.5f;   // 信心值低於這個就算無效

    // 身體追蹤和「同時手勢+手把」不能並存（OVRBody 看到 OVRManager 開著就會關掉自己）。
    // 勾選：啟動時把同時模式關掉，手肘才能動；但 BlowoutDepthProbe 拿著手把時的手部骨架排除可能失效。
    // 取消勾選：不碰同時模式，維持原狀，手肘追蹤不會啟動（顯示 simul ON）。
    public bool disableSimultaneousHands = true;

    // ---- 給其他腳本讀的結果 ----
    public bool simultaneousOn;          // OVRManager 的同時手勢+手把目前是否開著
    public bool simultaneousSwitchOk;    // 執行時關閉同時模式的呼叫是否回報成功（沒呼叫就是 false）
    public bool permissionGranted;
    public bool started;                 // OVRBody 有在跑（沒被自己關掉）
    public bool hasData;                 // 這一幀有拿到 BodyState
    public float confidence;
    public OVRPlugin.BodyTrackingCalibrationState calibration = OVRPlugin.BodyTrackingCalibrationState.Invalid;
    public bool jointsValid;             // 需要的關節位置都有效
    public float leftAngle;              // 度
    public float rightAngle;             // 度
    public bool isValid;                 // 以上全部都過關

    // 左右取較小值：兩手都要張開才算張開
    public float Angle => Mathf.Min(leftAngle, rightAngle);

    const OVRPlugin.BoneId Hips = OVRPlugin.BoneId.Body_Hips;
    const OVRPlugin.BoneId Chest = OVRPlugin.BoneId.Body_Chest;
    const OVRPlugin.BoneId LUpper = OVRPlugin.BoneId.Body_LeftArmUpper;
    const OVRPlugin.BoneId LLower = OVRPlugin.BoneId.Body_LeftArmLower;
    const OVRPlugin.BoneId RUpper = OVRPlugin.BoneId.Body_RightArmUpper;
    const OVRPlugin.BoneId RLower = OVRPlugin.BoneId.Body_RightArmLower;

    bool ownsBody;
    bool requested;
    int retries;
    float nextRetry;
    OVRManager manager;

    void Start()
    {
        manager = FindAnyObjectByType<OVRManager>();
        if (disableSimultaneousHands && manager != null && manager.SimultaneousHandsAndControllersEnabled)
        {
            simultaneousSwitchOk = OVRInput.DisableSimultaneousHandsAndControllers();
            manager.SimultaneousHandsAndControllersEnabled = false;
        }

        if (body == null)
        {
            // 先建在關閉的物件上，避免 OVRBody 在沒權限時就 OnEnable 然後把自己關掉
            var go = new GameObject("OVRBody (auto)");
            go.SetActive(false);
            go.transform.SetParent(transform, false);
            body = go.AddComponent<OVRBody>();
            ownsBody = true;
        }
    }

    void Update()
    {
        simultaneousOn = manager != null && manager.SimultaneousHandsAndControllersEnabled;
        permissionGranted =OVRPermissionsRequester.IsPermissionGranted(OVRPermissionsRequester.Permission.BodyTracking);

        if (!permissionGranted)
        {
            if (!requested)
            {
                OVRPermissionsRequester.Request(new[] { OVRPermissionsRequester.Permission.BodyTracking });
                requested = true;
            }
            started = false;
            hasData = false;
            Invalidate();
            return;
        }

        // 有權限了才啟動
        if (ownsBody && !body.gameObject.activeSelf)
            body.gameObject.SetActive(true);

        // OVRBody 啟動失敗會把自己 enabled 設成 false。隔幾秒重試幾次。
        if (body != null && !body.enabled && retries < 5 && Time.unscaledTime >= nextRetry)
        {
            retries++;
            nextRetry = Time.unscaledTime + 3f;
            body.enabled = true;
        }

        started = body != null && body.isActiveAndEnabled;
        if (!started) { hasData = false; Invalidate(); return; }

        OVRPlugin.BodyState? maybe = body.BodyState;
        hasData = maybe.HasValue;
        if (!hasData) { Invalidate(); return; }

        OVRPlugin.BodyState s = maybe.Value;
        confidence = s.Confidence;
        calibration = s.CalibrationStatus;

        var j = s.JointLocations;
        jointsValid = j != null
            && Ok(j, Hips) && Ok(j, Chest)
            && Ok(j, LUpper) && Ok(j, LLower)
            && Ok(j, RUpper) && Ok(j, RLower);

        if (jointsValid)
        {
            Vector3 down = P(j, Hips) - P(j, Chest);
            leftAngle = Vector3.Angle(P(j, LLower) - P(j, LUpper), down);
            rightAngle = Vector3.Angle(P(j, RLower) - P(j, RUpper), down);
        }

        isValid = jointsValid
            && confidence >= minConfidence
            && calibration == OVRPlugin.BodyTrackingCalibrationState.Valid;
    }

    void Invalidate()
    {
        isValid = false;
        jointsValid = false;
    }

    static bool Ok(OVRPlugin.BodyJointLocation[] j, OVRPlugin.BoneId id)
    {
        int i = (int)id;
        return i < j.Length && j[i].PositionValid;
    }

    static Vector3 P(OVRPlugin.BodyJointLocation[] j, OVRPlugin.BoneId id)
    {
        return j[(int)id].Pose.Position.FromFlippedZVector3f();
    }
}
