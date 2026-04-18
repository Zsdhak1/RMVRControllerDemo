using UnityEngine;
using UnityEngine.UI;
using TMPro;
using System.Collections.Generic;
using System.Collections;

public class EngineerUIManager : MonoBehaviour
{
    [Header("=== 数据源 ===")]
    private DataManager data;

    [Header("=== 顶部信息 (Top Info) ===")]
    public TextMeshProUGUI timerText;
    public TextMeshProUGUI redScoreText;
    public TextMeshProUGUI blueScoreText;
    public TextMeshProUGUI currentStageText; 
    
    [Header("=== 战场态势 (Battle Status) ===")]
    public Slider myBaseSlider;
    public Slider myOutpostSlider;
    public Slider enemyBaseSlider; 
    public Slider enemyOutpostSlider; 
    [Header("=== 阵营颜色设定 ===")]
    public Color redTeamColor = new Color(0.9f, 0.2f, 0.2f, 1f);  
    public Color blueTeamColor = new Color(0.2f, 0.5f, 0.9f, 1f); 

    [Header("=== 血条背景填充 (Fill) ===")]
    public Image myBaseFill;
    public Image myOutpostFill;
    public Image enemyBaseFill;
    public Image enemyOutpostFill;

    [Header("=== 自身状态 (Self Status) ===")]
    public Slider hpSlider;           
    public Image hpFillImage;         
    public TextMeshProUGUI hpText;    
    public TextMeshProUGUI goldText;  
    public TextMeshProUGUI stateText; 

    [Header("=== 队友列表 (Teammates) ===")]
    public Transform teammateListContainer; 
    public GameObject teammateItemPrefab;   
    private Dictionary<int, TeammateItem> teammateItems = new Dictionary<int, TeammateItem>();

    [Header("=== 交互面板: 科技核心装配 ===")]
    public GameObject assemblyPanel;       
    public TextMeshProUGUI assemblyTitle;  
    public TextMeshProUGUI assemblyDescText; // 新增一个用于详细状态描述的Text(如果有的话，没有也可以复用Title)
    public Button[] difficultyBtns;        
    public Button confirmBtn;              
    public Button cancelBtn;               

    [Header("=== 交互面板: 生存操作 ===")]
    public GameObject survivalPanel;       
    public Button btnRevive;        // 免费复活 (协议cmd_id=3)
    public Button btnRemoteHeal;    // 立即买活 (协议cmd_id=4)
    public TextMeshProUGUI txtReviveCost; // 新增：如果UI里有地方显示复活金币的话

    [Header("=== 视觉反馈 ===")]
    public CanvasGroup damageFlash;        
    public GameObject notificationPrefab;  
    public Transform notificationContainer;

    [Header("=== 重连控制 ===")]
    public Button btnReconnect;

    // 内部状态
    private int lastHp;
    private uint selectedDifficulty = 0;
    private int lastCoreStatus = -1; // 追踪核心状态机变化

    void Start()
    {
        data = DataManager.Instance;
        lastHp = 500; 

        // 【修改1：修复按钮对应的协议ID】
        // 协议规定：3 = 确认复活（免费）； 4 = 兑换立即复活（买活）
        if(btnRevive) btnRevive.onClick.AddListener(() => SendCommon(3));
        if(btnRemoteHeal) btnRemoteHeal.onClick.AddListener(() => SendCommon(4));

        if(confirmBtn) confirmBtn.onClick.AddListener(() => SendAssembly(1));
        if(cancelBtn) cancelBtn.onClick.AddListener(() => SendAssembly(2));

        for (int i = 0; i < difficultyBtns.Length; i++)
        {
            uint level = (uint)(i + 1);
            difficultyBtns[i].onClick.AddListener(() => SelectDifficulty(level));
        }

        if(assemblyPanel) assemblyPanel.SetActive(false);
        if(damageFlash) damageFlash.alpha = 0;
        
        if(btnReconnect != null) 
        {
            btnReconnect.onClick.AddListener(() => 
            {
                if(DataManager.Instance != null)
                    DataManager.Instance.ReconnectToServer();
            });
        }
    }

    void Update()
    {
        if (data == null) return;

        UpdateMatchInfo();
        UpdateCurrentStage();
        UpdateBuildings();
        UpdateSelf();
        UpdateTeammates();
        UpdateAssemblyUI();
        CheckDamage();
    }

    void UpdateMatchInfo()
    {
        int min = data.MatchTime / 60;
        int sec = data.MatchTime % 60;
        if(timerText) timerText.text = $"{min:D2}:{sec:D2}";

        if(redScoreText) redScoreText.text = data.RedScore.ToString();
        if(blueScoreText) blueScoreText.text = data.BlueScore.ToString();
    }

    void UpdateCurrentStage()
    {
        string stageStr = data.CurrentStage switch
        {
            0 => "未开始比赛",
            1 => "准备阶段",
            2 => "十五秒裁判系统自检阶段",
            3 => "五秒倒计时",
            4 => "比赛中",
            5 => "比赛结算中",
            _ => $"未知状态({data.CurrentStage})"
        };

        if (data.IsPaused)
            stageStr += " [暂停]";

        if (currentStageText) currentStageText.text = stageStr;
    }

    void UpdateBuildings()
    {
        bool isRedTeam = (data.MyID < 100);
        Color myColor = isRedTeam ? redTeamColor : blueTeamColor;
        Color enemyColor = isRedTeam ? blueTeamColor : redTeamColor;
        
        if (myBaseSlider != null) myBaseSlider.value = (float)data.BaseHP / 5000f;
        if (myOutpostSlider != null) myOutpostSlider.value = (float)data.OutpostHP / 1500f;
        if (enemyBaseSlider != null) enemyBaseSlider.value = (float)data.EnemyBaseHP / 5000f;
        if (enemyOutpostSlider != null) enemyOutpostSlider.value = (float)data.EnemyOutpostHP / 1500f;
        
        if (myBaseFill != null) myBaseFill.color = myColor;
        if (myOutpostFill != null) myOutpostFill.color = myColor;
        if (enemyBaseFill != null) enemyBaseFill.color = enemyColor;
        if (enemyOutpostFill != null) enemyOutpostFill.color = enemyColor;
    }

    // 【修改2：完全修复生存(复活/买活)的 Interactable 逻辑】
    void UpdateSelf()
    {
        if (data.MyRobot == null) return;

        if (hpSlider) hpSlider.value = (float)data.MyRobot.currentHp / data.MyRobot.maxHp;
        if (hpText) hpText.text = $"{data.MyRobot.currentHp}";
        
        if (hpFillImage)
        {
            float ratio = (float)data.MyRobot.currentHp / data.MyRobot.maxHp;
            hpFillImage.color = ratio < 0.3f ? Color.red : Color.green;
        }

        if (goldText) goldText.text = $"{data.MyGold} G";

        // 获取协议解析后的生存状态 (请确保DataManager中有这些变量名，否则需要修改)
        bool isDead = data.IsPendingRespawn; // 或者 data.MyRobot.isDead
        bool canFree = data.CanFreeRespawn;
        bool canPay = data.CanPayForRespawn;
        int payCost = (int)data.GoldCostForRespawn;

        string status = "正常";
        if (isDead) status = "阵亡待复活";
        else if (data.CurrentStage == 1) status = "准备";
        if (stateText) stateText.text = status;

        // 复活按钮始终可用（移除所有限制）
        if (btnRevive) btnRevive.interactable = true;
        if (btnRemoteHeal) btnRemoteHeal.interactable = true;
        
        // 仅显示状态文本（不影响按钮可用性）
        if (txtReviveCost)
        {
            if (!isDead)
            {
                txtReviveCost.text = "处于存活状态";
                txtReviveCost.color = Color.white;
            }
            else
            {
                txtReviveCost.color = data.MyGold >= payCost ? Color.yellow : Color.red;
                txtReviveCost.text = $"买活金币: {payCost}G";
            }
        }
    }

    void UpdateTeammates()
    {
        if (teammateListContainer == null || teammateItemPrefab == null) return;

        int myTeam = data.MyRobot.team;
        int myID = data.MyID;

        foreach (var kvp in data.MapData)
        {
            int id = kvp.Key;
            RobotInfo info = kvp.Value;

            if (info.team == myTeam && id != myID)
            {
                if (!teammateItems.ContainsKey(id))
                {
                    GameObject itemObj = Instantiate(teammateItemPrefab, teammateListContainer);
                    TeammateItem itemScript = itemObj.GetComponent<TeammateItem>();
                    if (itemScript != null) teammateItems.Add(id, itemScript);
                }

                if (teammateItems.TryGetValue(id, out TeammateItem item))
                {
                    item.UpdateInfo(id, info.currentHp, info.maxHp);
                }
            }
        }
    }

    // 【修改3：严谨的科技核心 6步 状态机重构】
    void UpdateAssemblyUI()
    {
        // 从底层拉取状态
        int coreStatus = data.TechCoreStatus; // 取值: 1 到 6
        int maxDiff = data.MaxDifficultyLevel;

        // 如果状态无改变，就不疯狂刷新按钮了
        if (coreStatus != lastCoreStatus)
        {
            lastCoreStatus = coreStatus;

            switch (coreStatus)
            {
                case 1: // 1：未进入装配状态 (完全初始状态)
                    if (assemblyTitle) assemblyTitle.text = "选择要装配的难度等级";
                    if (assemblyDescText) assemblyDescText.text = "准备就绪";
                    break;

                case 2: // 2：已选择装配难度，科技核心移动中 (推杆伸出)
                    if (assemblyTitle) assemblyTitle.text = "装配台对接中...";
                    if (assemblyDescText) assemblyDescText.text = "推杆正在伸出，请勿触碰";
                    break;

                case 3: // 3：科技核心移动完成，可进行首个装配步骤
                    if (assemblyTitle) assemblyTitle.text = "请进行阶段 1";
                    if (assemblyDescText) assemblyDescText.text = "<color=green>物理推杆已就位！请插入能量块</color>";
                    break;

                case 4: // 4：上一个装配步骤已完成，可进行下一个装配步骤
                    if (assemblyTitle) assemblyTitle.text = "请进行下一阶段";
                    if (assemblyDescText) assemblyDescText.text = "请继续执行指定动作";
                    break;

                case 5: // 5：装配步骤已全部完成！
                    if (assemblyTitle) assemblyTitle.text = "装配物理动作全完成！";
                    if (assemblyDescText) assemblyDescText.text = "<color=cyan>所有步骤达标！请按下[确认装配]</color>";
                    break;

                case 6: // 6：已确认装配，科技核心移动中 (回收流程)
                    if (assemblyTitle) assemblyTitle.text = "资源提取中";
                    if (assemblyDescText) assemblyDescText.text = "<color=yellow>平台回收中...</color>";
                    break;
            }
            
            // 所有按钮始终可用（移除限制）
            UnlockAllButtons();
        }
        
        // 持续渲染倒计时 (如果有的话)
        if (coreStatus == 2 || coreStatus == 4 || coreStatus == 6)
        {
            if (assemblyTitle && data.AssemblyRemainTimeAll > 0)
            {
                // 可以加个时间后缀
                // assemblyTitle.text += $" ({data.AssemblyRemainTime}s)";
            }
        }
    }

    private void UnlockAllButtons()
    {
        // 难度按钮始终可用
        foreach (var btn in difficultyBtns)
        {
            if (btn != null) btn.interactable = true;
        }
        // 确认和取消按钮也始终可用
        if (confirmBtn) confirmBtn.interactable = true;
        if (cancelBtn) cancelBtn.interactable = true;
    }

    // === 交互函数 ===

    public void SelectDifficulty(uint level)
    {
        selectedDifficulty = level;
        // 点下难度的瞬间，可以给服务器下发选择指令
        // 因为状态机的切换需要由服务器发回状态2来确认
        data.SendAssemblyCommand(1, selectedDifficulty); 
        ShowNotification($"已请求激活难度: {level} 级");
    }

    void SendAssembly(uint op)
    {
        // 移除状态检查限制，按钮始终可点击
        // 注：服务器端仍会验证操作合法性
        
        data.SendAssemblyCommand(op, selectedDifficulty);
        ShowNotification(op == 1 ? "发送: 确认装配" : "发送: 取消装配");
    }

    void SendCommon(uint type)
    {
        data.SendCommonCommand(type);
        ShowNotification(type == 3 ? "请求: 正常复活" : "请求: 扣除金币买活");
    }

    // === 视觉反馈 ===

    void CheckDamage()
    {
        if (data.MyRobot == null) return;
        if (data.MyRobot.currentHp < lastHp && data.MyRobot.currentHp > 0)
        {
            if (damageFlash) StartCoroutine(FlashRed());
        }
        lastHp = data.MyRobot.currentHp;
    }

    IEnumerator FlashRed()
    {
        damageFlash.alpha = 0.8f;
        while (damageFlash.alpha > 0)
        {
            damageFlash.alpha -= Time.deltaTime * 2f;
            yield return null;
        }
    }

    public void ShowNotification(string msg)
    {
        if (!notificationPrefab || !notificationContainer) return;
        
        GameObject go = Instantiate(notificationPrefab, notificationContainer);
        var txt = go.GetComponentInChildren<TextMeshProUGUI>();
        if (txt) txt.text = msg;
        
        Destroy(go, 2.5f);
    }
    
    public void ToggleMenu()
    {
        if (assemblyPanel) assemblyPanel.SetActive(!assemblyPanel.activeSelf);
        if (survivalPanel) survivalPanel.SetActive(assemblyPanel.activeSelf); // 取决于你的激活逻辑
    }
}