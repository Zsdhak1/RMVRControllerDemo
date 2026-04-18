using UnityEngine;

/// <summary>
/// 弧形槽位 - 位于 ArcConsoleManager 下的单个吸附点
/// </summary>
public class ArcSnapSlot : MonoBehaviour
{
    [Tooltip("当前吸附在此槽位上的 Canvas（null 表示空闲）")]
    public GrabbableCanvasSnap occupant;

    [Tooltip("Canvas 吸附时相对于槽位原点的局部偏移（Z+ 朝向玩家）")]
    public Vector3 attachOffset = new Vector3(0f, 0f, 0.008f);

    /// <summary>
    /// 尝试占据此槽位
    /// </summary>
    public bool TryOccupy(GrabbableCanvasSnap canvas)
    {
        if (occupant != null && occupant != canvas)
            return false;

        occupant = canvas;
        return true;
    }

    /// <summary>
    /// 释放指定 Canvas（只有当前 occupant 匹配时才释放）
    /// </summary>
    public bool Release(GrabbableCanvasSnap canvas)
    {
        if (occupant != canvas)
            return false;

        occupant = null;
        return true;
    }

    /// <summary>
    /// 检查槽位是否空闲
    /// </summary>
    public bool IsFree => occupant == null;
}
