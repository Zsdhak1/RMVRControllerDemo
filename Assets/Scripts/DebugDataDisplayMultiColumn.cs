using UnityEngine;
using TMPro;
using System.Collections.Generic;
using System.Linq;
using System.Text;

/// <summary>
/// 多栏数据调试图形界面 - 将数据分配到多个文本框分栏显示
/// </summary>
public class DebugDataDisplayMultiColumn : MonoBehaviour
{
    [System.Serializable]
    public class ColumnConfig
    {
        [Tooltip("该栏的标题")]
        public string columnTitle = "数据栏";
        
        [Tooltip("显示该栏数据的TMP文本框（必须指定）")]
        public TMP_Text textComponent;
        
        [Tooltip("该栏显示的数据分类，不填则自动分配剩余数据")]
        public List<string> assignedCategories = new List<string>();
        
        [Tooltip("字体大小")]
        [Range(8, 36)]
        public int fontSize = 12;
        
        [HideInInspector]
        public StringBuilder stringBuilder = new StringBuilder(1024);
    }

    [Header("=== 数据源 ===")]
    [Tooltip("DataManager实例，留空则自动查找")]
    public DataManager dataManager;

    [Header("=== 分栏配置 ===")]
    [Tooltip("配置各栏位（建议2-3栏）")]
    public List<ColumnConfig> columns = new List<ColumnConfig>();

    [Header("=== 显示设置 ===")]
    [Tooltip("是否启用调试显示")]
    public bool enableDisplay = true;
    
    [Tooltip("更新间隔（秒）")]
    [Range(0.05f, 1f)]
    public float updateInterval = 0.1f;

    [Tooltip("是否使用富文本颜色")]
    public bool useRichText = true;

    [Tooltip("DebugAll模式下使用紧凑字体（10号）")]
    public bool useCompactFontInDebugAll = true;

    [Tooltip("全局字体大小覆盖（0表示使用各栏设置）")]
    [Range(0, 36)]
    public int globalFontSizeOverride = 0;

    [Header("=== 调试输出 ===")]
    [Tooltip("在Console中输出调试信息")]
    public bool verboseLogging = false;
    
    [Tooltip("显示原始数据字符串（用于诊断）")]
    public bool showRawData = false;
    
    [Tooltip("显示过期/未收到的数据分类（DebugAll自动启用）")]
    public bool showExpiredData = true;
    
    [Header("=== 运行时查看（只读）===")]
    [Tooltip("当前接收到的原始数据（调试用）")]
    [TextArea(5, 10)]
    public string currentRawDataPreview = ""; // 在Inspector中查看原始数据

    [Header("=== 数据分类定义 ===")]
    [Tooltip("数据分类名称（对应DebugDataString中的【】标题）")]
    public List<string> availableCategories = new List<string>()
    {
        "比赛状态",
        "建筑数据", 
        "经济科技",
        "自身状态",
        "复活状态",
        "工程装配",
        "模块状态",
        "性能体系",
        "能量机关",
        "哨兵状态",
        "飞镖状态",
        "空中支援",
        "Buff信息",
        "雷达目标",
        "受伤统计",
        "判罚信息",
        "特殊机制"
    };

    [Header("=== 快速预设 ===")]
    [Tooltip("快速应用预设布局")]
    public LayoutPreset preset = LayoutPreset.Custom;

    public enum LayoutPreset
    {
        Custom,           // 自定义
        TwoColumn_Basic,  // 两栏-基础/详细
        TwoColumn_Self,   // 两栏-己方/敌方
        ThreeColumn,      // 三栏-比赛/自身/其他
        RadarOnly,        // 只显示雷达数据
        EngineerOnly,     // 只显示工程相关
        DebugAll          // 调试模式-显示所有数据
    }

    private float lastUpdateTime;
    private Dictionary<string, string> parsedSections = new Dictionary<string, string>();

    void Awake()
    {
        if (dataManager == null)
        {
            dataManager = DataManager.Instance;
        }

        if (preset == LayoutPreset.DebugAll)
        {
            DistributeAllCategories();
        }
        else
        {
            ApplyPreset();
        }
        
        InitializeColumns();
    }

    void Start()
    {
        // 确保字体设置在场景加载后正确应用
        ApplyFontSettings();
        
        if (preset == LayoutPreset.DebugAll && enableDisplay)
        {
            Debug.Log($"[DebugDataDisplay] DebugAll模式已启动，{columns.Count}个栏位准备就绪");
        }
    }

    void InitializeColumns()
    {
        ApplyFontSettings();
    }

    void ApplyFontSettings()
    {
        foreach (var column in columns)
        {
            if (column.textComponent != null)
            {
                column.textComponent.richText = useRichText;
                column.textComponent.alignment = TextAlignmentOptions.TopLeft;
                column.textComponent.overflowMode = TextOverflowModes.Truncate;
                column.textComponent.enableWordWrapping = true;
                
                // 应用字体大小设置
                int targetFontSize = GetTargetFontSize(column);
                if (column.textComponent.fontSize != targetFontSize)
                {
                    column.textComponent.fontSize = targetFontSize;
                }
            }
        }
    }

    int GetTargetFontSize(ColumnConfig column)
    {
        // 优先使用全局覆盖
        if (globalFontSizeOverride > 0)
        {
            return globalFontSizeOverride;
        }
        
        // DebugAll模式下可以选择使用更小字体
        if (preset == LayoutPreset.DebugAll && useCompactFontInDebugAll)
        {
            return Mathf.Min(column.fontSize, 10);
        }
        return column.fontSize;
    }

    void DistributeAllCategories()
    {
        if (columns.Count == 0) return;

        // 使用所有预定义分类，如果DataManager有更多数据也会通过availableCategories更新
        var allCategories = new List<string>(availableCategories);
        
        // 确保包含所有标准分类
        var standardCategories = new[] {
            "比赛状态", "建筑数据", "经济科技", "自身状态", "复活状态",
            "工程装配", "模块状态", "性能体系", "能量机关", "哨兵状态",
            "飞镖状态", "空中支援", "Buff信息", "雷达目标", "受伤统计",
            "判罚信息", "特殊机制"
        };
        
        foreach (var cat in standardCategories)
        {
            if (!allCategories.Contains(cat))
            {
                allCategories.Add(cat);
            }
        }

        // 根据栏位数量均匀分配
        int categoriesPerColumn = Mathf.CeilToInt((float)allCategories.Count / columns.Count);
        
        for (int i = 0; i < columns.Count; i++)
        {
            columns[i].columnTitle = $"调试数据 [{i + 1}/{columns.Count}]";
            columns[i].assignedCategories.Clear();
            
            int startIndex = i * categoriesPerColumn;
            int count = Mathf.Min(categoriesPerColumn, allCategories.Count - startIndex);
            
            if (count > 0)
            {
                columns[i].assignedCategories.AddRange(allCategories.GetRange(startIndex, count));
            }
        }
    }

    void ApplyPreset()
    {
        if (preset == LayoutPreset.Custom || preset == LayoutPreset.DebugAll) return;
        if (columns.Count < 2) return;

        // 清空现有分配
        foreach (var col in columns)
        {
            col.assignedCategories.Clear();
        }

        switch (preset)
        {
            case LayoutPreset.TwoColumn_Basic:
                // 左栏：基础重要数据
                if (columns.Count >= 1)
                {
                    columns[0].columnTitle = "基础状态";
                    columns[0].assignedCategories.AddRange(new[] { "比赛状态", "建筑数据", "自身状态", "复活状态", "经济科技" });
                }
                // 右栏：详细数据
                if (columns.Count >= 2)
                {
                    columns[1].columnTitle = "详细信息";
                    columns[1].assignedCategories.AddRange(new[] { "模块状态", "工程装配", "Buff信息", "能量机关", "判罚信息" });
                }
                break;

            case LayoutPreset.TwoColumn_Self:
                // 左栏：己方数据
                if (columns.Count >= 1)
                {
                    columns[0].columnTitle = "己方状态";
                    columns[0].assignedCategories.AddRange(new[] { "比赛状态", "建筑数据", "自身状态", "模块状态", "经济科技", "复活状态" });
                }
                // 右栏：敌方/全局数据
                if (columns.Count >= 2)
                {
                    columns[1].columnTitle = "敌方/全局";
                    columns[1].assignedCategories.AddRange(new[] { "雷达目标", "能量机关", "空中支援", "飞镖状态", "哨兵状态", "特殊机制" });
                }
                break;

            case LayoutPreset.ThreeColumn:
                // 左栏：比赛全局
                if (columns.Count >= 1)
                {
                    columns[0].columnTitle = "比赛全局";
                    columns[0].assignedCategories.AddRange(new[] { "比赛状态", "建筑数据", "经济科技", "特殊机制", "判罚信息" });
                }
                // 中栏：自身状态
                if (columns.Count >= 2)
                {
                    columns[1].columnTitle = "自身状态";
                    columns[1].assignedCategories.AddRange(new[] { "自身状态", "模块状态", "性能体系", "复活状态", "受伤统计", "Buff信息" });
                }
                // 右栏：其他系统
                if (columns.Count >= 3)
                {
                    columns[2].columnTitle = "其他系统";
                    columns[2].assignedCategories.AddRange(new[] { "工程装配", "能量机关", "哨兵状态", "飞镖状态", "空中支援", "雷达目标" });
                }
                break;

            case LayoutPreset.RadarOnly:
                if (columns.Count >= 1)
                {
                    columns[0].columnTitle = "雷达数据";
                    columns[0].assignedCategories.AddRange(new[] { "雷达目标", "比赛状态" });
                }
                break;

            case LayoutPreset.EngineerOnly:
                if (columns.Count >= 1)
                {
                    columns[0].columnTitle = "工程数据";
                    columns[0].assignedCategories.AddRange(new[] { "工程装配", "经济科技", "自身状态", "模块状态" });
                }
                break;

            case LayoutPreset.DebugAll:
                // 调试模式：自动将所有分类分配到所有可用栏位
                DistributeAllCategories();
                break;
        }
    }

    void Update()
    {
        if (!enableDisplay)
        {
            ClearAllColumns();
            return;
        }

        if (Time.time - lastUpdateTime < updateInterval) return;

        if (dataManager == null)
        {
            dataManager = DataManager.Instance;
            if (dataManager == null) 
            {
                ShowWaitingMessage();
                return;
            }
        }

        UpdateDisplay();
        lastUpdateTime = Time.time;
    }

    void UpdateDisplay()
    {
        // 检查DataManager的调试字符串是否生成
        if (string.IsNullOrEmpty(dataManager.DebugDataString))
        {
            if (verboseLogging) Debug.LogWarning("[DebugDataDisplay] dataManager.DebugDataString 为空");
            
            // 显示诊断信息
            foreach (var column in columns)
            {
                if (column.textComponent != null)
                {
                    column.textComponent.text = "<color=yellow>等待数据...</color>\n\n" +
                        "请检查:\n" +
                        "1. DataManager.EnableDebugStringUpdate = true\n" +
                        "2. 已连接到服务器\n" +
                        "3. 有数据流入";
                }
            }
            return;
        }

        // 更新运行时预览（每帧更新以便在Inspector中查看）
        if (dataManager.DebugDataString.Length > 500)
        {
            currentRawDataPreview = dataManager.DebugDataString.Substring(0, 500) + "\n... (截断)";
        }
        else
        {
            currentRawDataPreview = dataManager.DebugDataString;
        }

        if (verboseLogging)
        {
            Debug.Log($"[DebugDataDisplay] DebugDataString 长度: {dataManager.DebugDataString.Length}");
        }

        // 显示原始数据（诊断模式）
        if (showRawData && columns.Count > 0 && columns[0].textComponent != null)
        {
            columns[0].textComponent.text = "<size=8>" + dataManager.DebugDataString + "</size>";
            return;
        }

        // 确保字体设置已应用
        ApplyFontSettings();

        // 解析DataManager的字符串为各个Section
        ParseSections(dataManager.DebugDataString);
        
        if (verboseLogging)
        {
            Debug.Log($"[DebugDataDisplay] 解析到 {parsedSections.Count} 个Section: {string.Join(", ", parsedSections.Keys)}");
        }

        // 分配到各栏
        foreach (var column in columns)
        {
            if (column.textComponent == null) continue;

            column.stringBuilder.Clear();
            
            // 添加栏标题
            if (!string.IsNullOrEmpty(column.columnTitle))
            {
                column.stringBuilder.AppendLine($"=== {column.columnTitle} ===");
                column.stringBuilder.AppendLine();
            }

            // 添加分配的数据分类
            bool hasContent = false;
            List<string> missingCategories = new List<string>();
            
            foreach (var category in column.assignedCategories)
            {
                if (parsedSections.ContainsKey(category))
                {
                    // 添加小标题（带过期标记检查）
                    bool isStale = IsCategoryStale(category);
                    if (isStale)
                    {
                        column.stringBuilder.AppendLine($"<color=grey>【{category}】[过期]</color>");
                    }
                    else
                    {
                        column.stringBuilder.AppendLine($"【{category}】");
                    }
                    
                    // 添加内容
                    column.stringBuilder.AppendLine(parsedSections[category]);
                    column.stringBuilder.AppendLine(); // 添加空行分隔
                    hasContent = true;
                }
                else
                {
                    missingCategories.Add(category);
                    // 即使数据缺失，也显示分类标题和提示
                    column.stringBuilder.AppendLine($"【{category}】");
                    column.stringBuilder.AppendLine("  <color=grey>数据未收到或格式不匹配</color>");
                    column.stringBuilder.AppendLine();
                }
            }

            // Debug模式下显示诊断信息
            if (verboseLogging && missingCategories.Count > 0)
            {
                Debug.LogWarning($"[DebugDataDisplay] 栏位 '{column.columnTitle}' 缺少数据: {string.Join(", ", missingCategories)}");
            }

            // 自动分配剩余数据（给没有指定分类的最后一栏）
            if (column.assignedCategories.Count == 0 && column == columns.Last())
            {
                var assignedCategories = columns.SelectMany(c => c.assignedCategories).Distinct().ToList();
                var remainingCategories = parsedSections.Keys.Except(assignedCategories);
                
                foreach (var category in remainingCategories)
                {
                    column.stringBuilder.AppendLine(parsedSections[category]);
                }
            }

            column.textComponent.text = column.stringBuilder.ToString();
        }
    }

    void ParseSections(string fullText)
    {
        parsedSections.Clear();
        
        if (string.IsNullOrEmpty(fullText)) return;
        
        // 检查是否包含Section标记（支持【】和[过期]标记）
        if (!fullText.Contains("【") || !fullText.Contains("】"))
        {
            if (verboseLogging) Debug.LogWarning("[DebugDataDisplay] 数据字符串不包含 '【】' 标记");
            ParseSectionsBackup(fullText);
            return;
        }
        
        string[] lines = fullText.Split('\n');
        string currentSection = "";
        StringBuilder currentContent = new StringBuilder();
        int sectionCount = 0;

        foreach (string rawLine in lines)
        {
            string line = rawLine;
            
            // 处理带颜色标签的情况: <color=grey>【xxx】[过期]</color>
            // 先移除颜色标签
            string cleanLine = RemoveColorTags(line).Trim();
            
            // 检测节标题（格式：【xxx】）
            int startIdx = cleanLine.IndexOf('【');
            int endIdx = cleanLine.IndexOf('】');
            
            if (startIdx >= 0 && endIdx > startIdx)
            {
                // 保存上一节
                if (!string.IsNullOrEmpty(currentSection) && currentContent.Length > 0)
                {
                    parsedSections[currentSection] = currentContent.ToString().TrimEnd();
                    sectionCount++;
                }
                
                // 提取节名称（不包括【】）
                currentSection = cleanLine.Substring(startIdx + 1, endIdx - startIdx - 1);
                currentContent.Clear();
            }
            else
            {
                currentContent.AppendLine(line);
            }
        }

        // 保存最后一节
        if (!string.IsNullOrEmpty(currentSection) && currentContent.Length > 0)
        {
            parsedSections[currentSection] = currentContent.ToString().TrimEnd();
            sectionCount++;
        }
        
        if (verboseLogging) Debug.Log($"[DebugDataDisplay] 解析完成，共 {sectionCount} 个Section: {string.Join(", ", parsedSections.Keys)}");
    }
    
    /// <summary>
    /// 移除Unity富文本颜色标签
    /// </summary>
    string RemoveColorTags(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;
        
        // 移除 <color=xxx> 和 </color>
        string result = input;
        while (result.Contains("<color="))
        {
            int start = result.IndexOf("<color=");
            int end = result.IndexOf(">", start);
            if (end > start)
            {
                result = result.Remove(start, end - start + 1);
            }
            else break;
        }
        result = result.Replace("</color>", "");
        
        // 移除 <size=xxx> 和 </size>
        while (result.Contains("<size="))
        {
            int start = result.IndexOf("<size=");
            int end = result.IndexOf(">", start);
            if (end > start)
            {
                result = result.Remove(start, end - start + 1);
            }
            else break;
        }
        result = result.Replace("</size>", "");
        
        return result;
    }
    
    /// <summary>
    /// 备用解析方法，用于非标准格式
    /// </summary>
    void ParseSectionsBackup(string fullText)
    {
        // 将整个文本作为 "原始数据" Section
        parsedSections["原始数据"] = fullText;
    }

    void ClearAllColumns()
    {
        foreach (var column in columns)
        {
            if (column.textComponent != null && !string.IsNullOrEmpty(column.textComponent.text))
            {
                column.textComponent.text = "";
            }
        }
    }

    void ShowWaitingMessage()
    {
        foreach (var column in columns)
        {
            if (column.textComponent != null)
            {
                column.textComponent.text = "<color=red>等待DataManager连接...</color>";
            }
        }
    }

    /// <summary>
    /// 强制刷新显示
    /// </summary>
    [ContextMenu("立即刷新")]
    public void ForceRefresh()
    {
        lastUpdateTime = 0;
    }

    /// <summary>
    /// 应用预设（运行时可用）
    /// </summary>
    public void ApplyPresetRuntime(LayoutPreset newPreset)
    {
        preset = newPreset;
        if (preset == LayoutPreset.DebugAll)
        {
            DistributeAllCategories();
        }
        else
        {
            ApplyPreset();
        }
        ForceRefresh();
    }

    /// <summary>
    /// 快速设置为DebugAll模式（显示所有数据）
    /// </summary>
    [ContextMenu("设置为DebugAll模式")]
    public void SetDebugAllMode()
    {
        preset = LayoutPreset.DebugAll;
        showExpiredData = true; // Debug模式下显示所有数据，包括过期的
        useCompactFontInDebugAll = true; // 使用紧凑字体显示更多内容
        enableDisplay = true;
        updateInterval = 0.1f; // 快速更新
        DistributeAllCategories();
        
        // 列出所有分配的分类
        for (int i = 0; i < columns.Count; i++)
        {
            Debug.Log($"[DebugDataDisplay] 栏位[{i+1}/{columns.Count}]: {string.Join(", ", columns[i].assignedCategories)}");
        }
        
        ForceRefresh();
        ApplyFontSettings();
        Debug.Log($"[DebugDataDisplay] 已切换到DebugAll模式，{availableCategories.Count}个分类分配到 {columns.Count} 个栏位，字体大小: {(globalFontSizeOverride > 0 ? globalFontSizeOverride : (useCompactFontInDebugAll ? 10 : 12))}");
    }

    /// <summary>
    /// 检查某个分类的数据是否过期
    /// </summary>
    bool IsCategoryStale(string category)
    {
        if (dataManager == null) return false;
        
        return category switch
        {
            "比赛状态" => dataManager.IsGameStatusStale,
            "建筑数据" => dataManager.IsGlobalUnitStatusStale,
            "经济科技" => dataManager.IsGlobalLogisticsStatusStale,
            "自身状态" => dataManager.MyRobot?.IsDataStale ?? false,
            "复活状态" => dataManager.IsRespawnStatusStale,
            "工程装配" => dataManager.IsTechCoreStale,
            "模块状态" => dataManager.IsModuleStatusStale,
            "性能体系" => dataManager.IsPerformanceSelectionStale,
            "能量机关" => dataManager.IsRuneStale,
            "哨兵状态" => dataManager.IsSentryStatusStale,
            "飞镖状态" => dataManager.IsDartStale,
            "空中支援" => dataManager.IsAirSupportStale,
            "受伤统计" => dataManager.MyInjuryStat?.IsStale ?? false,
            "判罚信息" => dataManager.IsPenaltyStale,
            "特殊机制" => dataManager.IsSpecialMechanismStale,
            "Buff信息" => false, // Buff有过期自动清理机制
            "雷达目标" => false, // 雷达目标有过期自动清理机制
            _ => false
        };
    }

    /// <summary>
    /// 设置所有栏位的字体大小
    /// </summary>
    [ContextMenu("设置所有栏位字体为10")]
    public void SetAllFontSize10()
    {
        SetAllFontSize(10);
    }

    [ContextMenu("设置所有栏位字体为12")]
    public void SetAllFontSize12()
    {
        SetAllFontSize(12);
    }

    [ContextMenu("设置所有栏位字体为14")]
    public void SetAllFontSize14()
    {
        SetAllFontSize(14);
    }

    public void SetAllFontSize(int size)
    {
        globalFontSizeOverride = 0; // 清除全局覆盖
        foreach (var column in columns)
        {
            column.fontSize = size;
        }
        ApplyFontSettings();
        Debug.Log($"[DebugDataDisplay] 所有栏位字体大小已设置为 {size}");
    }

    /// <summary>
    /// 诊断数据问题
    /// </summary>
    [ContextMenu("诊断数据问题")]
    public void DiagnoseDataIssues()
    {
        StringBuilder diag = new StringBuilder();
        diag.AppendLine("=== DebugDataDisplay 诊断报告 ===");
        diag.AppendLine();
        
        // 检查DataManager
        if (dataManager == null)
        {
            dataManager = DataManager.Instance;
        }
        
        if (dataManager == null)
        {
            diag.AppendLine("❌ DataManager 为 null");
            diag.AppendLine("   解决方案: 确保场景中有DataManager且已初始化");
        }
        else
        {
            diag.AppendLine($"✓ DataManager 存在");
            diag.AppendLine($"  - 连接状态: {(dataManager.IsMqttConnected ? "已连接" : "未连接")}");
            diag.AppendLine($"  - DebugDataString 长度: {dataManager.DebugDataString?.Length ?? 0}");
            diag.AppendLine($"  - DebugDataString 为空: {string.IsNullOrEmpty(dataManager.DebugDataString)}");
        }
        
        diag.AppendLine();
        
        // 检查栏位配置
        diag.AppendLine($"栏位数量: {columns.Count}");
        for (int i = 0; i < columns.Count; i++)
        {
            var col = columns[i];
            diag.AppendLine($"  栏位[{i}]: {col.columnTitle}");
            diag.AppendLine($"    - TextComponent: {(col.textComponent != null ? "已分配" : "❌ 未分配")}");
            diag.AppendLine($"    - 分配分类: {col.assignedCategories.Count} 个");
            if (col.assignedCategories.Count > 0)
            {
                diag.AppendLine($"      [{string.Join(", ", col.assignedCategories)}]");
            }
        }
        
        diag.AppendLine();
        
        // 检查解析结果
        if (dataManager != null && !string.IsNullOrEmpty(dataManager.DebugDataString))
        {
            ParseSections(dataManager.DebugDataString);
            diag.AppendLine($"解析到的Section数: {parsedSections.Count}");
            if (parsedSections.Count > 0)
            {
                diag.AppendLine("Sections列表:");
                foreach (var key in parsedSections.Keys)
                {
                    diag.AppendLine($"  - {key}");
                }
            }
            
            diag.AppendLine();
            diag.AppendLine("原始数据前200字符:");
            diag.AppendLine(dataManager.DebugDataString.Substring(0, Mathf.Min(200, dataManager.DebugDataString.Length)));
        }
        
        diag.AppendLine();
        diag.AppendLine("=== 建议 ===");
        if (string.IsNullOrEmpty(dataManager?.DebugDataString))
        {
            diag.AppendLine("1. 检查DataManager.EnableDebugStringUpdate是否为true");
            diag.AppendLine("2. 检查是否已连接到服务器");
            diag.AppendLine("3. 检查是否有数据流入（看DataManager的MapData是否有更新）");
        }
        else if (parsedSections.Count == 0)
        {
            diag.AppendLine("1. 数据格式可能不匹配，勾选'showRawData'查看原始内容");
            diag.AppendLine("2. 检查数据是否包含'【】'标记的标题");
        }
        else if (columns.Count == 0 || columns.Any(c => c.textComponent == null))
        {
            diag.AppendLine("1. 为所有栏位分配TMP_Text组件");
        }
        else
        {
            diag.AppendLine("配置看起来正常，尝试勾选'verboseLogging'查看详细日志");
        }
        
        Debug.Log(diag.ToString());
    }

    void OnValidate()
    {
        // 编辑器中修改字体大小时实时应用
        if (!Application.isPlaying)
        {
            ApplyFontSettings();
        }
        
        // 编辑器中修改时自动应用预设
        if (preset == LayoutPreset.DebugAll)
        {
            DistributeAllCategories();
        }
        else if (preset != LayoutPreset.Custom)
        {
            ApplyPreset();
        }
    }
}

/// <summary>
/// StringBuilder扩展，用于兼容旧版.NET
/// </summary>
public static class StringBuilderExtensions
{
    public static void Clear(this StringBuilder sb)
    {
        sb.Length = 0;
    }
}
