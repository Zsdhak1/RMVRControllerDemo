using UnityEngine;
using UnityEngine.UI;
using System.Collections.Generic;

/// <summary>
/// 自定义 UGUI 折线图组件
/// 继承 MaskableGraphic，使用 OnPopulateMesh 生成折线 mesh，GPU 渲染，无 Texture 开销
/// </summary>
public class UILineChart : MaskableGraphic
{
    [Header("数据")]
    [Tooltip("数据点列表，X 按索引均匀分布，Y 为实际值")]
    public List<float> dataPoints = new List<float>();

    [Header("Y 轴范围")]
    [Tooltip("Y 轴最小值，留空则自动从数据推导")]
    public float yMin = -180f;

    [Tooltip("Y 轴最大值，留空则自动从数据推导")]
    public float yMax = 180f;

    [Tooltip("是否自动根据数据调整 Y 轴范围")]
    public bool autoScaleY = false;

    [Header("外观")]
    [Tooltip("折线颜色")]
    public Color lineColor = Color.cyan;

    [Tooltip("折线粗细（像素）")]
    public float lineThickness = 2f;

    [Tooltip("是否填充折线下方的区域")]
    public bool fillArea = false;

    [Tooltip("填充颜色（通常比 lineColor 更透明）")]
    public Color fillColor = new Color(0f, 0.8f, 1f, 0.15f);

    [Tooltip("背景网格线颜色")]
    public Color gridColor = new Color(1f, 1f, 1f, 0.1f);

    [Tooltip("水平网格线数量（不含边界）")]
    public int horizontalGridLines = 3;

    [Tooltip("垂直网格线数量（不含边界）")]
    public int verticalGridLines = 4;

    [Tooltip("网格线粗细（像素）")]
    public float gridThickness = 1f;

    [Tooltip("是否显示网格线")]
    public bool showGrid = true;

    [Tooltip("左右边距（0~1，相对于 rect 宽度）")]
    public float paddingX = 0.02f;

    [Tooltip("上下边距（0~1，相对于 rect 高度）")]
    public float paddingY = 0.1f;

    private RectTransform rectTransform;

    protected override void Awake()
    {
        base.Awake();
        rectTransform = GetComponent<RectTransform>();
        color = lineColor;
    }

    /// <summary>
    /// 设置数据并触发重绘
    /// </summary>
    public void SetData(List<float> data)
    {
        dataPoints = data ?? new List<float>();
        if (autoScaleY && dataPoints.Count > 0)
        {
            float min = dataPoints[0];
            float max = dataPoints[0];
            foreach (float v in dataPoints)
            {
                if (v < min) min = v;
                if (v > max) max = v;
            }
            float margin = (max - min) * 0.1f + 1f;
            yMin = min - margin;
            yMax = max + margin;
        }
        SetVerticesDirty();
    }

    protected override void OnPopulateMesh(VertexHelper vh)
    {
        vh.Clear();

        if (dataPoints == null || dataPoints.Count < 2)
            return;

        Rect rect = rectTransform.rect;
        float w = rect.width;
        float h = rect.height;

        float left = paddingX * w;
        float right = w - paddingX * w;
        float bottom = paddingY * h;
        float top = h - paddingY * h;

        float drawW = right - left;
        float drawH = top - bottom;

        // 绘制网格线
        if (showGrid)
        {
            // 水平网格线
            for (int i = 1; i <= horizontalGridLines; i++)
            {
                float t = (float)i / (horizontalGridLines + 1);
                float y = bottom + drawH * t;
                DrawLine(vh, new Vector2(left, y), new Vector2(right, y), gridThickness, gridColor);
            }
            // 垂直网格线
            for (int i = 1; i <= verticalGridLines; i++)
            {
                float t = (float)i / (verticalGridLines + 1);
                float x = left + drawW * t;
                DrawLine(vh, new Vector2(x, bottom), new Vector2(x, top), gridThickness, gridColor);
            }
        }

        // 生成折线顶点
        int count = dataPoints.Count;
        float xStep = drawW / Mathf.Max(1, count - 1);
        float yRange = yMax - yMin;
        if (Mathf.Abs(yRange) < 0.001f) yRange = 1f;

        // 填充区域
        if (fillArea)
        {
            for (int i = 0; i < count - 1; i++)
            {
                float x0 = left + i * xStep;
                float x1 = left + (i + 1) * xStep;
                float y0 = bottom + (dataPoints[i] - yMin) / yRange * drawH;
                float y1 = bottom + (dataPoints[i + 1] - yMin) / yRange * drawH;

                int baseIdx = vh.currentVertCount;
                vh.AddVert(new Vector3(x0, bottom, 0), fillColor, Vector2.zero);
                vh.AddVert(new Vector3(x0, y0, 0), fillColor, Vector2.zero);
                vh.AddVert(new Vector3(x1, y1, 0), fillColor, Vector2.zero);
                vh.AddVert(new Vector3(x1, bottom, 0), fillColor, Vector2.zero);

                vh.AddTriangle(baseIdx, baseIdx + 1, baseIdx + 2);
                vh.AddTriangle(baseIdx, baseIdx + 2, baseIdx + 3);
            }
        }

        // 折线
        for (int i = 0; i < count - 1; i++)
        {
            float x0 = left + i * xStep;
            float x1 = left + (i + 1) * xStep;
            float y0 = bottom + (dataPoints[i] - yMin) / yRange * drawH;
            float y1 = bottom + (dataPoints[i + 1] - yMin) / yRange * drawH;

            DrawLine(vh, new Vector2(x0, y0), new Vector2(x1, y1), lineThickness, lineColor);
        }
    }

    /// <summary>
    /// 绘制一条有宽度的线段（带状 mesh）
    /// </summary>
    void DrawLine(VertexHelper vh, Vector2 start, Vector2 end, float thickness, Color col)
    {
        Vector2 dir = end - start;
        if (dir.sqrMagnitude < 0.0001f) return;

        Vector2 normal = new Vector2(-dir.y, dir.x).normalized * thickness * 0.5f;

        int baseIdx = vh.currentVertCount;
        vh.AddVert(new Vector3(start.x + normal.x, start.y + normal.y, 0), col, Vector2.zero);
        vh.AddVert(new Vector3(start.x - normal.x, start.y - normal.y, 0), col, Vector2.zero);
        vh.AddVert(new Vector3(end.x - normal.x, end.y - normal.y, 0), col, Vector2.zero);
        vh.AddVert(new Vector3(end.x + normal.x, end.y + normal.y, 0), col, Vector2.zero);

        vh.AddTriangle(baseIdx, baseIdx + 1, baseIdx + 2);
        vh.AddTriangle(baseIdx, baseIdx + 2, baseIdx + 3);
    }

    /// <summary>
    /// 强制刷新网格
    /// </summary>
    public void Refresh()
    {
        SetVerticesDirty();
    }
}
