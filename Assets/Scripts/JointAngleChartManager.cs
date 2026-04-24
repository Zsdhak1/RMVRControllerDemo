using UnityEngine;
using TMPro;
using System.Collections.Generic;

/// <summary>
/// 七轴关节角度实时图表管理器
/// 以 10Hz 采样机械臂输出角度，维护 30 秒历史数据（300 点），驱动 7 个 UILineChart
/// </summary>
public class JointAngleChartManager : MonoBehaviour
{
    [Header("数据源")]
    [Tooltip("机械臂 IK 控制器，读取 outAngles 和 jointLimits")]
    public RobotIKController robotController;

    [Header("图表 UI 引用")]
    [Tooltip("7 个折线图组件，顺序对应 J1~J7")]
    public UILineChart[] charts = new UILineChart[7];

    [Tooltip("7 个标题文本，顺序对应 J1~J7。留空则不更新标题")]
    public TextMeshProUGUI[] titleTexts = new TextMeshProUGUI[7];

    [Header("采样设置")]
    [Tooltip("采样间隔（秒），默认 0.1s = 10Hz")]
    public float sampleInterval = 0.1f;

    [Tooltip("历史数据最大点数（默认 300 = 30s × 10Hz）")]
    public int maxSamples = 300;

    [Header("外观")]
    [Tooltip("每轴的折线颜色，留空则使用默认配色")]
    public Color[] axisColors = new Color[7];

    [Tooltip("是否使用关节限位作为 Y 轴固定范围")]
    public bool useJointLimitsForYRange = true;

    // 内部数据缓冲区
    private List<float>[] dataBuffers;
    private float sampleTimer = 0f;
    private bool isInitialized = false;

    // 默认配色（与计划一致）
    private static readonly Color[] defaultColors = new Color[]
    {
        new Color(0.31f, 0.76f, 0.97f), // J1 浅蓝 #4FC3F7
        new Color(0.51f, 0.78f, 0.52f), // J2 浅绿 #81C784
        new Color(1.00f, 0.72f, 0.30f), // J3 浅橙 #FFB74D
        new Color(0.90f, 0.45f, 0.45f), // J4 浅红 #E57373
        new Color(0.73f, 0.41f, 0.78f), // J5 浅紫 #BA68C8
        new Color(0.30f, 0.82f, 0.88f), // J6 青色 #4DD0E1
        new Color(1.00f, 0.95f, 0.46f), // J7 浅黄 #FFF176
    };

    void Start()
    {
        InitializeBuffers();
        InitializeCharts();
        isInitialized = true;
    }

    void Update()
    {
        if (!isInitialized || robotController == null) return;

        sampleTimer += Time.deltaTime;
        if (sampleTimer >= sampleInterval)
        {
            sampleTimer -= sampleInterval;
            SampleAndUpdate();
        }
    }

    void InitializeBuffers()
    {
        dataBuffers = new List<float>[7];
        for (int i = 0; i < 7; i++)
        {
            dataBuffers[i] = new List<float>(maxSamples);
        }
    }

    void InitializeCharts()
    {
        for (int i = 0; i < 7; i++)
        {
            if (charts[i] == null) continue;

            // 设置颜色
            Color col = (axisColors != null && axisColors.Length > i && axisColors[i].a > 0.01f)
                ? axisColors[i]
                : defaultColors[i];
            charts[i].lineColor = col;
            charts[i].fillColor = new Color(col.r, col.g, col.b, 0.15f);
            charts[i].fillArea = true;

            // 使用关节限位设置 Y 轴范围
            if (useJointLimitsForYRange && robotController != null)
            {
                var limits = robotController.jointLimits;
                if (limits != null && limits.Length > i)
                {
                    charts[i].yMin = limits[i].minAngle;
                    charts[i].yMax = limits[i].maxAngle;
                    charts[i].autoScaleY = false;
                }
                else
                {
                    charts[i].yMin = -180f;
                    charts[i].yMax = 180f;
                }
            }
            else
            {
                charts[i].autoScaleY = true;
            }
        }
    }

    void SampleAndUpdate()
    {
        float[] angles = robotController.outAngles;
        if (angles == null || angles.Length < 7) return;

        for (int i = 0; i < 7; i++)
        {
            // 推入新数据
            dataBuffers[i].Add(angles[i]);
            if (dataBuffers[i].Count > maxSamples)
            {
                dataBuffers[i].RemoveAt(0);
            }

            // 刷新图表
            if (charts[i] != null)
            {
                charts[i].SetData(new List<float>(dataBuffers[i]));
            }

            // 刷新标题文本
            if (titleTexts != null && titleTexts.Length > i && titleTexts[i] != null)
            {
                string axisName = $"J{i + 1}";
                string unit = "°";
                titleTexts[i].text = $"{axisName}: {angles[i]:F1}{unit}";
            }
        }
    }

    /// <summary>
    /// 手动清空所有历史数据
    /// </summary>
    [ContextMenu("清空历史数据")]
    public void ClearAllData()
    {
        if (dataBuffers == null) return;
        for (int i = 0; i < 7; i++)
        {
            dataBuffers[i]?.Clear();
            if (charts[i] != null)
            {
                charts[i].SetData(new List<float>());
            }
        }
    }
}
