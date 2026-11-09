using System.Collections.Generic;
using UnityEngine;

// 螞蟻:有 Path 就沿路線走,沒有就隨機遊走;舌尖靠近會逃,附近的螞蟻也跟著逃;逃完走回最近的路徑點;
// 被抓住的那隻不動;被吃掉後過幾秒重生。
// 掛在每個 AntRoot 上。不需要手動把螞蟻拖進 TongueTrigger,會自動登記。
public class AntWander : MonoBehaviour, ITongueEdible
{
    public static readonly List<AntWander> All = new List<AntWander>();
    public static Transform Threat;          // 舌尖(由 TongueTrigger 在伸出時設定,縮回時設 null)
    public static int EatenCount;

    [Header("吃掉獲得")]
    public float toxinAmount = 1f;           // 吃一隻螞蟻得到多少毒素

    [Header("遊走")]
    public float wanderRadius = 1.0f;
    public float wanderSpeed = 0.15f;
    public Vector2 retargetSeconds = new Vector2(1.5f, 4f);

    [Header("逃跑")]
    public float fleeTipRadius = 0.5f;       // 舌尖靠近多近會逃(水平距離)
    public float fleeTipHeight = 0.6f;       // 舌尖和螞蟻的高度差在這之內才算靠近
    public float alarmRadius = 1.0f;         // 一隻逃時,多近的鄰居也逃
    public float eatAlarmRadius = 1.5f;      // 有螞蟻被吃掉時,離牠被抓的地方多近的螞蟻會逃
    public float fleeSpeed = 0.8f;
    public float fleeSeconds = 1.5f;         // 沒有 Path 的螞蟻才用這個

    [Header("受驚(有 Path 的螞蟻)")]
    public float burstSeconds = 4f;          // 爆衝多久,速度用 Flee Speed
    public float panicSeconds = 16f;         // 爆衝後亂竄多久
    public float panicSpeed = 0.35f;
    public Vector2 panicMoveSeconds = new Vector2(0.4f, 1.5f);  // 亂竄時每段跑多久
    public Vector2 panicStopSeconds = new Vector2(0.1f, 0.5f);  // 亂竄時每次停多久
    public float scatterRadius = 3.5f;       // 離開路線的位置最遠能跑多遠
    public float scatterAngleDeg = 35f;      // 爆衝方向隨機偏多少度,讓每隻散開
    public float returnSpeed = 0.3f;         // 騷動結束後走回路線的速度

    [Header("重生")]
    public float respawnSeconds = 4f;

    [Header("路線")]
    public AntPath path;                     // 沒指定就隨機遊走
    public int startIndex;
    public Vector2 pauseSeconds = new Vector2(0.5f, 2f);    // 到達路徑點後停多久

    [Header("貼地")]
    public LayerMask groundMask;             // 哪些 Layer 算地面;Nothing = 不貼地
    public float groundOffset = 0f;          // 模型陷進地面時調高
    [Tooltip("自動把模型最低點(腳)對齊地面。模型的原點不在腳底時,不勾會浮在空中或陷進地面")]
    public bool autoFootOffset = true;
    [Tooltip("身體跟著地面坡度傾斜(前後左右各量一次地面高度),斜坡上頭尾都貼地")]
    public bool alignToSlope = true;
    [Tooltip("一步能上下的高度(公尺)。前方地面高低差超過這個值就當作峭壁邊緣,不走過去。0 = 不限制")]
    public float maxStepHeight = 0f;

    [Header("模型方向")]
    public float yawOffsetDeg;               // 模型前方不是 +Z 時用來修正

    const float GroundCastHeight = 0.5f;
    const float GroundCastDistance = 20f;
    const float StripLookAhead = 0.5f;

    static readonly RaycastHit[] groundHits = new RaycastHit[16];
    static bool warnedNoGroundMask;

    public bool IsCaught { get; private set; }
    public bool IsFleeing => Time.time < fleeUntil;
    public bool IsEaten => eaten;

    // 螞蟻頭的水平方向(已扣掉模型的 yawOffsetDeg)
    public Vector3 BodyForward
    {
        get
        {
            Vector3 f = transform.rotation * Quaternion.Euler(0, -yawOffsetDeg, 0) * Vector3.forward;
            f.y = 0;
            return f.sqrMagnitude < 0.0001f ? Vector3.forward : f.normalized;
        }
    }

    Vector3 home;
    Vector3 target;
    float footOffset;                    // 模型最低點比原點高多少(世界座標,直立時)
    Quaternion yawRotation = Quaternion.identity;   // 只有水平方向的轉向;實際旋轉 = 坡度傾斜 * 這個
    Quaternion slopeTilt = Quaternion.identity;
    float bodyHalfLength = 0.05f, bodyHalfWidth = 0.03f;
    float nextRetarget;
    float fleeUntil;
    Transform holder;
    Renderer[] renderers;
    bool eaten;
    int pathIndex;
    int pathDir = 1;
    float pauseUntil;
    bool wasFleeing;
    Vector3 fleeAnchor;      // 離開路線的位置,逃跑距離以這裡為中心
    Vector3 lastFleeDir;
    bool hadGround;
    float burstUntil;
    float burstAngle;
    bool panicMoving;
    float panicNextChange;
    Vector3 panicDir;
    bool returning;
    Vector3 caughtAt;
    float laneTarget;        // 帶狀通道裡目前想走的左右位置
    float blockedTime;       // 被峭壁邊緣擋住多久了

    bool HasPath => path != null && path.Points.Length > 0;

    void OnEnable() { All.Add(this); }
    void OnDisable() { All.Remove(this); }

    void Start()
    {
        home = transform.position;
        renderers = GetComponentsInChildren<Renderer>();
        MeasureModel();
        Vector3 f0 = transform.forward; f0.y = 0f;
        yawRotation = f0.sqrMagnitude > 0.0001f ? Quaternion.LookRotation(f0.normalized, Vector3.up) : Quaternion.identity;
        if (HasPath)
        {
            // 帶狀通道:開場散布在整條通道上,不要全部擠在入口
            if (path.IsStrip) PlaceInStrip(Random.Range(0f, path.StripLength));
            else MoveToStartPoint();
        }
        PickTarget();
        SnapNow();

        if (groundMask.value == 0 && !warnedNoGroundMask)
        {
            warnedNoGroundMask = true;
            Debug.LogWarning("[AntWander] Ground Mask 是 Nothing,螞蟻不會貼地。請在 Inspector 選擇地面的 Layer。", this);
        }
    }

    void Update()
    {
        if (eaten) return;

        if (IsCaught)
        {
            if (holder != null) transform.position = holder.position;
            return;
        }

        // 舌尖靠近 → 自己逃,並通知附近的
        if (Threat != null)
        {
            float d = Vector3.Distance(FlatPos(Threat.position), FlatPos(transform.position));
            float h = Mathf.Abs(Threat.position.y - transform.position.y);
            if (h <= fleeTipHeight && d < fleeTipRadius) Scare();
        }

        Vector3 pos = transform.position;
        Vector3 move = Vector3.zero;
        bool fleeing = IsFleeing;
        bool onPath = HasPath;

        if (onPath)
        {
            if (fleeing && !wasFleeing) { fleeAnchor = pos; returning = false; }
            else if (!fleeing && wasFleeing)
            {
                if (!path.IsStrip) pathIndex = NearestPointIndex(pos);
                pauseUntil = 0f;
                returning = true;
            }
        }
        wasFleeing = fleeing;

        if (fleeing && onPath)
        {
            move = PanicMove(pos);
        }
        else if (fleeing && Threat != null)
        {
            Vector3 away = FlatPos(pos) - FlatPos(Threat.position);
            if (away.sqrMagnitude < 0.0001f) away = Random.insideUnitSphere;
            away.y = 0;
            move = away.normalized * fleeSpeed;
        }
        else if (fleeing)
        {
            move = (target - pos).normalized * fleeSpeed; // 被鄰居驚動但舌頭已縮回:亂跑一下
        }
        else if (!onPath)
        {
            if (Time.time > nextRetarget || (target - pos).sqrMagnitude < 0.01f) PickTarget();
            move = (target - pos).normalized * wanderSpeed;
        }

        Vector3 next;
        if (onPath && !fleeing)
        {
            next = path.IsStrip ? StepAlongStrip(pos, out move) : StepAlongPath(pos, out move);
        }
        else
        {
            move.y = 0;
            next = pos + move * Time.deltaTime;

            // 不要跑太遠:有路線時以離開路線的位置為中心,否則以家為中心
            Vector3 center = onPath ? fleeAnchor : home;
            float limit = onPath ? scatterRadius : wanderRadius * 1.5f;
            Vector3 off = FlatPos(next) - FlatPos(center);
            if (off.magnitude > limit) next = new Vector3(center.x, pos.y, center.z) + off.normalized * limit;

            // 逃跑時不要跑到沒有地面的地方
            if (onPath && hadGround && !TryGetGroundY(next, out _)) next = pos;
        }

        bool wasGrounded = hadGround;
        hadGround = TryGetGroundY(next, out float groundY);

        // 高低差太大 = 峭壁邊緣:停在原地換方向;沿路線走卻卡住太久就從起點重來
        if (wasGrounded && hadGround && maxStepHeight > 0f && Mathf.Abs(groundY - pos.y) > maxStepHeight)
        {
            next = pos;
            groundY = pos.y;
            if (!onPath) PickTarget();
            lastFleeDir = -lastFleeDir;
            panicDir = -panicDir;
            blockedTime += Time.deltaTime;
            if (onPath && !fleeing && blockedTime > 2f)
            {
                if (path.IsStrip) PlaceInStrip(0f); else MoveToStartPoint();
                returning = false;
                blockedTime = 0f;
                SnapNow();
                return;
            }
        }
        else blockedTime = 0f;

        transform.position = new Vector3(next.x, hadGround ? groundY : pos.y, next.z);
        Vector3 flatMove = new Vector3(move.x, 0f, move.z);
        if (flatMove.sqrMagnitude > 0.0001f)
        {
            Quaternion look = Quaternion.LookRotation(flatMove.normalized, Vector3.up) * Quaternion.Euler(0, yawOffsetDeg, 0);
            yawRotation = Quaternion.Slerp(yawRotation, look, 8f * Time.deltaTime);
        }
        ApplyRotation(hadGround);
    }

    // 模型的腳在哪裡:用每個網格的範圍(不用讀頂點,打包後網格不一定能讀)換算到螞蟻自己的座標
    void MeasureModel()
    {
        footOffset = 0f;
        var filters = GetComponentsInChildren<MeshFilter>(true);
        if (filters.Length == 0) return;
        Matrix4x4 toRoot = transform.worldToLocalMatrix;
        float minY = float.MaxValue;
        Bounds local = new Bounds();
        bool first = true;
        foreach (var mf in filters)
        {
            if (mf.sharedMesh == null) continue;
            Matrix4x4 m = toRoot * mf.transform.localToWorldMatrix;
            Bounds b = mf.sharedMesh.bounds;
            for (int i = 0; i < 8; i++)
            {
                Vector3 c = b.center + Vector3.Scale(b.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                Vector3 p = m.MultiplyPoint3x4(c);
                minY = Mathf.Min(minY, p.y);
                if (first) { local = new Bounds(p, Vector3.zero); first = false; } else local.Encapsulate(p);
            }
        }
        if (first) return;
        Vector3 s = transform.lossyScale;
        if (autoFootOffset) footOffset = minY * s.y;
        // 身體前後/左右的一半長度(量坡度用),扣掉 yawOffsetDeg 的方向
        Vector3 size = Vector3.Scale(local.size, s);
        float yaw = Mathf.Abs(Mathf.DeltaAngle(0f, yawOffsetDeg));
        bool sideways = yaw > 45f && yaw < 135f;
        bodyHalfLength = Mathf.Max(0.005f, 0.5f * (sideways ? size.x : size.z));
        bodyHalfWidth = Mathf.Max(0.005f, 0.5f * (sideways ? size.z : size.x));
    }

    // 旋轉 = 坡度傾斜 * 水平轉向。前後、左右各量一次地面高度,身體跟著地面斜
    void ApplyRotation(bool grounded)
    {
        Quaternion tilt = Quaternion.identity;
        if (alignToSlope && grounded)
        {
            Vector3 p = transform.position;
            Vector3 fwd = BodyForwardOf(yawRotation);
            Vector3 side = Vector3.Cross(Vector3.up, fwd);
            if (TryGetGroundY(p + fwd * bodyHalfLength, out float yf) && TryGetGroundY(p - fwd * bodyHalfLength, out float yb) &&
                TryGetGroundY(p + side * bodyHalfWidth, out float yr) && TryGetGroundY(p - side * bodyHalfWidth, out float yl))
            {
                Vector3 alongF = fwd * (2f * bodyHalfLength) + Vector3.up * (yf - yb);
                Vector3 alongS = side * (2f * bodyHalfWidth) + Vector3.up * (yr - yl);
                Vector3 n = Vector3.Cross(alongF, alongS).normalized;
                if (n.y < 0f) n = -n;
                if (Vector3.Angle(n, Vector3.up) < 50f) tilt = Quaternion.FromToRotation(Vector3.up, n);   // 太陡(峭壁邊緣)就不斜
            }
        }
        slopeTilt = Quaternion.Slerp(slopeTilt, tilt, 10f * Time.deltaTime);
        transform.rotation = slopeTilt * yawRotation;
    }

    Vector3 BodyForwardOf(Quaternion r)
    {
        Vector3 f = r * Quaternion.Euler(0, -yawOffsetDeg, 0) * Vector3.forward;
        f.y = 0;
        return f.sqrMagnitude < 0.0001f ? Vector3.forward : f.normalized;
    }

    // 有路線的螞蟻受驚時的速度:先爆衝遠離舌尖,之後忽跑忽停地亂竄。
    Vector3 PanicMove(Vector3 pos)
    {
        if (Time.time < burstUntil)
        {
            if (Threat != null)
            {
                Vector3 away = FlatPos(pos) - FlatPos(Threat.position);
                if (away.sqrMagnitude < 0.0001f) away = RandomFlatDir();
                lastFleeDir = Quaternion.Euler(0, burstAngle, 0) * away.normalized;
            }
            else if (lastFleeDir == Vector3.zero)
            {
                lastFleeDir = RandomFlatDir();
            }
            return lastFleeDir * fleeSpeed;
        }

        if (Time.time >= panicNextChange)
        {
            panicMoving = !panicMoving;
            if (panicMoving)
            {
                panicDir = RandomFlatDir();
                panicNextChange = Time.time + Random.Range(panicMoveSeconds.x, panicMoveSeconds.y);
            }
            else
            {
                panicNextChange = Time.time + Random.Range(panicStopSeconds.x, panicStopSeconds.y);
            }
        }
        if (!panicMoving) return Vector3.zero;

        // 快到範圍邊界就往回跑,不要貼著邊滑
        Vector3 off = FlatPos(pos) - FlatPos(fleeAnchor);
        if (off.magnitude > scatterRadius * 0.9f) panicDir = -off.normalized;
        return panicDir * panicSpeed;
    }

    static Vector3 RandomFlatDir()
    {
        Vector2 r = Random.insideUnitCircle.normalized;
        return r == Vector2.zero ? Vector3.forward : new Vector3(r.x, 0, r.y);
    }

    // 沿路線走一步;只有到達路徑點才換下一個目標。回傳下一幀位置,move 是這一步的速度。
    Vector3 StepAlongPath(Vector3 pos, out Vector3 move)
    {
        move = Vector3.zero;
        if (Time.time < pauseUntil) return pos;

        Vector3 goal = path.Points[pathIndex].position;
        goal.y = pos.y;     // 只看水平距離,路徑點高度不準也走得到

        float speed = returning ? returnSpeed : wanderSpeed;
        Vector3 next = Vector3.MoveTowards(pos, goal, speed * Time.deltaTime);
        if (goal != pos) move = (goal - pos).normalized * speed;

        if ((goal - next).sqrMagnitude < 0.000001f)
        {
            returning = false;
            AdvancePathIndex();
            pauseUntil = Time.time + Random.Range(pauseSeconds.x, pauseSeconds.y);
        }
        return next;
    }

    void AdvancePathIndex()
    {
        int n = path.Points.Length;
        if (n <= 1) return;

        if (path.loop)
        {
            pathIndex = (pathIndex + pathDir + n) % n;
        }
        else
        {
            if (pathIndex + pathDir < 0 || pathIndex + pathDir >= n) pathDir = -pathDir;
            pathIndex += pathDir;
        }
    }

    int NearestPointIndex(Vector3 pos)
    {
        var pts = path.Points;
        int best = 0;
        float bestD = float.MaxValue;
        for (int i = 0; i < pts.Length; i++)
        {
            float d = (FlatPos(pts[i].position) - FlatPos(pos)).sqrMagnitude;
            if (d < bestD) { bestD = d; best = i; }
        }
        return best;
    }

    // 帶狀通道走一步:單向前進,左右在寬度內慢慢飄;走到底從入口補進來。
    Vector3 StepAlongStrip(Vector3 pos, out Vector3 move)
    {
        move = Vector3.zero;
        path.StripCoords(pos, out float along, out float lateral);
        float half = path.stripWidth * 0.5f;

        // 逃完先走回通道內最近的位置
        if (returning)
        {
            Vector3 back = path.StripPoint(Mathf.Clamp(along, 0f, path.StripLength), Mathf.Clamp(lateral, -half, half));
            back.y = pos.y;
            Vector3 stepped = Vector3.MoveTowards(pos, back, returnSpeed * Time.deltaTime);
            if (back != pos) move = (back - pos).normalized * returnSpeed;
            if ((back - stepped).sqrMagnitude < 0.000001f)
            {
                returning = false;
                laneTarget = Mathf.Clamp(lateral, -half, half);
            }
            return stepped;
        }

        if (along >= path.StripLength)
        {
            PlaceInStrip(0f);
            return transform.position;
        }

        if (Time.time > nextRetarget)
        {
            laneTarget = Random.Range(-half, half);
            nextRetarget = Time.time + Random.Range(retargetSeconds.x, retargetSeconds.y);
        }

        Vector3 goal = path.StripPoint(along + StripLookAhead, laneTarget);
        goal.y = pos.y;
        move = (goal - pos).normalized * wanderSpeed;
        return pos + move * Time.deltaTime;
    }

    void PlaceInStrip(float along)
    {
        float half = path.stripWidth * 0.5f;
        laneTarget = Random.Range(-half, half);
        transform.position = path.StripPoint(along, laneTarget);
        yawRotation = Quaternion.LookRotation(path.StripDir, Vector3.up) * Quaternion.Euler(0, yawOffsetDeg, 0);
        transform.rotation = slopeTilt * yawRotation;
        returning = false;
    }

    void MoveToStartPoint()
    {
        pathIndex = Mathf.Clamp(startIndex, 0, path.Points.Length - 1);
        pathDir = 1;
        pauseUntil = 0f;
        transform.position = path.Points[pathIndex].position;
    }

    // 從螞蟻上方往下射,找最近的地面;略過自己的碰撞體和 Trigger。
    bool TryGetGroundY(Vector3 p, out float y)
    {
        y = 0f;
        if (groundMask.value == 0) return false;

        Vector3 origin = p + Vector3.up * GroundCastHeight;
        int count = Physics.RaycastNonAlloc(origin, Vector3.down, groundHits, GroundCastDistance, groundMask, QueryTriggerInteraction.Ignore);

        float bestD = float.MaxValue;
        bool found = false;
        for (int i = 0; i < count; i++)
        {
            if (groundHits[i].collider.transform.IsChildOf(transform)) continue;
            if (groundHits[i].distance < bestD)
            {
                bestD = groundHits[i].distance;
                y = groundHits[i].point.y + groundOffset - footOffset;
                found = true;
            }
        }
        return found;
    }

    void SnapNow()
    {
        hadGround = TryGetGroundY(transform.position, out float groundY);
        if (hadGround)
        {
            Vector3 p = transform.position;
            p.y = groundY;
            transform.position = p;
        }
    }

    public void Scare()
    {
        BeginFlee();
        foreach (var other in All)
        {
            if (other == this || other.IsCaught || other.eaten) continue;
            if (Vector3.Distance(other.transform.position, transform.position) < alarmRadius)
                other.BeginFlee();
        }
    }

    // 再次被嚇會重新從爆衝開始算;舌尖一直在附近時每幀都會呼叫,所以只在新一輪爆衝才重抽方向。
    void BeginFlee()
    {
        if (!HasPath)
        {
            fleeUntil = Time.time + fleeSeconds;
            return;
        }

        if (Time.time >= burstUntil)
        {
            burstAngle = Random.Range(-scatterAngleDeg, scatterAngleDeg);
            lastFleeDir = Vector3.zero;
            panicMoving = false;
            panicNextChange = 0f;
        }
        burstUntil = Time.time + burstSeconds;
        fleeUntil = burstUntil + panicSeconds;
    }

    // 同伴被吃掉:遠離牠被抓的地方。舌頭這時已縮回,所以方向要在這裡先決定。
    void AlarmFromEat()
    {
        foreach (var other in All)
        {
            if (other == this || other.IsCaught || other.eaten) continue;
            Vector3 away = FlatPos(other.transform.position) - FlatPos(caughtAt);
            if (Vector3.Distance(other.transform.position, caughtAt) >= eatAlarmRadius) continue;

            other.BeginFlee();
            other.lastFleeDir = away.sqrMagnitude < 0.0001f ? RandomFlatDir() : Quaternion.Euler(0, other.burstAngle, 0) * away.normalized;
        }
    }

    // ---- ITongueEdible:讓 TongueTrigger 用同一套流程處理螞蟻、水珠、礦石 ----
    public bool CanBeCaught => isActiveAndEnabled && !IsCaught && !eaten;
    public float CatchValue(TongueTrigger tongue, Vector3 tipPosition) => tongue.CatchValue(this, tipPosition);
    public void OnCaught(Transform tip) => SetCaught(tip);
    public void OnEaten()
    {
        Vector3 where = transform.position;
        Eat();
        LizardResources.Add(ResourceKind.Toxin, toxinAmount, where);
    }
    public string DisplayName => name;

    public void SetCaught(Transform by) { IsCaught = true; holder = by; caughtAt = transform.position; }

    public void Release() { IsCaught = false; holder = null; }

    public void Eat()
    {
        IsCaught = false;
        holder = null;
        eaten = true;
        EatenCount++;
        AlarmFromEat();
        SetVisible(false);
        Invoke(nameof(Respawn), respawnSeconds);
    }

    void Respawn()
    {
        if (HasPath)
        {
            if (path.IsStrip) PlaceInStrip(0f);
            else MoveToStartPoint();
            fleeUntil = 0f;
            burstUntil = 0f;
            wasFleeing = false;
            returning = false;
        }
        else
        {
            Vector2 r = Random.insideUnitCircle * wanderRadius;
            transform.position = new Vector3(home.x + r.x, home.y, home.z + r.y);
        }
        eaten = false;
        SetVisible(true);
        PickTarget();
        SnapNow();
    }

    void PickTarget()
    {
        Vector2 r = Random.insideUnitCircle * wanderRadius;
        target = new Vector3(home.x + r.x, transform.position.y, home.z + r.y);
        nextRetarget = Time.time + Random.Range(retargetSeconds.x, retargetSeconds.y);
    }

    void SetVisible(bool v) { foreach (var rd in renderers) rd.enabled = v; }

    static Vector3 FlatPos(Vector3 p) { return new Vector3(p.x, 0, p.z); }
}