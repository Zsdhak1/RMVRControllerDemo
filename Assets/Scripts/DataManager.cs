using UnityEngine;
using MQTTnet;
using MQTTnet.Client;
using System.Threading.Tasks;
using System.Collections.Generic;
using Google.Protobuf;
using RoboMaster; // 假设您的 Protobuf 生成文件在这个命名空间下
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
    public bool isDead; // 新增：是否阵亡
    
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
        this.team = id < 100 ? 0 : 1; // 规则：红方ID<100, 蓝方ID>100
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
    public event Action<string> OnDebugLog; 
    public event Action<string> OnTxLog; 

    // ================== 公开数据 ==================

    [Header("=== 比赛全局 ===")]
    public int MatchTime;       // 剩余秒数
    public int CurrentStage;    // 0=未开始, 4=比赛中, 5=结算
    public bool IsPaused;
    public int RedScore;
    public int BlueScore;
    
    [Header("=== 建筑数据 (己方) ===")]
    public int BaseHP; 
    public int OutpostHP; 
    public bool BaseShieldActive; // 基地是否有护盾

    [Header("=== 建筑数据 (敌方) ===")]
    public int EnemyBaseHP;       // 新增：敌方基地血量
    public int EnemyOutpostHP;    // 新增：敌方前哨站血量
    public bool EnemyBaseShieldActive;

    [Header("=== 己方经济与科技 ===")]
    public int MyGold;
    public int MyTechLevel;       // 科技等级
    
    [Header("=== 工程机器人专属 ===")]
    // 科技核心状态: 0=无, 1=非四级, 2=四级
    public int EnemyCoreStatus;   
    // 己方装配总剩余时长
    public int AssemblyRemainTime;
    
    [Header("=== 自身状态引用 ===")]
    public int MyID = 1; // 默认值，连接 RobotStaticStatus 后会更新
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
        // 初始化所有可能的机器人 ID
        int[] ids = { 1, 2, 3, 4, 5, 6, 7, 101, 102, 103, 104, 105, 106, 107 };
        foreach (int id in ids)
        {
            MapData[id] = new RobotInfo(id);
        }
        
        // 默认将 MyRobot 指向红方英雄，连接后会自动修正
        MyRobot = MapData[1]; 
    }

// ================== 连接逻辑 ==================

    public async void ConnectToServer()
    {
        // 1. 读取你通过 LoginUI 输入并保存在 GlobalConfig 中的 IP 和端口
        string ip = GlobalConfig.CurrentIP;
        int port = GlobalConfig.CurrentPort;

        // 2. 强制清理旧连接，防止内存泄漏或端口占用
        if (mqttClient != null)
        {
            if (mqttClient.IsConnected) 
            {
                try { await mqttClient.DisconnectAsync(); } catch { }
            }
            mqttClient.Dispose();
            mqttClient = null;
        }

        var factory = new MqttFactory();
        mqttClient = factory.CreateMqttClient();
        
        // 3. 解决 "Error while authenticating" 的核心：生成唯一的 ClientID
        string uniqueID = "EngineerVR_" + System.Guid.NewGuid().ToString().Substring(0, 8);

        var options = new MqttClientOptionsBuilder()
            .WithTcpServer(ip, port)              // 使用动态传入的 IP 和端口
            .WithClientId(uniqueID)               // 使用唯一 ID
            .WithCleanSession(true)               // 告诉服务器这是一个全新的连接，不要记忆过去的状态
            .WithTimeout(TimeSpan.FromSeconds(5)) // 稍微延长一点超时时间，防止网络波动导致 Canceled
            .Build();

        mqttClient.ConnectedAsync += async e =>
        {
            string msg = $"<color=green> Connected to {ip}:{port}</color>";
            Debug.Log(msg);
            _context.Post(_ => OnDebugLog?.Invoke(msg), null); // 广播给DebugPanel

            await SubscribeAll();
            
            // 触发成功事件，你的 LoginUI 收到后会自动切换到驾驶舱
            _context.Post(_ => OnConnectSuccess?.Invoke(), null);
        };

        mqttClient.ApplicationMessageReceivedAsync += async e =>
        {
            byte[] payload = e.ApplicationMessage.Payload;
            string topic = e.ApplicationMessage.Topic;
            _context.Post(_ => ParsePacket(topic, payload), null);
            await Task.CompletedTask;
        };

        try 
        { 
            // 发起连接
            await mqttClient.ConnectAsync(options); 
        }
        catch (Exception ex) 
        { 
            // 捕获异常并反馈给 LoginUI
            string errorMsg = ex.Message;
            if (ex.InnerException != null) errorMsg += $" | {ex.InnerException.Message}";
            
            Debug.LogError($" {errorMsg}"); 
            _context.Post(_ => {
                OnConnectFail?.Invoke(errorMsg); // 触发失败事件，LoginUI 会显示红字
                OnDebugLog?.Invoke($"<color=red> {errorMsg}</color>");
            }, null); 
        }
    }

    private async Task SubscribeAll()
    {
        // 根据 V1.2.0 协议订阅必要 Topic
        string[] topics = {
            "GameStatus", 
            "GlobalUnitStatus", 
            "GlobalLogisticsStatus", 
            "Event", 
            "RobotStaticStatus", 
            "RobotDynamicStatus", 
            "RobotModuleStatus", 
            "RobotPosition", 
            "TechCoreMotionStateSync", // 2026 新增：科技核心同步
            "PenaltyInfo"
        };
        foreach (var t in topics) await mqttClient.SubscribeAsync(new MqttTopicFilterBuilder().WithTopic(t).Build());
    }

    // ================== 解析逻辑 ==================

    void ParsePacket(string topic, byte[] data)
    {
        try
        {
            IMessage parsedMsg = null;

            switch (topic)
            {
                case "GameStatus":
                    var game = GameStatus.Parser.ParseFrom(data);
                    // 错误修正：根据PDF Page 52，字段是 stage_countdown_sec 和 current_stage
                    MatchTime = game.StageCountdownSec;  // 原代码用了 StageRemainTime
                    CurrentStage = (int)game.CurrentStage; // 原代码用了 GameProgress
                    RedScore = (int)game.RedScore;
                    BlueScore = (int)game.BlueScore;
                    parsedMsg = game;
                    break;

                case "GlobalUnitStatus":
                    var unit = GlobalUnitStatus.Parser.ParseFrom(data);
                    BaseHP = (int)unit.BaseHealth;
                    OutpostHP = (int)unit.OutpostHealth;
                    BaseShieldActive = unit.BaseShield > 0;
                    
                    // 这里的报错是因为你的 Proto 文件还没重新生成，暂时先注释掉下面3行
                    // 等第三步做完后再解开注释
                    // EnemyBaseHP = (int)unit.EnemyBaseHealth;     
                    // EnemyOutpostHP = (int)unit.EnemyOutpostHealth;
                    // EnemyBaseShieldActive = unit.EnemyBaseShield > 0;

                    UpdateAllHP(unit.RobotHealth);
                    parsedMsg = unit;
                    break;

                case "GlobalLogisticsStatus":
                    var logi = GlobalLogisticsStatus.Parser.ParseFrom(data);
                    MyGold = (int)logi.RemainingEconomy;
                    MyTechLevel = (int)logi.TechLevel; // 科技等级
                    parsedMsg = logi;
                    break;
                
                case "TechCoreMotionStateSync":
                    var tech = TechCoreMotionStateSync.Parser.ParseFrom(data);
                    // 同样，如果 Proto 没更新，这些字段会报错，请先注释
                    // EnemyCoreStatus = (int)tech.EnemyCoreStatus; 
                    // AssemblyRemainTime = (int)tech.RemainTimeAll;
                    parsedMsg = tech;
                    break;

                case "Event":
                    var evt = RoboMaster.Event.Parser.ParseFrom(data);
                    // 可以在这里处理击杀提示
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
                    MyRobot.isDead = MyRobot.currentHp <= 0;
                    MyRobot.currentHeat = dyn.CurrentHeat;
                    MyRobot.currentAmmo = (int)dyn.RemainingAmmo;
                    MyRobot.chassisEnergy = (int)dyn.CurrentChassisEnergy;
                    MyRobot.isOutCombat = dyn.IsOutOfCombat;
                    MyRobot.lastUpdate = Time.time;
                    parsedMsg = dyn;
                    break;

                case "RobotModuleStatus":
                    var mod = RobotModuleStatus.Parser.ParseFrom(data);
                    MyRobot.modChassis = mod.PowerManager == 1; // 假设 1 是在线
                    MyRobot.modShooter = mod.SmallShooter == 1 || mod.BigShooter == 1;
                    MyRobot.modVideo = mod.VideoTransmission == 1;
                    parsedMsg = mod;
                    break;

                case "RobotPosition":
                    var pos = RobotPosition.Parser.ParseFrom(data);
                    MyRobot.pos = new Vector3(pos.X, 0, pos.Y); 
                    // 错误修正：PDF Page 63 字段名为 angle
                    MyRobot.yaw = pos.Yaw; // 如果报错，尝试改为 pos.Yaw
                    parsedMsg = pos;
                    break;
            }

            // 日志输出 (可选，防止 Log 刷屏)
            // if (parsedMsg != null && topic != "RobotPosition" && topic != "RobotDynamicStatus")
            // {
            //     string log = $"<color=cyan>[RX] {topic}</color>: {parsedMsg.ToString().Replace("\n", " ")}";
            //     OnDebugLog?.Invoke(log);
            // }
        }
        catch (Exception e) { Debug.LogWarning($"Parse Error {topic}: {e.Message}"); }
    }

    // 更新所有机器人的血量 (用于小地图和顶部血条)
    void UpdateAllHP(Google.Protobuf.Collections.RepeatedField<uint> hps)
    {
        // 假设协议顺序：前7个是红方(1-7)，后7个是蓝方(101-107)
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
                MapData[id].isDead = MapData[id].currentHp <= 0;
                // 如果血量 > 0 则视为可见 (简化逻辑)
                MapData[id].isVisible = MapData[id].currentHp > 0;
            }
        }
    }

    // ================== 发送指令 (Public Methods) ==================

    // 1. 发送高频遥控指令 (供您的 RobotDataSender 使用)
    // ================== 发送指令 (Public Methods) ==================

    // 【修改后】发送自定义控制数据 (适配 V1.2.0 协议)
    // 对应 RobotDataSender.cs 使用
    public void SendCustomControl(RoboMaster.CustomControl cmd)
    {
        // 话题名称必须严格对应 proto 中的 "CustomControl"
        SendCommand("CustomControl", cmd);
    }

    // 2. 发送工程机器人装配指令 (V1.2.0 AssemblyCommand)
    // operation: 1=确认, 2=取消
    // difficulty: 1-4
    public void SendAssemblyCommand(uint operation, uint difficulty)
    {
        var cmd = new RoboMaster.AssemblyCommand();
        cmd.Operation = operation;
        cmd.Difficulty = difficulty;
        
        string logInfo = operation == 1 ? "Confirm Assembly" : "Cancel Assembly";
        SendCommand("AssemblyCommand", cmd);
        OnTxLog?.Invoke($"[CMD] {logInfo} (Lv.{difficulty})");
    }

    // 3. 发送通用指令 (V1.2.0 CommonCommand)
    // type: 4=立即复活, 6=远程补血
    public void SendCommonCommand(uint cmdType, uint param = 0)
    {
        var cmd = new RoboMaster.CommonCommand();
        cmd.CmdType = cmdType;
        cmd.Param = param;

        SendCommand("CommonCommand", cmd);
        OnTxLog?.Invoke($"[CMD] Common Type:{cmdType} Param:{param}");
    }

    // 通用发送底层
    private async void SendCommand<T>(string topic, T message) where T : IMessage
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