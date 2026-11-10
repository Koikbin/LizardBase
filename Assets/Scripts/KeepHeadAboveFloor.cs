using UnityEngine;

// 掛在 OVRCameraRig 上。
// 攝影機架比玩家腳底低(目前 -0.83 公尺),坐著、蹲下時頭的高度可能比這個值還低,視角就會鑽到地面下。
// 這裡只在頭太低時把攝影機架往上抬,讓視角停在地面上方;站著或高度足夠時完全不影響。
public class KeepHeadAboveFloor : MonoBehaviour
{
    [Tooltip("CenterEyeAnchor")]
    public Transform head;
    [Tooltip("頭離玩家腳底至少多高(公尺)。低於這個高度時視角就不再往下")]
    public float minHeadHeight = 0.3f;

    float baseLocalY;

    void Awake()
    {
        baseLocalY = transform.localPosition.y;
    }

    void LateUpdate()
    {
        if (head == null) return;

        // 頭相對攝影機架的高度,換算到父物件(Player)的單位
        float headAboveRig = transform.InverseTransformPoint(head.position).y * transform.localScale.y;
        float headHeight = baseLocalY + headAboveRig;

        Vector3 local = transform.localPosition;
        local.y = baseLocalY + Mathf.Max(0f, minHeadHeight - headHeight);
        transform.localPosition = local;
    }
}
