using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// 弧形HUD控制器 - 弯曲的VR HUD
/// 
/// 使用方法：
/// 1. 创建一个空物体，挂载此脚本
/// 2. 创建3个World Space Canvas作为子物体（左/中/右）
/// 3. 给每个Canvas添加 CurvedUICanvas 脚本实现弯曲
/// 4. 或者使用 CurvedUIMesh + RenderTexture 方案
/// </summary>
public class CurvedHUD : MonoBehaviour
{
    [Header("面板设置")]
    public List<Canvas> uiPanels = new List<Canvas>();

    [Header("弧形排列")]
    public float radius = 1.2f;
    [Range(60f, 240f)]
    public float arcAngle = 160f;
    public float heightOffset = -0.2f;

    [Header("弯曲效果")]
    [Tooltip("是否启用Shader弯曲")]
    public bool enableCurvature = true;
    
    [Range(0f, 2f)]
    public float curveStrength = 0.5f;

    [Header("头部跟随")]
    public Transform headTransform;
    public float followSpeed = 8f;

    void Start()
    {
        FindHead();
        SetupPanels();
        ArrangePanels();
    }

    void LateUpdate()
    {
        FollowHead();
    }

    void FindHead()
    {
        if (headTransform == null)
        {
            var ovr = FindObjectOfType<OVRCameraRig>();
            if (ovr) headTransform = ovr.centerEyeAnchor;
            else if (Camera.main) headTransform = Camera.main.transform;
        }
    }

    void SetupPanels()
    {
        foreach (var panel in uiPanels)
        {
            if (panel == null) continue;
            
            panel.transform.SetParent(transform, false);
            panel.renderMode = RenderMode.WorldSpace;

            var rt = panel.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.localScale = Vector3.one * 0.001f;

            // 自动添加弯曲组件
            if (enableCurvature)
            {
                var curved = panel.GetComponent<CurvedUICanvas>();
                if (curved == null)
                {
                    curved = panel.gameObject.AddComponent<CurvedUICanvas>();
                }
                curved.curveStrength = curveStrength;
            }
        }
    }

    [ContextMenu("排列面板")]
    public void ArrangePanels()
    {
        if (uiPanels.Count == 0) return;

        float angleStep = arcAngle / Mathf.Max(1, uiPanels.Count - 1);
        float startAngle = -arcAngle / 2f;

        for (int i = 0; i < uiPanels.Count; i++)
        {
            if (uiPanels[i] == null) continue;

            float angleDeg = startAngle + angleStep * i;
            float angleRad = angleDeg * Mathf.Deg2Rad;

            Vector3 pos = new Vector3(
                Mathf.Sin(angleRad) * radius,
                heightOffset,
                Mathf.Cos(angleRad) * radius
            );

            var rt = uiPanels[i].GetComponent<RectTransform>();
            rt.anchoredPosition3D = pos;
            rt.localRotation = Quaternion.Euler(0, -angleDeg, 0);
        }
    }

    void FollowHead()
    {
        if (headTransform == null) return;

        Vector3 forward = headTransform.forward;
        forward.y = 0;
        forward.Normalize();

        Vector3 targetPos = headTransform.position + forward * radius;
        targetPos.y = headTransform.position.y + heightOffset;

        Quaternion targetRot = Quaternion.Euler(0, headTransform.eulerAngles.y, 0);

        transform.position = Vector3.Lerp(transform.position, targetPos, Time.deltaTime * followSpeed);
        transform.rotation = Quaternion.Slerp(transform.rotation, targetRot, Time.deltaTime * followSpeed);
    }

    /// <summary>
    /// 设置弯曲强度
    /// </summary>
    public void SetCurveStrength(float strength)
    {
        curveStrength = strength;
        foreach (var panel in uiPanels)
        {
            if (panel != null)
            {
                var curved = panel.GetComponent<CurvedUICanvas>();
                if (curved != null)
                {
                    curved.SetCurveStrength(strength);
                }
            }
        }
    }
}
