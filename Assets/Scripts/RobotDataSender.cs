using UnityEngine;
using Google.Protobuf; // 必须引用
using RoboMaster;      // 必须引用协议命名空间

public class RobotDataSender : MonoBehaviour
{
    [Header("依赖引用")]
    [Tooltip("必须引用场景中的 Robot_Root")]
    public RobotIKController robotController;

    [Header("发送设置")]
    [Tooltip("每秒发送次数，建议 50-60")]
    public int sendRate = 50;
    
    // 内部计时器
    private float sendInterval;
    private float sendTimer;

    void Start()
    {
        sendInterval = 1.0f / sendRate;
    }

    void Update()
    {
        // 1. 安全检查：确保已连接且控制器存在
        if (DataManager.Instance == null || robotController == null) return;
        
        // 2. 频率控制
        sendTimer += Time.deltaTime;
        if (sendTimer >= sendInterval)
        {
            sendTimer = 0;
            PackAndSend();
        }
    }

    void PackAndSend()
    {
        // === 步骤 A: 获取基础角度数据 ===
        // 从 IK 控制器拿到的 byte[30]，前 14 个字节已经是 J1-J7 的数据了
        byte[] dataPacket = robotController.GetPacketData();

        // === 步骤 B: 注入夹爪数据 (Byte 14) ===
        // 读取 Quest 右手食指扳机 (0 = 松开, 1 = 按下)
        // 你也可以改为读取 Axis1D 做模拟量控制
        bool triggerPressed = OVRInput.Get(OVRInput.Button.PrimaryIndexTrigger, OVRInput.Controller.RTouch);
        dataPacket[14] = triggerPressed ? (byte)1 : (byte)0;

        // === 步骤 C: 计算校验和 (Byte 15) ===
        // 简单的累加校验，确保数据完整性
        byte sum = 0;
        for (int i = 0; i < 15; i++)
        {
            sum += dataPacket[i];
        }
        dataPacket[15] = sum;

        // === 步骤 D: 组装 Protobuf 消息 ===
        RoboMaster.RemoteControl controlMsg = new RoboMaster.RemoteControl();
        
        // 填入核心数据
        controlMsg.Data = ByteString.CopyFrom(dataPacket);
        
        // 填入额外按钮 (例如 A 键用于特殊功能，如切换模式)
        controlMsg.RightButtonDown = OVRInput.Get(OVRInput.Button.One, OVRInput.Controller.RTouch);
        
        // 可选：填入鼠标/键盘模拟数据 (如果需要)
        // controlMsg.MouseX = ...

        // === 步骤 E: 通过 DataManager 发送 ===
        DataManager.Instance.SendRemoteControl(controlMsg);
    }
}