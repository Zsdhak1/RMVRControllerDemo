using UnityEngine;
using System.Collections.Generic;
using RoboMaster; // 引用 Protobuf 命名空间

// 定义宏映射的数据结构
[System.Serializable]
public class KeyMacroMapping
{
    [Header("=== 触发条件 ===")]
    [Tooltip("要检测的手柄")]
    public OVRInput.Controller controller;
    
    [Tooltip("要检测的物理按键")]
    public OVRInput.Button triggerButton;

    [Header("=== 触发效果 ===")]
    [Tooltip("触发时要点亮哪些虚拟键盘掩码？可多选！")]
    public KeyboardBitMask mappedKeys; 

    [Tooltip("是否在此按键按下的瞬间，弹窗提示？")]
    public bool showNotification = true;
    
    [Tooltip("弹出的提示文字")]
    public string notificationText = "触发绑定宏";

    [HideInInspector] public bool wasPressedLastFrame; 
}

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

    // 【核心新增】专门用来吸纳外部 UI 传递进来的综合按键事件
    // VirtualMacroButton.cs 会直接修改这个值
    public static uint ExternalUIMacroMask = 0;

    [Header("=== 核心映射表：物理按键 -> 键盘掩码 ===")]
    public List<KeyMacroMapping> customKeyMappings = new List<KeyMacroMapping>();

    [Header("=== 左摇杆默认 WASD 设置 ===")]
    public bool enableStickToWASD = true;
    // 调教后的轴向独立死区，适配八向移动
    public float stickDeadZone = 0.35f;

    // 内部状态跟踪
    private bool isDragging = false;
    private Quaternion lastControllerRot;
    
    // 累加器
    private int accumulatedMouseX = 0;
    private int accumulatedMouseY = 0;

    void Start()
    {
        sendInterval = 1.0f / sendRate;
    }

    void Update()
    {
        HandleMenuToggle();
        
        // 1. 计算鼠标位移增量
        CalculateMouseDelta();

        // 2. 检测物理按键宏并显示提示
        CheckKeysAndNotify();

        // 3. 达到发送频率，打包所有输入发给 MQTT
        sendTimer += Time.deltaTime;
        if (sendTimer >= sendInterval)
        {
            sendTimer = 0f;
            PackAndSendKeyboardMouseControl();
        }
    }

    /// <summary>
    /// 功能 A: 将左手 Grip (抓握) 映射为鼠标位移增量
    /// </summary>
    void CalculateMouseDelta()
    {
        bool gripHeld = OVRInput.Get(OVRInput.Button.PrimaryHandTrigger, OVRInput.Controller.LTouch);

        if (gripHeld)
        {
            Quaternion currentRot = OVRInput.GetLocalControllerRotation(OVRInput.Controller.LTouch);

            if (!isDragging)
            {
                isDragging = true;
                lastControllerRot = currentRot;
                accumulatedMouseX = 0;
                accumulatedMouseY = 0;
            }
            else
            {
                Quaternion deltaRot = currentRot * Quaternion.Inverse(lastControllerRot);
                
                float deltaYaw = NormalizeAngle(deltaRot.eulerAngles.y); 
                float deltaPitch = NormalizeAngle(deltaRot.eulerAngles.x); 
                
                accumulatedMouseX += Mathf.RoundToInt(deltaYaw * mouseSensitivityX);
                accumulatedMouseY += Mathf.RoundToInt(deltaPitch * mouseSensitivityY);

                lastControllerRot = currentRot;
            }
        }
        else
        {
            isDragging = false;
            accumulatedMouseX = 0;
            accumulatedMouseY = 0;
        }
    }

    /// <summary>
    /// 功能 B: 收集并打包所有按键及摇杆数据
    /// </summary>
    void PackAndSendKeyboardMouseControl()
    {
        if (DataManager.Instance == null || !DataManager.Instance.IsMqttConnected) return;

        KeyboardMouseControl kmc = new KeyboardMouseControl();

        kmc.MouseX = accumulatedMouseX;
        kmc.MouseY = -accumulatedMouseY; 
        kmc.MouseZ = 0; 
        accumulatedMouseX = 0;
        accumulatedMouseY = 0;

        kmc.LeftButtonDown = OVRInput.Get(OVRInput.Button.PrimaryIndexTrigger, OVRInput.Controller.RTouch);
        kmc.RightButtonDown = false; 

        // 3. 汇聚所有来源的键盘掩码 (摇杆 + 物理宏 + UI)
        kmc.KeyboardValue = GatherAllKeyboardMasks();

        DataManager.Instance.SendKeyboardMouseControl(kmc);
    }

    /// <summary>
    /// 功能 C: 融合所有输入源的位掩码
    /// </summary>
    uint GatherAllKeyboardMasks()
    {
        uint currentMask = 0;

        // 1. 左摇杆 -> WASD (八向独立判定)
        if (enableStickToWASD)
        {
            Vector2 stick = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, OVRInput.Controller.LTouch);
            
            // X轴
            if (stick.x > stickDeadZone)  currentMask |= (uint)KeyboardBitMask.D_Key;
            else if (stick.x < -stickDeadZone) currentMask |= (uint)KeyboardBitMask.A_Key;

            // Y轴
            if (stick.y > stickDeadZone)  currentMask |= (uint)KeyboardBitMask.W_Key;
            else if (stick.y < -stickDeadZone) currentMask |= (uint)KeyboardBitMask.S_Key;
        }

        // 2. 物理按键宏列表
        foreach (var mapping in customKeyMappings)
        {
            if (OVRInput.Get(mapping.triggerButton, mapping.controller))
            {
                currentMask |= (uint)mapping.mappedKeys;
            }
        }

        // 3. 外部 UI 虚拟按钮 (Button Down)
        currentMask |= ExternalUIMacroMask;

        return currentMask;
    }

    void CheckKeysAndNotify()
    {
        foreach (var mapping in customKeyMappings)
        {
            bool isPressed = OVRInput.Get(mapping.triggerButton, mapping.controller);
            if (isPressed && !mapping.wasPressedLastFrame)
            {
                if (mapping.showNotification && uiManager != null)
                {
                    uiManager.ShowNotification(mapping.notificationText);
                }
            }
            mapping.wasPressedLastFrame = isPressed;
        }
    }

    void HandleMenuToggle()
    {
        // 左手菜单键或 X 键呼出菜单
        if (OVRInput.GetDown(OVRInput.Button.Three, OVRInput.Controller.LTouch) || 
            OVRInput.GetDown(OVRInput.Button.Start, OVRInput.Controller.LTouch))
        {
            if (uiManager != null) uiManager.ToggleMenu();
        }
    }

    float NormalizeAngle(float a)
    {
        if (a > 180f) return a - 360f;
        return a;
    }
}