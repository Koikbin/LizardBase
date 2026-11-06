
using Meta.XR;
using UnityEngine;

// 用深度射線網量「嘴前方多遠有東西」,當作吹龍龍尾的位置。
// 只有從頭盔附近往外連成細線的命中才算吹龍;孤立的雜訊、懸在遠處的手指和大面積的手、地板都不算。
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
    [Tooltip("偵錯量測時射線打多遠(公尺),不影響舌頭:超過 Max Dist 的命中只會記錄,不會計入判定")]
    public float measureDist = 0.8f;

    [Header("細線判定")]
    [Tooltip("相鄰兩格的距離差超過這個值(公尺)就算斷開")]
    public float maxDepthJump = 0.15f;
    [Tooltip("相隔幾格內還算連著。深度常在線中間缺格,所以 2 代表可以跳過一格")]
    public int lineGapCells = 2;
    [Tooltip("線至少要有幾格,少於這個數量視為雜訊")]
    public int minLineCells = 3;
    [Tooltip("線最多幾格,超過視為手或牆面這種大物體")]
    public int maxLineCells = 12;
    [Tooltip("同一排最多幾格,超過就不算細線")]
    public int maxRowWidth = 3;
    [Tooltip("線不能碰到比這排更上面的位置(0 = 最上排)")]
    public int topmostRow = 0;
    [Tooltip("線必須延伸到這排或更下面,也就是從嘴巴方向出來(0 = 最上排)")]
    public int rootFromRow = 4;
    [Tooltip("線在嘴巴區域的部分,不能比這一欄更靠左(0 = 最左欄)")]
    public int rootFromColumn = 3;
    [Tooltip("線最靠近頭盔的一點不能比這個遠(公尺)。龍尾從嘴巴延伸出去,手指則是懸在遠處")]
    public float rootMaxDist = 0.225f;
    [Tooltip("線短暫消失時,舌頭維持原長度幾秒,避免一斷線就收回")]
    public float lineHoldTime = 0.4f;

    [Header("對應成 0~1")]
    public float retracted = 0.20f;
    public float extended = 0.45f;

    public float TipDistance { get; private set; }
    public int NearHitCount { get; private set; }
    public float Length01 { get; private set; }

    // 偵錯用:這一輪所有距離內的命中點、決定距離的點,以及未經判定的最遠距離
    public Vector3[] HitPoints { get; private set; } = new Vector3[0];
    public Vector3 TipPoint { get; private set; }
    public float RawFarDistance { get; private set; }
    public int LineCellCount { get; private set; }
    public int RayCount => grid * grid;

    // 偵錯用:每格射線的結果。索引 = 左右(0 = 最左) + 上下(0 = 最上) * grid
    public float[] CellDistances { get; private set; } = new float[0];
    public EnvironmentRaycastHitStatus[] CellStatuses { get; private set; } = new EnvironmentRaycastHitStatus[0];

    Vector3[] cellPoints = new Vector3[0];
    bool[] visited = new bool[0];
    int[] pending = new int[0];
    int[] rowWidths = new int[0];
    int frame;
    float lastLineTime = float.NegativeInfinity;

    void Reset()
    {
        raycastManager = GetComponent<EnvironmentRaycastManager>();
    }

    void Update()
    {
        if (head == null || raycastManager == null) return;
        if (++frame % Mathf.Max(1, everyNFrames) != 0) return;

        EnsureBuffers();

        Vector3 origin = head.position;

        for (int i = 0; i < grid; i++)
        {
            float yaw = grid == 1 ? 0 : Mathf.Lerp(-halfYawDeg, halfYawDeg, i / (float)(grid - 1));
            for (int j = 0; j < grid; j++)
            {
                float pitch = grid == 1 ? 0 : Mathf.Lerp(0, maxPitchDeg, j / (float)(grid - 1)); // 正值 = 往下
                Vector3 dir = head.rotation * Quaternion.Euler(pitch, yaw, 0) * Vector3.forward;
                var ray = new Ray(origin, dir);

                // 憑印象:Raycast(Ray, out EnvironmentRaycastHit, float maxDistance) 回傳 bool
                bool didHit = raycastManager.Raycast(ray, out var hit, Mathf.Max(maxDist, measureDist));
                int cell = i + j * grid;
                CellStatuses[cell] = hit.status;
                CellDistances[cell] = didHit ? Vector3.Distance(origin, hit.point) : 0f;
                cellPoints[cell] = hit.point;
            }
        }

        EvaluateLine(Time.time);
    }

    void EnsureBuffers()
    {
        if (CellDistances.Length == RayCount) return;

        HitPoints = new Vector3[RayCount];
        CellDistances = new float[RayCount];
        CellStatuses = new EnvironmentRaycastHitStatus[RayCount];
        cellPoints = new Vector3[RayCount];
        visited = new bool[RayCount];
        pending = new int[RayCount];
        rowWidths = new int[grid];
    }

    bool IsNear(int cell)
    {
        return CellStatuses[cell] == EnvironmentRaycastHitStatus.Hit
            && CellDistances[cell] >= minDist
            && CellDistances[cell] <= maxDist;
    }

    void EvaluateLine(float now)
    {
        int nearCount = 0;
        float rawFar = 0f;
        for (int cell = 0; cell < RayCount; cell++)
        {
            visited[cell] = false;
            if (!IsNear(cell)) continue;

            HitPoints[nearCount] = cellPoints[cell];
            nearCount++;
            if (CellDistances[cell] > rawFar) rawFar = CellDistances[cell];
        }
        NearHitCount = nearCount;
        RawFarDistance = rawFar;

        float bestDistance = 0f;
        int bestCell = -1;
        int bestSize = 0;

        for (int start = 0; start < RayCount; start++)
        {
            if (visited[start] || !IsNear(start)) continue;

            if (MeasureConnectedCells(start, out int size, out int farCell) && CellDistances[farCell] > bestDistance)
            {
                bestDistance = CellDistances[farCell];
                bestCell = farCell;
                bestSize = size;
            }
        }

        LineCellCount = bestSize;

        if (bestCell >= 0)
        {
            lastLineTime = now;
            TipDistance = bestDistance;
            TipPoint = cellPoints[bestCell];
            Length01 = Mathf.InverseLerp(retracted, extended, bestDistance);
        }
        else if (now - lastLineTime > lineHoldTime)
        {
            TipDistance = 0f;
            Length01 = 0f;
        }
    }

    // 從 start 把相鄰且距離接近的格子連起來,回傳這一群是否符合細線的條件。
    bool MeasureConnectedCells(int start, out int size, out int farCell)
    {
        System.Array.Clear(rowWidths, 0, grid);
        size = 0;
        farCell = start;

        int lowestRow = 0;
        int highestRow = grid;
        int leftmostRootColumn = grid;
        float nearestDistance = CellDistances[start];
        int pendingCount = 0;

        pending[pendingCount++] = start;
        visited[start] = true;

        while (pendingCount > 0)
        {
            int cell = pending[--pendingCount];
            int column = cell % grid;
            int row = cell / grid;

            size++;
            rowWidths[row]++;
            if (CellDistances[cell] > CellDistances[farCell]) farCell = cell;
            if (CellDistances[cell] < nearestDistance) nearestDistance = CellDistances[cell];
            if (row > lowestRow) lowestRow = row;
            if (row < highestRow) highestRow = row;
            if (row >= rootFromRow && column < leftmostRootColumn) leftmostRootColumn = column;

            for (int nextRow = Mathf.Max(0, row - lineGapCells); nextRow <= Mathf.Min(grid - 1, row + lineGapCells); nextRow++)
            {
                for (int nextColumn = Mathf.Max(0, column - lineGapCells); nextColumn <= Mathf.Min(grid - 1, column + lineGapCells); nextColumn++)
                {
                    int next = nextColumn + nextRow * grid;
                    if (visited[next] || !IsNear(next)) continue;
                    if (Mathf.Abs(CellDistances[next] - CellDistances[cell]) > maxDepthJump) continue;

                    visited[next] = true;
                    pending[pendingCount++] = next;
                }
            }
        }

        if (size < minLineCells || size > maxLineCells) return false;
        if (highestRow < topmostRow || lowestRow < rootFromRow) return false;
        if (leftmostRootColumn < rootFromColumn) return false;
        if (nearestDistance > rootMaxDist) return false;

        for (int row = 0; row < grid; row++)
        {
            if (rowWidths[row] > maxRowWidth) return false;
        }
        return true;
    }
}
