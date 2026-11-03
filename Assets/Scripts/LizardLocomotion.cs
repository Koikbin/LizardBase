using UnityEngine;

public class LizardLocomotion : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private CharacterController characterController;
    [SerializeField] private Transform leftHandAnchor;
    [SerializeField] private Transform rightHandAnchor;
    [SerializeField] private Transform cameraTransform;

    [Header("Locomotion Settings")]
    [SerializeField] private float speedMultiplier = 4f;
    [SerializeField] private float gravity = -9.8f;
    [SerializeField] private float pullThreshold = 0.3f;   // 往後拉的速度門檻
    [SerializeField] private float reachThreshold = 0.2f;  // 往前伸的速度門檻
    [SerializeField] private float momentum = 0.85f;        // 推進力衰減（0~1，越大滑越遠）

    [Header("Debug")]
    [SerializeField] private bool useKeyboardTest = false;

    // 每隻手的狀態
    private enum HandState { Idle, Reaching, Pulling }
    private HandState leftState  = HandState.Idle;
    private HandState rightState = HandState.Idle;

    private Vector3 prevLeftPos;
    private Vector3 prevRightPos;
    private Vector3 currentVelocity = Vector3.zero; // 角色當前推進速度
    private float verticalVelocity  = 0f;
    private bool isInitialized      = false;

    void Start()
    {
        StartCoroutine(InitNextFrame());
    }

    private System.Collections.IEnumerator InitNextFrame()
    {
        yield return null;
        prevLeftPos  = leftHandAnchor.position;
        prevRightPos = rightHandAnchor.position;
        isInitialized = true;
    }

    void Update()
    {
        if (!isInitialized) return;

        // ── 重力 ──
        if (characterController.isGrounded)
            verticalVelocity = -1f;
        else
            verticalVelocity += gravity * Time.deltaTime;

        if (useKeyboardTest)
        {
            KeyboardTest();
            return;
        }

        // ── 計算雙手這幀速度 ──
        Vector3 leftVel  = (leftHandAnchor.position  - prevLeftPos)  / Time.deltaTime;
        Vector3 rightVel = (rightHandAnchor.position - prevRightPos) / Time.deltaTime;
        prevLeftPos  = leftHandAnchor.position;
        prevRightPos = rightHandAnchor.position;

        // ── 相機方向 ──
        Vector3 camForward = Vector3.ProjectOnPlane(cameraTransform.forward, Vector3.up).normalized;

        // ── 偵測每隻手的動作 ──
        ProcessHand(leftVel,  camForward, ref leftState,  "左手");
        ProcessHand(rightVel, camForward, ref rightState, "右手");

        // ── 推進力衰減（模擬摩擦力）──
        currentVelocity *= momentum;
        if (currentVelocity.magnitude < 0.01f)
            currentVelocity = Vector3.zero;

        // ── 移動角色 ──
        Vector3 finalMove = currentVelocity + Vector3.up * verticalVelocity;
        characterController.Move(finalMove * Time.deltaTime);

        // Debug
        if (currentVelocity.magnitude > 0.01f)
            Debug.Log($"爬行速度: {currentVelocity.magnitude:F2} | 左手:{leftState} 右手:{rightState}");
    }

    private void ProcessHand(Vector3 handVel, Vector3 camForward, ref HandState state, string handName)
    {
        // 把手速度投影到相機前方軸（正 = 往前伸，負 = 往後拉）
        float forwardComponent = Vector3.Dot(handVel, camForward);

        switch (state)
        {
            case HandState.Idle:
                // 手往前伸 → 進入 Reaching 狀態
                if (forwardComponent > reachThreshold)
                {
                    state = HandState.Reaching;
                    Debug.Log($"{handName} 往前伸（抓地）");
                }
                break;

            case HandState.Reaching:
                // 手開始往後拉 → 進入 Pulling 狀態（產生推進力）
                if (forwardComponent < -pullThreshold)
                {
                    state = HandState.Pulling;

                    // 推進力 = 拉的速度大小 × 倍率，方向 = 相機前方
                    Vector3 push = camForward * Mathf.Abs(forwardComponent) * speedMultiplier;
                    currentVelocity += push;
                    Debug.Log($"{handName} 往後拉（推進！）力道:{Mathf.Abs(forwardComponent):F2}");
                }
                break;

            case HandState.Pulling:
                // 手速度接近0 → 回到 Idle，準備下一次爬行
                if (handVel.magnitude < 0.1f)
                {
                    state = HandState.Idle;
                }
                break;
        }
    }

    private void KeyboardTest()
    {
        // 鍵盤測試：按一下W產生一次推進力（模擬一次爬行）
        var keyboard = UnityEngine.InputSystem.Keyboard.current;
        if (keyboard == null) return;

        Vector3 camForward = Vector3.ProjectOnPlane(cameraTransform.forward, Vector3.up).normalized;
        Vector3 camRight   = Vector3.ProjectOnPlane(cameraTransform.right,   Vector3.up).normalized;

        if (keyboard.wKey.wasPressedThisFrame)
            currentVelocity += camForward * speedMultiplier;
        if (keyboard.sKey.wasPressedThisFrame)
            currentVelocity -= camForward * speedMultiplier;
        if (keyboard.aKey.wasPressedThisFrame)
            currentVelocity -= camRight * speedMultiplier;
        if (keyboard.dKey.wasPressedThisFrame)
            currentVelocity += camRight * speedMultiplier;

        // 衰減
        currentVelocity *= momentum;

        Vector3 finalMove = currentVelocity + Vector3.up * verticalVelocity;
        characterController.Move(finalMove * Time.deltaTime);
    }
}