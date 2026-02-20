using UnityEngine;
using UnityEngine.XR;

public class EngineerVRInput : MonoBehaviour
{
    [Header("设置")]
    public Transform cameraRig; // 请拖入 XR Origin / Camera Offset / Player Root
    public EngineerUIManager uiManager; // 引用上面的 UI 脚本

    [Header("视角控制 (模式A: 拖拽)")]
    public float sensitivity = 1.5f; // 旋转倍率，1.0 = 1:1跟随
    
    // 内部状态
    private bool isDragging = false;
    private Quaternion startControllerRot;
    private float startRigYaw;

    void Update()
    {
        HandleCameraDrag();
        HandleMenuToggle();
    }

    // 1. 左手 Grip 拖拽视角 (模式 A)
    void HandleCameraDrag()
    {
        // 读取左手 Grip (OVRInput 映射到 PrimaryHandTrigger)
        // 注意：这里使用 OVRInput 是为了配合你的 Meta SDK 环境
        // 如果你纯用 OpenXR，可能需要改用 UnityEngine.InputSystem
        bool gripHeld = OVRInput.Get(OVRInput.Button.PrimaryHandTrigger, OVRInput.Controller.LTouch);

        if (gripHeld)
        {
            Quaternion currentRot = OVRInput.GetLocalControllerRotation(OVRInput.Controller.LTouch);

            if (!isDragging)
            {
                //刚按下：记录初始状态
                isDragging = true;
                startControllerRot = currentRot;
                startRigYaw = cameraRig.eulerAngles.y;
            }
            else
            {
                // 拖拽中：计算手柄转了多少度
                // 计算当前手柄相对于初始手柄的旋转差
                Quaternion deltaRot = currentRot * Quaternion.Inverse(startControllerRot);
                
                // 获取 Y 轴旋转分量 (Yaw)
                float deltaYaw = deltaRot.eulerAngles.y;
                
                // 处理 0-360 的跳变问题，将其转换为 -180 到 180
                if (deltaYaw > 180) deltaYaw -= 360;

                // 应用旋转：
                // 如果我手向左转(-)，视角应该向左转(-)，所以是加法还是减法取决于你的"抓取"体感
                // 既然是"抓着世界"，手向左转，世界(CameraRig)应该跟随向左转
                float targetYaw = startRigYaw - (deltaYaw * sensitivity);

                Vector3 newRot = cameraRig.eulerAngles;
                newRot.y = targetYaw;
                cameraRig.eulerAngles = newRot;
            }
        }
        else
        {
            isDragging = false;
        }
    }

    // 2. 左手 X 键呼出菜单
    void HandleMenuToggle()
    {
        // 修复：使用 Button.Three 代表左手控制器的 X 键 (或者 Button.One 如果是老式通用映射)
        // 同时也监听 Start 键 (菜单键)
        if (OVRInput.GetDown(OVRInput.Button.Three, OVRInput.Controller.LTouch) || 
            OVRInput.GetDown(OVRInput.Button.Start, OVRInput.Controller.LTouch))
        {
            if (uiManager != null)
            {
                uiManager.ToggleMenu();
            }
        }
    }
}