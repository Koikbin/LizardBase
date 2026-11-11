using UnityEngine;

// 膨脹校準:每次開遊戲做一次,結果留到關閉遊戲為止,換場景不用重做。
// 由 ExpansionInput 自動加上並呼叫,不需要手動掛。
// 在 Unity 編輯器裡按 Play 時預設沿用上次存的校準,省得每次測試都要做一遍。
public class ExpansionCalibration : MonoBehaviour
{
    [Tooltip("只影響 Unity 編輯器:勾選 = 按 Play 時沿用上次的校準。頭盔上的正式版一律每次校準。")]
    public bool reuseSavedInEditor = true;
    [Tooltip("併攏和張開的手把距離至少要差這麼多(公尺)。")]
    public float minHandRange = 0.2f;

    [Header("預設值:還沒校準時先用這組,不用校準就能膨脹。按左手 X 可以改用自己的校準。")]
    [Tooltip("勾選 = 開遊戲直接用預設值,不跳校準提示。")]
    public bool useDefaultsUntilCalibrated = true;
    [Tooltip("雙手併攏時的手把距離(公尺)")]
    public float defaultMinD = 0.10f;
    [Tooltip("雙手張開時的手把距離(公尺)")]
    public float defaultMaxD = 0.50f;
    [Tooltip("雙手併攏時的手肘外展角(度)")]
    public float defaultMinA = 30f;
    [Tooltip("雙手張開時的手肘外展角(度)")]
    public float defaultMaxA = 75f;

    // 目前用的是預設值(不是玩家自己校準的)
    public static bool UsingDefaults { get; private set; }

    public static bool IsDone { get; private set; }
    public static bool HasElbow { get; private set; }
    public static float MinD { get; private set; }
    public static float MaxD { get; private set; }
    public static float MinA { get; private set; }
    public static float MaxA { get; private set; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        IsDone = false;
        HasElbow = false;
        UsingDefaults = false;
        MinD = MaxD = MinA = MaxA = 0f;
    }

    ExpansionInput input;
    int step;                 // 0 = 等待併攏, 1 = 等待外展
    float pendingMinD;
    float pendingMinA;
    bool pendingElbowValid;
    string hint = "";         // 校準失敗的原因,字型只有英文所以用英文

    public void Init(ExpansionInput owner)
    {
        input = owner;

#if UNITY_EDITOR
        if (!IsDone && reuseSavedInEditor) LoadSaved();
#endif

        // 這個場景需要手肘,但目前的校準沒有手肘資料時要重做
        if (IsDone && input.Required && !HasElbow) IsDone = false;

        if (!IsDone && useDefaultsUntilCalibrated) ApplyDefaults();
    }

    void ApplyDefaults()
    {
        MinD = defaultMinD;
        MaxD = defaultMaxD;
        MinA = defaultMinA;
        MaxA = defaultMaxA;
        HasElbow = true;
        UsingDefaults = true;
        IsDone = true;
    }

    public void Restart()
    {
        IsDone = false;
        step = 0;
        hint = "";
    }

    public string Prompt => (step == 0 ? "Hands together" : "Hands apart") + "\nright trigger";
    public string Hint => hint;

    // 由 ExpansionInput 每幀呼叫(只有還沒校準完時)
    public void Tick(float distance, bool trigger)
    {
        if (!trigger) return;

        if (step == 0)
        {
            if (input.Required && !input.ElbowReady)
            {
                hint = "\nNo elbow tracking";
                return;
            }
            hint = "";
            pendingMinD = distance;
            pendingElbowValid = input.ElbowReady;
            if (pendingElbowValid) pendingMinA = input.elbow.Angle;
            step = 1;
            return;
        }

        if (distance - pendingMinD < minHandRange)
        {
            hint = "\nOpen hands wider";
            step = 0;
            return;
        }

        bool elbowOk = pendingElbowValid && input.ElbowReady
            && input.elbow.Angle - pendingMinA >= input.minElbowRange;

        if (input.Required && !elbowOk)
        {
            hint = input.ElbowReady ? "\nRaise elbows wider" : "\nNo elbow tracking";
            step = 0;
            return;
        }

        MinD = pendingMinD;
        MaxD = distance;
        PlayerPrefs.SetFloat("minD", MinD);
        PlayerPrefs.SetFloat("maxD", MaxD);

        if (elbowOk)
        {
            MinA = pendingMinA;
            MaxA = input.elbow.Angle;
            HasElbow = true;
            PlayerPrefs.SetFloat("elbowMinA", MinA);
            PlayerPrefs.SetFloat("elbowMaxA", MaxA);
        }

        hint = "";
        step = 0;
        UsingDefaults = false;
        IsDone = true;
    }

    void LoadSaved()
    {
        if (!PlayerPrefs.HasKey("minD") || !PlayerPrefs.HasKey("maxD")) return;

        MinD = PlayerPrefs.GetFloat("minD");
        MaxD = PlayerPrefs.GetFloat("maxD");

        if (PlayerPrefs.HasKey("elbowMinA") && PlayerPrefs.HasKey("elbowMaxA"))
        {
            MinA = PlayerPrefs.GetFloat("elbowMinA");
            MaxA = PlayerPrefs.GetFloat("elbowMaxA");
            HasElbow = true;
        }

        IsDone = true;
    }
}
