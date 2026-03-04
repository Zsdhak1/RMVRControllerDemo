using UnityEngine;
using TMPro;
using System.Text;
using System.Reflection; 

public class SystemMonitorUI : MonoBehaviour
{
    [Header("=== UI Component ===")]
    public TextMeshProUGUI monitorText; 
    
    [Header("=== Target Scripts ===")]
    public FreeRMMemoryVideoPlayer rmVideoPlayer; 

    private StringBuilder sb = new StringBuilder();
    private float updateTimer = 0f;

    void Start()
    {
        if (rmVideoPlayer == null) 
        {
            Debug.LogWarning("[SystemMonitorUI] 监视大盘：你忘了拖入 FreeRMMemoryVideoPlayer！");
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

        sb.AppendLine("<b>[ 图传底层：UDP 内存融合 ]</b>");
        if (rmVideoPlayer != null)
        {
            bool isRun = rmVideoPlayer.isRunning;

            string statusColor = isRun ? "green" : "red";
            sb.AppendLine($"接收伺服引擎: <color={statusColor}>{(isRun ? "正在运行" : "掉线")}</color>");

            float mbps = rmVideoPlayer.currentRateKbps / 1000f;
            string netColor = mbps > 0.5f ? "green" : (mbps > 0f ? "yellow" : "red");
            sb.AppendLine($"瞬时 UDP 吞吐量: <color={netColor}>{mbps:F2} Mbps</color>");
            
            sb.AppendLine($"收讫物理帧包: {rmVideoPlayer.probeTotalPacketsReceived}");

            if (mbps == 0 && isRun)
            {
                sb.AppendLine("<color=red>警告: 接不到图传信道</color>");
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