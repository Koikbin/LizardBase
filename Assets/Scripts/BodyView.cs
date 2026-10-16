using UnityEngine;

public class BodyView : MonoBehaviour
{
    public ExpansionInput input;   // Expansion 物件上的腳本
    public Transform head;         // CenterEyeAnchor
    public Transform leftHand;     // LeftHandAnchor
    public Transform rightHand;    // RightHandAnchor
    public Transform torso;
    public Transform armL;
    public Transform armR;

    public float torsoDrop = 0.45f;   // 軀幹中心在頭下方多少公尺
    public float shoulderDrop = 0.25f;
    public float shoulderWidth = 0.2f;
    public float armThickness = 0.07f;

    void LateUpdate()
    {
        float s = input.scale;

        // 軀幹：在頭正下方，只取頭的水平轉向
        Quaternion yaw = Quaternion.Euler(0f, head.eulerAngles.y, 0f);
        torso.position = head.position - Vector3.up * torsoDrop;
        torso.rotation = yaw;
        torso.localScale = new Vector3(0.3f * s, 0.25f, 0.2f * s);

        // 肩膀位置
        Vector3 right = yaw * Vector3.right;
        Vector3 shoulderL = head.position - Vector3.up * shoulderDrop - right * shoulderWidth * s;
        Vector3 shoulderR = head.position - Vector3.up * shoulderDrop + right * shoulderWidth * s;

        PlaceArm(armL, shoulderL, leftHand.position, s);
        PlaceArm(armR, shoulderR, rightHand.position, s);
    }

    void PlaceArm(Transform arm, Vector3 from, Vector3 to, float s)
    {
        Vector3 dir = to - from;
        float len = dir.magnitude;
        if (len < 0.01f) return;
        arm.position = (from + to) * 0.5f;
        arm.rotation = Quaternion.FromToRotation(Vector3.up, dir);
        float th = armThickness * s;
        // Unity 圓柱預設高度為 2，所以長度要除以 2
        arm.localScale = new Vector3(th, len * 0.5f, th);
    }
}