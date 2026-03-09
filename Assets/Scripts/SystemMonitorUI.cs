using UnityEngine;
using TMPro;
using System.Text;
using System.Reflection; 

public class SystemMonitorUI : MonoBehaviour
{
    [Header("=== UI Component ===")]
    public TextMeshProUGUI monitorText; 
    
    [Header("=== Target Scripts ===")]
    public FreeRMVideoPlayerTCP rmVideoPlayer; 

    private StringBuilder sb = new StringBuilder();
    private float updateTimer = 0f;

    void Start()
    {
        if (rmVideoPlayer == null) 
        {
            Debug.LogWarning("[SystemMonitorUI] 监视大盘：你忘了拖入 FreeRMVideoPlayerTCP！");
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

        sb.AppendLine("<b>[ 图传底层：UDP->TCP 转发 ]</b>");
        if (rmVideoPlayer != null)
        {
            bool isRun = rmVideoPlayer.isRunning;
            bool isTcpConnected = rmVideoPlayer.isTcpConnected;

            string statusColor = isRun ? "green" : "red";
            sb.AppendLine($"UDP接收引擎: <color={statusColor}>{(isRun ? "正在运行" : "已停止")}</color>");
            
            string tcpColor = isTcpConnected ? "green" : "yellow";
            sb.AppendLine($"TCP转发状态: <color={tcpColor}>{(isTcpConnected ? "已连接" : "等待连接")}</color>");

            float mbps = rmVideoPlayer.currentRateKbps / 1000f;
            string netColor = mbps > 0.5f ? "green" : (mbps > 0f ? "yellow" : "red");
            sb.AppendLine($"瞬时吞吐速率: <color={netColor}>{mbps:F2} Mbps</color>");
            
            sb.AppendLine($"收讫UDP包数: {rmVideoPlayer.probeTotalPacketsReceived}");
            sb.AppendLine($"转发字节数: {rmVideoPlayer.totalBytesSent / 1024 / 1024} MB");
            sb.AppendLine($"丢弃包数: {rmVideoPlayer.droppedPackets}");
            sb.AppendLine($"缓冲队列: {rmVideoPlayer.queueSize}");

            if (mbps == 0 && isRun)
            {
                sb.AppendLine("<color=red>警告: 当前无视频数据流入</color>");
            }
        }
        else
        {
            sb.AppendLine("<color=red>监控大盘处于盲区：未绑定视频管理组件</color>");
        }
        sb.AppendLine();

        sb.AppendLine("<b>[ MQTT 赛场业务链路 ]</b>");
        if (DataManager.Instance != null)
        {
            var data = DataManager.Instance;
            var robot = data.MyRobot;

            bool isMqttConnected = data.IsMqttConnected;

            sb.AppendLine($"信道握手: {(isMqttConnected ? "<color=green>稳定</color>" : "<color=red>断流 (请求重连)</color>")}");
            sb.AppendLine($"赛段: {data.CurrentStage} | 剩余: {data.MatchTime}s | 经济: {data.MyGold}");

            if (robot != null)
            {
                sb.AppendLine($"\n<b>[ 机器人 (ID:{robot.id}) ]</b>");
                sb.AppendLine($"结构: {robot.currentHp} / {robot.maxHp} HP");
                sb.AppendLine($"动力: {robot.chassisEnergy} J");
                
                string chsColor = robot.modChassis ? "green" : "red";
                sb.AppendLine($"电调系统握手: 底盘模块(<color={chsColor}>{robot.modChassis}</color>)");
            }
        }
        else
        {
            sb.AppendLine("<color=red>DataManager 异常脱落！</color>");
        }

        monitorText.text = sb.ToString();
    }
}