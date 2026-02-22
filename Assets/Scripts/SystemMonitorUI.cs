using UnityEngine;
using TMPro;
using System.Text;
using System.Reflection; 

public class SystemMonitorUI : MonoBehaviour
{
    [Header("=== UI Component ===")]
    public TextMeshProUGUI monitorText; 
    
    [Header("=== Target Scripts ===")]
    public StreamForwarder streamForwarder; 

    private StringBuilder sb = new StringBuilder();
    private float updateTimer = 0f;
    private float currentBitratekbps = 0f; 
    private bool isPlayerConnected = false; 

    void OnEnable()
    {
        Application.logMessageReceived += HandleLog;
    }

    void OnDisable()
    {
        Application.logMessageReceived -= HandleLog;
    }

    void HandleLog(string logString, string stackTrace, LogType type)
    {
        // 捕获码率
        if (logString.Contains("[StreamForwarder] Bitrate:"))
        {
            try 
            {
                string[] parts = logString.Split(' ');
                for (int i = 0; i < parts.Length; i++)
                {
                    if (parts[i] == "kbps" && i > 0)
                    {
                        if (float.TryParse(parts[i-1], out float bps))
                        {
                            currentBitratekbps = bps;
                        }
                    }
                }
            } catch {}
        }
        
        // 捕获播放器连接状态
        if (logString.Contains("Unity 内部播放器连接成功"))
        {
            isPlayerConnected = true;
        }
        else if (logString.Contains("Client Disconnected"))
        {
            isPlayerConnected = false;
        }
    }

    void Update()
    {
        updateTimer += Time.deltaTime;
        
        if (updateTimer >= 0.1f)
        {
            updateTimer = 0f;
            RefreshMonitor();
        }
    }

    private void RefreshMonitor()
    {
        if (monitorText == null) return;
        sb.Clear();

        sb.AppendLine("<size=120%><b><color=#FFD700>=== RMVR 系统调试大盘 ===</color></b></size>\n");

        sb.AppendLine("<b>[ 图传 TCP/UDP 转发链路 ]</b>");
        if (streamForwarder != null)
        {
            // 【关键修复】直接读取公有变量 isRunning，抛弃容易报错的暴力反射！
            bool isRun = streamForwarder.isRunning;

            string statusColor = isRun ? "green" : "red";
            sb.AppendLine($"服务内核: <color={statusColor}>{(isRun ? "激活动力" : "离线")}</color>");
            
            // 内部播放器连接状态
            string playerColor = isPlayerConnected ? "cyan" : "grey";
            sb.AppendLine($"播放器TCP握手: <color={playerColor}>{(isPlayerConnected ? "已连接解码" : "等待连接...")}</color>");

            float mbps = currentBitratekbps / 1000f;
            string netColor = mbps > 0.5f ? "green" : (mbps > 0f ? "yellow" : "red");
            sb.AppendLine($"当前总吞吐: <color={netColor}>{mbps:F2} Mbps</color>");
            
            // 数据包验证
            sb.AppendLine($"已接收物理UDP包: {streamForwarder.probeTotalPacketsReceived}");

            if (mbps == 0 && isRun)
            {
                sb.AppendLine("<color=red>警告: 管道通畅，但源头枯竭！</color>");
            }
        }
        else
        {
            sb.AppendLine("<color=red>未绑定 StreamForwarder 实例！</color>");
        }
        sb.AppendLine();

        sb.AppendLine("<b>[ MQTT 赛场业务链路 ]</b>");
        if (DataManager.Instance != null)
        {
            var data = DataManager.Instance;
            var robot = data.MyRobot;

            // 这里如果依然是 Private, 则继续使用反射
            bool isMqttConnected = GetFieldValue<bool>(data, "IsMqttConnected");

            sb.AppendLine($"信道: {(data.CurrentStage >= 0 || isMqttConnected ? "<color=green>稳定</color>" : "<color=red>死锁断流</color>")}");
            sb.AppendLine($"赛段: {data.CurrentStage} | 剩余: {data.MatchTime}s | 经济: {data.MyGold}");

            if (robot != null)
            {
                sb.AppendLine($"\n<b>[ 战车工兵 (ID:{robot.id}) ]</b>");
                sb.AppendLine($"结构: {robot.currentHp} / {robot.maxHp} HP");
                sb.AppendLine($"动力池: {robot.chassisEnergy} J");
                
                string chsColor = robot.modChassis ? "green" : "red";
                sb.AppendLine($"电调握手: 底盘(<color={chsColor}>{robot.modChassis}</color>)");
            }
        }
        else
        {
            sb.AppendLine("<color=red>DataManager 异常！</color>");
        }

        monitorText.text = sb.ToString();
    }

    private T GetFieldValue<T>(object obj, string fieldName)
    {
        if (obj == null) return default(T);
        var field = obj.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
        var prop = obj.GetType().GetProperty(fieldName, BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
        
        if (field != null) return (T)field.GetValue(obj);
        if (prop != null) return (T)prop.GetValue(obj);
        return default(T);
    }
}