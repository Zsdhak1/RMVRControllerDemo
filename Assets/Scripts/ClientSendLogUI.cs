using UnityEngine;
using TMPro;
using UnityEngine.UI;
using System.Text;
using System.Collections;

public class ClientSendLogUI : MonoBehaviour
{
    // 这些 public 变量就是你应该看到的槽位
    [Header("UI 组件 (请拖拽)")]
    public TextMeshProUGUI logText;
    public ScrollRect scrollView;
    
    [Header("设置")]
    public int maxLogCharacters = 10000; 
    public bool pauseUpdate = false;

    private StringBuilder logBuffer = new StringBuilder();

    void Start()
    {
        // 这里的 DataManager.Instance.OnTxLog 必须在 DataManager 中定义
        if (DataManager.Instance != null)
        {
            DataManager.Instance.OnTxLog += HandleNewLog;
        }
        logText.text = "--- 等待发送指令 ---";
    }

    void HandleNewLog(string message)
    {
        if (pauseUpdate) return;

        string time = System.DateTime.Now.ToString("HH:mm:ss");
        logBuffer.Append($"<color=orange>[{time}] >></color> {message}\n");

        if (logBuffer.Length > maxLogCharacters)
        {
            logBuffer.Remove(0, logBuffer.Length / 2);
        }

        logText.text = logBuffer.ToString();

        if (scrollView != null)
        {
            StartCoroutine(AutoScrollToBottom());
        }
    }

    IEnumerator AutoScrollToBottom()
    {
        yield return new WaitForEndOfFrame();
        Canvas.ForceUpdateCanvases();
        scrollView.verticalNormalizedPosition = 0f;
    }
    
    public void TogglePause(bool isPaused) => pauseUpdate = isPaused;

    public void ClearLogs()
    {
        logBuffer.Clear();
        logText.text = "";
    }

    void OnDestroy()
    {
        if (DataManager.Instance != null) DataManager.Instance.OnTxLog -= HandleNewLog;
    }
}