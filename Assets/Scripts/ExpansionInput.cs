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
    public ElbowTracker elbow;        // 留空會自動找場景裡的 ElbowTracker
    public float blendTime = 0.25f;   // 手肘有效/無效切換的淡入淡出時間
    public float minElbowRange = 20f; // 校準時併攏和張開的角度差至少要這麼多度，否則不存手肘校準

    // 量測用，給顯示腳本讀
    public float distanceT;           // 手把距離換算的 0~1
    public float elbowT;              // 手肘角換算的 0~1（最後一次有效值）
    public float elbowWeight;         // 0 = 只用手把，1 = 取兩者較小值
    public bool elbowCalibrated;
    public float minA, maxA;          // 手肘校準角度（度）

    float minD, maxD;
    float velocity;
    int state;                        // 0 = 等待併攏, 1 = 等待外展, 2 = 運作中
    bool placed;
    float pendingMinA;
    bool pendingMinAValid;

    void Start()
    {
        if (PlayerPrefs.HasKey("minD") && PlayerPrefs.HasKey("maxD"))
        {
            minD = PlayerPrefs.GetFloat("minD");
            maxD = PlayerPrefs.GetFloat("maxD");
            state = 2;
        }

        // 手肘校準用另外的鍵，不動原本的 minD / maxD
        if (PlayerPrefs.HasKey("elbowMinA") && PlayerPrefs.HasKey("elbowMaxA"))
        {
            minA = PlayerPrefs.GetFloat("elbowMinA");
            maxA = PlayerPrefs.GetFloat("elbowMaxA");
            elbowCalibrated = true;
        }

        if (useElbow && elbow == null) elbow = FindAnyObjectByType<ElbowTracker>();
    }

    bool ElbowReady => useElbow && elbow != null && elbow.isValid;

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
        elbowWeight = Mathf.MoveTowards(elbowWeight, ok ? 1f : 0f, step);

        // 淡出期間用最後一次有效的 elbowT，不讀無效的角度
        return Mathf.Lerp(tCtrl, Mathf.Min(tCtrl, elbowT), elbowWeight);
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
            if (trigger)
            {
                minD = distance;
                pendingMinAValid = ElbowReady;
                if (pendingMinAValid) pendingMinA = elbow.Angle;
                state = 1;
            }
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

                    // 同時記下手肘角。兩次都有效且差距夠大才存，否則沿用舊的手肘校準
                    if (pendingMinAValid && ElbowReady && elbow.Angle - pendingMinA >= minElbowRange)
                    {
                        minA = pendingMinA;
                        maxA = elbow.Angle;
                        PlayerPrefs.SetFloat("elbowMinA", minA);
                        PlayerPrefs.SetFloat("elbowMaxA", maxA);
                        elbowCalibrated = true;
                    }
                    state = 2;
                }
            }
        }
        else
        {
            distanceT = Mathf.Clamp01(Mathf.InverseLerp(minD, maxD, distance));
            float t = Combine(distanceT);
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