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

    float minD, maxD;
    float velocity;
    int state;                        // 0 = 等待併攏, 1 = 等待外展, 2 = 運作中
    bool placed;

    void Start()
    {
        if (PlayerPrefs.HasKey("minD") && PlayerPrefs.HasKey("maxD"))
        {
            minD = PlayerPrefs.GetFloat("minD");
            maxD = PlayerPrefs.GetFloat("maxD");
            state = 2;
        }
    }

    void Update()
    {
        // 用手相對於追蹤空間的本地座標，不受 rig 縮放影響
        distance = Vector3.Distance(leftHand.localPosition, rightHand.localPosition);

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
        rig.localScale = Vector3.one * s;
        Vector3 after = head.position;

        Vector3 p = rig.position;
        p.x += before.x - after.x;
        p.z += before.z - after.z;
        rig.position = p;
    }

    void Show(string s)
    {
        if (display != null) display.text = s;
    }
}