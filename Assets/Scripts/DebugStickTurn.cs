using UnityEngine;

// 偵錯用:右手搖桿左右推 = 原地轉向(以頭為中心轉,不會被甩出去)。
// 掛在 Player(有 CharacterController 的最上層物件)。正式版不需要時取消勾選 Enable Turn 或拿掉這個元件。
public class DebugStickTurn : MonoBehaviour
{
    [Tooltip("打勾 = 右手搖桿可以轉向")]
    public bool enableTurn = true;
    [Tooltip("打勾 = 一格一格轉(推一下轉 Snap Degrees);不勾 = 推著就連續轉")]
    public bool snapTurn = false;
    [Tooltip("連續轉向的速度(度/秒,搖桿推到底時)")]
    public float turnSpeedDeg = 90f;
    [Tooltip("一格轉幾度")]
    public float snapDegrees = 30f;
    [Tooltip("搖桿推超過這個值才轉")]
    [Range(0f, 1f)] public float deadZone = 0.25f;
    [Tooltip("轉向的中心(頭)。留空用主攝影機")]
    public Transform head;

    bool snapArmed = true;

    void Update()
    {
        if (!enableTurn) return;
        float x = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, OVRInput.Controller.RTouch).x;

        float angle = 0f;
        if (snapTurn)
        {
            if (Mathf.Abs(x) < deadZone * 0.5f) snapArmed = true;
            else if (snapArmed && Mathf.Abs(x) > deadZone)
            {
                angle = Mathf.Sign(x) * snapDegrees;
                snapArmed = false;
            }
        }
        else if (Mathf.Abs(x) > deadZone)
        {
            // 扣掉死區後重新換算成 0~1,轉起來比較順
            float t = (Mathf.Abs(x) - deadZone) / (1f - deadZone);
            angle = Mathf.Sign(x) * t * turnSpeedDeg * Time.deltaTime;
        }

        if (angle == 0f) return;
        if (head == null && Camera.main != null) head = Camera.main.transform;
        Vector3 pivot = head != null ? head.position : transform.position;
        transform.RotateAround(pivot, Vector3.up, angle);
        Physics.SyncTransforms();   // Player 有 CharacterController,直接改 Transform 後要同步給物理系統
    }
}
