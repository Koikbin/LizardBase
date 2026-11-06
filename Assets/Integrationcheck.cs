using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

// 每秒在 Console 印一行整體狀態,貼給我就能看出哪裡沒接上。
// 掛在 Systems 底下新建的空物件。
public class IntegrationCheck : MonoBehaviour
{
    public TongueTrigger tongue;
    public BlowoutDepthProbe probe;
    public float interval = 1f;

    float next;

    void Update()
    {
        if (Time.time < next) return;
        next = Time.time + interval;

        var sb = new StringBuilder("[Check] ");

        sb.Append("scenes=");
        for (int i = 0; i < SceneManager.sceneCount; i++)
            sb.Append(SceneManager.GetSceneAt(i).name).Append(i < SceneManager.sceneCount - 1 ? "," : "");

        sb.Append(" | debugKeys=").Append(DebugInput.Active);

        if (probe != null)
            sb.Append(" | depth: tip=").Append(probe.TipDistance.ToString("F2"))
              .Append(" hits=").Append(probe.NearHitCount)
              .Append(" L01=").Append(probe.Length01.ToString("F2"));
        else sb.Append(" | probe=NULL");

        if (tongue != null)
            sb.Append(" | tongue: raw=").Append(tongue.RawInput.ToString("F2"))
              .Append(" ext=").Append(tongue.Extension.ToString("F2"))
              .Append(" caught=").Append(tongue.Caught != null ? tongue.Caught.name : "-")
              .Append(" strike=").Append(tongue.StrikeWindowOpen ? "open" : "closed");
        else sb.Append(" | tongue=NULL");

        sb.Append(" | ants=").Append(AntWander.All.Count)
          .Append(" eaten=").Append(AntWander.EatenCount);

        Debug.Log(sb.ToString());
    }
}