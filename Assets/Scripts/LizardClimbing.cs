using UnityEngine;

public class LizardClimbing : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private CharacterController characterController;
    [SerializeField] private Transform leftHandAnchor;
    [SerializeField] private Transform rightHandAnchor;
    [SerializeField] private LizardLocomotion locomotionScript;

    [Header("Climbing Settings")]
    [SerializeField] private float climbSpeed = 3f;
    [SerializeField] private float gravity = -9.8f;

    // 內部變數
    private bool leftGripping  = false;
    private bool rightGripping = false;

    private Vector3 leftHandPrevPos;
    private Vector3 rightHandPrevPos;

    private float verticalVelocity = 0f;
    public bool IsClimbing { get; private set; } = false;

    void Update()
    {
        // ── 1. 偵測 Grip 按鈕 ──
        bool leftGrip  = OVRInput.Get(OVRInput.Button.PrimaryHandTrigger,  OVRInput.Controller.LTouch);
        bool rightGrip = OVRInput.Get(OVRInput.Button.PrimaryHandTrigger, OVRInput.Controller.RTouch);

        // ── 2. 按下瞬間：記住手的位置（錨點）──
        if (leftGrip && !leftGripping)
        {
            leftGripping     = true;
            leftHandPrevPos  = leftHandAnchor.position;
        }
        if (rightGrip && !rightGripping)
        {
            rightGripping    = true;
            rightHandPrevPos = rightHandAnchor.position;
        }

        // 放開時清除狀態
        if (!leftGrip)  leftGripping  = false;
        if (!rightGrip) rightGripping = false;

        // ── 3. 判斷是否在攀爬 ──
        IsClimbing = leftGripping || rightGripping;

        // ── 4. 攀爬移動邏輯 ──
        if (IsClimbing)
        {
            // 停用地面爬行
            locomotionScript.enabled = false;

            Vector3 totalDelta = Vector3.zero;
            int gripCount = 0;

            if (leftGripping)
            {
                // 手往上移動 → 角色往上（反向）
                Vector3 delta = leftHandAnchor.position - leftHandPrevPos;
                totalDelta -= delta;
                leftHandPrevPos = leftHandAnchor.position;
                gripCount++;
            }

            if (rightGripping)
            {
                Vector3 delta = rightHandAnchor.position - rightHandPrevPos;
                totalDelta -= delta;
                rightHandPrevPos = rightHandAnchor.position;
                gripCount++;
            }

            if (gripCount > 0)
                totalDelta /= gripCount;

            totalDelta *= climbSpeed;
            characterController.Move(totalDelta * Time.deltaTime);

            // 攀爬時重置重力
            verticalVelocity = 0f;
        }
        else
        {
            // 恢復地面爬行
            locomotionScript.enabled = true;

            // 套用重力
            if (characterController.isGrounded)
                verticalVelocity = -1f;
            else
                verticalVelocity += gravity * Time.deltaTime;

            characterController.Move(Vector3.up * verticalVelocity * Time.deltaTime);
        }
    }
}
