using UnityEngine;

/// 手抓住表面後，手相對身體移動多少，身體就往反方向移動多少。
/// 一般表面（地面）：手碰到就抓住，只能水平爬行。
/// Tag 為 Climbable 的表面（牆）：手碰到並按住 Grip 才抓住，可以往任何方向爬，抓住時不受重力影響，放開 Grip 才會放手。
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

    [Header("Debug")]
    [SerializeField] private bool logGrabEvents = true;

    private class Hand
    {
        public string name;
        public Transform anchor;
        public Vector3 previousLocalPosition;
        public bool hasPreviousPosition;
        public bool isGrabbing;
        public bool onClimbable;
    }

    private readonly Collider[] overlapResults = new Collider[16];
    private Hand leftHand;
    private Hand rightHand;
    private float verticalVelocity;

    public bool IsClimbing => (leftHand != null && leftHand.onClimbable)
                           || (rightHand != null && rightHand.onClimbable);

    private void Awake()
    {
        leftHand = new Hand { name = "左手", anchor = leftHandAnchor };
        rightHand = new Hand { name = "右手", anchor = rightHandAnchor };
    }

    private void OnEnable()
    {
        verticalVelocity = 0f;
        ResetHand(leftHand);
        ResetHand(rightHand);
    }

    private void OnDisable()
    {
        ResetHand(leftHand);
        ResetHand(rightHand);
    }

    private void Update()
    {
        if (Time.deltaTime <= 0f) return;

        bool leftGrip = OVRInput.Get(OVRInput.Button.PrimaryHandTrigger, OVRInput.Controller.LTouch);
        bool rightGrip = OVRInput.Get(OVRInput.Button.PrimaryHandTrigger, OVRInput.Controller.RTouch);
        Tick(Time.deltaTime, leftGrip, rightGrip);
    }

    private void Tick(float deltaTime, bool leftGrip, bool rightGrip)
    {
        UpdateGrab(leftHand, leftGrip);
        UpdateGrab(rightHand, rightGrip);

        Vector3 leftMove = CalculateBodyMove(leftHand, deltaTime);
        Vector3 rightMove = CalculateBodyMove(rightHand, deltaTime);

        // 兩手都抓住時採用移動較多的那隻，這樣放著不動的手不會讓另一隻手的拉動減半
        Vector3 bodyMove = leftMove.sqrMagnitude >= rightMove.sqrMagnitude ? leftMove : rightMove;

        if (IsClimbing)
            verticalVelocity = 0f;
        else if (characterController.isGrounded)
            verticalVelocity = -1f;
        else
            verticalVelocity += gravity * deltaTime;

        characterController.Move(bodyMove + Vector3.up * (verticalVelocity * deltaTime));
    }

    private void UpdateGrab(Hand hand, bool gripHeld)
    {
        float radius = hand.isGrabbing ? releaseRadius : grabRadius;
        FindSurfaces(hand.anchor.position, radius, out bool touchingGround, out bool touchingClimbable);

        // 抓住牆後只要 Grip 還按著就不會放手，即使手暫時離開牆面
        bool onClimbable = gripHeld && (touchingClimbable || hand.onClimbable);
        bool isGrabbing = onClimbable || touchingGround;

        if (logGrabEvents && (isGrabbing != hand.isGrabbing || onClimbable != hand.onClimbable))
            Debug.Log(isGrabbing ? $"{hand.name} 抓住（{(onClimbable ? "牆面" : "地面")}）" : $"{hand.name} 放開");

        hand.isGrabbing = isGrabbing;
        hand.onClimbable = onClimbable;
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

            if (surface.CompareTag(ClimbableTag))
                touchingClimbable = true;
            else
                touchingGround = true;
        }
    }

    private Vector3 CalculateBodyMove(Hand hand, float deltaTime)
    {
        // 用 TrackingSpace 的本地座標，身體移動才不會被算成手的移動
        Vector3 localPosition = trackingSpace.InverseTransformPoint(hand.anchor.position);
        Vector3 localDelta = localPosition - hand.previousLocalPosition;
        bool hadPreviousPosition = hand.hasPreviousPosition;

        hand.previousLocalPosition = localPosition;
        hand.hasPreviousPosition = true;

        if (!hadPreviousPosition || !hand.isGrabbing) return Vector3.zero;

        Vector3 handDelta = trackingSpace.TransformVector(localDelta);
        if (handDelta.magnitude / deltaTime > maxHandSpeed) return Vector3.zero;

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
