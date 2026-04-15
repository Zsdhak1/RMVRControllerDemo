using UnityEngine;

public class PassthroughController : MonoBehaviour
{
    [Header("引用")]
    public GameObject safetyFloor; // 拖入你的 Safety_Floor
    
    private OVRPassthroughLayer passthroughLayer;
    private MeshRenderer floorRenderer; // 专门用来控制地板显示

    void Start()
    {
        passthroughLayer = FindObjectOfType<OVRPassthroughLayer>();
        
        // 获取地板的渲染组件
        if (safetyFloor != null)
        {
            floorRenderer = safetyFloor.GetComponent<MeshRenderer>();
        }

        // 默认开启透视（根据你的需求调整）
        SetMRMode(true);
    }

    [Header("临时禁用开关")]
    [Tooltip("禁用手柄对透视模式的切换")]
    public bool disablePassthroughInput = true;

    void Update()
    {
        if (disablePassthroughInput) return;

        // 右手 B 键切换
        if (OVRInput.GetDown(OVRInput.Button.Two, OVRInput.Controller.RTouch))
        {
            ToggleMode();
        }

    }

    void ToggleMode()
    {
        if (passthroughLayer == null) return;
        bool isHidden = passthroughLayer.hidden;
        SetMRMode(isHidden);
    }

    void SetMRMode(bool active)
    {
        if (passthroughLayer == null) return;

        // 1. 控制透视层开关
        passthroughLayer.hidden = !active;

        // 2. 控制地板显示（关键修改！）
        // 开启透视(MR) -> 地板变透明 (Renderer关)，但物体还在 (Collider开)
        // 关闭透视(VR) -> 地板变黑 (Renderer开)
        if (floorRenderer != null)
        {
            floorRenderer.enabled = !active; 
        }
    }
}