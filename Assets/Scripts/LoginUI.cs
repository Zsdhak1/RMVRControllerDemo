using UnityEngine;
using TMPro;           // 引用 TextMeshPro
using UnityEngine.UI;  // 引用标准 UI 组件 (Button)
using System.Collections.Generic; // List<T>

public class LoginUI : MonoBehaviour
{
    [Header("UI 组件引用 (请在 Inspector 拖拽赋值)")]
    public TMP_InputField inputIP;    // IP 输入框
    public TMP_InputField inputPort;  // 端口输入框
    public TMP_Dropdown dropdownRobotID; // 机器人ID下拉框
    public Button btnConnect;         // 连接按钮
    public TextMeshProUGUI statusText;// 状态提示文字

    [Header("界面切换")]
    public GameObject loginPanelObj;  // 登录界面 (Panel)
    public GameObject cockpitRootObj; // 驾驶舱根节点

    void Start()
    {
        // 1. 初始化：读取上次保存的配置
        GlobalConfig.LoadConfig();
        inputIP.text = GlobalConfig.CurrentIP;
        inputPort.text = GlobalConfig.CurrentPort.ToString();

        // 初始化机器人ID下拉框选项
        if (dropdownRobotID != null)
        {
            dropdownRobotID.ClearOptions();
            var options = new List<TMP_Dropdown.OptionData>();
            for (int i = 1; i <= 10; i++)
                options.Add(new TMP_Dropdown.OptionData($"红方 {i}"));
            for (int i = 101; i <= 110; i++)
                options.Add(new TMP_Dropdown.OptionData($"蓝方 {i}"));
            dropdownRobotID.AddOptions(options);

            // 恢复上次保存的选项
            string savedId = GlobalConfig.CurrentRobotID;
            int savedIndex = options.FindIndex(o => o.text.Contains(savedId));
            dropdownRobotID.value = savedIndex >= 0 ? savedIndex : 0;
            dropdownRobotID.RefreshShownValue();
        }

        // 2. 绑定按钮点击事件
        btnConnect.onClick.AddListener(OnConnectClicked);

        // 3. 【核心】订阅 DataManager 的信号
        // 意思是：当 DataManager 发生成功或失败时，请通知我
        if (DataManager.Instance != null)
        {
            DataManager.Instance.OnConnectSuccess += HandleConnectSuccess;
            DataManager.Instance.OnConnectFail += HandleConnectFail;
        }
        else
        {
            Debug.LogError("严重错误：场景中找不到 DataManager！请检查 SystemManager 物体。");
            statusText.text = "系统错误：缺失数据中心";
            btnConnect.interactable = false;
        }

        // 4. 界面初始化状态
        statusText.text = "就绪，请点击连接";
        statusText.color = Color.white;
        loginPanelObj.SetActive(true);
        cockpitRootObj.SetActive(false);
    }

    // 当点击按钮时触发
    void OnConnectClicked()
    {
        // 提取选中的机器人ID
        string robotId = "1";
        if (dropdownRobotID != null && dropdownRobotID.options.Count > 0)
        {
            string selectedText = dropdownRobotID.options[dropdownRobotID.value].text;
            // 从 "红方 1" 或 "蓝方 101" 中提取数字
            var parts = selectedText.Split(' ');
            if (parts.Length > 1) robotId = parts[parts.Length - 1];
        }

        // 1. 保存当前输入到全局配置 (硬盘)
        GlobalConfig.SaveConfig(inputIP.text, inputPort.text, robotId);

        // 2. 更新 UI 状态
        statusText.text = "正在连接服务器...";
        statusText.color = Color.yellow;
        btnConnect.interactable = false; // 禁用按钮，防止重复点击

        // 3. 命令 DataManager 发起连接
        if (DataManager.Instance != null)
        {
            DataManager.Instance.ConnectToServer();
        }
    }

    // === 回调函数：当连接成功时 ===
    void HandleConnectSuccess()
    {
        statusText.text = "连接成功！正在进入系统...";
        statusText.color = Color.green;

        // 延迟 1 秒进入驾驶舱，让用户看清“连接成功”这几个字
        Invoke(nameof(EnterCockpit), 1.0f);
    }

    // === 回调函数：当连接失败时 ===
    void HandleConnectFail(string errorMsg)
    {
        statusText.text = $"连接失败: {errorMsg}";
        statusText.color = Color.red;
        
        // 恢复按钮，允许用户改 IP 重试
        btnConnect.interactable = true; 
    }

    // 真正切换界面的逻辑
    void EnterCockpit()
    {
        loginPanelObj.SetActive(false); // 隐藏登录页
        cockpitRootObj.SetActive(true); // 显示驾驶舱
    }

    // 脚本销毁时取消订阅 (防止内存泄漏)
    void OnDestroy()
    {
        if (DataManager.Instance != null)
        {
            DataManager.Instance.OnConnectSuccess -= HandleConnectSuccess;
            DataManager.Instance.OnConnectFail -= HandleConnectFail;
        }
    }
}