using UnityEngine;
using UnityEngine.UI;
using TMPro;

public class BaseTransformTuner : MonoBehaviour
{
    [Header("=== 目标引用 ===")]
    [Tooltip("这里拖入整个工程机器人的根节点 (或机械臂底座)，我们将移动这个物体，让它适应操作手的位置")]
    public Transform robotBaseRoot;

    [Header("=== 控制面板的滑动条 ===")]
    public Slider sliderX; // 左右平移 (Left / Right)
    public TextMeshProUGUI valueTextX;
    
    public Slider sliderY; // 上下高度 (Up / Down)
    public TextMeshProUGUI valueTextY;
    
    public Slider sliderZ; // 前后距离 (Forward / Backward)
    public TextMeshProUGUI valueTextZ;
    
    [Header("基座微调范围设限 (米)")]
    public float rangeX = 1.0f; // 左右各 1 米
    public float rangeY = 1.5f; // 上下各 1.5 米
    public float rangeZ = 1.0f; // 前后各 1 米

    private Vector3 initialBasePosition;

    void Start()
    {
        if (robotBaseRoot == null)
        {
            Debug.LogWarning("[BaseTransformTuner] 请在 Inspector 指定 RobotBaseRoot 的 Transform！");
            return;
        }

        // 保存启动瞬间的原始位置
        initialBasePosition = robotBaseRoot.localPosition; // 或 position 看你的场景层级结构而定

        // --- 配置滑块极值并置中 ---
        SetupSlider(sliderX, -rangeX, rangeX, 0f);
        SetupSlider(sliderY, -rangeY, rangeY, 0f);
        SetupSlider(sliderZ, -rangeZ, rangeZ, 0f);

        // --- 绑定滑动监听事件 ---
        sliderX.onValueChanged.AddListener(OnValueChange);
        sliderY.onValueChanged.AddListener(OnValueChange);
        sliderZ.onValueChanged.AddListener(OnValueChange);

        UpdateLabels();
    }

    private void SetupSlider(Slider s, float min, float max, float current)
    {
        if (s == null) return;
        s.minValue = min;
        s.maxValue = max;
        s.value = current;
    }

    // 滑动条产生变动时触发核心移动逻辑
    private void OnValueChange(float val)
    {
        if (robotBaseRoot != null)
        {
            // 通过累加初始坐标和滑动条计算新的位置
            Vector3 newOffset = new Vector3(sliderX.value, sliderY.value, sliderZ.value);
            // 建议使用 localPosition 进行位移，避免破坏世界坐标系的追踪
            robotBaseRoot.localPosition = initialBasePosition + newOffset;
        }

        UpdateLabels();
    }

    private void UpdateLabels()
    {
        if (valueTextX != null) valueTextX.text = $"水平(X): {sliderX.value:F2} m";
        if (valueTextY != null) valueTextY.text = $"高度(Y): {sliderY.value:F2} m";
        if (valueTextZ != null) valueTextZ.text = $"纵深(Z): {sliderZ.value:F2} m";
    }

    /// <summary>
    /// 可绑定给一个重置按钮的复位方法
    /// </summary>
    public void ResetToDefault()
    {
        if (sliderX) sliderX.value = 0f;
        if (sliderY) sliderY.value = 0f;
        if (sliderZ) sliderZ.value = 0f;
    }
}