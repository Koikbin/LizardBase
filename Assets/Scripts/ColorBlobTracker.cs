using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

public class ColorBlobTracker : MonoBehaviour
{
    [Header("畫面來源")]
    [SerializeField] Material sourceMaterial;   // 跟 PassthroughCameraAccess 的 TargetMaterial 同一個
    [SerializeField] string texturePropertyName = "_BaseMap";

    [Header("顯示")]
    [SerializeField] Transform quad;            // 顯示相機畫面的板子
    [SerializeField] Transform marker;          // 標記球
    [SerializeField] bool flipY = false;        // 球的上下跟畫面相反時勾選

    [Header("目標顏色（預設是黃色）")]
    [SerializeField, Range(0f, 1f)] float hueMin = 0.10f;
    [SerializeField, Range(0f, 1f)] float hueMax = 0.20f;
    [SerializeField, Range(0f, 1f)] float satMin = 0.5f;
    [SerializeField, Range(0f, 1f)] float valMin = 0.4f;

    [Header("效能與判定")]
    [SerializeField] int stride = 4;            // 每隔幾個像素取樣一次，越大越快但越粗
    [SerializeField] int minPixels = 30;        // 符合顏色的取樣點少於這個數字，就當作沒看到

    bool pending;

    void Update()
    {
        if (pending) return;
        if (sourceMaterial == null || marker == null || quad == null) return;

        var tex = sourceMaterial.GetTexture(texturePropertyName);
        if (tex == null || tex.width <= 0) return;

        if (!SystemInfo.supportsAsyncGPUReadback)
        {
            Debug.LogError("這個裝置不支援 AsyncGPUReadback");
            enabled = false;
            return;
        }

        pending = true;
        AsyncGPUReadback.Request(tex, 0, TextureFormat.RGBA32, OnReadback);
    }

    void OnReadback(AsyncGPUReadbackRequest request)
    {
        pending = false;
        if (request.hasError)
        {
            Debug.LogError("AsyncGPUReadback 失敗");
            return;
        }

        NativeArray<Color32> data = request.GetData<Color32>();
        int width = request.width;
        int height = request.height;
        int step = Mathf.Max(1, stride);

        long sumX = 0, sumY = 0;
        int count = 0;

        for (int y = 0; y < height; y += step)
        {
            for (int x = 0; x < width; x += step)
            {
                Color.RGBToHSV(data[y * width + x], out float hue, out float sat, out float val);
                if (hue >= hueMin && hue <= hueMax && sat >= satMin && val >= valMin)
                {
                    sumX += x;
                    sumY += y;
                    count++;
                }
            }
        }

        if (count < minPixels)
        {
            marker.gameObject.SetActive(false);
            return;
        }

        float nx = (float)sumX / count / width;
        float ny = (float)sumY / count / height;
        if (flipY) ny = 1f - ny;

        marker.gameObject.SetActive(true);
        marker.position = quad.TransformPoint(new Vector3(nx - 0.5f, ny - 0.5f, -0.02f));
        marker.localScale = Vector3.one * Mathf.Clamp(Mathf.Sqrt(count) * 0.002f, 0.02f, 0.25f);
    }
}