using UnityEngine;
using UnityEngine.UI;
using TMPro;

public class OVRInputVisualizer : MonoBehaviour
{
    [Header("=== 左手柄摇杆 ===")]
    public Slider leftStickX;
    public Slider leftStickY;
    public TextMeshProUGUI leftStickText;

    [Header("=== 右手柄摇杆 ===")]
    public Slider rightStickX;
    public Slider rightStickY;
    public TextMeshProUGUI rightStickText;

    [Header("=== 扳机与抓取 (填入 Image 用于变色) ===")]
    public Image leftIndexTrigger;  // 左手食指扳机
    public Image leftHandTrigger;   // 左手中指抓取
    public Image rightIndexTrigger; // 右手食指扳机
    public Image rightHandTrigger;  // 右手中指抓取

    [Header("=== ABXY 按键 ===")]
    public Image btnA;
    public Image btnB;
    public Image btnX;
    public Image btnY;

    [Header("按键反馈颜色")]
    public Color idleColor = new Color(0.2f, 0.2f, 0.2f, 1f); // 暗灰色
    public Color activeColor = Color.green;                   // 绿色激活

    void Start()
    {
        // 初始化摇杆范围 -1 到 1
        SetupStickSlider(leftStickX);
        SetupStickSlider(leftStickY);
        SetupStickSlider(rightStickX);
        SetupStickSlider(rightStickY);
    }

    private void SetupStickSlider(Slider s)
    {
        if (s == null) return;
        s.minValue = -1f;
        s.maxValue = 1f;
        s.value = 0f;
    }

    void Update()
    {
        // 1. 读取摇杆二维向量 (Thumbstick)
        Vector2 lStick = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, OVRInput.Controller.LTouch);
        Vector2 rStick = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, OVRInput.Controller.RTouch);

        if (leftStickX) leftStickX.value = lStick.x;
        if (leftStickY) leftStickY.value = lStick.y;
        if (leftStickText) leftStickText.text = $"L-Stick: ({lStick.x:F2}, {lStick.y:F2})";

        if (rightStickX) rightStickX.value = rStick.x;
        if (rightStickY) rightStickY.value = rStick.y;
        if (rightStickText) rightStickText.text = $"R-Stick: ({rStick.x:F2}, {rStick.y:F2})";

        // 2. 读取扳机 (返回 0.0 到 1.0 的模拟量，大于 0.5 视为变色)
        float lIndex = OVRInput.Get(OVRInput.Axis1D.PrimaryIndexTrigger, OVRInput.Controller.LTouch);
        float lHand = OVRInput.Get(OVRInput.Axis1D.PrimaryHandTrigger, OVRInput.Controller.LTouch);
        float rIndex = OVRInput.Get(OVRInput.Axis1D.PrimaryIndexTrigger, OVRInput.Controller.RTouch);
        float rHand = OVRInput.Get(OVRInput.Axis1D.PrimaryHandTrigger, OVRInput.Controller.RTouch);

        UpdateImageColor(leftIndexTrigger, lIndex > 0.1f);
        UpdateImageColor(leftHandTrigger, lHand > 0.1f);
        UpdateImageColor(rightIndexTrigger, rIndex > 0.1f);
        UpdateImageColor(rightHandTrigger, rHand > 0.1f);

        // 3. 读取经典四键
        bool isA = OVRInput.Get(OVRInput.Button.One, OVRInput.Controller.RTouch);
        bool isB = OVRInput.Get(OVRInput.Button.Two, OVRInput.Controller.RTouch);
        bool isX = OVRInput.Get(OVRInput.Button.One, OVRInput.Controller.LTouch);
        bool isY = OVRInput.Get(OVRInput.Button.Two, OVRInput.Controller.LTouch);

        UpdateImageColor(btnA, isA);
        UpdateImageColor(btnB, isB);
        UpdateImageColor(btnX, isX);
        UpdateImageColor(btnY, isY);
    }

    private void UpdateImageColor(Image img, bool isActive)
    {
        if (img != null)
        {
            img.color = isActive ? activeColor : idleColor;
        }
    }
}