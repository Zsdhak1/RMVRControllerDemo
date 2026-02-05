using UnityEngine;
using MQTTnet;
using MQTTnet.Client;
using System.Threading.Tasks;
using System.Collections.Generic;
using Google.Protobuf;
using RoboMaster; // 引用你生成的协议文件
using System;     // 引用System以使用Action事件

// 定义一个小地图用的数据类
[System.Serializable]
public class RobotMapInfo
{
    public int id;
    public float x;
    public float y;
    public float yaw;
    public int hp;
    public int maxHp;
    public bool isVisible;    // 是否被雷达或视野探测到
    public float lastUpdate;  // 上次更新时间，用于判断信号丢失
}

public class DataManager : MonoBehaviour
{
    // 单例模式，让任何脚本都能通过 DataManager.Instance 访问
    public static DataManager Instance;

    // === 事件信号 (UI脚本监听这些来决定是否跳转) ===
    public event Action OnConnectSuccess; 
    public event Action<string> OnConnectFail; 

    // ================== 1. 公开数据池 (UI直接读取这些变量) ==================

    [Header("=== 比赛全局 ===")]
    public int MatchTime;           // 倒计时 (秒)
    public int CurrentStage;        // 阶段
    public int MyGold;              // 己方经济
    public int BaseHP;              // 己方基地血量
    public int OutpostHP;           // 己方前哨站血量

    [Header("=== 自身状态 ===")]
    public int MyID;                // 我的ID
    public int MyHP;                // 当前血量
    public int MyMaxHP;             // 血量上限
    public int MyAmmo;              // 剩余弹丸
    public float MyHeat;            // 当前热量
    public int MyMaxHeat;           // 热量上限
    public int MyChassisEnergy;     // 底盘能量
    public int MyExp;               // 当前经验值
    
    [Header("=== 自身位置 (Unity坐标) ===")]
    public Vector3 MyPosition;      
    public float MyYaw;             

    [Header("=== 模块状态 (用于指示灯) ===")]
    public bool Module_Chassis;     // 底盘是否正常
    public bool Module_Shooter;     // 发射机构是否正常
    public bool Module_Video;       // 图传是否正常

    [Header("=== 全场地图数据 ===")]
    // 键是机器人ID (1-7 红, 101-107 蓝), 值是位置信息
    public Dictionary<int, RobotMapInfo> MapData = new Dictionary<int, RobotMapInfo>();

    // ===============================================================

    private IMqttClient mqttClient;
    private System.Threading.SynchronizationContext _context;

    void Awake()
    {
        // 初始化单例
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        DontDestroyOnLoad(gameObject); // 切换场景时不销毁

        // 获取主线程上下文，用于在网络回调中操作Unity组件
        _context = System.Threading.SynchronizationContext.Current;
        
        // 初始化地图数据槽位
        InitializeMapData();
    }

    void Start()
    {
        // 这里留空，不要自动连接。等待 LoginUI 调用 ConnectToServer
        Debug.Log("[数据中心] 待机中...");
    }

    void InitializeMapData()
    {
        // 预先填充所有可能的机器人ID，防止空指针错误
        int[] ids = { 1, 2, 3, 4, 5, 6, 7, 101, 102, 103, 104, 105, 106, 107 };
        foreach (int id in ids)
        {
            MapData[id] = new RobotMapInfo { id = id, maxHp = 200, isVisible = false };
        }
    }

    // === 2. 连接控制逻辑 ===

    // 由 LoginUI 按钮点击触发
    public async void ConnectToServer()
    {
        // 读取全局配置（LoginUI刚才保存进去的）
        string targetIP = GlobalConfig.CurrentIP;
        int targetPort = GlobalConfig.CurrentPort;

        Debug.Log($"[数据中心] 发起连接请求 -> {targetIP}:{targetPort}");

        // 如果已有连接，先断开
        if (mqttClient != null && mqttClient.IsConnected)
        {
            await mqttClient.DisconnectAsync();
        }

        await ConnectMQTT(targetIP, targetPort);
    }

    private async Task ConnectMQTT(string ip, int port)
    {
        var factory = new MqttFactory();
        mqttClient = factory.CreateMqttClient();

        // 设置3秒超时，防止填错IP导致程序卡死
        var options = new MqttClientOptionsBuilder()
            .WithTcpServer(ip, port)
            .WithClientId("Quest3_Client_" + UnityEngine.Random.Range(1000, 9999))
            .WithTimeout(TimeSpan.FromSeconds(3))
            .WithCleanSession()
            .Build();

        // 绑定：连接成功回调
        mqttClient.ConnectedAsync += async e =>
        {
            Debug.Log($"<color=green>[连接成功] 已连接到裁判系统 {ip}</color>");

            // 订阅所有需要的数据包
            await SubscribeSafe("GameStatus");
            await SubscribeSafe("GlobalUnitStatus");
            await SubscribeSafe("GlobalLogisticsStatus");
            await SubscribeSafe("RobotStaticStatus");
            await SubscribeSafe("RobotDynamicStatus");
            await SubscribeSafe("RobotModuleStatus");
            await SubscribeSafe("RobotPosition");
            await SubscribeSafe("Buff");
            await SubscribeSafe("RaderInfoToClient");
            // 注意：RemoteControl 通常是上行发送，如果需要回环测试也可以订阅
            // await SubscribeSafe("RemoteControl"); 

            // 回到主线程通知 UI 跳转
            _context.Post(_ => OnConnectSuccess?.Invoke(), null);
        };

        // 绑定：收到消息回调
        mqttClient.ApplicationMessageReceivedAsync += async e =>
        {
            byte[] payload = e.ApplicationMessage.Payload;
            string topic = e.ApplicationMessage.Topic;

            // 回到主线程解析数据（因为要更新上面的 public 变量）
            _context.Post(_ => ParsePacket(topic, payload), null);
            await Task.CompletedTask;
        };

        // 绑定：断开连接回调
        mqttClient.DisconnectedAsync += async e =>
        {
            Debug.LogWarning($"[连接断开] 原因: {e.Reason}");
            await Task.CompletedTask;
        };

        // 执行连接
        try
        {
            await mqttClient.ConnectAsync(options);
        }
        catch (Exception ex)
        {
            Debug.LogError($"[连接异常] {ex.Message}");
            // 回到主线程通知 UI 显示红字错误
            _context.Post(_ => OnConnectFail?.Invoke(ex.Message), null);
        }
    }

    private async Task SubscribeSafe(string topic)
    {
        await mqttClient.SubscribeAsync(new MqttTopicFilterBuilder().WithTopic(topic).Build());
    }

    // === 3. 数据解析逻辑 (核心) ===
    void ParsePacket(string topic, byte[] data)
    {
        try
        {
            switch (topic)
            {
                case "GameStatus":
                    var game = GameStatus.Parser.ParseFrom(data);
                    MatchTime = game.StageCountdownSec;
                    CurrentStage = (int)game.CurrentStage;
                    break;

                case "RobotStaticStatus":
                    var stat = RobotStaticStatus.Parser.ParseFrom(data);
                    MyID = (int)stat.RobotId;
                    MyMaxHP = (int)stat.MaxHealth;
                    MyMaxHeat = (int)stat.MaxHeat;
                    break;

                case "RobotDynamicStatus":
                    var dyn = RobotDynamicStatus.Parser.ParseFrom(data);
                    MyHP = (int)dyn.CurrentHealth;
                    MyHeat = dyn.CurrentHeat;
                    MyAmmo = (int)dyn.RemainingAmmo;
                    MyChassisEnergy = (int)dyn.CurrentChassisEnergy;
                    MyExp = (int)dyn.CurrentExperience;
                    // 更新我在小地图上的血量状态
                    UpdateMapInfo(MyID, MyPosition.x, MyPosition.z, MyYaw, MyHP);
                    break;

                case "RobotPosition":
                    var pos = RobotPosition.Parser.ParseFrom(data);
                    // 假设协议发来的是 (X, Y) 平面坐标，对应 Unity 的 (X, Z)
                    MyPosition = new Vector3(pos.X, 0, pos.Y);
                    MyYaw = pos.Yaw;
                    break;

                case "GlobalUnitStatus":
                    var unit = GlobalUnitStatus.Parser.ParseFrom(data);
                    BaseHP = (int)unit.BaseHealth;
                    OutpostHP = (int)unit.OutpostHealth;
                    // 更新全场所有机器人的血量
                    UpdateAllRobotHP(unit.RobotHealth);
                    break;

                case "GlobalLogisticsStatus":
                    var logi = GlobalLogisticsStatus.Parser.ParseFrom(data);
                    MyGold = (int)logi.RemainingEconomy;
                    break;

                case "RobotModuleStatus":
                    var mod = RobotModuleStatus.Parser.ParseFrom(data);
                    // 简单的判断：1 表示在线/正常
                    Module_Video = mod.VideoTransmission == 1;
                    // 任意一个发射机构正常就算正常
                    Module_Shooter = (mod.SmallShooter == 1) || (mod.BigShooter == 1);
                    // 电源管理或底盘在线
                    Module_Chassis = mod.PowerManager == 1; 
                    break;
                
                case "RaderInfoToClient":
                    var radar = RaderInfoToClient.Parser.ParseFrom(data);
                    // 雷达发来的敌方位置信息
                    UpdateMapInfo((int)radar.TargetRobotId, radar.TargetPosX, radar.TargetPosY, radar.TargetAngle, -1);
                    break;
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"解析错误 [{topic}]: {e.Message}");
        }
    }

    // 更新小地图数据的辅助函数
    void UpdateMapInfo(int id, float x, float y, float angle, int hp)
    {
        if (MapData.ContainsKey(id))
        {
            MapData[id].x = x;
            MapData[id].y = y;
            MapData[id].yaw = angle;
            // 如果传入的血量不是-1，说明是确切血量数据，更新它
            if (hp != -1) MapData[id].hp = hp; 
            
            MapData[id].isVisible = true;
            MapData[id].lastUpdate = Time.time;
        }
    }

    // 更新全场血量 (修复了 uint 类型问题)
    void UpdateAllRobotHP(Google.Protobuf.Collections.RepeatedField<uint> hps)
    {
        // 映射规则：数组前7个是红方1-7，后7个是蓝方1-7
        // 对应文档附录ID说明
        int[] redIDs = { 1, 2, 3, 4, 5, 6, 7 };
        int[] blueIDs = { 101, 102, 103, 104, 105, 106, 107 };

        for (int i = 0; i < hps.Count; i++)
        {
            int id = 0;
            if (i < 7) id = redIDs[i];         // 红方
            else if (i < 14) id = blueIDs[i - 7]; // 蓝方

            if (id != 0 && MapData.ContainsKey(id))
            {
                MapData[id].hp = (int)hps[i];
                // 如果血量为0，视为死亡/离线，在地图上隐藏
                if (MapData[id].hp == 0) 
                {
                    MapData[id].isVisible = false;
                }
            }
        }
    }

    // 退出游戏时断开连接
    private async void OnDestroy()
    {
        if (mqttClient != null)
        {
            await mqttClient.DisconnectAsync();
        }
    }
}