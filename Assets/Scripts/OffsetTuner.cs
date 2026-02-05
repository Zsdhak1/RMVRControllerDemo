using UnityEngine;
using UnityEngine.UI;
using TMPro;
using System.Text; // 引入 StringBuilder 用于优化文本拼接

public class OffsetTuner : MonoBehaviour
{
    [Header("目标控制器")]
    public RobotIKController ikController;

    [Header("=== 偏移调节 UI ===")]
    public Slider sliderX;
    public TextMeshProUGUI valueTextX;
    
    public Slider sliderY;
    public TextMeshProUGUI valueTextY;
    
    public Slider sliderZ;
    public TextMeshProUGUI valueTextZ;

    [Header("调节范围 (米)")]
    public float rangeMin = -0.2f;
    public float rangeMax = 0.2f;

    [Header("=== 关节数据显示 (新增) ===")]
    [Tooltip("拖入一个大的 TextMeshPro 组件，用于显示 J1-J7 的角度")]
    public TextMeshProUGUI jointStatusText;

    // 缓存字符串构建器，减少垃圾回收(GC)，提升VR性能
    private StringBuilder sb = new StringBuilder();

    void Start()
    {
        // --- 1. 初始化滑块 ---
        SetupSlider(sliderX);
        SetupSlider(sliderY);
        SetupSlider(sliderZ);

        // --- 2. 同步当前数值 ---
        if (ikController != null)
        {
            Vector3 currentOffset = ikController.gripOffset;
            sliderX.value = currentOffset.x;
            sliderY.value = currentOffset.y;
            sliderZ.value = currentOffset.z;
        }

        // --- 3. 绑定事件 ---
        sliderX.onValueChanged.AddListener(OnValuesChanged);
        sliderY.onValueChanged.AddListener(OnValuesChanged);
        sliderZ.onValueChanged.AddListener(OnValuesChanged);

        UpdateLabels();
    }

    void SetupSlider(Slider s)
    {
        s.minValue = rangeMin;
        s.maxValue = rangeMax;
    }

    void OnValuesChanged(float val)
    {
        if (ikController != null)
        {
            Vector3 newOffset = new Vector3(sliderX.value, sliderY.value, sliderZ.value);
            ikController.gripOffset = newOffset;
        }
        UpdateLabels();
    }

    void UpdateLabels()
    {
        valueTextX.text = $"X: {sliderX.value:F3}";
        valueTextY.text = $"Y: {sliderY.value:F3}";
        valueTextZ.text = $"Z: {sliderZ.value:F3}";
    }
    
    public void ResetZero()
    {
        sliderX.value = 0;
        sliderY.value = 0;
        sliderZ.value = 0;
    }

    // === 新增：每帧刷新显示关节角度 ===
    void Update()
    {
        if (jointStatusText == null || ikController == null) return;

        sb.Clear();
        sb.AppendLine("<b><color=yellow>=== 实时关节角度 ===</color></b>");

        float[] angles = ikController.outAngles;
        
        // 遍历 7 个关节
        for (int i = 0; i < 7; i++)
        {
            // 归一化角度方便阅读 (-180 ~ 180)
            float angle = NormalizeAngle(angles[i]);
            
            // 检查是否达到限位 (如果脚本里有限位配置)
            string colorHex = "white";
            bool isLimit = false;

            // 尝试读取控制器里的限位配置来变色 (高阶功能)
            // 这里做一个简单的安全检查防止数组越界
            if (i < ikController.jointLimits.Length)
            {
                var limit = ikController.jointLimits[i];
                // 接近极限 2 度时变红
                if (angle <= limit.minAngle + 2f || angle >= limit.maxAngle - 2f)
                {
                    colorHex = "#FF00FF"; // 洋红色警告
                    isLimit = true;
                }
            }

            string icon = isLimit ? "(!)" : "";
            sb.AppendLine($"<color={colorHex}>J{i + 1}: {angle, 6:F1}° {icon}</color>");
        }

        jointStatusText.text = sb.ToString();
    }

    float NormalizeAngle(float a)
    {
        while (a > 180) a -= 360;
        while (a < -180) a += 360;
        return a;
    }
}