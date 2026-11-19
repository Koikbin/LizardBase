using UnityEngine;
using UnityEngine.XR;

/// 手抓住表面後，手相對身體移動多少，身體就往反方向移動多少。
/// 一般表面（地面）：手碰到就抓住，只能水平爬行。
/// Tag 為 Climbable 的表面（牆）：手碰到並按住 Grip 才抓住，可以往任何方向爬，抓住時不受重力影響，放開 Grip 才會放手。
///
/// 沒有頭顯時（場景裡有啟用的 DebugInput、Simulate Hands 有勾、沒有偵測到 XR 裝置），改用滑鼠模擬手：
/// 按住滑鼠左鍵 = 左手、按住 F = 右手，手會放到滑鼠游標指到的表面上並按住 Grip；
/// 按著往下拖，手就往身體拉，身體會往前（地面）或往上（牆）移動。
[RequireComponent(typeof(CharacterController))]
public class LizardLocomotion : MonoBehaviour
{
    private const string ClimbableTag = "Climbable";

    [Header("References")]
    [SerializeField] private CharacterController characterController;
    [SerializeField] private Transform trackingSpace;
    [SerializeField] private Transform leftHandAnchor;
    [SerializeField] private Transform rightHandAnchor;
    [SerializeField] private Transform cameraTransform;

    [Header("Grab")]
    [Tooltip("手距離表面多近會抓住（公尺）")]
    [SerializeField] private float grabRadius = 0.12f;
    [Tooltip("手距離地面多遠會放開（公尺），要比 Grab Radius 大，避免在邊界反覆抓放")]
    [SerializeField] private float releaseRadius = 0.18f;
    [SerializeField] private LayerMask surfaceMask = ~0;

    [Header("Movement")]
    [Tooltip("手移動 1 公尺時，身體移動幾公尺")]
    [SerializeField] private float pullGain = 1f;
    [Tooltip("手速超過這個值（公尺/秒）視為追蹤跳動，該幀不移動")]
    [SerializeField] private float maxHandSpeed = 6f;
    [Tooltip("在地面上能否往視線後方移動。關閉時，手貼地往前伸不會把身體往後推")]
    [SerializeField] private bool allowBackwardOnGround = false;
    [SerializeField] private float gravity = -9.8f;

    [Header("跳躍:兩手同時碰著地面、一起快速往下推 → 往看的方向彈出去")]
    [SerializeField] private bool enableJump = true;
    [Tooltip("兩隻手往下推的速度都超過這個值才跳(公尺/秒,真實的手速)")]
    [SerializeField] private float jumpPushSpeed = 0.8f;
    [Tooltip("手往下推到這個速度 = 跳最遠")]
    [SerializeField] private float jumpFullPushSpeed = 2.0f;
    [Tooltip("起跳往上的速度(公尺/秒):最輕 ~ 最用力")]
    [SerializeField] private Vector2 jumpUpSpeed = new Vector2(1.6f, 2.6f);
    [Tooltip("起跳往前的速度(公尺/秒):最輕 ~ 最用力")]
    [SerializeField] private Vector2 jumpForwardSpeed = new Vector2(1.2f, 3.0f);
    [Tooltip("手離開地面後多久內往下推還算數(秒);往下推時手會穿進地面,碰不到了")]
    [SerializeField] private float jumpGroundMemory = 0.2f;
    [SerializeField] private float jumpCooldown = 0.4f;

    [Header("坐著玩:把真實的某個高度當作遊戲地面(手放在這個高度 = 摸到地面)")]
    [Tooltip("打勾 = 坐姿。真實高度 Seated Ground Height 會對到遊戲的地面")]
    [SerializeField] private bool seated = true;
    [Tooltip("坐著時當作地面的真實高度(公尺,從真實地板算)。0.62 ≈ 坐著手放在大腿上")]
    [SerializeField] private float seatedGroundHeight = 0.62f;
    [Tooltip("按右手 B:把現在兩手的高度設成地面(會記住)")]
    [SerializeField] private bool allowGroundCalibration = true;
    private const string GroundHeightKey = "seatedGroundHeight";

    [Header("重設視角:依照玩家現在的頭的位置,把視角放回蜥蜴身上")]
    [Tooltip("打勾 = 開始時自動重設一次(頭部追蹤穩定後)。每個人坐的高度、位置不同,不重設容易穿進地裡或摸不到地面")]
    [SerializeField] private bool autoResetViewOnStart = true;
    [Tooltip("重設後,眼睛離遊戲地面的真實高度(公尺)。坐著時眼睛到大腿/桌面大約 0.55 m")]
    [SerializeField] private float eyeHeightAboveGround = 0.55f;
    [Tooltip("開始後等幾秒、頭部追蹤穩定了才自動重設")]
    [SerializeField] private float autoResetDelay = 0.5f;
    [Tooltip("左手 Y:隨時重設視角。另外長按 Meta 鍵重新置中時也會自動重設")]
    [SerializeField] private bool allowManualReset = true;
    private bool viewResetDone;
    private float viewResetAt;

    [Header("回饋 / 除錯")]
    [Tooltip("抓住地面或牆面時手把震一下")]
    [SerializeField] private bool hapticOnGrab = true;
    [Tooltip("每秒在 Log 印一次雙手離最近表面多遠、有沒有抓住(用 adb logcat 看)")]
    [SerializeField] private bool logDiagnostics = true;

    [Header("Debug")]
    [SerializeField] private bool logGrabEvents = true;
    [Tooltip("模擬手時，手最遠能伸多遠（公尺，從相機算起）")]
    [SerializeField] private float debugReach = 1.5f;
    [Tooltip("模擬手放開時的位置（相機的本地座標，左手會自動把 X 變負）")]
    [SerializeField] private Vector3 debugRestOffset = new Vector3(0.2f, -0.05f, 0.1f);

    private class Hand
    {
        public string name;
        public float side;               // 左手 -1、右手 +1，模擬手用
        public Transform anchor;
        public Vector3 position;         // 這一幀手的世界座標（真實追蹤或模擬）
        public bool simulatedPressed;
        public Vector3 previousLocalPosition;
        public bool hasPreviousPosition;
        public bool isGrabbing;
        public bool onClimbable;
        public Vector3 localVelocity;    // 手在 TrackingSpace 裡的速度(真實公尺/秒,平滑過)
        public float hapticUntil;
        public bool hapticOn;
        public float lastGroundTime = -10f;
    }

    private readonly Collider[] overlapResults = new Collider[16];
    private readonly RaycastHit[] rayResults = new RaycastHit[16];
    private Hand leftHand;
    private Hand rightHand;
    private float verticalVelocity;
    private Camera debugCamera;

    public bool IsAirborne => airborne;
    public event System.Action Jumped;
    private bool airborne;
    private Vector3 airVelocity;
    private float lastJumpTime = -10f;

    // 站在石頭階梯旁、比石頭低的坑壁上:抓不住,一直往下滑(SlipperySlope)
    public bool IsSliding { get; private set; }
    private float slideSpeed;

    public bool IsClimbing => (leftHand != null && leftHand.onClimbable)
                           || (rightHand != null && rightHand.onClimbable);

    /// 有啟用 DebugInput 並勾選 Simulate Hands，而且沒有接頭顯時，用滑鼠模擬手。
    public static bool UseSimulatedHands =>
        DebugInput.Active && DebugInput.SimulateHands && !XRSettings.isDeviceActive;

    private void Awake()
    {
        // 自動重設視角時,地面高度每次都依當下的人重新算,不沿用上一位玩家存的校準
        if (!autoResetViewOnStart && PlayerPrefs.HasKey(GroundHeightKey)) seatedGroundHeight = PlayerPrefs.GetFloat(GroundHeightKey);
        leftHand = new Hand { name = "左手", side = -1f, anchor = leftHandAnchor };
        rightHand = new Hand { name = "右手", side = 1f, anchor = rightHandAnchor };
        if (cameraTransform != null) debugCamera = cameraTransform.GetComponent<Camera>();
    }

    private void OnEnable()
    {
        verticalVelocity = 0f;
        ResetHand(leftHand);
        ResetHand(rightHand);
        viewResetDone = false;
        viewResetAt = Time.time + autoResetDelay;
        HookSystemRecenter();
    }

    private void OnDisable()
    {
        ResetHand(leftHand);
        ResetHand(rightHand);
        if (hookedDisplay != null) hookedDisplay.RecenteredPose -= OnSystemRecenter;
        hookedDisplay = null;
    }

    private OVRDisplay hookedDisplay;
    // OVRManager 可能比這個元件晚建立,所以 Update 裡也會再試
    private void HookSystemRecenter()
    {
        if (hookedDisplay != null || OVRManager.display == null) return;
        hookedDisplay = OVRManager.display;
        hookedDisplay.RecenteredPose += OnSystemRecenter;
    }

    // 長按 Meta 鍵重新置中後,系統會把追蹤原點移到頭的位置,這時再把視角放回蜥蜴身上
    private void OnSystemRecenter() => ResetView();

    private void Update()
    {
        if (Time.deltaTime <= 0f) return;

        bool leftGrip, rightGrip;
        if (UseSimulatedHands)
        {
            leftGrip = SimulateHand(leftHand, DebugInput.LeftHand);
            rightGrip = SimulateHand(rightHand, DebugInput.RightHand);
        }
        else
        {
            ApplySeatedOffset();
            HookSystemRecenter();
            if (autoResetViewOnStart && !viewResetDone && Time.time >= viewResetAt && HeadTracked()) ResetView();
            if (allowManualReset && OVRInput.GetDown(OVRInput.Button.Two, OVRInput.Controller.LTouch)) ResetView();
            if (allowGroundCalibration && OVRInput.GetDown(OVRInput.Button.Two, OVRInput.Controller.RTouch)) CalibrateGround();
            leftHand.position = leftHand.anchor.position;
            rightHand.position = rightHand.anchor.position;
            leftGrip = OVRInput.Get(OVRInput.Button.PrimaryHandTrigger, OVRInput.Controller.LTouch);
            rightGrip = OVRInput.Get(OVRInput.Button.PrimaryHandTrigger, OVRInput.Controller.RTouch);
        }
        Tick(Time.deltaTime, leftGrip, rightGrip);
        UpdateHaptics(leftHand, OVRInput.Controller.LTouch);
        UpdateHaptics(rightHand, OVRInput.Controller.RTouch);
        if (logDiagnostics && Time.time >= nextDiagTime)
        {
            nextDiagTime = Time.time + 1f;
            Debug.Log($"[Loco] 玩家 {transform.position} 著地={characterController.isGrounded} 縮放={SizeScale:F2} | " +
                      $"{Diag(leftHand, leftGrip)} | {Diag(rightHand, rightGrip)}");
        }
    }

    /// 按著：手放到游標指到的表面（太遠就停在最遠處的空中）；放開：手回到相機旁邊。回傳是否按住 Grip。
    private bool SimulateHand(Hand hand, bool pressed)
    {
        // 按下或放開那一幀手會瞬移，不能算成拉動
        if (pressed != hand.simulatedPressed) hand.hasPreviousPosition = false;
        hand.simulatedPressed = pressed;

        if (!pressed)
        {
            Vector3 rest = new Vector3(debugRestOffset.x * hand.side, debugRestOffset.y, debugRestOffset.z);
            hand.position = cameraTransform.TransformPoint(rest);
            return false;
        }

        Ray ray = debugCamera != null
            ? debugCamera.ScreenPointToRay(DebugInput.PointerPosition)
            : new Ray(cameraTransform.position, cameraTransform.forward);

        float reach = debugReach * SizeScale;
        hand.position = ray.GetPoint(reach);
        float nearest = reach;
        int count = Physics.RaycastNonAlloc(ray, rayResults, reach, surfaceMask, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < count; i++)
        {
            RaycastHit hit = rayResults[i];
            if (hit.collider.transform.IsChildOf(transform) || hit.distance >= nearest) continue;
            nearest = hit.distance;
            hand.position = hit.point;
        }
        return true;
    }

    private float nextDiagTime;

    // 坐姿:TrackingSpace 往下移 Seated Ground Height,真實的這個高度就剛好是遊戲地面(TrackingSpace 的本地單位 = 真實公尺)
    private void ApplySeatedOffset()
    {
        if (trackingSpace == null) return;
        Vector3 lp = trackingSpace.localPosition;
        float y = seated ? -seatedGroundHeight : 0f;
        if (!Mathf.Approximately(lp.y, y)) trackingSpace.localPosition = new Vector3(lp.x, y, lp.z);
    }

    private bool HeadTracked()
    {
        if (cameraTransform == null) return false;
        if (OVRManager.instance != null && !OVRManager.isHmdPresent) return false;
        return cameraTransform.localPosition.sqrMagnitude > 0.0001f;   // 追蹤還沒開始時頭在原點
    }

    // 重設視角:
    //   1. 高度:地面 = 頭現在的真實高度 - Eye Height Above Ground → 不管多高、坐多低,眼睛離地面都一樣高
    //   2. 位置與方向:轉動、平移 TrackingSpace,讓頭剛好在蜥蜴身體正上方、面向蜥蜴的前方
    // TrackingSpace 的本地單位 = 真實公尺;OVRCameraRig 的縮放(0.25)會把它變成蜥蜴大小。
    public void ResetView()
    {
        if (trackingSpace == null || cameraTransform == null) return;
        viewResetDone = true;

        Vector3 head = cameraTransform.localPosition;   // 頭在 TrackingSpace 裡(真實公尺)
        seatedGroundHeight = Mathf.Clamp(head.y - eyeHeightAboveGround, 0f, 1.5f);

        Vector3 forward = cameraTransform.localRotation * Vector3.forward;
        float headYaw = Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg;
        Quaternion rot = Quaternion.Euler(0f, -headYaw, 0f);
        Vector3 rotatedHead = rot * head;

        trackingSpace.localRotation = rot;
        trackingSpace.localPosition = new Vector3(-rotatedHead.x, seated ? -seatedGroundHeight : 0f, -rotatedHead.z);

        // 手的位置瞬間跳了,不能算成一次拉動
        ResetHand(leftHand);
        ResetHand(rightHand);
        if (leftHand != null && rightHand != null && Application.isPlaying)
        {
            Buzz(leftHand, 0.15f, 0.6f);
            Buzz(rightHand, 0.15f, 0.6f);
        }
        Debug.Log($"[Loco] 重設視角:頭高 {head.y:F2} m → 地面 {seatedGroundHeight:F2} m(眼睛離地 {eyeHeightAboveGround:F2} m),轉 {-headYaw:F0}°");
    }

    // 右手 B:兩手現在的高度(真實)當作地面,稍微往下 3 公分,手放著就算摸到
    private void CalibrateGround()
    {
        if (leftHand.anchor == null || rightHand.anchor == null) return;
        float h = (leftHand.anchor.localPosition.y + rightHand.anchor.localPosition.y) * 0.5f;   // 相對 TrackingSpace = 真實地板
        seatedGroundHeight = Mathf.Clamp(h - 0.03f, 0f, 1.5f);
        PlayerPrefs.SetFloat(GroundHeightKey, seatedGroundHeight);
        PlayerPrefs.Save();
        ApplySeatedOffset();
        Buzz(leftHand, 0.25f, 1f);
        Buzz(rightHand, 0.25f, 1f);
        Debug.Log($"[Loco] 地面高度校準 = 真實 {seatedGroundHeight:F2} m");
    }

    // 手離最近的表面多遠(換算成真實公尺,跟抓取半徑比)
    private string Diag(Hand hand, bool grip)
    {
        float s = SizeScale;
        string nearest = "附近 0.5m 內沒有表面";
        int count = Physics.OverlapSphereNonAlloc(hand.position, 0.5f * s, overlapResults, surfaceMask, QueryTriggerInteraction.Ignore);
        float best = float.MaxValue;
        Collider bestC = null;
        for (int i = 0; i < count; i++)
        {
            var c = overlapResults[i];
            if (c.transform.IsChildOf(transform)) continue;
            Vector3 p = c is MeshCollider mc && !mc.convex ? c.ClosestPointOnBounds(hand.position) : c.ClosestPoint(hand.position);
            float d = Vector3.Distance(p, hand.position);
            if (d < best) { best = d; bestC = c; }
        }
        if (bestC != null)
            nearest = $"最近表面 {best / s:F2}m(真實,抓取半徑 {grabRadius:F2}m) {(bestC.transform.parent != null ? bestC.transform.parent.name : bestC.name)}/{bestC.name}";
        string below = Physics.Raycast(hand.position + Vector3.up * 0.05f * s, Vector3.down, out RaycastHit hb, 1.5f * s, surfaceMask, QueryTriggerInteraction.Ignore)
            ? $"正下方表面 {(hb.distance - 0.05f * s) / s:F2}m {hb.collider.name}" : "正下方 1.5m 內沒有表面";
        return $"{hand.name} grip={grip} 抓住={hand.isGrabbing} 爬牆={hand.onClimbable} 手高={(hand.position.y - transform.position.y) / s:F2}m(真實,離腳底) {below} {nearest}";
    }

    private void UpdateHaptics(Hand hand, OVRInput.Controller controller)
    {
        if (UseSimulatedHands) return;
        if (hand.hapticOn && Time.time > hand.hapticUntil)
        {
            OVRInput.SetControllerVibration(0f, 0f, controller);
            hand.hapticOn = false;
        }
    }

    private void Buzz(Hand hand, float seconds, float amplitude)
    {
        if (!hapticOnGrab || UseSimulatedHands) return;
        OVRInput.SetControllerVibration(0.5f, amplitude, hand == leftHand ? OVRInput.Controller.LTouch : OVRInput.Controller.RTouch);
        hand.hapticUntil = Time.time + seconds;
        hand.hapticOn = true;
    }

    // 玩家大小(OVRCameraRig 的縮放,含膨脹)。抓取半徑、手速上限、模擬手的距離都跟著縮放
    private float SizeScale => trackingSpace != null ? trackingSpace.lossyScale.x : 1f;

    private void Tick(float deltaTime, bool leftGrip, bool rightGrip)
    {
        UpdateGrab(leftHand, leftGrip);
        UpdateGrab(rightHand, rightGrip);

        Vector3 leftMove = CalculateBodyMove(leftHand, deltaTime);
        Vector3 rightMove = CalculateBodyMove(rightHand, deltaTime);

        // 兩手都抓住時採用移動較多的那隻，這樣放著不動的手不會讓另一隻手的拉動減半
        Vector3 bodyMove = leftMove.sqrMagnitude >= rightMove.sqrMagnitude ? leftMove : rightMove;

        if (IsClimbing)
        {
            verticalVelocity = 0f;
            airborne = false;               // 跳到一半抓住石頭側面 → 停住,改成攀爬
            airVelocity = Vector3.zero;
        }
        else if (airborne)
        {
            bodyMove = airVelocity * deltaTime;   // 空中:照起跳速度飛,手抓地面不算
            verticalVelocity += gravity * deltaTime;
            if (characterController.isGrounded && verticalVelocity < 0f && Time.time > lastJumpTime + 0.1f)
            {
                airborne = false;
                airVelocity = Vector3.zero;
            }
        }
        else if (characterController.isGrounded)
            verticalVelocity = -1f;
        else
            verticalVelocity += gravity * deltaTime;

        bodyMove += Slide(deltaTime);

        TryJump();

        characterController.Move(bodyMove + Vector3.up * (verticalVelocity * deltaTime));
    }

    // 腳下是會滑的坑壁 → 沿著坡往下滑,越滑越快;離開坑壁(或抓住石頭)就停。
    // 不看 isGrounded:接近 Slope Limit 的陡坡上 CharacterController 常常判定沒著地,人會卡在坡上
    private Vector3 Slide(float deltaTime)
    {
        var slope = SlipperySlope.Instance;
        bool sliding = false;
        Vector3 downhill = Vector3.zero;
        float sin = 0f;
        if (slope != null && !IsClimbing)
        {
            float s = SizeScale;
            Vector3 origin = transform.position + Vector3.up * (0.1f * s);
            // 斜坡上膠囊是側邊碰到坡面,正下方的坡面會比腳底低大約一個半徑,所以往下多找一點
            float reach = 0.1f * s + 1.5f * characterController.radius + 2f * characterController.skinWidth;
            bool landing = !airborne || (verticalVelocity < 0f && Time.time > lastJumpTime + 0.1f);   // 跳起來往上的時候不算
            if (landing
                && Physics.Raycast(origin, Vector3.down, out RaycastHit hit, reach, surfaceMask, QueryTriggerInteraction.Ignore)
                && !hit.collider.transform.IsChildOf(transform)
                && slope.IsSlippery(hit.collider, transform.position))
            {
                downhill = Vector3.ProjectOnPlane(Vector3.down, hit.normal);
                sin = downhill.magnitude;                       // = sin(坡度)
                if (sin > 0.05f) { downhill /= sin; sliding = true; }
            }
        }
        if (!sliding) { slideSpeed = 0f; IsSliding = false; return Vector3.zero; }
        if (!IsSliding && logGrabEvents) Debug.Log("[Loco] 踩到濕滑的坑壁,往下滑");
        IsSliding = true;
        airborne = false;               // 跳歪落在坑壁上 → 改成往下滑
        airVelocity = Vector3.zero;
        verticalVelocity = -1f;
        slideSpeed = Mathf.Min(slope.maxSlideSpeed, slideSpeed + slope.slideAccel * sin * deltaTime);
        return downhill * (slideSpeed * deltaTime);
    }

    // 兩手剛碰過地面、一起快速往下推 → 起跳。鍵盤測試:DebugInput 的 J 鍵 = 最用力的一跳
    private void TryJump()
    {
        if (!enableJump || airborne || IsClimbing || Time.time < lastJumpTime + jumpCooldown) return;
        if (!characterController.isGrounded) return;

        float push;
        if (UseSimulatedHands) push = DebugInput.JumpDown ? jumpFullPushSpeed : 0f;
        else
        {
            bool bothOnGround = Time.time - leftHand.lastGroundTime < jumpGroundMemory
                             && Time.time - rightHand.lastGroundTime < jumpGroundMemory;
            if (!bothOnGround) return;
            push = Mathf.Min(-leftHand.localVelocity.y, -rightHand.localVelocity.y);   // 比較慢的那隻手
        }
        if (push < jumpPushSpeed) return;

        float t = Mathf.InverseLerp(jumpPushSpeed, jumpFullPushSpeed, push);
        Vector3 forward = Vector3.ProjectOnPlane(cameraTransform.forward, Vector3.up).normalized;
        airVelocity = forward * Mathf.Lerp(jumpForwardSpeed.x, jumpForwardSpeed.y, t);
        verticalVelocity = Mathf.Lerp(jumpUpSpeed.x, jumpUpSpeed.y, t);
        airborne = true;
        lastJumpTime = Time.time;
        if (logGrabEvents) Debug.Log($"跳!手往下推 {push:F2} m/s → 往前 {airVelocity.magnitude:F2}、往上 {verticalVelocity:F2} m/s");
        Jumped?.Invoke();
    }

    private void UpdateGrab(Hand hand, bool gripHeld)
    {
        float radius = (hand.isGrabbing ? releaseRadius : grabRadius) * SizeScale;
        FindSurfaces(hand.position, radius, out bool touchingGround, out bool touchingClimbable);

        // 抓住牆後只要 Grip 還按著就不會放手，即使手暫時離開牆面
        bool onClimbable = gripHeld && (touchingClimbable || hand.onClimbable);
        bool isGrabbing = onClimbable || touchingGround;

        if (isGrabbing && (!hand.isGrabbing || onClimbable != hand.onClimbable)) Buzz(hand, 0.06f, onClimbable ? 0.8f : 0.4f);
        if (logGrabEvents && (isGrabbing != hand.isGrabbing || onClimbable != hand.onClimbable))
            Debug.Log(isGrabbing ? $"{hand.name} 抓住（{(onClimbable ? "牆面" : "地面")}）" : $"{hand.name} 放開");

        hand.isGrabbing = isGrabbing;
        hand.onClimbable = onClimbable;
        if (touchingGround || touchingClimbable) hand.lastGroundTime = Time.time;
    }

    private void FindSurfaces(Vector3 position, float radius, out bool touchingGround, out bool touchingClimbable)
    {
        touchingGround = false;
        touchingClimbable = false;
        int count = Physics.OverlapSphereNonAlloc(position, radius, overlapResults, surfaceMask, QueryTriggerInteraction.Ignore);

        for (int i = 0; i < count; i++)
        {
            Collider surface = overlapResults[i];
            if (surface.transform.IsChildOf(transform)) continue;
            // 石頭階梯旁、比石頭低的坑壁抓不住
            if (SlipperySlope.Instance != null && SlipperySlope.Instance.IsSlippery(surface, position)) continue;

            if (surface.CompareTag(ClimbableTag))
                touchingClimbable = true;
            else
                touchingGround = true;
        }
    }

    private Vector3 CalculateBodyMove(Hand hand, float deltaTime)
    {
        // 用 TrackingSpace 的本地座標，身體移動才不會被算成手的移動
        Vector3 localPosition = trackingSpace.InverseTransformPoint(hand.position);
        Vector3 localDelta = localPosition - hand.previousLocalPosition;
        bool hadPreviousPosition = hand.hasPreviousPosition;
        if (hadPreviousPosition) hand.localVelocity = Vector3.Lerp(hand.localVelocity, localDelta / deltaTime, 0.5f);

        hand.previousLocalPosition = localPosition;
        hand.hasPreviousPosition = true;

        if (!hadPreviousPosition || !hand.isGrabbing) return Vector3.zero;

        Vector3 handDelta = trackingSpace.TransformVector(localDelta);
        if (handDelta.magnitude / deltaTime > maxHandSpeed * SizeScale) return Vector3.zero;

        Vector3 move = -handDelta * pullGain;
        if (hand.onClimbable) return move;

        move.y = 0f;
        if (!allowBackwardOnGround)
        {
            Vector3 forward = Vector3.ProjectOnPlane(cameraTransform.forward, Vector3.up).normalized;
            float forwardAmount = Vector3.Dot(move, forward);
            if (forwardAmount < 0f)
                move -= forward * forwardAmount;
        }

        return move;
    }

    private void ResetHand(Hand hand)
    {
        if (hand == null) return;

        hand.hasPreviousPosition = false;
        hand.isGrabbing = false;
        hand.onClimbable = false;
    }
}
