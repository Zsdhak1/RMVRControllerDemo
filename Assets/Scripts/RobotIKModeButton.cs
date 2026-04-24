using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// 可序列化的机械臂姿态数据
/// </summary>
[Serializable]
public class RobotPose
{
    [Tooltip("姿势名称，可用于按钮文本显示")]
    public string poseName = "Pose";

    [Tooltip("J1~J7 的七个关节角度（单位：度）")]
    public float[] jointAngles = new float[] { 0f, 45f, -90f, 0f, 0f, 0f, 0f };

    /// <summary>
    /// 返回安全的 7 元素角度数组；若长度不足则补齐为 0
    /// </summary>
    public float[] GetSafeAngles()
    {
        float[] result = new float[7];
        if (jointAngles != null)
        {
            for (int i = 0; i < Mathf.Min(jointAngles.Length, 7); i++)
                result[i] = jointAngles[i];
        }
        return result;
    }
}

/// <summary>
/// 机械臂控制模式切换按钮 - 可直接挂在 TMP 按钮上
/// 支持：模式切换 / 重置姿态 / 自定义姿态（单姿态 or 多姿态列表 + 循环切换）
/// </summary>
[RequireComponent(typeof(Button))]
public class RobotIKModeButton : MonoBehaviour
{
    [Header("目标 IK 控制器")]
    [Tooltip("拖拽目标 RobotIKController 到此处")]
    public RobotIKController ikController;

    [Header("按钮功能")]
    [Tooltip("选择此按钮的功能")]
    public ButtonFunction buttonFunction = ButtonFunction.SetIKMode;

    [Header("可选：按钮文本更新")]
    [Tooltip("如果有 TMP_Text 组件，自动更新显示当前模式/姿态")]
    public TextMeshProUGUI buttonText;

    [Header("可选：模式指示器")]
    [Tooltip("切换模式时高亮显示当前激活的按钮")]
    public Image highlightImage;
    public Color activeColor = new Color(0.2f, 0.8f, 0.2f, 1f);
    public Color inactiveColor = new Color(0.3f, 0.3f, 0.3f, 1f);

    [Header("自定义姿态")]
    [Tooltip("姿态预设列表。SetCustomPose/NextPose/PreviousPose/SetPoseByIndex 均从此列表读取")]
    public List<RobotPose> posePresets = new List<RobotPose>();

    [Tooltip("当 ButtonFunction 为 SetPoseByIndex 时，切换到该索引对应的姿态")]
    public int targetPoseIndex = 0;

    // 兼容旧版本的单姿态数据（不再在 Inspector 中显示，但保留反序列化）
    [SerializeField, HideInInspector]
    private float[] customPoseAngles = new float[] { 0f, 45f, -90f, 0f, 0f, 0f, 0f };

    // 全局共享的循环索引，保证多个 Next/Prev 按钮状态对齐
    private static int s_globalPoseIndex = 0;

    private Button button;

    public enum ButtonFunction
    {
        SetIKMode,                  // 设置为逆运动学模式
        SetDirectMode,              // 设置为直接角度映射模式
        ToggleMode,                 // 在两种模式之间切换
        ResetPose,                  // 重置到初始姿态
        SetCustomPose,              // 设置到 posePresets[0]（单姿态快捷方式）
        NextPose,                   // 切换到下一个姿态（循环）
        PreviousPose,               // 切换到上一个姿态（循环）
        SetPoseByIndex,             // 按 targetPoseIndex 设置指定姿态
        TogglePositionOnlyMode,     // 切换仅位置模式（J4-J7 固定）
        ToggleBoundaryCheck,        // 循环切换边界检测模式
    }

    void Awake()
    {
        button = GetComponent<Button>();
        if (button != null)
        {
            button.onClick.AddListener(OnButtonClick);
        }

        if (buttonText == null)
        {
            buttonText = GetComponentInChildren<TextMeshProUGUI>();
        }

        if (highlightImage == null)
        {
            highlightImage = GetComponent<Image>();
        }

        // 兼容旧数据：如果 posePresets 为空但旧版 customPoseAngles 有数据，自动迁移
        MigrateLegacyPoseData();
    }

    void OnEnable()
    {
        UpdateVisualState();
    }

    void Update()
    {
        if (buttonFunction == ButtonFunction.SetIKMode ||
            buttonFunction == ButtonFunction.SetDirectMode ||
            buttonFunction == ButtonFunction.TogglePositionOnlyMode ||
            buttonFunction == ButtonFunction.ToggleBoundaryCheck)
        {
            UpdateVisualState();
        }
    }

    void OnButtonClick()
    {
        if (ikController == null)
        {
            Debug.LogError("[RobotIKModeButton] 未设置 IK Controller！请在 Inspector 中拖拽 RobotIKController");
            return;
        }

        switch (buttonFunction)
        {
            case ButtonFunction.SetIKMode:
                ikController.SetModeIK();
                Debug.Log("[RobotIKModeButton] 已切换到逆运动学模式");
                break;

            case ButtonFunction.SetDirectMode:
                ikController.SetModeDirect();
                Debug.Log("[RobotIKModeButton] 已切换到直接角度映射模式");
                break;

            case ButtonFunction.ToggleMode:
                ikController.ToggleMode();
                var newMode = ikController.GetControlMode();
                Debug.Log($"[RobotIKModeButton] 已切换到 {newMode} 模式");
                break;

            case ButtonFunction.ResetPose:
                ikController.ResetToInitialPose();
                Debug.Log("[RobotIKModeButton] 已重置到初始姿态");
                break;

            case ButtonFunction.SetCustomPose:
                ApplyPoseByListIndex(0);
                break;

            case ButtonFunction.NextPose:
                if (!ValidatePoseList()) break;
                s_globalPoseIndex = (s_globalPoseIndex + 1) % posePresets.Count;
                ApplyPoseByListIndex(s_globalPoseIndex);
                break;

            case ButtonFunction.PreviousPose:
                if (!ValidatePoseList()) break;
                s_globalPoseIndex = (s_globalPoseIndex - 1 + posePresets.Count) % posePresets.Count;
                ApplyPoseByListIndex(s_globalPoseIndex);
                break;

            case ButtonFunction.SetPoseByIndex:
                if (!ValidatePoseList()) break;
                int clampedIndex = Mathf.Clamp(targetPoseIndex, 0, posePresets.Count - 1);
                s_globalPoseIndex = clampedIndex;
                ApplyPoseByListIndex(clampedIndex);
                break;

            case ButtonFunction.TogglePositionOnlyMode:
                ikController.TogglePositionOnlyMode();
                var posMode = ikController.GetControlMode();
                Debug.Log($"[RobotIKModeButton] {(posMode == RobotIKController.ControlMode.PositionOnly ? "已进入" : "已退出")}仅位置模式");
                break;

            case ButtonFunction.ToggleBoundaryCheck:
                ikController.CycleBoundaryCheckMode();
                var bcMode = ikController.GetBoundaryCheckMode();
                Debug.Log($"[RobotIKModeButton] 边界检测模式切换为: {bcMode}");
                break;
        }

        UpdateVisualState();
    }

    /// <summary>
    /// 更新按钮的显示状态（文字和高亮）
    /// </summary>
    void UpdateVisualState()
    {
        if (ikController == null) return;

        var currentMode = ikController.GetControlMode();
        bool isActive = false;

        switch (buttonFunction)
        {
            case ButtonFunction.SetIKMode:
                isActive = (currentMode == RobotIKController.ControlMode.InverseKinematics);
                if (buttonText != null)
                {
                    buttonText.text = isActive ? "[IK] 逆运动学" : "IK 逆运动学";
                }
                break;

            case ButtonFunction.SetDirectMode:
                isActive = (currentMode == RobotIKController.ControlMode.DirectAngleMapping);
                if (buttonText != null)
                {
                    buttonText.text = isActive ? "[Direct] 直接角度" : "Direct 直接角度";
                }
                break;

            case ButtonFunction.ToggleMode:
                if (buttonText != null)
                {
                    buttonText.text = $"切换模式\n({(currentMode == RobotIKController.ControlMode.InverseKinematics ? "IK" : "Direct")})";
                }
                break;

            case ButtonFunction.ResetPose:
                if (buttonText != null && string.IsNullOrEmpty(buttonText.text))
                {
                    buttonText.text = "重置姿态";
                }
                break;

            case ButtonFunction.SetCustomPose:
                if (buttonText != null && string.IsNullOrEmpty(buttonText.text))
                {
                    string name = (posePresets.Count > 0 && !string.IsNullOrEmpty(posePresets[0].poseName))
                        ? posePresets[0].poseName : "自定义姿态";
                    buttonText.text = name;
                }
                break;

            case ButtonFunction.NextPose:
                if (buttonText != null && string.IsNullOrEmpty(buttonText.text))
                {
                    buttonText.text = "下一个姿态";
                }
                break;

            case ButtonFunction.PreviousPose:
                if (buttonText != null && string.IsNullOrEmpty(buttonText.text))
                {
                    buttonText.text = "上一个姿态";
                }
                break;

            case ButtonFunction.SetPoseByIndex:
                if (buttonText != null && string.IsNullOrEmpty(buttonText.text))
                {
                    string name = GetPoseName(targetPoseIndex);
                    buttonText.text = $"姿态: {name}";
                }
                break;

            case ButtonFunction.TogglePositionOnlyMode:
                isActive = (currentMode == RobotIKController.ControlMode.PositionOnly);
                if (buttonText != null)
                {
                    buttonText.text = isActive ? "[仅位置] ON" : "仅位置模式";
                }
                break;

            case ButtonFunction.ToggleBoundaryCheck:
                var bcMode = ikController.GetBoundaryCheckMode();
                isActive = (bcMode != RobotIKController.BoundaryCheckMode.None);
                if (buttonText != null)
                {
                    string bcLabel = bcMode == RobotIKController.BoundaryCheckMode.FKValidation ? "FK验证"
                                   : bcMode == RobotIKController.BoundaryCheckMode.Precomputed ? "预计算"
                                   : "关闭";
                    buttonText.text = isActive ? $"[边界] {bcLabel}" : $"边界: {bcLabel}";
                }
                break;
        }

        // 更新高亮颜色（模式按钮需要）
        if (highlightImage != null && (buttonFunction == ButtonFunction.SetIKMode || buttonFunction == ButtonFunction.SetDirectMode || buttonFunction == ButtonFunction.TogglePositionOnlyMode || buttonFunction == ButtonFunction.ToggleBoundaryCheck))
        {
            highlightImage.color = isActive ? activeColor : inactiveColor;
        }
    }

    void OnDestroy()
    {
        if (button != null)
        {
            button.onClick.RemoveListener(OnButtonClick);
        }
    }

    // 快速设置方法（供编辑器脚本或其他组件调用）
    public void SetIKController(RobotIKController controller)
    {
        ikController = controller;
    }

    // ========== 内部辅助 ==========

    void MigrateLegacyPoseData()
    {
        if (posePresets == null || posePresets.Count == 0)
        {
            if (customPoseAngles != null && customPoseAngles.Length >= 7)
            {
                posePresets = new List<RobotPose>
                {
                    new RobotPose { poseName = "自定义姿态", jointAngles = (float[])customPoseAngles.Clone() }
                };
            }
        }
    }

    bool ValidatePoseList()
    {
        if (posePresets == null || posePresets.Count == 0)
        {
            Debug.LogWarning("[RobotIKModeButton] posePresets 为空，无法执行姿态切换。请在 Inspector 中配置姿态列表。");
            return false;
        }
        return true;
    }

    void ApplyPoseByListIndex(int index)
    {
        if (posePresets == null || posePresets.Count == 0) return;
        index = Mathf.Clamp(index, 0, posePresets.Count - 1);
        float[] angles = posePresets[index].GetSafeAngles();
        ikController.SetCustomPose(angles);
        Debug.Log($"[RobotIKModeButton] 已切换到姿态 [{posePresets[index].poseName}] (索引 {index}): [{string.Join(", ", angles)}]");
    }

    string GetPoseName(int index)
    {
        if (posePresets == null || posePresets.Count == 0) return "未配置";
        index = Mathf.Clamp(index, 0, posePresets.Count - 1);
        return string.IsNullOrEmpty(posePresets[index].poseName) ? $"姿态 {index}" : posePresets[index].poseName;
    }
}
