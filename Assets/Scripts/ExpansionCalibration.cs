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
