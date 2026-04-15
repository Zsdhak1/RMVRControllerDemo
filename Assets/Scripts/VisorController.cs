using UnityEngine;
//using Cysharp.Threading.Tasks; // 引用了你项目里的 UniTask

public class VisorController : MonoBehaviour
{
    [Header("面罩引用的根节点")]
    [Tooltip("请拖入你刚组装好的包含了图传和UI的总父物体 CockpitRoot")]
    public GameObject cockpitRoot;
    
    [Header("位置复位参考基准")]
    [Tooltip("比如你的玩家头部视角 (CenterEyeAnchor)")]
    public Transform headTransform;
    
    [Header("复位偏移量")]
    public Vector3 defaultOffset = new Vector3(0, 0, 1.5f); // 默认在眼前 1.5 米处
    public float defaultScale = 1.0f; // 默认缩放大小

    private bool isVisorMuted = false; // 当前是否处于紧急瞎子模式
    private CanvasGroup[] allCanvasGroups; 

    void Start()
    {
        // 缓存下级所有 CanvasGroup 来实现优雅的淡入淡出（如果你懒得做动画可以直接 SetActive(false)）
        // 建议在图传或 UI 画面的 Root 上挂载 CanvasGroup，而不是把整个结构 SetActive 关掉，
        // 这样不会打断 UDP 的内部接收循环逻辑 (StreamForwarder是独立线程无所谓，但有些协程会中断)
        if (cockpitRoot != null)
        {
            allCanvasGroups = cockpitRoot.GetComponentsInChildren<CanvasGroup>();
        }
    }

    [Header("临时禁用开关")]
    [Tooltip("禁用手柄对面罩的控制")]
    public bool disableVisorInput = true;

    void Update()
    {
        if (disableVisorInput) return;

        // === 紧急隐藏面罩：按下左手柄或右手柄的摇杆 (Thumbstick Click) 触发 ===
        // 一般来说 LTouch 摇杆按下比较少冲突
        if (OVRInput.GetDown(OVRInput.Button.PrimaryThumbstick, OVRInput.Controller.LTouch))
        {
            ToggleVisor();
        }

        // === 强制复位：长按 Y 键 1秒钟将漫游的图传强制拉回眼前 ===
        if (OVRInput.Get(OVRInput.Button.Four, OVRInput.Controller.LTouch))
        {
            // 长按复位逻辑，可以直接写在这里或另外拆分协程
            ResetVisorPosition();
        }
    }

    private void ToggleVisor()
    {
        isVisorMuted = !isVisorMuted;

        if (cockpitRoot != null)
        {
            // 粗暴模式：直接隐藏整个树，但注意别把你挂在上面的收发核心关了！
            // cockpitRoot.SetActive(!isVisorMuted); 

            // 安全模式：如果有 CanvasGroup，调整透明度和射线遮挡。
            // 对于非 Canvas 的图传 Quad（MeshRenderer），调整它的 Renderer enabled
            SetVisibility(!isVisorMuted);
        }
    }

    private void SetVisibility(bool isVisible)
    {
        // 1. 处理 UI 级
        if (allCanvasGroups != null)
        {
            foreach (var cg in allCanvasGroups)
            {
                cg.alpha = isVisible ? 1f : 0f;
                cg.blocksRaycasts = isVisible;
                cg.interactable = isVisible;
            }
        }

        // 2. 处理 3D 对象级 (比如图传 Quad 的 MeshRenderer)
        MeshRenderer[] renderers = cockpitRoot.GetComponentsInChildren<MeshRenderer>();
        foreach (var r in renderers)
        {
            r.enabled = isVisible;
        }

        // 如果你项目用了 RawImage 放图传，也会被第一步的 CanvasGroup 管到。
    }

    /// <summary>
    /// 当把面罩扯太远找不到时，一键拉回面前
    /// </summary>
    public void ResetVisorPosition()
    {
        if (cockpitRoot == null || headTransform == null) return;

        // 设置到相机的正前方指定距离，去除 Y 轴的仰角，保证屏幕垂直面对操作员
        Vector3 flatForward = headTransform.forward;
        flatForward.y = 0; 
        flatForward.Normalize();

        cockpitRoot.transform.position = headTransform.position + (flatForward * defaultOffset.z) + new Vector3(0, defaultOffset.y, 0);
        
        // 朝向玩家
        cockpitRoot.transform.LookAt(headTransform);
        // 让屏幕转过来，而不是背对着玩家
        cockpitRoot.transform.Rotate(0, 180, 0); 
        
        // 恢复初始放缩大小
        cockpitRoot.transform.localScale = Vector3.one * defaultScale;
    }
}