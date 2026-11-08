using UnityEngine;

// 天敵「繞髖部傾斜、腳不動」。掛在 HeronRoot(和 HeronThreat 同一個物件)。
//
// HeronThreat 照舊把整個 HeronRoot 繞腳底(原點)傾斜,這支腳本在它之後做兩件修正:
//   (a) 移動 HeronRoot 位置,讓髖部的世界位置和「沒傾斜時」一樣 → 看起來是繞髖部轉
//   (b) 把兩隻腳(group_3)拉回「沒傾斜時」的世界姿勢 → 腳不跟著轉
//
// 不累積誤差:
//   每幀 LateUpdate 都只用 HeronThreat 剛設好的位置/旋轉重新算,不用自己上一幀的結果。
//   下一幀的 Update(在 HeronThreat.Update 之前)先把位置還原成 HeronThreat 設的值,
//   所以 HeronThreat 讀 transform.position 時(接近、撤退)看到的永遠是它自己的位置。
//   如果這段期間有別人改過位置(例如 ResetRound),就不還原,以別人設的為準。
//
// 執行順序:DefaultExecutionOrder(-100),比 HeronThreat 和 HeronNeckAim(都是預設 0)早。
//   Update:      本腳本(還原)→ HeronThreat(移動、轉身、傾斜)
//   LateUpdate:  本腳本(修正)→ HeronNeckAim(脖子用修正後的身體姿勢)
//   所有 Update 一定在所有 LateUpdate 之前,所以修正一定在 HeronThreat 更新之後。
[DefaultExecutionOrder(-100)]
public class HeronHipLean : MonoBehaviour
{
    [Tooltip("天敵主腳本。留空會抓同一個物件上的 HeronThreat。")]
    public HeronThreat heron;

    [Tooltip("取消勾選 = 和沒有這支腳本時完全一樣(繞腳底整隻傾斜)。")]
    public bool enableLean = true;

    [Tooltip("髖部轉軸,HeronRoot 本地座標。HeronRoot 縮放是 0.01,所以 1 單位 = 1 公分。\n預設 (2, 197, 0.5):兩隻腳網格頂端的中心。")]
    public Vector3 hipLocalCm = new Vector3(2f, 197f, 0.5f);

    [Tooltip("兩隻腳的父物件。留空會在 HeronRoot/HeronModel 底下用名字 group_3 找。")]
    public Transform legs;

    // 腳在休息姿勢時,相對 HeronRoot 的位置/旋轉(啟動時記錄)
    Vector3 legRelPos;
    Quaternion legRelRot;
    Vector3 legRestLocalPos;
    Quaternion legRestLocalRot;
    bool legsMoved;

    // 位置還原用
    bool compensated;
    Vector3 threatPos;      // HeronThreat 設的位置
    Vector3 appliedPos;     // 本腳本改成的位置
    bool ready;

    /// <summary>髖部修正目前有沒有在作用(給 HeronNeckAim 的除錯預測用)。</summary>
    public bool LeanActive => ready && isActiveAndEnabled && enableLean;

    void Awake()
    {
        if (heron == null) heron = GetComponent<HeronThreat>();
        if (heron == null)
        {
            Debug.LogError("[HeronHipLean] 同一個物件上找不到 HeronThreat,不會修正傾斜。", this);
            enabled = false;
            return;
        }
        if (legs == null)
        {
            Transform model = transform.Find("HeronModel");
            if (model != null) legs = FindDeep(model, "group_3");
        }
        if (legs == null)
        {
            Debug.LogError("[HeronHipLean] 找不到 HeronRoot/HeronModel 底下的 group_3(兩隻腳),不會修正傾斜。請手動指定 Legs。", this);
            enabled = false;
            return;
        }

        legRestLocalPos = legs.localPosition;
        legRestLocalRot = legs.localRotation;
        legRelPos = transform.InverseTransformPoint(legs.position);
        legRelRot = Quaternion.Inverse(transform.rotation) * legs.rotation;
        ready = true;
    }

    void OnDisable()
    {
        RestoreRoot();
        RestoreLegs();
    }

    // 在 HeronThreat.Update 之前:把位置還原成 HeronThreat 上一幀設的值
    void Update()
    {
        RestoreRoot();
    }

    void LateUpdate()
    {
        if (!ready) return;
        if (!enableLean) { RestoreLegs(); return; }

        // 這一幀 HeronThreat 設好的姿勢
        Vector3 p = transform.position;
        Quaternion r = transform.rotation;

        // 拆出「沒傾斜時」的旋轉 r0 = 面向 × 模型修正
        // HeronThreat:r = Yaw(yaw) × 傾斜(繞 X) × Yaw(modelYawOffsetDeg)
        // 直接用 HeronThreat 的面向,傾斜超過 90° 也算得對
        Quaternion r0 = heron.UprightRotation;

        if (Quaternion.Angle(r, r0) < 0.001f) { RestoreLegs(); return; }   // 沒傾斜

        Vector3 s = transform.lossyScale;

        // (a) 髖部世界位置 = 沒傾斜時的髖部位置
        Vector3 h = Vector3.Scale(s, hipLocalCm);
        transform.position = p + r0 * h - r * h;
        threatPos = p;
        appliedPos = transform.position;
        compensated = true;

        // (b) 腳 = 沒傾斜時的世界姿勢(以 HeronThreat 設的位置 p、無傾斜旋轉 r0 為準)
        legs.SetPositionAndRotation(p + r0 * Vector3.Scale(s, legRelPos), r0 * legRelRot);
        legsMoved = true;
    }

    void RestoreRoot()
    {
        if (!compensated) return;
        compensated = false;
        // 只有位置還是本腳本設的值才還原;被別人改過(例如 ResetRound)就以別人的為準
        if ((transform.position - appliedPos).sqrMagnitude < 1e-12f) transform.position = threatPos;
    }

    void RestoreLegs()
    {
        if (!legsMoved || legs == null) return;
        legs.localPosition = legRestLocalPos;
        legs.localRotation = legRestLocalRot;
        legsMoved = false;
    }

    static Transform FindDeep(Transform t, string name)
    {
        if (t.name == name) return t;
        foreach (Transform c in t)
        {
            var r = FindDeep(c, name);
            if (r != null) return r;
        }
        return null;
    }
}
