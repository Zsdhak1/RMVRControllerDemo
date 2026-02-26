using UnityEngine;
using MQTTnet;
using MQTTnet.Client;
using System.Threading.Tasks;
using System.Collections.Generic;
using Google.Protobuf;
using RoboMaster; 
using System;

[System.Serializable]
public class RobotInfo
{
    public int id;
    public int team; 
    public int type;
    public int level;
    public int maxHp;
    public int maxHeat;
    public int currentHp;
    public float currentHeat;
    public int currentAmmo;
    public int chassisEnergy;
    public bool isOutCombat;
    public bool isDead;
    public Vector3 pos; 
    public float yaw;
    public bool modChassis;
    public bool modShooter;
    public bool modVideo;
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
    public event Action<string> OnDebugLog; 
    public event Action<string> OnTxLog; 

    // ================== 公开数据 ==================

    [Header("=== 比赛全局 ===")]
    public int MatchTime;       
    public int CurrentStage;    
    public bool IsPaused;
    public int RedScore;
    public int BlueScore;
    
    [Header("=== 建筑数据 ===")]
    public int BaseHP; 
    public int OutpostHP; 
    public bool BaseShieldActive;
    public int EnemyBaseHP;       
    public int EnemyOutpostHP;    
    public bool EnemyBaseShieldActive;

    [Header("=== 己方经济与科技 ===")]
    public int MyGold;
    public int MyTechLevel;       
    
    // 【核心新增：工程装配状态机变量】
    // 对应 TechCoreMotionStateSync 协议包
    public int MaxDifficultyLevel = 1; // 默认一级
    public int TechCoreStatus = 1;     // 默认初始状态
    public int EnemyCoreStatus;   
    public int AssemblyRemainTime;
    
    // 【核心新增：生存与复活变量】
    // 对应 RobotRespawnStatus（如果协议有单发）或 SentryInfo 里面的状态
    public bool IsPendingRespawn;     // 是否处于战亡读条中
    public bool CanFreeRespawn;       // 是否可确认复活（读条完毕）
    public bool CanPayForRespawn;     // 是否允许买活
    public int GoldCostForRespawn;    // 买活需要多少钱
    
    [Header("=== 自身状态引用 ===")]
    public int MyID = 1; 
    public RobotInfo MyRobot; 

    [Header("=== 全场机器人 ===")]
    public Dictionary<int, RobotInfo> MapData = new Dictionary<int, RobotInfo>();

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
        foreach (int id in ids) MapData[id] = new RobotInfo(id);
        MyRobot = MapData[1]; 
    }

    public async void ConnectToServer()
    {
        string ip = GlobalConfig.CurrentIP;
        int port = GlobalConfig.CurrentPort;

        if (mqttClient != null)
        {
            if (mqttClient.IsConnected) try { await mqttClient.DisconnectAsync(); } catch { }
            mqttClient.Dispose();
            mqttClient = null;
        }

        var factory = new MqttFactory();
        mqttClient = factory.CreateMqttClient();
        
        string uniqueID = "EngineerVR_" + System.Guid.NewGuid().ToString().Substring(0, 8);

        var options = new MqttClientOptionsBuilder()
            .WithTcpServer(ip, port)
            .WithClientId(uniqueID)
            .WithCleanSession(true)
            .WithTimeout(TimeSpan.FromSeconds(5))
            .Build();

        mqttClient.ConnectedAsync += async e =>
        {
            string msg = $"<color=green> Connected to {ip}:{port}</color>";
            Debug.Log(msg);
            _context.Post(_ => OnDebugLog?.Invoke(msg), null); 
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

        try 
        { 
            await mqttClient.ConnectAsync(options); 
        }
        catch (Exception ex) 
        { 
            string errorMsg = ex.Message;
            if (ex.InnerException != null) errorMsg += $" | {ex.InnerException.Message}";
            Debug.LogError($" {errorMsg}"); 
            _context.Post(_ => {
                OnConnectFail?.Invoke(errorMsg); 
                OnDebugLog?.Invoke($"<color=red> {errorMsg}</color>");
            }, null); 
        }
    }

    private async Task SubscribeAll()
    {
        string[] topics = {
            "GameStatus", 
            "GlobalUnitStatus", 
            "GlobalLogisticsStatus", 
            "Event", 
            "RobotStaticStatus", 
            "RobotDynamicStatus", 
            "RobotModuleStatus", 
            "RobotPosition", 
            "TechCoreMotionStateSync", 
            "SentryInfo", // 哨兵信息包里通常包含复活金币数，具体看协议文档
            "RobotRespawnStatus" // 如果有独立的复活包
        };
        foreach (var t in topics) await mqttClient.SubscribeAsync(new MqttTopicFilterBuilder().WithTopic(t).Build());
    }

    public bool IsMqttConnected => mqttClient != null && mqttClient.IsConnected;

    public async void ReconnectToServer()
    {
        if (mqttClient != null)
        {
            if (mqttClient.IsConnected)
            {
                try
                {
                    // 修复枚举引用错误
                    var disconnectOptions = new MqttClientDisconnectOptionsBuilder()
                        .WithReason(MqttClientDisconnectOptionsReason.NormalDisconnection)
                        .Build();
                    await mqttClient.DisconnectAsync(disconnectOptions);
                }
                catch { }
            }
            mqttClient.Dispose();
            mqttClient = null;
        }
        CurrentStage = 0;
        MatchTime = 0;
        GlobalConfig.LoadConfig(); 
        ConnectToServer(); 
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
                    RedScore = (int)game.RedScore;
                    BlueScore = (int)game.BlueScore;
                    break;

                case "GlobalUnitStatus":
                    var unit = GlobalUnitStatus.Parser.ParseFrom(data);
                    BaseHP = (int)unit.BaseHealth;
                    OutpostHP = (int)unit.OutpostHealth;
                    BaseShieldActive = unit.BaseShield > 0;
                    // 如果你的proto已更新，这里解开即可
                    // EnemyBaseHP = (int)unit.EnemyBaseHealth;     
                    // EnemyOutpostHP = (int)unit.EnemyOutpostHealth;
                    UpdateAllHP(unit.RobotHealth);
                    break;

                case "GlobalLogisticsStatus":
                    var logi = GlobalLogisticsStatus.Parser.ParseFrom(data);
                    MyGold = (int)logi.RemainingEconomy;
                    MyTechLevel = (int)logi.TechLevel; 
                    break;
                
                // 【核心修复：TechCore 状态同步】
                case "TechCoreMotionStateSync":
                    var tech = TechCoreMotionStateSync.Parser.ParseFrom(data);
                    // 填入刚才声明的公共变量！
                    MaxDifficultyLevel = (int)tech.MaximumDifficultyLevel; 
                    TechCoreStatus = (int)tech.Status; 
                    // EnemyCoreStatus = (int)tech.EnemyCoreStatus; // 暂注
                    // AssemblyRemainTime = (int)tech.RemainTimeAll; // 暂注
                    break;

                // 【核心修复：复活与生存同步】
                // 假设协议中存在 RobotRespawnStatus 这个包
                case "RobotRespawnStatus":
                    var respawn = RobotRespawnStatus.Parser.ParseFrom(data);
                    IsPendingRespawn = respawn.IsPendingRespawn;
                    CanFreeRespawn = respawn.CanFreeRespawn;
                    CanPayForRespawn = respawn.CanPayForRespawn;
                    GoldCostForRespawn = (int)respawn.GoldCostForRespawn;
                    break;

                case "RobotStaticStatus":
                    var stat = RobotStaticStatus.Parser.ParseFrom(data);
                    MyID = (int)stat.RobotId;
                    if(MapData.ContainsKey(MyID)) MyRobot = MapData[MyID];
                    MyRobot.maxHp = (int)stat.MaxHealth;
                    MyRobot.maxHeat = (int)stat.MaxHeat;
                    break;

                case "RobotDynamicStatus":
                    var dyn = RobotDynamicStatus.Parser.ParseFrom(data);
                    MyRobot.currentHp = (int)dyn.CurrentHealth;
                    MyRobot.isDead = MyRobot.currentHp <= 0;
                    
                    // 如果没有独立的复活包，可以用这个简单判定
                    if (MyRobot.isDead) IsPendingRespawn = true; 
                    else IsPendingRespawn = false;

                    MyRobot.currentHeat = dyn.CurrentHeat;
                    MyRobot.currentAmmo = (int)dyn.RemainingAmmo;
                    MyRobot.chassisEnergy = (int)dyn.CurrentChassisEnergy;
                    MyRobot.isOutCombat = dyn.IsOutOfCombat;
                    break;

                case "RobotModuleStatus":
                    var mod = RobotModuleStatus.Parser.ParseFrom(data);
                    MyRobot.modChassis = mod.PowerManager == 1; 
                    MyRobot.modVideo = mod.VideoTransmission == 1;
                    break;

                case "RobotPosition":
                    var pos = RobotPosition.Parser.ParseFrom(data);
                    MyRobot.pos = new Vector3(pos.X, 0, pos.Y); 
                    MyRobot.yaw = pos.Yaw; 
                    break;
            }
        }
        catch (Exception e) { Debug.LogWarning($"Parse Error {topic}: {e.Message}"); }
    }

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
                MapData[id].isDead = MapData[id].currentHp <= 0;
                MapData[id].isVisible = MapData[id].currentHp > 0;
            }
        }
    }

    public void SendCustomControl(RoboMaster.CustomControl cmd)
    {
        SendCommand("CustomControl", cmd);
    }

    public void SendAssemblyCommand(uint operation, uint difficulty)
    {
        var cmd = new RoboMaster.AssemblyCommand();
        cmd.Operation = operation;
        cmd.Difficulty = difficulty;
        SendCommand("AssemblyCommand", cmd);
        OnTxLog?.Invoke($"[CMD] Assembly Op:{operation}");
    }

    public void SendCommonCommand(uint cmdType, uint param = 0)
    {
        var cmd = new RoboMaster.CommonCommand();
        cmd.CmdType = cmdType;
        cmd.Param = param;
        SendCommand("CommonCommand", cmd);
        OnTxLog?.Invoke($"[CMD] Common Type:{cmdType}");
    }

    public async void SendKeyboardMouseControl(KeyboardMouseControl kmc)
    {
        if (mqttClient == null || !mqttClient.IsConnected) return;
        try
        {
            byte[] payload = kmc.ToByteArray();
            var message = new MqttApplicationMessageBuilder()
                .WithTopic("KeyboardMouseControl")  
                .WithPayload(payload)
                .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
                .Build();
            await mqttClient.PublishAsync(message);
        }
        catch { }
    }

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