using Meta.XR.EnvironmentDepth;
using UnityEngine;
using UnityEngine.Rendering;

// 偵錯用:直接在深度圖上找「從畫面邊緣長出來的細長近距離物體」,也就是吹龍的形狀。
// 目前只記錄結果,不影響舌頭。由 DepthSnapshot 在執行時加上。
// 步驟:距離 30 公分內的像素 → 補起小缺口(先膨脹再侵蝕)→ 連成一塊塊 → 依形狀篩選。
// 左右眼的深度各算一次,用來比較哪一邊比較準。低頭時不偵測(容易把自己的腿和身體當成吹龍)。
public class DepthStripDetector : MonoBehaviour
{
    const int GridSize = 160;
    const int CellCount = GridSize * GridSize;
    const int CloseRadius = 2;
    const float NearMeters = 0.30f;

    // 形狀條件(單位是 160x160 的格子),由 2026-11-10 的測試資料調出來
    const int MinArea = 25;
    const int MaxArea = 1500;
    const int MaxWidth = 60;
    const int MinHeight = 15;
    const int MaxBottomGap = 60;    // 根部在畫面下方:這一塊的下緣最高到哪裡
    const int MinTop = 50;          // 這一塊的上緣至少要到哪裡
    const int EdgeMargin = 1;       // 根部在左右邊緣:離邊緣幾格內算碰到

    public struct Strip
    {
        public bool found;
        public int area, width, height, x, y;
    }

    public float interval = 0.2f;
    [Tooltip("頭往下超過這個角度就不偵測")]
    public float maxPitchDown = 30f;

    public Strip Left { get; private set; }
    public Strip Right { get; private set; }
    public bool LookingDown { get; private set; }

    static readonly int DepthTextureId = Shader.PropertyToID("_EnvironmentDepthTexture");
    static readonly int SliceId = Shader.PropertyToID("_Slice");

    EnvironmentDepthManager depthManager;
    Transform head;
    readonly Material[] materials = new Material[2];
    readonly RenderTexture[] targets = new RenderTexture[2];
    int pending;
    float nextTime;

    readonly bool[] raw = new bool[CellCount];
    readonly bool[] dilated = new bool[CellCount];
    readonly bool[] mask = new bool[CellCount];
    readonly bool[] seen = new bool[CellCount];
    readonly int[] stack = new int[CellCount];

    public void Init(Shader depthShader, EnvironmentDepthManager manager, Transform headTransform)
    {
        for (int eye = 0; eye < 2; eye++)
        {
            materials[eye] = new Material(depthShader);
            materials[eye].SetFloat(SliceId, eye);
        }
        depthManager = manager;
        head = headTransform;
    }

    void OnDestroy()
    {
        for (int eye = 0; eye < 2; eye++)
        {
            if (materials[eye] != null) Destroy(materials[eye]);
            if (targets[eye] != null) targets[eye].Release();
        }
    }

    void Update()
    {
        if (materials[0] == null || pending > 0 || Time.unscaledTime < nextTime) return;
        if (depthManager == null || !depthManager.IsDepthAvailable) return;
        var depthTexture = Shader.GetGlobalTexture(DepthTextureId);
        if (depthTexture == null) return;
        nextTime = Time.unscaledTime + interval;

        LookingDown = head != null && Mathf.Asin(Mathf.Clamp(-head.forward.y, -1f, 1f)) * Mathf.Rad2Deg >= maxPitchDown;
        if (LookingDown)
        {
            Left = new Strip();
            Right = new Strip();
            return;
        }

        for (int eye = 0; eye < 2; eye++)
        {
            var target = targets[eye];
            if (target == null || target.width != depthTexture.width || target.height != depthTexture.height)
            {
                if (target != null) target.Release();
                target = targets[eye] = new RenderTexture(depthTexture.width, depthTexture.height, 0, RenderTextureFormat.RFloat);
            }
            Graphics.Blit(Texture2D.blackTexture, target, materials[eye]);
            pending++;
            int capturedEye = eye;
            AsyncGPUReadback.Request(target, 0, TextureFormat.RFloat, request => OnReadback(request, capturedEye));
        }
    }

    void OnReadback(AsyncGPUReadbackRequest request, int eye)
    {
        pending--;
        if (this == null || request.hasError) return;

        var meters = request.GetData<float>();
        int width = request.width;
        int step = Mathf.Max(1, width / GridSize);
        for (int y = 0; y < GridSize; y++)
        {
            for (int x = 0; x < GridSize; x++)
            {
                int index = Mathf.Min(x * step + step / 2, width - 1)
                          + Mathf.Min(y * step + step / 2, request.height - 1) * width;
                float value = meters[index];
                raw[x + y * GridSize] = value > 0f && value <= NearMeters;
            }
        }

        CloseGaps();
        var strip = FindStrip();
        if (eye == 0) Left = strip;
        else Right = strip;
    }

    void CloseGaps()
    {
        for (int y = 0; y < GridSize; y++)
            for (int x = 0; x < GridSize; x++)
                dilated[x + y * GridSize] = AnyInWindow(raw, x, y, true);

        for (int y = 0; y < GridSize; y++)
        {
            for (int x = 0; x < GridSize; x++)
            {
                int cell = x + y * GridSize;
                mask[cell] = !AnyInWindow(dilated, x, y, false);
                seen[cell] = false;
            }
        }
    }

    // 視窗內有沒有任何一格等於 wanted(超出畫面的格子忽略)
    static bool AnyInWindow(bool[] source, int x, int y, bool wanted)
    {
        for (int dy = -CloseRadius; dy <= CloseRadius; dy++)
        {
            for (int dx = -CloseRadius; dx <= CloseRadius; dx++)
            {
                int nx = x + dx, ny = y + dy;
                if (nx < 0 || nx >= GridSize || ny < 0 || ny >= GridSize) continue;
                if (source[nx + ny * GridSize] == wanted) return true;
            }
        }
        return false;
    }

    Strip FindStrip()
    {
        var best = new Strip();

        for (int start = 0; start < CellCount; start++)
        {
            if (!mask[start] || seen[start]) continue;

            int count = 0, area = 0, minX = GridSize, maxX = 0, minY = GridSize, maxY = 0;
            stack[count++] = start;
            seen[start] = true;
            while (count > 0)
            {
                int cell = stack[--count];
                int x = cell % GridSize, y = cell / GridSize;
                area++;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
                if (x > 0) Push(cell - 1, ref count);
                if (x < GridSize - 1) Push(cell + 1, ref count);
                if (y > 0) Push(cell - GridSize, ref count);
                if (y < GridSize - 1) Push(cell + GridSize, ref count);
            }

            int width = maxX - minX + 1, height = maxY - minY + 1;
            bool touchesTop = maxY >= GridSize - 2;
            if (touchesTop || area < MinArea || area > MaxArea || width > MaxWidth) continue;
            if (height < MinHeight || maxY < MinTop) continue;

            // 根部:靠近畫面底部,或從左右邊緣伸進來
            bool rootAtBottom = minY <= MaxBottomGap;
            bool rootAtSide = minX <= EdgeMargin || maxX >= GridSize - 1 - EdgeMargin;
            if (!rootAtBottom && !rootAtSide) continue;

            // 有好幾塊符合時,記錄最大的那一塊
            if (area > best.area)
            {
                best.found = true;
                best.area = area;
                best.width = width;
                best.height = height;
                best.x = minX;
                best.y = minY;
            }
        }

        return best;
    }

    void Push(int cell, ref int count)
    {
        if (!mask[cell] || seen[cell]) return;
        seen[cell] = true;
        stack[count++] = cell;
    }
}
