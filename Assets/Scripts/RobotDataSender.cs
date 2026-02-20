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

        // ... (前面代码不变)

        // === 步骤 D: 组装 Protobuf 消息 (V1.2.0) ===
        // 使用 CustomControl 替代 RemoteControl
        RoboMaster.CustomControl controlMsg = new RoboMaster.CustomControl();
        
        // 填入核心数据 (最大 30 字节)
        controlMsg.Data = ByteString.CopyFrom(dataPacket);
        
        // 注意：CustomControl 只有 Data 字段。
        // 如果你需要发送 RightButtonDown，必须把它塞进 dataPacket 的某个字节里(比如第16个字节)
        // 例如: dataPacket[16] = OVRInput.Get(OVRInput.Button.One) ? (byte)1 : (byte)0;

        // === 步骤 E: 通过 DataManager 发送 ===
        // 调用新的发送方法
        DataManager.Instance.SendCustomControl(controlMsg);
    }
}