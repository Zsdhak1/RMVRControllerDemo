using UnityEngine;
using UnityEngine.UI;
using TMPro;
using System.Collections.Generic;

public class FullCockpitUI : MonoBehaviour
{
    [Header("=== 顶部状态栏 ===")]
    public TextMeshProUGUI txtTime;
    public TextMeshProUGUI txtGold;
    public Image imgBaseHP;
    public TextMeshProUGUI txtBaseHPNum;
    public Image imgOutpostHP;

    [Header("=== 自身仪表盘 ===")]
    public Image imgSelfHP;
    public TextMeshProUGUI txtSelfHPNum;
    public Image imgHeat;
    public Image imgEnergy; // 能量条
    public TextMeshProUGUI txtAmmo;
    
    [Header("=== 模块指示灯 (Image) ===")]
    public Image lightChassis;
    public Image lightShooter;
    public Image lightVideo;
    public Color colorOK = Color.green;
    public Color colorWarn = Color.red;

    [Header("=== 小地图系统 ===")]
    public RectTransform minimapContent; // 地图容器
    public GameObject iconPrefabSelf;    // 自己的图标
    public GameObject iconPrefabEnemy;   // 敌人图标
    public GameObject iconPrefabTeammate;// 队友图标

    // 场地实际尺寸 (米)
    const float FIELD_W = 28.0f;
    const float FIELD_H = 15.0f;

    // 地图图标缓存
    private Dictionary<int, RectTransform> mapIcons = new Dictionary<int, RectTransform>();

    void Update()
    {
        if (DataManager.Instance == null) return;
        var data = DataManager.Instance;
        // 获取自己的机器人信息对象
        var myRobot = data.MyRobot; 

        // 1. 更新顶部
        int min = data.MatchTime / 60;
        int sec = data.MatchTime % 60;
        txtTime.text = $"{min:00}:{sec:00}";
        txtGold.text = data.MyGold.ToString();
        
        // 基地血量
        imgBaseHP.fillAmount = (float)data.BaseHP / 5000f;
        txtBaseHPNum.text = data.BaseHP.ToString();
        imgOutpostHP.fillAmount = (float)data.OutpostHP / 1500f;

        // 2. 更新自身 (从 myRobot 对象里取值)
        if (myRobot.maxHp > 0)
        {
            imgSelfHP.fillAmount = (float)myRobot.currentHp / (float)myRobot.maxHp;
            txtSelfHPNum.text = $"{myRobot.currentHp}/{myRobot.maxHp}";
        }
        
        if (myRobot.maxHeat > 0) 
            imgHeat.fillAmount = myRobot.currentHeat / (float)myRobot.maxHeat;
            
        // 假设底盘能量上限200
        imgEnergy.fillAmount = (float)myRobot.chassisEnergy / 200f; 
        txtAmmo.text = myRobot.currentAmmo.ToString();
        
        // 3. 更新模块灯
        lightChassis.color = myRobot.modChassis ? colorOK : colorWarn;
        lightShooter.color = myRobot.modShooter ? colorOK : colorWarn;
        lightVideo.color = myRobot.modVideo ? colorOK : colorWarn;

        // 4. 更新小地图
        UpdateMinimap();
    }

    void UpdateMinimap()
    {
        var data = DataManager.Instance;
        float mapW = minimapContent.rect.width;
        float mapH = minimapContent.rect.height;

        foreach (var kvp in data.MapData)
        {
            int id = kvp.Key;
            RobotInfo info = kvp.Value;

            // 过滤显示：自己永远显示；其他人只有 isVisible=true 且最近有更新才显示
            bool show = (id == data.MyID) || (info.isVisible && (Time.time - info.lastUpdate < 2.0f));
            
            if (!show)
            {
                if (mapIcons.ContainsKey(id)) mapIcons[id].gameObject.SetActive(false);
                continue;
            }

            // 创建图标
            if (!mapIcons.ContainsKey(id))
            {
                GameObject prefab = null;
                if (id == data.MyID) prefab = iconPrefabSelf;
                else if (IsTeammate(id, data.MyID)) prefab = iconPrefabTeammate;
                else prefab = iconPrefabEnemy;

                GameObject obj = Instantiate(prefab, minimapContent);
                mapIcons[id] = obj.GetComponent<RectTransform>();
            }

            RectTransform icon = mapIcons[id];
            icon.gameObject.SetActive(true);

            // 坐标映射
            float normX = Mathf.Clamp01(info.pos.x / FIELD_W);
            float normY = Mathf.Clamp01(info.pos.z / FIELD_H); // 注意：Unity中用 Z 代表平面纵向

            icon.anchoredPosition = new Vector2(normX * mapW, normY * mapH);
            
            // 只有自己显示旋转
            if (id == data.MyID)
            {
                icon.localRotation = Quaternion.Euler(0, 0, -info.yaw);
            }
        }
    }

    bool IsTeammate(int targetID, int myID)
    {
        bool targetRed = targetID < 100;
        bool myRed = myID < 100;
        return targetRed == myRed;
    }
}