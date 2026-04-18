using UnityEngine;
using Oculus.Interaction;

/// <summary>
/// 可抓取 Canvas 的弧形槽位吸附/释放逻辑
/// 要求同物体上已有 Meta SDK 的 Grabbable 组件
/// </summary>
[RequireComponent(typeof(Grabbable))]
public class GrabbableCanvasSnap : MonoBehaviour
{
    [Header("吸附设置")]
    [Tooltip("释放后自动吸附到最近空闲槽位的最大距离")]
    public float snapDistance = 0.15f;

    [Tooltip("吸附时的动画时长（0 = 瞬间对齐）")]
    public float snapDuration = 0.1f;

    [Tooltip("吸附后的局部旋转偏移（如背对可改为 0,180,0）")]
    public Vector3 snappedRotationEuler = Vector3.zero;

    [Header("运行时状态（只读）")]
    [SerializeField] private ArcSnapSlot currentSlot;
    [SerializeField] private bool isGrabbed;

    public bool IsGrabbed => isGrabbed;
    public ArcSnapSlot CurrentSlot => currentSlot;

    private Grabbable grabbable;
    private Rigidbody rb;
    private Transform originalParent;
    private Vector3 snapStartPos;
    private Quaternion snapStartRot;
    private float snapTimer;
    private bool isSnapping;

    void Start()
    {
        grabbable = GetComponent<Grabbable>();
        if (grabbable != null)
        {
            grabbable.WhenPointerEventRaised += HandlePointerEvent;
        }
        rb = GetComponent<Rigidbody>();
        originalParent = transform.parent;
    }

    void OnDestroy()
    {
        if (grabbable != null)
        {
            grabbable.WhenPointerEventRaised -= HandlePointerEvent;
        }
    }

    void Update()
    {
        if (isSnapping && currentSlot != null)
        {
            snapTimer += Time.deltaTime;
            float t = Mathf.Clamp01(snapTimer / Mathf.Max(0.001f, snapDuration));
            Vector3 targetPos = currentSlot.attachOffset;
            Quaternion targetRot = Quaternion.Euler(snappedRotationEuler);
            transform.localPosition = Vector3.Lerp(snapStartPos, targetPos, t);
            transform.localRotation = Quaternion.Slerp(snapStartRot, targetRot, t);

            if (t >= 1f)
            {
                isSnapping = false;
                transform.localPosition = targetPos;
                transform.localRotation = targetRot;
            }
        }
    }

    private void HandlePointerEvent(PointerEvent evt)
    {
        switch (evt.Type)
        {
            case PointerEventType.Select:
                OnGrab();
                break;
            case PointerEventType.Unselect:
                OnRelease();
                break;
        }
    }

    private void OnGrab()
    {
        isGrabbed = true;
        isSnapping = false;

        if (currentSlot != null)
        {
            currentSlot.Release(this);
            currentSlot = null;
        }

        // 脱离弧形控制台，恢复为原层级或场景根级
        transform.SetParent(originalParent, true);
    }

    private void OnRelease()
    {
        isGrabbed = false;

        ArcSnapSlot nearestSlot = FindNearestFreeSlot();
        if (nearestSlot != null)
        {
            SnapToSlot(nearestSlot);
        }
    }

    private ArcSnapSlot FindNearestFreeSlot()
    {
        ArcSnapSlot nearest = null;
        float nearestSqrDist = snapDistance * snapDistance;

        // 遍历场景中所有 ArcSnapSlot
        var slots = FindObjectsOfType<ArcSnapSlot>();
        foreach (var slot in slots)
        {
            if (!slot.IsFree) continue;

            float sqrDist = (transform.position - slot.transform.position).sqrMagnitude;
            if (sqrDist < nearestSqrDist)
            {
                nearestSqrDist = sqrDist;
                nearest = slot;
            }
        }

        return nearest;
    }

    private void SnapToSlot(ArcSnapSlot slot)
    {
        if (!slot.TryOccupy(this))
            return;

        currentSlot = slot;
        transform.SetParent(slot.transform, false);

        // 延迟停止物理，确保在 Grabbable 的 throw 逻辑之后覆盖它
        if (gameObject.activeInHierarchy)
        {
            StartCoroutine(StabilizeAfterSnap());
        }

        Vector3 targetPos = slot.attachOffset;
        Quaternion targetRot = Quaternion.Euler(snappedRotationEuler);

        if (snapDuration > 0f)
        {
            snapStartPos = transform.localPosition;
            snapStartRot = transform.localRotation;
            snapTimer = 0f;
            isSnapping = true;
        }
        else
        {
            transform.localPosition = targetPos;
            transform.localRotation = targetRot;
        }
    }

    private System.Collections.IEnumerator StabilizeAfterSnap()
    {
        yield return new WaitForFixedUpdate();
        if (currentSlot == null) yield break;

        if (rb != null)
        {
            rb.isKinematic = true;
            rb.velocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
        }
        // 强制修正位置和旋转，防止 throw 导致的偏移
        transform.localPosition = currentSlot.attachOffset;
        transform.localRotation = Quaternion.Euler(snappedRotationEuler);
    }
}
