using UnityEngine;
using TMPro;

public class SprayInput : MonoBehaviour
{
    public Transform head;              // CenterEyeAnchor
    public TextMeshProUGUI sprayText;   // SprayText

    public float swingThreshold = 120f; // 向上角速度門檻（度/秒）
    public float verticalRatio = 1.5f;  // 向上速度要大於左右轉速的幾倍
    public float window = 0.3f;         // 按鍵與擺頭容許的時間差（秒）
    public float cooldown = 1.0f;       // 觸發後的冷卻時間
    public float smoothTime = 0.05f;    // 速度平滑時間（秒）

    float prevPitch, prevYaw;
    bool first = true;
    float upVel, yawVel;
    float recentPeak, recentPeakTime = -99f;
    float lastPressPeak;
    float lastSwingTime = -99f, lastPressTime = -99f, lastFireTime = -99f;
    float swingUp, swingYaw;
    int fireCount;

    void Update()
    {
        Vector3 f = head.forward;
        float pitch = Mathf.Asin(Mathf.Clamp(f.y, -1f, 1f)) * Mathf.Rad2Deg;
        float yaw = Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg;
        if (first) { prevPitch = pitch; prevYaw = yaw; first = false; }

        float dt = Mathf.Max(Time.deltaTime, 0.001f);
        float rawUp = (pitch - prevPitch) / dt;
        float rawYaw = Mathf.DeltaAngle(prevYaw, yaw) / dt;
        prevPitch = pitch;
        prevYaw = yaw;

        // 低通平滑，稀釋單幀尖峰
        float k = 1f - Mathf.Exp(-dt / smoothTime);
        upVel += (rawUp - upVel) * k;
        yawVel += (rawYaw - yawVel) * k;

        // 最近 0.5 秒內的向上速度峰值，方便對照
        if (upVel > recentPeak || Time.time - recentPeakTime > 0.5f)
        {
            recentPeak = Mathf.Max(upVel, 0f);
            recentPeakTime = Time.time;
        }

        // 向上夠快，而且明顯大於左右轉，才算一次擺頭
        if (upVel > swingThreshold && upVel > Mathf.Abs(yawVel) * verticalRatio)
        {
            lastSwingTime = Time.time;
            swingUp = upVel;
            swingYaw = yawVel;
        }

        // 右手 A 鍵
        if (OVRInput.GetDown(OVRInput.Button.One, OVRInput.Controller.RTouch))
        {
            lastPressTime = Time.time;
            lastPressPeak = recentPeak;
            Debug.Log("press  peak " + recentPeak.ToString("F0")
                + "  yaw " + yawVel.ToString("F0"));
        }

        if (lastSwingTime > -1f && lastPressTime > -1f
            && Mathf.Abs(lastSwingTime - lastPressTime) <= window
            && Time.time - lastFireTime > cooldown)
        {
            fireCount++;
            lastFireTime = Time.time;
            Debug.Log("SPRAY #" + fireCount
                + "  up " + swingUp.ToString("F0")
                + "  yaw " + swingYaw.ToString("F0"));
            lastSwingTime = -99f;
            lastPressTime = -99f;
        }

        if (sprayText != null)
        {
            bool flash = Time.time - lastFireTime < 0.5f;
            sprayText.text = (flash ? "SPRAY!\n" : "")
                + "up " + upVel.ToString("F0") + "  yaw " + yawVel.ToString("F0")
                + "\npk " + lastPressPeak.ToString("F0")
                + "\nfired " + fireCount;
        }
    }
}