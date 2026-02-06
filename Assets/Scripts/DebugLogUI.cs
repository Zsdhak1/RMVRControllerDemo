using UnityEngine;
using TMPro;
using UnityEngine.UI;
using System.Text;
using System.Collections; // 引入协程命名空间

public class DebugLogUI : MonoBehaviour
{
    public TextMeshProUGUI logText;
    public ScrollRect scrollView;
    
    [Header("设置")]
    public int maxLogCharacters = 15000; // 限制字符数比限制行数性能更好
    public bool pauseUpdate = false;

    private StringBuilder logBuffer = new StringBuilder();

    void Start()
    {
        if (DataManager.Instance != null)
        {
            DataManager.Instance.OnDebugLog += HandleNewLog;
        }
        logText.text = "--- 系统就绪 ---";
    }

    void HandleNewLog(string message)
    {
        if (pauseUpdate) return;

        string time = System.DateTime.Now.ToString("HH:mm:ss");
        // 拼接新日志
        logBuffer.Append($"<color=grey>[{time}]</color> {message}\n");

        // 防止字符串无限增长导致内存崩溃
        if (logBuffer.Length > maxLogCharacters)
        {
            // 删掉前半部分，保留后半部分
            logBuffer.Remove(0, logBuffer.Length / 2);
            // 此时第一行可能是断裂的，但这比复杂的行数计算性能好得多
        }

        logText.text = logBuffer.ToString();

        // 启动自动滚动协程
        if (scrollView != null)
        {
            StartCoroutine(AutoScrollToBottom());
        }
    }

    // 强制滚动的协程
    IEnumerator AutoScrollToBottom()
    {
        // 1. 等待当前帧结束，确保TextMeshPro已经计算好新的文字长度
        yield return new WaitForEndOfFrame();

        // 2. 强制刷新UI布局系统，让Content变长
        Canvas.ForceUpdateCanvases();

        // 3. 将滚动位置设为0 (0是底部，1是顶部)
        scrollView.verticalNormalizedPosition = 0f;
        
        // 双重保险：有时候一次刷新不够，稍微再推一下
        // scrollView.velocity = new Vector2(0, 1000f); 
    }
    
    public void TogglePause(bool isPaused) => pauseUpdate = isPaused;

    public void ClearLogs()
    {
        logBuffer.Clear();
        logText.text = "";
    }

    void OnDestroy()
    {
        if (DataManager.Instance != null) DataManager.Instance.OnDebugLog -= HandleNewLog;
    }
}