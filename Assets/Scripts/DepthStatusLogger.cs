using UnityEngine;
using Meta.XR.EnvironmentDepth;

public class DepthStatusLogger : MonoBehaviour
{
    EnvironmentDepthManager manager;
    float t;

    void Start()
    {
        manager = FindFirstObjectByType<EnvironmentDepthManager>();
        Debug.Log("[DepthStatus] manager found = " + (manager != null));
    }

    void Update()
    {
        t += Time.unscaledDeltaTime;
        if (t < 1f) return;
        t = 0f;
        Debug.Log("[DepthStatus] IsSupported=" + EnvironmentDepthManager.IsSupported
            + ", enabled=" + (manager != null && manager.enabled)
            + ", IsDepthAvailable=" + (manager != null && manager.IsDepthAvailable));
    }
}