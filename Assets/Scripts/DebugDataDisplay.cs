using UnityEngine;
using TMPro;

/// <summary>
/// 数据调试图形界面 - 实时显示DataManager中的所有数据
/// 将此脚本挂载到包含TMP_Text组件的GameObject上
/// </summary>
[RequireComponent(typeof(TMP_Text))]
public class DebugDataDisplay : MonoBehaviour
{
    [Header("=== 数据源 ===")]
    [Tooltip("DataManager实例，留空则自动查找")]
    public DataManager dataManager;

    [Header("=== 显示设置 ===")]
    [Tooltip("是否启用调试显示")]
    public bool enableDisplay = true;
    
    [Tooltip("更新间隔（秒），0表示每帧更新")]
    [Range(0f, 1f)]
    public float updateInterval = 0.1f;

    [Tooltip("是否使用富文本（颜色标记）")]
    public bool useRichText = true;

    [Tooltip("字体大小")]
    [Range(8, 36)]
    public int fontSize = 14;

    [Tooltip("最大显示行数，超出则截断")]
    [Range(20, 200)]
    public int maxLines = 100;

    [Header("=== 外观设置 ===")]
    [Tooltip("背景颜色（可选）")]
    public Color backgroundColor = new Color(0, 0, 0, 0.7f);

    [Tooltip("是否显示背景")]
    public bool showBackground = true;

    [Header("=== 过滤显示 ===")]
    [Tooltip("只显示过期数据")]
    public bool showOnlyStaleData = false;

    [Tooltip("要隐藏的数据分类（用逗号分隔）")]
    public string hideCategories = "";

    // 内部变量
    private TMP_Text tmpText;
    private float lastUpdateTime;
    private RectTransform rectTransform;
    private UnityEngine.UI.Image backgroundImage;

    void Awake()
    {
        tmpText = GetComponent<TMP_Text>();
        rectTransform = GetComponent<RectTransform>();
        
        // 如果没有指定DataManager，尝试自动查找
        if (dataManager == null)
        {
            dataManager = DataManager.Instance;
            if (dataManager == null)
            {
                Debug.LogWarning("[DebugDataDisplay] 未找到DataManager实例，请手动指定或确保DataManager先于本脚本初始化");
            }
        }

        // 初始化Text设置
        SetupText();
        
        // 创建背景
        if (showBackground)
        {
            CreateBackground();
        }
    }

    void SetupText()
    {
        if (tmpText == null) return;

        tmpText.fontSize = fontSize;
        tmpText.richText = useRichText;
        tmpText.alignment = TextAlignmentOptions.TopLeft;
        tmpText.overflowMode = TextOverflowModes.Truncate;
        tmpText.enableWordWrapping = true;
        
        // 等宽字体更适合显示数据
        if (tmpText.font == null)
        {
            Debug.LogWarning("[DebugDataDisplay] 建议为调试文本使用等宽字体以获得最佳显示效果");
        }
    }

    void CreateBackground()
    {
        // 查找或创建背景Image
        Transform bgTransform = transform.Find("DebugBackground");
        if (bgTransform == null)
        {
            GameObject bgObj = new GameObject("DebugBackground");
            bgObj.transform.SetParent(transform, false);
            bgTransform = bgObj.transform;
            
            // 设置RectTransform
            RectTransform bgRect = bgObj.AddComponent<RectTransform>();
            bgRect.anchorMin = Vector2.zero;
            bgRect.anchorMax = Vector2.one;
            bgRect.offsetMin = Vector2.zero;
            bgRect.offsetMax = Vector2.zero;
            bgRect.sizeDelta = Vector2.zero;
            
            // 添加Image组件
            backgroundImage = bgObj.AddComponent<UnityEngine.UI.Image>();
            backgroundImage.color = backgroundColor;
            
            // 确保背景在最底层
            bgTransform.SetAsFirstSibling();
        }
        else
        {
            backgroundImage = bgTransform.GetComponent<UnityEngine.UI.Image>();
        }
    }

    void Update()
    {
        if (!enableDisplay) 
        {
            if (tmpText != null && !string.IsNullOrEmpty(tmpText.text))
            {
                tmpText.text = "";
            }
            return;
        }

        // 检查更新间隔
        if (updateInterval > 0 && Time.time - lastUpdateTime < updateInterval)
        {
            return;
        }

        // 如果DataManager丢失，尝试重新获取
        if (dataManager == null)
        {
            dataManager = DataManager.Instance;
            if (dataManager == null)
            {
                tmpText.text = "<color=red>等待DataManager连接...</color>";
                return;
            }
        }

        // 更新显示
        UpdateDisplay();
        lastUpdateTime = Time.time;
    }

    void UpdateDisplay()
    {
        if (tmpText == null || dataManager == null) return;

        string displayText = dataManager.DebugDataString;

        // 应用过滤
        if (showOnlyStaleData)
        {
            displayText = FilterStaleDataOnly(displayText);
        }

        if (!string.IsNullOrEmpty(hideCategories))
        {
            displayText = FilterCategories(displayText, hideCategories);
        }

        // 限制行数
        if (maxLines > 0)
        {
            displayText = LimitLines(displayText, maxLines);
        }

        tmpText.text = displayText;
    }

    /// <summary>
    /// 只保留包含过期标记的数据
    /// </summary>
    string FilterStaleDataOnly(string input)
    {
        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        string[] lines = input.Split('\n');
        bool inStaleSection = false;

        foreach (string line in lines)
        {
            // 检查是否是过期数据行
            if (line.Contains("[过期]") || line.Contains("⚠"))
            {
                sb.AppendLine(line);
                inStaleSection = true;
            }
            else if (inStaleSection && string.IsNullOrWhiteSpace(line))
            {
                // 节结束
                sb.AppendLine();
                inStaleSection = false;
            }
            else if (!inStaleSection && line.StartsWith("="))
            {
                // 保留标题
                sb.AppendLine(line);
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// 隐藏指定分类的数据
    /// </summary>
    string FilterCategories(string input, string categoriesToHide)
    {
        string[] categories = categoriesToHide.Split(',', ';');
        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        string[] lines = input.Split('\n');
        bool skipSection = false;

        foreach (string line in lines)
        {
            // 检查是否是节标题
            if (line.StartsWith("【") && line.Contains("】"))
            {
                skipSection = false;
                foreach (string cat in categories)
                {
                    string trimmedCat = cat.Trim();
                    if (!string.IsNullOrEmpty(trimmedCat) && line.Contains(trimmedCat))
                    {
                        skipSection = true;
                        break;
                    }
                }
            }

            if (!skipSection)
            {
                sb.AppendLine(line);
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// 限制显示行数
    /// </summary>
    string LimitLines(string input, int maxLineCount)
    {
        string[] lines = input.Split('\n');
        if (lines.Length <= maxLineCount) return input;

        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        sb.AppendLine("<color=yellow>... (数据已截断) ...</color>");
        
        for (int i = lines.Length - maxLineCount; i < lines.Length; i++)
        {
            sb.AppendLine(lines[i]);
        }

        return sb.ToString();
    }

    /// <summary>
    /// 强制立即刷新显示
    /// </summary>
    [ContextMenu("立即刷新")]
    public void ForceRefresh()
    {
        lastUpdateTime = 0;
        UpdateDisplay();
    }

    /// <summary>
    /// 切换显示开关
    /// </summary>
    [ContextMenu("切换显示")]
    public void ToggleDisplay()
    {
        enableDisplay = !enableDisplay;
    }

    /// <summary>
    /// 设置DataManager（用于运行时切换）
    /// </summary>
    public void SetDataManager(DataManager newDataManager)
    {
        dataManager = newDataManager;
        ForceRefresh();
    }

    void OnValidate()
    {
        // 在Inspector中修改时实时更新
        if (tmpText != null)
        {
            SetupText();
        }
        
        if (backgroundImage != null)
        {
            backgroundImage.color = backgroundColor;
            backgroundImage.gameObject.SetActive(showBackground);
        }
    }
}
