using TMPro;
using UnityEngine;

// 沒擋下鷺鷥這一口時,在玩家眼前顯示失敗文字。掛在 HeronRoot(和 HeronThreat 同一個物件)。
// 只要 HeronThreat.IsCaught 為真就顯示,ResetRound / BeginApproach 之後自動消失。
// 文字用 Overlay shader 畫在最上層,鳥嘴貼到臉前也不會被擋住。
public class HeronDeathScreen : MonoBehaviour
{
    [Tooltip("天敵主腳本。留空會抓同一個物件上的 HeronThreat。")]
    public HeronThreat heron;
    [Tooltip("文字要跟著的頭。留空用主攝影機。")]
    public Transform head;
    [Tooltip("畫在最上層用的 shader。要在 Inspector 指定,打包時才會被包含。")]
    public Shader overlayShader;

    public string message = "You are dead, mission failed.";
    [Tooltip("被抓之後等幾秒才顯示,先讓玩家看到被咬。")]
    public float showDelay = 0.35f;
    [Tooltip("離眼睛多遠(公尺)。")]
    public float distance = 1.2f;
    [Tooltip("文字大小。1 大約是 10 公分高。")]
    public float fontSize = 1f;
    [Tooltip("文字區塊寬度(公尺),超過會自動換行。")]
    public float width = 1f;
    public Color textColor = new Color(1f, 0.15f, 0.15f, 1f);
    [Tooltip("文字後面的底色,透明度 0 = 不要底色。")]
    public Color backgroundColor = new Color(0f, 0f, 0f, 0.75f);

    TextMeshPro text;
    Material overlayMaterial;
    float caughtSince = -1f;

    void Awake()
    {
        if (heron == null) heron = GetComponent<HeronThreat>();
        if (heron == null)
        {
            Debug.LogError("[HeronDeathScreen] 同一個物件上找不到 HeronThreat,不會顯示失敗畫面。", this);
            enabled = false;
        }
    }

    void OnDisable()
    {
        caughtSince = -1f;
        if (text != null) text.gameObject.SetActive(false);
    }

    void OnDestroy()
    {
        if (text != null) Destroy(text.gameObject);
        if (overlayMaterial != null) Destroy(overlayMaterial);
    }

    void LateUpdate()
    {
        if (!heron.IsCaught)
        {
            caughtSince = -1f;
            if (text != null && text.gameObject.activeSelf) text.gameObject.SetActive(false);
            return;
        }

        if (caughtSince < 0f) caughtSince = Time.time;
        if (Time.time - caughtSince < showDelay) return;

        if (head == null && Camera.main != null) head = Camera.main.transform;
        if (head == null) return;

        if (text == null) CreateText();
        if (!text.gameObject.activeSelf) text.gameObject.SetActive(true);

        text.transform.SetPositionAndRotation(head.position + head.forward * distance, head.rotation);
    }

    void CreateText()
    {
        var go = new GameObject("HeronDeathText");
        go.layer = 2;   // Ignore Raycast
        text = go.AddComponent<TextMeshPro>();
        text.alignment = TextAlignmentOptions.Center;
        text.textWrappingMode = TextWrappingModes.Normal;
        text.fontSize = fontSize;
        text.color = textColor;
        text.rectTransform.sizeDelta = new Vector2(width, 1f);
        text.richText = true;

        string body = message;
        if (backgroundColor.a > 0f)
            body = $"<mark=#{ColorUtility.ToHtmlStringRGBA(backgroundColor)}>{message}</mark>";
        text.text = body;

        if (overlayShader != null)
        {
            overlayMaterial = new Material(text.fontSharedMaterial) { shader = overlayShader };
            text.fontSharedMaterial = overlayMaterial;
        }
        else
        {
            Debug.LogWarning("[HeronDeathScreen] 沒有指定 Overlay Shader,文字可能被鳥嘴擋住。", this);
        }
    }
}
