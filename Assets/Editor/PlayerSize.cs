using System.Text;
using UnityEditor;
using UnityEngine;

// 玩家大小:身長 = 螞蟻的兩倍(TerrainSetup.AntLength x 2 = 0.3 公尺)。
// 原本的玩家是 1.2 公尺高的膠囊、OVRCameraRig 縮放 1;現在全部乘上 Scale(= 0.3 / 1.2 = 0.25):
//   - CharacterController 的高度、半徑、台階高度
//   - OVRCameraRig 的基本縮放(ExpansionInput.baseScale):眼睛高度、手、舌頭都跟著變小,世界看起來大 4 倍
//   膨脹時是在這個大小上再乘 1~2 倍。
public static class PlayerSize
{
    public const float ReferenceHeight = 1.2f;                         // 原本的膠囊高度(rig 縮放 1 時)
    public static float BodyLength => TerrainSetup.AntLength * 2f;     // 螞蟻的兩倍
    public static float Scale => BodyLength / ReferenceHeight;
    public const float TongueLength = 1f;                              // 舌頭最長(公尺,世界座標;膨脹時再放大)
    public const float NearClipReal = 0.08f;                           // 相機近裁切面(真實公尺,乘上縮放後 = 0.02)

    public static void Apply(CharacterController cc, ExpansionInput expansion, StringBuilder report, TongueTrigger tongue = null)
    {
        float s = Scale;
        if (tongue != null)
        {
            tongue.maxLength = TongueLength / s;             // 舌頭在縮小的相機底下,本地長度要除回去
            EditorUtility.SetDirty(tongue);
        }
        if (cc != null)
        {
            cc.height = ReferenceHeight * s;                 // 0.30
            cc.radius = 0.25f * s;                           // 0.0625
            cc.skinWidth = Mathf.Max(0.005f, 0.03f * s);     // 0.0075
            cc.center = new Vector3(0f, cc.height * 0.5f + cc.skinWidth, 0f);
            cc.stepOffset = 0.3f * s;                        // 0.075
            // 跟 TerrainSetup 的峭壁門檻一致:比這個緩的都是「地面」,要能走上去;
            // 比這個陡的是可以用 Grip 攀爬的峭壁。原本 50 度會讓 50~60 度的坡兩邊都不算,卡住玩家
            cc.slopeLimit = TerrainSetup.ClimbAngleDeg;
            cc.minMoveDistance = 0f;
            EditorUtility.SetDirty(cc);
        }
        if (expansion != null && expansion.cam != null)
        {
            // 近裁切面是世界單位,不會跟著 rig 縮放;0.1 在縮小 0.25 倍時等於眼前 40 公分都看不到,
            // 低頭爬行時地面會被切掉、看穿到地下。改成真實 8 公分
            expansion.cam.nearClipPlane = NearClipReal * s;
            EditorUtility.SetDirty(expansion.cam);
        }
        if (expansion != null)
        {
            expansion.baseScale = s;
            if (expansion.rig != null)
            {
                expansion.rig.localScale = Vector3.one * s;  // 編輯器裡也先縮好,Scene 視窗看得到
                EditorUtility.SetDirty(expansion.rig);
            }
            EditorUtility.SetDirty(expansion);
        }
        report?.AppendLine($"玩家大小:身長 {BodyLength:F2} m(螞蟻 {TerrainSetup.AntLength} m 的兩倍),縮放 x{s:F2}:" +
            $"膠囊高 {ReferenceHeight * s:F2} m、半徑 {0.25f * s:F3} m、台階 {0.3f * s:F3} m;" +
            (tongue != null ? $"舌頭最長 {TongueLength} m(Max Length {tongue.maxLength:F1})" : "⚠ 找不到 TongueTrigger,舌頭長度沒改") + "(膨脹時再放大)");
    }
}
