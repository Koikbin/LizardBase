using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TextCore.LowLevel;

// 選單 Lizard > Setup Game (Stages + Food):對目前開啟的場景(FullTest)
//   有用 Lizard > 標記工具 標記的話,優先依標記(水珠、礦石、地洞頂/底、森林範圍、地洞出入口),沒有標記才用下面的自動判斷:
//   1. 背景裡的小球體(葉片上的水珠)→ TongueFood 水分,整顆吞
//   2. 洞穴底部的礦石物件 → TongueFood 礦石能量,每塊咬 3 口
//   3. GameSystems:LizardResources(毒素/水分/礦石)+ StageManager(森林 → 地洞 → 出口)
//   4. CaveZone(洞穴範圍)、ExitZone(出口)、兩個光柱標記
//   5. 視野 HUD(中文字型 Noto Sans TC)
// 報告寫到 Console 和 Logs/GameSetupReport.txt。可以重複執行,不會重複加東西。
public static class GameSetup
{
    // 洞穴底部的礦石(場景3.fbx 的名稱;跟 場景2.0 的 物件_4060/4078/4079/4080/4086、匯入測試 的 物件_34/52/53/54/60 是同一批幾何、同一位置。原本分析時找到:5 個高細節、約 1 公尺高、位在場景中央地下 Y≈15 的物件)
    static readonly string[] OreObjectNames = { "物件_4079", "物件_4097", "物件_4098", "物件_4099", "物件_4105" };

    const int OreBitesEach = 3;
    const float WaterGoalDefault = 10f;
    const float DropletMaxSize = 0.8f;       // 水珠最大直徑(公尺)
    const float CaveMarginXZ = 3f;           // 洞穴範圍比礦石範圍往外多幾公尺(沒有地洞標記時)
    const float CaveCellSize = 0.2f;         // 地洞範圍格子大小(公尺)
    const bool RequireWaterForCave = false;  // 測試中先關掉;正式玩法改成 true(或在 GameSystems 的 StageManager 勾 Require Water For Cave)
    const float AbyssBelowStones = 1f;       // 比最低的石頭頂面低多少 = 掉進深淵
    const float AbyssMarginXZ = 1f;          // 深淵範圍比石頭水平範圍往外多少(沒有地洞範圍時)
    const float LandingMargin = 0.5f;        // 可以走的洞底:出入口/礦石/石頭往外多少
    const float MinCaveHeight = 0.15f;       // 地洞頂和地洞底至少差這麼多才算地洞

    const string FontSource = @"C:\Windows\Fonts\NotoSansTC-VF.ttf";
    const string FontPath = "Assets/Fonts/NotoSansTC-VF.ttf";
    const string FontAssetPath = "Assets/Fonts/NotoSansTC SDF.asset";
    const string OreChunkMatPath = "Assets/Generated/OreChunk.mat";
    const string CaveBeaconMatPath = "Assets/Generated/CaveBeacon.mat";
    const string ExitBeaconMatPath = "Assets/Generated/ExitBeacon.mat";
    const string ReportPath = "Logs/GameSetupReport.txt";

    [MenuItem("Lizard/Setup Game (Stages + Food)")]
    public static void SetupFromMenu()
    {
        Scene scene = SceneManager.GetActiveScene();
        var report = new StringBuilder($"[GameSetup] 場景 {scene.name}  {System.DateTime.Now}\n");
        try
        {
            if (Setup(scene, report))
            {
                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene);
            }
        }
        finally
        {
            EditorUtility.ClearProgressBar();
            string text = report.ToString();
            Debug.Log(text);
            Directory.CreateDirectory("Logs");
            File.WriteAllText(ReportPath, text, Encoding.UTF8);
        }
    }

    public static bool Setup(Scene scene, StringBuilder report)
    {
        var roots = scene.GetRootGameObjects();
        var bg = roots.FirstOrDefault(g => g.name == TerrainSetup.BackgroundName);
        var player = roots.FirstOrDefault(g => g.GetComponent<LizardLocomotion>() != null);
        var expansion = roots.SelectMany(r => r.GetComponentsInChildren<ExpansionInput>(true)).FirstOrDefault();
        if (bg == null || player == null)
        {
            report.AppendLine($"⚠ 找不到 {(bg == null ? TerrainSetup.BackgroundName : "Player(LizardLocomotion)")},請先開啟 FullTest.unity");
            return false;
        }

        // ---- 0. 標記(Lizard > 標記工具) ----
        var marks = LevelMarks.ResolveForSetup(bg, report);
        int restored = LevelMarks.RestoreZones(scene);
        if (restored > 0) report.AppendLine($"從 {LevelMarks.DataPath} 放回 {restored} 個範圍框");
        var oldSystems = roots.FirstOrDefault(g => g.name == "GameSystems");
        if (oldSystems != null)   // 上次依出入口圈產生的範圍先拿掉,下面重新產生
        {
            DestroyChild(oldSystems.transform, "Entrances");
            DestroyChild(oldSystems.transform, "ForestBoundary");
            DestroyChild(oldSystems.transform, "PathBoundary");
        }
        var forestZones = LevelMarks.Zones(scene, GameZone.ZoneRole.Forest);
        var entranceZones = LevelMarks.Zones(scene, GameZone.ZoneRole.CaveEntrance);

        // 上次 Setup 加上去的食物先拿掉(標記改過時,沒被標記的物件不能還是水珠/礦石)
        foreach (var old in bg.GetComponentsInChildren<TongueFood>(true))
            if (PrefabUtility.IsAddedComponentOverride(old)) Object.DestroyImmediate(old);

        // ---- 1. 水珠 ----
        EditorUtility.DisplayProgressBar("Setup Game", "設定水珠", 0.1f);
        var droplets = new List<TongueFood>();
        var markedWater = LevelMarks.Of(marks, MarkKind.Water);
        IEnumerable<MeshFilter> waterSources = markedWater.Count > 0
            ? markedWater
            : bg.GetComponentsInChildren<MeshFilter>(true).Where(IsAutoDroplet).ToList();
        foreach (var mf in waterSources)
        {
            RemoveTerrainColliders(mf.transform);    // 水珠不是地面,也要能被吞掉
            var food = GetOrAdd<TongueFood>(mf.gameObject);
            food.kind = ResourceKind.Water;
            food.amountPerBite = 1f;
            food.bites = 1;
            food.swallowWhole = true;
            food.catchPadding = 0.08f;
            food.respawnSeconds = 0f;
            EditorUtility.SetDirty(food);
            droplets.Add(food);
        }
        report.AppendLine($"水珠 {droplets.Count} 顆(每顆水分 +1)" + (markedWater.Count > 0 ? ",依標記" : ",自動找小球(沒有水珠標記)"));

        // ---- 2. 礦石 ----
        EditorUtility.DisplayProgressBar("Setup Game", "設定礦石", 0.3f);
        var chunkMat = GetOrCreateMaterial(OreChunkMatPath, new Color(0.55f, 0.3f, 1f), emission: new Color(0.6f, 0.3f, 1f) * 1.5f);
        var ores = new List<TongueFood>();
        var oreObjects = new List<GameObject>();
        var markedOre = LevelMarks.Of(marks, MarkKind.Ore);
        if (markedOre.Count > 0) oreObjects.AddRange(markedOre.Select(mf => mf.gameObject));
        else
        {
            var all = bg.GetComponentsInChildren<Transform>(true);
            foreach (var oreName in OreObjectNames)
            {
                var t = all.FirstOrDefault(x => x.name == oreName);
                if (t == null) report.AppendLine($"  ⚠ 找不到礦石物件 {oreName}");
                else oreObjects.Add(t.gameObject);
            }
        }
        foreach (var go in oreObjects)
        {
            var food = GetOrAdd<TongueFood>(go);
            food.kind = ResourceKind.OreEnergy;
            food.amountPerBite = 1f;
            food.bites = OreBitesEach;
            food.swallowWhole = false;           // 咬一小塊,礦石本體(和它的碰撞體)留著
            food.catchPadding = 0.12f;
            food.chunkMaterial = chunkMat;
            food.chunkSize = 0.06f;
            EditorUtility.SetDirty(food);
            ores.Add(food);
        }
        report.AppendLine($"礦石 {ores.Count} 塊,每塊 {OreBitesEach} 口 → 礦石能量總共 {ores.Count * OreBitesEach}" + (markedOre.Count > 0 ? "(依標記)" : "(依名稱)"));

        // ---- 3. 洞穴範圍與出口 ----
        EditorUtility.DisplayProgressBar("Setup Game", "建立關卡區域", 0.5f);
        var systems = roots.FirstOrDefault(g => g.name == "GameSystems");
        if (systems == null)
        {
            systems = new GameObject("GameSystems");
            SceneManager.MoveGameObjectToScene(systems, scene);
        }
        DestroyChild(systems.transform, "CaveZone");
        DestroyChild(systems.transform, "CaveVolume");
        DestroyChild(systems.transform, "ExitZone");
        DestroyChild(systems.transform, "CaveBeacon");
        DestroyChild(systems.transform, "ExitBeacon");

        GameZone caveZone = null, exitZone = null;
        CaveVolume caveVolume = null;
        GameObject caveBeacon = null, exitBeacon = null;
        bool hasCaveArea = false;
        Bounds caveBounds = default;

        var ceilings = LevelMarks.Of(marks, MarkKind.CaveCeiling);
        var floors = LevelMarks.Of(marks, MarkKind.CaveFloor);
        if (ceilings.Count > 0 && floors.Count > 0)
        {
            // 依標記:地洞頂和地洞底中間的空間
            var go = new GameObject("CaveVolume");
            go.transform.SetParent(systems.transform, false);
            caveVolume = go.AddComponent<CaveVolume>();
            if (BuildCaveVolume(caveVolume, ceilings, floors, report))
            {
                hasCaveArea = true;
                caveBounds = caveVolume.bounds;
            }
            else { Object.DestroyImmediate(go); caveVolume = null; }
        }
        else if (ceilings.Count + floors.Count > 0)
            report.AppendLine("⚠ 地洞頂和地洞底要兩種都有標記才能算地洞範圍,先用礦石位置估計");

        if (!hasCaveArea && ores.Count > 0)
        {
            // 沒有標記:用礦石的範圍往外擴
            Bounds oreB = TerrainSetup.RendererBounds(ores[0].gameObject);
            foreach (var o in ores) oreB.Encapsulate(TerrainSetup.RendererBounds(o.gameObject));
            Bounds bgB = TerrainSetup.RendererBounds(bg);

            float top = oreB.max.y + 0.5f;
            Vector3 min = new Vector3(oreB.min.x - CaveMarginXZ, bgB.min.y - 0.5f, oreB.min.z - CaveMarginXZ);
            Vector3 max = new Vector3(oreB.max.x + CaveMarginXZ, top, oreB.max.z + CaveMarginXZ);

            // 洞穴範圍不能蓋到森林的水珠
            foreach (var d in droplets)
            {
                Bounds db = d.WorldBounds();
                bool overXZ = db.center.x > min.x && db.center.x < max.x && db.center.z > min.z && db.center.z < max.z;
                if (overXZ && db.min.y - 0.1f < max.y)
                {
                    max.y = db.min.y - 0.1f;
                    report.AppendLine($"  洞穴範圍頂部降到 {max.y:F2},避開水珠 {d.name}");
                }
            }
            caveBounds = new Bounds((min + max) * 0.5f, max - min);
            caveZone = CreateZone(systems.transform, "CaveZone", caveBounds, new Color(1f, 0.5f, 0f, 0.15f));
            hasCaveArea = true;
            report.AppendLine($"洞穴範圍 CaveZone(依礦石位置估計):中心 {caveBounds.center},大小 {caveBounds.size}(可在 Scene 視窗拖 Box Collider 調整)");
        }

        // 地洞出入口(圈):從洞底一路到圈的上方,玩家從圈中間穿過時一定在這個範圍裡
        var rings = LevelMarks.Of(marks, MarkKind.CaveEntrance);
        if (rings.Count > 0)
        {
            var parent = new GameObject("Entrances").transform;
            parent.SetParent(systems.transform, false);
            var floorSet = new HashSet<Transform>(floors.Select(f => f.transform));
            var made = new List<GameZone>();
            foreach (var ring in rings)
            {
                Bounds rb = ring.GetComponent<Renderer>().bounds;
                float? floorY = FloorBelow(rb, floorSet);
                float bottom = (floorY ?? rb.min.y - 2.5f) - 0.3f;
                float top = rb.max.y + 1f;
                var zb = new Bounds(new Vector3(rb.center.x, (top + bottom) * 0.5f, rb.center.z),
                                    new Vector3(rb.size.x + 0.4f, top - bottom, rb.size.z + 0.4f));
                var z = CreateZone(parent, $"Entrance_{ring.name}", zb, new Color(1f, 0.85f, 0.2f, 0.25f));
                z.role = GameZone.ZoneRole.CaveEntrance;
                made.Add(z);
                report.AppendLine($"地洞出入口 {ring.name}:範圍 Y {bottom:F2}~{top:F2}" +
                    (floorY.HasValue ? $"(洞底 {floorY.Value:F2})" : "(⚠ 圈下面找不到地洞底,先往下 2.5 公尺)"));
            }
            entranceZones = entranceZones.Concat(made).ToArray();
        }

        // 森林邊界:最外圍的葉子/草連成一圈
        ForestBoundary forest = null;
        var edge = LevelMarks.Of(marks, MarkKind.ForestEdge);
        if (edge.Count >= 3)
        {
            var go = new GameObject("ForestBoundary");
            go.transform.SetParent(systems.transform, false);
            forest = go.AddComponent<ForestBoundary>();
            var boxes = edge.Select(m => m.GetComponent<Renderer>().bounds).ToList();
            var groundSet = new HashSet<Transform>(LevelMarks.Of(marks, MarkKind.Ground).Select(m => m.transform));
            forest.polygon = ForestBoundary.BuildEnvelope(boxes, LevelMarks.ForestEdgeMargin, 48,
                groundSet.Count > 0 ? (System.Func<Vector2, bool>)(p => LevelMarks.IsOverGround(p, groundSet)) : null);
            forest.drawY = boxes.Average(b => b.center.y);
            EditorUtility.SetDirty(forest);
            int cut = boxes.Count(b => !new[] { -1f, 1f }.All(sx => new[] { -1f, 1f }.All(sz =>
                forest.Contains(b.center + new Vector3(sx * b.extents.x, 0f, sz * b.extents.z)))));
            report.AppendLine($"森林邊界:{edge.Count} 個物件整個包進去再往外 {LevelMarks.ForestEdgeMargin} m,面積 {ForestBoundary.Area(forest.polygon):F1} m²" +
                (groundSet.Count > 0 ? ",超出地面的地方收回地面邊緣" : "") +
                (cut > 0 ? $"(⚠ {cut} 個物件因為靠近地面邊緣,有一部分在範圍外)" : ",沒有切到任何物件"));
        }
        else if (edge.Count > 0) report.AppendLine($"⚠ 森林邊界只有 {edge.Count} 個點,至少要 3 個才能連成範圍");

        // 通道(太空艙 → 森林):通道邊界標記 + 太空艙包成一圈,第一關也可以在這裡活動
        ForestBoundary path = null;
        var pathEdge = LevelMarks.Of(marks, MarkKind.PathEdge);
        if (pathEdge.Count > 0)
        {
            var pathBoxes = pathEdge.Select(m => m.GetComponent<Renderer>().bounds).ToList();
            var spawnBoxes = LevelMarks.Of(marks, MarkKind.Spawn).Select(m => LevelMarks.MeshWorldBounds(m)).ToList();
            var groundSet = new HashSet<Transform>(LevelMarks.Of(marks, MarkKind.Ground).Select(m => m.transform));
            var poly = LevelMarks.BuildPathPolygon(pathBoxes, spawnBoxes, groundSet);
            if (poly != null)
            {
                var go = new GameObject("PathBoundary");
                go.transform.SetParent(systems.transform, false);
                path = go.AddComponent<ForestBoundary>();
                path.polygon = poly;
                path.drawY = pathBoxes.Average(b => b.center.y);
                path.gizmoColor = new Color(1f, 0.6f, 0.15f, 1f);
                EditorUtility.SetDirty(path);
                report.AppendLine($"通道邊界(太空艙→森林):{pathEdge.Count} 個物件 + 太空艙 {spawnBoxes.Count} 個物件包成一圈,再往外 {LevelMarks.PathEdgeMargin} m(超出地面的地方收回地面邊緣),面積 {ForestBoundary.Area(poly):F1} m²");
            }
            else report.AppendLine($"⚠ 通道邊界 {pathEdge.Count} 個 + 太空艙 {spawnBoxes.Count} 個,合起來至少要 3 個才能連成範圍");
        }

        // 石頭階梯:坑 = 深淵(掉進去遊戲結束);洞底只有出入口前面和礦石區可以走,坑兩側不能走(一定要跳石頭)
        GameZone abyss = null;
        var stones = LevelMarks.Of(marks, MarkKind.Stepping);
        DestroyChild(systems.transform, "AbyssZone");
        if (caveVolume != null) caveVolume.zone = null;
        var oldSlope = systems.GetComponent<SlipperySlope>();
        if (stones.Count == 0 && oldSlope != null) Object.DestroyImmediate(oldSlope);
        if (stones.Count > 0)
        {
            var sb = stones.Select(st => st.GetComponent<Renderer>().bounds).ToList();
            // 石頭旁邊、比石頭低的坑壁(地洞底標記)抓不住、會往下滑
            var slope = GetOrAdd<SlipperySlope>(systems);
            slope.stones = sb.ToArray();
            slope.surfaces = LevelMarks.Of(marks, MarkKind.CaveFloor).Select(m => m.transform).ToArray();
            EditorUtility.SetDirty(slope);
            report.AppendLine($"  濕滑坑壁:石頭 {slope.range} m 以內、比最近那塊石頭頂面低的地洞底({slope.surfaces.Length} 個物件)抓不住、站上去會往下滑");
            float abyssY = sb.Min(x => x.max.y) - AbyssBelowStones;
            report.AppendLine($"石頭階梯 {stones.Count} 塊:最低的頂面往下 {AbyssBelowStones} m(高度 {abyssY:F2})以下 = 深淵");
            foreach (var pair in stones.Zip(sb, (m, bb) => (m, bb)).OrderBy(x => -x.bb.center.z))
            {
                float near = sb.Where(o => o != pair.bb).Select(o => EdgeGap(pair.bb, o)).DefaultIfEmpty(0f).Min();
                report.AppendLine($"  {pair.m.name}:頂面 {pair.bb.max.y:F2},大小 {pair.bb.size.x:F1}x{pair.bb.size.z:F1} m,和最近的石頭水平間距 {near:F2} m");
            }

            if (caveVolume != null)
            {
                // 可以走的洞底:每個出入口圈 + 最近的石頭;所有礦石 + 最近的石頭(各自包成凸多邊形,往外 LandingMargin)
                var ringB = LevelMarks.Of(marks, MarkKind.CaveEntrance).Select(r => r.GetComponent<Renderer>().bounds).ToList();
                var oreB = ores.Select(o => o.WorldBounds()).ToList();
                var landings = new List<Vector2[]>();
                System.Func<Vector3, Bounds> nearestStone = c => sb.OrderBy(x => (x.center - c).sqrMagnitude).First();
                foreach (var rb in ringB) landings.Add(Hull(new[] { rb, nearestStone(rb.center) }, LandingMargin));
                if (oreB.Count > 0)
                {
                    Vector3 oc = Vector3.zero;
                    foreach (var ob in oreB) oc += ob.center;
                    oc /= oreB.Count;
                    landings.Add(Hull(oreB.Concat(new[] { nearestStone(oc) }), LandingMargin));
                }

                var vol = caveVolume;
                vol.zone = new byte[vol.floorY.Length];
                vol.abyssY = abyssY;
                int nLanding = 0, nPit = 0, nSide = 0;
                for (int iz = 0; iz < vol.countZ; iz++)
                    for (int ix = 0; ix < vol.countX; ix++)
                    {
                        int i = iz * vol.countX + ix;
                        if (vol.ceilingY[i] <= vol.floorY[i]) continue;
                        var c2 = new Vector2(vol.origin.x + (ix + 0.5f) * vol.cellSize, vol.origin.y + (iz + 0.5f) * vol.cellSize);
                        if (vol.floorY[i] < abyssY) { vol.zone[i] = (byte)CaveVolume.Zone.Pit; nPit++; }
                        else if (landings.Any(L => InPolygon(L, c2))) { vol.zone[i] = (byte)CaveVolume.Zone.Landing; nLanding++; }
                        else { vol.zone[i] = (byte)CaveVolume.Zone.Side; nSide++; }
                    }
                EditorUtility.SetDirty(vol);
                float a2 = vol.cellSize * vol.cellSize;
                report.AppendLine($"  洞底分區:可以走 {nLanding * a2:F0} m²(出入口前面 {ringB.Count} 塊 + 礦石區),深淵 {nPit * a2:F0} m²,坑兩側不能走 {nSide * a2:F0} m²");
                foreach (var ob in oreB)
                    if (vol.ZoneAt(ob.center) == CaveVolume.Zone.Side) report.AppendLine($"  ⚠ 有礦石在不能走的側邊洞底上");
            }
            else
            {
                // 沒有地洞範圍時退回方塊:石頭水平範圍往外 1 公尺
                Bounds all = sb[0];
                foreach (var x in sb) all.Encapsulate(x);
                float bottom = TerrainSetup.RendererBounds(bg).min.y - 10f;
                var ab = new Bounds(new Vector3(all.center.x, (abyssY + bottom) * 0.5f, all.center.z),
                                    new Vector3(all.size.x + 2f * AbyssMarginXZ, abyssY - bottom, all.size.z + 2f * AbyssMarginXZ));
                abyss = CreateZone(systems.transform, "AbyssZone", ab, new Color(0.2f, 0f, 0.3f, 0.25f));
                report.AppendLine("  ⚠ 沒有地洞範圍(地洞頂/地洞底標記),深淵先用石頭周圍的方塊");
            }
        }

        bool hasForestLimit = forestZones.Length > 0 || forest != null;
        System.Func<Vector3, bool> inForest = p => !hasForestLimit || forestZones.Any(z => z.Contains(p)) || (forest != null && forest.Contains(p))
                                                   || (path != null && path.Contains(p));
        if (hasForestLimit)
        {
            int outside = droplets.Count(d => !inForest(d.WorldBounds().center));
            if (outside > 0) report.AppendLine($"  ⚠ 有 {outside} 顆水珠在森林範圍外面,第一關吃不到");
        }

        if (hasCaveArea)
        {
            Vector3 pp = player.transform.position;
            bool playerInCave = caveVolume != null ? caveVolume.Contains(pp) : caveZone.Contains(pp);
            if (playerInCave)
                report.AppendLine("  ⚠ Player 目前在洞穴範圍裡,遊戲一開始會被推出去。請把 Player 移到森林");
            if (!inForest(pp) && !entranceZones.Any(z => z.Contains(pp)))
                report.AppendLine("  Player 出生在森林範圍外:走進森林之後才開始限制範圍(之前不會推回)");
            if (caveVolume != null)
                foreach (var z in entranceZones)
                    if (!TouchesCave(z, caveVolume))
                        report.AppendLine($"  ⚠ {z.name} 和地洞範圍沒有接在一起,從這裡進出會被當成鑽縫隙推回來");

            var caveMat = GetOrCreateMaterial(CaveBeaconMatPath, new Color(0.3f, 0.9f, 1f), emission: new Color(0.3f, 0.9f, 1f) * 2f);
            var exitMat = GetOrCreateMaterial(ExitBeaconMatPath, new Color(1f, 0.85f, 0.2f), emission: new Color(1f, 0.85f, 0.2f) * 2f);

            if (entranceZones.Length > 0)
            {
                // 依標記:出入口就是洞口,從出入口爬出去 = 過關(不另外放 ExitZone)
                var doorBox = entranceZones[0].GetComponent<BoxCollider>().bounds;
                var doorBase = new Vector3(doorBox.center.x, doorBox.max.y - 1f, doorBox.center.z);
                caveBeacon = CreateBeacon(systems.transform, "CaveBeacon", doorBase, caveMat);
                exitBeacon = CreateBeacon(systems.transform, "ExitBeacon", doorBase, exitMat);
                report.AppendLine($"地洞出入口 {entranceZones.Length} 個:從出入口爬出洞外 = 過關;光柱在 {entranceZones[0].name}");
            }
            else
            {
                // 出口:洞口正上方,爬出洞就到(找不到真正的出口時的預設值)
                Vector3 exitCenter = new Vector3(caveBounds.center.x, caveBounds.max.y + 1.5f, caveBounds.center.z);
                exitZone = CreateZone(systems.transform, "ExitZone", new Bounds(exitCenter, new Vector3(4f, 3f, 4f)), new Color(1f, 0.9f, 0.2f, 0.2f));
                report.AppendLine($"出口 ExitZone:中心 {exitCenter}(沒有地洞出入口標記,預設在洞穴正上方)");
                caveBeacon = CreateBeacon(systems.transform, "CaveBeacon", new Vector3(caveBounds.center.x, caveBounds.max.y, caveBounds.center.z), caveMat);
                exitBeacon = CreateBeacon(exitZone.transform, "ExitBeacon", exitCenter - Vector3.up * 1.5f, exitMat);
            }
        }
        else report.AppendLine("⚠ 沒有地洞標記也沒有礦石,沒有建立洞穴範圍");
        if (forestZones.Length > 0) report.AppendLine($"森林範圍框 {forestZones.Length} 個");
        if (hasForestLimit) report.AppendLine("第一關只能在森林範圍(框 + 邊界圈)" + (path != null ? "、通道(太空艙→森林)" : "") + "和地洞出入口裡活動");

        // ---- 4. 系統 ----
        var resources = GetOrAdd<LizardResources>(systems);
        var stages = GetOrAdd<StageManager>(systems);
        stages.player = player.transform;
        stages.caveZone = caveZone;
        stages.caveVolume = caveVolume;
        stages.exitZone = exitZone;
        stages.forestZones = forestZones;
        stages.forestBoundary = forest;
        stages.pathBoundary = path;
        stages.abyssZone = abyss;
        stages.entranceZones = entranceZones;
        stages.caveBeacon = caveBeacon;
        stages.exitBeacon = exitBeacon;
        stages.waterGoal = Mathf.Min(WaterGoalDefault, droplets.Count);
        stages.requireWaterForCave = RequireWaterForCave;
        if (!RequireWaterForCave) report.AppendLine("⚠ 測試模式:不用吃夠水珠就能進地洞(GameSetup.RequireWaterForCave = false)");
        stages.oreGoal = 0f;  // 全部礦石
        report.AppendLine($"關卡目標:水分 {stages.waterGoal} / 礦石能量 全部({ores.Count * OreBitesEach})");
        LevelMarks.SaveZones(scene);

        // ---- 5. HUD ----
        EditorUtility.DisplayProgressBar("Setup Game", "建立中文字型與 HUD", 0.7f);
        var font = GetOrCreateCjkFont(report, stages);
        if (expansion != null && expansion.displayRoot != null)
        {
            var canvas = expansion.displayRoot;
            var hudT = canvas.Find("GameHUD");
            if (hudT == null)
            {
                var go = new GameObject("GameHUD", typeof(RectTransform));
                go.layer = canvas.gameObject.layer;
                hudT = go.transform;
                hudT.SetParent(canvas, false);
            }
            var rt = (RectTransform)hudT;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = new Vector2(0f, 230f);
            rt.sizeDelta = new Vector2(760f, 260f);
            var tmp = GetOrAdd<TextMeshProUGUI>(hudT.gameObject);
            if (font != null) tmp.font = font;
            tmp.fontSize = 34;
            tmp.fontStyle = FontStyles.Bold;     // Noto Sans TC 可變字型預設是最細的字重,用粗體補回來
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = Color.white;
            tmp.textWrappingMode = TextWrappingModes.NoWrap;
            tmp.text = "第一關:森林";
            stages.hud = tmp;
            EditorUtility.SetDirty(tmp);
            report.AppendLine("HUD:Canvas/GameHUD(跟著視線,在膨脹提示文字上方)");
        }
        else report.AppendLine("⚠ 找不到 ExpansionInput 的 Canvas,沒有建立 HUD");

        EditorUtility.SetDirty(resources);
        EditorUtility.SetDirty(stages);
        return true;
    }

    // ---------------------------------------------------------------

    static bool IsAutoDroplet(MeshFilter mf)
    {
        if (mf.sharedMesh == null || mf.sharedMesh.vertexCount < 2000) return false;
        var r = mf.GetComponent<Renderer>();
        if (r == null) return false;
        Vector3 s = r.bounds.size;
        float max = Mathf.Max(s.x, s.y, s.z), min = Mathf.Min(s.x, s.y, s.z);
        return max <= DropletMaxSize && min > 0f && max / min <= 1.12f;   // 小球
    }

    // 圈的正下方,地洞底的高度(用 Setup Terrain 產生的碰撞體往下打)
    static float? FloorBelow(Bounds ring, HashSet<Transform> floorSet)
    {
        var hits = Physics.RaycastAll(new Vector3(ring.center.x, ring.max.y + 0.5f, ring.center.z), Vector3.down, 15f, ~0, QueryTriggerInteraction.Ignore);
        float? best = null;
        foreach (var h in hits)
        {
            var t = h.collider.transform;
            if (floorSet.Contains(t) || (t.parent != null && floorSet.Contains(t.parent)))
                if (best == null || h.point.y > best.Value) best = h.point.y;
        }
        return best;
    }

    // 出入口範圍裡至少有一些點在地洞範圍內(接得上)
    static bool TouchesCave(GameZone z, CaveVolume vol)
    {
        var b = z.GetComponent<BoxCollider>();
        for (int ix = 0; ix < 5; ix++)
            for (int iy = 0; iy < 10; iy++)
                for (int iz = 0; iz < 5; iz++)
                {
                    var local = b.center + Vector3.Scale(b.size, new Vector3(ix / 4f - 0.5f, iy / 9f - 0.5f, iz / 4f - 0.5f));
                    if (vol.Contains(z.transform.TransformPoint(local))) return true;
                }
        return false;
    }

    // 一堆方塊的水平範圍(往外 margin)包成凸多邊形
    static Vector2[] Hull(IEnumerable<Bounds> boxes, float margin)
    {
        var pts = new List<Vector2>();
        foreach (var b in boxes)
            foreach (float sx in new[] { -1f, 1f })
                foreach (float sz in new[] { -1f, 1f })
                    pts.Add(new Vector2(b.center.x + sx * (b.extents.x + margin), b.center.z + sz * (b.extents.z + margin)));
        pts = pts.Distinct().OrderBy(p => p.x).ThenBy(p => p.y).ToList();
        if (pts.Count < 3) return pts.ToArray();
        System.Func<Vector2, Vector2, Vector2, float> cross = (o, a, b) => (a.x - o.x) * (b.y - o.y) - (a.y - o.y) * (b.x - o.x);
        var hull = new List<Vector2>();
        for (int pass = 0; pass < 2; pass++)
        {
            int start = hull.Count;
            foreach (var p in pass == 0 ? pts : Enumerable.Reverse(pts))
            {
                while (hull.Count >= start + 2 && cross(hull[hull.Count - 2], hull[hull.Count - 1], p) <= 0f) hull.RemoveAt(hull.Count - 1);
                hull.Add(p);
            }
            hull.RemoveAt(hull.Count - 1);
        }
        return hull.ToArray();
    }

    static bool InPolygon(Vector2[] poly, Vector2 p)
    {
        bool inside = false;
        for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
            if ((poly[i].y > p.y) != (poly[j].y > p.y) &&
                p.x < (poly[j].x - poly[i].x) * (p.y - poly[i].y) / (poly[j].y - poly[i].y) + poly[i].x)
                inside = !inside;
        return inside;
    }

    // 兩個方塊在水平方向的最近距離(重疊 = 0)
    static float EdgeGap(Bounds a, Bounds b)
    {
        float dx = Mathf.Max(0f, Mathf.Max(a.min.x - b.max.x, b.min.x - a.max.x));
        float dz = Mathf.Max(0f, Mathf.Max(a.min.z - b.max.z, b.min.z - a.max.z));
        return Mathf.Sqrt(dx * dx + dz * dz);
    }

    static void DestroyChild(Transform parent, string name)
    {
        var t = parent.Find(name);
        if (t != null) Object.DestroyImmediate(t.gameObject);
    }

    // 地洞範圍:把 地洞頂 / 地洞底 的三角面投影到水平格子上
    //   每格:地洞頂最低的面 = 頂的下緣;地洞底在頂下緣以下最高的面 = 地板
    static bool BuildCaveVolume(CaveVolume vol, List<MeshFilter> ceilings, List<MeshFilter> floors, StringBuilder report)
    {
        const float cell = CaveCellSize;
        Bounds area = ceilings[0].GetComponent<Renderer>().bounds;
        foreach (var mf in ceilings.Concat(floors)) area.Encapsulate(mf.GetComponent<Renderer>().bounds);

        int nx = Mathf.CeilToInt(area.size.x / cell) + 1, nz = Mathf.CeilToInt(area.size.z / cell) + 1;
        if ((long)nx * nz > 4000000)
        {
            report.AppendLine($"⚠ 地洞標記範圍太大({area.size.x:F0} x {area.size.z:F0} 公尺),請只標記地洞上下那兩片物件");
            return false;
        }
        var origin = new Vector2(area.min.x, area.min.z);
        var ceil = new float[nx * nz];
        var floor = new float[nx * nz];
        for (int i = 0; i < ceil.Length; i++) { ceil[i] = float.PositiveInfinity; floor[i] = float.NegativeInfinity; }

        EditorUtility.DisplayProgressBar("Setup Game", "計算地洞範圍(地洞頂)", 0.55f);
        foreach (var mf in ceilings) Rasterize(mf, origin, cell, nx, nz, (i, y) => { if (y < ceil[i]) ceil[i] = y; });
        FillGaps(ceil, nx, nz, float.PositiveInfinity);
        EditorUtility.DisplayProgressBar("Setup Game", "計算地洞範圍(地洞底)", 0.6f);
        foreach (var mf in floors) Rasterize(mf, origin, cell, nx, nz, (i, y) => { if (y < ceil[i] - 0.01f && y > floor[i]) floor[i] = y; });
        FillGaps(floor, nx, nz, float.NegativeInfinity);

        int valid = 0;
        float minH = float.MaxValue, maxH = 0f, sumH = 0f;
        var b = new Bounds();
        bool init = false;
        for (int z = 0; z < nz; z++)
            for (int x = 0; x < nx; x++)
            {
                int i = z * nx + x;
                float h = ceil[i] - floor[i];
                if (float.IsInfinity(ceil[i]) || float.IsInfinity(floor[i]) || h < MinCaveHeight)
                {
                    ceil[i] = 0f; floor[i] = 1f;   // 不是地洞
                    continue;
                }
                valid++;
                minH = Mathf.Min(minH, h); maxH = Mathf.Max(maxH, h); sumH += h;
                float wx = origin.x + (x + 0.5f) * cell, wz = origin.y + (z + 0.5f) * cell;
                var lo = new Vector3(wx, floor[i], wz);
                var hi = new Vector3(wx, ceil[i], wz);
                if (!init) { b = new Bounds(lo, Vector3.zero); init = true; }
                b.Encapsulate(lo); b.Encapsulate(hi);
            }
        if (valid == 0)
        {
            report.AppendLine("⚠ 地洞頂和地洞底之間找不到空間:請確認地洞頂在上、地洞底在下,而且兩片在水平方向有重疊");
            return false;
        }
        b.Expand(new Vector3(cell, 0f, cell));

        vol.origin = origin;
        vol.cellSize = cell;
        vol.countX = nx;
        vol.countZ = nz;
        vol.ceilingY = ceil;
        vol.floorY = floor;
        vol.bounds = b;
        EditorUtility.SetDirty(vol);
        report.AppendLine($"地洞範圍 CaveVolume(依標記:地洞頂 {ceilings.Count} 個、地洞底 {floors.Count} 個):");
        report.AppendLine($"  面積 {valid * cell * cell:F1} m²,高度 {minH:F2}~{maxH:F2} m(平均 {sumH / valid:F2}),範圍中心 {b.center},大小 {b.size}");
        return true;
    }

    // 把網格的每個三角面投影到 XZ 格子,在格子中心算出三角面的高度
    static void Rasterize(MeshFilter mf, Vector2 origin, float cell, int nx, int nz, System.Action<int, float> write)
    {
        var mesh = mf.sharedMesh;
        if (!mesh.isReadable) return;   // TerrainSetup 會先開啟 Read/Write
        var local = mesh.vertices;
        var m = mf.transform.localToWorldMatrix;
        var w = new Vector3[local.Length];
        for (int i = 0; i < local.Length; i++) w[i] = m.MultiplyPoint3x4(local[i]);
        for (int s = 0; s < mesh.subMeshCount; s++)
        {
            var tris = mesh.GetTriangles(s);
            for (int t = 0; t < tris.Length; t += 3)
            {
                Vector3 a = w[tris[t]], b = w[tris[t + 1]], c = w[tris[t + 2]];
                float det = (b.x - a.x) * (c.z - a.z) - (c.x - a.x) * (b.z - a.z);
                if (Mathf.Abs(det) < 1e-8f) continue;   // 垂直的面,投影下去沒有面積
                int x0 = Mathf.Max(0, Mathf.FloorToInt((Mathf.Min(a.x, Mathf.Min(b.x, c.x)) - origin.x) / cell - 0.5f));
                int x1 = Mathf.Min(nx - 1, Mathf.CeilToInt((Mathf.Max(a.x, Mathf.Max(b.x, c.x)) - origin.x) / cell - 0.5f));
                int z0 = Mathf.Max(0, Mathf.FloorToInt((Mathf.Min(a.z, Mathf.Min(b.z, c.z)) - origin.y) / cell - 0.5f));
                int z1 = Mathf.Min(nz - 1, Mathf.CeilToInt((Mathf.Max(a.z, Mathf.Max(b.z, c.z)) - origin.y) / cell - 0.5f));
                for (int z = z0; z <= z1; z++)
                    for (int x = x0; x <= x1; x++)
                    {
                        float px = origin.x + (x + 0.5f) * cell, pz = origin.y + (z + 0.5f) * cell;
                        float u = ((px - a.x) * (c.z - a.z) - (c.x - a.x) * (pz - a.z)) / det;
                        float v = ((b.x - a.x) * (pz - a.z) - (px - a.x) * (b.z - a.z)) / det;
                        if (u < -1e-4f || v < -1e-4f || u + v > 1f + 1e-4f) continue;
                        write(z * nx + x, a.y + u * (b.y - a.y) + v * (c.y - a.y));
                    }
            }
        }
    }

    // 補格子中間沒取樣到的小洞(周圍 8 格有 5 格以上有值就取平均)
    static void FillGaps(float[] h, int nx, int nz, float empty)
    {
        var copy = (float[])h.Clone();
        for (int z = 1; z < nz - 1; z++)
            for (int x = 1; x < nx - 1; x++)
            {
                if (copy[z * nx + x] != empty) continue;
                int n = 0; float sum = 0f;
                for (int dz = -1; dz <= 1; dz++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        float v = copy[(z + dz) * nx + x + dx];
                        if (v != empty) { n++; sum += v; }
                    }
                if (n >= 5) h[z * nx + x] = sum / n;
            }
    }

    static T GetOrAdd<T>(GameObject go) where T : Component =>
        go.GetComponent<T>() ?? go.AddComponent<T>();

    static void RemoveTerrainColliders(Transform t)
    {
        foreach (Transform c in t.Cast<Transform>().ToList())
            if (c.name == "_GroundCollider" || c.name == "_CliffCollider") Object.DestroyImmediate(c.gameObject);
        foreach (var col in t.GetComponents<MeshCollider>())
        {
            if (PrefabUtility.IsAddedComponentOverride(col)) Object.DestroyImmediate(col);
            else col.enabled = false;
        }
    }

    static GameZone CreateZone(Transform parent, string name, Bounds worldBounds, Color color)
    {
        var existing = parent.Find(name);
        if (existing != null) Object.DestroyImmediate(existing.gameObject);
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.transform.position = worldBounds.center;
        var box = go.AddComponent<BoxCollider>();
        box.isTrigger = true;
        box.size = worldBounds.size;
        var zone = go.AddComponent<GameZone>();
        zone.gizmoColor = color;
        return zone;
    }

    static GameObject CreateBeacon(Transform parent, string name, Vector3 basePos, Material mat)
    {
        var existing = parent.Find(name);
        if (existing != null) Object.DestroyImmediate(existing.gameObject);
        var go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        go.name = name;
        Object.DestroyImmediate(go.GetComponent<Collider>());
        go.transform.SetParent(parent, true);
        go.transform.localScale = new Vector3(0.15f, 2.5f, 0.15f);   // 圓柱高度 2 → 5 公尺高的光柱
        go.transform.position = basePos + Vector3.up * 2.5f;
        go.GetComponent<Renderer>().sharedMaterial = mat;
        go.SetActive(false);   // StageManager 依階段開關
        return go;
    }

    static Material GetOrCreateMaterial(string path, Color baseColor, Color emission)
    {
        var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat != null) return mat;
        var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
        mat = new Material(shader);
        mat.SetColor("_BaseColor", baseColor);
        mat.SetColor("_Color", baseColor);
        mat.EnableKeyword("_EMISSION");
        mat.SetColor("_EmissionColor", emission);
        mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        AssetDatabase.CreateAsset(mat, path);
        return mat;
    }

    static TMP_FontAsset GetOrCreateCjkFont(StringBuilder report, StageManager stages)
    {
        var fa = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(FontAssetPath);
        if (fa == null)
        {
            if (!File.Exists(FontPath))
            {
                if (!File.Exists(FontSource))
                {
                    report.AppendLine($"⚠ 找不到 {FontSource},HUD 的中文會顯示成方塊。請自行放一個中文字型到 Assets/Fonts 並建立 TMP Font Asset");
                    return null;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(FontPath));
                File.Copy(FontSource, FontPath);
                AssetDatabase.ImportAsset(FontPath);
            }
            var font = AssetDatabase.LoadAssetAtPath<Font>(FontPath);
            fa = TMP_FontAsset.CreateFontAsset(font, 64, 6, GlyphRenderMode.SDFAA, 2048, 2048, AtlasPopulationMode.Dynamic, true);
            fa.name = "NotoSansTC SDF";
            AssetDatabase.CreateAsset(fa, FontAssetPath);
            if (fa.atlasTextures != null && fa.atlasTextures.Length > 0 && fa.atlasTextures[0] != null)
            {
                fa.atlasTextures[0].name = "NotoSansTC SDF Atlas";
                AssetDatabase.AddObjectToAsset(fa.atlasTextures[0], fa);
            }
            fa.material.name = "NotoSansTC SDF Material";
            AssetDatabase.AddObjectToAsset(fa.material, fa);
            report.AppendLine($"建立中文字型 {FontAssetPath}(Noto Sans TC,OFL 授權可隨遊戲散佈)");
        }

        // 先把 HUD 會用到的字放進字型圖集
        string chars = string.Concat(stages.forestText, stages.goToCaveText, stages.caveText, stages.goToExitText,
            stages.finishedText, stages.blockedCaveText, stages.blockedLeaveText, stages.blockedForestText, stages.useEntranceText, stages.sideFloorText, stages.gameOverText, stages.statusFormat,
            "+毒素水分礦石能量0123456789./ ");
        fa.TryAddCharacters(chars, out string missing);
        if (!string.IsNullOrEmpty(missing)) report.AppendLine($"  字型缺字:{missing}");
        EditorUtility.SetDirty(fa);
        AssetDatabase.SaveAssets();
        return fa;
    }
}
