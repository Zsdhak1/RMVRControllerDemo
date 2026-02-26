using UnityEngine;
using System.Collections.Generic;
using UnityEngine.UI;

// 定义在 Inspector 中可见的可配置宏结构体
[System.Serializable]
public class UIMacroConfig
{
    [Tooltip("按钮上显示的文字标签")]
    public string buttonLabel = "抽拉全开";
    
    [Tooltip("要下发的组合键（可在下拉框多选！）")]
    public KeyboardBitMask keysToPress;
    
    [Tooltip("按下时弹出的UI提示")]
    public string popupMessage = "执行联动：抽拉全开";
}

public class MacroKeyboardSpawner : MonoBehaviour
{
    [Header("=== 生成环境配置 ===")]
    [Tooltip("存放这些自动生成按钮的父容器 (例如挂有 Grid Layout Group 的 Panel)")]
    public Transform container;
    
    [Tooltip("你的虚拟按钮预制体，上面必须带有 Button 和 VirtualMacroButton 脚本")]
    public GameObject macroButtonPrefab;

    [Tooltip("连接 UI 管理器，仅用于弹出系统右上角的通知 (ShowNotification)")]
    public EngineerUIManager uiManager;

    [Header("=== 虚拟按键绑定列表 ===")]
    [Tooltip("在这里添加按键，系统启动时会自动生成并排布好")]
    public List<UIMacroConfig> customMacros = new List<UIMacroConfig>();

    void Start()
    {
        // 安全检查：如果少了核心组件，直接阻断生成并报错
        if (macroButtonPrefab == null || container == null)
        {
            Debug.LogError("[MacroKeyboardSpawner] 预制体 (Prefab) 或容器 (Container) 没有挂载，无法生成虚拟键盘！");
            return;
        }

        SpawnKeyboard();
    }

    /// <summary>
    /// 遍历列表，将预制体实例化并注入宏数据
    /// </summary>
    private void SpawnKeyboard()
    {
        foreach (UIMacroConfig macro in customMacros)
        {
            // 实体化一个按钮对象放到容器下
            GameObject btnObj = Instantiate(macroButtonPrefab, container);
            
            // 为了在 Hierarchy 层次窗口好辨认，给它重命名
            btnObj.name = $"MacroBtn_{macro.buttonLabel}";

            // 获取其身上的基础逻辑接收器
            VirtualMacroButton virtualBtn = btnObj.GetComponent<VirtualMacroButton>();
            
            if (virtualBtn != null)
            {
                // 调用我们在 VirtualMacroButton.cs 里写好的 Setup 配置函数
                virtualBtn.Setup(
                    macro.buttonLabel, 
                    macro.keysToPress, 
                    macro.popupMessage, 
                    uiManager
                );
            }
            else
            {
                Debug.LogWarning($"[MacroKeyboardSpawner] 生成失败：你提供的预制体 [{macroButtonPrefab.name}] 上并没有挂载 VirtualMacroButton 脚本！");
            }
        }
    }
}