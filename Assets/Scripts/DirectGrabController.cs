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

    void Start()
    {
        // 监听 Meta SDK 的事件
        grabbable.WhenPointerEventRaised += HandlePointerEvent;
    }

    void OnDestroy()
    {
        grabbable.WhenPointerEventRaised -= HandlePointerEvent;
    }

    // 处理抓取/松开事件
    private void HandlePointerEvent(PointerEvent evt)
    {
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
        // 如果没被抓取，把手要时刻跟随腕部中心(J6)
        // 这样你下次伸手时，把手就在正确的位置
        if (!isGrabbed && robotWrist != null)
        {
            // 把手位置对应 J6 位置
            transform.position = robotWrist.position;
            // 把手旋转对应 J6 旋转（J4/J5/J6 的组合姿态）
            transform.rotation = robotWrist.rotation;
        }
    }
}