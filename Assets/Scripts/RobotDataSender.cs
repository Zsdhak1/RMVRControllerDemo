using UnityEngine;
using System;
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

    [Header("角度编码方式")]
    [Tooltip("ShortFixedPoint = 原有 short×100 (Byte 0~13)；UInt8Mapped = uint8_t 映射 (Byte 0~6)")]
    public AngleEncoding angleEncoding = AngleEncoding.ShortFixedPoint;

    [Header("临时禁用开关")]
    [Tooltip("禁用手柄扳机读取，夹爪状态固定为松开")]
    public bool disableGripperInput = true;

    [Tooltip("仅在 ShortFixedPoint 模式下有效：禁用 Byte 15 累加校验和")]
    public bool disableDataChecksum = true;

    [Header("手动覆盖模式 (无手柄调试)")]
    [Tooltip("启用手动角度覆盖，直接用下方手动角度替代 IK 输出")]
    public bool useManualAngles = false;
    [Tooltip("手动设置的 7 个关节角度 (度)")]
    public float[] manualAngles = new float[] { 0f, 45f, -90f, 0f, 0f, 0f, 0f };

    [Header("uint8_t 映射范围 (仅 UInt8Mapped 生效)")]
    [Tooltip("角度映射到 uint8_t(0~255) 的最小值")]
    public float angleMapMin = -180f;
    [Tooltip("角度映射到 uint8_t(0~255) 的最大值")]
    public float angleMapMax = 180f;

    public enum AngleEncoding
    {
        ShortFixedPoint,
        UInt8Mapped
    }

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
        byte[] dataPacket = new byte[30];
        float[] sourceAngles = useManualAngles ? manualAngles : robotController.outAngles;
        bool triggerPressed = disableGripperInput ? false : OVRInput.Get(OVRInput.Button.PrimaryIndexTrigger, OVRInput.Controller.RTouch);

        if (angleEncoding == AngleEncoding.ShortFixedPoint)
        {
            // === 原有方案：7 个关节角度用 short ×100 定点数 (Byte 0~13) ===
            for (int i = 0; i < 7; i++)
            {
                short val = (short)(sourceAngles[i] * 100f);
                byte[] b = BitConverter.GetBytes(val);
                dataPacket[i * 2] = b[0];
                dataPacket[i * 2 + 1] = b[1];
            }

            // 夹爪 Byte 14
            dataPacket[14] = triggerPressed ? (byte)1 : (byte)0;

            // 校验和 Byte 15
            if (!disableDataChecksum)
            {
                byte sum = 0;
                for (int i = 0; i < 15; i++) sum += dataPacket[i];
                dataPacket[15] = sum;
            }
        }
        else // UInt8Mapped
        {
            // === 新方案：每 3 字节表示 1 个关节角度，精确到小数点后两位 ===
            // 编码规则：
            //   Byte0 = (sbyte)(angle / 10)               带符号的十位/百位
            //   Byte1 = (byte)((|angle| % 1) * 100)        小数点后两位 0~99
            //   Byte2 = (byte)(|angle| % 10)               整数部分的个位 0~9
            // 例：-175.55° -> -17, 55, 5
            // 解码：sign * (|Byte0|*10 + Byte2 + Byte1/100)
            for (int i = 0; i < 7; i++)
            {
                float angle = sourceAngles[i];
                int baseIdx = i * 3;
                sbyte b0 = (sbyte)(angle / 10f);
                byte b1 = (byte)((Mathf.Abs(angle) % 1f) * 100f);
                byte b2 = (byte)(Mathf.Abs(angle) % 10f);
                dataPacket[baseIdx + 0] = (byte)b0;
                dataPacket[baseIdx + 1] = b1;
                dataPacket[baseIdx + 2] = b2;
            }

            // 夹爪 Byte 28
            dataPacket[28] = triggerPressed ? (byte)1 : (byte)0;

            // Byte 29 固定为 3
            dataPacket[29] = 3;
        }

        // 组装 Protobuf 消息
        RoboMaster.CustomControl controlMsg = new RoboMaster.CustomControl();
        controlMsg.Data = ByteString.CopyFrom(dataPacket);
        DataManager.Instance.SendCustomControl(controlMsg);
    }
}