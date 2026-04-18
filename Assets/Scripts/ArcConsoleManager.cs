using UnityEngine;

/// <summary>
/// 弧形控制台管理器
/// 在玩家头部前方生成 4 个弧形槽位，并平滑跟随头部 Yaw 转动
/// </summary>
public class ArcConsoleManager : MonoBehaviour
{
    [Header("跟随目标")]
    [Tooltip("留空则自动查找 OVRCameraRig.centerEyeAnchor")]
    public Transform headTransform;

    [Header("弧形参数")]
    [Tooltip("槽位数量")]
    public int slotCount = 4;

    [Tooltip("弧形半径（到头部中心的水平距离）")]
    public float radius = 1.0f;

    [Tooltip("弧形总展开角度（度）")]
    [Range(30f, 240f)]
    public float arcAngle = 120f;

    [Tooltip("相对头部的高度偏移")]
    public float heightOffset = -0.15f;

    [Header("跟随平滑")]
    public float followSpeed = 8f;

    [Header("视觉参考")]
    [Tooltip("是否为每个槽位生成半透明视觉指示器")]
    public bool showVisualGuides = true;

    [Tooltip("视觉指示器大小（米）")]
    public float guideSize = 0.12f;

    [Tooltip("视觉指示器厚度（米）")]
    public float guideThickness = 0.005f;

    [Tooltip("视觉指示器颜色")]
    public Color guideColor = new Color(0f, 0.8f, 1f, 0.25f);

    [Tooltip("Canvas 吸附位置相对于槽位原点的偏移（Z+ 朝向玩家）")]
    public Vector3 canvasAttachOffset = new Vector3(0f, 0f, 0.008f);

    [Tooltip("预吸附高亮颜色（当抓取的 Canvas 进入吸附范围时）")]
    public Color highlightColor = new Color(0.2f, 1f, 0.2f, 0.5f);

    private ArcSnapSlot[] slots;
    private Renderer[] slotRenderers;
    private Material visualMaterial;

    void Start()
    {
        FindHeadTransform();
        InitializeSlots();
    }

    void LateUpdate()
    {
        FollowHead();
        UpdateVisualGuides();
    }

    void FindHeadTransform()
    {
        if (headTransform != null) return;

        var ovr = FindObjectOfType<OVRCameraRig>();
        if (ovr != null)
        {
            headTransform = ovr.centerEyeAnchor;
        }
        else if (Camera.main != null)
        {
            headTransform = Camera.main.transform;
        }
    }

    void InitializeSlots()
    {
        slots = new ArcSnapSlot[slotCount];
        slotRenderers = new Renderer[slotCount];

        for (int i = 0; i < slotCount; i++)
        {
            var go = new GameObject($"SnapSlot_{i}");
            go.transform.SetParent(transform, false);
            slots[i] = go.AddComponent<ArcSnapSlot>();
            slots[i].attachOffset = canvasAttachOffset;

            if (showVisualGuides)
            {
                slotRenderers[i] = CreateVisualGuide(slots[i].transform);
            }
        }

        RefreshSlotLayout();
    }

    Renderer CreateVisualGuide(Transform parent)
    {
        var guide = GameObject.CreatePrimitive(PrimitiveType.Cube);
        guide.name = "VisualGuide";
        Destroy(guide.GetComponent<Collider>());
        guide.transform.SetParent(parent, false);
        // 将薄立方体放在 Canvas 吸附位置的后方，避免遮挡交互
        float zOffset = -guideThickness * 0.5f - 0.002f;
        guide.transform.localPosition = new Vector3(0f, 0f, zOffset);
        guide.transform.localScale = new Vector3(guideSize, guideSize, guideThickness);

        var rend = guide.GetComponent<Renderer>();
        if (rend != null)
        {
            if (visualMaterial == null)
            {
                visualMaterial = new Material(Shader.Find("Unlit/Transparent"));
                visualMaterial.name = "ArcSlotVisual";
                visualMaterial.color = guideColor;
            }
            rend.material = visualMaterial;
        }
        return rend;
    }

    void UpdateVisualGuides()
    {
        if (!showVisualGuides || slotRenderers == null) return;

        var canvases = FindObjectsOfType<GrabbableCanvasSnap>();
        for (int i = 0; i < slots.Length; i++)
        {
            if (slotRenderers[i] == null) continue;

            bool shouldHighlight = false;
            if (slots[i].IsFree)
            {
                foreach (var canvas in canvases)
                {
                    if (canvas.IsGrabbed && canvas.CurrentSlot == null)
                    {
                        float dist = Vector3.Distance(canvas.transform.position, slots[i].transform.position);
                        if (dist <= canvas.snapDistance)
                        {
                            shouldHighlight = true;
                            break;
                        }
                    }
                }
            }

            slotRenderers[i].material.color = shouldHighlight ? highlightColor : guideColor;
        }
    }

    /// <summary>
    /// 根据当前弧形参数重新排列槽位
    /// </summary>
    [ContextMenu("刷新槽位布局")]
    public void RefreshSlotLayout()
    {
        if (slots == null || slots.Length == 0) return;

        float angleStep = arcAngle / Mathf.Max(1, slots.Length - 1);
        float startAngle = -arcAngle / 2f;

        for (int i = 0; i < slots.Length; i++)
        {
            if (slots[i] == null) continue;

            float angleDeg = startAngle + angleStep * i;
            float angleRad = angleDeg * Mathf.Deg2Rad;

            Vector3 localPos = new Vector3(
                Mathf.Sin(angleRad) * radius,
                heightOffset,
                Mathf.Cos(angleRad) * radius
            );
            Quaternion localRot = Quaternion.Euler(0, 180f + angleDeg, 0f);

            slots[i].transform.localPosition = localPos;
            slots[i].transform.localRotation = localRot;
        }
    }

    void FollowHead()
    {
        if (headTransform == null) return;

        // 只跟随头部的水平朝向（Yaw）和水平位置
        Vector3 forward = headTransform.forward;
        forward.y = 0f;
        if (forward.sqrMagnitude < 0.0001f) forward = Vector3.forward;
        forward.Normalize();

        Vector3 targetPos = headTransform.position;

        Quaternion targetRot = Quaternion.Euler(0, headTransform.eulerAngles.y, 0);

        transform.position = Vector3.Lerp(transform.position, targetPos, Time.deltaTime * followSpeed);
        transform.rotation = Quaternion.Slerp(transform.rotation, targetRot, Time.deltaTime * followSpeed);
    }

    void OnValidate()
    {
        if (Application.isPlaying && slots != null)
        {
            RefreshSlotLayout();
        }
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
            Mathf.Cos(startAngle) * radius
        ));

        for (int i = 1; i <= segments; i++)
        {
            float t = (float)i / segments;
            float angle = Mathf.Lerp(startAngle, endAngle, t);

            Vector3 pos = transform.TransformPoint(new Vector3(
                Mathf.Sin(angle) * radius,
                heightOffset,
                Mathf.Cos(angle) * radius
            ));

            Gizmos.DrawLine(prevPos, pos);
            prevPos = pos;
        }
    }
}
