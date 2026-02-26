using UnityEngine;
using UnityEngine.EventSystems;
using TMPro; // 如果按键里有文字

[RequireComponent(typeof(UnityEngine.UI.Button))] 
public class VirtualMacroButton : MonoBehaviour, IPointerDownHandler, IPointerUpHandler, IPointerExitHandler
{
    [Header("=== 数据配置 ===")]
    [Tooltip("想要映射的键盘掩码 (可以通过按位或组合)")]
    public KeyboardBitMask mappedKeys;
    
    [Header("=== 视觉反馈设置 (可选) ===")]
    public TMP_Text buttonNameText; 
    public string notificationMessage = "";
    public EngineerUIManager uiManager; 

    // 被按下状态的记录
    private bool _isBeingPressed = false;

    // 此方法由系统外部（比如管理器）调用来动态设置它
    public void Setup(string btnName, KeyboardBitMask keysToBind, string notifyMsg, EngineerUIManager manager)
    {
        if (buttonNameText != null)
        {
            buttonNameText.text = btnName;
        }

        mappedKeys = keysToBind;
        notificationMessage = notifyMsg;
        uiManager = manager;
    }

    public void OnPointerDown(PointerEventData eventData)
    {
        if (!_isBeingPressed)
        {
            _isBeingPressed = true;
            ApplyMask();

            if (!string.IsNullOrEmpty(notificationMessage) && uiManager != null)
            {
                uiManager.ShowNotification(notificationMessage);
            }
        }
    }

    public void OnPointerUp(PointerEventData eventData)
    {
        if (_isBeingPressed)
        {
            _isBeingPressed = false;
            ReleaseMask();
        }
    }

    public void OnPointerExit(PointerEventData eventData)
    {
        // 射线移开了，也要防止幽灵长按
        if (_isBeingPressed)
        {
            _isBeingPressed = false;
            ReleaseMask();
        }
    }

    private void ApplyMask()
    {
        EngineerVRInput.ExternalUIMacroMask |= (uint)mappedKeys;
    }

    private void ReleaseMask()
    {
        EngineerVRInput.ExternalUIMacroMask &= ~(uint)mappedKeys;
    }

    void OnDisable()
    {
        if (_isBeingPressed)
        {
            _isBeingPressed = false;
            ReleaseMask();
        }
    }
}