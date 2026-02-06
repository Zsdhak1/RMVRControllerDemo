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

    // === 事件定义 ===
    public event Action OnConnectSuccess; 
    public event Action<string> OnConnectFail; 
    public event Action<string> OnGameEvent;
    // [调试] 日志广播事件
    public event Action<string> OnDebugLog; 
    // [新增] 专门用于发送数据的日志事件
    public event Action<string> OnTxLog; 

    // ================== 公开数据 ==================

    [Header("=== 比赛全局 ===")]
    public int MatchTime;
    public int CurrentStage;
    public bool IsPaused;
    public int RedScore;
    public int BlueScore;
    
    [Header("=== 基地数据 ===")]
    public int BaseHP; 
    public int OutpostHP; 

    [Header("=== 己方经济 ===")]
    public int MyGold;
    public int MyTechLevel;
    
    [Header("=== 自身状态引用 ===")]
    public int MyID;
    public RobotInfo MyRobot; 

    [Header("=== 全场机器人 ===")]
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
        MyID = 1; // 默认值，连接后会更新
        MyRobot = MapData[1];
    }

    void Start() { } 

    // ================== 连接逻辑 ==================

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
            string msg = $"<color=green>[System] Connected to {ip}</color>";
            Debug.Log(msg);
            _context.Post(_ => OnDebugLog?.Invoke(msg), null); // 广播给DebugPanel

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
            _context.Post(_ => {
                OnConnectFail?.Invoke(ex.Message);
                OnDebugLog?.Invoke($"<color=red>[Error] {ex.Message}</color>");
            }, null); 
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

    // ================== 解析逻辑 (含日志) ==================

    void ParsePacket(string topic, byte[] data)
    {
        try
        {
            IMessage parsedMsg = null;

            switch (topic)
            {
                case "GameStatus":
                    var game = GameStatus.Parser.ParseFrom(data);
                    MatchTime = game.StageCountdownSec;
                    CurrentStage = (int)game.CurrentStage;
                    RedScore = (int)game.RedScore;
                    BlueScore = (int)game.BlueScore;
                    parsedMsg = game;
                    break;

                case "GlobalUnitStatus":
                    var unit = GlobalUnitStatus.Parser.ParseFrom(data);
                    BaseHP = (int)unit.BaseHealth;
                    OutpostHP = (int)unit.OutpostHealth;
                    // 【关键修复】这里调用了 UpdateAllHP
                    UpdateAllHP(unit.RobotHealth);
                    parsedMsg = unit;
                    break;

                case "GlobalLogisticsStatus":
                    var logi = GlobalLogisticsStatus.Parser.ParseFrom(data);
                    MyGold = (int)logi.RemainingEconomy;
                    parsedMsg = logi;
                    break;

                case "Event":
                    var evt = RoboMaster.Event.Parser.ParseFrom(data);
                    OnGameEvent?.Invoke($"Event: {evt.EventId}");
                    parsedMsg = evt;
                    break;

                case "RobotStaticStatus":
                    var stat = RobotStaticStatus.Parser.ParseFrom(data);
                    MyID = (int)stat.RobotId;
                    if(MapData.ContainsKey(MyID)) MyRobot = MapData[MyID];
                    MyRobot.maxHp = (int)stat.MaxHealth;
                    MyRobot.maxHeat = (int)stat.MaxHeat;
                    parsedMsg = stat;
                    break;

                case "RobotDynamicStatus":
                    var dyn = RobotDynamicStatus.Parser.ParseFrom(data);
                    MyRobot.currentHp = (int)dyn.CurrentHealth;
                    MyRobot.currentHeat = dyn.CurrentHeat;
                    MyRobot.currentAmmo = (int)dyn.RemainingAmmo;
                    MyRobot.chassisEnergy = (int)dyn.CurrentChassisEnergy;
                    MyRobot.lastUpdate = Time.time;
                    parsedMsg = dyn;
                    break;

                case "RobotModuleStatus":
                    var mod = RobotModuleStatus.Parser.ParseFrom(data);
                    MyRobot.modChassis = mod.PowerManager == 1;
                    MyRobot.modShooter = mod.SmallShooter == 1 || mod.BigShooter == 1;
                    MyRobot.modVideo = mod.VideoTransmission == 1;
                    parsedMsg = mod;
                    break;

                case "RobotPosition":
                    var pos = RobotPosition.Parser.ParseFrom(data);
                    MyRobot.pos = new Vector3(pos.X, 0, pos.Y);
                    MyRobot.yaw = pos.Yaw;
                    parsedMsg = pos;
                    break;

                case "RaderInfoToClient":
                    var radar = RaderInfoToClient.Parser.ParseFrom(data);
                    int tid = (int)radar.TargetRobotId;
                    UpdateMapInfo(tid, radar.TargetPosX, radar.TargetPosY, radar.TargetAngle, -1);
                    parsedMsg = radar;
                    break;
                
                default:
                    OnDebugLog?.Invoke($"<color=cyan>[RX] {topic}</color> ({data.Length} bytes)");
                    break;
            }

            // 广播日志到 Debug Panel
            if (parsedMsg != null)
            {
                string log = $"<color=cyan>[RX] {topic}</color>: {parsedMsg.ToString().Replace("\n", " ")}";
                OnDebugLog?.Invoke(log);
            }
        }
        catch (Exception e) { Debug.LogWarning($"Parse Error {topic}: {e.Message}"); }
    }

    // ================== 辅助函数 (被找回的遗失拼图) ==================

    // 【关键修复】这就是你报错缺少的函数
    void UpdateAllHP(Google.Protobuf.Collections.RepeatedField<uint> hps)
    {
        int[] redIDs = { 1, 2, 3, 4, 5, 6, 7 };
        int[] blueIDs = { 101, 102, 103, 104, 105, 106, 107 };

        for (int i = 0; i < hps.Count; i++)
        {
            int id = 0;
            if (i < 7) id = redIDs[i];
            else if (i < 14) id = blueIDs[i - 7];

            if (id != 0 && MapData.ContainsKey(id))
            {
                MapData[id].currentHp = (int)hps[i];
                if (MapData[id].currentHp == 0) MapData[id].isVisible = false;
            }
        }
    }

    void UpdateMapInfo(int id, float x, float y, float angle, int hp)
    {
        if (MapData.ContainsKey(id))
        {
            var t = MapData[id];
            t.pos = new Vector3(x, 0, y);
            t.yaw = angle;
            if (hp != -1) t.currentHp = hp;
            t.isVisible = true;
            t.lastUpdate = Time.time;
        }
    }

    // ================== 发送指令 ==================

    // ================== 发送指令 ==================

    public async void SendCommand<T>(string topic, T message) where T : IMessage
    {
        if (mqttClient == null || !mqttClient.IsConnected) return;
        
        // 过滤高频日志 (RemoteControl 每秒75次，不屏蔽会卡死UI)
        // 如果你一定要看摇杆数据，可以注释掉这个 if
        if (topic != "RemoteControl")
        {
            // 格式化日志内容
            string log = $"<color=yellow>[TX] {topic}</color>: {message.ToString().Replace("\n", " ")}";
            
            // 1. 广播给原来的总控制台 (可选，如果你希望总台也能看到发送数据)
            OnDebugLog?.Invoke(log);
            
            // 2. [新增] 广播给专门的发送监视面板
            OnTxLog?.Invoke(log);
        }

        byte[] payload = message.ToByteArray();
        var mqttMsg = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtMostOnce)
            .Build();
        await mqttClient.PublishAsync(mqttMsg);
    }
    
    // 快捷发送接口
    public void SendRemoteControl(RoboMaster.RemoteControl cmd) => SendCommand("RemoteControl", cmd);

    private async void OnDestroy()
    {
        if (mqttClient != null) await mqttClient.DisconnectAsync();
    }
}