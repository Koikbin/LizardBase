using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

// 沒有頭顯時,用鍵盤假裝吹龍與雙手外展。
// 按住 Space = 吹龍(Blow01 慢慢升),放開慢慢降。
// 按住 E = 雙手外展(Expand01 慢慢升)。
// WASD 移動,按住滑鼠右鍵轉視角。
// 掛在任何一個物件上即可(例如 Systems 底下新建一個 DebugInput 物件)。
// 勾掉 useKeyboard 就回到真實輸入。
public class DebugInput : MonoBehaviour
{
    public static bool Active;
    public static float Blow01;
    public static float Expand01;

    public bool useKeyboard = true;
    public Transform moveRoot;      // 要移動的物件(沒頭顯時可指向 OVRCameraRig)
    public float riseSpeed = 1.2f;  // 每秒上升量
    public float fallSpeed = 1.8f;  // 每秒下降量
    public float moveSpeed = 2f;
    public float lookSpeed = 2f;

    float yaw, pitch;

    void OnEnable() { Active = useKeyboard; }
    void OnDisable() { Active = false; Blow01 = 0; Expand01 = 0; }

    void Update()
    {
        Active = useKeyboard;
        if (!useKeyboard) return;

        Blow01 = Step(Blow01, KeyHeld(KeyCode.Space));
        Expand01 = Step(Expand01, KeyHeld(KeyCode.E));

        if (moveRoot != null)
        {
            Vector2 m = MoveAxis();
            Vector3 f = moveRoot.forward; f.y = 0; f.Normalize();
            Vector3 r = moveRoot.right; r.y = 0; r.Normalize();
            moveRoot.position += (f * m.y + r * m.x) * moveSpeed * Time.deltaTime;

            if (RightMouseHeld())
            {
                Vector2 d = MouseDelta() * lookSpeed * 0.1f;
                yaw += d.x;
                pitch = Mathf.Clamp(pitch - d.y, -80f, 80f);
                moveRoot.rotation = Quaternion.Euler(pitch, yaw, 0);
            }
        }
    }

    float Step(float v, bool held)
    {
        return Mathf.Clamp01(v + (held ? riseSpeed : -fallSpeed) * Time.deltaTime);
    }

#if ENABLE_INPUT_SYSTEM
    bool KeyHeld(KeyCode k)
    {
        var kb = Keyboard.current; if (kb == null) return false;
        if (k == KeyCode.Space) return kb.spaceKey.isPressed;
        if (k == KeyCode.E) return kb.eKey.isPressed;
        return false;
    }
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
    Vector2 MoveAxis() { return new Vector2(Input.GetAxisRaw("Horizontal"), Input.GetAxisRaw("Vertical")); }
    bool RightMouseHeld() { return Input.GetMouseButton(1); }
    Vector2 MouseDelta() { return new Vector2(Input.GetAxis("Mouse X"), Input.GetAxis("Mouse Y")) * 10f; }
#endif
}