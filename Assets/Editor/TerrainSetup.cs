using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

// 選單 Lizard > Setup Terrain (Cliffs + Ants):對目前開啟的場景
//   1. 分析背景「場景3」每個三角面的坡度(用目前的旋轉/縮放,所以要先擺好背景)
//      - 坡度 < ClimbAngleDeg            → 地面碰撞體 _GroundCollider(只能水平爬)
//      - 坡度 >= ClimbAngleDeg 且相連的陡面高度 >= MinCliffHeight → 峭壁碰撞體 _CliffCollider(Tag = Climbable)
//      - 太矮的陡面(小台階)當地面
//   2. Player 垂直貼到腳下的地面
//   3. 重新放螞蟻:在 Player 前方找一段平坦的地面當路線,另外幾隻在附近遊走
//   4. 報告寫到 Console 和 Logs/TerrainReport.txt
// 背景擺放改過之後重新執行一次即可。
public static class TerrainSetup
{
    public const string BackgroundName = "Background_場景3";
    public static readonly string[] LegacyBackgroundNames = { "Background_場景2.0", "Background_匯入測試" };   // 舊背景(換場景時用)
    const string GeneratedAsset = "Assets/Generated/FullTestTerrainColliders.asset";
    const string ReportPath = "Logs/TerrainReport.txt";
    const string GroundChild = "_GroundCollider";
    const string CliffChild = "_CliffCollider";
    const string ClimbableTag = "Climbable";

    // ---- 可調整的數值 ----
    public const float ClimbAngleDeg = 60f;   // 比這個陡就算峭壁
    public const float MinCliffHeight = 0.4f; // 相連陡面的高度至少這麼高才能爬(公尺)
    const float WeldSize = 0.01f;             // 判斷三角面相連時,頂點距離小於 1 公分視為同一點

    public const float AntLength = 0.15f;
    const int TrailAntCount = 6;
    const int WanderAntCount = 3;
    static float TrailDistance => 0.8f * PlayerSize.TongueLength;   // 路線離玩家多遠(舌頭搆得到)
    const float TrailHalfLength = 2f;
    const float TrailWidth = 0.6f;
    const float AntMaxStep = 0.08f;           // 螞蟻一步能上下的高度,超過就當作峭壁邊緣不走過去

    const int DefaultLayer = 0;
    const int DefaultMask = 1 << DefaultLayer;

    [MenuItem("Lizard/Setup Terrain (Cliffs + Ants)")]
    public static void SetupFromMenu()
    {
        Scene scene = SceneManager.GetActiveScene();
        var report = new StringBuilder($"[TerrainSetup] 場景 {scene.name}  {System.DateTime.Now}\n");
        try
        {
            var bg = scene.GetRootGameObjects().FirstOrDefault(g => g.name == BackgroundName);
            if (bg == null)
            {
                EditorUtility.DisplayDialog("Setup Terrain", $"目前場景找不到 {BackgroundName}。請先開啟 FullTest.unity。", "OK");
                return;
            }
            if (!ClassifyTerrain(bg, report)) return;

            var player = scene.GetRootGameObjects().FirstOrDefault(g => g.GetComponent<LizardLocomotion>() != null);
            if (player == null)
            {
                report.AppendLine("⚠ 找不到掛 LizardLocomotion 的 Player,略過放螞蟻");
            }
            else
            {
                if (!PlaceAtSpawn(player.transform, bg, report)) SnapPlayerToGround(player.transform, report);
                PlaceAnts(scene, player.transform, report);
            }

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
        }
        finally
        {
            EditorUtility.ClearProgressBar();
            WriteReport(report);
        }
    }

    // =================================================================
    // 1. 地形分類
    // =================================================================

    class Cluster { public float minY = float.MaxValue, maxY = float.MinValue, area; public Vector3 centerSum; public int tris; public string mesh; }

    public static bool ClassifyTerrain(GameObject bg, StringBuilder report)
    {
        var filters = bg.GetComponentsInChildren<MeshFilter>(true).Where(f => f.sharedMesh != null).ToList();
        if (!EnsureReadable(filters, report)) return false;

        // 標記成 地面 / 地洞頂 / 地洞底 的物件(Lizard > 標記工具):碰撞體做成雙面,
        // 玩家不會從模型背面穿過去(SketchUp 的面常常方向不一,單面 MeshCollider 從背面撞不到)
        var marks = LevelMarks.ResolveForSetup(bg, report);
        int autoOpen = 0, autoInverted = 0;   // 自動判斷要雙面的物件數
        var doors = new HashSet<MeshFilter>(LevelMarks.Of(marks, MarkKind.Door));   // 可穿過:不產生碰撞體、隱藏
        var doubleSided = new HashSet<MeshFilter>(marks.Where(p => p.Value.Contains(MarkKind.Ground) ||
            p.Value.Contains(MarkKind.CaveCeiling) || p.Value.Contains(MarkKind.CaveFloor) || p.Value.Contains(MarkKind.Spawn) ||
            p.Value.Contains(MarkKind.Stepping)).Select(p => p.Key));

        // 清掉上一次產生的東西
        foreach (var t in bg.GetComponentsInChildren<Transform>(true).Where(t => t.name == GroundChild || t.name == CliffChild).ToList())
            Object.DestroyImmediate(t.gameObject);
        if (AssetDatabase.LoadMainAssetAtPath(GeneratedAsset) != null) AssetDatabase.DeleteAsset(GeneratedAsset);
        Directory.CreateDirectory(Path.GetDirectoryName(GeneratedAsset));

        Object container = null;
        float cosClimb = Mathf.Cos(ClimbAngleDeg * Mathf.Deg2Rad);
        float flatArea = 0, slopeArea = 0, cliffArea = 0, shortSteepArea = 0;
        int groundTris = 0, cliffTris = 0;
        var cliffs = new List<Cluster>();
        Bounds world = new Bounds();
        bool boundsInit = false;

        for (int fi = 0; fi < filters.Count; fi++)
        {
            var mf = filters[fi];
            EditorUtility.DisplayProgressBar("Setup Terrain", $"分析坡度 {fi + 1}/{filters.Count}  {mf.name}", (float)fi / filters.Count * 0.8f);

            bool isDoor = doors.Contains(mf);
            var rend = mf.GetComponent<Renderer>();
            if (rend != null && rend.enabled == isDoor) { rend.enabled = !isDoor; EditorUtility.SetDirty(rend); }
            if (isDoor)
            {
                foreach (var col in mf.GetComponents<MeshCollider>()) col.enabled = false;
                continue;
            }

            Mesh mesh = mf.sharedMesh;
            Vector3[] local = mesh.vertices;
            var tris = new List<int>();
            for (int s = 0; s < mesh.subMeshCount; s++) tris.AddRange(mesh.GetTriangles(s));
            int triCount = tris.Count / 3;
            if (triCount == 0) continue;

            Matrix4x4 l2w = mf.transform.localToWorldMatrix;
            var w = new Vector3[local.Length];
            for (int i = 0; i < local.Length; i++)
            {
                w[i] = l2w.MultiplyPoint3x4(local[i]);
                if (!boundsInit) { world = new Bounds(w[i], Vector3.zero); boundsInit = true; }
                else world.Encapsulate(w[i]);
            }

            // 每個三角面的坡度與面積(用 |normal.y|,SketchUp 反面也算對)
            var steep = new bool[triCount];
            var area = new float[triCount];
            for (int t = 0; t < triCount; t++)
            {
                Vector3 a = w[tris[t * 3]], b = w[tris[t * 3 + 1]], c = w[tris[t * 3 + 2]];
                Vector3 n = Vector3.Cross(b - a, c - a);
                float len = n.magnitude;
                if (len < 1e-10f) continue;
                area[t] = len * 0.5f;
                float ny = Mathf.Abs(n.y) / len;
                steep[t] = ny < cosClimb;
                if (!steep[t]) { if (ny > 0.966f) flatArea += area[t]; else slopeArea += area[t]; }
            }

            // 相連的陡面分群(頂點位置焊接後共用就算相連)
            var weldId = new int[w.Length];
            var weldMap = new Dictionary<Vector3Int, int>();
            for (int i = 0; i < w.Length; i++)
            {
                var key = Vector3Int.RoundToInt(w[i] / WeldSize);
                if (!weldMap.TryGetValue(key, out int id)) { id = weldMap.Count; weldMap[key] = id; }
                weldId[i] = id;
            }
            var parent = Enumerable.Range(0, triCount).ToArray();
            int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
            var owner = new int[weldMap.Count];
            for (int i = 0; i < owner.Length; i++) owner[i] = -1;
            for (int t = 0; t < triCount; t++)
            {
                if (!steep[t]) continue;
                for (int k = 0; k < 3; k++)
                {
                    int id = weldId[tris[t * 3 + k]];
                    if (owner[id] < 0) owner[id] = t;
                    else { int ra = Find(t), rb = Find(owner[id]); if (ra != rb) parent[ra] = rb; }
                }
            }
            var clusters = new Dictionary<int, Cluster>();
            for (int t = 0; t < triCount; t++)
            {
                if (!steep[t]) continue;
                int r = Find(t);
                if (!clusters.TryGetValue(r, out var cl)) clusters[r] = cl = new Cluster { mesh = mf.name };
                for (int k = 0; k < 3; k++)
                {
                    float y = w[tris[t * 3 + k]].y;
                    cl.minY = Mathf.Min(cl.minY, y);
                    cl.maxY = Mathf.Max(cl.maxY, y);
                }
                cl.area += area[t];
                cl.centerSum += (w[tris[t * 3]] + w[tris[t * 3 + 1]] + w[tris[t * 3 + 2]]) / 3f * area[t];
                cl.tris++;
            }

            var groundList = new List<int>();
            var cliffList = new List<int>();
            for (int t = 0; t < triCount; t++)
            {
                bool climb = steep[t] && clusters[Find(t)].maxY - clusters[Find(t)].minY >= MinCliffHeight;
                var dst = climb ? cliffList : groundList;
                dst.Add(tris[t * 3]); dst.Add(tris[t * 3 + 1]); dst.Add(tris[t * 3 + 2]);
                if (steep[t] && !climb) shortSteepArea += area[t];
            }
            foreach (var cl in clusters.Values)
                if (cl.maxY - cl.minY >= MinCliffHeight) { cliffs.Add(cl); cliffArea += cl.area; }
            groundTris += groundList.Count / 3;
            cliffTris += cliffList.Count / 3;

            // 原本的 MeshCollider 換成兩個子物件的碰撞體
            foreach (var col in mf.GetComponents<MeshCollider>())
            {
                if (PrefabUtility.IsAddedComponentOverride(col)) Object.DestroyImmediate(col);
                else col.enabled = false;
            }
            // 沒封閉的網格(葉子、藤蔓這種單層的面)和面朝內的實心物件:單面碰撞體從背面撞不到、會穿過去,做成雙面
            bool twoSided = doubleSided.Contains(mf);
            if (!twoSided)
            {
                var edgeCount = new Dictionary<long, int>(triCount * 3);
                double vol = 0;
                for (int t = 0; t < triCount; t++)
                {
                    int i0 = tris[t * 3], i1 = tris[t * 3 + 1], i2 = tris[t * 3 + 2];
                    vol += Vector3.Dot(w[i0], Vector3.Cross(w[i1], w[i2]));
                    CountEdge(edgeCount, weldId[i0], weldId[i1]);
                    CountEdge(edgeCount, weldId[i1], weldId[i2]);
                    CountEdge(edgeCount, weldId[i2], weldId[i0]);
                }
                int boundary = 0;
                foreach (int ec in edgeCount.Values) if (ec == 1) boundary++;
                bool open = boundary > edgeCount.Count * 0.01f;
                bool inverted = !open && vol < 0;
                if (open) autoOpen++;
                if (inverted) autoInverted++;
                twoSided = open || inverted;
            }
            container = AddCollider(mf, groundList, local, GroundChild, false, container, twoSided);
            container = AddCollider(mf, cliffList, local, CliffChild, true, container, twoSided);
        }
        AssetDatabase.SaveAssets();
        Physics.SyncTransforms();

        // ---- 報告 ----
        float total = flatArea + slopeArea + cliffArea + shortSteepArea;
        report.AppendLine($"背景世界範圍:X {world.min.x:F1}~{world.max.x:F1}  Y {world.min.y:F1}~{world.max.y:F1}  Z {world.min.z:F1}~{world.max.z:F1}");
        report.AppendLine($"  大小 {world.size.x:F1} x {world.size.y:F1}(高) x {world.size.z:F1} 公尺,{filters.Count} 個網格");
        report.AppendLine($"表面積 {total:F0} m²:");
        report.AppendLine($"  平地(<15°)    {flatArea:F0} m²  {Pct(flatArea, total)}");
        report.AppendLine($"  緩坡(15~{ClimbAngleDeg:F0}°) {slopeArea:F0} m²  {Pct(slopeArea, total)}");
        report.AppendLine($"  峭壁(可爬)     {cliffArea:F0} m²  {Pct(cliffArea, total)}  共 {cliffs.Count} 片");
        report.AppendLine($"  太矮的陡面(當地面) {shortSteepArea:F0} m²  {Pct(shortSteepArea, total)}");
        report.AppendLine($"碰撞三角面:地面 {groundTris}、峭壁 {cliffTris}");
        report.AppendLine($"自動雙面碰撞體:沒封閉的網格(葉子、藤蔓…){autoOpen} 個、面朝內的實心物件 {autoInverted} 個(從背面也撞得到,不會穿過)");
        if (doors.Count > 0) report.AppendLine($"可穿過(艙門,沒有碰撞體、隱藏):{string.Join("、", doors.Select(m => m.name))}");
        if (doubleSided.Count > 0) report.AppendLine($"雙面碰撞體(標記成 地面/地洞頂/地洞底):{string.Join("、", doubleSided.Select(m => m.name))}");
        if (total > 0 && (flatArea + slopeArea) / total < 0.3f)
            report.AppendLine("⚠ 平地和緩坡不到三成,地形可能還是立著或是歪的,請檢查背景的 Rotation");

        report.AppendLine("最大的峭壁(依面積):");
        foreach (var cl in cliffs.OrderByDescending(c => c.area).Take(15))
        {
            Vector3 c = cl.centerSum / Mathf.Max(cl.area, 1e-6f);
            report.AppendLine($"  {cl.mesh,-12} 中心 ({c.x:F1}, {c.y:F1}, {c.z:F1})  高 {cl.maxY - cl.minY:F2} m  面積 {cl.area:F1} m²");
        }
        BuildMapBoundary(bg, LevelMarks.Of(marks, MarkKind.Ground), report);
        return true;
    }

    // =================================================================
    // 場地外圍的隱形牆:沿著「地面」標記物件的外緣圍一圈,玩家不會走出去掉下去
    //   Layer = Ignore Raycast:手抓不到(LizardLocomotion 的 Surface Mask 不含)、螞蟻貼地的射線也打不到,但身體會被擋住
    // =================================================================

    const string BoundaryRoot = "MapBoundary";
    const float BoundaryInset = 0.2f;       // 牆比地面邊緣往內多少(公尺)
    const float BoundaryThickness = 0.3f;
    const int BoundarySegments = 120;

    static void BuildMapBoundary(GameObject bg, List<MeshFilter> ground, StringBuilder report)
    {
        var scene = bg.scene;
        foreach (var old in scene.GetRootGameObjects().Where(g => g.name == BoundaryRoot).ToList()) Object.DestroyImmediate(old);
        if (ground.Count == 0)
        {
            report.AppendLine("⚠ 沒有「地面」標記,沒有建立場地外圍的隱形牆");
            return;
        }

        // 地面所有頂點(世界座標)
        var pts = new List<Vector3>();
        foreach (var mf in ground)
        {
            if (!mf.sharedMesh.isReadable) continue;
            var m = mf.transform.localToWorldMatrix;
            foreach (var v in mf.sharedMesh.vertices) pts.Add(m.MultiplyPoint3x4(v));
        }
        if (pts.Count == 0) return;
        float minY = pts.Min(p => p.y), maxY = pts.Max(p => p.y);
        Vector2 c = Vector2.zero;
        foreach (var p in pts) c += new Vector2(p.x, p.z);
        c /= pts.Count;

        // 每個方向離中心最遠的頂點 = 地面邊緣;和左右鄰格取最小(寧可往內,不要蓋到地面外面),再往內收一點
        int n = BoundarySegments;
        var r = new float[n];
        foreach (var p in pts)
        {
            Vector2 d = new Vector2(p.x, p.z) - c;
            int k = ((int)((Mathf.Atan2(d.y, d.x) + Mathf.PI) / (2f * Mathf.PI) * n) % n + n) % n;
            r[k] = Mathf.Max(r[k], d.magnitude);
        }
        var rr = new float[n];
        for (int k = 0; k < n; k++)
        {
            float a = r[(k - 1 + n) % n], b = r[k], d2 = r[(k + 1) % n];
            float v = Mathf.Min(b, Mathf.Min(a > 0 ? a : b, d2 > 0 ? d2 : b));
            rr[k] = Mathf.Max(0f, v - BoundaryInset);
        }

        var root = new GameObject(BoundaryRoot);
        SceneManager.MoveGameObjectToScene(root, scene);
        root.layer = 2;
        float y0 = minY - 2f, y1 = maxY + 3f;
        for (int k = 0; k < n; k++)
        {
            float a0 = -Mathf.PI + (k + 0.5f) * 2f * Mathf.PI / n, a1 = -Mathf.PI + (k + 1.5f) * 2f * Mathf.PI / n;
            var p0 = c + new Vector2(Mathf.Cos(a0), Mathf.Sin(a0)) * rr[k];
            var p1 = c + new Vector2(Mathf.Cos(a1), Mathf.Sin(a1)) * rr[(k + 1) % n];
            Vector2 seg = p1 - p0;
            if (seg.magnitude < 0.01f) continue;
            var go = new GameObject($"Wall_{k:000}");
            go.layer = 2;   // Ignore Raycast
            go.transform.SetParent(root.transform, false);
            go.transform.position = new Vector3((p0.x + p1.x) * 0.5f, (y0 + y1) * 0.5f, (p0.y + p1.y) * 0.5f);
            go.transform.rotation = Quaternion.LookRotation(new Vector3(seg.x, 0f, seg.y), Vector3.up);
            var box = go.AddComponent<BoxCollider>();
            box.size = new Vector3(BoundaryThickness, y1 - y0, seg.magnitude + 0.05f);
        }
        report.AppendLine($"場地外圍隱形牆:沿著 {string.Join("、", ground.Select(g => g.name))} 的邊緣往內 {BoundaryInset} m 圍一圈({n} 段,手抓不到、只擋身體)");
    }

    static void CountEdge(Dictionary<long, int> edges, int a, int b)
    {
        if (a == b) return;
        long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
        edges[key] = edges.TryGetValue(key, out int c) ? c + 1 : 1;
    }

    static string Pct(float a, float total) => total > 0 ? $"{a / total * 100f:F0}%" : "-";

    static Object AddCollider(MeshFilter mf, List<int> tris, Vector3[] local, string name, bool climbable, Object container, bool twoSided = false)
    {
        if (tris.Count == 0) return container;
        if (twoSided)
        {
            // 每個三角面再加一個反方向的,正反兩面都會擋住
            int n = tris.Count;
            for (int i = 0; i < n; i += 3) { tris.Add(tris[i]); tris.Add(tris[i + 2]); tris.Add(tris[i + 1]); }
        }

        // 只保留用到的頂點
        var remap = new Dictionary<int, int>();
        var verts = new List<Vector3>();
        var idx = new int[tris.Count];
        for (int i = 0; i < tris.Count; i++)
        {
            if (!remap.TryGetValue(tris[i], out int n)) { n = verts.Count; remap[tris[i]] = n; verts.Add(local[tris[i]]); }
            idx[i] = n;
        }
        var mesh = new Mesh { name = $"{mf.name}{name}" };
        if (verts.Count > 65535) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        mesh.SetVertices(verts);
        mesh.SetTriangles(idx, 0);
        mesh.RecalculateBounds();

        if (container == null) { AssetDatabase.CreateAsset(mesh, GeneratedAsset); container = mesh; }
        else AssetDatabase.AddObjectToAsset(mesh, container);

        var go = new GameObject(name);
        go.layer = DefaultLayer;
        go.transform.SetParent(mf.transform, false);
        if (climbable) go.tag = ClimbableTag;
        go.AddComponent<MeshCollider>().sharedMesh = mesh;
        return container;
    }

    static bool EnsureReadable(List<MeshFilter> filters, StringBuilder report)
    {
        var unreadable = filters.Where(f => !f.sharedMesh.isReadable).ToList();
        if (unreadable.Count == 0) return true;

        var paths = unreadable.Select(f => AssetDatabase.GetAssetPath(f.sharedMesh)).Where(p => !string.IsNullOrEmpty(p)).Distinct().ToList();
        foreach (var p in paths)
        {
            if (AssetImporter.GetAtPath(p) is ModelImporter mi && !mi.isReadable)
            {
                EditorUtility.DisplayProgressBar("Setup Terrain", $"開啟 Read/Write 並重新匯入 {Path.GetFileName(p)}", 0.05f);
                mi.isReadable = true;
                mi.SaveAndReimport();
                report.AppendLine($"已開啟 {p} 的 Read/Write(分析網格需要)");
            }
        }
        if (filters.Any(f => f.sharedMesh == null || !f.sharedMesh.isReadable))
        {
            report.AppendLine("⚠ 網格仍然無法讀取,請手動在模型的 Import Settings 勾選 Read/Write 後再執行一次");
            return false;
        }
        return true;
    }

    // =================================================================
    // 2. Player 貼地
    // =================================================================

    // 有「出生點」標記時:Player 放在這些物件(太空艙)合起來的正中間,往下找到裡面的地板站上去,面向森林
    const float OutsideDoorDistance = 0.6f;   // 出生在艙門外多遠(公尺)

    public static bool PlaceAtSpawn(Transform player, GameObject bg, StringBuilder report)
    {
        var marks = LevelMarks.Resolve(bg, LevelMarks.Load(), null);
        var spawn = LevelMarks.Of(marks, MarkKind.Spawn);
        if (spawn.Count == 0) return false;

        Bounds b = LevelMarks.MeshWorldBounds(spawn[0]);
        foreach (var mf in spawn) b.Encapsulate(LevelMarks.MeshWorldBounds(mf));
        Vector3 start = b.center;

        // 有艙門(可穿過)標記時:出生在艙門外面 OutsideDoorDistance 公尺的地上,不用自己爬出艙
        string where = "中間";
        var doors = LevelMarks.Of(marks, MarkKind.Door);
        if (doors.Count > 0)
        {
            Bounds db = LevelMarks.MeshWorldBounds(doors[0]);
            Vector3 outDir = db.center - b.center; outDir.y = 0f;
            if (outDir.sqrMagnitude > 1e-4f)
            {
                start = db.center + outDir.normalized * OutsideDoorDistance;
                where = $"艙門 {doors[0].name} 外面 {OutsideDoorDistance} m";
            }
        }

        // 從艙內往下打,背面也要算(艙底的內側可能是模型的背面)
        bool prev = Physics.queriesHitBackfaces;
        Physics.queriesHitBackfaces = true;
        RaycastHit[] hits;
        try { hits = Physics.RaycastAll(start, Vector3.down, b.extents.y + 3f, DefaultMask, QueryTriggerInteraction.Ignore); }
        finally { Physics.queriesHitBackfaces = prev; }

        string floor;
        if (hits.Length > 0)
        {
            var hit = hits.OrderBy(h => h.distance).First();
            player.position = hit.point;
            floor = hit.collider.transform.parent != null ? hit.collider.transform.parent.name : hit.collider.name;
        }
        else
        {
            player.position = new Vector3(start.x, b.min.y, start.z);
            floor = "⚠ 下面找不到地板,先放在艙底高度";
        }

        // 面向森林(森林邊界標記的中心)
        var forest = LevelMarks.Of(marks, MarkKind.ForestEdge);
        string facing = "";
        if (forest.Count > 0)
        {
            Vector3 fc = Vector3.zero;
            foreach (var mf in forest) fc += mf.GetComponent<Renderer>().bounds.center;
            fc /= forest.Count;
            Vector3 dir = fc - player.position; dir.y = 0f;
            if (dir.sqrMagnitude > 0.01f) { player.rotation = Quaternion.LookRotation(dir.normalized, Vector3.up); facing = ",面向森林"; }
        }
        report.AppendLine($"Player 出生點:{string.Join(" 和 ", spawn.Select(m => m.name))} {where} {player.position}(腳下是 {floor}){facing}");
        return true;
    }

    // fromTop = false:從 Player 上方 1.5 公尺往下找(保留使用者擺的高度層);true:從目前位置一路往下找
    public static void SnapPlayerToGround(Transform player, StringBuilder report, bool fromTop = false)
    {
        Vector3 start = fromTop ? player.position : player.position + Vector3.up * 1.5f;
        float dist = fromTop ? 200f : 30f;
        if (Physics.Raycast(start, Vector3.down, out RaycastHit hit, dist, DefaultMask, QueryTriggerInteraction.Ignore))
        {
            float dy = hit.point.y - player.position.y;
            player.position = hit.point;
            report.AppendLine($"Player 貼地:{player.position}(高度修正 {dy:+0.00;-0.00} m,腳下是 {hit.collider.name})");
        }
        else report.AppendLine($"⚠ Player {player.position} 下方 30 公尺內沒有地面,請把 Player 拖到地形上再執行一次");
    }

    // =================================================================
    // 3. 螞蟻
    // =================================================================

    public static void PlaceAnts(Scene scene, Transform player, StringBuilder report)
    {
        EditorUtility.DisplayProgressBar("Setup Terrain", "放螞蟻", 0.9f);
        foreach (var old in scene.GetRootGameObjects().Where(g => g.name == "Ants")) Object.DestroyImmediate(old);

        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefab/AntRoot.prefab");
        var root = new GameObject("Ants");
        SceneManager.MoveGameObjectToScene(root, scene);

        // 有「地面」標記時,螞蟻只放在標記的地面上
        var groundMarks = LevelMarks.Of(LevelMarks.Resolve(LevelMarks.FindBackground(scene), LevelMarks.Load(), null), MarkKind.Ground);
        antGround = new HashSet<Transform>(groundMarks.Select(m => m.transform));
        if (antGround.Count > 0) report.AppendLine($"螞蟻只放在地面標記上:{string.Join("、", groundMarks.Select(m => m.name))}");

        Vector3 origin = player.position;
        Vector3 fwd = Vector3.ProjectOnPlane(player.forward, Vector3.up).normalized;
        if (fwd == Vector3.zero) fwd = Vector3.forward;

        // 從玩家正前方開始,左右輪流轉 20° 找一段平坦的地面
        Vector3 entry = default, exit = default, center = default;
        bool found = false;
        float halfLen = TrailHalfLength;
        for (int attempt = 0; attempt < 3 && !found; attempt++, halfLen *= 0.5f)
        {
            for (int k = 0; k <= 18 && !found; k++)
            {
                float yaw = (k % 2 == 1 ? 1 : -1) * ((k + 1) / 2) * 20f;
                Vector3 dir = Quaternion.Euler(0, yaw, 0) * fwd;
                Vector3 side = Vector3.Cross(Vector3.up, dir);
                if (TryFlatLine(origin + dir * TrailDistance, side, halfLen, origin.y, out entry, out exit))
                {
                    center = (entry + exit) * 0.5f;
                    found = true;
                    report.AppendLine($"螞蟻路線:玩家前方偏 {yaw:+0;-0;0}°,長 {halfLen * 2:F1} m,入口 {entry} → 出口 {exit}");
                }
            }
        }

        AntPath trail = null;
        if (found)
        {
            var trailGo = new GameObject("AntTrail");
            trailGo.transform.SetParent(root.transform, false);
            trail = trailGo.AddComponent<AntPath>();
            trail.strip = true;
            trail.stripWidth = TrailWidth;
            var e = new GameObject("Entry").transform; e.SetParent(trailGo.transform, false); e.position = entry;
            var x = new GameObject("Exit").transform; x.SetParent(trailGo.transform, false); x.position = exit;
        }
        else
        {
            center = Probe(origin + fwd * TrailDistance, origin.y, out var h) ? h.point : origin + fwd * TrailDistance;
            report.AppendLine("⚠ 玩家附近找不到夠平的一段路線,所有螞蟻改成隨機遊走");
        }

        int placed = 0;
        float scale = 0f;
        if (trail != null)
            for (int i = 0; i < TrailAntCount; i++)
                SpawnAnt(prefab, scene, root.transform, $"Ant_Trail_{i + 1}", Vector3.Lerp(entry, exit, (i + 0.5f) / TrailAntCount), trail, ref scale, ref placed);

        int wanderCount = trail != null ? WanderAntCount : WanderAntCount + TrailAntCount;
        for (int i = 0; i < wanderCount; i++)
        {
            Vector2 r = Random.insideUnitCircle * 0.5f;
            Vector3 p = center + new Vector3(r.x, 0, r.y);
            if (Probe(p, center.y, out var hit) && IsGround(hit)) p = hit.point; else p = center;
            SpawnAnt(prefab, scene, root.transform, $"Ant_Wander_{i + 1}", p, null, ref scale, ref placed);
        }
        report.AppendLine($"螞蟻 {placed} 隻,身長 {AntLength} m(prefab 縮放 x{scale:F4}),一步最多上下 {AntMaxStep} m,不會走下/爬上峭壁");
    }

    static bool TryFlatLine(Vector3 center, Vector3 side, float halfLen, float refY, out Vector3 a, out Vector3 b)
    {
        a = b = default;
        const int samples = 9;
        float prevY = float.NaN;
        var pts = new Vector3[samples];
        for (int i = 0; i < samples; i++)
        {
            Vector3 p = center + side * Mathf.Lerp(-halfLen, halfLen, i / (samples - 1f));
            if (!Probe(p, refY, out var hit) || !IsGround(hit)) return false;
            if (Mathf.Abs(hit.point.y - refY) > 0.6f) return false;
            if (!float.IsNaN(prevY) && Mathf.Abs(hit.point.y - prevY) > 0.15f) return false;
            // 左右兩側也要有地面,螞蟻散開時才不會掉下去
            foreach (float off in new[] { -TrailWidth * 0.5f, TrailWidth * 0.5f })
            {
                Vector3 q = p + Vector3.Cross(side, Vector3.up) * off;
                if (!Probe(q, hit.point.y, out var h2) || !IsGround(h2) || Mathf.Abs(h2.point.y - hit.point.y) > 0.15f) return false;
            }
            prevY = hit.point.y;
            pts[i] = hit.point;
        }
        a = pts[0];
        b = pts[samples - 1];
        return true;
    }

    static bool Probe(Vector3 p, float refY, out RaycastHit hit) =>
        Physics.Raycast(new Vector3(p.x, refY + 1f, p.z), Vector3.down, out hit, 3f, DefaultMask, QueryTriggerInteraction.Ignore);

    static HashSet<Transform> antGround = new HashSet<Transform>();

    static bool IsGround(RaycastHit hit) => hit.collider != null && hit.collider.name == GroundChild &&
        (antGround.Count == 0 || antGround.Contains(hit.collider.transform.parent));

    public static void SpawnAnt(GameObject prefab, Scene scene, Transform parent, string name, Vector3 pos, AntPath path, ref float scale, ref int count)
    {
        var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
        go.name = name;
        go.transform.SetParent(parent, true);
        go.transform.position = pos;

        // 縮到 AntLength,並讓模型中心對齊根物件、腳底貼齊根物件
        Bounds b = RendererBounds(go);
        float size = Mathf.Max(b.size.x, b.size.z);
        if (size > 0.0001f)
        {
            go.transform.localScale *= AntLength / size;
            scale = go.transform.localScale.x;
            b = RendererBounds(go);
            Vector3 off = b.center - go.transform.position;
            off.y = b.min.y - go.transform.position.y;
            foreach (Transform child in go.transform) child.position -= off;
        }

        var ant = go.GetComponent<AntWander>();
        if (ant != null)
        {
            ant.path = path;
            ant.groundMask = DefaultMask;
            ant.groundOffset = 0f;
            ant.maxStepHeight = AntMaxStep;
            if (path == null) ant.wanderRadius = 0.5f;
            EditorUtility.SetDirty(ant);
        }
        count++;
    }

    public static Bounds RendererBounds(GameObject go)
    {
        var rs = go.GetComponentsInChildren<Renderer>(true);
        if (rs.Length == 0) return new Bounds(go.transform.position, Vector3.zero);
        Bounds b = rs[0].bounds;
        foreach (var r in rs) b.Encapsulate(r.bounds);
        return b;
    }

    static void WriteReport(StringBuilder report)
    {
        string text = report.ToString();
        Debug.Log(text);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ReportPath));
            File.WriteAllText(ReportPath, text, Encoding.UTF8);
        }
        catch (IOException e) { Debug.LogWarning($"[TerrainSetup] 無法寫入 {ReportPath}:{e.Message}"); }
    }
}
