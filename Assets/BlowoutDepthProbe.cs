
using Meta.XR;
using UnityEngine;
using UnityEngine.Serialization;

// 用深度射線網量「嘴前方多遠有東西」,當作吹龍龍尾的位置。
// 只有從頭盔附近往外連成細線的命中才算吹龍;孤立的雜訊、懸在遠處的手指和大面積的手、地板都不算。
// 掛在 DepthProbe,和 EnvironmentRaycastManager 同一個物件。
// Head 要指向目前場景的 CenterEyeAnchor。
public class BlowoutDepthProbe : MonoBehaviour
{
    public Transform head;
    public EnvironmentRaycastManager raycastManager;

    [Header("射線網")]
    [Tooltip("左右幾欄")]
    [FormerlySerializedAs("grid")] public int columns = 7;
    [Tooltip("上下幾排")]
    public int rows = 10;
    public float halfYawDeg = 25f;      // 左右各幾度
    [Tooltip("最上排往下幾度(0 = 平視)")]
    public float minPitchDeg = 0f;
    [Tooltip("最下排往下幾度")]
    public float maxPitchDeg = 70f;
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
    public int rootFromRow = 5;
    [Tooltip("線在嘴巴區域的部分,最多離中間那一欄幾欄(左右對稱)。7 欄、設 1 = 只允許中間 3 欄;設 3 = 不限制")]
    public int rootMaxColumnOffset = 3;
    [Tooltip("線最靠近頭盔的一點不能比這個遠(公尺)。龍尾從嘴巴延伸出去,手指則是懸在遠處")]
    public float rootMaxDist = 0.225f;
    [Tooltip("線短暫消失時,舌頭維持原長度幾秒,避免一斷線就收回")]
    public float lineHoldTime = 0.4f;
    [Tooltip("龍尾距離變短時,每秒最多退幾公尺。變長時立刻跟上。用來壓掉深度缺格造成的瞬間縮短。0 = 不平滑")]
    public float retractSpeed = 0.5f;

    [Header("排除手把")]
    [Tooltip("打到的點離左右手把(或手)太近就不算,用來擋掉手把伸到臉前造成的誤判")]
    public bool ignoreNearHands = true;
    [Tooltip("離手把多近(公尺)內的命中不算。手把本體加上握著它的手,15 公分會漏出邊緣,所以用 22 公分")]
    public float handIgnoreRadius = 0.22f;

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
    public int RayCount => columns * rows;

    // 偵錯用:每格射線的結果。索引 = 左右(0 = 最左) + 上下(0 = 最上) * columns
    public float[] CellDistances { get; private set; } = new float[0];
    public EnvironmentRaycastHitStatus[] CellStatuses { get; private set; } = new EnvironmentRaycastHitStatus[0];
    // 這格的命中點離手把太近,被排除
    public bool[] CellNearHand { get; private set; } = new bool[0];
    public int HandIgnoredCount { get; private set; }

    // 偵錯用:近距離的格子連成的每一塊,以及每一塊有沒有通過細線判定、沒過的原因
    public enum Verdict { Pass, TooFew, TooMany, TooHigh, NoRoot, OffCenter, RootTooFar, TooWide }

    public struct Cluster
    {
        public int size;
        public int widestRow;        // 最寬那一排有幾格
        public int highestRow;       // 碰到的最上面一排
        public int lowestRow;        // 碰到的最下面一排
        public int rootColumnOffset; // 嘴巴區的部分離中間最多幾欄
        public float nearest;        // 最靠近眼睛的距離
        public float far;            // 最遠的距離
        public Verdict verdict;
    }

    // 每格屬於哪一塊(-1 = 不屬於任何一塊,也就是不在有效距離內)
    public int[] CellClusters { get; private set; } = new int[0];
    public Cluster[] Clusters { get; private set; } = new Cluster[0];
    public int ClusterCount { get; private set; }
    // 被採用的那一塊(-1 = 這一輪沒有吹龍)
    public int AcceptedCluster { get; private set; } = -1;

    Vector3[] cellPoints = new Vector3[0];
    bool[] visited = new bool[0];
    int[] pending = new int[0];
    int[] rowWidths = new int[0];
    int frame;
    float lastLineTime = float.NegativeInfinity;
    float lastEvaluateTime = float.NegativeInfinity;
    OVRCameraRig rig;
    readonly Vector3[] handPositions = new Vector3[2];
    int handCount;

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
        UpdateHandPositions();
        int handIgnored = 0;

        for (int i = 0; i < columns; i++)
        {
            float yaw = YawOfColumn(i);
            for (int j = 0; j < rows; j++)
            {
                float pitch = PitchOfRow(j); // 正值 = 往下
                Vector3 dir = head.rotation * Quaternion.Euler(pitch, yaw, 0) * Vector3.forward;
                var ray = new Ray(origin, dir);

                bool didHit = raycastManager.Raycast(ray, out var hit, Mathf.Max(maxDist, measureDist));
                int cell = i + j * columns;
                CellStatuses[cell] = hit.status;
                CellDistances[cell] = didHit ? Vector3.Distance(origin, hit.point) : 0f;
                cellPoints[cell] = hit.point;
                CellNearHand[cell] = didHit && IsNearHand(hit.point);
                if (CellNearHand[cell]) handIgnored++;
            }
        }

        HandIgnoredCount = handIgnored;
        EvaluateLine(Time.time);
    }

    // 第 row 排往下幾度(0 = 最上排)
    public float PitchOfRow(int row)
    {
        return rows == 1 ? minPitchDeg : Mathf.Lerp(minPitchDeg, maxPitchDeg, row / (float)(rows - 1));
    }

    // 第 column 欄往右幾度(負值 = 往左,0 = 最左欄)
    public float YawOfColumn(int column)
    {
        return columns == 1 ? 0f : Mathf.Lerp(-halfYawDeg, halfYawDeg, column / (float)(columns - 1));
    }

    // 找出這一幀有在追蹤的手把位置。手把放下、沒在追蹤時就不排除。
    void UpdateHandPositions()
    {
        handCount = 0;
        if (!ignoreNearHands) return;

        if (rig == null) rig = FindFirstObjectByType<OVRCameraRig>();
        if (rig == null) return;

        if (rig.leftHandAnchor != null && IsTracked(OVRInput.Controller.LTouch, OVRInput.Controller.LHand))
            handPositions[handCount++] = rig.leftHandAnchor.position;
        if (rig.rightHandAnchor != null && IsTracked(OVRInput.Controller.RTouch, OVRInput.Controller.RHand))
            handPositions[handCount++] = rig.rightHandAnchor.position;
    }

    static bool IsTracked(OVRInput.Controller controller, OVRInput.Controller hand)
    {
        return OVRInput.GetControllerPositionTracked(controller) || OVRInput.GetControllerPositionTracked(hand);
    }

    bool IsNearHand(Vector3 point)
    {
        float radiusSqr = handIgnoreRadius * handIgnoreRadius;
        for (int i = 0; i < handCount; i++)
        {
            if ((point - handPositions[i]).sqrMagnitude <= radiusSqr) return true;
        }
        return false;
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
        rowWidths = new int[rows];
        CellClusters = new int[RayCount];
        CellNearHand = new bool[RayCount];
        Clusters = new Cluster[RayCount];
    }

    public static string VerdictText(Verdict verdict)
    {
        switch (verdict)
        {
            case Verdict.Pass: return "通過";
            case Verdict.TooFew: return "太少";
            case Verdict.TooMany: return "太大塊";
            case Verdict.TooHigh: return "碰到太上面";
            case Verdict.NoRoot: return "沒到嘴巴";
            case Verdict.OffCenter: return "偏太多";
            case Verdict.RootTooFar: return "根部太遠";
            case Verdict.TooWide: return "太寬";
            default: return "?";
        }
    }

    // 這一塊為什麼過或沒過,附上數字和門檻
    public string DescribeCluster(int index)
    {
        Cluster c = Clusters[index];
        string detail;
        switch (c.verdict)
        {
            case Verdict.TooFew: detail = $"{c.size}格 < {minLineCells}"; break;
            case Verdict.TooMany: detail = $"{c.size}格 > {maxLineCells}"; break;
            case Verdict.TooHigh: detail = $"碰到第{c.highestRow}排 < {topmostRow}"; break;
            case Verdict.NoRoot: detail = $"最低只到第{c.lowestRow}排,要到第{rootFromRow}排"; break;
            case Verdict.OffCenter: detail = $"嘴巴區偏{c.rootColumnOffset}欄 > {rootMaxColumnOffset}"; break;
            case Verdict.RootTooFar: detail = $"最近{c.nearest * 100f:F0}cm > {rootMaxDist * 100f:F1}"; break;
            case Verdict.TooWide: detail = $"某排{c.widestRow}格 > {maxRowWidth}"; break;
            default: detail = $"{c.size}格 龍尾{c.far * 100f:F0}cm"; break;
        }
        return $"{(char)('A' + index)} {VerdictText(c.verdict)}({detail})";
    }

    // 一句話說明這一輪的結果:通過的那塊,或沒有通過時最大那一塊被擋的原因
    public string DescribeResult()
    {
        if (AcceptedCluster >= 0) return DescribeCluster(AcceptedCluster);
        if (ClusterCount == 0) return "沒打到 15~" + (maxDist * 100f).ToString("F0") + "cm 內的東西";

        int biggest = 0;
        for (int i = 1; i < ClusterCount; i++)
            if (Clusters[i].size > Clusters[biggest].size) biggest = i;
        return DescribeCluster(biggest);
    }

    bool IsNear(int cell)
    {
        return CellStatuses[cell] == EnvironmentRaycastHitStatus.Hit
            && !CellNearHand[cell]
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
            CellClusters[cell] = -1;
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
        int clusterCount = 0;
        AcceptedCluster = -1;

        for (int start = 0; start < RayCount; start++)
        {
            if (visited[start] || !IsNear(start)) continue;

            int id = clusterCount++;
            Cluster cluster = MeasureConnectedCells(start, id, out int farCell);
            Clusters[id] = cluster;

            if (cluster.verdict == Verdict.Pass && cluster.far > bestDistance)
            {
                bestDistance = cluster.far;
                bestCell = farCell;
                bestSize = cluster.size;
                AcceptedCluster = id;
            }
        }

        ClusterCount = clusterCount;
        LineCellCount = bestSize;

        // 每隔 everyNFrames 才評估一次,所以用上次評估到現在的實際時間
        float dt = Mathf.Min(now - lastEvaluateTime, 0.1f);
        lastEvaluateTime = now;

        if (bestCell >= 0)
        {
            lastLineTime = now;
            // 變長立刻跟上;變短慢慢退,避免龍尾那格偶爾缺格時舌頭一縮一伸
            if (bestDistance >= TipDistance || retractSpeed <= 0f)
                TipDistance = bestDistance;
            else
                TipDistance = Mathf.MoveTowards(TipDistance, bestDistance, retractSpeed * dt);
            TipPoint = cellPoints[bestCell];
            Length01 = Mathf.InverseLerp(retracted, extended, TipDistance);
        }
        else if (now - lastLineTime > lineHoldTime)
        {
            TipDistance = 0f;
            Length01 = 0f;
        }
    }

    // 從 start 把相鄰且距離接近的格子連起來,標成第 id 塊,回傳這一塊的形狀與是否符合細線的條件。
    Cluster MeasureConnectedCells(int start, int id, out int farCell)
    {
        System.Array.Clear(rowWidths, 0, rows);
        int size = 0;
        farCell = start;

        int lowestRow = 0;
        int highestRow = rows;
        int maxRootColumnOffset = 0;
        float centerColumn = (columns - 1) * 0.5f;
        float nearestDistance = CellDistances[start];
        int pendingCount = 0;

        pending[pendingCount++] = start;
        visited[start] = true;

        while (pendingCount > 0)
        {
            int cell = pending[--pendingCount];
            int column = cell % columns;
            int row = cell / columns;

            size++;
            rowWidths[row]++;
            CellClusters[cell] = id;
            if (CellDistances[cell] > CellDistances[farCell]) farCell = cell;
            if (CellDistances[cell] < nearestDistance) nearestDistance = CellDistances[cell];
            if (row > lowestRow) lowestRow = row;
            if (row < highestRow) highestRow = row;
            if (row >= rootFromRow)
            {
                int offset = Mathf.CeilToInt(Mathf.Abs(column - centerColumn));
                if (offset > maxRootColumnOffset) maxRootColumnOffset = offset;
            }

            for (int nextRow = Mathf.Max(0, row - lineGapCells); nextRow <= Mathf.Min(rows - 1, row + lineGapCells); nextRow++)
            {
                for (int nextColumn = Mathf.Max(0, column - lineGapCells); nextColumn <= Mathf.Min(columns - 1, column + lineGapCells); nextColumn++)
                {
                    int next = nextColumn + nextRow * columns;
                    if (visited[next] || !IsNear(next)) continue;
                    if (Mathf.Abs(CellDistances[next] - CellDistances[cell]) > maxDepthJump) continue;

                    visited[next] = true;
                    pending[pendingCount++] = next;
                }
            }
        }

        int widestRow = 0;
        for (int row = 0; row < rows; row++)
        {
            if (rowWidths[row] > widestRow) widestRow = rowWidths[row];
        }

        var cluster = new Cluster
        {
            size = size,
            widestRow = widestRow,
            highestRow = highestRow,
            lowestRow = lowestRow,
            rootColumnOffset = maxRootColumnOffset,
            nearest = nearestDistance,
            far = CellDistances[farCell],
        };

        if (size < minLineCells) cluster.verdict = Verdict.TooFew;
        else if (size > maxLineCells) cluster.verdict = Verdict.TooMany;
        else if (highestRow < topmostRow) cluster.verdict = Verdict.TooHigh;
        else if (lowestRow < rootFromRow) cluster.verdict = Verdict.NoRoot;
        else if (maxRootColumnOffset > rootMaxColumnOffset) cluster.verdict = Verdict.OffCenter;
        else if (nearestDistance > rootMaxDist) cluster.verdict = Verdict.RootTooFar;
        else if (widestRow > maxRowWidth) cluster.verdict = Verdict.TooWide;
        else cluster.verdict = Verdict.Pass;

        return cluster;
    }
}
