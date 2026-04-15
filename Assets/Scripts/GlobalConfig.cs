using UnityEngine;

// 这是一个静态工具类，任何脚本在任何地方都能读到它
public static class GlobalConfig
{
    // 默认值
    private const string KEY_IP = "Saved_Server_IP";
    private const string KEY_PORT = "Saved_Server_Port";
    private const string KEY_ROBOT_ID = "Saved_Robot_ID";
    private const string DEFAULT_IP = "192.168.1.69";
    private const int DEFAULT_PORT = 3333;
    private const string DEFAULT_ROBOT_ID = "1";

    // 当前使用的配置
    public static string CurrentIP { get; private set; }
    public static int CurrentPort { get; private set; }
    public static string CurrentRobotID { get; private set; }

    // 初始化：从硬盘读取配置，如果没有就用默认值
    public static void LoadConfig()
    {
        CurrentIP = PlayerPrefs.GetString(KEY_IP, DEFAULT_IP);
        CurrentPort = PlayerPrefs.GetInt(KEY_PORT, DEFAULT_PORT);
        CurrentRobotID = PlayerPrefs.GetString(KEY_ROBOT_ID, DEFAULT_ROBOT_ID);
        Debug.Log($"[系统配置] 已读取配置: {CurrentIP}:{CurrentPort} RobotID={CurrentRobotID}");
    }

    // 保存：把用户输入的新IP存到硬盘
    public static void SaveConfig(string ip, string portStr)
    {
        CurrentIP = ip;
        if (int.TryParse(portStr, out int parsedPort))
        {
            CurrentPort = parsedPort;
        }
        else
        {
            Debug.LogWarning("[配置] 端口格式错误，使用默认端口");
            CurrentPort = DEFAULT_PORT;
        }

        PlayerPrefs.SetString(KEY_IP, CurrentIP);
        PlayerPrefs.SetInt(KEY_PORT, CurrentPort);
        PlayerPrefs.Save(); // 强制写入磁盘
        Debug.Log("[系统配置] 配置已保存");
    }

    public static void SaveConfig(string ip, string portStr, string robotId)
    {
        SaveConfig(ip, portStr);
        CurrentRobotID = robotId;
        PlayerPrefs.SetString(KEY_ROBOT_ID, CurrentRobotID);
        PlayerPrefs.Save();
        Debug.Log($"[系统配置] RobotID 已保存: {CurrentRobotID}");
    }
}