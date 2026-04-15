using UnityEngine;
using Oculus.Interaction; // 引用 Meta SDK

public class DirectGrabController : MonoBehaviour
{
    [Header("引用")]
    public Grabbable grabbable;      // Meta的抓取组件
    public Transform robotEndTip;    // 机械臂真实的末端位置(Joint7)
    public Transform robotWrist;     // 【新增】腕部中心(Joint6)，把手位置对应此点
    public RobotIKController ikController; // 我们的IK解算器

    private bool isGrabbed = false;

    [Header("行为开关")]
    [Tooltip("松开抓取后是否将把手吸附回机械臂腕部")]
    public bool snapBackOnRelease = false;

    void Start()
    {
        // 监听 Meta SDK 的事件
        grabbable.WhenPointerEventRaised += HandlePointerEvent;
    }

    void OnDestroy()
    {
        grabbable.WhenPointerEventRaised -= HandlePointerEvent;
    }

    [Header("临时禁用开关")]
    [Tooltip("禁用 VR 手柄抓取功能")]
    public bool disableGrabInput = true;

    // 处理抓取/松开事件
    private void HandlePointerEvent(PointerEvent evt)
    {
        if (disableGrabInput) return;

        if (evt.Type == PointerEventType.Select)
        {
            isGrabbed = true;
            // 告诉 IK 控制器：现在由把手接管控制权
            ikController.SetTarget(this.transform);
        }
        else if (evt.Type == PointerEventType.Unselect)
        {
            isGrabbed = false;
            // 告诉 IK 控制器：停止跟随
            ikController.StopTracking();
        }
    }

    void Update()
    {
        // 如果没被抓取，且启用了吸附回腕部，则把手跟随腕部中心(J6)
        if (snapBackOnRelease && !isGrabbed && robotWrist != null)
        {
            transform.position = robotWrist.position;
            transform.rotation = robotWrist.rotation;
        }
    }
}