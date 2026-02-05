using UnityEngine;
using MQTTnet;
using MQTTnet.Client;
using System.Threading.Tasks;
using System.Collections.Generic;
using Google.Protobuf;
using RoboMaster; 
using System;

// 聚合后的单机信息类
[System.Serializable]
public class RobotInfo
{
    public int id;
    public int team; // 0=Red, 1=Blue
    
    // 静态数据
    public int type;
    public int level;
    public int maxHp;
    public int maxHeat;
    
    // 动态数据
    public int currentHp;
    public float currentHeat;
    public int currentAmmo;
    public int chassisEnergy;
    public bool isOutCombat;
    
    // 位置
    public Vector3 pos; 
    public float yaw;
    
    // 模块状态
    public bool modChassis;
    public bool modShooter;
    public bool modVideo;
    
    // 地图显示用
    public bool isVisible;    
    public float lastUpdate;  
    
    public RobotInfo(int id) 
    { 
        this.id = id; 
        this.team = id < 100 ? 0 : 1;
        this.maxHp = 100; 
    }
}

public class DataManager : MonoBehaviour
{
    public static DataManager Instance;

    public event Action OnConnectSuccess; 
    public event Action<string> OnConnectFail; 
    public event Action<string> OnGameEvent; 

    // ================== 公开数据 ==================

    [Header("=== 比赛全局 ===")]
    public int MatchTime;
    public int CurrentStage;
    public bool IsPaused;
    public int RedScore;
    public int BlueScore;
    
    [Header("=== 基地数据 ===")]
    public int BaseHP; // 当前基地血量
    public int OutpostHP; // 前哨站血量

    [Header("=== 己方经济 ===")]
    public int MyGold;
    public int MyTechLevel;
    
    [Header("=== 自身状态引用 ===")]
    public int MyID;
    public RobotInfo MyRobot; 

    [Header("=== 全场机器人 (MapData) ===")]
    // 为了兼容UI脚本，我们保留 MapData 这个名字，但类型升级为 RobotInfo
    public Dictionary<int, RobotInfo> MapData = new Dictionary<int, RobotInfo>();

    // =================================================

    private IMqttClient mqttClient;
    private System.Threading.SynchronizationContext _context;

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);
        _context = System.Threading.SynchronizationContext.Current;
        InitializeData();
    }

    void InitializeData()
    {
        int[] ids = { 1, 2, 3, 4, 5, 6, 7, 101, 102, 103, 104, 105, 106, 107 };
        foreach (int id in ids)
        {
            MapData[id] = new RobotInfo(id);
        }
        MyID = 1; // 默认红1
        MyRobot = MapData[1];
    }

    void Start() { } // 手动连接，不自动启动

    public async void ConnectToServer()
    {
        string ip = GlobalConfig.CurrentIP;
        int port = GlobalConfig.CurrentPort;

        if (mqttClient != null && mqttClient.IsConnected) await mqttClient.DisconnectAsync();

        var factory = new MqttFactory();
        mqttClient = factory.CreateMqttClient();
        var options = new MqttClientOptionsBuilder()
            .WithTcpServer(ip, port)
            .WithClientId("Quest3_" + UnityEngine.Random.Range(1000, 9999))
            .WithTimeout(TimeSpan.FromSeconds(3))
            .WithCleanSession()
            .Build();

        mqttClient.ConnectedAsync += async e =>
        {
            Debug.Log("<color=green>[DataManager] 连接成功</color>");
            await SubscribeAll();
            _context.Post(_ => OnConnectSuccess?.Invoke(), null);
        };

        mqttClient.ApplicationMessageReceivedAsync += async e =>
        {
            byte[] payload = e.ApplicationMessage.Payload;
            string topic = e.ApplicationMessage.Topic;
            _context.Post(_ => ParsePacket(topic, payload), null);
            await Task.CompletedTask;
        };

        try { await mqttClient.ConnectAsync(options); }
        catch (Exception ex) { 
            Debug.LogError(ex); 
            _context.Post(_ => OnConnectFail?.Invoke(ex.Message), null); 
        }
    }

    private async Task SubscribeAll()
    {
        string[] topics = {
            "GameStatus", "GlobalUnitStatus", "GlobalLogisticsStatus", 
            "Event", "RobotStaticStatus", "RobotDynamicStatus", 
            "RobotModuleStatus", "RobotPosition", "RaderInfoToClient"
        };
        foreach (var t in topics) await mqttClient.SubscribeAsync(new MqttTopicFilterBuilder().WithTopic(t).Build());
    }

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

                case "GlobalUnitStatus":
                    var unit = GlobalUnitStatus.Parser.ParseFrom(data);
                    BaseHP = (int)unit.BaseHealth;
                    OutpostHP = (int)unit.OutpostHealth;
                    // 这里可以添加 UpdateAllHP(unit.RobotHealth) 逻辑
                    break;

                case "GlobalLogisticsStatus":
                    var logi = GlobalLogisticsStatus.Parser.ParseFrom(data);
                    MyGold = (int)logi.RemainingEconomy;
                    break;

                // 【修复点1】明确指定 RoboMaster.Event 以避免冲突
                case "Event":
                    RoboMaster.Event evt = RoboMaster.Event.Parser.ParseFrom(data);
                    OnGameEvent?.Invoke($"Event: {evt.EventId}");
                    break;

                case "RobotStaticStatus":
                    var stat = RobotStaticStatus.Parser.ParseFrom(data);
                    MyID = (int)stat.RobotId;
                    // 更新引用
                    if(MapData.ContainsKey(MyID)) MyRobot = MapData[MyID];
                    
                    MyRobot.maxHp = (int)stat.MaxHealth;
                    MyRobot.maxHeat = (int)stat.MaxHeat;
                    break;

                case "RobotDynamicStatus":
                    var dyn = RobotDynamicStatus.Parser.ParseFrom(data);
                    MyRobot.currentHp = (int)dyn.CurrentHealth;
                    MyRobot.currentHeat = dyn.CurrentHeat;
                    MyRobot.currentAmmo = (int)dyn.RemainingAmmo;
                    MyRobot.chassisEnergy = (int)dyn.CurrentChassisEnergy;
                    MyRobot.lastUpdate = Time.time;
                    break;

                case "RobotModuleStatus":
                    var mod = RobotModuleStatus.Parser.ParseFrom(data);
                    MyRobot.modChassis = mod.PowerManager == 1;
                    MyRobot.modShooter = mod.SmallShooter == 1 || mod.BigShooter == 1;
                    MyRobot.modVideo = mod.VideoTransmission == 1;
                    break;

                case "RobotPosition":
                    var pos = RobotPosition.Parser.ParseFrom(data);
                    MyRobot.pos = new Vector3(pos.X, 0, pos.Y);
                    MyRobot.yaw = pos.Yaw;
                    // 【修复点2】移除对 MyPosition 的直接赋值，改用 MyRobot 访问
                    break;

                case "RaderInfoToClient":
                    var radar = RaderInfoToClient.Parser.ParseFrom(data);
                    int tid = (int)radar.TargetRobotId;
                    if (MapData.ContainsKey(tid))
                    {
                        var t = MapData[tid];
                        t.pos = new Vector3(radar.TargetPosX, 0, radar.TargetPosY);
                        t.yaw = radar.TargetAngle;
                        t.isVisible = true;
                        t.lastUpdate = Time.time;
                    }
                    break;
            }
        }
        catch (Exception e) { Debug.LogWarning($"Parse Error {topic}: {e.Message}"); }
    }
    
    // 供UI调用的通用发送指令
    public async void SendCommand<T>(string topic, T message) where T : IMessage
    {
        if (mqttClient == null || !mqttClient.IsConnected) return;
        byte[] payload = message.ToByteArray();
        var mqttMsg = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtMostOnce)
            .Build();
        await mqttClient.PublishAsync(mqttMsg);
    }

    private async void OnDestroy()
    {
        if (mqttClient != null) await mqttClient.DisconnectAsync();
    }
}