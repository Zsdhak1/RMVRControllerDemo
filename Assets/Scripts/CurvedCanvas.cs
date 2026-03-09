using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// 弧形Canvas - 将UI分块排列成弧形，保持每块内部布局不变
/// </summary>
public class CurvedCanvas : MonoBehaviour
{
    [Header("UI分块（保持内部布局）")]
    [Tooltip("将UI分为左/中/右三个部分，每个部分的内部布局保持不变")]
    public RectTransform leftPanel;
    public RectTransform centerPanel;
    public RectTransform rightPanel;

    [Header("弧形参数")]
    public float radius = 1.2f;
    public float arcAngle = 160f;
    public float heightOffset = -0.2f;

    [Header("偏移微调")]
    public Vector3 leftPanelOffset;
    public Vector3 centerPanelOffset;
    public Vector3 rightPanelOffset;

    [Header("跟随头部")]
    public Transform headTransform;
    public bool followHead = true;
    public float followSpeed = 5f;

    [Header("自动查找")]
    public bool autoFindOVRCamera = true;

    private Transform[] panels;
    private Vector3[] offsets;

    void Start()
    {
        // 自动查找相机
        if (headTransform == null && autoFindOVRCamera)
        {
            var cameraRig = FindObjectOfType<OVRCameraRig>();
            if (cameraRig != null) headTransform = cameraRig.centerEyeAnchor;
        }

        InitializePanels();
    }

    void Update()
    {
        if (followHead && headTransform != null)
        {
            UpdateFollow();
        }
    }

    void OnValidate()
    {
        if (Application.isEditor && panels != null)
        {
            RefreshLayout();
        }
    }

    void InitializePanels()
    {
        // 收集面板
        var panelList = new List<Transform>();
        var offsetList = new List<Vector3>();

        if (leftPanel != null) { panelList.Add(leftPanel); offsetList.Add(leftPanelOffset); }
        if (centerPanel != null) { panelList.Add(centerPanel); offsetList.Add(centerPanelOffset); }
        if (rightPanel != null) { panelList.Add(rightPanel); offsetList.Add(rightPanelOffset); }

        panels = panelList.ToArray();
        offsets = offsetList.ToArray();

        // 确保所有面板是World Space模式
        foreach (var p in panels)
        {
            var canvas = p.GetComponent<Canvas>();
            if (canvas == null)
            {
                canvas = p.gameObject.AddComponent<Canvas>();
            }
            canvas.renderMode = RenderMode.WorldSpace;
            
            // 添加GraphicRaycaster用于交互
            if (p.GetComponent<UnityEngine.UI.GraphicRaycaster>() == null)
            {
                p.gameObject.AddComponent<UnityEngine.UI.GraphicRaycaster>();
            }
        }

        RefreshLayout();
    }

    public void RefreshLayout()
    {
        if (panels == null || panels.Length == 0) return;

        float angleStep = arcAngle / Mathf.Max(1, panels.Length - 1);
        float startAngle = -arcAngle / 2f;

        for (int i = 0; i < panels.Length; i++)
        {
            if (panels[i] == null) continue;

            float angle = startAngle + angleStep * i;
            PositionPanel(panels[i], angle, offsets[i]);
        }
    }

    void PositionPanel(Transform panel, float angle, Vector3 offset)
    {
        float radian = angle * Mathf.Deg2Rad;
        
        // 弧形位置
        Vector3 basePos = new Vector3(
            Mathf.Sin(radian) * radius,
            heightOffset,
            Mathf.Cos(radian) * radius - radius
        );

        panel.localPosition = basePos + offset;
        panel.localRotation = Quaternion.Euler(0, -angle, 0);
        panel.localScale = Vector3.one * 0.001f; // Canvas在World Space中通常需要缩小
    }

    void UpdateFollow()
    {
        // 平滑跟随头部
        Vector3 targetPos = headTransform.position + headTransform.forward * radius;
        targetPos.y = headTransform.position.y + heightOffset;
        
        Quaternion targetRot = Quaternion.Euler(0, headTransform.eulerAngles.y, 0);

        transform.position = Vector3.Lerp(transform.position, targetPos, Time.deltaTime * followSpeed);
        transform.rotation = Quaternion.Slerp(transform.rotation, targetRot, Time.deltaTime * followSpeed);
    }

    void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.cyan;
        
        float startAngle = -arcAngle / 2f * Mathf.Deg2Rad;
        float endAngle = arcAngle / 2f * Mathf.Deg2Rad;
        int segments = 32;
        
        Vector3 prevPos = transform.TransformPoint(new Vector3(
            Mathf.Sin(startAngle) * radius,
            heightOffset,
            Mathf.Cos(startAngle) * radius - radius
        ));
        
        for (int i = 1; i <= segments; i++)
        {
            float t = (float)i / segments;
            float angle = Mathf.Lerp(startAngle, endAngle, t);
            
            Vector3 pos = transform.TransformPoint(new Vector3(
                Mathf.Sin(angle) * radius,
                heightOffset,
                Mathf.Cos(angle) * radius - radius
            ));
            
            Gizmos.DrawLine(prevPos, pos);
            prevPos = pos;
        }
    }
}
