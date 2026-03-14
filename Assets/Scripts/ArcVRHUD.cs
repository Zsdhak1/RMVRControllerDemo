using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// 弧形HUD - 将UI面板排列成弧形并弯曲
/// 方案：多个小段Canvas拼接成弧形
/// </summary>
public class ArcVRHUD : MonoBehaviour
{
    [Header("UI分块（已摆好布局的Canvas）")]
    public List<Canvas> uiPanels = new List<Canvas>();

    [Header("弧形参数")]
    public float radius = 1.2f;
    [Range(60f, 240f)]
    public float arcAngle = 160f;
    public float height = -0.2f;

    [Header("弯曲设置")]
    [Tooltip("每个面板之间的角度偏移（形成弯曲感）")]
    public float panelAngleOffset = 15f;

    [Header("头部跟随")]
    public Transform headTransform;
    public float followSpeed = 8f;

    void Start()
    {
        FindHead();
        SetupPanels();
        Arrange();
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
            
            if (panel.renderMode != RenderMode.WorldSpace)
                panel.renderMode = RenderMode.WorldSpace;

            // 设置合理的大小
            var rt = panel.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.localScale = Vector3.one * 0.001f;
        }
    }

    [ContextMenu("排列")]
    public void Arrange()
    {
        if (uiPanels.Count == 0) return;

        float angleStep = arcAngle / Mathf.Max(1, uiPanels.Count - 1);
        float startAngle = -arcAngle / 2f;

        for (int i = 0; i < uiPanels.Count; i++)
        {
            if (uiPanels[i] == null) continue;

            float angleDeg = startAngle + angleStep * i;
            float angleRad = angleDeg * Mathf.Deg2Rad;

            // 位置：在弧形上
            Vector3 pos = new Vector3(
                Mathf.Sin(angleRad) * radius,
                height,
                Mathf.Cos(angleRad) * radius
            );

            var rt = uiPanels[i].GetComponent<RectTransform>();
            rt.anchoredPosition3D = pos;

            // 关键：旋转面板使其朝向弧形切线方向，形成"弯曲"效果
            // 基础朝向中心 + 额外偏移产生弯曲感
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
        targetPos.y = headTransform.position.y + height;

        Quaternion targetRot = Quaternion.Euler(0, headTransform.eulerAngles.y, 0);

        transform.position = Vector3.Lerp(transform.position, targetPos, Time.deltaTime * followSpeed);
        transform.rotation = Quaternion.Slerp(transform.rotation, targetRot, Time.deltaTime * followSpeed);
    }

    void OnDrawGizmos()
    {
        if (uiPanels.Count == 0) return;

        Gizmos.color = Color.red;
        foreach (var p in uiPanels)
        {
            if (p != null)
                Gizmos.DrawWireCube(p.transform.position, Vector3.one * 0.05f);
        }

        Gizmos.color = Color.cyan;
        
        Vector3 center = Application.isPlaying ? transform.position : 
            (headTransform != null ? headTransform.position : transform.position);
        
        Quaternion rot = Application.isPlaying ? transform.rotation : 
            (headTransform != null ? Quaternion.Euler(0, headTransform.eulerAngles.y, 0) : Quaternion.identity);

        float startA = -arcAngle / 2f * Mathf.Deg2Rad;
        float endA = arcAngle / 2f * Mathf.Deg2Rad;
        
        Vector3 prev = center + rot * new Vector3(
            Mathf.Sin(startA) * radius, height, Mathf.Cos(startA) * radius);

        for (int i = 1; i <= 24; i++)
        {
            float t = i / 24f;
            float a = Mathf.Lerp(startA, endA, t);
            
            Vector3 pos = center + rot * new Vector3(
                Mathf.Sin(a) * radius, height, Mathf.Cos(a) * radius);
            
            Gizmos.DrawLine(prev, pos);
            prev = pos;
        }
    }
}
