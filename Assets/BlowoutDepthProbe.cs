using UnityEngine;
using Meta.XR; // 若編譯說找不到 EnvironmentRaycastManager，改成 using Meta.XR.MRUtilityKit;

public class BlowoutDepthProbe : MonoBehaviour
{
    [Header("引用")]
    [SerializeField] EnvironmentRaycastManager raycastManager;
    [SerializeField] Transform head;      // 拖 CenterEyeAnchor
    [SerializeField] Transform marker;    // 拖 TipMarker（選填）

    [Header("掃描範圍（度）")]
    [SerializeField] float yawHalfRange = 15f;   // 左右各幾度
    [SerializeField] float pitchDownMax = 30f;   // 往下最多幾度
    [SerializeField, Range(3, 15)] int columns = 7;
    [SerializeField, Range(3, 15)] int rows = 7;
    [SerializeField, Range(1, 6)] int frameInterval = 2; // 每幾幀掃一次

    [Header("距離篩選（公尺）")]
    [SerializeField] float minDistance = 0.15f;
    [SerializeField] float maxDistance = 0.30f;

    [Header("長度換算（先用預設，量完再改）")]
    [SerializeField] float retractedDistance = 0.20f;
    [SerializeField] float extendedDistance = 0.30f;

    public float TipDistance { get; private set; }
    public int NearHitCount { get; private set; }
    public float Length01 { get; private set; }

    float nextLog;

    void Update()
    {
        if (raycastManager == null || head == null) return;
        if (Time.frameCount % frameInterval != 0) return;

        Vector3 origin = head.position;
        float best = 0f;
        Vector3 bestPoint = Vector3.zero;
        int count = 0;

        for (int r = 0; r < rows; r++)
        {
            float pitch = (rows == 1 ? 0f : (float)r / (rows - 1)) * pitchDownMax;
            for (int c = 0; c < columns; c++)
            {
                float t = columns == 1 ? 0.5f : (float)c / (columns - 1);
                float yaw = Mathf.Lerp(-yawHalfRange, yawHalfRange, t);
                Vector3 dir = head.rotation * Quaternion.Euler(pitch, yaw, 0f) * Vector3.forward;

                if (raycastManager.Raycast(new Ray(origin, dir), out var hit, maxDistance))
                {
                    float d = Vector3.Distance(origin, hit.point);
                    if (d < minDistance || d > maxDistance) continue;
                    count++;
                    if (d > best) { best = d; bestPoint = hit.point; }
                }
            }
        }

        NearHitCount = count;
        TipDistance = count > 0 ? best : 0f;
        Length01 = count > 0 ? Mathf.InverseLerp(retractedDistance, extendedDistance, best) : 0f;

        if (marker != null)
        {
            marker.gameObject.SetActive(count > 0);
            if (count > 0) marker.position = bestPoint;
        }

        if (Time.unscaledTime >= nextLog)
        {
            nextLog = Time.unscaledTime + 0.5f;
            Debug.Log("[BlowoutProbe] near=" + count + ", tip(cm)=" + (best * 100f).ToString("F1")
                + ", len01=" + Length01.ToString("F2")
                + ", supported=" + EnvironmentRaycastManager.IsSupported);
        }
    }
}