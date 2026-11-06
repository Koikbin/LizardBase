using System.Collections;
using UnityEngine;
#if UNITY_ANDROID
using UnityEngine.Android;
#endif

public class CameraPermissionGate : MonoBehaviour
{
    // 把場景裡的 PassthroughCameraAccess 元件拖進來
    [SerializeField] Behaviour passthroughCameraAccess;

    const string CameraPermission = "horizonos.permission.HEADSET_CAMERA";

    IEnumerator Start()
    {
        passthroughCameraAccess.enabled = false;
#if UNITY_ANDROID && !UNITY_EDITOR
        if (!Permission.HasUserAuthorizedPermission(CameraPermission))
            Permission.RequestUserPermission(CameraPermission);
        while (!Permission.HasUserAuthorizedPermission(CameraPermission))
            yield return null;
#endif
        yield return null;
        passthroughCameraAccess.enabled = true;
    }
}