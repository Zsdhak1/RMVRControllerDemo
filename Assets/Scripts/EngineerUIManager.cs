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
    public TextMeshProUGUI timerText; // 倒计时
    public TextMeshProUGUI redScoreText;  // 【修改】红方比分独立
    public TextMeshProUGUI blueScoreText; // 【修改】蓝方比分独立
    
    [Header("=== 战场态势 (Battle Status) ===")]
    public Slider myBaseSlider;
    public Slider myOutpostSlider;
    public Slider enemyBaseSlider; 
    public Slider enemyOutpostSlider; 

    [Header("=== 自身状态 (Self Status) ===")]
    public Slider hpSlider;           
    public Image hpFillImage;         
    public TextMeshProUGUI hpText;    
    public TextMeshProUGUI goldText;  
    public TextMeshProUGUI stateText; 

    [Header("=== 队友列表 (Teammates) ===")]
    public Transform teammateListContainer; // 【新增】拖入一个 VerticalLayoutGroup
    public GameObject teammateItemPrefab;   // 【新增】拖入挂载了 TeammateItem 脚本的预制体
    // 内部字典，用于管理生成的队友条目
    private Dictionary<int, TeammateItem> teammateItems = new Dictionary<int, TeammateItem>();

    [Header("=== 交互面板: 科技核心装配 ===")]
    public GameObject assemblyPanel;       
    public TextMeshProUGUI assemblyTitle;  
    public Button[] difficultyBtns;        
    public Button confirmBtn;              
    public Button cancelBtn;               

    [Header("=== 交互面板: 生存操作 ===")]
    public GameObject survivalPanel;       
    public Button btnRevive;               
    public Button btnRemoteHeal;           

    [Header("=== 视觉反馈 ===")]
    public CanvasGroup damageFlash;        
    public GameObject notificationPrefab;  
    public Transform notificationContainer;

    // 内部状态
    private int lastHp;
    private uint selectedDifficulty = 0;

    void Start()
    {
        data = DataManager.Instance;
        lastHp = 500; 

        // 按钮绑定
        if(confirmBtn) confirmBtn.onClick.AddListener(() => SendAssembly(1));
        if(cancelBtn) cancelBtn.onClick.AddListener(() => SendAssembly(2));
        if(btnRevive) btnRevive.onClick.AddListener(() => SendCommon(4));
        if(btnRemoteHeal) btnRemoteHeal.onClick.AddListener(() => SendCommon(6));

        for (int i = 0; i < difficultyBtns.Length; i++)
        {
            uint level = (uint)(i + 1);
            difficultyBtns[i].onClick.AddListener(() => SelectDifficulty(level));
        }

        if(assemblyPanel) assemblyPanel.SetActive(false);
        if(damageFlash) damageFlash.alpha = 0;
    }

    void Update()
    {
        if (data == null) return;

        UpdateMatchInfo();
        UpdateBuildings();
        UpdateSelf();
        UpdateTeammates();  // 【新增】更新队友信息
        UpdateAssemblyUI(); 
        CheckDamage();      
    }

    // 1. 更新比赛通用信息
    void UpdateMatchInfo()
    {
        int min = data.MatchTime / 60;
        int sec = data.MatchTime % 60;
        if(timerText) timerText.text = $"{min:D2}:{sec:D2}";
        
        // 【修改】分开设置比分
        if(redScoreText) redScoreText.text = data.RedScore.ToString();
        if(blueScoreText) blueScoreText.text = data.BlueScore.ToString();
    }

    // 2. 更新建筑血量
    void UpdateBuildings()
    {
        UpdateSlider(myBaseSlider, data.BaseHP, 5000);
        UpdateSlider(myOutpostSlider, data.OutpostHP, 1500);
        UpdateSlider(enemyBaseSlider, data.EnemyBaseHP, 5000);
        UpdateSlider(enemyOutpostSlider, data.EnemyOutpostHP, 1500);
    }

    void UpdateSlider(Slider s, float val, float max)
    {
        if (s != null) s.value = Mathf.Lerp(s.value, val / max, Time.deltaTime * 5f);
    }

    // 3. 更新自身状态
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

        string status = "正常";
        if (data.MyRobot.isDead) status = "战亡";
        else if (data.CurrentStage == 1) status = "准备";
        if (stateText) stateText.text = status;

        if (btnRevive) btnRevive.interactable = data.MyRobot.isDead && data.MyGold >= 200; 
        if (btnRemoteHeal) btnRemoteHeal.interactable = !data.MyRobot.isDead && data.MyGold >= 100;
    }

    // 【新增】更新队友列表
    void UpdateTeammates()
    {
        if (teammateListContainer == null || teammateItemPrefab == null) return;

        int myTeam = data.MyRobot.team;
        int myID = data.MyID;

        // 遍历所有数据
        foreach (var kvp in data.MapData)
        {
            int id = kvp.Key;
            RobotInfo info = kvp.Value;

            // 筛选条件：同队 且 不是自己
            if (info.team == myTeam && id != myID)
            {
                // 如果还没有这个队友的 UI 条目，创建一个
                if (!teammateItems.ContainsKey(id))
                {
                    GameObject itemObj = Instantiate(teammateItemPrefab, teammateListContainer);
                    TeammateItem itemScript = itemObj.GetComponent<TeammateItem>();
                    if (itemScript != null)
                    {
                        teammateItems.Add(id, itemScript);
                    }
                }

                // 更新数据
                if (teammateItems.TryGetValue(id, out TeammateItem item))
                {
                    item.UpdateInfo(id, info.currentHp, info.maxHp);
                }
            }
        }
    }

    // 4. 装配逻辑
    void UpdateAssemblyUI()
    {
        if (assemblyTitle)
        {
            string coreState = data.EnemyCoreStatus == 0 ? "无核心" : "核心就位";
            assemblyTitle.text = $"{coreState} - 剩余时间: {data.AssemblyRemainTime}s";
        }
    }

    // === 交互函数 ===

    public void SelectDifficulty(uint level)
    {
        selectedDifficulty = level;
        ShowNotification($"已选择难度: {level}级");
    }

    void SendAssembly(uint op)
    {
        if (op == 1 && selectedDifficulty == 0)
        {
            ShowNotification("请先选择难度！");
            return;
        }
        data.SendAssemblyCommand(op, selectedDifficulty);
        ShowNotification(op == 1 ? "发送: 确认装配" : "发送: 取消装配");
    }

    void SendCommon(uint type)
    {
        data.SendCommonCommand(type);
        ShowNotification(type == 4 ? "请求: 立即复活" : "请求: 远程补血");
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
        if (survivalPanel) survivalPanel.SetActive(assemblyPanel.activeSelf);
    }
}