using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

// 沒有頭顯時,用鍵盤假裝吹龍與雙手外展。
// 按住 Space = 吹龍(Blow01 慢慢升),放開慢慢降。
// 按住 E = 雙手外展(Expand01 慢慢升)。
// WASD 移動,按住滑鼠右鍵轉視角。J = 跳(模擬兩手往下推),R = 遊戲結束後重新開始。
// 模擬手(給 LizardLocomotion 用):按住滑鼠左鍵 = 左手、按住 F = 右手,手會放到游標指到的表面上,往下拖就把身體往前/往上拉。
// 掛在任何一個物件上即可(例如 Systems 底下新建一個 DebugInput 物件)。
// 勾掉 useKeyboard 就回到真實輸入。
public class DebugInput : MonoBehaviour
{
    public static bool Active;
    public static float Blow01;
    public static float Expand01;
    public static bool SimulateHands;
    public static bool LeftHand;
    public static bool RightHand;
    public static Vector2 PointerPosition;   // 滑鼠在螢幕上的位置(像素)
    public static bool JumpDown;             // 這一幀按下 J
    public static bool RestartDown;          // 這一幀按下 R

    public bool useKeyboard = true;
    [Tooltip("偵測到頭顯(含 Quest Link)時自動停用鍵盤輸入,否則實機上舌頭會一直讀鍵盤而無法用扳機/吹龍")]
    public bool disableWhenHeadsetActive = true;
    public bool simulateHands = true;   // 沒頭顯時用滑鼠模擬手來測試爬行
    public Transform moveRoot;      // 要移動的物件(有 CharacterController 時會用 Move,不會穿牆)
    [Tooltip("上下看要轉哪個物件(建議 TrackingSpace)。沒指定就和左右轉一起套在 Move Root 上")]
    public Transform pitchRoot;
    [Tooltip("沒頭顯時相機高度(公尺,套在 pitchRoot 上)。Floor Level 的 rig 沒頭顯時相機會在地板上")]
    public float simulatedEyeHeight = 0.6f;
    public float riseSpeed = 1.2f;  // 每秒上升量
    public float fallSpeed = 1.8f;  // 每秒下降量
    public float moveSpeed = 2f;
    public float lookSpeed = 2f;

    float yaw, pitch;
    CharacterController moveController;

    bool ShouldBeActive => useKeyboard
        && !(disableWhenHeadsetActive && UnityEngine.XR.XRSettings.isDeviceActive);

    void OnEnable()
    {
        Active = ShouldBeActive;
        if (moveRoot != null)
        {
            moveController = moveRoot.GetComponent<CharacterController>();
            yaw = moveRoot.eulerAngles.y;
        }
    }
    void OnDisable() { Active = false; Blow01 = 0; Expand01 = 0; ClearHands(); }

    void Update()
    {
        Active = ShouldBeActive;
        if (!Active) { ClearHands(); Blow01 = 0; Expand01 = 0; JumpDown = RestartDown = false; return; }
        JumpDown = KeyDown(KeyCode.J);
        RestartDown = KeyDown(KeyCode.R);

        if (pitchRoot != null) pitchRoot.localPosition = Vector3.up * simulatedEyeHeight;

        Blow01 = Step(Blow01, KeyHeld(KeyCode.Space));
        Expand01 = Step(Expand01, KeyHeld(KeyCode.E));

        SimulateHands = simulateHands;
        LeftHand = simulateHands && LeftMouseHeld();
        RightHand = simulateHands && KeyHeld(KeyCode.F);
        PointerPosition = MousePosition();

        if (moveRoot != null)
        {
            Vector2 m = MoveAxis();
            Vector3 f = moveRoot.forward; f.y = 0; f.Normalize();
            Vector3 r = moveRoot.right; r.y = 0; r.Normalize();
            Vector3 delta = (f * m.y + r * m.x) * moveSpeed * Time.deltaTime;
            if (moveController != null && moveController.enabled) moveController.Move(delta);
            else moveRoot.position += delta;

            if (RightMouseHeld())
            {
                Vector2 d = MouseDelta() * lookSpeed * 0.1f;
                yaw += d.x;
                pitch = Mathf.Clamp(pitch - d.y, -80f, 80f);
                if (pitchRoot != null)
                {
                    // 身體只左右轉,保持直立;上下看交給 pitchRoot
                    moveRoot.rotation = Quaternion.Euler(0, yaw, 0);
                    pitchRoot.localRotation = Quaternion.Euler(pitch, 0, 0);
                }
                else
                {
                    moveRoot.rotation = Quaternion.Euler(pitch, yaw, 0);
                }
            }
        }
    }

    float Step(float v, bool held)
    {
        return Mathf.Clamp01(v + (held ? riseSpeed : -fallSpeed) * Time.deltaTime);
    }

    static void ClearHands() { SimulateHands = false; LeftHand = false; RightHand = false; }

#if ENABLE_INPUT_SYSTEM
    bool KeyHeld(KeyCode k)
    {
        var kb = Keyboard.current; if (kb == null) return false;
        if (k == KeyCode.Space) return kb.spaceKey.isPressed;
        if (k == KeyCode.E) return kb.eKey.isPressed;
        if (k == KeyCode.F) return kb.fKey.isPressed;
        return false;
    }
    bool LeftMouseHeld() { return Mouse.current != null && Mouse.current.leftButton.isPressed; }
    bool KeyDown(KeyCode k)
    {
        var kb = Keyboard.current; if (kb == null) return false;
        if (k == KeyCode.J) return kb.jKey.wasPressedThisFrame;
        if (k == KeyCode.R) return kb.rKey.wasPressedThisFrame;
        return false;
    }
    Vector2 MousePosition() { return Mouse.current != null ? Mouse.current.position.ReadValue() : Vector2.zero; }
    Vector2 MoveAxis()
    {
        var kb = Keyboard.current; if (kb == null) return Vector2.zero;
        float x = (kb.dKey.isPressed ? 1 : 0) - (kb.aKey.isPressed ? 1 : 0);
        float y = (kb.wKey.isPressed ? 1 : 0) - (kb.sKey.isPressed ? 1 : 0);
        return new Vector2(x, y);
    }
    bool RightMouseHeld() { return Mouse.current != null && Mouse.current.rightButton.isPressed; }
    Vector2 MouseDelta() { return Mouse.current != null ? Mouse.current.delta.ReadValue() : Vector2.zero; }
#else
    bool KeyHeld(KeyCode k) { return Input.GetKey(k); }
    bool KeyDown(KeyCode k) { return Input.GetKeyDown(k); }
    Vector2 MoveAxis() { return new Vector2(Input.GetAxisRaw("Horizontal"), Input.GetAxisRaw("Vertical")); }
    bool RightMouseHeld() { return Input.GetMouseButton(1); }
    bool LeftMouseHeld() { return Input.GetMouseButton(0); }
    Vector2 MousePosition() { return Input.mousePosition; }
    Vector2 MouseDelta() { return new Vector2(Input.GetAxis("Mouse X"), Input.GetAxis("Mouse Y")) * 10f; }
#endif
}