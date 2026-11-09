using TMPro;
using UnityEngine;
using UnityEngine.Events;

// 關卡流程:
//   Forest   第一關森林:吃葉子上的水珠,水分達到 Water Goal 前不能進洞穴(碰到洞穴範圍會被推回來)
//            有森林範圍框時,也不能走出 森林範圍 + 地洞出入口
//   GoToCave 水分足夠,前往洞穴(洞穴標記亮起)
//   Cave     第二關地洞:咬礦石,礦石能量達到 Ore Goal 前不能離開洞穴
//   GoToExit 礦石收集完成,出口標記亮起
//   Finished 走進出口範圍 → 過關(觸發 On Finished,可以接下一個場景)
//   GameOver 從石頭階梯掉進深淵 → 繼續往下掉、遊戲結束(右手 A / 鍵盤 R 重新開始)
// 地洞裡只有出入口前面和礦石區的洞底可以走,坑兩側的洞底踩上去會被推回來(一定要跳石頭)。
//            沒有出口範圍時:從地洞出入口爬出去(不在洞裡、也不在出入口裡)就算過關
// 有地洞出入口框時,洞裡和洞外只能經過出入口來往:從地洞頂/地洞底交界的縫隙鑽進鑽出會被推回來。
// 螞蟻(毒素)隨時都可以吃,會顯示在 HUD 上。
public class StageManager : MonoBehaviour
{
    public enum Stage { Forest, GoToCave, Cave, GoToExit, Finished, GameOver }

    public static StageManager Instance { get; private set; }

    [Header("參考")]
    public Transform player;          // Player(有 CharacterController)
    public GameZone caveZone;         // 洞穴範圍(方塊);有 Cave Volume 時不用
    public CaveVolume caveVolume;     // 洞穴範圍(地洞頂/地洞底標記算出來的精準空間),優先使用
    public GameZone[] forestZones;    // 森林範圍框
    public ForestBoundary forestBoundary;  // 森林邊界(標記的葉子連成的圈);框和邊界都沒有 = 不限制森林範圍
    public ForestBoundary pathBoundary;    // 通道(太空艙 → 森林);第一關在這裡也可以活動
    public GameZone[] entranceZones;  // 地洞出入口框:森林和洞穴之間的通道,兩個階段都能待
    public GameZone abyssZone;        // 石頭階梯下面的深淵:掉進去 = 遊戲結束
    [Tooltip("掉進深淵後,繼續往下掉幾秒(不會撞到坑底)")]
    public float abyssFallSeconds = 2.5f;
    public GameZone exitZone;         // 出口範圍;沒指定 = 離開洞穴範圍就算出去
    public TMP_Text hud;
    [Tooltip("在視野裡顯示關卡文字和數值。關掉時只有遊戲結束會顯示文字")]
    public bool showHud = false;
    public GameObject caveBeacon;     // GoToCave 時顯示
    public GameObject exitBeacon;     // GoToExit 時顯示

    [Header("目標")]
    [Tooltip("打勾 = 水分要達到 Water Goal 才能進地洞(正式玩法)。測試時可以取消")]
    public bool requireWaterForCave = true;
    public float waterGoal = 10f;
    [Tooltip("0 = 場景裡所有礦石的總量(全部咬完)")]
    public float oreGoal = 0f;

    [Header("文字({0} = 目前數量, {1} = 目標)")]
    [TextArea] public string forestText = "第一關:森林\n用舌頭吃葉子上的水珠\n水分 {0} / {1}";
    [TextArea] public string goToCaveText = "水分足夠了!\n前往場景中央的洞穴";
    [TextArea] public string caveText = "第二關:地洞\n用舌頭咬下礦石\n礦石能量 {0} / {1}";
    [TextArea] public string goToExitText = "礦石收集完成!\n從出口離開洞穴";
    [TextArea] public string finishedText = "成功離開地洞!";
    public string blockedCaveText = "水分不夠,還不能進入洞穴";
    public string blockedLeaveText = "礦石還沒收集完,不能離開";
    public string blockedForestText = "超出森林範圍了";
    public string useEntranceText = "要從地洞出入口進出";
    public string sideFloorText = "這裡過不去,要跳石頭過去";
    [TextArea] public string gameOverText = "掉進深淵了……\n遊戲結束\n按右手 A 重新開始";
    public string statusFormat = "毒素 {0}   水分 {1}   礦石 {2}";

    [Header("事件")]
    public UnityEvent onForestComplete;
    public UnityEvent onEnterCave;
    public UnityEvent onCaveComplete;
    public UnityEvent onFinished;
    public UnityEvent onGameOver;

    public Stage CurrentStage => stage;
    public float OreGoal => oreGoalResolved;

    Stage stage;
    float oreGoalResolved;
    CharacterController controller;
    Vector3 lastSafe;
    bool hasSafe;
    enum Region { Outside, Entrance, Cave }
    Region lastRegion = Region.Outside;   // 上一個合法位置在哪一區
    string flash;
    float flashUntil;

    void OnEnable() { Instance = this; }
    void OnDisable() { if (Instance == this) Instance = null; }

    void Start()
    {
        if (player != null) controller = player.GetComponent<CharacterController>();

        oreGoalResolved = oreGoal;
        if (oreGoalResolved <= 0f)
        {
            foreach (var f in TongueFood.All)
                if (f.kind == ResourceKind.OreEnergy) oreGoalResolved += f.TotalAmount;
            if (oreGoalResolved <= 0f) Debug.LogWarning("[StageManager] 場景裡沒有礦石(TongueFood Kind = OreEnergy)");
        }

        if (LizardResources.Instance != null) LizardResources.Instance.Gained += OnGained;
        SetStage(Stage.Forest);
    }

    void OnDestroy()
    {
        if (LizardResources.Instance != null) LizardResources.Instance.Gained -= OnGained;
    }

    void Update()
    {
        if (player == null) return;

        if (stage == Stage.GameOver) { UpdateGameOver(); return; }

        if (stage == Stage.Forest && !requireWaterForCave) SetStage(Stage.GoToCave);
        StopBuzzIfDone();

        Vector3 pos = player.position;
        if ((caveVolume != null && caveVolume.InAbyss(pos)) || (abyssZone != null && abyssZone.Contains(pos))) { StartGameOver(); return; }
        if ((stage == Stage.Cave || stage == Stage.GoToExit) && caveVolume != null && caveVolume.OnForbiddenFloor(pos))
        {
            PushBack(sideFloorText);
            UpdateHud(LizardResources.Instance, LizardResources.Instance != null ? LizardResources.Instance.water : 0f,
                      LizardResources.Instance != null ? LizardResources.Instance.oreEnergy : 0f);
            return;
        }
        bool inCave = InCave(pos);
        bool inEntrance = InAny(entranceZones, pos);
        bool hasForestLimit = (forestZones != null && forestZones.Length > 0) || forestBoundary != null;
        bool inForest = !hasForestLimit || InAny(forestZones, pos) || (forestBoundary != null && forestBoundary.Contains(pos))
                        || (pathBoundary != null && pathBoundary.Contains(pos));
        bool hasEntrances = entranceZones != null && entranceZones.Length > 0;
        Region region = inCave ? Region.Cave : inEntrance ? Region.Entrance : Region.Outside;
        // 洞裡 ↔ 洞外 直接切換(沒經過出入口)= 從縫隙鑽過去
        bool skippedEntrance = hasEntrances && hasSafe &&
            ((region == Region.Cave && lastRegion == Region.Outside) || (region == Region.Outside && lastRegion == Region.Cave));
        var res = LizardResources.Instance;
        float water = res != null ? res.water : 0f;
        float ore = res != null ? res.oreEnergy : 0f;

        switch (stage)
        {
            case Stage.Forest:
            case Stage.GoToCave:
                if (inCave)
                {
                    if (stage == Stage.Forest) PushBack(blockedCaveText);
                    else if (skippedEntrance) PushBack(useEntranceText);
                    else { SetStage(Stage.Cave); Remember(pos, region); }
                }
                else if (!inForest && !inEntrance) PushBack(blockedForestText);
                else Remember(pos, region);
                if (stage == Stage.Forest && water >= waterGoal) SetStage(Stage.GoToCave);
                break;

            case Stage.Cave:
                if (!inCave && !inEntrance) PushBack(skippedEntrance ? useEntranceText : blockedLeaveText);
                else Remember(pos, region);
                if (ore >= oreGoalResolved && oreGoalResolved > 0f) SetStage(Stage.GoToExit);
                break;

            case Stage.GoToExit:
                if (exitZone != null)
                {
                    if (exitZone.Contains(pos)) SetStage(Stage.Finished);
                }
                else if (region == Region.Outside)
                {
                    if (skippedEntrance) PushBack(useEntranceText);
                    else SetStage(Stage.Finished);
                }
                else Remember(pos, region);
                break;
        }

        UpdateHud(res, water, ore);
    }

    void SetStage(Stage s)
    {
        stage = s;
        Debug.Log($"[StageManager] 進入階段 {s}");
        if (caveBeacon != null) caveBeacon.SetActive(s == Stage.GoToCave);
        if (exitBeacon != null) exitBeacon.SetActive(s == Stage.GoToExit);
        switch (s)
        {
            case Stage.GoToCave: onForestComplete?.Invoke(); break;
            case Stage.Cave: onEnterCave?.Invoke(); break;
            case Stage.GoToExit: onCaveComplete?.Invoke(); break;
            case Stage.Finished: onFinished?.Invoke(); break;
            case Stage.GameOver: onGameOver?.Invoke(); break;
        }
    }

    float fallSpeed, fallUntil;

    // 掉進深淵:停掉爬行和碰撞,讓玩家繼續往下掉(不會撞到坑底)
    void StartGameOver()
    {
        var loco = player.GetComponent<LizardLocomotion>();
        if (loco != null) loco.enabled = false;
        if (controller != null) controller.enabled = false;
        fallSpeed = 0f;
        fallUntil = Time.time + abyssFallSeconds;
        SetStage(Stage.GameOver);
    }

    void UpdateGameOver()
    {
        if (Time.time < fallUntil)
        {
            fallSpeed += 9.8f * Time.deltaTime;
            player.position += Vector3.down * fallSpeed * Time.deltaTime;
        }
        if (hud != null) hud.text = gameOverText;
        bool restart = OVRInput.GetDown(OVRInput.Button.One, OVRInput.Controller.RTouch) || DebugInput.RestartDown;
        if (restart)
            UnityEngine.SceneManagement.SceneManager.LoadScene(UnityEngine.SceneManagement.SceneManager.GetActiveScene().buildIndex);
    }

    void Remember(Vector3 pos, Region region)
    {
        lastSafe = pos;
        hasSafe = true;
        lastRegion = region;
    }

    public bool InCave(Vector3 pos)
    {
        if (caveVolume != null) return caveVolume.Contains(pos);
        return caveZone != null && caveZone.Contains(pos);
    }

    static bool InAny(GameZone[] zones, Vector3 pos)
    {
        if (zones == null) return false;
        foreach (var z in zones) if (z != null && z.Contains(pos)) return true;
        return false;
    }

    // 把玩家放回上一個合法位置,再往遠離越界方向推一點,避免卡在邊界反覆觸發
    void PushBack(string message)
    {
        if (!hasSafe) return;   // 遊戲剛開始還沒進過合法範圍(例如出生點在森林外):先讓玩家自己走進去,不跳警告
        Flash(message, 2.5f);
        Buzz();   // 文字關掉時,至少讓玩家知道被擋住了

        Vector3 dir = lastSafe - player.position;
        dir.y = 0f;
        dir = dir.sqrMagnitude > 0.0001f ? dir.normalized : Vector3.zero;
        Vector3 target = lastSafe + dir * 0.1f;

        if (controller != null) controller.enabled = false;
        player.position = target;
        if (controller != null) controller.enabled = true;
        Physics.SyncTransforms();
    }

    float buzzUntil = -1f;

    void Buzz()
    {
        if (Time.time < buzzUntil - 0.1f) return;   // 一直被推回時不要一直重新開始震
        OVRInput.SetControllerVibration(0.3f, 0.6f, OVRInput.Controller.LTouch);
        OVRInput.SetControllerVibration(0.3f, 0.6f, OVRInput.Controller.RTouch);
        buzzUntil = Time.time + 0.15f;
    }

    void StopBuzzIfDone()
    {
        if (buzzUntil > 0f && Time.time > buzzUntil)
        {
            OVRInput.SetControllerVibration(0f, 0f, OVRInput.Controller.LTouch);
            OVRInput.SetControllerVibration(0f, 0f, OVRInput.Controller.RTouch);
            buzzUntil = -1f;
        }
    }

    void OnGained(ResourceKind kind, float amount, Vector3 where)
    {
        string label = kind == ResourceKind.Toxin ? "毒素" : kind == ResourceKind.Water ? "水分" : "礦石能量";
        Flash($"+{amount:0.#} {label}", 1.2f);
    }

    void Flash(string text, float seconds)
    {
        flash = text;
        flashUntil = Time.time + seconds;
    }

    void UpdateHud(LizardResources res, float water, float ore)
    {
        if (hud == null) return;
        if (!showHud) { hud.text = ""; return; }

        string objective;
        switch (stage)
        {
            case Stage.Forest: objective = string.Format(forestText, water.ToString("0"), waterGoal.ToString("0")); break;
            case Stage.GoToCave: objective = goToCaveText; break;
            case Stage.Cave: objective = string.Format(caveText, ore.ToString("0"), oreGoalResolved.ToString("0")); break;
            case Stage.GoToExit: objective = goToExitText; break;
            default: objective = finishedText; break;
        }

        float toxin = res != null ? res.toxin : 0f;
        string status = string.Format(statusFormat, toxin.ToString("0"), water.ToString("0"), ore.ToString("0"));
        string extra = Time.time < flashUntil ? $"\n<color=#FFE066>{flash}</color>" : "";
        hud.text = $"{objective}\n<size=70%>{status}</size>{extra}";
    }
}
