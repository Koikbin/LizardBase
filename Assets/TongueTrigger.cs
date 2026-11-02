using UnityEngine;

public class TongueTrigger : MonoBehaviour
{
    public Transform tip;
    public Transform[] targets;
    public float maxLength = 1.5f;
    public float catchDistance = 0.15f;

    [Header("輸入來源")]
    public BlowoutDepthProbe depthInput;   // 拖 DepthProbe
    public bool useDepth = true;           // 取消勾選就回到扳機
    [Range(0f, 30f)] public float smoothing = 12f; // 0 = 不平滑，越大越跟手但越抖

    Transform caught;
    float smoothed;

    Vector3 CenterOf(Transform t)
    {
        var rs = t.GetComponentsInChildren<Renderer>();
        if (rs.Length == 0) return t.position;
        Bounds b = rs[0].bounds;
        for (int i = 1; i < rs.Length; i++) b.Encapsulate(rs[i].bounds);
        return b.center;
    }

    float ReadInput()
    {
        if (useDepth && depthInput != null)
        {
            float raw = depthInput.Length01;
            if (smoothing <= 0f) { smoothed = raw; return raw; }
            smoothed = Mathf.Lerp(smoothed, raw, 1f - Mathf.Exp(-smoothing * Time.deltaTime));
            return smoothed;
        }
        return OVRInput.Get(OVRInput.Axis1D.PrimaryIndexTrigger, OVRInput.Controller.RTouch);
    }

    void Update()
    {
        float t = ReadInput();
        transform.localScale = new Vector3(1f, 1f, Mathf.Max(0.001f, t * maxLength));

        if (caught == null)
        {
            if (t > 0.5f)
            {
                foreach (var target in targets)
                {
                    if (target == null || !target.gameObject.activeSelf) continue;
                    if (Vector3.Distance(tip.position, CenterOf(target)) < catchDistance)
                    {
                        caught = target;
                        break;
                    }
                }
            }
        }
        else
        {
            caught.position += tip.position - CenterOf(caught);
            if (t < 0.1f)
            {
                caught.gameObject.SetActive(false);
                caught = null;
            }
        }
    }

    void OnDrawGizmosSelected()
    {
        if (tip == null) return;
        Gizmos.color = Color.yellow;
        Gizmos.DrawWireSphere(tip.position, catchDistance);
    }
}