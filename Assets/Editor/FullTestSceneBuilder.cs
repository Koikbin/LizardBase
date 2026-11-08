using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

// 選單 Lizard > Build FullTest Scene:從 main.unity 重新建立 FullTest.unity。
//   1. 移除舊環境(地形1、場景、水晶、蒼鷺、測試方塊)、噴血(SprayInput)、舊螞蟻
//   2. 放入「場景3.fbx」當背景(放大 100 倍,擺放用使用者調好的位置/旋轉)
//   3. 新增 Player(CharacterController + LizardLocomotion),把 OVRCameraRig 放到底下
//      → 爬行移動 Player,膨脹縮放 OVRCameraRig,兩者不會打架
//   4. 啟用膨脹(ExpansionInput),舌頭在校準時不讀扳機
//   5. 交給 TerrainSetup:依坡度產生地面/峭壁碰撞體、Player 貼地、放螞蟻
//   6. Build Settings 把 FullTest 放第一個
// 只想重新分析地形或重放螞蟻(不想重建整個場景),用 Lizard > Setup Terrain (Cliffs + Ants)。
public static class FullTestSceneBuilder
{
    const string SourceScene = "Assets/Scenes/main.unity";
    const string TargetScene = "Assets/Scenes/FullTest.unity";
    public const string BackgroundPath = "Assets/Models/場景3.fbx";
    const string ClimbableTag = "Climbable";

    // ---- 背景擺放(2026-11-06 使用者在 FullTest 手動調整的值;SketchUp 匯出是 Z 軸朝上,所以要繞 X 轉) ----
    const float BackgroundScale = 100f;
    static readonly Vector3 BackgroundPosition = new Vector3(2.65f, 18.29f, -7.42f);
    static readonly Vector3 BackgroundEuler = new Vector3(-90f, 0f, 0f);   // -90 時模型裡的底板完全水平
    // Player 出生點的水平位置(高度會自動貼地)
    static readonly Vector2 PlayerSpawnXZ = new Vector2(-4.276f, -0.9f);

    const int IgnoreRaycastLayer = 2;
    const int UILayer = 5;

    static readonly string[] RemoveRootNames = { "地形1", "場景", "HeronRoot", "Plane", "Cube", "Sphere", "AntPath_1" };
    static readonly string[] RemoveRootPrefixes = { "CristalRoot", "AntRoot" };

    [MenuItem("Lizard/Build FullTest Scene")]
    public static void Build()
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

        if (AssetDatabase.LoadAssetAtPath<SceneAsset>(TargetScene) != null &&
            !EditorUtility.DisplayDialog("Build FullTest Scene",
                "FullTest.unity 已經存在。\n要從 main.unity 重新建立並覆蓋嗎?(你在 FullTest 裡手動做的修改會消失)", "覆蓋", "取消"))
            return;

        var log = new StringBuilder("[FullTestBuilder] 完成\n");
        try
        {
            EditorUtility.DisplayProgressBar("Build FullTest Scene", "準備場景", 0.05f);
            EnsureTag(ClimbableTag);

            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(TargetScene) != null) AssetDatabase.DeleteAsset(TargetScene);
            if (!AssetDatabase.CopyAsset(SourceScene, TargetScene))
            {
                Debug.LogError($"[FullTestBuilder] 無法複製 {SourceScene}");
                return;
            }
            Scene scene = EditorSceneManager.OpenScene(TargetScene, OpenSceneMode.Single);

            // ---- 先抓住需要的元件(刪東西之前) ----
            var expansion = FindInScene<ExpansionInput>(scene);
            var tongue = FindInScene<TongueTrigger>(scene);
            var debugInput = FindInScene<DebugInput>(scene);
            if (expansion == null || expansion.rig == null || expansion.head == null)
            {
                Debug.LogError("[FullTestBuilder] 在 main.unity 找不到 ExpansionInput 或它的 Rig/Head 欄位,無法定位 OVRCameraRig。");
                return;
            }
            Transform rig = expansion.rig;
            Transform head = expansion.head;
            Transform trackingSpace = FindDeep(rig, "TrackingSpace") ?? head.parent;

            // ---- 1. 移除不需要的東西 ----
            foreach (var root in scene.GetRootGameObjects())
            {
                if (RemoveRootNames.Contains(root.name) || RemoveRootPrefixes.Any(p => root.name.StartsWith(p)))
                {
                    log.AppendLine($"  移除 {root.name}");
                    Object.DestroyImmediate(root);
                }
            }
            foreach (var spray in FindAllInScene<SprayInput>(scene))
            {
                if (spray.sprayText != null) Object.DestroyImmediate(spray.sprayText.gameObject);
                log.AppendLine($"  移除噴血 {spray.gameObject.name}");
                Object.DestroyImmediate(spray.gameObject);
            }

            // ---- 2. 背景 ----
            EditorUtility.DisplayProgressBar("Build FullTest Scene", "放入背景", 0.15f);
            var bg = InstantiateBackground(scene, BackgroundPosition, Quaternion.Euler(BackgroundEuler), Vector3.one * BackgroundScale);
            if (bg == null) return;

            // 依坡度產生地面/峭壁碰撞體
            if (!TerrainSetup.ClassifyTerrain(bg, log)) return;

            // ---- 3. Player = CharacterController + LizardLocomotion,OVRCameraRig 放底下 ----
            EditorUtility.DisplayProgressBar("Build FullTest Scene", "建立 Player 與爬行", 0.85f);
            var player = new GameObject("Player");
            SceneManager.MoveGameObjectToScene(player, scene);
            player.layer = IgnoreRaycastLayer;   // 螞蟻貼地的射線不要打到玩家
            float top = TerrainSetup.RendererBounds(bg).max.y + 1f;
            player.transform.SetPositionAndRotation(new Vector3(PlayerSpawnXZ.x, top, PlayerSpawnXZ.y), Quaternion.Euler(0f, rig.eulerAngles.y, 0f));
            rig.SetParent(player.transform, false);
            rig.localPosition = Vector3.zero;
            rig.localRotation = Quaternion.identity;
            rig.localScale = Vector3.one;

            var cc = player.AddComponent<CharacterController>();
            PlayerSize.Apply(cc, expansion, log, tongue);

            var loco = player.AddComponent<LizardLocomotion>();
            var so = new SerializedObject(loco);
            so.FindProperty("characterController").objectReferenceValue = cc;
            so.FindProperty("trackingSpace").objectReferenceValue = trackingSpace;
            so.FindProperty("leftHandAnchor").objectReferenceValue = expansion.leftHand;
            so.FindProperty("rightHandAnchor").objectReferenceValue = expansion.rightHand;
            so.FindProperty("cameraTransform").objectReferenceValue = head;
            so.FindProperty("surfaceMask").intValue = ~((1 << IgnoreRaycastLayer) | (1 << UILayer));
            so.ApplyModifiedPropertiesWithoutUndo();

            // 從背景最上方往下找地面(出生點在高處平台上時才不會掉到下層)
            if (!TerrainSetup.PlaceAtSpawn(player.transform, bg, log))
                TerrainSetup.SnapPlayerToGround(player.transform, log, fromTop: true);

            // ---- 4. 膨脹、舌頭、除錯輸入 ----
            expansion.gameObject.SetActive(true);
            if (expansion.display != null) expansion.display.gameObject.SetActive(true);
            if (expansion.displayRoot != null) expansion.displayRoot.gameObject.SetActive(true);
            EditorUtility.SetDirty(expansion);

            if (tongue != null)
            {
                tongue.expansion = expansion;
                tongue.useDepth = true;   // 舌頭用吹龍感應(BlowoutDepthProbe),不用把手扳機
                EditorUtility.SetDirty(tongue);
            }
            else log.AppendLine("  ⚠ 找不到 TongueTrigger");

            if (debugInput != null)
            {
                debugInput.useKeyboard = true;              // 有頭顯時會自動停用
                debugInput.disableWhenHeadsetActive = true;
                debugInput.simulateHands = true;
                debugInput.moveRoot = player.transform;
                debugInput.pitchRoot = trackingSpace;
                EditorUtility.SetDirty(debugInput);
            }

            DisableConflictingRigs(scene, log);

            // ---- 5. 螞蟻 ----
            TerrainSetup.PlaceAnts(scene, player.transform, log);

            // ---- 6. 遊戲機制:水珠、礦石、關卡流程、HUD ----
            GameSetup.Setup(scene, log);

            // ---- 燈光 ----
            foreach (var root in scene.GetRootGameObjects())
                if (root.name == "Directional Light") root.SetActive(true);

            // ---- 存檔 + Build Settings ----
            EditorUtility.DisplayProgressBar("Build FullTest Scene", "存檔", 0.95f);
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            SetBuildScenes();

            Selection.activeGameObject = player;
            SceneView.lastActiveSceneView?.FrameSelected();
            log.AppendLine("Build Settings:FullTest 放第一個並啟用,其他場景保留但取消勾選");
        }
        finally
        {
            EditorUtility.ClearProgressBar();
            Debug.Log(log.ToString());
        }
    }

    // ---------------------------------------------------------------

    // 放入背景模型(BackgroundPath),也給 LizardPendingTask 換背景用
    public static GameObject InstantiateBackground(Scene scene, Vector3 position, Quaternion rotation, Vector3 scale)
    {
        var bgAsset = AssetDatabase.LoadAssetAtPath<GameObject>(BackgroundPath);
        if (bgAsset == null)
        {
            Debug.LogError($"[FullTestBuilder] 找不到 {BackgroundPath}");
            return null;
        }
        var bg = (GameObject)PrefabUtility.InstantiatePrefab(bgAsset, scene);
        bg.name = TerrainSetup.BackgroundName;
        bg.transform.SetPositionAndRotation(position, rotation);
        bg.transform.localScale = scale;
        foreach (var mf in bg.GetComponentsInChildren<MeshFilter>(true))
            GameObjectUtility.SetStaticEditorFlags(mf.gameObject, StaticEditorFlags.BatchingStatic);
        foreach (var cam in bg.GetComponentsInChildren<Camera>(true)) cam.gameObject.SetActive(false); // SketchUp 匯出的相機
        return bg;
    }

    // main.unity 裡 Meta 的 Interaction Rig 有自己的搖桿移動/轉向,會把 OVRCameraRig 移走,
    // 跟 LizardLocomotion(雙手交替爬行)打架;瞬移的 NavMesh 也用不到。只關掉,不刪掉(要恢復把它打勾就好)。
    public static void DisableConflictingRigs(Scene scene, StringBuilder log)
    {
        foreach (var root in scene.GetRootGameObjects())
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                string n = t.name;
                bool conflict = n.Contains("OVRComprehensiveInteractionRig") || n.Contains("InteractionRig")
                             || (t.parent == null && n == "Teleport NavMesh");
                if (!conflict || !t.gameObject.activeSelf) continue;
                t.gameObject.SetActive(false);
                EditorUtility.SetDirty(t.gameObject);
                log?.AppendLine($"  關掉 {n}(搖桿移動/瞬移,會跟雙手爬行打架)");
            }
    }

    static T FindInScene<T>(Scene scene) where T : Component => FindAllInScene<T>(scene).FirstOrDefault();

    static IEnumerable<T> FindAllInScene<T>(Scene scene) where T : Component =>
        scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<T>(true)).ToList();

    static Transform FindDeep(Transform root, string name)
    {
        if (root.name == name) return root;
        foreach (Transform c in root)
        {
            var f = FindDeep(c, name);
            if (f != null) return f;
        }
        return null;
    }

    static void EnsureTag(string tag)
    {
        var tagManager = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")[0]);
        var tags = tagManager.FindProperty("tags");
        for (int i = 0; i < tags.arraySize; i++)
            if (tags.GetArrayElementAtIndex(i).stringValue == tag) return;
        tags.arraySize++;
        tags.GetArrayElementAtIndex(tags.arraySize - 1).stringValue = tag;
        tagManager.ApplyModifiedPropertiesWithoutUndo();
        AssetDatabase.SaveAssets();
        Debug.Log($"[FullTestBuilder] 新增 Tag:{tag}");
    }

    static void SetBuildScenes()
    {
        var list = EditorBuildSettings.scenes
            .Where(s => s.path != TargetScene)
            .Select(s => new EditorBuildSettingsScene(s.path, false))
            .ToList();
        list.Insert(0, new EditorBuildSettingsScene(TargetScene, true));
        EditorBuildSettings.scenes = list.ToArray();
    }
}
