
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

    [Header("排除無效深度")]
    [Tooltip("還在深度相機範圍內的射線裡,有這個比例以上「一開始就被擋住」(ro),就當這一輪深度資料失效,整輪不算。2026-11-09 測試:全紫的圖都在 73% 以上,正常的圖最高 39%")]
    [Range(0f, 1f)] public float maxOccludedRatio = 0.5f;
    [Tooltip("整個畫面裡,眼睛正前方不到 Near Block Forward 的命中點達到這個數量,就當作深度資料不可靠,這一輪不算吹龍。0 = 不檢查")]
    public int maxNearBlockCells = 8;
    [Tooltip("上面那條規則用的距離(公尺,眼睛正前方)")]
    public float nearBlockForward = 0.125f;

    [Header("吹龍形狀")]
    [Tooltip("線必須碰到這一排或更下面的位置(0 = 最上排),而且靠近中央")]
    public int centerRootFromRow = 6;
    [Tooltip("線最下面那排所有點的平均位置,離中間那一欄最多幾欄")]
    public int centerRootMaxOffset = 2;
    [Tooltip("由下往上,每排的平均距離可以比下面一排少多少(公尺)還算越來越遠")]
    public float risingTolerance = 0.01f;

    [Header("排除手把")]
    [Tooltip("打到的點離左右手把(或手)太近就不算,用來擋掉手把伸到臉前造成的誤判")]
    public bool ignoreNearHands = true;
    [Tooltip("只有手把、沒有手部骨架時,離手把多近(公尺)內的命中不算。手把本體加上握著它的手,15 公分會漏出邊緣,所以用 22 公分")]
    public float handIgnoreRadius = 0.22f;
    [Tooltip("同時用手部追蹤的骨架(每隻手 26 個關節)排除手指和手掌。需要 OVRManager 開啟「同時追蹤手和手把」")]
    public bool useHandJoints = true;
    [Tooltip("離任何一個手部關節多近(公尺)內的命中不算。手指本身約 1 公分粗,加上深度圖邊緣的雜訊")]
    public float handJointRadius = 0.05f;
    [Tooltip("有手部骨架時,手把本體只要擋這麼大(公尺)就好,握著它的手交給關節去擋")]
    public float controllerRadiusWithHand = 0.12f;

    [Header("嘴巴位置")]
    [Tooltip("勾選 = 所有距離(有效距離、細線判定、龍尾、舌頭長度)改從嘴巴量,並改用下面「嘴巴模式門檻」那一組;"
           + "不勾 = 照舊從兩眼中間量,用上面原本那一組。射線本身都是從兩眼中間射出")]
    public bool measureFromMouth = true;
    [Tooltip("嘴巴相對兩眼中間的位置(公尺,頭的座標):y 負 = 往下、z 正 = 往前。不論有沒有勾選,格子圖都會另外記一份從這裡量的距離")]
    public Vector3 mouthOffset = new Vector3(0f, -0.09f, 0.05f);

    [Header("嘴巴模式門檻(公尺,勾 Measure From Mouth 時才用)")]
    [Tooltip("對應眼睛模式的 Min Dist。2026-11-07 測試:吹龍最靠近嘴巴的格子約 6~13 公分")]
    public float mouthMinDist = 0.05f;
    [Tooltip("對應眼睛模式的 Max Dist。測試時吹龍最遠的格子約 11~20 公分")]
    public float mouthMaxDist = 0.22f;
    [Tooltip("對應眼睛模式的 Root Max Dist:線最靠近嘴巴的一點不能比這個遠")]
    public float mouthRootMaxDist = 0.13f;
    [Tooltip("對應眼睛模式的 Retracted:龍尾離嘴巴這麼近時舌頭 = 0")]
    public float mouthRetracted = 0.10f;
    [Tooltip("對應眼睛模式的 Extended:龍尾離嘴巴這麼遠時舌頭 = 1。吹龍實際長約 13 公分")]
    public float mouthExtended = 0.20f;

    // 目前實際使用的門檻(依 measureFromMouth 選眼睛或嘴巴那一組)
    public float MinDist => measureFromMouth ? mouthMinDist : minDist;
    public float MaxDist => measureFromMouth ? mouthMaxDist : maxDist;
    public float RootMaxDist => measureFromMouth ? mouthRootMaxDist : rootMaxDist;
    public float Retracted => measureFromMouth ? mouthRetracted : retracted;
    public float Extended => measureFromMouth ? mouthExtended : extended;

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
    // 偵錯用:每格命中點到眼睛、到嘴巴的距離(沒打到 = 0)。CellDistances 等於其中一個,看 measureFromMouth
    public float[] CellEyeDistances { get; private set; } = new float[0];
    public float[] CellMouthDistances { get; private set; } = new float[0];
    // 偵錯用:每格命中點在眼睛正前方幾公尺(頭的座標 z,沒打到 = 0)
    public float[] CellForwardDistances { get; private set; } = new float[0];
    // 偵錯用:Meta 判定「不可靠」(oc)的命中點,離嘴巴和眼睛正前方的距離(不是 oc = 0)。只記錄,不參與判定
    public float[] CellOcMouthDistances { get; private set; } = new float[0];
    public float[] CellOcForwardDistances { get; private set; } = new float[0];
    public Vector3 CellPoint(int cell) => cellPoints[cell];
    // 這一輪還在深度相機範圍內的射線裡,一開始就被擋住(ro)的比例;超出範圍(fr)的比例
    public float OccludedRatio { get; private set; }
    public float OutOfFrustumRatio { get; private set; }
    // 這一輪深度資料失效,整輪不算
    public bool DepthInvalid { get; private set; }
    // 這一輪眼前很近的命中點數量(偵錯用,也用來判斷畫面是否不可靠)
    public int NearBlockCount { get; private set; }
    // 這一輪估計的嘴巴位置(世界座標)
    public Vector3 MouthPoint { get; private set; }
    public EnvironmentRaycastHitStatus[] CellStatuses { get; private set; } = new EnvironmentRaycastHitStatus[0];
    // 這格的命中點離手把太近,被排除
    public bool[] CellNearHand { get; private set; } = new bool[0];
    public int HandIgnoredCount { get; private set; }

    // 偵錯用:近距離的格子連成的每一塊,以及每一塊有沒有通過細線判定、沒過的原因
    public enum Verdict { Pass, TooFew, TooMany, TooHigh, NoRoot, OffCenter, RootTooFar, TooWide, NearBlock, NoCenterRoot, TipNotOnTop, NotRising }

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
    float[] rowSums = new float[0];
    float[] rowMaxes = new float[0];
    float[] rowColumnSums = new float[0];
    int frame;
    float lastLineTime = float.NegativeInfinity;
    float lastEvaluateTime = float.NegativeInfinity;
    OVRCameraRig rig;
    // 玩家縮放(head.lossyScale):距離門檻和手的半徑都是真實公尺,跟世界座標互換時要乘/除這個
    float worldScale = 1f;
    // 要排除的點(手把中心、手部關節)和各自的半徑平方
    readonly System.Collections.Generic.List<Vector3> handPositions = new System.Collections.Generic.List<Vector3>(64);
    readonly System.Collections.Generic.List<float> handRadiiSqr = new System.Collections.Generic.List<float>(64);
    OVRPlugin.HandState handState = new OVRPlugin.HandState();
    // 這一幀各手有沒有抓到骨架(偵錯用)
    public int TrackedHandSkeletons { get; private set; }

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
        // 玩家縮小時(OVRCameraRig 縮放 0.25),世界座標的距離 = 真實距離 x 縮放;這裡的距離設定都是真實公尺,要換算回去
        worldScale = Mathf.Max(0.0001f, head.lossyScale.x);
        // 用 TransformPoint,讓嘴巴偏移和命中點一樣跟著 rig 縮放
        Vector3 mouth = head.TransformPoint(mouthOffset);
        MouthPoint = mouth;
        UpdateHandPositions();
        int handIgnored = 0;
        int occluded = 0;
        int outOfFrustum = 0;

        for (int i = 0; i < columns; i++)
        {
            float yaw = YawOfColumn(i);
            for (int j = 0; j < rows; j++)
            {
                float pitch = PitchOfRow(j); // 正值 = 往下
                Vector3 dir = head.rotation * Quaternion.Euler(pitch, yaw, 0) * Vector3.forward;
                var ray = new Ray(origin, dir);

                bool didHit = raycastManager.Raycast(ray, out var hit, Mathf.Max(maxDist, measureDist) * worldScale);
                int cell = i + j * columns;
                CellStatuses[cell] = hit.status;
                CellEyeDistances[cell] = didHit ? Vector3.Distance(origin, hit.point) / worldScale : 0f;
                CellMouthDistances[cell] = didHit ? Vector3.Distance(mouth, hit.point) / worldScale : 0f;
                CellDistances[cell] = measureFromMouth ? CellMouthDistances[cell] : CellEyeDistances[cell];
                // InverseTransformPoint 已經把玩家縮放換算掉,結果是真實公尺
                CellForwardDistances[cell] = didHit ? head.InverseTransformPoint(hit.point).z : 0f;
                bool occludedHit = hit.status == EnvironmentRaycastHitStatus.HitPointOccluded;
                CellOcMouthDistances[cell] = occludedHit ? Vector3.Distance(mouth, hit.point) / worldScale : 0f;
                CellOcForwardDistances[cell] = occludedHit ? head.InverseTransformPoint(hit.point).z : 0f;
                cellPoints[cell] = hit.point;
                CellNearHand[cell] = didHit && IsNearHand(hit.point);
                if (CellNearHand[cell]) handIgnored++;
                if (hit.status == EnvironmentRaycastHitStatus.RayOccluded) occluded++;
                else if (hit.status == EnvironmentRaycastHitStatus.HitPointOutsideOfCameraFrustum) outOfFrustum++;
            }
        }

        HandIgnoredCount = handIgnored;
        int inFrustum = RayCount - outOfFrustum;
        OccludedRatio = inFrustum > 0 ? occluded / (float)inFrustum : 0f;
        OutOfFrustumRatio = RayCount > 0 ? outOfFrustum / (float)RayCount : 0f;
        DepthInvalid = OccludedRatio >= maxOccludedRatio;

        int nearBlock = 0;
        for (int cell = 0; cell < RayCount; cell++)
        {
            if (CellStatuses[cell] == EnvironmentRaycastHitStatus.Hit && CellForwardDistances[cell] < nearBlockForward)
                nearBlock++;
        }
        NearBlockCount = nearBlock;

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

    // 找出這一幀要排除的點:手把中心,加上手部追蹤骨架的每個關節。
    // 手把放下、沒在追蹤時就不排除。
    void UpdateHandPositions()
    {
        handPositions.Clear();
        handRadiiSqr.Clear();
        TrackedHandSkeletons = 0;
        if (!ignoreNearHands) return;

        if (rig == null) rig = FindFirstObjectByType<OVRCameraRig>();
        if (rig == null || rig.trackingSpace == null) return;

        AddHand(OVRPlugin.Hand.HandLeft, OVRInput.Controller.LTouch, OVRInput.Controller.LHand);
        AddHand(OVRPlugin.Hand.HandRight, OVRInput.Controller.RTouch, OVRInput.Controller.RHand);
    }

    void AddHand(OVRPlugin.Hand side, OVRInput.Controller touch, OVRInput.Controller hand)
    {
        Transform space = rig.trackingSpace;
        bool gotSkeleton = useHandJoints && AddHandJoints(side, space);
        if (gotSkeleton) TrackedHandSkeletons++;

        if (OVRInput.GetControllerPositionTracked(touch))
        {
            // 有骨架時手交給關節擋,手把只擋本體;沒有骨架就照舊用大球把手和手把一起包住
            float r = gotSkeleton ? controllerRadiusWithHand : handIgnoreRadius;
            Add(space.TransformPoint(OVRInput.GetLocalControllerPosition(touch)), r);
        }
        else if (!gotSkeleton && OVRInput.GetControllerPositionTracked(hand))
        {
            // 純手部追蹤但拿不到關節(例如舊版骨架):退回手腕一點加大球
            Add(space.TransformPoint(OVRInput.GetLocalControllerPosition(hand)), handIgnoreRadius);
        }
    }

    bool AddHandJoints(OVRPlugin.Hand side, Transform space)
    {
        if (!OVRPlugin.GetHandState(OVRPlugin.Step.Render, side, ref handState)) return false;
        if ((handState.Status & OVRPlugin.HandStatus.HandTracked) == 0) return false;

        var bones = handState.BonePositions;
        if (bones == null || bones.Length == 0)
        {
            // 拿不到各關節,只有手腕
            Add(space.TransformPoint(handState.RootPose.Position.FromFlippedZVector3f()), handIgnoreRadius);
            return true;
        }

        // OpenXR 骨架:關節位置和 RootPose 一樣在 tracking space
        for (int i = 0; i < bones.Length; i++)
            Add(space.TransformPoint(bones[i].FromFlippedZVector3f()), handJointRadius);
        return true;
    }

    void Add(Vector3 point, float radius)
    {
        handPositions.Add(point);
        float r = radius * worldScale;   // 半徑是真實公尺,換成世界座標
        handRadiiSqr.Add(r * r);
    }

    bool IsNearHand(Vector3 point)
    {
        for (int i = 0; i < handPositions.Count; i++)
        {
            if ((point - handPositions[i]).sqrMagnitude <= handRadiiSqr[i]) return true;
        }
        return false;
    }

    void EnsureBuffers()
    {
        if (CellDistances.Length == RayCount) return;

        HitPoints = new Vector3[RayCount];
        CellDistances = new float[RayCount];
        CellEyeDistances = new float[RayCount];
        CellMouthDistances = new float[RayCount];
        CellForwardDistances = new float[RayCount];
        CellOcMouthDistances = new float[RayCount];
        CellOcForwardDistances = new float[RayCount];
        CellStatuses = new EnvironmentRaycastHitStatus[RayCount];
        cellPoints = new Vector3[RayCount];
        visited = new bool[RayCount];
        pending = new int[RayCount];
        rowWidths = new int[rows];
        rowSums = new float[rows];
        rowMaxes = new float[rows];
        rowColumnSums = new float[rows];
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
            case Verdict.NearBlock: return "畫面近點太多";
            case Verdict.NoCenterRoot: return "沒從下方中間長出";
            case Verdict.TipNotOnTop: return "最遠點不在最上排";
            case Verdict.NotRising: return "沒有越往上越遠";
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
            case Verdict.RootTooFar: detail = $"最近{c.nearest * 100f:F0}cm > {RootMaxDist * 100f:F1}"; break;
            case Verdict.TooWide: detail = $"某排{c.widestRow}格 > {maxRowWidth}"; break;
            case Verdict.NearBlock: detail = $"{NearBlockCount}點 >= {maxNearBlockCells}"; break;
            case Verdict.NoCenterRoot: detail = $"最低第{c.lowestRow}排"; break;
            case Verdict.TipNotOnTop: detail = $"{c.size}格 龍尾{c.far * 100f:F0}cm"; break;
            case Verdict.NotRising: detail = $"{c.size}格 龍尾{c.far * 100f:F0}cm"; break;
            default: detail = $"{c.size}格 龍尾{c.far * 100f:F0}cm"; break;
        }
        return $"{(char)('A' + index)} {VerdictText(c.verdict)}({detail})";
    }

    // 一句話說明這一輪的結果:通過的那塊,或沒有通過時最大那一塊被擋的原因
    public string DescribeResult()
    {
        if (DepthInvalid) return "深度失效(ro " + (OccludedRatio * 100f).ToString("F0") + "%)";
        if (AcceptedCluster >= 0) return DescribeCluster(AcceptedCluster);
        if (ClusterCount == 0) return "沒打到 " + (MinDist * 100f).ToString("F0") + "~" + (MaxDist * 100f).ToString("F0") + "cm 內的東西";

        int biggest = 0;
        for (int i = 1; i < ClusterCount; i++)
            if (Clusters[i].size > Clusters[biggest].size) biggest = i;
        return DescribeCluster(biggest);
    }

    // 偵錯拍照用:這一輪有沒有射線打到 maxDistance(公尺)以內的東西。其他條件和 hits 相同,只是上限可以不同
    public bool HasHitWithin(float maxDistance)
    {
        if (DepthInvalid) return false;
        for (int cell = 0; cell < CellDistances.Length; cell++)
        {
            if (CellStatuses[cell] == EnvironmentRaycastHitStatus.Hit
                && !CellNearHand[cell]
                && CellDistances[cell] >= MinDist
                && CellDistances[cell] <= maxDistance)
                return true;
        }
        return false;
    }

    bool IsNear(int cell)
    {
        return !DepthInvalid
            && CellStatuses[cell] == EnvironmentRaycastHitStatus.Hit
            && !CellNearHand[cell]
            && CellDistances[cell] >= MinDist
            && CellDistances[cell] <= MaxDist;
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
            Length01 = Mathf.InverseLerp(Retracted, Extended, TipDistance);
        }
        else if (now - lastLineTime > lineHoldTime)
        {
            TipDistance = 0f;
            Length01 = 0f;
        }
    }

    // 這一塊由下往上,每排的平均距離是否越來越遠(至少要有兩排)
    bool IsRisingUpward()
    {
        int rowsUsed = 0;
        float previous = 0f;
        for (int row = rows - 1; row >= 0; row--)
        {
            if (rowWidths[row] == 0) continue;
            float average = rowSums[row] / rowWidths[row];
            if (rowsUsed > 0 && average < previous - risingTolerance) return false;
            previous = average;
            rowsUsed++;
        }
        return rowsUsed >= 2;
    }

    // 從 start 把相鄰且距離接近的格子連起來,標成第 id 塊,回傳這一塊的形狀與是否符合細線的條件。
    Cluster MeasureConnectedCells(int start, int id, out int farCell)
    {
        System.Array.Clear(rowWidths, 0, rows);
        System.Array.Clear(rowSums, 0, rows);
        System.Array.Clear(rowMaxes, 0, rows);
        System.Array.Clear(rowColumnSums, 0, rows);
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
            rowSums[row] += CellDistances[cell];
            if (CellDistances[cell] > rowMaxes[row]) rowMaxes[row] = CellDistances[cell];
            rowColumnSums[row] += column;
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

        // 根部:線最下面那排的平均位置要夠低、夠靠中央
        bool rootInCenter = lowestRow >= centerRootFromRow
            && Mathf.Abs(rowColumnSums[lowestRow] / rowWidths[lowestRow] - centerColumn) <= centerRootMaxOffset;

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
        else if (nearestDistance > RootMaxDist) cluster.verdict = Verdict.RootTooFar;
        else if (widestRow > maxRowWidth) cluster.verdict = Verdict.TooWide;
        else if (maxNearBlockCells > 0 && NearBlockCount >= maxNearBlockCells) cluster.verdict = Verdict.NearBlock;
        else if (!rootInCenter) cluster.verdict = Verdict.NoCenterRoot;
        else if (rowMaxes[highestRow] < cluster.far - 0.005f) cluster.verdict = Verdict.TipNotOnTop;
        else if (!IsRisingUpward()) cluster.verdict = Verdict.NotRising;
        else cluster.verdict = Verdict.Pass;

        return cluster;
    }
}
