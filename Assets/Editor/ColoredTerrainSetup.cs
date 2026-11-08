using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

// 選單 Lizard > Apply Colored Terrain (上色場地):對目前開啟的場景(FullTest)
//   隊友在 ColoredTerrain/Scenes/場景1.0.unity 裡把「場景2.0」的每個物件上了材質。
//   「場景3」是同一個模型重新匯出,但物件編號(物件_N)重排過,不能用名稱對,所以:
//   1. 用幾何形狀(頂點數 + 位置大小)把場景3 的每個網格對應回場景2.0,套上隊友給那個網格的材質
//   2. 對不到的(場景3 新增或改過的物件):看它原本 FBX 的材質名稱,
//      用隊友場景裡「同一個原始材質最常被換成什麼」來上色
//   3. 天空盒、環境光、發光用的 Global Volume(Bloom)也一起複製過來
//   4. 打光:複製隊友的彩色點光源「燈光們」,平行光改成隊友的設定(見 CopyLights)
// 只改 MeshRenderer 的材質,碰撞體(_GroundCollider / _CliffCollider)不受影響。
// 重新 Build FullTest Scene 之後再執行一次即可。
public static class ColoredTerrainSetup
{
    const string SourceScene = "Assets/ColoredTerrain/Scenes/場景1.0.unity";
    const string SourceFbx = "Assets/Models/場景2.0.fbx";
    const string VolumeProfilePath = "Assets/ColoredTerrain/Scenes/場景1.0/Global Volume Profile.asset";
    const string CopiedVolumeName = "Global Volume (上色場地)";
    const string SourceTerrainRoot = "場景2.0";
    const string SourceLightGroup = "燈光們";
    const string CopiedLightGroupName = "燈光們 (上色場地)";
    const string ExtrasRootName = "隊友新增物件 (上色場地)";

    [MenuItem("Lizard/Apply Colored Terrain (上色場地)")]
    static void Apply() => Run(true);

    public static string Run(bool showDialog)
    {
        var target = SceneManager.GetActiveScene();
        var bg = target.GetRootGameObjects().FirstOrDefault(g => g.name == TerrainSetup.BackgroundName);
        string error = null;
        if (bg == null) error = $"目前場景找不到 {TerrainSetup.BackgroundName}。請先開啟 FullTest.unity。";
        else if (AssetDatabase.LoadAssetAtPath<SceneAsset>(SourceScene) == null) error = $"找不到隊友的上色場景 {SourceScene}";
        if (error != null)
        {
            if (showDialog) EditorUtility.DisplayDialog("Apply Colored Terrain", error, "OK");
            Debug.LogWarning(error);
            return error;
        }

        var report = new StringBuilder("=== Apply Colored Terrain ===\n");

        // ---- 讀隊友場景 ----
        // 注意:場景2.0 和 場景3 重新匯出時「物件_N」的編號會重排,同名不代表同一個物件
        // (實測 4215 個同名物件只有 679 個真的一樣),所以用「網格(Mesh)」當 key,
        // 再用幾何形狀(頂點數 + 位置大小)把場景3 的網格對應回場景2.0 的網格。
        var byMesh = new Dictionary<Mesh, Material[]>();                   // 場景2.0 的網格 → 隊友給的材質
        var votes = new Dictionary<string, Dictionary<Material, int>>();   // 原始材質名 → 新材質出現次數
        int duplicateMeshes = 0;
        Material skybox; AmbientMode ambientMode; Color ambientLight, ambientSky, ambientEquator, ambientGround; float ambientIntensity;
        GameObject copiedVolume = null;

        // 場景2.0 FBX:每個網格的幾何特徵 + 原本的材質
        var fbxDefaults = new Dictionary<Mesh, Material[]>();
        var fbxNode = new Dictionary<Mesh, Matrix4x4>();   // 每個網格在 FBX 裡原本的位置(地形根物件座標系)
        var geo2 = new Dictionary<int, List<(Mesh mesh, Vector3 center, Vector3 size)>>();   // 頂點數 → 候選
        var fbx = AssetDatabase.LoadAssetAtPath<GameObject>(SourceFbx);
        if (fbx != null)
            foreach (var r in fbx.GetComponentsInChildren<MeshRenderer>(true))
            {
                var mesh = r.GetComponent<MeshFilter>()?.sharedMesh;
                if (mesh == null) continue;
                fbxDefaults[mesh] = r.sharedMaterials;
                fbxNode[mesh] = fbx.transform.worldToLocalMatrix * r.transform.localToWorldMatrix;
                var g = Geometry(fbx.transform, r.transform, mesh);
                if (!geo2.TryGetValue(mesh.vertexCount, out var list)) geo2[mesh.vertexCount] = list = new List<(Mesh, Vector3, Vector3)>();
                list.Add((mesh, g.center, g.size));
            }

        var movedTo = new Dictionary<Mesh, Matrix4x4>();   // 隊友移動過的物件:在地形根物件座標系下的新位置
        int copiedExtras = 0;

        var src = EditorSceneManager.OpenScene(SourceScene, OpenSceneMode.Additive);
        try
        {
            var srcTerrain = src.GetRootGameObjects().FirstOrDefault(g => g.name == SourceTerrainRoot);
            var extras = new List<MeshRenderer>();
            foreach (var root in src.GetRootGameObjects())
            foreach (var r in root.GetComponentsInChildren<MeshRenderer>(true))
            {
                var mesh = r.GetComponent<MeshFilter>()?.sharedMesh;
                if (mesh == null) continue;
                // 隊友複製出來的 (1)(2) 物件:之後整個複製過來
                if (byMesh.ContainsKey(mesh) || Regex.IsMatch(r.gameObject.name, @" \(\d+\)$")) { duplicateMeshes++; extras.Add(r); continue; }
                var mats = r.sharedMaterials;
                byMesh[mesh] = mats;

                // 隊友移動過的物件
                if (srcTerrain != null && fbxNode.TryGetValue(mesh, out var orig0))
                {
                    var now = srcTerrain.transform.worldToLocalMatrix * r.transform.localToWorldMatrix;
                    float d = 0; for (int i = 0; i < 16; i++) d = Mathf.Max(d, Mathf.Abs(now[i] - orig0[i]));
                    if (d > 1e-3f) movedTo[mesh] = now;
                }

                if (fbxDefaults.TryGetValue(mesh, out var orig))
                    for (int i = 0; i < Mathf.Min(orig.Length, mats.Length); i++)
                    {
                        if (orig[i] == null || mats[i] == null) continue;
                        if (!votes.TryGetValue(orig[i].name, out var v)) votes[orig[i].name] = v = new Dictionary<Material, int>();
                        v[mats[i]] = v.TryGetValue(mats[i], out int c) ? c + 1 : 1;
                    }
            }

            // 隊友複製出來的裝飾物件 → 放到我們場景的「隊友新增物件」底下,位置換算到 Background_場景3
            foreach (var old in target.GetRootGameObjects().Where(g => g.name == ExtrasRootName).ToList())
                Undo.DestroyObjectImmediate(old);
            if (srcTerrain != null && extras.Count > 0)
            {
                var extrasRoot = new GameObject(ExtrasRootName);
                SceneManager.MoveGameObjectToScene(extrasRoot, target);
                extrasRoot.transform.SetPositionAndRotation(bg.transform.position, bg.transform.rotation);
                extrasRoot.transform.localScale = bg.transform.lossyScale;
                foreach (var r in extras)
                {
                    var rel = srcTerrain.transform.worldToLocalMatrix * r.transform.localToWorldMatrix;
                    var copy = new GameObject(r.gameObject.name);
                    copy.transform.SetParent(extrasRoot.transform, false);
                    copy.transform.localPosition = rel.GetColumn(3);
                    copy.transform.localRotation = rel.rotation;
                    copy.transform.localScale = rel.lossyScale;
                    copy.AddComponent<MeshFilter>().sharedMesh = r.GetComponent<MeshFilter>().sharedMesh;
                    copy.AddComponent<MeshRenderer>().sharedMaterials = r.sharedMaterials;
                    copiedExtras++;
                }
                Undo.RegisterCreatedObjectUndo(extrasRoot, "Copy Teammate Extras");
            }

            // RenderSettings 是跟著「作用中場景」的,先切過去讀
            SceneManager.SetActiveScene(src);
            skybox = RenderSettings.skybox;
            ambientMode = RenderSettings.ambientMode;
            ambientLight = RenderSettings.ambientLight;
            ambientSky = RenderSettings.ambientSkyColor;
            ambientEquator = RenderSettings.ambientEquatorColor;
            ambientGround = RenderSettings.ambientGroundColor;
            ambientIntensity = RenderSettings.ambientIntensity;
            SceneManager.SetActiveScene(target);

            // 發光用的 Global Volume(Bloom)
            var profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(VolumeProfilePath);
            var srcVolume = src.GetRootGameObjects()
                .SelectMany(g => g.GetComponentsInChildren<Volume>(true))
                .FirstOrDefault(v => v.sharedProfile == profile);
            if (srcVolume != null)
            {
                foreach (var old in target.GetRootGameObjects().Where(g => g.name == CopiedVolumeName).ToList())
                    Undo.DestroyObjectImmediate(old);
                copiedVolume = Object.Instantiate(srcVolume.gameObject);
                copiedVolume.name = CopiedVolumeName;
                copiedVolume.transform.SetParent(null);
                SceneManager.MoveGameObjectToScene(copiedVolume, target);
                Undo.RegisterCreatedObjectUndo(copiedVolume, "Copy Colored Volume");
            }

            CopyLights(src, target, bg, report);
        }
        finally
        {
            SceneManager.SetActiveScene(target);
            EditorSceneManager.CloseScene(src, true);
        }

        var fallback = votes.ToDictionary(kv => kv.Key, kv => kv.Value.OrderByDescending(x => x.Value).First().Key);

        // ---- 套到 Background_場景3 ----
        int byGeoCount = 0, byFallbackCount = 0, untouched = 0, hiddenCount = 0, movedCount = 0;
        var untouchedNames = new List<string>();
        var bgSource = PrefabUtility.GetCorrespondingObjectFromSource(bg) ?? bg;
        foreach (var r in bg.GetComponentsInChildren<MeshRenderer>(true))
        {
            Material[] newMats = null;
            // 重複執行時 r 上已經是新材質,所以從來源 prefab(FBX)拿原始資料
            var source = PrefabUtility.GetCorrespondingObjectFromSource(r) ?? r;
            var mesh3 = r.GetComponent<MeshFilter>()?.sharedMesh;
            var mesh2 = mesh3 == null ? null : FindSameGeometry(geo2, Geometry(bgSource.transform, source.transform, mesh3), mesh3.vertexCount);
            if (mesh2 != null && byMesh.TryGetValue(mesh2, out var matched))
            {
                newMats = matched;
                byGeoCount++;
            }
            else
            {
                // 用 FBX 原本的材質名找對照
                var orig = source.sharedMaterials;
                var mats = r.sharedMaterials;
                bool any = false;
                for (int i = 0; i < Mathf.Min(orig.Length, mats.Length); i++)
                    if (orig[i] != null && fallback.TryGetValue(orig[i].name, out var m)) { mats[i] = m; any = true; }
                if (any) { newMats = mats; byFallbackCount++; }
            }

            if (newMats == null)
            {
                untouched++;
                if (untouchedNames.Count < 30) untouchedNames.Add(r.gameObject.name);
                newMats = source.sharedMaterials;   // 回到 FBX 原本的材質(清掉之前執行留下的)
            }
            // 隊友在場景裡刪掉的物件 → 只關掉 MeshRenderer(看不到,但碰撞體照舊)
            bool deletedByTeammate = mesh2 != null && !byMesh.ContainsKey(mesh2);
            if (deletedByTeammate) hiddenCount++;

            Undo.RecordObject(r, "Apply Colored Terrain");
            r.sharedMaterials = newMats;
            r.enabled = !deletedByTeammate;
            PrefabUtility.RecordPrefabInstancePropertyModifications(r);

            // 隊友移動過的物件 → 搬到同樣的位置(重複執行時先回到 FBX 原位再搬)
            var tr = r.transform;
            if (mesh2 != null && movedTo.TryGetValue(mesh2, out var moved))
            {
                Undo.RecordObject(tr, "Apply Colored Terrain");
                var world = bg.transform.localToWorldMatrix * moved;
                tr.SetPositionAndRotation(world.GetColumn(3), world.rotation);
                var ps = tr.parent != null ? tr.parent.lossyScale : Vector3.one;
                var ws = world.lossyScale;
                tr.localScale = new Vector3(ws.x / ps.x, ws.y / ps.y, ws.z / ps.z);
                PrefabUtility.RecordPrefabInstancePropertyModifications(tr);
                movedCount++;
            }
            else if (tr.localPosition != source.transform.localPosition || tr.localRotation != source.transform.localRotation || tr.localScale != source.transform.localScale)
            {
                PrefabUtility.RevertObjectOverride(tr, InteractionMode.AutomatedAction);   // 回到 FBX 原位
            }
        }

        // ---- 天空盒 / 環境光 ----
        RenderSettings.skybox = skybox;
        RenderSettings.ambientMode = ambientMode;
        RenderSettings.ambientLight = ambientLight;
        RenderSettings.ambientSkyColor = ambientSky;
        RenderSettings.ambientEquatorColor = ambientEquator;
        RenderSettings.ambientGroundColor = ambientGround;
        RenderSettings.ambientIntensity = ambientIntensity;
        DynamicGI.UpdateEnvironment();

        EditorSceneManager.MarkSceneDirty(target);

        report.AppendLine($"隊友場景:{byMesh.Count} 個上色網格(重複的複製物件略過 {duplicateMeshes})");
        report.AppendLine($"依幾何形狀對應套用:{byGeoCount}");
        report.AppendLine($"隊友刪掉的物件(已隱藏,碰撞體不變):{hiddenCount}");
        report.AppendLine($"隊友移動過的物件(已搬到同位置):{movedCount}");
        report.AppendLine($"隊友複製出來的裝飾物件(已複製到 {ExtrasRootName}):{copiedExtras}");
        report.AppendLine($"依原始材質推斷套用:{byFallbackCount}");
        report.AppendLine($"沒有對應、維持原樣:{untouched}" + (untouchedNames.Count > 0 ? "  例:" + string.Join(", ", untouchedNames) : ""));
        report.AppendLine($"天空盒:{(skybox != null ? skybox.name : "無")}  環境光模式:{ambientMode}");
        report.AppendLine(copiedVolume != null ? $"已複製 {CopiedVolumeName}(Bloom)" : "⚠ 隊友場景找不到發光用的 Global Volume");
        report.AppendLine("記得存檔 (Ctrl+S)。");
        Debug.Log(report.ToString());
        if (showDialog) EditorUtility.DisplayDialog("Apply Colored Terrain", report.ToString(), "OK");
        return report.ToString();
    }

    // 網格在模型根物件座標系下的中心和大小
    static (Vector3 center, Vector3 size) Geometry(Transform root, Transform t, Mesh mesh)
    {
        var m = root.worldToLocalMatrix * t.localToWorldMatrix;
        var b = mesh.bounds;
        var s = m.lossyScale;
        return (m.MultiplyPoint3x4(b.center), Vector3.Scale(b.size, new Vector3(Mathf.Abs(s.x), Mathf.Abs(s.y), Mathf.Abs(s.z))));
    }

    // 在場景2.0 裡找頂點數相同、位置大小最接近的網格;差太多就當作沒有
    static Mesh FindSameGeometry(Dictionary<int, List<(Mesh mesh, Vector3 center, Vector3 size)>> geo2, (Vector3 center, Vector3 size) g, int vertexCount)
    {
        if (!geo2.TryGetValue(vertexCount, out var list)) return null;
        Mesh best = null; float bestD = float.MaxValue;
        foreach (var c in list)
        {
            float d = (c.center - g.center).magnitude + (c.size - g.size).magnitude;
            if (d < bestD) { bestD = d; best = c.mesh; }
        }
        float tol = 1e-4f + 1e-3f * g.size.magnitude;
        return bestD <= tol ? best : null;
    }

    // 隊友的打光:很暗的白色平行光(0.2)+「燈光們」裡 7 盞彩色點光源(藍色地表光、紫色洞穴光)。
    // 我們原本是兩盞白色平行光(強度 1 和 2),會把顏色洗成一片亮白/亮黃,所以:
    //   1. 「燈光們」整組複製過來,依隊友地形根物件 → Background_場景3 的相對位置擺放
    //   2. 我們的平行光改成隊友 Directional Light 的顏色/強度/角度,其他平行光關掉(只關 Light 元件,不刪物件)
    static void CopyLights(Scene src, Scene target, GameObject bg, StringBuilder report)
    {
        var srcRoots = src.GetRootGameObjects();
        var srcTerrain = srcRoots.FirstOrDefault(g => g.name == SourceTerrainRoot);
        var srcGroup = srcRoots.FirstOrDefault(g => g.name == SourceLightGroup);
        if (srcGroup != null)
        {
            foreach (var old in target.GetRootGameObjects().Where(g => g.name == CopiedLightGroupName).ToList())
                Undo.DestroyObjectImmediate(old);
            var group = Object.Instantiate(srcGroup);
            group.name = CopiedLightGroupName;
            SceneManager.MoveGameObjectToScene(group, target);
            // 隊友地形座標系下的擺放 → 換到我們的地形座標系
            Matrix4x4 m = srcTerrain != null
                ? bg.transform.localToWorldMatrix * srcTerrain.transform.worldToLocalMatrix * srcGroup.transform.localToWorldMatrix
                : srcGroup.transform.localToWorldMatrix;
            group.transform.SetPositionAndRotation(m.GetColumn(3), m.rotation);
            group.transform.localScale = m.lossyScale;
            Undo.RegisterCreatedObjectUndo(group, "Copy Colored Lights");
            report.AppendLine($"已複製 {CopiedLightGroupName}({group.GetComponentsInChildren<Light>(true).Length} 盞彩色點光源)");
        }
        else report.AppendLine($"⚠ 隊友場景找不到 {SourceLightGroup}");

        var srcSun = srcRoots.SelectMany(g => g.GetComponentsInChildren<Light>(true))
            .FirstOrDefault(l => l.type == LightType.Directional);
        if (srcSun == null) return;
        var ourSuns = target.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<Light>(true))
            .Where(l => l.type == LightType.Directional && l.transform.root.name != CopiedLightGroupName).ToList();
        var main = ourSuns.FirstOrDefault(l => l.name == "Directional Light") ?? ourSuns.FirstOrDefault();
        foreach (var l in ourSuns)
        {
            Undo.RecordObject(l, "Apply Colored Lights");
            Undo.RecordObject(l.transform, "Apply Colored Lights");
            if (l == main)
            {
                l.enabled = true;
                l.color = srcSun.color;
                l.intensity = srcSun.intensity;
                l.colorTemperature = srcSun.colorTemperature;
                l.useColorTemperature = srcSun.useColorTemperature;
                l.transform.rotation = bg.transform.rotation * Quaternion.Inverse(srcTerrain != null ? srcTerrain.transform.rotation : Quaternion.identity) * srcSun.transform.rotation;
            }
            else l.enabled = false;
        }
        report.AppendLine($"平行光:{main?.name} 改成強度 {srcSun.intensity}、顏色 {srcSun.color}" +
            (ourSuns.Count > 1 ? $";關掉其他平行光:{string.Join(", ", ourSuns.Where(l => l != main).Select(l => l.name))}" : ""));
    }
}
