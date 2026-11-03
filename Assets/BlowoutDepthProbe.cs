
using Meta.XR; 
using UnityEngine;

// 用深度射線網量「嘴前方多遠有東西」,當作吹龍龍尾的位置。
// 掛在 DepthProbe,和 EnvironmentRaycastManager 同一個物件。
// Head 要指向目前場景的 CenterEyeAnchor。
public class BlowoutDepthProbe : MonoBehaviour
{
    public Transform head;
    public EnvironmentRaycastManager raycastManager;

    [Header("射線網")]
    public int grid = 7;
    public float halfYawDeg = 25f;      // 左右各幾度
    public float maxPitchDeg = 50f;     // 往下 0 到幾度
    public int everyNFrames = 2;

    [Header("有效距離(公尺)")]
    public float minDist = 0.15f;
    public float maxDist = 0.60f;

    [Header("對應成 0~1")]
    public float retracted = 0.20f;
    public float extended = 0.45f;

    public float TipDistance { get; private set; }
    public int NearHitCount { get; private set; }
    public float Length01 { get; private set; }

    int frame;

    void Reset()
    {
        raycastManager = GetComponent<EnvironmentRaycastManager>();
    }

    void Update()
    {
        if (head == null || raycastManager == null) return;
        if (++frame % Mathf.Max(1, everyNFrames) != 0) return;

        Vector3 origin = head.position;
        float far = 0f;
        int count = 0;

        for (int i = 0; i < grid; i++)
        {
            float yaw = grid == 1 ? 0 : Mathf.Lerp(-halfYawDeg, halfYawDeg, i / (float)(grid - 1));
            for (int j = 0; j < grid; j++)
            {
                float pitch = grid == 1 ? 0 : Mathf.Lerp(0, maxPitchDeg, j / (float)(grid - 1)); // 正值 = 往下
                Vector3 dir = head.rotation * Quaternion.Euler(pitch, yaw, 0) * Vector3.forward;
                var ray = new Ray(origin, dir);

                // 憑印象:Raycast(Ray, out EnvironmentRaycastHit, float maxDistance) 回傳 bool
                if (raycastManager.Raycast(ray, out var hit, maxDist))
                {
                    float d = Vector3.Distance(origin, hit.point);
                    if (d >= minDist && d <= maxDist)
                    {
                        count++;
                        if (d > far) far = d;
                    }
                }
            }
        }

        NearHitCount = count;
        TipDistance = far;
        Length01 = count == 0 ? 0f : Mathf.InverseLerp(retracted, extended, far);
    }
}