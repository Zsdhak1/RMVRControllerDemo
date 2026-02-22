using UnityEngine;
using RoboMaster; // 引用你挂了 Protobuf 的那个命名空间

public class EngineerVRInput : MonoBehaviour
{
    [Header("UI 控制")]
    public EngineerUIManager uiManager;

    [Header("滑鼠转译灵敏度 (Mouse Sensitivity)")]
    [Tooltip("手柄转动 1 度，转化为多少个像素的鼠标位移")]
    public float mouseSensitivityX = 10f; 
    public float mouseSensitivityY = 10f; 

    [Header("发送频率控制")]
    [Tooltip("建议 RMUC 这个指令的发包率在 60-75Hz 左右")]
    public int sendRate = 60;
    private float sendInterval;
    private float sendTimer;

    // 内部状态跟踪
    private bool isDragging = false;
    private Quaternion lastControllerRot;
    
    // 累加器，用于在每一帧累加鼠标变动量并在发包时清空
    private int accumulatedMouseX = 0;
    private int accumulatedMouseY = 0;

    void Start()
    {
        sendInterval = 1.0f / sendRate;
    }

    void Update()
    {
        HandleMenuToggle();
        
        // 1. 每帧计算鼠标增量并存入累加器
        CalculateMouseDelta();

        // 2. 达到发送频率，打包组装 WASD 和 Mouse 数据发给 MQTT
        sendTimer += Time.deltaTime;
        if (sendTimer >= sendInterval)
        {
            sendTimer = 0f;
            PackAndSendKeyboardMouseControl();
        }
    }

    /// <summary>
    /// 功能 A: 将左手 Grip (抓握) 映射为鼠标位移增量 (Mouse Delta)
    /// 不再旋转 VR 世界，只生成数据！
    /// </summary>
    void CalculateMouseDelta()
    {
        bool gripHeld = OVRInput.Get(OVRInput.Button.PrimaryHandTrigger, OVRInput.Controller.LTouch);

        if (gripHeld)
        {
            Quaternion currentRot = OVRInput.GetLocalControllerRotation(OVRInput.Controller.LTouch);

            if (!isDragging)
            {
                // 刚按下：重置零点
                isDragging = true;
                lastControllerRot = currentRot;
                accumulatedMouseX = 0;
                accumulatedMouseY = 0;
            }
            else
            {
                // 拖拽中：计算两帧之间的局部旋转变化
                Quaternion deltaRot = currentRot * Quaternion.Inverse(lastControllerRot);
                
                // 将四元数分解为欧拉角，处理 0-360 跳变
                float deltaYaw = NormalizeAngle(deltaRot.eulerAngles.y); // 左右转 -> 对应 Mouse X
                float deltaPitch = NormalizeAngle(deltaRot.eulerAngles.x); // 上下转 -> 对应 Mouse Y
                
                // 将度数转化为 int 型鼠标相对位移，并累加给本次发送循环
                accumulatedMouseX += Mathf.RoundToInt(deltaYaw * mouseSensitivityX);
                accumulatedMouseY += Mathf.RoundToInt(deltaPitch * mouseSensitivityY);

                // 更新上一帧坐标
                lastControllerRot = currentRot;
            }
        }
        else
        {
            isDragging = false;
            // 没按抓握时，鼠标不产生相对位移
            accumulatedMouseX = 0;
            accumulatedMouseY = 0;
        }
    }

    /// <summary>
    /// 功能 B: 收集并打包所有按键及摇杆数据，触发 DataManager 的 MQTT 发送
    /// </summary>
    void PackAndSendKeyboardMouseControl()
    {
        // 这一步安全检查极其重要，否则断网或未连上时会疯狂报空引用
        if (DataManager.Instance == null || !DataManager.Instance.IsMqttConnected) return;

        // 1. 构建键鼠控制指令结构体
        KeyboardMouseControl kmc = new KeyboardMouseControl();

        // 2. 封装鼠标位移与按键 (读取刚才我们的累加器)
        // 注意：RMUC 协议中说：左转为负，下移为负。你可能需要在此处给 accumulatedMouseY 加一个负号，取决于你的物理体感
        kmc.MouseX = accumulatedMouseX;
        kmc.MouseY = -accumulatedMouseY; // 视需求反转 Y 轴
        kmc.MouseZ = 0; // 滚轮目前没用到

        // 读取一下是否有开枪等鼠标按键需求
        kmc.LeftButtonDown = OVRInput.Get(OVRInput.Button.PrimaryIndexTrigger, OVRInput.Controller.RTouch);
        kmc.RightButtonDown = false; // 按需绑定

        // 3. 将左手摇杆 (Thumbstick) 映射为 WASD 键盘位掩码
        kmc.KeyboardValue = CalculateKeyboardMask();

        // 4. 清空当次鼠标累加器（准备收集下一波鼠标微操）
        accumulatedMouseX = 0;
        accumulatedMouseY = 0;

        // 5. 将这只饱满的数据包，扔给 MQTT！
        // 如果 DataManager 暂无此方法，你需要把它加进去
        DataManager.Instance.SendKeyboardMouseControl(kmc);
    }

    /// <summary>
    /// 功能 C: 将模拟量摇杆转换为协议所要求的严格 WASD 对应 Bit 掩码 (uint32)
    /// 支持物理摇杆推向全向/八向对角线时的组合键并发（例如 W+A 并发）
    /// </summary>
    uint CalculateKeyboardMask()
    {
        uint mask = 0;
        
        // 读取左摇杆 2D 值，范围大致在 (-1, -1) 到 (1, 1) 的单位圆内
        Vector2 stick = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, OVRInput.Controller.LTouch);
        
        // 设定轴向激活阈值 (Deadzone)
        // 注意：摇杆推到最对角时，分量约为 0.707，所以阈值不能设成 >0.8 这种荒唐值！
        // 0.3f - 0.5f 之间能保证完美的容错率和八向触发敏感度
        float triggerThreshold = 0.35f;

        // X轴判断：互斥（不能同时按 A 和 D）
        if (stick.x > triggerThreshold) 
        {
            mask |= (1u << 3); // bit 3 = D (右)
        }
        else if (stick.x < -triggerThreshold)
        {
            mask |= (1u << 2); // bit 2 = A (左)
        }

        // Y轴判断：互斥（不能同时按 W 和 S）
        if (stick.y > triggerThreshold)
        {
            mask |= (1u << 0); // bit 0 = W (前)
        }
        else if (stick.y < -triggerThreshold)
        {
            mask |= (1u << 1); // bit 1 = S (后)
        }

        // --- 附加功能：Shift 加速与 Ctrl 减速掩码映射 ---
        // 工程车如果需要切挡位，可以用摇杆按下 (L3) 或手柄其他按键
        // 假设用左手柄的 X 键 来触发 Shift 加速 (bit 4)
        if (OVRInput.Get(OVRInput.Button.Three, OVRInput.Controller.LTouch))
        {
            mask |= (1u << 4);
        }
        
        // 假设用左手柄的 Y 键 来触发 Ctrl 减速 (bit 5)
        if (OVRInput.Get(OVRInput.Button.Four, OVRInput.Controller.LTouch))
        {
            mask |= (1u << 5);
        }

        return mask;
    }

    // 辅助菜单调出
    void HandleMenuToggle()
    {
        if (OVRInput.GetDown(OVRInput.Button.Three, OVRInput.Controller.LTouch) || 
            OVRInput.GetDown(OVRInput.Button.Start, OVRInput.Controller.LTouch))
        {
            if (uiManager != null) uiManager.ToggleMenu();
        }
    }

    // 将 0~360 的角度转化为正负 -180~180 的偏差度数，这是计算 delta 的基础
    float NormalizeAngle(float a)
    {
        if (a > 180f) return a - 360f;
        return a;
    }
}