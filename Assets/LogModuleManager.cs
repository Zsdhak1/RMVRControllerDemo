using UnityEngine;
using TMPro; 
using UnityEngine.UI;

public class LogModuleManager : MonoBehaviour
{
    [Header("UI Components")]
    public TMP_Text logDisplayText; 
    public TMP_InputField ipInputField;
    public Button applyButton;
    public ScrollRect scrollRect; 

    [Header("Target Controller")]
    public QuestRomoMasterController controller;

    void Start()
    {
        if (controller != null)
        {
            ipInputField.text = controller.videoPath.Replace("udp://", "");
        }

        applyButton.onClick.AddListener(OnApplyIP);
        
        // --- Changed to English ---
        AddLog("System initialized...");
        AddLog("Waiting for user interaction...");
    }

    public void AddLog(string message)
    {
        string timestamp = System.DateTime.Now.ToString("[HH:mm:ss] ");
        logDisplayText.text += timestamp + message + "\n";

        if (scrollRect != null)
        {
            Canvas.ForceUpdateCanvases();
            scrollRect.verticalNormalizedPosition = 0f;
        }
    }

    public void OnApplyIP()
    {
        if (controller != null)
        {
            string newIp = ipInputField.text;
            if (!newIp.StartsWith("udp://"))
            {
                newIp = "udp://" + newIp;
            }
            
            controller.videoPath = newIp;
            // --- Changed to English ---
            AddLog($"<color=yellow>Target IP changed to: {newIp}</color>");
            AddLog("Changes will apply on next spawn.");
        }
    }
}