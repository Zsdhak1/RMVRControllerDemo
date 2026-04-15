using UnityEngine;
using MQTTnet;
using MQTTnet.Client;
using System.Threading.Tasks;
using System.Collections.Generic;
using Google.Protobuf;
using RoboMaster; 
using System;
using System.Linq;
using System.Text;

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
    public bool IsDataStale => Time.time - lastUpdate > 3.0f;
    
    public RobotInfo(int id) 
    { 
        this.id = id; 
        this.team = id < 100 ? 0 : 1; 
        this.maxHp = 100; 
        this.lastUpdate = -999f;
    }
}

[System.Serializable]
public class BuffInfo
{
    public uint robotId;
    public uint buffType;
    public int buffLevel;
    public uint buffMaxTime;
    public uint buffLeftTime;
    public float receiveTime;
    public bool IsStale => Time.time - receiveTime > 5.0f;
}

[System.Serializable]
public class InjuryStat
{
    public uint totalDamage;
    public uint collisionDamage;
    public uint smallProjectileDamage;
    public uint largeProjectileDamage;
    public uint dartSplashDamage;
    public uint moduleOfflineDamage;
    public uint offlineDamage;
    public uint penaltyDamage;
    public uint serverKillDamage;
    public uint killerId;
    public float lastUpdate;
    public bool IsStale => Time.time - lastUpdate > 3.0f;
}

[System.Serializable]
public class SpecialMechanism
{
    public uint mechanismId;
    public int mechanismTimeSec;
}

[System.Serializable]
public class RadarTargetInfo
{
    public uint targetRobotId;
    public Vector2 targetPos;
    public float towardAngle;
    public uint isHighLight;
    public float lastUpdate;
    public bool IsStale => Time.time - lastUpdate > 5.0f;
}

[System.Serializable]
public class TopicDataQuality
{
    public string topicName;
    public float lastReceiveTime;
    public int expectedHz;
    public int receiveCount;
    public int lostCount;
    public float averageLatency;
    public bool isStale => Time.time - lastReceiveTime > (1.5f / Mathf.Max(expectedHz, 1)) + 1.0f;
    public float staleSeconds => Time.time - lastReceiveTime;
    public string StatusText => isStale ? $"[过期{staleSeconds:F1}s]" : $"[正常 {(receiveCount > 0 ? receiveCount.ToString() : "未收")}]";
}

public class DataManager : MonoBehaviour
{
    public static DataManager Instance;

    public event Action OnConnectSuccess; 
    public event Action<string> OnConnectFail; 
    public event Action<string> OnGameEvent;
    public event Action<string> OnDebugLog; 
    public event Action<string> OnTxLog;
    public event Action<string> OnDataStale;

    [Header("=== 比赛全局 ===")]
    public int MatchTime;
    public int CurrentStage;
    public bool IsPaused;
    public int RedScore;
    public int BlueScore;
    public uint CurrentRound;
    public uint TotalRounds;
    public int StageElapsedSec;
    public float GameStatusLastUpdate = -999f;
    public bool IsGameStatusStale => Time.time - GameStatusLastUpdate > 1.0f;

    [Header("=== 建筑数据 ===")]
    public int BaseHP;
    public int OutpostHP;
    public uint BaseStatus;
    public uint OutpostStatus;
    public uint BaseShield;
    public int EnemyBaseHP;
    public int EnemyOutpostHP;
    public uint EnemyBaseStatus;
    public uint EnemyOutpostStatus;
    public uint EnemyBaseShield;
    public bool BaseShieldActive;
    public bool EnemyBaseShieldActive;
    public uint TotalDamageAlly;
    public uint TotalDamageEnemy;
    public float GlobalUnitStatusLastUpdate = -999f;
    public bool IsGlobalUnitStatusStale => Time.time - GlobalUnitStatusLastUpdate > 2.0f;

    [Header("=== 己方经济与科技 ===")]
    public int MyGold;
    public ulong TotalEconomyObtained;
    public int MyTechLevel;
    public uint EncryptionLevel;
    public float GlobalLogisticsStatusLastUpdate = -999f;
    public bool IsGlobalLogisticsStatusStale => Time.time - GlobalLogisticsStatusLastUpdate > 2.0f;

    [Header("=== 工程装配状态 ===")]
    public int MaxDifficultyLevel = 1;
    public int TechCoreStatus = 1;
    public int EnemyCoreStatus;
    public uint AssemblyRemainTimeAll;
    public uint AssemblyRemainTimeStep;
    public float TechCoreLastUpdate = -999f;
    public bool IsTechCoreStale => Time.time - TechCoreLastUpdate > 2.0f;

    [Header("=== 复活状态 ===")]
    public bool IsPendingRespawn;
    public bool CanFreeRespawn;
    public bool CanPayForRespawn;
    public uint GoldCostForRespawn;
    public uint TotalRespawnProgress;
    public uint CurrentRespawnProgress;
    public float RespawnStatusLastUpdate = -999f;
    public bool IsRespawnStatusStale => Time.time - RespawnStatusLastUpdate > 2.0f;

    [Header("=== 自身状态 ===")]
    public int MyID = 1;
    public RobotInfo MyRobot;

    [Header("=== 全场机器人 ===")]
    public Dictionary<int, RobotInfo> MapData = new Dictionary<int, RobotInfo>();
    public List<int> RobotBullets = new List<int>();

    [Header("=== 机器人静态属性 ===")]
    public uint ConnectionState;
    public uint FieldState;
    public uint AliveState;
    public uint RobotType;
    public uint PerformanceSystemShooter;
    public uint PerformanceSystemChassis;
    public float HeatCooldownRate;
    public uint MaxPower;
    public uint MaxBufferEnergy;
    public uint MaxChassisEnergy;
    public float StaticStatusLastUpdate = -999f;
    public bool IsStaticStatusStale => Time.time - StaticStatusLastUpdate > 2.0f;

    [Header("=== 机器人动态属性 ===")]
    public float LastProjectileFireRate;
    public uint CurrentExperience;
    public uint ExperienceForUpgrade;
    public uint TotalProjectilesFired;
    public uint OutOfCombatCountdown;
    public bool CanRemoteHeal;
    public bool CanRemoteAmmo;
    public float DynamicStatusLastUpdate = -999f;
    public bool IsDynamicStatusStale => Time.time - DynamicStatusLastUpdate > 0.5f;

    [Header("=== 模块状态 ===")]
    public uint PowerManagerStatus;
    public uint RfidStatus;
    public uint LightStripStatus;
    public uint SmallShooterStatus;
    public uint BigShooterStatus;
    public uint UwbStatus;
    public uint ArmorStatus;
    public uint VideoTransmissionStatus;
    public uint CapacitorStatus;
    public uint MainControllerStatus;
    public uint LaserDetectionModuleStatus;
    public float ModuleStatusLastUpdate = -999f;
    public bool IsModuleStatusStale => Time.time - ModuleStatusLastUpdate > 3.0f;

    [Header("=== 位置数据 ===")]
    public float PositionLastUpdate = -999f;
    public bool IsPositionStale => Time.time - PositionLastUpdate > 3.0f;

    [Header("=== 全局特殊机制 ===")]
    public List<SpecialMechanism> ActiveMechanisms = new List<SpecialMechanism>();
    public float SpecialMechanismLastUpdate = -999f;
    public bool IsSpecialMechanismStale => Time.time - SpecialMechanismLastUpdate > 2.0f;

    [Header("=== 受伤统计 ===")]
    public InjuryStat MyInjuryStat = new InjuryStat();

    [Header("=== Buff信息 ===")]
    public List<BuffInfo> ActiveBuffs = new List<BuffInfo>();

    [Header("=== 判罚信息 ===")]
    public uint PenaltyType;
    public uint PenaltyEffectSec;
    public uint TotalPenaltyNum;
    public float PenaltyLastUpdate = -999f;
    public bool IsPenaltyStale => Time.time - PenaltyLastUpdate > 10.0f;

    [Header("=== 哨兵轨迹规划 ===")]
    public uint SentryIntention;
    public uint SentryPathStartX;
    public uint SentryPathStartY;
    public List<int> SentryPathOffsetX = new List<int>();
    public List<int> SentryPathOffsetY = new List<int>();
    public uint SentryPathSenderId;
    public float PathPlanLastUpdate = -999f;
    public bool IsPathPlanStale => Time.time - PathPlanLastUpdate > 3.0f;

    [Header("=== 雷达目标信息 ===")]
    public List<RadarTargetInfo> RadarTargets = new List<RadarTargetInfo>();

    [Header("=== 性能体系状态 ===")]
    public uint CurrentShooterPerformance;
    public uint CurrentChassisPerformance;
    public uint SentryControlMode;
    public float PerformanceSelectionLastUpdate = -999f;
    public bool IsPerformanceSelectionStale => Time.time - PerformanceSelectionLastUpdate > 2.0f;

    [Header("=== 部署模式状态 ===")]
    public uint DeployModeStatus;
    public float DeployModeLastUpdate = -999f;
    public bool IsDeployModeStale => Time.time - DeployModeLastUpdate > 2.0f;

    [Header("=== 能量机关状态 ===")]
    public uint RuneStatus;
    public uint RuneActivatedArms;
    public uint RuneAverageRings;
    public float RuneLastUpdate = -999f;
    public bool IsRuneStale => Time.time - RuneLastUpdate > 2.0f;

    [Header("=== 哨兵姿态状态 ===")]
    public uint SentryPostureId;
    public bool SentryIsWeakened;
    public float SentryStatusLastUpdate = -999f;
    public bool IsSentryStatusStale => Time.time - SentryStatusLastUpdate > 2.0f;

    [Header("=== 飞镖目标状态 ===")]
    public uint DartTargetId;
    public uint DartOpenStatus;
    public float DartLastUpdate = -999f;
    public bool IsDartStale => Time.time - DartLastUpdate > 2.0f;

    [Header("=== 哨兵控制结果 ===")]
    public uint LastSentryCommandId;
    public uint LastSentryCommandResult;

    [Header("=== 空中支援状态 ===")]
    public uint AirSupportStatus;
    public uint AirSupportLeftTime;
    public uint AirSupportCostCoins;
    public uint IsBeingTargeted;
    public uint AirSupportShooterStatus;
    public float AirSupportLastUpdate = -999f;
    public bool IsAirSupportStale => Time.time - AirSupportLastUpdate > 2.0f;

    [Header("=== 自定义数据流 ===")]
    public byte[] CustomByteBlockData;
    public float CustomByteBlockLastUpdate = -999f;

    [Header("=== 数据质量监控 ===")]
    public Dictionary<string, TopicDataQuality> DataQuality = new Dictionary<string, TopicDataQuality>();
    public float ConnectionQuality = 1.0f;
    public int TotalPacketsReceived = 0;
    public int TotalPacketsLost = 0;
    
    [Header("=== 调试显示 ===")]
    [Tooltip("实时更新的调试信息字符串，可直接绑定到TMP Text")]
    public string DebugDataString;
    public bool EnableDebugStringUpdate = true;
    public int DebugUpdateInterval = 10; // 每10帧更新一次
    private int frameCount = 0;

    [Header("=== 超时配置 ===")]
    public float ModuleStatusTimeout = 3.0f;
    public float PositionTimeout = 3.0f;
    public float BuffTimeout = 5.0f;
    public float RadarTargetTimeout = 5.0f;

    private IMqttClient mqttClient;
    private System.Threading.SynchronizationContext _context;
    private float lastConnectionCheck = 0;
    private float connectionCheckInterval = 1.0f;
    private StringBuilder debugBuilder = new StringBuilder(4096);

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);
        _context = System.Threading.SynchronizationContext.Current;
        InitializeData();
        InitializeDataQuality();
    }

    void InitializeData()
    {
        int[] ids = { 1, 2, 3, 4, 5, 6, 7, 101, 102, 103, 104, 105, 106, 107 };
        foreach (int id in ids) MapData[id] = new RobotInfo(id);
        MyRobot = MapData[1];
    }

    void InitializeDataQuality()
    {
        var topicConfigs = new Dictionary<string, int>
        {
            { "GameStatus", 5 },
            { "GlobalUnitStatus", 1 },
            { "GlobalLogisticsStatus", 1 },
            { "GlobalSpecialMechanism", 1 },
            { "Event", 0 },
            { "RobotInjuryStat", 1 },
            { "RobotRespawnStatus", 1 },
            { "RobotStaticStatus", 1 },
            { "RobotDynamicStatus", 10 },
            { "RobotModuleStatus", 1 },
            { "RobotPosition", 1 },
            { "Buff", 1 },
            { "PenaltyInfo", 0 },
            { "RobotPathPlanInfo", 1 },
            { "RadarInfoToClient", 1 },
            { "CustomByteBlock", 50 },
            { "TechCoreMotionStateSync", 1 },
            { "RobotPerformanceSelectionSync", 1 },
            { "DeployModeStatusSync", 1 },
            { "RuneStatusSync", 1 },
            { "SentryStatusSync", 1 },
            { "DartSelectTargetStatusSync", 1 },
            { "SentryCtrlResult", 1 },
            { "AirSupportStatusSync", 1 }
        };

        foreach (var config in topicConfigs)
        {
            DataQuality[config.Key] = new TopicDataQuality
            {
                topicName = config.Key,
                expectedHz = config.Value,
                lastReceiveTime = -999f
            };
        }
    }

    void Update()
    {
        if (Time.time - lastConnectionCheck > connectionCheckInterval)
        {
            CheckDataFreshness();
            lastConnectionCheck = Time.time;
        }

        if (EnableDebugStringUpdate)
        {
            frameCount++;
            if (frameCount >= DebugUpdateInterval)
            {
                UpdateDebugString();
                frameCount = 0;
            }
        }
    }

    /// <summary>
    /// 生成格式化调试字符串，可直接显示在TMP Text中
    /// </summary>
    public void UpdateDebugString()
    {
        debugBuilder.Clear();
        float now = Time.time;

        debugBuilder.AppendLine("=== 数据调试面板 ===");
        debugBuilder.AppendLine($"连接质量: {ConnectionQuality:P0} | 总接收: {TotalPacketsReceived}");
        debugBuilder.AppendLine();

        // 比赛状态
        AppendSection(debugBuilder, "【比赛状态】", IsGameStatusStale);
        debugBuilder.AppendLine($"  阶段: {CurrentStage} {(IsPaused ? "[暂停]" : "")} {(IsGameStatusStale ? "[过期]" : "")}");
        debugBuilder.AppendLine($"  时间: {MatchTime}s / 已过{StageElapsedSec}s");
        debugBuilder.AppendLine($"  比分: 红{RedScore} - 蓝{BlueScore} | 局{CurrentRound}/{TotalRounds}");
        debugBuilder.AppendLine();

        // 建筑数据
        AppendSection(debugBuilder, "【建筑数据】", IsGlobalUnitStatusStale);
        debugBuilder.AppendLine($"  己方: 基地{BaseHP}({BaseStatus}) 前哨{OutpostHP}({OutpostStatus})");
        debugBuilder.AppendLine($"        护盾{(BaseShieldActive ? "开" : "关")} 总伤{TotalDamageAlly}");
        debugBuilder.AppendLine($"  敌方: 基地{EnemyBaseHP} 前哨{EnemyOutpostHP} 总伤{TotalDamageEnemy}");
        debugBuilder.AppendLine();

        // 经济与科技
        AppendSection(debugBuilder, "【经济科技】", IsGlobalLogisticsStatusStale);
        debugBuilder.AppendLine($"  金币: {MyGold} / 累计{TotalEconomyObtained}");
        debugBuilder.AppendLine($"  科技等级: {MyTechLevel} | 加密等级: {EncryptionLevel}");
        debugBuilder.AppendLine();

        // 自身机器人
        AppendSection(debugBuilder, "【自身状态】", MyRobot?.IsDataStale ?? true);
        if (MyRobot != null)
        {
            debugBuilder.AppendLine($"  ID: {MyID} | 类型: {RobotType} | 存活: {(MyRobot.isDead ? "死亡" : "存活")}");
            debugBuilder.AppendLine($"  HP: {MyRobot.currentHp}/{MyRobot.maxHp} | 热量: {MyRobot.currentHeat:F1}/{MyRobot.maxHeat}");
            debugBuilder.AppendLine($"  弹药: {MyRobot.currentAmmo} | 底盘能量: {MyRobot.chassisEnergy}");
            debugBuilder.AppendLine($"  位置: ({MyRobot.pos.x:F2}, {MyRobot.pos.z:F2}) Yaw:{MyRobot.yaw:F1}° {(IsPositionStale ? "[过期]" : "")}");
            debugBuilder.AppendLine($"  脱战: {(MyRobot.isOutCombat ? "是" : "否")} 倒计时{OutOfCombatCountdown}s");
        }
        debugBuilder.AppendLine();

        // 复活状态
        AppendSection(debugBuilder, "【复活状态】", IsRespawnStatusStale);
        debugBuilder.AppendLine($"  待复活: {(IsPendingRespawn ? "是" : "否")} | 可免费: {(CanFreeRespawn ? "是" : "否")}");
        debugBuilder.AppendLine($"  进度: {CurrentRespawnProgress}/{TotalRespawnProgress} | 买活: {(CanPayForRespawn ? $"{GoldCostForRespawn}金币" : "不可")}");
        debugBuilder.AppendLine();

        // 工程装配
        AppendSection(debugBuilder, "【工程装配】", IsTechCoreStale);
        debugBuilder.AppendLine($"  最高难度: {MaxDifficultyLevel} | 状态: {TechCoreStatus}");
        debugBuilder.AppendLine($"  敌方状态: {EnemyCoreStatus} | 剩余: {AssemblyRemainTimeAll}s/{AssemblyRemainTimeStep}s");
        debugBuilder.AppendLine();

        // 模块状态
        AppendSection(debugBuilder, "【模块状态】", IsModuleStatusStale);
        debugBuilder.AppendLine($"  电源:{StatusString(PowerManagerStatus)} 主控:{StatusString(MainControllerStatus)} 图传:{StatusString(VideoTransmissionStatus)}");
        debugBuilder.AppendLine($"  装甲:{StatusString(ArmorStatus)} 电容:{StatusString(CapacitorStatus)} UWB:{StatusString(UwbStatus)}");
        debugBuilder.AppendLine($"  小发射:{StatusString(SmallShooterStatus)} 大发射:{StatusString(BigShooterStatus)} 激光:{StatusString(LaserDetectionModuleStatus)}");
        debugBuilder.AppendLine();

        // 性能体系
        AppendSection(debugBuilder, "【性能体系】", IsPerformanceSelectionStale);
        debugBuilder.AppendLine($"  发射机构: {PerformanceSystemShooter} | 底盘: {PerformanceSystemChassis} | 哨兵控制: {SentryControlMode}");
        debugBuilder.AppendLine();

        // 能量机关
        AppendSection(debugBuilder, "【能量机关】", IsRuneStale);
        debugBuilder.AppendLine($"  状态: {RuneStatus} | 已激活: {RuneActivatedArms}臂 | 平均: {RuneAverageRings}环");
        debugBuilder.AppendLine();

        // 哨兵状态
        AppendSection(debugBuilder, "【哨兵状态】", IsSentryStatusStale);
        debugBuilder.AppendLine($"  姿态: {SentryPostureId} | 弱化: {(SentryIsWeakened ? "是" : "否")}");
        if (!IsPathPlanStale)
        {
            debugBuilder.AppendLine($"  意图: {SentryIntention} | 路径点: {SentryPathOffsetX.Count}");
        }
        debugBuilder.AppendLine();

        // 飞镖状态
        AppendSection(debugBuilder, "【飞镖状态】", IsDartStale);
        debugBuilder.AppendLine($"  目标: {DartTargetId} | 闸门: {DartOpenStatus}");
        debugBuilder.AppendLine();

        // 空中支援
        AppendSection(debugBuilder, "【空中支援】", IsAirSupportStale);
        debugBuilder.AppendLine($"  状态: {AirSupportStatus} | 剩余: {AirSupportLeftTime}s | 花费: {AirSupportCostCoins}");
        debugBuilder.AppendLine($"  被照射: {(IsBeingTargeted > 0 ? "是" : "否")} | 发射机构: {(AirSupportShooterStatus > 0 ? "正常" : "被锁定")}");
        debugBuilder.AppendLine();

        // Buff信息
        AppendSection(debugBuilder, "【Buff信息】", false);
        var activeBuffs = ActiveBuffs.Where(b => !b.IsStale).Take(5).ToList();
        if (activeBuffs.Any())
        {
            foreach (var buff in activeBuffs)
            {
                string buffName = BuffTypeName(buff.buffType);
                debugBuilder.AppendLine($"  [{buff.robotId}] {buffName} Lv{buff.buffLevel} 剩余{buff.buffLeftTime}/{buff.buffMaxTime}s");
            }
        }
        else
        {
            debugBuilder.AppendLine("  无活跃Buff");
        }
        debugBuilder.AppendLine();

        // 雷达目标
        AppendSection(debugBuilder, "【雷达目标】", false);
        var activeTargets = RadarTargets.Where(t => !t.IsStale).ToList();
        if (activeTargets.Any())
        {
            foreach (var target in activeTargets.Take(5))
            {
                debugBuilder.AppendLine($"  [{target.targetRobotId}] ({target.targetPos.x:F1}, {target.targetPos.y:F1}) {(target.isHighLight > 0 ? "★" : "")}");
            }
        }
        else
        {
            debugBuilder.AppendLine("  无雷达目标");
        }
        debugBuilder.AppendLine();

        // 受伤统计
        AppendSection(debugBuilder, "【受伤统计】", MyInjuryStat.IsStale);
        if (MyInjuryStat.totalDamage > 0)
        {
            debugBuilder.AppendLine($"  总计: {MyInjuryStat.totalDamage} | 17mm:{MyInjuryStat.smallProjectileDamage} 42mm:{MyInjuryStat.largeProjectileDamage}");
            debugBuilder.AppendLine($"  撞击:{MyInjuryStat.collisionDamage} 飞镖:{MyInjuryStat.dartSplashDamage} 离线:{MyInjuryStat.offlineDamage}");
            debugBuilder.AppendLine($"  判罚:{MyInjuryStat.penaltyDamage} 击杀者:[{MyInjuryStat.killerId}]");
        }
        else
        {
            debugBuilder.AppendLine("  本轮无受伤");
        }
        debugBuilder.AppendLine();

        // 判罚信息
        AppendSection(debugBuilder, "【判罚信息】", IsPenaltyStale);
        if (PenaltyType > 0)
        {
            debugBuilder.AppendLine($"  类型: {PenaltyType} | 时长: {PenaltyEffectSec}s | 累计: {TotalPenaltyNum}");
        }
        else
        {
            debugBuilder.AppendLine("  无当前判罚");
        }
        debugBuilder.AppendLine();

        // 特殊机制
        AppendSection(debugBuilder, "【特殊机制】", IsSpecialMechanismStale);
        if (ActiveMechanisms.Any())
        {
            foreach (var mech in ActiveMechanisms.Take(3))
            {
                debugBuilder.AppendLine($"  机制{mech.mechanismId}: {mech.mechanismTimeSec}s");
            }
        }
        else
        {
            debugBuilder.AppendLine("  无活跃机制");
        }
        debugBuilder.AppendLine();

        // 数据质量总览
        debugBuilder.AppendLine("=== Topic质量总览 ===");
        var staleTopics = DataQuality.Where(q => q.Value.isStale && q.Value.receiveCount > 0).Take(5);
        foreach (var topic in staleTopics)
        {
            debugBuilder.AppendLine($"⚠ {topic.Key}: {topic.Value.staleSeconds:F1}s");
        }
        if (!staleTopics.Any())
        {
            debugBuilder.AppendLine("✓ 所有数据正常");
        }

        DebugDataString = debugBuilder.ToString();
    }

    void AppendSection(StringBuilder sb, string title, bool isStale)
    {
        if (isStale)
            sb.AppendLine($"<color=grey>{title}[过期]</color>");
        else
            sb.AppendLine(title);
    }

    string StatusString(uint status)
    {
        return status switch
        {
            0 => "<color=red>离</color>",
            1 => "<color=green>在</color>",
            2 => "<color=yellow>异</color>",
            3 => "<color=grey>?</color>",
            _ => "?"
        };
    }

    string BuffTypeName(uint type)
    {
        return type switch
        {
            1 => "攻击",
            2 => "防御",
            3 => "冷却",
            4 => "功率",
            5 => "回血",
            6 => "兑弹",
            7 => "地形",
            _ => $"Buff{type}"
        };
    }

    void CheckDataFreshness()
    {
        float now = Time.time;
        int staleCount = 0;
        int activeCount = 0;

        foreach (var quality in DataQuality.Values)
        {
            if (quality.lastReceiveTime < 0) continue;

            float elapsed = now - quality.lastReceiveTime;
            float threshold = quality.expectedHz > 0 ? (2.0f / quality.expectedHz) + 1.0f : 10.0f;

            if (elapsed > threshold)
            {
                quality.lostCount++;
                staleCount++;
                HandleStaleData(quality.topicName, elapsed);
            }
            else
            {
                activeCount++;
            }
        }

        int total = staleCount + activeCount;
        if (total > 0) ConnectionQuality = (float)activeCount / total;

        if (ConnectionQuality < 0.5f && IsMqttConnected)
        {
            OnDataStale?.Invoke($"连接质量差: {ConnectionQuality:P0}");
        }
    }

    void HandleStaleData(string topic, float staleSeconds)
    {
        switch (topic)
        {
            case "RobotModuleStatus":
                if (Time.time - ModuleStatusLastUpdate > ModuleStatusTimeout)
                {
                    PowerManagerStatus = 3;
                    ArmorStatus = 3;
                    VideoTransmissionStatus = 3;
                }
                break;

            case "RobotPosition":
                if (MyRobot != null && Time.time - PositionLastUpdate > PositionTimeout)
                {
                    MyRobot.isVisible = false;
                }
                break;

            case "Buff":
                ActiveBuffs.RemoveAll(b => Time.time - b.receiveTime > BuffTimeout);
                break;

            case "RadarInfoToClient":
                RadarTargets.RemoveAll(t => Time.time - t.lastUpdate > RadarTargetTimeout);
                break;

            case "RobotDynamicStatus":
                OnDataStale?.Invoke("动态数据过期");
                break;
        }
    }

    void UpdateDataQuality(string topic)
    {
        if (DataQuality.ContainsKey(topic))
        {
            var quality = DataQuality[topic];
            quality.receiveCount++;
            quality.lastReceiveTime = Time.time;
            TotalPacketsReceived++;
        }
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

        string clientId = GlobalConfig.CurrentRobotID;
        if (int.TryParse(clientId, out int parsedRobotId))
        {
            MyID = parsedRobotId;
        }

        var options = new MqttClientOptionsBuilder()
            .WithTcpServer(ip, port)
            .WithClientId(clientId)
            .WithCleanSession(true)
            .WithTimeout(TimeSpan.FromSeconds(5))
            .Build();

        mqttClient.ConnectedAsync += async e =>
        {
            string msg = $"<color=green> Connected to {ip}:{port} as Robot {clientId}</color>";
            Debug.Log(msg);
            _context.Post(_ => OnDebugLog?.Invoke(msg), null);
            await SubscribeAll();
            _context.Post(_ => OnConnectSuccess?.Invoke(), null);
        };

        mqttClient.ApplicationMessageReceivedAsync += async e =>
        {
            byte[] payload = e.ApplicationMessage.Payload;
            string topic = e.ApplicationMessage.Topic;
            _context.Post(_ => {
                UpdateDataQuality(topic);
                ParsePacket(topic, payload);
            }, null);
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
            "GameStatus", "GlobalUnitStatus", "GlobalLogisticsStatus", "GlobalSpecialMechanism",
            "Event", "RobotInjuryStat", "RobotRespawnStatus", "RobotStaticStatus",
            "RobotDynamicStatus", "RobotModuleStatus", "RobotPosition", "Buff",
            "PenaltyInfo", "RobotPathPlanInfo", "RadarInfoToClient", "CustomByteBlock",
            "TechCoreMotionStateSync", "RobotPerformanceSelectionSync", "DeployModeStatusSync",
            "RuneStatusSync", "SentryStatusSync", "DartSelectTargetStatusSync",
            "SentryCtrlResult", "AirSupportStatusSync"
        };
        foreach (var t in topics) await mqttClient.SubscribeAsync(new MqttTopicFilterBuilder().WithTopic(t).Build());
    }

    public bool IsMqttConnected => mqttClient != null && mqttClient.IsConnected;

    public TopicDataQuality GetDataQuality(string topic)
    {
        return DataQuality.ContainsKey(topic) ? DataQuality[topic] : null;
    }

    public bool IsDataFresh(string topic, float maxAgeSeconds = 2.0f)
    {
        if (!DataQuality.ContainsKey(topic)) return false;
        return Time.time - DataQuality[topic].lastReceiveTime < maxAgeSeconds;
    }

    public async void ReconnectToServer()
    {
        if (mqttClient != null)
        {
            if (mqttClient.IsConnected)
            {
                try
                {
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
        
        foreach (var quality in DataQuality.Values)
        {
            quality.lastReceiveTime = -999f;
            quality.receiveCount = 0;
            quality.lostCount = 0;
        }
        
        CurrentStage = 0;
        MatchTime = 0;
        ConnectionQuality = 1.0f;
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
                    CurrentRound = game.CurrentRound;
                    TotalRounds = game.TotalRounds;
                    StageElapsedSec = game.StageElapsedSec;
                    IsPaused = game.IsPaused;
                    GameStatusLastUpdate = Time.time;
                    break;

                case "GlobalUnitStatus":
                    var unit = GlobalUnitStatus.Parser.ParseFrom(data);
                    BaseHP = (int)unit.BaseHealth;
                    OutpostHP = (int)unit.OutpostHealth;
                    BaseStatus = unit.BaseStatus;
                    OutpostStatus = unit.OutpostStatus;
                    BaseShield = unit.BaseShield;
                    BaseShieldActive = unit.BaseShield > 0;
                    EnemyBaseHP = (int)unit.EnemyBaseHealth;     
                    EnemyOutpostHP = (int)unit.EnemyOutpostHealth;
                    EnemyBaseStatus = unit.EnemyBaseStatus;
                    EnemyOutpostStatus = unit.EnemyOutpostStatus;
                    EnemyBaseShield = unit.EnemyBaseShield;
                    EnemyBaseShieldActive = unit.EnemyBaseShield > 0;
                    TotalDamageAlly = unit.TotalDamageAlly;
                    TotalDamageEnemy = unit.TotalDamageEnemy;
                    UpdateAllHP(unit.RobotHealth);
                    RobotBullets.Clear();
                    foreach (var bullet in unit.RobotBullets) RobotBullets.Add((int)bullet);
                    GlobalUnitStatusLastUpdate = Time.time;
                    break;

                case "GlobalLogisticsStatus":
                    var logi = GlobalLogisticsStatus.Parser.ParseFrom(data);
                    MyGold = (int)logi.RemainingEconomy;
                    TotalEconomyObtained = logi.TotalEconomyObtained;
                    MyTechLevel = (int)logi.TechLevel; 
                    EncryptionLevel = logi.EncryptionLevel;
                    GlobalLogisticsStatusLastUpdate = Time.time;
                    break;

                case "GlobalSpecialMechanism":
                    var mech = GlobalSpecialMechanism.Parser.ParseFrom(data);
                    ActiveMechanisms.Clear();
                    for (int i = 0; i < mech.MechanismId.Count && i < mech.MechanismTimeSec.Count; i++)
                    {
                        ActiveMechanisms.Add(new SpecialMechanism
                        {
                            mechanismId = mech.MechanismId[i],
                            mechanismTimeSec = mech.MechanismTimeSec[i]
                        });
                    }
                    SpecialMechanismLastUpdate = Time.time;
                    break;

                case "Event":
                    var evt = RoboMaster.Event.Parser.ParseFrom(data);
                    OnGameEvent?.Invoke($"Event[{evt.EventId}]: {evt.Param}");
                    break;

                case "RobotInjuryStat":
                    var injury = RobotInjuryStat.Parser.ParseFrom(data);
                    MyInjuryStat.totalDamage = injury.TotalDamage;
                    MyInjuryStat.collisionDamage = injury.CollisionDamage;
                    MyInjuryStat.smallProjectileDamage = injury.SmallProjectileDamage;
                    MyInjuryStat.largeProjectileDamage = injury.LargeProjectileDamage;
                    MyInjuryStat.dartSplashDamage = injury.DartSplashDamage;
                    MyInjuryStat.moduleOfflineDamage = injury.ModuleOfflineDamage;
                    MyInjuryStat.offlineDamage = injury.OfflineDamage;
                    MyInjuryStat.penaltyDamage = injury.PenaltyDamage;
                    MyInjuryStat.serverKillDamage = injury.ServerKillDamage;
                    MyInjuryStat.killerId = injury.KillerId;
                    MyInjuryStat.lastUpdate = Time.time;
                    break;
                
                case "RobotRespawnStatus":
                    var respawn = RobotRespawnStatus.Parser.ParseFrom(data);
                    IsPendingRespawn = respawn.IsPendingRespawn;
                    CanFreeRespawn = respawn.CanFreeRespawn;
                    CanPayForRespawn = respawn.CanPayForRespawn;
                    GoldCostForRespawn = respawn.GoldCostForRespawn;
                    TotalRespawnProgress = respawn.TotalRespawnProgress;
                    CurrentRespawnProgress = respawn.CurrentRespawnProgress;
                    RespawnStatusLastUpdate = Time.time;
                    break;

                case "RobotStaticStatus":
                    var stat = RobotStaticStatus.Parser.ParseFrom(data);
                    MyID = (int)stat.RobotId;
                    if(MapData.ContainsKey(MyID)) MyRobot = MapData[MyID];
                    ConnectionState = stat.ConnectionState;
                    FieldState = stat.FieldState;
                    AliveState = stat.AliveState;
                    RobotType = stat.RobotType;
                    PerformanceSystemShooter = stat.PerformanceSystemShooter;
                    PerformanceSystemChassis = stat.PerformanceSystemChassis;
                    MyRobot.maxHp = (int)stat.MaxHealth;
                    MyRobot.maxHeat = (int)stat.MaxHeat;
                    HeatCooldownRate = stat.HeatCooldownRate;
                    MaxPower = stat.MaxPower;
                    MaxBufferEnergy = stat.MaxBufferEnergy;
                    MaxChassisEnergy = stat.MaxChassisEnergy;
                    StaticStatusLastUpdate = Time.time;
                    break;

                case "RobotDynamicStatus":
                    var dyn = RobotDynamicStatus.Parser.ParseFrom(data);
                    MyRobot.currentHp = (int)dyn.CurrentHealth;
                    MyRobot.isDead = MyRobot.currentHp <= 0;
                    if (MyRobot.isDead) IsPendingRespawn = true; 
                    else IsPendingRespawn = false;
                    MyRobot.currentHeat = dyn.CurrentHeat;
                    MyRobot.currentAmmo = (int)dyn.RemainingAmmo;
                    MyRobot.chassisEnergy = (int)dyn.CurrentChassisEnergy;
                    MyRobot.isOutCombat = dyn.IsOutOfCombat;
                    LastProjectileFireRate = dyn.LastProjectileFireRate;
                    CurrentExperience = dyn.CurrentExperience;
                    ExperienceForUpgrade = dyn.ExperienceForUpgrade;
                    TotalProjectilesFired = dyn.TotalProjectilesFired;
                    OutOfCombatCountdown = dyn.OutOfCombatCountdown;
                    CanRemoteHeal = dyn.CanRemoteHeal;
                    CanRemoteAmmo = dyn.CanRemoteAmmo;
                    DynamicStatusLastUpdate = Time.time;
                    break;

                case "RobotModuleStatus":
                    var mod = RobotModuleStatus.Parser.ParseFrom(data);
                    PowerManagerStatus = mod.PowerManager;
                    RfidStatus = mod.Rfid;
                    LightStripStatus = mod.LightStrip;
                    SmallShooterStatus = mod.SmallShooter;
                    BigShooterStatus = mod.BigShooter;
                    UwbStatus = mod.Uwb;
                    ArmorStatus = mod.Armor;
                    VideoTransmissionStatus = mod.VideoTransmission;
                    CapacitorStatus = mod.Capacitor;
                    MainControllerStatus = mod.MainController;
                    LaserDetectionModuleStatus = mod.LaserDetectionModule;
                    ModuleStatusLastUpdate = Time.time;
                    MyRobot.modChassis = mod.PowerManager == 1; 
                    MyRobot.modVideo = mod.VideoTransmission == 1;
                    break;

                case "RobotPosition":
                    var pos = RobotPosition.Parser.ParseFrom(data);
                    MyRobot.pos = new Vector3(pos.X, 0, pos.Y); 
                    MyRobot.yaw = pos.Yaw;
                    PositionLastUpdate = Time.time;
                    MyRobot.lastUpdate = Time.time;
                    break;

                case "Buff":
                    var buff = Buff.Parser.ParseFrom(data);
                    var existingBuff = ActiveBuffs.Find(b => b.robotId == buff.RobotId && b.buffType == buff.BuffType);
                    if (existingBuff != null)
                    {
                        existingBuff.buffLevel = buff.BuffLevel;
                        existingBuff.buffMaxTime = buff.BuffMaxTime;
                        existingBuff.buffLeftTime = buff.BuffLeftTime;
                        existingBuff.receiveTime = Time.time;
                    }
                    else
                    {
                        ActiveBuffs.Add(new BuffInfo
                        {
                            robotId = buff.RobotId,
                            buffType = buff.BuffType,
                            buffLevel = buff.BuffLevel,
                            buffMaxTime = buff.BuffMaxTime,
                            buffLeftTime = buff.BuffLeftTime,
                            receiveTime = Time.time
                        });
                    }
                    break;

                case "PenaltyInfo":
                    var penalty = PenaltyInfo.Parser.ParseFrom(data);
                    PenaltyType = penalty.PenaltyType;
                    PenaltyEffectSec = penalty.PenaltyEffectSec;
                    TotalPenaltyNum = penalty.TotalPenaltyNum;
                    PenaltyLastUpdate = Time.time;
                    break;

                case "RobotPathPlanInfo":
                    var path = RobotPathPlanInfo.Parser.ParseFrom(data);
                    SentryIntention = path.Intention;
                    SentryPathStartX = path.StartPosX;
                    SentryPathStartY = path.StartPosY;
                    SentryPathSenderId = path.SenderId;
                    SentryPathOffsetX.Clear();
                    SentryPathOffsetY.Clear();
                    foreach (var x in path.OffsetX) SentryPathOffsetX.Add(x);
                    foreach (var y in path.OffsetY) SentryPathOffsetY.Add(y);
                    PathPlanLastUpdate = Time.time;
                    break;

                case "RadarInfoToClient":
                    var radar = RadarInfoToClient.Parser.ParseFrom(data);
                    var existingTarget = RadarTargets.Find(t => t.targetRobotId == radar.TargetRobotId);
                    if (existingTarget != null)
                    {
                        existingTarget.targetPos = new Vector2(radar.TargetPosX, radar.TargetPosY);
                        existingTarget.towardAngle = radar.TorwardAngle;
                        existingTarget.isHighLight = radar.IsHighLight;
                        existingTarget.lastUpdate = Time.time;
                    }
                    else
                    {
                        RadarTargets.Add(new RadarTargetInfo
                        {
                            targetRobotId = radar.TargetRobotId,
                            targetPos = new Vector2(radar.TargetPosX, radar.TargetPosY),
                            towardAngle = radar.TorwardAngle,
                            isHighLight = radar.IsHighLight,
                            lastUpdate = Time.time
                        });
                    }
                    break;

                case "CustomByteBlock":
                    var customBlock = CustomByteBlock.Parser.ParseFrom(data);
                    CustomByteBlockData = customBlock.Data.ToByteArray();
                    CustomByteBlockLastUpdate = Time.time;
                    break;

                case "TechCoreMotionStateSync":
                    var tech = TechCoreMotionStateSync.Parser.ParseFrom(data);
                    MaxDifficultyLevel = (int)tech.MaximumDifficultyLevel; 
                    TechCoreStatus = (int)tech.Status; 
                    EnemyCoreStatus = (int)tech.EnemyCoreStatus;
                    AssemblyRemainTimeAll = tech.RemainTimeAll;
                    AssemblyRemainTimeStep = tech.RemainTimeStep;
                    TechCoreLastUpdate = Time.time;
                    break;

                case "RobotPerformanceSelectionSync":
                    var perf = RobotPerformanceSelectionSync.Parser.ParseFrom(data);
                    CurrentShooterPerformance = perf.Shooter;
                    CurrentChassisPerformance = perf.Chassis;
                    SentryControlMode = perf.SentryControl;
                    PerformanceSelectionLastUpdate = Time.time;
                    break;

                case "DeployModeStatusSync":
                    var deploy = DeployModeStatusSync.Parser.ParseFrom(data);
                    DeployModeStatus = deploy.Status;
                    DeployModeLastUpdate = Time.time;
                    break;

                case "RuneStatusSync":
                    var rune = RuneStatusSync.Parser.ParseFrom(data);
                    RuneStatus = rune.RuneStatus;
                    RuneActivatedArms = rune.ActivatedArms;
                    RuneAverageRings = rune.AverageRings;
                    RuneLastUpdate = Time.time;
                    break;

                case "SentryStatusSync":
                    var sentry = SentryStatusSync.Parser.ParseFrom(data);
                    SentryPostureId = sentry.PostureId;
                    SentryIsWeakened = sentry.IsWeakened;
                    SentryStatusLastUpdate = Time.time;
                    break;

                case "DartSelectTargetStatusSync":
                    var dart = DartSelectTargetStatusSync.Parser.ParseFrom(data);
                    DartTargetId = dart.TargetId;
                    DartOpenStatus = dart.Open;
                    DartLastUpdate = Time.time;
                    break;

                case "SentryCtrlResult":
                    var ctrlResult = SentryCtrlResult.Parser.ParseFrom(data);
                    LastSentryCommandId = ctrlResult.CommandId;
                    LastSentryCommandResult = ctrlResult.ResultCode;
                    break;

                case "AirSupportStatusSync":
                    var air = AirSupportStatusSync.Parser.ParseFrom(data);
                    AirSupportStatus = air.AirsupportStatus;
                    AirSupportLeftTime = air.LeftTime;
                    AirSupportCostCoins = air.CostCoins;
                    IsBeingTargeted = air.IsBeingTargeted;
                    AirSupportShooterStatus = air.ShooterStatus;
                    AirSupportLastUpdate = Time.time;
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
