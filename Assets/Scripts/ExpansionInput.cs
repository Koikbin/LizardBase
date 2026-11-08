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

    [Tooltip("玩家的基本大小(OVRCameraRig 的縮放)。0.25 = 世界看起來大 4 倍;膨脹是在這個大小上再乘 1~Max Scale")]
    public float baseScale = 1f;
    public float maxScale = 2f;
    [Tooltip("在視野裡顯示膨脹的數值(除錯用)。關掉時只有按左手 X 重新校準時才顯示校準提示")]
    public bool showDisplay = false;
    [Tooltip("沒校準過時用的預設值:兩手併攏 / 張開的距離(公尺)")]
    public float defaultMinD = 0.2f;
    public float defaultMaxD = 1.0f;
    public float deadZone = 0.05f;
    public float smoothTime = 0.08f;
    public float followSpeed = 6f;

    public float distance;
    public float scale = 1f;

    float minD, maxD;
    float velocity;
    Vector3 displayBaseScale;
    bool displayScales;   // Canvas 不在 rig 底下時,要自己跟著玩家大小縮放
    int state;                        // 0 = 等待併攏, 1 = 等待外展, 2 = 運作中
    bool placed;

    // 校準中右扳機是「確認」鍵,TongueTrigger 會參考這個值,避免校準時誤伸舌頭
    public bool IsCalibrating => isActiveAndEnabled && state != 2;

    public float RigScale => baseScale * scale;

    void Start()
    {
        if (displayRoot != null)
        {
            displayBaseScale = displayRoot.localScale;
            displayScales = rig == null || !displayRoot.IsChildOf(rig);
        }
        if (rig != null && head != null) ApplyRigScale(scale);

        if (PlayerPrefs.HasKey("minD") && PlayerPrefs.HasKey("maxD"))
        {
            minD = PlayerPrefs.GetFloat("minD");
            maxD = PlayerPrefs.GetFloat("maxD");
            state = 2;
        }
        else
        {
            // 沒校準過:直接用預設值開始,不擋在眼前要人校準(要校準就按左手 X)
            minD = defaultMinD;
            maxD = defaultMaxD;
            state = 2;
        }
        if (!showDisplay) Show("");
    }

    void Update()
    {
        // 用手相對於追蹤空間的本地座標，不受 rig 縮放影響
        distance = Vector3.Distance(leftHand.localPosition, rightHand.localPosition);

        // 文字跟著頭走
        if (displayRoot != null && Time.time > 2f)
        {
            float rs = RigScale;
            if (displayScales) displayRoot.localScale = displayBaseScale * rs;
            Vector3 goalPos = head.position + (head.forward * 1.0f - head.up * 0.25f) * rs;
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
        if (recal) state = 0;

        if (state == 0)
        {
            Show("Hands together\nright trigger\nd " + distance.ToString("F2"));
            if (trigger) { minD = distance; state = 1; }
        }
        else if (state == 1)
        {
            Show("Hands apart\nright trigger\nd " + distance.ToString("F2"));
            if (trigger)
            {
                maxD = distance;
                if (maxD - minD < 0.2f) { state = 0; }
                else
                {
                    PlayerPrefs.SetFloat("minD", minD);
                    PlayerPrefs.SetFloat("maxD", maxD);
                    state = 2;
                }
            }
        }
        else
        {
            float t = Mathf.Clamp01(Mathf.InverseLerp(minD, maxD, distance));
            if (t < deadZone) t = 0f;
            float targetScale = Mathf.Lerp(1f, maxScale, t);
            scale = Mathf.SmoothDamp(scale, targetScale, ref velocity, smoothTime);

            ApplyRigScale(scale);

            Show("d " + distance.ToString("F2")
                + "\nx" + scale.ToString("F2")
                + "\neyes " + RenderedSeparation().ToString("F3"));
        }
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
        rig.localScale = Vector3.one * (baseScale * s);
        Vector3 after = head.position;

        Vector3 p = rig.position;
        p.x += before.x - after.x;
        p.z += before.z - after.z;
        rig.position = p;

        // 上層的 Player 有 CharacterController,直接改 Transform 後要同步給物理系統
        Physics.SyncTransforms();
    }

    void Show(string s)
    {
        // 校準中(state 0/1)一定要看得到提示;平常的數值只有 Show Display 打勾才顯示
        if (!showDisplay && state == 2) s = "";
        if (display != null) display.text = s;
    }
}