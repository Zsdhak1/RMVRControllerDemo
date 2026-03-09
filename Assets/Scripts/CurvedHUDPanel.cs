using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// 弧形HUD面板 - VR World Space 弧形布局
/// 使用方法：挂载到包含UI子物体的父物体上（Canvas模式需为World Space）
/// </summary>
public class CurvedHUDPanel : MonoBehaviour
{
    [Header("弧形参数")]
    [Tooltip("弧形半径（米）")]
    public float radius = 1.2f;
    
    [Tooltip("弧形角度范围（度）")]
    [Range(30f, 240f)]
    public float arcAngle = 160f;

    [Header("整体偏移")]
    [Tooltip("整体位置偏移")]
    public Vector3 positionOffset = Vector3.zero;
    
    [Tooltip("整体旋转偏移")]
    public Vector3 rotationOffset = Vector3.zero;

    [Header("布局设置")]
    [Tooltip("是否自动排列子物体")]
    public bool autoArrangeOnStart = true;
    
    [Tooltip("元素是否朝向弧形中心")]
    public bool faceCenter = true;
    
    [Tooltip("元素在弧形上的相对位置(0=底部, 1=顶部)")]
    [Range(0f, 1f)]
    public float heightPosition = 0.3f;

    [Header("元素独立偏移")]
    [Tooltip("启用元素独立偏移调节")]
    public bool useElementOffsets = false;
    
    [System.Serializable]
    public class ElementOffset
    {
        [Tooltip("目标元素名称（留空则按索引匹配）")]
        public string elementName = "";
        
        [Tooltip("位置偏移（局部坐标）")]
        public Vector3 positionOffset = Vector3.zero;
        
        [Tooltip("旋转偏移）")]
        public Vector3 rotationOffset = Vector3.zero;
        
        [Tooltip("缩放）")]
        public Vector3 scale = Vector3.one;
        
        [HideInInspector]
        public int targetIndex = -1;
    }
    
    [Tooltip("元素偏移列表（按索引顺序匹配子物体）")]
    public List<ElementOffset> elementOffsets = new List<ElementOffset>();

    private List<RectTransform> childElements = new List<RectTransform>();

    void Start()
    {
        if (autoArrangeOnStart)
        {
            RefreshLayout();
        }
    }

    void OnValidate()
    {
        if (Application.isEditor)
        {
            RefreshLayout();
        }
    }

    /// <summary>
    /// 刷新弧形布局
    /// </summary>
    public void RefreshLayout()
    {
        CollectChildElements();
        ArrangeInArc();
    }

    /// <summary>
    /// 收集所有子UI元素
    /// </summary>
    void CollectChildElements()
    {
        childElements.Clear();
        
        foreach (Transform child in transform)
        {
            if (child.gameObject.activeInHierarchy)
            {
                RectTransform rt = child.GetComponent<RectTransform>();
                if (rt != null)
                {
                    childElements.Add(rt);
                }
            }
        }
    }

    /// <summary>
    /// 将元素排列成弧形
    /// </summary>
    void ArrangeInArc()
    {
        if (childElements.Count == 0) return;

        float angleStep = arcAngle / Mathf.Max(1, childElements.Count - 1);
        float startAngle = -arcAngle / 2f;

        for (int i = 0; i < childElements.Count; i++)
        {
            float angle = startAngle + angleStep * i;
            PositionElement(childElements[i], i, angle);
        }
    }

    /// <summary>
    /// 定位元素到指定角度
    /// </summary>
    void PositionElement(RectTransform element, int index, float angle)
    {
        float radian = angle * Mathf.Deg2Rad;
        
        // 基础弧形位置
        float y = Mathf.Lerp(-0.2f, 0.4f, heightPosition);
        Vector3 basePos = new Vector3(
            Mathf.Sin(radian) * radius,
            y,
            Mathf.Cos(radian) * radius - radius
        );

        // 应用整体偏移
        Vector3 finalPos = basePos + positionOffset;

        // 应用元素独立偏移
        Vector3 finalRot = faceCenter ? new Vector3(0, -angle, 0) : Vector3.zero;
        Vector3 finalScale = Vector3.one;

        if (useElementOffsets && index < elementOffsets.Count)
        {
            ElementOffset offset = elementOffsets[index];
            finalPos += offset.positionOffset;
            finalRot += offset.rotationOffset + rotationOffset;
            finalScale = offset.scale;
        }
        else
        {
            finalRot += rotationOffset;
        }

        element.localPosition = finalPos;
        element.localRotation = Quaternion.Euler(finalRot);
        element.localScale = finalScale;
    }

    /// <summary>
    /// 添加元素到弧形
    /// </summary>
    public void AddElement(RectTransform element)
    {
        element.SetParent(transform, false);
        RefreshLayout();
    }

    /// <summary>
    /// 设置弧形参数
    /// </summary>
    public void SetArcParameters(float newRadius, float newArcAngle)
    {
        radius = newRadius;
        arcAngle = newArcAngle;
        RefreshLayout();
    }

    /// <summary>
    /// 设置整体偏移
    /// </summary>
    public void SetOffset(Vector3 posOffset, Vector3 rotOffset)
    {
        positionOffset = posOffset;
        rotationOffset = rotOffset;
        RefreshLayout();
    }

    /// <summary>
    /// 获取元素当前的独立偏移（没有则创建）
    /// </summary>
    public ElementOffset GetOrCreateElementOffset(int index)
    {
        while (elementOffsets.Count <= index)
        {
            elementOffsets.Add(new ElementOffset());
        }
        return elementOffsets[index];
    }

    void OnDrawGizmosSelected()
    {
        // 绘制弧形辅助线
        Gizmos.color = Color.cyan;
        
        float startAngle = -arcAngle / 2f * Mathf.Deg2Rad;
        float endAngle = arcAngle / 2f * Mathf.Deg2Rad;
        int segments = 32;
        
        float y = Mathf.Lerp(-0.2f, 0.4f, heightPosition) + positionOffset.y;
        Vector3 offset = new Vector3(positionOffset.x, 0, positionOffset.z);
        
        Vector3 prevPos = transform.TransformPoint(offset + new Vector3(
            Mathf.Sin(startAngle) * radius,
            y,
            Mathf.Cos(startAngle) * radius - radius
        ));
        
        for (int i = 1; i <= segments; i++)
        {
            float t = (float)i / segments;
            float angle = Mathf.Lerp(startAngle, endAngle, t);
            
            Vector3 pos = transform.TransformPoint(offset + new Vector3(
                Mathf.Sin(angle) * radius,
                y,
                Mathf.Cos(angle) * radius - radius
            ));
            
            Gizmos.DrawLine(prevPos, pos);
            prevPos = pos;
        }
    }
}
