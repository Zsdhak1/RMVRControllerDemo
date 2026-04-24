using UnityEngine;

/// <summary>
/// 预计算工作空间边界数据
/// 在PC端用脚本采样计算后导入Unity，运行时快速查表判断目标是否可达
/// </summary>
[CreateAssetMenu(fileName = "WorkspaceBoundaryData", menuName = "RMVR/Workspace Boundary Data")]
public class WorkspaceBoundaryData : ScriptableObject
{
    [Tooltip("采样时使用的J2下限（度）")]
    public float j2Min;

    [Tooltip("采样时使用的J2上限（度）")]
    public float j2Max;

    [Tooltip("采样时使用的J3下限（度）")]
    public float j3Min;

    [Tooltip("采样时使用的J3上限（度）")]
    public float j3Max;

    [Tooltip("大臂长度L1（米）")]
    public float l1BigArm;

    [Tooltip("小臂长度L2（米）")]
    public float l2SmallArm;

    [Tooltip("高度采样最小值（米，相对J2底座）")]
    public float heightMin;

    [Tooltip("高度采样最大值（米，相对J2底座）")]
    public float heightMax;

    [Tooltip("高度采样步长（米）")]
    public float heightStep;

    /// <summary>
    /// 每个高度层对应的最大水平 reach（XZ平面半径，米）
    /// 索引 0 对应 heightMin，依次递增 heightStep
    /// </summary>
    [Tooltip("各高度层最大水平 reach（米），按 heightStep 递增排列")]
    public float[] maxReachPerHeight;

    /// <summary>
    /// 查询给定相对高度的最大水平 reach
    /// </summary>
    /// <param name="relativeHeight">相对J2底座的垂直高度（米）</param>
    /// <returns>最大水平 reach（米），若超出范围则返回 0</returns>
    public float GetMaxReachAtHeight(float relativeHeight)
    {
        if (maxReachPerHeight == null || maxReachPerHeight.Length == 0)
            return 0f;

        // 限制在有效范围内
        if (relativeHeight <= heightMin)
            return maxReachPerHeight[0];
        if (relativeHeight >= heightMax)
            return maxReachPerHeight[maxReachPerHeight.Length - 1];

        int index = Mathf.FloorToInt((relativeHeight - heightMin) / heightStep);
        index = Mathf.Clamp(index, 0, maxReachPerHeight.Length - 2);

        float t = (relativeHeight - (heightMin + index * heightStep)) / heightStep;
        return Mathf.Lerp(maxReachPerHeight[index], maxReachPerHeight[index + 1], t);
    }

    /// <summary>
    /// 快速判断：给定相对高度和水平距离，是否在工作空间内
    /// </summary>
    public bool IsReachable(float relativeHeight, float horizontalDistance)
    {
        float maxReach = GetMaxReachAtHeight(relativeHeight);
        return horizontalDistance <= maxReach;
    }
}
