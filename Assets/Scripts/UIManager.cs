using UnityEngine;
using UnityEngine.UI;
using TMPro;
using System.Collections.Generic;
using System.Collections;

public class UIManager : MonoBehaviour
{
    [Header("=== 数据源引用 ===")]
    // 确保 DataManager 已经在场景中
    private DataManager data; 

    [Header("=== 顶部状态栏 (Top Panel) ===")]
    public TextMeshProUGUI matchTimerText;
    public TextMeshProUGUI currentStageText;
    [Space]
    public TextMeshProUGUI redScoreText;
    public TextMeshProUGUI blueScoreText;
    [Space]
    public Slider baseHpSlider;     // 基地血量 (默认最大 5000)
    public TextMeshProUGUI baseHpText;
    public Slider outpostHpSlider;  // 前哨站血量 (默认最大 1500)
    
    [Header("=== 玩家仪表盘 (Player HUD) ===")]
    public SmoothHealthBar playerHealthBar; // 引用你之前写的血条脚本
    public Slider energySlider;             // 底盘能量 (最大 60)
    public Slider heatSlider;               // 枪口热量
    public Image heatFillImage;             // 用于热量过高变色
    [Space]
    public TextMeshProUGUI ammoText;        // 弹丸数
    public TextMeshProUGUI goldText;        // 金币
    public TextMeshProUGUI levelText;       // 等级
    
    [Header("=== 模块状态 (Status Icons) ===")]
    // 拖入 Image 组件，损坏时我们会将其变红或显示为叉号
    public Image chassisIcon;
    public Image shooterIcon;
    public Image videoIcon;
    
    [Header("=== 视觉效果 ===")]
    public CanvasGroup damageFlashGroup;    // 全屏受击红光 (Alpha 0->1)
    public Color normalColor = Color.white;
    public Color warningColor = Color.red;
    public Color offlineColor = Color.gray;

    [Header("=== 消息日志 (Log System) ===")]
    public Transform eventLogContainer;     // 游戏事件 (击杀等) 的父物体
    public GameObject eventTextPrefab;      // 预制体: 带 TextMeshPro 的空物体
    public ScrollRect debugScrollRect;      // 调试信息滚动视图
    public TextMeshProUGUI debugContent;    // 调试信息的文本组件 (长文本)
    
    // 内部变量
    private int lastHp;
    private const int MAX_BASE_HP = 5000;
    private const int MAX_OUTPOST_HP = 1500;
    private const float MAX_CHASSIS_ENERGY = 60f; // 标准缓冲能量

    void Start()
    {
        data = DataManager.Instance;
        
        // 订阅事件
        if (data != null)
        {
            data.OnGameEvent += AddGameEventLog;
            data.OnDebugLog += AddDebugLog;
            data.OnTxLog += AddDebugLog; // 发送日志也显示在调试面板
        }

        // 初始化 UI 状态
        if(damageFlashGroup) damageFlashGroup.alpha = 0;
        lastHp = 100; // 假设初始
    }

    void Update()
    {
        if (data == null) return;

        UpdateTopPanel();
        UpdatePlayerHUD();
        CheckDamageFeedback();
    }

    // --- 1. 顶部面板更新 ---
    void UpdateTopPanel()
    {
        // 倒计时 MM:SS
        int min = data.MatchTime / 60;
        int sec = data.MatchTime % 60;
        matchTimerText.text = $"{min:D2}:{sec:D2}";

        // 阶段显示 (可以根据整数对应枚举，这里简化显示)
        currentStageText.text = GetStageName(data.CurrentStage);

        // 分数
        redScoreText.text = data.RedScore.ToString();
        blueScoreText.text = data.BlueScore.ToString();

        // 基地与前哨站 (假设 DataManager 中的 HP 是己方的)
        if (baseHpSlider)
        {
            baseHpSlider.maxValue = MAX_BASE_HP;
            baseHpSlider.value = Mathf.Lerp(baseHpSlider.value, data.BaseHP, Time.deltaTime * 5f);
            if(baseHpText) baseHpText.text = $"{data.BaseHP}/{MAX_BASE_HP}";
        }

        if (outpostHpSlider)
        {
            outpostHpSlider.maxValue = MAX_OUTPOST_HP;
            outpostHpSlider.value = Mathf.Lerp(outpostHpSlider.value, data.OutpostHP, Time.deltaTime * 5f);
        }
    }

    // --- 2. 玩家 HUD 更新 ---
    void UpdatePlayerHUD()
    {
        var robot = data.MyRobot;
        if (robot == null) return;

        // 血条 (使用 SmoothHealthBar)
        if (playerHealthBar)
        {
            playerHealthBar.maxHealth = robot.maxHp; // 动态更新最大血量(升级后会变)
            playerHealthBar.SetHealth(robot.currentHp);
        }

        // 能量条 (底盘缓冲)
        if (energySlider)
        {
            energySlider.maxValue = MAX_CHASSIS_ENERGY; // 或者是 robot.chassisEnergy 的上限
            energySlider.value = Mathf.Lerp(energySlider.value, robot.chassisEnergy, Time.deltaTime * 10f);
        }

        // 热量条 (FPS 风格：热量高了变红)
        if (heatSlider)
        {
            heatSlider.maxValue = robot.maxHeat;
            heatSlider.value = Mathf.Lerp(heatSlider.value, robot.currentHeat, Time.deltaTime * 5f);
            
            // 热量过高警告 (超过 80% 变红)
            if (heatFillImage)
            {
                float heatRatio = (float)robot.currentHeat / robot.maxHeat;
                heatFillImage.color = heatRatio > 0.8f ? warningColor : normalColor;
            }
        }

        // 文本数值
        ammoText.text = robot.currentAmmo.ToString();
        goldText.text = data.MyGold.ToString();
        levelText.text = $"LV.{robot.level}";

        // 模块状态图标 (Icon Color)
        SetModuleStatus(chassisIcon, robot.modChassis);
        SetModuleStatus(shooterIcon, robot.modShooter);
        SetModuleStatus(videoIcon, robot.modVideo);
    }

    // 辅助：设置模块图标颜色
    void SetModuleStatus(Image icon, bool isOk)
    {
        if (icon == null) return;
        // 正常白色，损坏显示灰色/红色
        icon.color = isOk ? normalColor : offlineColor;
    }

    // --- 3. 受击反馈 ---
    void CheckDamageFeedback()
    {
        if (data.MyRobot == null) return;

        // 简单的受击检测：血量突然减少
        if (data.MyRobot.currentHp < lastHp)
        {
            StartCoroutine(FlashDamageEffect());
        }
        lastHp = data.MyRobot.currentHp;
    }

    IEnumerator FlashDamageEffect()
    {
        if (damageFlashGroup == null) yield break;
        
        damageFlashGroup.alpha = 0.8f;
        while (damageFlashGroup.alpha > 0)
        {
            damageFlashGroup.alpha -= Time.deltaTime * 2f;
            yield return null;
        }
    }

    // --- 4. 日志系统 ---
    
    // 游戏事件 (屏幕中央飘字或左侧击杀栏)
    void AddGameEventLog(string msg)
    {
        if (eventLogContainer == null || eventTextPrefab == null) return;

        GameObject newLog = Instantiate(eventTextPrefab, eventLogContainer);
        newLog.GetComponent<TextMeshProUGUI>().text = msg;
        
        // 5秒后自动销毁
        Destroy(newLog, 5f);
    }

    // 调试日志 (控制台)
    void AddDebugLog(string msg)
    {
        if (debugContent == null) return;
        
        // 追加文本 (限制长度防止内存溢出)
        debugContent.text += $"\n{System.DateTime.Now:HH:mm:ss} {msg}";
        
        if (debugContent.text.Length > 10000)
            debugContent.text = debugContent.text.Substring(5000);

        // 自动滚动到底部
        if(debugScrollRect) StartCoroutine(ScrollToBottom());
    }

    IEnumerator ScrollToBottom()
    {
        yield return new WaitForEndOfFrame();
        debugScrollRect.verticalNormalizedPosition = 0f;
    }

    string GetStageName(int stageIndex)
    {
        // 这里需要根据具体的 Protobuf 枚举定义
        switch(stageIndex) {
            case 0: return "PRE MATCH";
            case 1: return "SETUP";
            case 2: return "COMBAT"; // 7分钟比赛
            case 3: return "END";
            default: return "UNKNOWN";
        }
    }
}