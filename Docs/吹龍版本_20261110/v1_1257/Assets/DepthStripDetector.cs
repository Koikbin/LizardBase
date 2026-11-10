using Meta.XR.EnvironmentDepth;
using UnityEngine;
using UnityEngine.Rendering;

// 偵錯用:直接在深度圖上找「從畫面邊緣長出來的細長近距離物體」,也就是吹龍的形狀。
// 左右眼任一眼找到夠長的細長條,就控制舌頭(BlowoutDepthProbe 勾選 Use Strip Detector 時)。由 DepthSnapshot 在執行時加上。
// 步驟:距離 30 公分內的像素 → 補起小缺口(先膨脹再侵蝕)→ 連成一塊塊 → 依形狀篩選。
// 左右眼的深度各算一次,用來比較哪一邊比較準。低頭時不偵測(容易把自己的腿和身體當成吹龍)。
// 另外記錄形狀數值(長度、寬度、角度),用來研究能不能分開吹和翹起。
public class DepthStripDetector : MonoBehaviour
{
    const int GridSize = 160;
    const int CellCount = GridSize * GridSize;
    const int CloseRadius = 2;
    const float NearMeters = 0.30f;

    // 形狀條件(單位是 160x160 的格子),由 2026-11-10 的測試資料調出來
    const int MinArea = 25;
    const int MaxArea = 3000;
    const int MaxWidth = 60;
    const int MinHeight = 15;
    const int MaxBottomGap = 60;    // 根部在畫面下方:這一塊的下緣最高到哪裡
    const int MinTop = 40;          // 這一塊的上緣至少要到哪裡
    const int EdgeMargin = 1;       // 根部在左右邊緣:離邊緣幾格內算碰到
    const float MinTongueLength = 55f;  // 色塊沿長軸至少這麼長才算吹龍伸出來(翹起時多半比較短)
    const float TipPercentile = 0.9f;   // 龍尾取離嘴巴第 90 百分位的格子,避免單一雜點

    public struct Strip
    {
        public bool found;
        public int area, width, height, x, y;
        // 用主成分分析(PCA)算的形狀:沿著色塊自己方向的長度、寬度(格),長軸的角度(度,0 = 水平)
        public float length, thickness, angle;
    }

    // 位置條件:吹龍在左眼深度圖的右半邊、右眼的左半邊(色塊中心的 x,單位是格)
    const float LeftEyeMinCenterX = 70f;
    const float RightEyeMaxCenterX = 90f;

    public float interval = 0.2f;
    [Tooltip("頭往下超過這個角度就不偵測")]
    public float maxPitchDown = 30f;

    public Strip Left { get; private set; }
    public Strip Right { get; private set; }
    public bool LookingDown { get; private set; }

    // 控制舌頭用:有沒有偵測到伸出來的吹龍(任一眼)、色塊在圖上的長度(格,兩眼取長的)。
    // 龍尾離嘴巴多遠(3D 反推)只算左眼,而且吹龍的深度值常被填成最近距離,目前只記錄參考
    public bool HornFound { get; private set; }
    public float TipMouthDistance { get; private set; }
    public Vector3 TipWorldPoint { get; private set; }
    public float ImageLength { get; private set; }

    static readonly int DepthTextureId = Shader.PropertyToID("_EnvironmentDepthTexture");
    static readonly int SliceId = Shader.PropertyToID("_Slice");
    static readonly int ReprojectionId = Shader.PropertyToID("_EnvironmentDepthReprojectionMatrices");
    static readonly int ZBufferParamsId = Shader.PropertyToID("_EnvironmentDepthZBufferParams");

    EnvironmentDepthManager depthManager;
    Transform head;
    BlowoutDepthProbe probe;
    // 送出讀取時的投影矩陣(反矩陣)、深度換算參數、嘴巴位置,和那一張深度圖對齊
    readonly Matrix4x4[] depthToWorld = new Matrix4x4[2];
    readonly bool[] hasMatrix = new bool[2];
    Vector4 zParams;
    Vector3 mouthAtCapture;
    float scaleAtCapture = 1f;
    readonly Material[] materials = new Material[2];
    readonly RenderTexture[] targets = new RenderTexture[2];
    int pending;
    float nextTime;

    readonly bool[] raw = new bool[CellCount];
    readonly bool[] dilated = new bool[CellCount];
    readonly bool[] mask = new bool[CellCount];
    readonly bool[] seen = new bool[CellCount];
    readonly int[] stack = new int[CellCount];
    readonly int[] labels = new int[CellCount];
    readonly float[] gridMeters = new float[CellCount];
    readonly float[] tipDistances = new float[CellCount];
    int bestLabel = -1;
    bool leftHorn, rightHorn;
    float leftLength, rightLength;
    int textureWidth, textureHeight, textureStep;

    public void Init(Shader depthShader, EnvironmentDepthManager manager, BlowoutDepthProbe depthProbe)
    {
        for (int eye = 0; eye < 2; eye++)
        {
            materials[eye] = new Material(depthShader);
            materials[eye].SetFloat(SliceId, eye);
        }
        depthManager = manager;
        probe = depthProbe;
        head = depthProbe != null ? depthProbe.head : null;
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
            ClearHorn();
            return;
        }

        var matrices = Shader.GetGlobalMatrixArray(ReprojectionId);
        for (int eye = 0; eye < 2; eye++)
        {
            hasMatrix[eye] = matrices != null && matrices.Length > eye;
            if (hasMatrix[eye]) depthToWorld[eye] = matrices[eye].inverse;
        }
        zParams = Shader.GetGlobalVector(ZBufferParamsId);
        if (probe != null)
        {
            mouthAtCapture = probe.MouthPoint;
            scaleAtCapture = probe.WorldScale;
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
                if (eye == 0) gridMeters[x + y * GridSize] = value;
            }
        }

        CloseGaps();
        var strip = FindStrip(eye);
        if (eye == 0)
        {
            Left = strip;
            textureWidth = width;
            textureHeight = request.height;
            textureStep = step;
            UpdateHorn(strip);
        }
        else
        {
            Right = strip;
            rightHorn = strip.found && strip.length >= MinTongueLength;
            rightLength = strip.found ? strip.length : 0f;
        }
        CombineEyes();
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

    Strip FindStrip(int eye)
    {
        var best = new Strip();
        bestLabel = -1;

        for (int start = 0; start < CellCount; start++)
        {
            if (!mask[start] || seen[start]) continue;

            int label = start;
            int count = 0, area = 0, minX = GridSize, maxX = 0, minY = GridSize, maxY = 0;
            long sumX = 0, sumY = 0, sumXX = 0, sumYY = 0, sumXY = 0;
            stack[count++] = start;
            seen[start] = true;
            while (count > 0)
            {
                int cell = stack[--count];
                int x = cell % GridSize, y = cell / GridSize;
                labels[cell] = label;
                area++;
                sumX += x; sumY += y; sumXX += x * x; sumYY += y * y; sumXY += x * y;
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

            // 位置:吹龍應該出現的那一半
            float centerX = sumX / (float)area;
            if (eye == 0 ? centerX < LeftEyeMinCenterX : centerX > RightEyeMaxCenterX) continue;

            // 有好幾塊符合時,記錄最大的那一塊
            if (area > best.area)
            {
                best.found = true;
                best.area = area;
                best.width = width;
                best.height = height;
                best.x = minX;
                best.y = minY;
                MeasureShape(area, sumX, sumY, sumXX, sumYY, sumXY, ref best);
                bestLabel = label;
            }
        }

        return best;
    }

    // 主成分分析:由所有格子座標的變異數,找出色塊的長軸方向和沿長軸、垂直長軸的分散程度。
    // 均勻的細長條,長度 = sqrt(12 x 變異數);彎曲或碎裂的色塊,寬度會變大
    static void MeasureShape(int area, long sumX, long sumY, long sumXX, long sumYY, long sumXY, ref Strip strip)
    {
        double meanX = sumX / (double)area, meanY = sumY / (double)area;
        double varX = sumXX / (double)area - meanX * meanX;
        double varY = sumYY / (double)area - meanY * meanY;
        double cov = sumXY / (double)area - meanX * meanY;
        double half = (varX + varY) * 0.5;
        double spread = System.Math.Sqrt(((varX - varY) * 0.5) * ((varX - varY) * 0.5) + cov * cov);
        double major = System.Math.Max(0.0, half + spread);
        double minor = System.Math.Max(0.0, half - spread);
        strip.length = (float)System.Math.Sqrt(12.0 * major);
        strip.thickness = (float)System.Math.Sqrt(12.0 * minor);
        strip.angle = (float)(0.5 * System.Math.Atan2(2.0 * cov, varX - varY) * 180.0 / System.Math.PI);
    }

    void ClearHorn()
    {
        HornFound = false;
        TipMouthDistance = 0f;
        ImageLength = 0f;
        leftHorn = rightHorn = false;
        leftLength = rightLength = 0f;
    }

    // 任一眼偵測到就算;長度取偵測到的那幾眼裡比較長的
    void CombineEyes()
    {
        HornFound = leftHorn || rightHorn;
        ImageLength = Mathf.Max(leftHorn ? leftLength : 0f, rightHorn ? rightLength : 0f);
    }

    // 左眼的色塊夠長才算吹龍伸出來;再把色塊裡每一格用深度值反推回 3D,取離嘴巴第 90 百分位的距離當龍尾
    void UpdateHorn(Strip strip)
    {
        TipMouthDistance = 0f;
        leftLength = strip.found ? strip.length : 0f;
        leftHorn = strip.found && strip.length >= MinTongueLength && bestLabel >= 0;
        if (!leftHorn) return;
        if (!hasMatrix[0] || probe == null) return;

        int count = 0;
        for (int cell = 0; cell < CellCount; cell++)
        {
            if (!InBestStrip(cell)) continue;
            tipDistances[count++] = MouthDistance(cell);
        }
        if (count == 0) return;

        System.Array.Sort(tipDistances, 0, count);
        float tip = tipDistances[Mathf.Min(count - 1, Mathf.FloorToInt(TipPercentile * (count - 1)))];

        // 找出最接近這個距離的格子,當作龍尾點(偵錯照片上的紅點)
        float bestGap = float.MaxValue;
        Vector3 tipPoint = Vector3.zero;
        for (int cell = 0; cell < CellCount; cell++)
        {
            if (!InBestStrip(cell)) continue;
            float gap = Mathf.Abs(MouthDistance(cell) - tip);
            if (gap < bestGap)
            {
                bestGap = gap;
                tipPoint = CellToWorld(cell, gridMeters[cell]);
            }
        }
        TipMouthDistance = tip;
        TipWorldPoint = tipPoint;
    }

    bool InBestStrip(int cell)
    {
        float meters = gridMeters[cell];
        return mask[cell] && labels[cell] == bestLabel && meters > 0f && meters <= NearMeters;
    }

    // 這一格離嘴巴多遠(真實公尺)
    float MouthDistance(int cell)
    {
        return Vector3.Distance(CellToWorld(cell, gridMeters[cell]), mouthAtCapture) / scaleAtCapture;
    }

    // 深度圖上的一格 + 深度值(公尺)→ 世界座標。和 Meta 射線程式的做法相同:
    // 先把線性深度換回投影後的 z,再乘上投影矩陣的反矩陣
    Vector3 CellToWorld(int cell, float meters)
    {
        int x = cell % GridSize, y = cell / GridSize;
        float u = (Mathf.Min(x * textureStep + textureStep / 2, textureWidth - 1) + 0.5f) / textureWidth;
        float v = (Mathf.Min(y * textureStep + textureStep / 2, textureHeight - 1) + 0.5f) / textureHeight;
        float ndcZ = zParams.x / meters - zParams.y;
        Vector4 clip = new Vector4(u * 2f - 1f, v * 2f - 1f, ndcZ, 1f);
        Vector4 world = depthToWorld[0] * clip;
        return new Vector3(world.x, world.y, world.z) / world.w;
    }

    void Push(int cell, ref int count)
    {
        if (!mask[cell] || seen[cell]) return;
        seen[cell] = true;
        stack[count++] = cell;
    }
}
