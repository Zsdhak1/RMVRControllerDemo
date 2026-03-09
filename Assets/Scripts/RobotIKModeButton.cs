using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// 机械臂控制模式切换按钮 - 可直接挂在 TMP 按钮上
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
    [Tooltip("如果有 TMP_Text 组件，自动更新显示当前模式")]
    public TextMeshProUGUI buttonText;
    
    [Header("可选：模式指示器")]
    [Tooltip("切换模式时高亮显示当前激活的按钮")]
    public Image highlightImage;
    public Color activeColor = new Color(0.2f, 0.8f, 0.2f, 1f);
    public Color inactiveColor = new Color(0.3f, 0.3f, 0.3f, 1f);
    
    private Button button;
    
    public enum ButtonFunction
    {
        SetIKMode,              // 设置为逆运动学模式
        SetDirectMode,          // 设置为直接角度映射模式
        ToggleMode,             // 在两种模式之间切换
        ResetPose,              // 重置到初始姿态
    }
    
    void Awake()
    {
        button = GetComponent<Button>();
        if (button != null)
        {
            button.onClick.AddListener(OnButtonClick);
        }
        
        // 如果没有指定 buttonText，自动查找
        if (buttonText == null)
        {
            buttonText = GetComponentInChildren<TextMeshProUGUI>();
        }
        
        // 如果没有指定 highlightImage，自动查找按钮的 Image
        if (highlightImage == null)
        {
            highlightImage = GetComponent<Image>();
        }
    }
    
    void OnEnable()
    {
        // 每次显示时更新状态
        UpdateVisualState();
    }
    
    void Update()
    {
        // 如果是模式按钮，实时更新显示状态
        if (buttonFunction == ButtonFunction.SetIKMode || 
            buttonFunction == ButtonFunction.SetDirectMode)
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
                if (buttonText != null && buttonText.text == "")
                {
                    buttonText.text = "🔄 重置姿态";
                }
                break;
        }
        
        // 更新高亮颜色
        if (highlightImage != null && (buttonFunction == ButtonFunction.SetIKMode || buttonFunction == ButtonFunction.SetDirectMode))
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
}
