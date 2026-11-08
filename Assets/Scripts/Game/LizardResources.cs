using System;
using UnityEngine;

public enum ResourceKind
{
    Toxin,      // 毒素:吃螞蟻
    Water,      // 水分:吃森林水珠
    OreEnergy,  // 礦石能量:咬地洞裡的礦石
}

// 蜥蜴身上的資源。場景裡放一個(GameSystems 底下),其他腳本用 LizardResources.Add(...) 加資源。
public class LizardResources : MonoBehaviour
{
    public static LizardResources Instance { get; private set; }

    [Header("目前數值(執行時看得到)")]
    public float toxin;
    public float water;
    public float oreEnergy;

    [Header("水分隨時間流失(0 = 不流失)")]
    public float waterDrainPerSecond = 0f;

    /// 獲得資源時通知(種類、數量、在哪裡吃到的)
    public event Action<ResourceKind, float, Vector3> Gained;

    void OnEnable() { Instance = this; }
    void OnDisable() { if (Instance == this) Instance = null; }

    void Update()
    {
        if (waterDrainPerSecond > 0f) water = Mathf.Max(0f, water - waterDrainPerSecond * Time.deltaTime);
    }

    public float Get(ResourceKind kind)
    {
        switch (kind)
        {
            case ResourceKind.Toxin: return toxin;
            case ResourceKind.Water: return water;
            default: return oreEnergy;
        }
    }

    public static void Add(ResourceKind kind, float amount, Vector3 where)
    {
        if (Instance == null)
        {
            Debug.LogWarning($"[LizardResources] 場景裡沒有 LizardResources,{kind} +{amount} 沒有被記錄");
            return;
        }
        switch (kind)
        {
            case ResourceKind.Toxin: Instance.toxin += amount; break;
            case ResourceKind.Water: Instance.water += amount; break;
            default: Instance.oreEnergy += amount; break;
        }
        Debug.Log($"[LizardResources] {kind} +{amount}  (毒素 {Instance.toxin} / 水分 {Instance.water} / 礦石 {Instance.oreEnergy})");
        Instance.Gained?.Invoke(kind, amount, where);
    }
}
