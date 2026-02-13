using UnityEngine;
using MQTTnet;
using MQTTnet.Client;
using System.Threading.Tasks;
using System.Collections.Generic;
using Google.Protobuf;
using RoboMaster; // 确保 Protobuf 生成的命名空间正确
using System;



// 聚合后的机器人信息类
[System.Serializable]
public class RobotInfo
{
    public int id;
    public int team; // 0=Red, 1=Blue
    
    // 静态与动态数据
    public int type;
    public int level;
    public int maxHp;
    public int currentHp;
    public int maxHeat;
    public float currentHeat;
    public int currentAmmo;
    public int chassisEnergy;
    public bool isOutCombat;
    
    // 位置与姿态
    public Vector3 pos; 
    public float yaw;
    
    // 模块在线状态
    public bool modChassis;
    public bool modShooter;
    public bool modVideo;
    
    // 逻辑状态
    public bool isVisible;    
    public float lastUpdate;  
    
    public RobotInfo(int id) 
    { 
        this.id = id; 
        this.team = id < 100 ? 0 : 1;
        this.maxHp = 100; 
        this.isVisible = false;
    }
}

public class DataManager : MonoBehaviour
{
    public static DataManager Instance;

    // === 事件定义 ===
    public event Action OnConnectSuccess; 
    public event Action<string> OnConnectFail; 
    public event Action<string> OnGameEvent;
    public event Action<string> OnDebugLog; 
    public event Action<string> OnTxLog; 

    [Header("=== 比赛全局数据 ===")]
    public int MatchTime;
    public int CurrentStage;
    public int RedScore;
    public int BlueScore;
    
    [Header("=== 基地/前哨站血量 ===")]
    public int BaseHP; 
    public int OutpostHP; 

    [Header("=== 资源与等级 ===")]
    public int MyGold;
    public int MyTechLevel;
    
    [Header("=== 自身状态 ===")]
    public int MyID;
    public RobotInfo MyRobot; 

    [Header("=== 全场机器人映射表 ===")]
    public Dictionary<int, RobotInfo> MapData = new Dictionary<int, RobotInfo>();

    
    public bool IsConnected => mqttClient != null && mqttClient.IsConnected;

    private IMqttClient mqttClient;
    private System.Threading.SynchronizationContext _context;

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);
        
        // 抓取主线程上下文用于 UI 更新
        _context = System.Threading.SynchronizationContext.Current;
        InitializeRobotMap();
    }

    void InitializeRobotMap()
    {
        // 初始化红蓝双方所有可能出现的机器人 ID
        int[] ids = { 1, 2, 3, 4, 5, 6, 7, 101, 102, 103, 104, 105, 106, 107 };
        foreach (int id in ids)
        {
            MapData[id] = new RobotInfo(id);
        }
        MyID = 1; 
        MyRobot = MapData[1];
    }

    // ================== MQTT 连接逻辑 (优化版) ==================

    public async void ConnectToServer()
    {
        string ip = GlobalConfig.CurrentIP;
        int port = GlobalConfig.CurrentPort;

        if (mqttClient != null && mqttClient.IsConnected) await mqttClient.DisconnectAsync();

        var factory = new MqttFactory();
        mqttClient = factory.CreateMqttClient();
        
        var options = new MqttClientOptionsBuilder()
            .WithTcpServer(ip, port)
            .WithClientId("Quest3_Cockpit_" + UnityEngine.Random.Range(1000, 9999))
            .WithTimeout(TimeSpan.FromSeconds(3))
            .WithCleanSession()
            .Build();

        // 连接成功回调
        mqttClient.ConnectedAsync += async e =>
    {
        Debug.Log("<color=green>[DataManager] MQTT TCP Connected.</color>");
        
        // 建议在这里加上一个微小的延迟，或者直接广播
        _context.Post(_ => {
            // 安全分发事件：防止因为某个脚本报错导致整个跳转卡死
            if (OnConnectSuccess != null)
            {
                foreach (Action handler in OnConnectSuccess.GetInvocationList())
                {
                    try {
                        handler.Invoke();
                    } catch (Exception ex) {
                        Debug.LogError($"事件处理程序报错 (可能是图传脚本): {ex.Message}");
                    }
                }
            }
            OnDebugLog?.Invoke($"<color=green>[System] 已建立连接</color>");
        }, null);

        _ = SubscribeTopicsAsync();
        await Task.CompletedTask;
    };

        // 收到消息回调
        mqttClient.ApplicationMessageReceivedAsync += async e =>
        {
            byte[] payload = e.ApplicationMessage.Payload;
            string topic = e.ApplicationMessage.Topic;
            // 将数据包抛回主线程解析
            _context.Post(_ => ParsePacket(topic, payload), null);
            await Task.CompletedTask;
        };

        try 
        { 
            await mqttClient.ConnectAsync(options); 
        }
        catch (Exception ex) 
        { 
            Debug.LogError($"[MQTT Connect Error] {ex.Message}"); 
            _context.Post(_ => {
                OnConnectFail?.Invoke(ex.Message);
                OnDebugLog?.Invoke($"<color=red>[Error] 连接服务器失败: {ex.Message}</color>");
            }, null); 
        }
    }

    private async Task SubscribeTopicsAsync()
    {
        string[] topics = {
            "GameStatus", "GlobalUnitStatus", "GlobalLogisticsStatus", 
            "Event", "RobotStaticStatus", "RobotDynamicStatus", 
            "RobotModuleStatus", "RobotPosition", "RaderInfoToClient"
        };
        
        foreach (var t in topics) 
        {
            try {
                await mqttClient.SubscribeAsync(new MqttTopicFilterBuilder().WithTopic(t).Build());
            } catch (Exception e) {
                Debug.LogWarning($"订阅主题失败 {t}: {e.Message}");
            }
        }
    }

    // ================== 数据解析逻辑 ==================

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
                    OnGameEvent?.Invoke($"比赛事件: {evt.EventId}");
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
                    UpdateMapInfo((int)radar.TargetRobotId, radar.TargetPosX, radar.TargetPosY, radar.TargetAngle, -1);
                    parsedMsg = radar;
                    break;
                
                default:
                    // 未知主题仅记录
                    break;
            }

            // 广播调试日志
            if (parsedMsg != null)
            {
                string logStr = $"<color=cyan>[RX] {topic}</color>: {parsedMsg.ToString().Replace("\n", " ")}";
                OnDebugLog?.Invoke(logStr);
            }
        }
        catch (Exception e) { Debug.LogWarning($"解析错误 {topic}: {e.Message}"); }
    }

    // ================== 内部辅助函数 ==================

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
                // 如果血量为0，在地图上暂时隐藏
                if (MapData[id].currentHp <= 0) MapData[id].isVisible = false;
            }
        }
    }

    void UpdateMapInfo(int id, float x, float y, float angle, int hp)
    {
        if (MapData.ContainsKey(id))
        {
            var r = MapData[id];
            r.pos = new Vector3(x, 0, y);
            r.yaw = angle;
            if (hp != -1) r.currentHp = hp;
            r.isVisible = true;
            r.lastUpdate = Time.time;
        }
    }

    // ================== 指令发送接口 ==================

    public async void SendCommand<T>(string topic, T message) where T : IMessage
    {
        if (mqttClient == null || !mqttClient.IsConnected) return;

        // 屏蔽高频遥控指令日志，防止控制台卡死
        if (topic != "RemoteControl")
        {
            string log = $"<color=yellow>[TX] {topic}</color>: {message.ToString().Replace("\n", " ")}";
            OnDebugLog?.Invoke(log);
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
    
    public void SendRemoteControl(RoboMaster.RemoteControl cmd) => SendCommand("RemoteControl", cmd);

    private async void OnDestroy()
    {
        if (mqttClient != null) await mqttClient.DisconnectAsync();
    }
    
}