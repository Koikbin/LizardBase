using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

// 關卡標記:使用者手動告訴 Setup Terrain / Setup Game 哪些東西是什麼。
//
// 物件標記(地面、水珠、礦石、地洞頂、地洞底):在 Hierarchy 選背景模型裡的物件 → Lizard > 標記工具 按按鈕
//   (或在 Hierarchy 右鍵 > Lizard 標記)。選到群組 = 底下所有網格都標記。
//   存在 Assets/Generated/LevelMarks.json:名稱 + 頂點數 + 世界座標範圍。
//   換新版背景模型(物件重新編號)時,會用 頂點數 + 位置 自動對回同一個物件。
// 範圍框(森林範圍、地洞出入口):場景裡 LevelMarkers 底下的 GameZone 方塊,自己拖拉調整大小。
//   也會存進 LevelMarks.json,重建場景(Build FullTest Scene)時自動放回來。
public enum MarkKind { Ground, Water, Ore, CaveCeiling, CaveFloor, CaveEntrance, ForestEdge, Spawn, Stepping, Door, PathEdge }

[Serializable]
public class MeshMark
{
    public string kind;      // MarkKind 名稱
    public string name;      // 標記時的物件名稱(只是參考,換模型後可能不同)
    public int verts;        // Mesh.vertexCount
    public Vector3 center;   // Renderer.bounds(世界座標)
    public Vector3 size;
}

[Serializable]
public class ZoneMark
{
    public string role;      // GameZone.ZoneRole 名稱
    public string name;
    public Vector3 position;
    public Quaternion rotation;
    public Vector3 scale;
    public Vector3 boxCenter;
    public Vector3 boxSize;
}

[Serializable]
public class LevelMarkData
{
    public List<MeshMark> meshes = new List<MeshMark>();
    public List<ZoneMark> zones = new List<ZoneMark>();
}

[InitializeOnLoad]
public static class LevelMarks
{
    public const string DataPath = "Assets/Generated/LevelMarks.json";
    public const string MarkersRoot = "LevelMarkers";
    const float MatchTolerance = 0.05f;   // 換模型時,位置差 5 公分內算同一個物件

    public static string Label(MarkKind k)
    {
        switch (k)
        {
            case MarkKind.Ground: return "地面";
            case MarkKind.Water: return "水珠";
            case MarkKind.Ore: return "礦石";
            case MarkKind.CaveCeiling: return "地洞頂";
            case MarkKind.CaveFloor: return "地洞底";
            case MarkKind.CaveEntrance: return "地洞出入口(圈)";
            case MarkKind.ForestEdge: return "森林邊界";
            case MarkKind.Spawn: return "出生點(太空艙)";
            case MarkKind.Stepping: return "石頭階梯";
            case MarkKind.PathEdge: return "通道邊界(太空艙→森林)";
            default: return "可穿過(艙門)";
        }
    }

    public static Color KindColor(MarkKind k)
    {
        switch (k)
        {
            case MarkKind.Ground: return new Color(0.4f, 0.9f, 0.3f);
            case MarkKind.Water: return new Color(0.3f, 0.7f, 1f);
            case MarkKind.Ore: return new Color(0.75f, 0.4f, 1f);
            case MarkKind.CaveCeiling: return new Color(1f, 0.55f, 0.1f);
            case MarkKind.CaveFloor: return new Color(0.75f, 0.45f, 0.2f);
            case MarkKind.CaveEntrance: return new Color(1f, 0.9f, 0.2f);
            case MarkKind.ForestEdge: return new Color(0.1f, 0.6f, 0.15f);
            case MarkKind.Spawn: return new Color(0.9f, 0.95f, 1f);
            case MarkKind.Stepping: return new Color(0.55f, 0.65f, 0.8f);
            case MarkKind.PathEdge: return new Color(1f, 0.6f, 0.15f);
            default: return new Color(1f, 0.4f, 0.6f);
        }
    }

    public static string Label(GameZone.ZoneRole r) => r == GameZone.ZoneRole.Forest ? "森林範圍" : "地洞出入口";
    static Color ZoneColor(GameZone.ZoneRole r) =>
        r == GameZone.ZoneRole.Forest ? new Color(0.3f, 0.9f, 0.3f, 0.12f) : new Color(1f, 0.85f, 0.2f, 0.25f);

    // =================================================================
    // 讀寫
    // =================================================================

    public static LevelMarkData Load()
    {
        if (!File.Exists(DataPath)) return new LevelMarkData();
        try { return JsonUtility.FromJson<LevelMarkData>(File.ReadAllText(DataPath)) ?? new LevelMarkData(); }
        catch (Exception e)
        {
            Debug.LogError($"[LevelMarks] 讀取 {DataPath} 失敗:{e.Message}");
            return new LevelMarkData();
        }
    }

    public static void Save(LevelMarkData data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DataPath));
        File.WriteAllText(DataPath, JsonUtility.ToJson(data, true), new UTF8Encoding(false));
        AssetDatabase.ImportAsset(DataPath);
        MarkDirty();
    }

    public static bool TryKind(MeshMark m, out MarkKind k) => Enum.TryParse(m.kind, out k);

    // 網格的世界座標範圍(跟 Renderer.bounds 一樣算法,但 Renderer 被隱藏時也有值,例如打開的艙門)
    public static Bounds MeshWorldBounds(MeshFilter mf)
    {
        var lb = mf.sharedMesh.bounds;
        var m = mf.transform.localToWorldMatrix;
        var b = new Bounds(m.MultiplyPoint3x4(lb.center), Vector3.zero);
        for (int i = 0; i < 8; i++)
            b.Encapsulate(m.MultiplyPoint3x4(lb.center + Vector3.Scale(lb.extents,
                new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1))));
        return b;
    }

    public static GameObject FindBackground(Scene scene) =>
        scene.IsValid() ? scene.GetRootGameObjects().FirstOrDefault(g => g.name == TerrainSetup.BackgroundName) : null;

    // =================================================================
    // 標記 → 背景裡的 MeshFilter(名稱或頂點數 + 位置對應)
    // =================================================================

    public static Dictionary<MeshFilter, List<MarkKind>> Resolve(GameObject bg, LevelMarkData data, List<string> warnings) =>
        Resolve(bg, data, warnings, out _);

    // 一個物件可以有好幾種標記(例如 物件_22 同時是 地面 和 地洞頂)。
    // verts = 0 的標記 = 只用名稱找(手寫進 JSON 的);找到後會補上頂點數和位置(upgraded = true),之後換模型也對得回來。
    public static Dictionary<MeshFilter, List<MarkKind>> Resolve(GameObject bg, LevelMarkData data, List<string> warnings, out bool upgraded)
    {
        upgraded = false;
        var result = new Dictionary<MeshFilter, List<MarkKind>>();
        if (bg == null || data.meshes.Count == 0) return result;

        var byVerts = new Dictionary<int, List<(MeshFilter mf, Bounds b)>>();
        var byName = new Dictionary<string, List<(MeshFilter mf, Bounds b)>>();
        foreach (var mf in bg.GetComponentsInChildren<MeshFilter>(true))
        {
            if (mf.sharedMesh == null) continue;
            var r = mf.GetComponent<Renderer>();
            if (r == null) continue;
            int v = mf.sharedMesh.vertexCount;
            if (!byVerts.TryGetValue(v, out var list)) byVerts[v] = list = new List<(MeshFilter, Bounds)>();
            var wb = MeshWorldBounds(mf);
            list.Add((mf, wb));
            if (!byName.TryGetValue(mf.name, out var nl)) byName[mf.name] = nl = new List<(MeshFilter, Bounds)>();
            nl.Add((mf, wb));
        }

        int missing = 0, renamed = 0;
        foreach (var m in data.meshes)
        {
            if (!TryKind(m, out var kind)) continue;
            MeshFilter best = null;
            if (m.verts <= 0)
            {
                if (byName.TryGetValue(m.name ?? "", out var named))
                {
                    if (named.Count > 1) warnings?.Add($"有 {named.Count} 個物件都叫 {m.name},{Label(kind)}標記用第一個");
                    best = named[0].mf;
                    m.verts = best.sharedMesh.vertexCount;
                    m.center = named[0].b.center;
                    m.size = named[0].b.size;
                    upgraded = true;
                }
            }
            else if (byVerts.TryGetValue(m.verts, out var cands))
            {
                float bestD = MatchTolerance;
                foreach (var (mf, b) in cands)
                {
                    float d = Vector3.Distance(b.center, m.center) + Vector3.Distance(b.size, m.size);
                    if (mf.name == m.name) d *= 0.5f;          // 同名優先
                    if (d < bestD) { bestD = d; best = mf; }
                }
            }
            if (best == null)
            {
                missing++;
                if (missing <= 20) warnings?.Add($"找不到{Label(kind)}標記 {m.name}(頂點 {m.verts},位置 {m.center}),模型可能改過這個物件");
                continue;
            }
            if (best.name != m.name) renamed++;
            if (!result.TryGetValue(best, out var kinds)) result[best] = kinds = new List<MarkKind>();
            if (!kinds.Contains(kind)) kinds.Add(kind);
        }
        if (missing > 20) warnings?.Add($"…總共 {missing} 個標記找不到");
        if (renamed > 0) warnings?.Add($"{renamed} 個標記的物件換了名稱(新版模型重新編號),已依頂點數與位置自動對回");
        return result;
    }

    // 給 Setup Terrain / Setup Game 用:讀檔 + 對應 + 寫報告
    public static Dictionary<MeshFilter, List<MarkKind>> ResolveForSetup(GameObject bg, StringBuilder report)
    {
        var data = Load();
        var warnings = new List<string>();
        var map = Resolve(bg, data, warnings, out bool upgraded);
        if (upgraded) Save(data);   // 只有名稱的標記補上頂點數和位置
        if (data.meshes.Count > 0)
        {
            var counts = map.Values.SelectMany(k => k).GroupBy(k => k).Select(g => $"{Label(g.Key)} {g.Count()}");
            report.AppendLine($"標記({DataPath}):{string.Join("、", counts)}");
            foreach (var w in warnings) report.AppendLine($"  ⚠ {w}");
        }
        return map;
    }

    public static List<MeshFilter> Of(Dictionary<MeshFilter, List<MarkKind>> map, MarkKind k) =>
        map.Where(p => p.Value.Contains(k)).Select(p => p.Key).ToList();

    // =================================================================
    // 標記選取的物件
    // =================================================================

    public static void MarkSelection(MarkKind? kind)
    {
        var scene = SceneManager.GetActiveScene();
        var bg = FindBackground(scene);
        if (bg == null)
        {
            EditorUtility.DisplayDialog("標記", $"目前場景找不到 {TerrainSetup.BackgroundName}。請先開啟 FullTest.unity。", "OK");
            return;
        }
        var filters = new HashSet<MeshFilter>();
        int outside = 0;
        foreach (var go in Selection.gameObjects)
        {
            if (!go.transform.IsChildOf(bg.transform)) { outside++; continue; }
            foreach (var mf in go.GetComponentsInChildren<MeshFilter>(true))
                if (mf.sharedMesh != null && mf.GetComponent<Renderer>() != null) filters.Add(mf);
        }
        if (filters.Count == 0)
        {
            EditorUtility.DisplayDialog("標記", outside > 0
                ? $"選取的物件不在背景 {TerrainSetup.BackgroundName} 底下。森林範圍 / 地洞出入口請用「新增範圍框」。"
                : "請先在 Hierarchy 選取背景模型裡的物件(可以多選,選群組 = 底下全部)。", "OK");
            return;
        }
        string what = kind.HasValue ? $"加上「{Label(kind.Value)}」標記" : "清除全部標記";
        if (filters.Count > 300 && !EditorUtility.DisplayDialog("標記", $"選取範圍裡有 {filters.Count} 個網格,確定全部{what}?", "確定", "取消"))
            return;

        var data = Load();
        string kindName = kind.HasValue ? kind.Value.ToString() : null;
        foreach (var mf in filters)
        {
            var b = MeshWorldBounds(mf);
            int v = mf.sharedMesh.vertexCount;
            // 同一個物件:加標記時只換掉同種類的(可以同時有好幾種),清除時全部拿掉
            data.meshes.RemoveAll(m => (kindName == null || m.kind == kindName) &&
                ((m.verts == v && Vector3.Distance(m.center, b.center) < 0.005f) || (m.verts <= 0 && m.name == mf.name)));
            if (kind.HasValue)
                data.meshes.Add(new MeshMark { kind = kindName, name = mf.name, verts = v, center = b.center, size = b.size });
        }
        Save(data);
        Debug.Log($"[LevelMarks] {filters.Count} 個網格{what}");
        LevelMarkWindow.RepaintAll();
    }

    public static void SelectMarked(MarkKind kind)
    {
        var map = Resolve(FindBackground(SceneManager.GetActiveScene()), Load(), null);
        var gos = Of(map, kind).Select(mf => (UnityEngine.Object)mf.gameObject).ToArray();
        Selection.objects = gos;
        if (gos.Length > 0) SceneView.lastActiveSceneView?.FrameSelected();
    }

    // =================================================================
    // 範圍框
    // =================================================================

    // 使用者自己放的範圍框(在 LevelMarkers 底下);Setup Game 依出入口圈產生的在 GameSystems 底下
    public static bool IsManualZone(GameZone z) => z.transform.root.name == MarkersRoot;

    public static GameZone[] Zones(Scene scene, GameZone.ZoneRole role) =>
        scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<GameZone>(true))
             .Where(z => z.role == role).ToArray();

    static Transform MarkersParent(Scene scene)
    {
        var root = scene.GetRootGameObjects().FirstOrDefault(g => g.name == MarkersRoot);
        if (root == null)
        {
            root = new GameObject(MarkersRoot);
            SceneManager.MoveGameObjectToScene(root, scene);
            Undo.RegisterCreatedObjectUndo(root, "Create LevelMarkers");
        }
        return root.transform;
    }

    static GameZone CreateZone(Scene scene, GameZone.ZoneRole role, string name, Vector3 pos, Quaternion rot, Vector3 scale, Vector3 boxCenter, Vector3 boxSize)
    {
        var go = new GameObject(name);
        go.transform.SetParent(MarkersParent(scene), false);
        go.transform.SetPositionAndRotation(pos, rot);
        go.transform.localScale = scale;
        var box = go.AddComponent<BoxCollider>();
        box.isTrigger = true;
        box.center = boxCenter;
        box.size = boxSize;
        var zone = go.AddComponent<GameZone>();
        zone.role = role;
        zone.gizmoColor = ZoneColor(role);
        return zone;
    }

    public static void AddZone(GameZone.ZoneRole role)
    {
        var scene = SceneManager.GetActiveScene();
        var view = SceneView.lastActiveSceneView;
        Vector3 pos = view != null ? view.pivot : Vector3.zero;
        // 放在畫面中心正下方的地面上
        if (Physics.Raycast(pos + Vector3.up * 50f, Vector3.down, out var hit, 200f, ~0, QueryTriggerInteraction.Ignore)) pos = hit.point;
        Vector3 size = role == GameZone.ZoneRole.Forest ? new Vector3(8f, 4f, 8f) : new Vector3(1.5f, 2f, 1.5f);
        int n = Zones(scene, role).Length + 1;
        var zone = CreateZone(scene, role, $"{Label(role)}_{n}", pos + Vector3.up * size.y * 0.5f, Quaternion.identity, Vector3.one, Vector3.zero, size);
        Undo.RegisterCreatedObjectUndo(zone.gameObject, $"Add {Label(role)}");
        Selection.activeGameObject = zone.gameObject;
        view?.FrameSelected();
        EditorSceneManager.MarkSceneDirty(scene);
        SaveZones(scene);
    }

    // 場景裡的範圍框 → JSON
    public static void SaveZones(Scene scene)
    {
        var data = Load();
        data.zones.Clear();
        foreach (var role in new[] { GameZone.ZoneRole.Forest, GameZone.ZoneRole.CaveEntrance })
            foreach (var z in Zones(scene, role).Where(IsManualZone))
            {
                var box = z.GetComponent<BoxCollider>();
                var t = z.transform;
                data.zones.Add(new ZoneMark
                {
                    role = role.ToString(), name = z.name, position = t.position, rotation = t.rotation, scale = t.lossyScale,
                    boxCenter = box.center, boxSize = box.size,
                });
            }
        Save(data);
    }

    // JSON → 場景(場景裡一個範圍框都沒有時才放回來,例如重建 FullTest 之後)
    public static int RestoreZones(Scene scene)
    {
        if (Zones(scene, GameZone.ZoneRole.Forest).Concat(Zones(scene, GameZone.ZoneRole.CaveEntrance)).Any(IsManualZone)) return 0;
        int n = 0;
        foreach (var zm in Load().zones)
        {
            if (!Enum.TryParse(zm.role, out GameZone.ZoneRole role)) continue;
            CreateZone(scene, role, zm.name, zm.position, zm.rotation, zm.scale, zm.boxCenter, zm.boxSize);
            n++;
        }
        return n;
    }

    // =================================================================
    // Scene 視窗 / Hierarchy 顯示
    // =================================================================

    public static bool ShowInScene
    {
        get => EditorPrefs.GetBool("LizardLevelMarks.Show", true);
        set { EditorPrefs.SetBool("LizardLevelMarks.Show", value); SceneView.RepaintAll(); }
    }

    static bool cacheDirty = true;
    static int cacheScene;
    static readonly List<(Bounds b, MarkKind k)> cacheBounds = new List<(Bounds, MarkKind)>();
    static Vector2[] cacheForest, cachePath;
    static float cacheForestY, cachePathY;
    public const float ForestEdgeMargin = 0.5f;   // 森林邊界從標記物件的外緣再往外推多少(公尺),超出地面會收回地面邊緣

    // 通道(太空艙 → 森林):「通道邊界」標記 + 出生點(太空艙)一起包成一圈,所以太空艙裡面也算在通道內。
    // 收回地面邊緣時,太空艙底下不是地面也不收(太空艙的地板不是地面標記)。
    public const float PathEdgeMargin = 1.5f;     // 通道邊界往外推比森林多,超出地面的地方一樣收回地面邊緣

    public static Vector2[] BuildPathPolygon(IList<Bounds> edge, IList<Bounds> spawn, HashSet<Transform> groundSet, float margin = PathEdgeMargin)
    {
        var all = edge.Concat(spawn).ToList();
        if (edge.Count == 0 || all.Count < 3) return null;
        Func<Vector2, bool> onGround = null;
        if (groundSet != null && groundSet.Count > 0)
        {
            float pad = margin + 0.2f;
            onGround = p => spawn.Any(b => Mathf.Abs(p.x - b.center.x) <= b.extents.x + pad && Mathf.Abs(p.y - b.center.z) <= b.extents.z + pad)
                            || IsOverGround(p, groundSet);
        }
        return ForestBoundary.BuildEnvelope(all, margin, 48, onGround);
    }

    // 這個水平位置的正下方有沒有標記成地面的物件(用 Setup Terrain 產生的碰撞體)
    public static bool IsOverGround(Vector2 xz, HashSet<Transform> groundSet)
    {
        var hits = Physics.RaycastAll(new Vector3(xz.x, 500f, xz.y), Vector3.down, 1000f, ~0, QueryTriggerInteraction.Ignore);
        foreach (var h in hits)
        {
            var t = h.collider.transform;
            if (groundSet.Contains(t) || (t.parent != null && groundSet.Contains(t.parent))) return true;
        }
        return false;
    }
    static readonly Dictionary<int, List<MarkKind>> cacheIds = new Dictionary<int, List<MarkKind>>();
    public static readonly List<string> CacheWarnings = new List<string>();
    public static readonly Dictionary<MarkKind, int> CacheCounts = new Dictionary<MarkKind, int>();

    static LevelMarks()
    {
        SceneView.duringSceneGui += OnSceneGUI;
        EditorApplication.hierarchyWindowItemOnGUI += OnHierarchyGUI;
        EditorApplication.hierarchyChanged += MarkDirty;
        EditorSceneManager.sceneOpened += (s, m) => MarkDirty();
    }

    public static void MarkDirty()
    {
        cacheDirty = true;
        SceneView.RepaintAll();
        EditorApplication.RepaintHierarchyWindow();
    }

    public static void RefreshCache()
    {
        var scene = SceneManager.GetActiveScene();
        if (!cacheDirty && cacheScene == scene.handle) return;
        cacheDirty = false;
        cacheScene = scene.handle;
        cacheBounds.Clear(); cacheIds.Clear(); CacheWarnings.Clear(); CacheCounts.Clear(); cacheForest = null; cachePath = null;
        var bg = FindBackground(scene);
        if (bg == null) return;
        foreach (var p in Resolve(bg, Load(), CacheWarnings))
        {
            var r = p.Key.GetComponent<Renderer>();
            cacheIds[p.Key.gameObject.GetInstanceID()] = p.Value;
            foreach (var k in p.Value)
            {
                cacheBounds.Add((MeshWorldBounds(p.Key), k));
                CacheCounts[k] = CacheCounts.TryGetValue(k, out int c) ? c + 1 : 1;
            }
        }
        var edge = cacheBounds.Where(x => x.k == MarkKind.ForestEdge).Select(x => x.b).ToList();
        var pathEdge = cacheBounds.Where(x => x.k == MarkKind.PathEdge).Select(x => x.b).ToList();
        HashSet<Transform> groundSet = null;
        if (edge.Count >= 3 || pathEdge.Count > 0)
            groundSet = new HashSet<Transform>(cacheIds.Keys.Select(id => EditorUtility.InstanceIDToObject(id) as GameObject)
                .Where(g => g != null && cacheIds[g.GetInstanceID()].Contains(MarkKind.Ground)).Select(g => g.transform));
        if (pathEdge.Count > 0)
        {
            var spawn = cacheBounds.Where(x => x.k == MarkKind.Spawn).Select(x => x.b).ToList();
            cachePath = BuildPathPolygon(pathEdge, spawn, groundSet);
            cachePathY = pathEdge.Average(b => b.center.y);
        }
        if (edge.Count >= 3)
        {
            // 跟 Setup Game 用一樣的算法,預覽就是套用後的範圍
            cacheForest = ForestBoundary.BuildEnvelope(edge, ForestEdgeMargin, 48,
                groundSet.Count > 0 ? (System.Func<Vector2, bool>)(p => IsOverGround(p, groundSet)) : null);
            cacheForestY = edge.Average(b => b.center.y);
        }
    }

    static void OnSceneGUI(SceneView view)
    {
        if (!ShowInScene || Event.current.type != EventType.Repaint) return;
        RefreshCache();
        foreach (var (b, k) in cacheBounds)
        {
            Handles.color = KindColor(k);
            Handles.DrawWireCube(b.center, b.size);
        }
        if (cacheForest != null)
        {
            // 森林邊界預覽(套用後的範圍)
            var pts = cacheForest.Select(p => new Vector3(p.x, cacheForestY, p.y)).ToList();
            pts.Add(pts[0]);
            Handles.color = KindColor(MarkKind.ForestEdge);
            Handles.DrawAAPolyLine(4f, pts.ToArray());
        }
        if (cachePath != null)
        {
            // 通道邊界預覽(太空艙 → 森林)
            var pts = cachePath.Select(p => new Vector3(p.x, cachePathY, p.y)).ToList();
            pts.Add(pts[0]);
            Handles.color = KindColor(MarkKind.PathEdge);
            Handles.DrawAAPolyLine(4f, pts.ToArray());
        }
    }

    static GUIStyle hierStyle;

    static void OnHierarchyGUI(int id, Rect rect)
    {
        if (!ShowInScene) return;
        RefreshCache();
        if (!cacheIds.TryGetValue(id, out var kinds) || kinds.Count == 0) return;
        if (hierStyle == null) hierStyle = new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleRight, fontStyle = FontStyle.Bold };
        hierStyle.normal.textColor = KindColor(kinds[0]);
        GUI.Label(rect, string.Join(" + ", kinds.Select(Label)), hierStyle);
    }

    // =================================================================
    // Hierarchy 右鍵選單(多選時 Unity 會對每個物件各呼叫一次,只處理第一次)
    // =================================================================

    static double lastContextTime;
    static bool FirstCall()
    {
        if (EditorApplication.timeSinceStartup - lastContextTime < 0.2) return false;
        lastContextTime = EditorApplication.timeSinceStartup;
        return true;
    }

    [MenuItem("GameObject/Lizard 標記/地面", false, 0)] static void CtxGround() { if (FirstCall()) MarkSelection(MarkKind.Ground); }
    [MenuItem("GameObject/Lizard 標記/水珠", false, 1)] static void CtxWater() { if (FirstCall()) MarkSelection(MarkKind.Water); }
    [MenuItem("GameObject/Lizard 標記/礦石", false, 2)] static void CtxOre() { if (FirstCall()) MarkSelection(MarkKind.Ore); }
    [MenuItem("GameObject/Lizard 標記/地洞頂", false, 3)] static void CtxCeil() { if (FirstCall()) MarkSelection(MarkKind.CaveCeiling); }
    [MenuItem("GameObject/Lizard 標記/地洞底", false, 4)] static void CtxFloor() { if (FirstCall()) MarkSelection(MarkKind.CaveFloor); }
    [MenuItem("GameObject/Lizard 標記/清除標記", false, 20)] static void CtxClear() { if (FirstCall()) MarkSelection(null); }
}

// Lizard > 標記工具
public class LevelMarkWindow : EditorWindow
{
    Vector2 scroll;

    [MenuItem("Lizard/標記工具 (Level Marks)")]
    static void Open() => GetWindow<LevelMarkWindow>("標記工具");

    public static void RepaintAll()
    {
        foreach (var w in Resources.FindObjectsOfTypeAll<LevelMarkWindow>()) w.Repaint();
    }

    void OnHierarchyChange() => Repaint();
    void OnSelectionChange() => Repaint();

    void OnGUI()
    {
        LevelMarks.RefreshCache();
        var scene = SceneManager.GetActiveScene();
        var bg = LevelMarks.FindBackground(scene);
        scroll = EditorGUILayout.BeginScrollView(scroll);

        if (bg == null)
            EditorGUILayout.HelpBox($"目前場景找不到 {TerrainSetup.BackgroundName},請開啟 FullTest.unity。", MessageType.Warning);

        EditorGUILayout.LabelField("1. 物件標記", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox("在 Hierarchy 選取背景模型裡的物件(可多選;選群組 = 底下全部網格),再按下面的按鈕。\n" +
            "在 Scene 視窗點物件:第一下會選到整個背景,再點一下才會選到裡面的物件。", MessageType.None);
        EditorGUILayout.LabelField($"目前選取 {Selection.gameObjects.Length} 個物件");
        foreach (MarkKind k in Enum.GetValues(typeof(MarkKind)))
        {
            EditorGUILayout.BeginHorizontal();
            var old = GUI.backgroundColor;
            GUI.backgroundColor = LevelMarks.KindColor(k);
            if (GUILayout.Button($"選取的物件 → {LevelMarks.Label(k)}", GUILayout.Height(24))) LevelMarks.MarkSelection(k);
            GUI.backgroundColor = old;
            int c = LevelMarks.CacheCounts.TryGetValue(k, out int n) ? n : 0;
            GUILayout.Label($"{c} 個", GUILayout.Width(50));
            using (new EditorGUI.DisabledScope(c == 0))
                if (GUILayout.Button("選出來看", GUILayout.Width(70))) LevelMarks.SelectMarked(k);
            EditorGUILayout.EndHorizontal();
        }
        if (GUILayout.Button("清除選取物件的全部標記")) LevelMarks.MarkSelection(null);
        EditorGUILayout.HelpBox("一個物件可以有好幾種標記(例如同時是 地面 和 地洞頂)。\n" +
            "地面:螞蟻和玩家爬的地表(螞蟻只放在地面上;碰撞體雙面,不會從背面穿過去;陡的高牆還是可以爬)\n" +
            "水珠:每個網格是一顆水珠(有標記就不再自動找小球)\n礦石:可以咬的礦石(有標記就不用名稱找)\n" +
            "地洞頂 / 地洞底:上下兩片中間的空間 = 地洞範圍(碰撞體雙面,在洞裡穿不出去)\n" +
            "地洞出入口(圈):洞口的圓圈,自動產生從洞底到圈上方的出入口範圍,要從圈中間穿過\n" +
            "森林邊界:最外圍的葉子/草,至少 3 個,整個包進去再往外推(Scene 視窗會畫出深綠色的線)\n" +
            "出生點(太空艙):玩家出生在這些物件的正中間、站在裡面的地板上,面向森林(碰撞體雙面)\n" +
            "通道邊界(太空艙→森林):太空艙到森林之間那塊地的最外圍物件,跟太空艙一起包成一圈(Scene 視窗畫橘色的線);" +
            "第一關可以在 森林 + 通道 裡活動,走出去會被推回來\n" +
            "石頭階梯:地洞裡要跳過去的石頭;坑裡低於最低石頭頂面 1 公尺 = 深淵,掉進去遊戲結束;" +
            "洞底只有出入口前面和礦石區能走,坑兩側踩上去會被推回來\n" +
            "可穿過(艙門):沒有碰撞體、而且隱藏(像門打開了),例如太空艙的艙門", MessageType.None);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("2. 範圍框", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox("按下去會在 Scene 視窗中心放一個方塊,用 Box Collider 的控制點(Edit Collider)或 Scale 調整大小。\n" +
            "森林範圍:第一關只能在這些框裡活動(可以放好幾個)\n地洞出入口:森林和地洞之間的通道,從這裡爬出去 = 過關", MessageType.None);
        foreach (var role in new[] { GameZone.ZoneRole.Forest, GameZone.ZoneRole.CaveEntrance })
        {
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button($"新增 {LevelMarks.Label(role)} 框", GUILayout.Height(24))) LevelMarks.AddZone(role);
            var zones = LevelMarks.Zones(scene, role);
            GUILayout.Label($"{zones.Length} 個", GUILayout.Width(50));
            using (new EditorGUI.DisabledScope(zones.Length == 0))
                if (GUILayout.Button("選出來看", GUILayout.Width(70))) Selection.objects = zones.Select(z => (UnityEngine.Object)z.gameObject).ToArray();
            EditorGUILayout.EndHorizontal();
        }

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("3. 套用", EditorStyles.boldLabel);
        LevelMarks.ShowInScene = EditorGUILayout.ToggleLeft("在 Scene 視窗 / Hierarchy 顯示標記", LevelMarks.ShowInScene);
        if (GUILayout.Button("套用標記(儲存範圍框 + Setup Terrain + Setup Game)", GUILayout.Height(30)))
        {
            LevelMarks.SaveZones(scene);
            TerrainSetup.SetupFromMenu();
            GameSetup.SetupFromMenu();
            EditorUtility.DisplayDialog("套用標記", "完成。結果在 Logs/TerrainReport.txt、Logs/GameSetupReport.txt(Console 也有)。", "OK");
        }
        EditorGUILayout.LabelField($"標記檔:{LevelMarks.DataPath}", EditorStyles.miniLabel);

        if (LevelMarks.CacheWarnings.Count > 0)
        {
            EditorGUILayout.Space();
            foreach (var w in LevelMarks.CacheWarnings) EditorGUILayout.HelpBox(w, MessageType.Warning);
        }
        EditorGUILayout.EndScrollView();
    }
}
