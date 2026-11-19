using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// 一次性任務:Library/LizardPendingTask.txt 存在時,Unity 編譯完會自動執行一次,然後刪掉這個檔案。
//   1. 開啟 FullTest.unity
//   2. 背景「匯入測試」旋轉改成 X = -90°(SketchUp Z 軸朝上 → 地面完全水平)
//   3. 舌頭改用吹龍感應(TongueTrigger.useDepth = true)
//   4. 依序執行 Setup Terrain、Setup Game
// 之後想再跑一次,用選單 Lizard > Run Pending Task (Rotate -90 + Setup All)。
[InitializeOnLoad]
static class LizardPendingTask
{
    const string Marker = "Library/LizardPendingTask.txt";
    const string ScenePath = "Assets/Scenes/FullTest.unity";

    // 只重跑 Setup Game 的一次性任務
    const string GameOnlyMarker = "Library/LizardPendingGameSetup.txt";

    // 把 FullTest 的背景(匯入測試 / 場景2.0)換成目前的背景模型(FullTestSceneBuilder.BackgroundPath)的一次性任務
    const string SwapMarker = "Library/LizardPendingSwapBackground.txt";

    // 刪掉手動範圍框 + 玩家改成螞蟻兩倍大 + 套用標記 的一次性任務
    const string ResizeMarker = "Library/LizardPendingResize.txt";

    static LizardPendingTask()
    {
        if (File.Exists(ResizeMarker)) EditorApplication.delayCall += TryRunResize;
        else if (File.Exists(SwapMarker)) EditorApplication.delayCall += TryRunSwap;
        else if (File.Exists(Marker)) EditorApplication.delayCall += TryRun;
        else if (File.Exists(GameOnlyMarker)) EditorApplication.delayCall += TryRunGameOnly;
    }

    static void TryRunGameOnly()
    {
        if (!File.Exists(GameOnlyMarker)) return;
        if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating)
        {
            EditorApplication.delayCall += TryRunGameOnly;
            return;
        }
        File.Delete(GameOnlyMarker);
        // FullTest 已經開著就直接跑(上次中斷留下的未存檔變更會一起存進去);沒開才開啟
        if (UnityEngine.SceneManagement.SceneManager.GetActiveScene().path != ScenePath)
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        }
        GameSetup.SetupFromMenu();
        Debug.Log("[LizardPendingTask] Setup Game 完成。報告:Logs/GameSetupReport.txt");
    }

    static void TryRunResize()
    {
        if (!File.Exists(ResizeMarker)) return;
        if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating)
        {
            EditorApplication.delayCall += TryRunResize;
            return;
        }
        File.Delete(ResizeMarker);
        ResizeAndApply();
    }

    // 1. 刪掉 LevelMarkers(手動放的範圍框)並從 LevelMarks.json 清掉  2. 玩家改成螞蟻兩倍大  3. Setup Terrain + Setup Game
    [MenuItem("Lizard/Player Size = 2x Ant (+ Setup All)")]
    static void ResizeAndApply()
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
        var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

        foreach (var root in scene.GetRootGameObjects().Where(g => g.name == LevelMarks.MarkersRoot).ToList())
        {
            Debug.Log($"[LizardPendingTask] 刪除手動範圍框:{string.Join("、", root.GetComponentsInChildren<GameZone>(true).Select(z => z.name))}");
            Object.DestroyImmediate(root);
        }
        var data = LevelMarks.Load();
        data.zones.Clear();
        LevelMarks.Save(data);

        var player = scene.GetRootGameObjects().FirstOrDefault(g => g.GetComponent<LizardLocomotion>() != null);
        var expansion = scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<ExpansionInput>(true)).FirstOrDefault();
        var log = new System.Text.StringBuilder();
        var tongue = scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<TongueTrigger>(true)).FirstOrDefault();
        PlayerSize.Apply(player != null ? player.GetComponent<CharacterController>() : null, expansion, log, tongue);
        FullTestSceneBuilder.DisableConflictingRigs(scene, log);
        Debug.Log("[LizardPendingTask] " + log);

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);

        TerrainSetup.SetupFromMenu();   // Player 貼地、依新大小重放螞蟻(會存檔)
        GameSetup.SetupFromMenu();      // 出入口圈、森林邊界、地洞範圍…(會存檔)
        Debug.Log("[LizardPendingTask] 完成。報告:Logs/TerrainReport.txt、Logs/GameSetupReport.txt");
    }

    static void TryRunSwap()
    {
        if (!File.Exists(SwapMarker)) return;
        if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating)
        {
            EditorApplication.delayCall += TryRunSwap;
            return;
        }
        File.Delete(SwapMarker);
        SwapBackground();
    }

    // 換背景模型:保留 FullTest 裡其他東西(Player、螞蟻路線以外的設定、燈光…),
    // 舊背景的位置/旋轉/縮放照搬到新背景(兩個 FBX 的座標系相同),然後重跑 Setup Terrain + Setup Game。
    [MenuItem("Lizard/Swap Background to 場景3 (+ Setup All)")]
    static void SwapBackground()
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
        var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        var roots = scene.GetRootGameObjects();

        var old = roots.FirstOrDefault(g => TerrainSetup.LegacyBackgroundNames.Contains(g.name))
               ?? roots.FirstOrDefault(g => g.name == TerrainSetup.BackgroundName);
        if (old == null)
        {
            Debug.LogError($"[LizardPendingTask] {ScenePath} 裡找不到 {string.Join("、", TerrainSetup.LegacyBackgroundNames)} 或 {TerrainSetup.BackgroundName}");
            return;
        }
        Vector3 pos = old.transform.position, scale = old.transform.localScale;
        Quaternion rot = old.transform.rotation;
        int sibling = old.transform.GetSiblingIndex();
        string oldName = old.name;
        Object.DestroyImmediate(old);

        var bg = FullTestSceneBuilder.InstantiateBackground(scene, pos, rot, scale);
        if (bg == null) return;
        bg.transform.SetSiblingIndex(sibling);
        Debug.Log($"[LizardPendingTask] 背景 {oldName} → {bg.name}(位置 {pos}、旋轉 {rot.eulerAngles}、縮放 {scale})");

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);

        TerrainSetup.SetupFromMenu();   // 新模型的地面/峭壁碰撞體、Player 貼地、重放螞蟻(會存檔)
        GameSetup.SetupFromMenu();      // 水珠、礦石、洞穴範圍、出口、HUD(會存檔)
        Debug.Log("[LizardPendingTask] 換背景完成。報告:Logs/TerrainReport.txt、Logs/GameSetupReport.txt");
    }

    static void TryRun()
    {
        if (!File.Exists(Marker)) return;
        if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating)
        {
            EditorApplication.delayCall += TryRun;   // 等 Unity 閒下來
            return;
        }
        File.Delete(Marker);   // 先刪,出錯也不會每次編譯都重跑
        Run();
    }

    [MenuItem("Lizard/Run Pending Task (Rotate -90 + Setup All)")]
    static void Run()
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
        var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

        var bg = scene.GetRootGameObjects().FirstOrDefault(g => g.name == TerrainSetup.BackgroundName);
        if (bg == null)
        {
            Debug.LogError($"[LizardPendingTask] {ScenePath} 裡找不到 {TerrainSetup.BackgroundName}");
            return;
        }
        Vector3 e = bg.transform.eulerAngles;
        bg.transform.rotation = Quaternion.Euler(-90f, 0f, 0f);
        Debug.Log($"[LizardPendingTask] 背景旋轉 {e} → (-90, 0, 0)");

        foreach (var tongue in scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<TongueTrigger>(true)))
        {
            tongue.useDepth = true;
            EditorUtility.SetDirty(tongue);
            Debug.Log($"[LizardPendingTask] {tongue.name}:舌頭改用吹龍感應(depthInput = {(tongue.depthInput != null ? tongue.depthInput.name : "未指定!")})");
        }

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);

        TerrainSetup.SetupFromMenu();   // 依新的旋轉重新分出地面/峭壁、Player 貼地、重放螞蟻(會存檔)
        GameSetup.SetupFromMenu();      // 水珠、礦石、洞穴範圍、出口、HUD(會存檔)
        Debug.Log("[LizardPendingTask] 完成。報告:Logs/TerrainReport.txt、Logs/GameSetupReport.txt");
    }
}
