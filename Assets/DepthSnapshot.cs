using System.IO;
using System.Threading.Tasks;
using Meta.XR;
using Meta.XR.EnvironmentDepth;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine.Android;
#endif

// 只要有射線打到 Photo Max Distance 以內的東西,不論有沒有按標記鍵,每隔 Min Interval 存下當下的畫面:
//   xxx.png        頭顯彩色鏡頭的照片,畫上藍點(青色)和龍尾點(紅色)
//   xxx_depth.png  深度感測的距離圖,也就是偵測真正看到的東西:
//                  紫色 = 比有效距離還近、橘黃 = 有效距離內(越近越黃)、灰 = 比有效距離遠(越遠越暗)、黑 = 沒有資料
//                  白色小十字 = 70 條射線打到的位置、灰色小十字 = 被當成手把排除的、青色圓點 = 藍點、紅色大圓點 = 龍尾點
//   xxx.txt        同一瞬間的格子圖
// 編輯器(Link):存到專案資料夾的 DepthShots/。頭顯上:存到 Application.persistentDataPath/DepthShots/。
// 彩色照片需要 PassthroughCameraAccess;深度圖需要場景裡有 EnvironmentDepthManager。
public class DepthSnapshot : MonoBehaviour
{
    public PassthroughCameraAccess cameraAccess;
    public EnvironmentDepthManager depthManager;
    public Shader depthShader;
    public BlowoutDepthProbe probe;
    public TongueTrigger tongue;
    public TongueDepthDebug depthDebug;

    [Tooltip("兩次存檔至少相隔幾秒,避免連續觸發時存一堆幾乎一樣的圖")]
    public float minInterval = 0.5f;
    [Tooltip("只有打到這個距離(公尺)以內的東西才拍照。距離的量法和吹龍判定相同(目前從嘴巴量)")]
    public float photoMaxDistance = 0.30f;
    [Tooltip("彩色照片上藍點畫多大(像素)")]
    public int dotRadius = 7;
    [Tooltip("彩色照片上的點和實際物體上下顛倒時勾選,照片本身也會一起翻正")]
    public bool imageIsTopDown = false;

    [Header("深度圖")]
    public bool saveDepth = true;
    [Tooltip("深度圖解析度很低,放大幾倍再存,方便看")]
    public int depthScale = 4;
    [Tooltip("用哪一眼的深度(0 = 左、1 = 右)")]
    public int depthEye = 0;
    [Tooltip("灰色漸層畫到多遠(公尺),更遠的都是最暗的灰")]
    public float depthGrayFar = 1.5f;
    [Tooltip("深度圖上的點和實際位置上下顛倒時勾選")]
    public bool depthIsTopDown = false;

    const string CameraPermission = "horizonos.permission.HEADSET_CAMERA";

    static readonly int DepthTextureId = Shader.PropertyToID("_EnvironmentDepthTexture");
    static readonly int ReprojectionId = Shader.PropertyToID("_EnvironmentDepthReprojectionMatrices");
    static readonly int SliceId = Shader.PropertyToID("_Slice");

    static readonly Color32 DotColor = new Color32(0, 230, 255, 255);
    static readonly Color32 TipColor = new Color32(255, 40, 40, 255);
    static readonly Color32 OccludedColor = new Color32(60, 235, 60, 255);
    static readonly Color32 RingColor = new Color32(0, 0, 0, 255);
    static readonly Color32 RayColor = new Color32(255, 255, 255, 255);
    static readonly Color32 HandRayColor = new Color32(120, 120, 120, 255);

    string folder;
    float nextAllowedTime;
    int savedCount;
    bool warnedNoCamera;
    bool warnedNoDepth;
    Material depthMaterial;

    void Start()
    {
#if UNITY_EDITOR
        folder = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "DepthShots"));
#else
        folder = Path.Combine(Application.persistentDataPath, "DepthShots");
#endif
        Directory.CreateDirectory(folder);
        Debug.Log("[DepthShot] 照片會存到 " + folder);

        if (depthShader != null)
        {
            depthMaterial = new Material(depthShader);
            gameObject.AddComponent<DepthStripDetector>().Init(depthShader, depthManager, probe != null ? probe.head : null);
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        if (!Permission.HasUserAuthorizedPermission(CameraPermission))
            Permission.RequestUserPermission(CameraPermission);
#endif
    }

    void OnDestroy()
    {
        if (depthMaterial != null) Destroy(depthMaterial);
    }

    void LateUpdate()
    {
        string mark = TongueDepthDebug.CurrentMark() ?? "none";
        if (Time.unscaledTime < nextAllowedTime || !probe.HasHitWithin(photoMaxDistance)) return;
        nextAllowedTime = Time.unscaledTime + minInterval;

        string result = probe.DescribeResult();
        string name = $"{savedCount:D3}_t{Time.unscaledTime:F2}_{mark}_{SafeName(result)}";
        savedCount++;

        string report = depthDebug != null ? depthDebug.GridReport(mark == "none" ? null : mark) : result;

        bool savedPhoto = TrySaveCameraPhoto(Path.Combine(folder, name + ".png"));
        string depthNote = saveDepth ? TrySaveDepthImage(Path.Combine(folder, name + "_depth.png")) : "深度圖:未開啟";

        File.WriteAllText(Path.Combine(folder, name + ".txt"), report + depthNote + "\n");
        Debug.Log($"[DepthShot] {name}  photo={(savedPhoto ? "ok" : "--")}  {depthNote}");
    }

    // ---------- 彩色照片 ----------

    bool TrySaveCameraPhoto(string path)
    {
        if (cameraAccess == null || !cameraAccess.IsPlaying)
        {
            if (!warnedNoCamera)
            {
                Debug.LogWarning("[DepthShot] 鏡頭畫面還沒準備好,這次沒拍彩色照片。編輯器裡需要 Link 開啟穿透攝影機權限,頭顯上需要允許相機權限。");
                warnedNoCamera = true;
            }
            return false;
        }

        var colors = cameraAccess.GetColors();
        if (!colors.IsCreated || colors.Length == 0) return false;

        int width = cameraAccess.CurrentResolution.x;
        int height = cameraAccess.CurrentResolution.y;
        Color32[] pixels = colors.ToArray();
        if (imageIsTopDown) FlipRows(pixels, width, height);

        // 點的位置用鏡頭拍下這張畫面時的姿勢來換算
        Pose cameraPose = cameraAccess.GetCameraPose();
        for (int i = 0; i < probe.NearHitCount; i++)
            DrawOnPhoto(pixels, width, height, probe.HitPoints[i], cameraPose, DotColor, dotRadius);
        for (int cell = 0; cell < probe.RayCount; cell++)
        {
            if (IsNearOccludedCell(cell))
                DrawOnPhoto(pixels, width, height, probe.CellPoint(cell), cameraPose, OccludedColor, dotRadius);
        }
        if (probe.TipDistance > 0f)
            DrawOnPhoto(pixels, width, height, probe.TipPoint, cameraPose, TipColor, dotRadius * 2);

        SavePngInBackground(pixels, width, height, path);
        return true;
    }

    void DrawOnPhoto(Color32[] pixels, int width, int height, Vector3 worldPoint, Pose cameraPose, Color32 color, int radius)
    {
        // 點在鏡頭後方就不畫
        Vector3 local = Quaternion.Inverse(cameraPose.rotation) * (worldPoint - cameraPose.position);
        if (local.z <= 0f) return;

        Vector2 viewport = cameraAccess.WorldToViewportPoint(worldPoint, cameraPose);
        DrawDot(pixels, width, height, Mathf.RoundToInt(viewport.x * width), Mathf.RoundToInt(viewport.y * height), color, radius);
    }

    // ---------- 深度圖 ----------

    string TrySaveDepthImage(string path)
    {
        var depthTexture = Shader.GetGlobalTexture(DepthTextureId);
        if (depthManager == null || !depthManager.IsDepthAvailable || depthTexture == null || depthMaterial == null)
        {
            if (!warnedNoDepth)
            {
                Debug.LogWarning("[DepthShot] 拿不到深度圖,這次沒存。需要場景裡有啟用的 EnvironmentDepthManager,並指定 Depth Shader。");
                warnedNoDepth = true;
            }
            return "深度圖:拿不到";
        }

        int width = depthTexture.width;
        int height = depthTexture.height;

        // 用著色器把深度轉成公尺,寫到浮點貼圖,再讀回 CPU
        var target = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.RFloat);
        depthMaterial.SetFloat(SliceId, depthEye);
        Graphics.Blit(Texture2D.blackTexture, target, depthMaterial);
        var request = AsyncGPUReadback.Request(target, 0, TextureFormat.RFloat);
        request.WaitForCompletion();
        RenderTexture.ReleaseTemporary(target);
        if (request.hasError) return "深度圖:讀取失敗";

        float[] meters = request.GetData<float>().ToArray();
        if (depthIsTopDown) FlipRows(meters, width, height);

        // 放大並上色
        int scale = Mathf.Max(1, depthScale);
        int outW = width * scale, outH = height * scale;
        var pixels = new Color32[outW * outH];
        for (int y = 0; y < outH; y++)
        {
            int sy = y / scale;
            for (int x = 0; x < outW; x++)
                pixels[x + y * outW] = DepthColor(meters[x / scale + sy * width]);
        }

        // 把射線和藍點投影到深度圖上
        Matrix4x4[] matrices = Shader.GetGlobalMatrixArray(ReprojectionId);
        if (matrices == null || matrices.Length <= depthEye) return "深度圖:沒有投影矩陣";
        Matrix4x4 worldToDepth = matrices[depthEye];

        int inside = 0, outside = 0;
        int columns = probe.columns, rows = probe.rows;
        for (int row = 0; row < rows; row++)
        {
            for (int column = 0; column < columns; column++)
            {
                int cell = column + row * columns;
                if (cell >= probe.CellStatuses.Length) continue;
                bool hit = probe.CellStatuses[cell] == EnvironmentRaycastHitStatus.Hit;
                float distance = hit ? probe.CellDistances[cell] : 0.5f;
                Vector3 dir = probe.head.rotation * Quaternion.Euler(probe.PitchOfRow(row), probe.YawOfColumn(column), 0f) * Vector3.forward;
                Vector3 point = probe.head.position + dir * distance;
                if (Project(worldToDepth, point, outW, outH, out int px, out int py))
                    DrawCross(pixels, outW, outH, px, py, probe.CellNearHand[cell] ? HandRayColor : RayColor, Mathf.Max(2, scale));
            }
        }

        int radius = Mathf.Max(3, scale + 2);
        for (int i = 0; i < probe.NearHitCount; i++)
        {
            if (Project(worldToDepth, probe.HitPoints[i], outW, outH, out int px, out int py))
            {
                DrawDot(pixels, outW, outH, px, py, DotColor, radius);
                inside++;
            }
            else outside++;
        }
        for (int cell = 0; cell < probe.RayCount; cell++)
        {
            if (IsNearOccludedCell(cell) && Project(worldToDepth, probe.CellPoint(cell), outW, outH, out int ox, out int oy))
                DrawDot(pixels, outW, outH, ox, oy, OccludedColor, radius);
        }
        if (probe.TipDistance > 0f && Project(worldToDepth, probe.TipPoint, outW, outH, out int tx, out int ty))
            DrawDot(pixels, outW, outH, tx, ty, TipColor, radius * 2);

        SavePngInBackground(pixels, outW, outH, path);
        return $"深度圖:{width}x{height},藍點在圖內 {inside} 個、圖外 {outside} 個";
    }

    // 綠點:Meta 判定不可靠(oc)、但距離在拍照範圍內的命中點。只畫出來看,不參與判定
    bool IsNearOccludedCell(int cell)
    {
        float distance = probe.CellOcMouthDistances[cell];
        return distance > 0f && distance <= photoMaxDistance;
    }

    Color32 DepthColor(float meters)
    {
        if (float.IsNaN(meters) || meters <= 0f || meters > 20f) return new Color32(10, 10, 10, 255);
        if (meters < probe.minDist) return new Color32(170, 60, 210, 255);
        if (meters <= probe.maxDist)
        {
            float t = Mathf.InverseLerp(probe.minDist, probe.maxDist, meters);
            return new Color32(255, (byte)Mathf.Lerp(230, 110, t), (byte)Mathf.Lerp(60, 0, t), 255);
        }
        float g = Mathf.InverseLerp(probe.maxDist, depthGrayFar, meters);
        byte v = (byte)Mathf.Lerp(220, 40, g);
        return new Color32(v, v, v, 255);
    }

    static bool Project(Matrix4x4 worldToClip, Vector3 world, int width, int height, out int px, out int py)
    {
        Vector4 clip = worldToClip * new Vector4(world.x, world.y, world.z, 1f);
        px = py = 0;
        if (clip.w <= 0f) return false;
        float u = clip.x / clip.w * 0.5f + 0.5f;
        float v = clip.y / clip.w * 0.5f + 0.5f;
        if (u < 0f || u > 1f || v < 0f || v > 1f) return false;
        px = Mathf.RoundToInt(u * (width - 1));
        py = Mathf.RoundToInt(v * (height - 1));
        return true;
    }

    // ---------- 共用 ----------

    static void DrawDot(Color32[] pixels, int width, int height, int cx, int cy, Color32 color, int radius)
    {
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

    static void DrawCross(Color32[] pixels, int width, int height, int cx, int cy, Color32 color, int arm)
    {
        for (int i = -arm; i <= arm; i++)
        {
            if (cx + i >= 0 && cx + i < width && cy >= 0 && cy < height) pixels[cx + i + cy * width] = color;
            if (cy + i >= 0 && cy + i < height && cx >= 0 && cx < width) pixels[cx + (cy + i) * width] = color;
        }
    }

    static void SavePngInBackground(Color32[] pixels, int width, int height, string path)
    {
        // PNG 編碼比較慢,放到背景執行緒,避免畫面卡頓
        uint w = (uint)width, h = (uint)height;
        Task.Run(() =>
        {
            byte[] png = ImageConversion.EncodeArrayToPNG(pixels, GraphicsFormat.R8G8B8A8_UNorm, w, h);
            File.WriteAllBytes(path, png);
        });
    }

    static void FlipRows<T>(T[] pixels, int width, int height)
    {
        var row = new T[width];
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
