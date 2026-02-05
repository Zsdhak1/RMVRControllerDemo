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
    public TextMeshProUGUI txtLevel;

    [Header("=== 模块指示灯 (Image) ===")]
    public Image lightChassis;
    public Image lightShooter;
    public Image lightVideo;
    public Color colorOK = Color.green;
    public Color colorWarn = Color.red;

    [Header("=== 小地图系统 ===")]
    public RectTransform minimapContent; // 地图容器
    public GameObject iconPrefabSelf;    // 自己的图标(箭头)
    public GameObject iconPrefabEnemy;   // 敌人图标(红点)
    public GameObject iconPrefabTeammate;// 队友图标(蓝点)

    // 场地实际尺寸 (米) [Page 32] 28m x 15m
    const float FIELD_W = 28.0f;
    const float FIELD_H = 15.0f;

    // 地图图标缓存池
    private Dictionary<int, RectTransform> mapIcons = new Dictionary<int, RectTransform>();

    void Update()
    {
        if (DataManager.Instance == null) return;
        var data = DataManager.Instance;

        // 1. 更新顶部
        int min = data.MatchTime / 60;
        int sec = data.MatchTime % 60;
        txtTime.text = $"{min:00}:{sec:00}";
        txtGold.text = data.MyGold.ToString();
        
        // 假设基地满血5000，前哨站1500
        imgBaseHP.fillAmount = (float)data.BaseHP / 5000f;
        txtBaseHPNum.text = data.BaseHP.ToString();
        imgOutpostHP.fillAmount = (float)data.OutpostHP / 1500f;

        // 2. 更新自身
        if (data.MyMaxHP > 0)
        {
            imgSelfHP.fillAmount = (float)data.MyHP / (float)data.MyMaxHP;
            txtSelfHPNum.text = $"{data.MyHP}/{data.MyMaxHP}";
        }
        if (data.MyMaxHeat > 0) imgHeat.fillAmount = data.MyHeat / (float)data.MyMaxHeat;
        // 假设底盘能量上限 (如60或200，取决于等级)
        imgEnergy.fillAmount = (float)data.MyChassisEnergy / 200f; 
        txtAmmo.text = data.MyAmmo.ToString();
        
        // 3. 更新模块灯
        lightChassis.color = data.Module_Chassis ? colorOK : colorWarn;
        lightShooter.color = data.Module_Shooter ? colorOK : colorWarn;
        lightVideo.color = data.Module_Video ? colorOK : colorWarn;

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
            RobotMapInfo info = kvp.Value;

            // 过滤：如果是我自己，或者最近没有更新(雷达丢失)，或者已死亡
            bool show = info.isVisible && info.hp > 0 && (Time.time - info.lastUpdate < 2.0f || id == data.MyID);
            
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

            // 坐标映射：假设 (0,0) 是场地左下角
            // 需要归一化 (0~1) 然后乘地图尺寸
            float normX = Mathf.Clamp01(info.x / FIELD_W);
            float normY = Mathf.Clamp01(info.y / FIELD_H);

            icon.anchoredPosition = new Vector2(normX * mapW, normY * mapH);
            
            // 只有自己才旋转图标，其他人一般只显示点
            if (id == data.MyID)
            {
                icon.localRotation = Quaternion.Euler(0, 0, -info.yaw);
            }
        }
    }

    // 简单判断队友：ID < 100 是红方， > 100 是蓝方
    // 如果我 < 100 且 目标 < 100 -> 队友
    bool IsTeammate(int targetID, int myID)
    {
        bool targetRed = targetID < 100;
        bool myRed = myID < 100;
        return targetRed == myRed;
    }
}