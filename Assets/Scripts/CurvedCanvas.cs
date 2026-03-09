using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// 弧形Canvas - 将已布局好的平面Canvas整体弯曲成弧形
/// 使用方法：挂载到已摆好UI的父物体上，设置centerPoint为布局中心
/// </summary>
public class CurvedCanvas : MonoBehaviour
{
    [Header("弧形参数")]
    [Tooltip("弧形半径（米）")]
    public float radius = 1.5f;
    
    [Tooltip("弧形弯曲强度(0=平面, 1=完整弧形)")]
    [Range(0f, 2f)]
    public float curveStrength = 0.5f;

    [Header("布局中心")]
    [Tooltip("布局中心点（局部坐标），以此点为基准弯曲")]
    public Vector3 centerPoint = Vector3.zero;
    
    [Tooltip("自动计算中心点（取所有子物体的中心）")]
    public bool autoCenter = true;

    [Header("轴向设置")]
    [Tooltip("弯曲轴向）")]
    public CurveAxis curveAxis = CurveAxis.Horizontal;
    
    [Tooltip("弯曲方向(1或-1)")]
    public float curveDirection = 1f;

    [Header("高级设置")]
    [Tooltip("是否包含非激活物体")]
    public bool includeInactive = false;
    
    [Tooltip("运行时实时更新（性能开销较大）")]
    public bool updateInRuntime = false;
    
    [Tooltip("平滑过渡时间（秒，0=立即）")]
    public float transitionTime = 0f;

    public enum CurveAxis { Horizontal, Vertical }

    // 存储原始位置
    private Dictionary<Transform, OriginalTransform> originalTransforms = new Dictionary<Transform, OriginalTransform>();
    private float currentCurveStrength = 0f;
    private float targetCurveStrength = 0f;
    private float transitionTimer = 0f;

    private struct OriginalTransform
    {
        public Vector3 localPosition;
        public Quaternion localRotation;
        public Vector3 localScale;
    }

    void Start()
    {
        SaveOriginalTransforms();
        targetCurveStrength = curveStrength;
        currentCurveStrength = transitionTime > 0 ? 0f : curveStrength;
        ApplyCurve();
    }

    void Update()
    {
        if (updateInRuntime && transitionTimer <= 0)
        {
            ApplyCurve();
        }

        // 处理平滑过渡
        if (transitionTimer > 0)
        {
            transitionTimer -= Time.deltaTime;
            float t = 1f - (transitionTimer / transitionTime);
            currentCurveStrength = Mathf.Lerp(0f, targetCurveStrength, Mathf.SmoothStep(0f, 1f, t));
            ApplyCurve();
        }
    }

    void OnValidate()
    {
        if (Application.isEditor && !Application.isPlaying)
        {
            if (originalTransforms.Count == 0)
            {
                SaveOriginalTransforms();
            }
            targetCurveStrength = curveStrength;
            currentCurveStrength = curveStrength;
            ApplyCurve();
        }
    }

    void OnEnable()
    {
        if (originalTransforms.Count > 0)
        {
            ApplyCurve();
        }
    }

    /// <summary>
    /// 保存所有子物体的原始变换
    /// </summary>
    public void SaveOriginalTransforms()
    {
        originalTransforms.Clear();
        
        RectTransform[] children = GetComponentsInChildren<RectTransform>(includeInactive);
        foreach (var child in children)
        {
            if (child == transform) continue; // 跳过自己
            
            originalTransforms[child] = new OriginalTransform
            {
                localPosition = child.localPosition,
                localRotation = child.localRotation,
                localScale = child.localScale
            };
        }

        if (autoCenter && originalTransforms.Count > 0)
        {
            CalculateAutoCenter();
        }
    }

    /// <summary>
    /// 自动计算布局中心
    /// </summary>
    void CalculateAutoCenter()
    {
        Vector3 min = Vector3.one * float.MaxValue;
        Vector3 max = Vector3.one * float.MinValue;

        foreach (var kvp in originalTransforms)
        {
            Vector3 pos = kvp.Value.localPosition;
            min = Vector3.Min(min, pos);
            max = Vector3.Max(max, pos);
        }

        centerPoint = (min + max) / 2f;
    }

    /// <summary>
    /// 应用弧形弯曲
    /// </summary>
    public void ApplyCurve()
    {
        if (originalTransforms.Count == 0) return;

        float currentRadius = radius / Mathf.Max(0.01f, currentCurveStrength);

        foreach (var kvp in originalTransforms)
        {
            Transform child = kvp.Key;
            OriginalTransform original = kvp.Value;

            // 计算相对于中心点的偏移
            Vector3 offset = original.localPosition - centerPoint;

            // 根据轴向计算角度
            float distance = curveAxis == CurveAxis.Horizontal ? offset.x : offset.y;
            float angle = distance / currentRadius * curveDirection;

            // 计算弧形上的新位置
            Vector3 curvedPosition;
            if (curveAxis == CurveAxis.Horizontal)
            {
                curvedPosition = new Vector3(
                    Mathf.Sin(angle) * currentRadius,
                    offset.y,
                    Mathf.Cos(angle) * currentRadius - currentRadius
                );
            }
            else
            {
                curvedPosition = new Vector3(
                    offset.x,
                    Mathf.Sin(angle) * currentRadius,
                    Mathf.Cos(angle) * currentRadius - currentRadius
                );
            }

            // 应用位置
            child.localPosition = centerPoint + curvedPosition;

            // 应用旋转（让元素朝向弧形切线方向）
            if (currentCurveStrength > 0.01f)
            {
                float rotAngle = -angle * Mathf.Rad2Deg * curveDirection;
                Vector3 rotAxis = curveAxis == CurveAxis.Horizontal ? Vector3.up : Vector3.right;
                child.localRotation = original.localRotation * Quaternion.AngleAxis(rotAngle, rotAxis);
            }
            else
            {
                child.localRotation = original.localRotation;
            }

            // 保持原始缩放
            child.localScale = original.localScale;
        }
    }

    /// <summary>
    /// 设置弧形强度（带过渡）
    /// </summary>
    public void SetCurveStrength(float strength, bool animate = false)
    {
        curveStrength = Mathf.Clamp(strength, 0f, 2f);
        targetCurveStrength = curveStrength;
        
        if (animate && transitionTime > 0)
        {
            transitionTimer = transitionTime;
        }
        else
        {
            currentCurveStrength = curveStrength;
            ApplyCurve();
        }
    }

    /// <summary>
    /// 临时切换为平面/弧形
    /// </summary>
    public void ToggleCurve(bool curved, bool animate = false)
    {
        SetCurveStrength(curved ? 0.5f : 0f, animate);
    }

    /// <summary>
    /// 重置为原始平面布局
    /// </summary>
    public void ResetToFlat()
    {
        currentCurveStrength = 0f;
        ApplyCurve();
    }

    /// <summary>
    /// 完全恢复原始状态
    /// </summary>
    public void RestoreOriginal()
    {
        foreach (var kvp in originalTransforms)
        {
            Transform child = kvp.Key;
            OriginalTransform original = kvp.Value;
            
            child.localPosition = original.localPosition;
            child.localRotation = original.localRotation;
            child.localScale = original.localScale;
        }
    }

    void OnDrawGizmosSelected()
    {
        // 绘制中心点和弧形参考线
        Gizmos.color = Color.yellow;
        Vector3 worldCenter = transform.TransformPoint(centerPoint);
        Gizmos.DrawWireSphere(worldCenter, 0.02f);

        // 绘制弧形参考
        if (currentCurveStrength > 0)
        {
            Gizmos.color = Color.cyan;
            float currentRadius = radius / currentCurveStrength;
            int segments = 32;
            float arcAngle = 60f * currentCurveStrength; // 显示参考弧形
            
            Vector3 prevPos = worldCenter + transform.TransformDirection(
                curveAxis == CurveAxis.Horizontal 
                    ? new Vector3(Mathf.Sin(-arcAngle * Mathf.Deg2Rad) * currentRadius, 0, Mathf.Cos(-arcAngle * Mathf.Deg2Rad) * currentRadius - currentRadius)
                    : new Vector3(0, Mathf.Sin(-arcAngle * Mathf.Deg2Rad) * currentRadius, Mathf.Cos(-arcAngle * Mathf.Deg2Rad) * currentRadius - currentRadius)
            );
            
            for (int i = 1; i <= segments; i++)
            {
                float t = (float)i / segments;
                float angle = Mathf.Lerp(-arcAngle, arcAngle, t) * Mathf.Deg2Rad;
                
                Vector3 localOffset = curveAxis == CurveAxis.Horizontal
                    ? new Vector3(Mathf.Sin(angle) * currentRadius, 0, Mathf.Cos(angle) * currentRadius - currentRadius)
                    : new Vector3(0, Mathf.Sin(angle) * currentRadius, Mathf.Cos(angle) * currentRadius - currentRadius);
                
                Vector3 pos = worldCenter + transform.TransformDirection(localOffset);
                Gizmos.DrawLine(prevPos, pos);
                prevPos = pos;
            }
        }
    }
}
