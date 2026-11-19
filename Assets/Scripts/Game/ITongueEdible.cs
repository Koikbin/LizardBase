using UnityEngine;

// 舌頭可以黏住、縮回後吃掉的東西(螞蟻、水珠、礦石)。TongueTrigger 只透過這個介面操作。
public interface ITongueEdible
{
    /// 現在能不能被黏住(已被吃掉、正被黏著、還在冷卻 → false)
    bool CanBeCaught { get; }

    /// 舌尖在 tipPosition 時的抓取值:小於 1 才抓得到,越小越優先
    float CatchValue(TongueTrigger tongue, Vector3 tipPosition);

    /// 被舌尖黏住
    void OnCaught(Transform tip);

    /// 舌頭縮回,吃下去(在這裡加資源)
    void OnEaten();

    string DisplayName { get; }
}
