using System.IO;
using System.Threading.Tasks;
using Meta.XR;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine.Android;
#endif

// 偵測到吹龍、舌頭出現的那一刻(舌頭輸入從 0 變成大於 0),用頭顯鏡頭拍一張照片。
// 照片上會畫出當下的藍點(青色)和決定舌頭長度的龍尾點(紅色),旁邊另存一份同名的格子圖文字。
// 編輯器(Link):存到專案資料夾的 DepthShots/。頭顯上:存到 Application.persistentDataPath/DepthShots/。
// 需要同一個場景裡有 PassthroughCameraAccess;編輯器裡要用 Meta Horizon Link v85 以上。
public class DepthSnapshot : MonoBehaviour
{
    public PassthroughCameraAccess cameraAccess;
    public BlowoutDepthProbe probe;
    public TongueTrigger tongue;
    public TongueDepthDebug depthDebug;

    [Tooltip("兩張照片至少相隔幾秒,避免連續觸發時存一堆幾乎一樣的照片")]
    public float minInterval = 0.5f;
    [Tooltip("藍點在照片上畫多大(像素)")]
    public int dotRadius = 7;
    [Tooltip("照片上的點和實際物體上下顛倒時勾選,照片本身也會一起翻正")]
    public bool imageIsTopDown = false;

    const string CameraPermission = "horizonos.permission.HEADSET_CAMERA";

    static readonly Color32 DotColor = new Color32(0, 230, 255, 255);
    static readonly Color32 TipColor = new Color32(255, 40, 40, 255);
    static readonly Color32 RingColor = new Color32(0, 0, 0, 255);

    string folder;
    float previousInput;
    float nextAllowedTime;
    int savedCount;
    bool warnedNotPlaying;

    void Start()
    {
#if UNITY_EDITOR
        folder = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "DepthShots"));
#else
        folder = Path.Combine(Application.persistentDataPath, "DepthShots");
#endif
        Directory.CreateDirectory(folder);
        Debug.Log("[DepthShot] 照片會存到 " + folder);

#if UNITY_ANDROID && !UNITY_EDITOR
        if (!Permission.HasUserAuthorizedPermission(CameraPermission))
            Permission.RequestUserPermission(CameraPermission);
#endif
    }

    void LateUpdate()
    {
        float input = tongue.RawInput;
        bool tongueAppeared = previousInput <= 0f && input > 0f;
        previousInput = input;

        if (!tongueAppeared || Time.unscaledTime < nextAllowedTime) return;
        if (cameraAccess == null || !cameraAccess.IsPlaying)
        {
            if (!warnedNotPlaying)
            {
                Debug.LogWarning("[DepthShot] 鏡頭畫面還沒準備好,這次沒拍到。編輯器裡需要 Meta Horizon Link v85 以上,頭顯上需要允許相機權限。");
                warnedNotPlaying = true;
            }
            return;
        }

        nextAllowedTime = Time.unscaledTime + minInterval;
        Capture();
    }

    void Capture()
    {
        var colors = cameraAccess.GetColors();
        if (!colors.IsCreated || colors.Length == 0) return;

        int width = cameraAccess.CurrentResolution.x;
        int height = cameraAccess.CurrentResolution.y;
        Color32[] pixels = colors.ToArray();
        if (imageIsTopDown) FlipRows(pixels, width, height);

        // 點的位置用鏡頭拍下這張畫面時的姿勢來換算
        Pose cameraPose = cameraAccess.GetCameraPose();
        for (int i = 0; i < probe.NearHitCount; i++)
            DrawPoint(pixels, width, height, probe.HitPoints[i], cameraPose, DotColor, dotRadius);
        if (probe.TipDistance > 0f)
            DrawPoint(pixels, width, height, probe.TipPoint, cameraPose, TipColor, dotRadius * 2);

        string mark = TongueDepthDebug.CurrentMark() ?? "none";
        string result = probe.DescribeResult();
        string name = $"{savedCount:D3}_t{Time.unscaledTime:F2}_{mark}_{SafeName(result)}";
        savedCount++;

        string pngPath = Path.Combine(folder, name + ".png");
        string txtPath = Path.Combine(folder, name + ".txt");
        string report = depthDebug != null ? depthDebug.GridReport(mark == "none" ? null : mark) : result;
        File.WriteAllText(txtPath, report);

        // PNG 編碼比較慢,放到背景執行緒,避免畫面卡頓
        uint w = (uint)width, h = (uint)height;
        Task.Run(() =>
        {
            byte[] png = ImageConversion.EncodeArrayToPNG(pixels, GraphicsFormat.R8G8B8A8_UNorm, w, h);
            File.WriteAllBytes(pngPath, png);
        });

        Debug.Log($"[DepthShot] {name}.png  hits={probe.NearHitCount}  far={(probe.TipDistance * 100f):F1}cm");
    }

    void DrawPoint(Color32[] pixels, int width, int height, Vector3 worldPoint, Pose cameraPose, Color32 color, int radius)
    {
        // 點在鏡頭後方就不畫
        Vector3 local = Quaternion.Inverse(cameraPose.rotation) * (worldPoint - cameraPose.position);
        if (local.z <= 0f) return;

        Vector2 viewport = cameraAccess.WorldToViewportPoint(worldPoint, cameraPose);
        int cx = Mathf.RoundToInt(viewport.x * width);
        int cy = Mathf.RoundToInt(viewport.y * height);

        int ring = radius + 2;
        for (int y = -ring; y <= ring; y++)
        {
            for (int x = -ring; x <= ring; x++)
            {
                int px = cx + x, py = cy + y;
                if (px < 0 || py < 0 || px >= width || py >= height) continue;
                int d2 = x * x + y * y;
                if (d2 > ring * ring) continue;
                pixels[px + py * width] = d2 <= radius * radius ? color : RingColor;
            }
        }
    }

    static void FlipRows(Color32[] pixels, int width, int height)
    {
        var row = new Color32[width];
        for (int y = 0; y < height / 2; y++)
        {
            int top = y * width, bottom = (height - 1 - y) * width;
            System.Array.Copy(pixels, top, row, 0, width);
            System.Array.Copy(pixels, bottom, pixels, top, width);
            System.Array.Copy(row, 0, pixels, bottom, width);
        }
    }

    static string SafeName(string text)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            text = text.Replace(c, '-');
        return text.Replace(' ', '-').Replace('<', '-').Replace('>', '-').Replace(',', '-');
    }
}
