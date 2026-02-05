using UnityEngine;
using System;

[Serializable]
public class JointConfig
{
    public string name = "Joint";
    [Range(-360, 360)] public float minAngle = -170f;
    [Range(-360, 360)] public float maxAngle = 170f;
    public Renderer meshRenderer;
}

public class RobotIKController : MonoBehaviour
{
    [Header("=== 1. 物理拟真 (核心修复) ===")]
    [Tooltip("关节最大旋转速度 (度/秒)。真实工业机器人通常在 90-180 之间。越小越像真机，越大越跟手。")]
    public float maxJointSpeed = 120.0f;
    
    [Tooltip("是否启用物理平滑。如果不勾选，机械臂会瞬移 (用于调试)。")]
    public bool enablePhysicsSmoothing = true;

    [Header("=== 2. 自动测量 ===")]
    public bool autoCalibrate = true;

    [Header("=== 3. 机械臂参数 ===")]
    public float L1_BigArm = 0.5f;   
    public float L2_SmallArm = 0.5f; 
    public float J4_Drop_Offset = 0.1f; 

    [Header("=== 4. 腕部连杆偏移 ===")]
    public Vector3 Offset_J4_to_J5 = new Vector3(0, 0, 0.1f);
    public Vector3 Offset_J5_to_J6 = new Vector3(0, 0, 0.1f);
    public Vector3 Offset_J6_to_J7 = new Vector3(0, 0, 0.15f);

    [Header("=== 5. IK 稳定性设置 ===")]
    [Range(0.1f, 1f)] public float iterationDamping = 0.6f; 
    public float j1DeadZoneRadius = 0.2f;
    
    // 输入端平滑 (减少手抖)
    [Range(0f, 0.5f)] public float inputSmoothTime = 0.1f;

    [Header("=== 6. J7 控制设置 ===")]
    public float j7RotationSpeed = 90.0f;

    [Header("=== 7. 关节限位配置 ===")]
    public JointConfig[] jointLimits = new JointConfig[7];
    public Color normalColor = Color.white;
    public Color warningColor = Color.magenta;

    [Header("=== 8. 关节引用 ===")]
    public Transform visual_J1;
    public Transform visual_J2;
    public Transform visual_J3;
    public Transform visual_PlatformBase; 
    public Transform visual_J4;
    public Transform visual_J5;
    public Transform visual_J6;
    public Transform visual_J7; 

    [Header("=== 9. 其他设置 ===")]
    public Transform ref_J3_Pivot;
    public Transform ref_J4_Pivot;
    public Vector3 gripOffset = Vector3.zero;
    public bool invert_J2 = false;
    public bool invert_J3 = true;

    // === 数据层 ===
    
    // 最终输出给网络和视觉的角度 (模拟后的物理角度)
    [HideInInspector] public float[] outAngles = new float[7];

    // IK 计算出的理想目标角度 (数学角度)
    private float[] targetIKAngles = new float[7];

    private Transform activeTarget = null;
    private bool isTracking = false;
    
    private GameObject ghostRoot;
    private Transform ghost_Platform, ghost_J4, ghost_J5, ghost_J6, g_J7;

    // 平滑缓存
    private Vector3 currentVelocityPos; 
    private Vector3 smoothedInputPos; 
    private Quaternion smoothedInputRot;
    private float lastJ4Angle = 0f;
    private MaterialPropertyBlock propBlock;

    void Start()
    {
        if (jointLimits.Length != 7) Array.Resize(ref jointLimits, 7);
        propBlock = new MaterialPropertyBlock();

        if (autoCalibrate && visual_J2 && ref_J3_Pivot && ref_J4_Pivot)
        {
            L1_BigArm = Vector3.Distance(visual_J2.position, ref_J3_Pivot.position);
            L2_SmallArm = Vector3.Distance(ref_J3_Pivot.position, ref_J4_Pivot.position);
            
            if(visual_J4 && visual_J5) Offset_J4_to_J5 = visual_J4.InverseTransformPoint(visual_J5.position);
            if(visual_J5 && visual_J6) Offset_J5_to_J6 = visual_J5.InverseTransformPoint(visual_J6.position);
            if(visual_J6 && visual_J7) Offset_J6_to_J7 = visual_J6.InverseTransformPoint(visual_J7.position);
        }
        
        // 初始化平滑位置
        if (visual_J7) 
        {
            smoothedInputPos = visual_J7.position;
            smoothedInputRot = visual_J7.rotation;
        }

        BuildGhostRig();
    }

    public void SetTarget(Transform target) 
    { 
        activeTarget = target; 
        isTracking = true;
        // 抓取瞬间：重置输入平滑，防止输入端跳变
        Vector3 rawTargetPos = target.position + target.TransformDirection(gripOffset);
        smoothedInputPos = rawTargetPos;
        smoothedInputRot = target.rotation;
        currentVelocityPos = Vector3.zero;
        
        // 同时也把当前的物理角度同步给IK目标，防止瞬间回弹
        Array.Copy(outAngles, targetIKAngles, 7);
    }

    public void StopTracking() { activeTarget = null; isTracking = false; }
    
    public byte[] GetPacketData()
    {
        byte[] data = new byte[30];
        for(int i=0; i<7; i++) {
            short val = (short)(NormalizeAngle(outAngles[i]) * 100);
            byte[] b = BitConverter.GetBytes(val);
            data[i*2] = b[0]; data[i*2+1] = b[1];
        }
        return data;
    }

    void Update()
    {
        // === 1. J7 摇杆控制 (直接叠加到目标值) ===
        float joystickY = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, OVRInput.Controller.RTouch).y;
        targetIKAngles[6] += joystickY * j7RotationSpeed * Time.deltaTime;
        
        // J7 也要通过物理模拟层，所以这里不直接改 outAngles

        // === 2. IK 解算 (计算 J0 - J5) ===
        if (isTracking && activeTarget != null)
        {
            Vector3 rawPos = activeTarget.position + activeTarget.TransformDirection(gripOffset);
            
            // 输入端简单滤波 (处理手抖)
            smoothedInputPos = Vector3.SmoothDamp(smoothedInputPos, rawPos, ref currentVelocityPos, inputSmoothTime);
            // 旋转不需要过度平滑，否则手腕反应迟钝，直接用 Slerp
            smoothedInputRot = Quaternion.Slerp(smoothedInputRot, activeTarget.rotation, Time.deltaTime * 20f);

            SolveIterativeIK(smoothedInputPos, smoothedInputRot);
        }

        // === 3. 物理伺服模拟 (核心新增) ===
        SimulatePhysicsMotors();

        // === 4. 限位与视觉 ===
        ApplyLimitsAndWarnings();
        ApplyToVisuals();
    }

    // 新增：模拟电机物理运动
    void SimulatePhysicsMotors()
    {
        float dt = Time.deltaTime;

        for (int i = 0; i < 7; i++)
        {
            if (enablePhysicsSmoothing)
            {
                // 使用 MoveTowardsAngle: 
                // 1. 自动处理 359度 -> 1度 的最短路径插值
                // 2. 限制每帧最大变化量 (Speed * dt)
                outAngles[i] = Mathf.MoveTowardsAngle(outAngles[i], targetIKAngles[i], maxJointSpeed * dt);
            }
            else
            {
                // 如果关闭物理模拟，直接瞬移 (调试用)
                outAngles[i] = targetIKAngles[i];
            }
        }
    }

    void BuildGhostRig()
    {
        if(ghostRoot) Destroy(ghostRoot);
        ghostRoot = new GameObject("IK_Math_Solver");
        ghostRoot.transform.SetParent(transform);
        ghostRoot.transform.localPosition = Vector3.zero;
        ghostRoot.transform.localRotation = Quaternion.identity;

        ghost_Platform = CreateGhost("G_Platform", ghostRoot.transform);
        ghost_J4 = CreateGhost("G_J4", ghost_Platform);
        ghost_J5 = CreateGhost("G_J5", ghost_J4);
        ghost_J6 = CreateGhost("G_J6", ghost_J5);
        g_J7 = CreateGhost("G_J7", ghost_J6);
        
        ghost_J4.localPosition = Vector3.zero;
        ghost_J5.localPosition = Offset_J4_to_J5;
        ghost_J6.localPosition = Offset_J5_to_J6;
        g_J7.localPosition = Offset_J6_to_J7;
    }

    Transform CreateGhost(string name, Transform parent)
    {
        GameObject g = new GameObject(name);
        g.transform.SetParent(parent);
        g.transform.localPosition = Vector3.zero;
        g.transform.localRotation = Quaternion.identity;
        return g.transform;
    }

    void SolveIterativeIK(Vector3 targetPos, Quaternion targetRot)
    {
        // 初始猜测：使用当前的 IK 目标值，而不是物理值 (防止震荡)
        float currentJ1 = targetIKAngles[0];

        // 迭代 5 次
        for (int i = 0; i < 5; i++)
        {
            Quaternion baseRot = Quaternion.Euler(0, currentJ1, 0);
            Quaternion wristLocal = Quaternion.Inverse(baseRot) * targetRot;
            Vector3 wristAngles = RobustDecompose(wristLocal);
            
            float j4 = wristAngles.y;
            float j5 = wristAngles.z;
            float j6 = wristAngles.x;

            ghost_J4.localEulerAngles = new Vector3(0, j4, 0);
            ghost_J5.localEulerAngles = new Vector3(0, 0, j5);
            ghost_J6.localEulerAngles = new Vector3(j6, 0, 0);

            Vector3 wristVector = g_J7.position - ghost_Platform.position;
            Vector3 platformTarget = targetPos - wristVector;

            Vector3 j3TipTarget = platformTarget + Vector3.up * J4_Drop_Offset;
            Vector3 rootLocalTarget = transform.InverseTransformPoint(j3TipTarget);
            
            float targetJ1 = 0;
            float flatDist = new Vector2(rootLocalTarget.x, rootLocalTarget.z).magnitude;

            if (flatDist > j1DeadZoneRadius)
                targetJ1 = Mathf.Atan2(rootLocalTarget.x, rootLocalTarget.z) * Mathf.Rad2Deg;
            else
                targetJ1 = currentJ1; 

            currentJ1 = Mathf.LerpAngle(currentJ1, targetJ1, iterationDamping);

            // 更新数学目标数组
            targetIKAngles[0] = currentJ1;
            
            SolveArmPosition(rootLocalTarget, currentJ1);

            targetIKAngles[3] = j4;
            targetIKAngles[4] = NormalizeAngle(j5);
            targetIKAngles[5] = j6;
        }
        lastJ4Angle = targetIKAngles[3];
    }

    Vector3 RobustDecompose(Quaternion q)
    {
        Vector3 forward = q * Vector3.forward;
        float yaw;
        if (Mathf.Abs(forward.y) > 0.98f) yaw = lastJ4Angle; 
        else yaw = Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg;

        Quaternion q_yaw = Quaternion.Euler(0, yaw, 0);
        Quaternion rem1 = Quaternion.Inverse(q_yaw) * q;
        Vector3 right = rem1 * Vector3.right;
        float roll = Mathf.Atan2(right.y, right.x) * Mathf.Rad2Deg;
        Quaternion q_roll = Quaternion.Euler(0, 0, roll);
        Quaternion rem2 = Quaternion.Inverse(q_roll) * rem1;
        float pitch = rem2.eulerAngles.x;
        
        return new Vector3(NormalizeAngle(pitch), NormalizeAngle(yaw), NormalizeAngle(roll));
    }

    void SolveArmPosition(Vector3 target, float j1_angle)
    {
        float shoulderHeight = visual_J2.localPosition.y;
        float flatDist = new Vector2(target.x, target.z).magnitude; 
        float y = target.y - shoulderHeight;

        float D = Mathf.Sqrt(flatDist * flatDist + y * y);
        D = Mathf.Clamp(D, 0.01f, L1_BigArm + L2_SmallArm - 0.001f);

        float cosAlpha = (L1_BigArm * L1_BigArm + D * D - L2_SmallArm * L2_SmallArm) / (2 * L1_BigArm * D);
        float alpha = Mathf.Acos(Mathf.Clamp(cosAlpha, -1f, 1f));
        float beta = Mathf.Atan2(y, flatDist);
        float cosGamma = (L1_BigArm * L1_BigArm + L2_SmallArm * L2_SmallArm - D * D) / (2 * L1_BigArm * L2_SmallArm);
        float gamma = Mathf.Acos(Mathf.Clamp(cosGamma, -1f, 1f));

        targetIKAngles[1] = (beta + alpha) * Mathf.Rad2Deg; 
        targetIKAngles[2] = (Mathf.PI - gamma) * Mathf.Rad2Deg; 
    }

    void ApplyLimitsAndWarnings()
    {
        for (int i = 0; i < 7; i++)
        {
            // 限制的是最终输出的物理角度
            float angle = NormalizeAngle(outAngles[i]);
            if (i < jointLimits.Length)
            {
                JointConfig limit = jointLimits[i];
                angle = Mathf.Clamp(angle, limit.minAngle, limit.maxAngle);
            }
            outAngles[i] = angle;
            UpdateLimitVisual(i);
        }
    }

    void UpdateLimitVisual(int index)
    {
        if (index >= jointLimits.Length) return;
        JointConfig limit = jointLimits[index];
        if (limit.meshRenderer == null) return;
        
        float angle = outAngles[index];
        bool atLimit = (angle <= limit.minAngle + 2f) || (angle >= limit.maxAngle - 2f);
        
        limit.meshRenderer.GetPropertyBlock(propBlock);
        propBlock.SetColor("_Color", atLimit ? warningColor : normalColor);
        propBlock.SetColor("_BaseColor", atLimit ? warningColor : normalColor); 
        limit.meshRenderer.SetPropertyBlock(propBlock);
    }

    void ApplyToVisuals()
    {
        // 使用 outAngles (物理角度) 来驱动模型
        visual_J1.localRotation = Quaternion.Euler(0, outAngles[0], 0);
        
        float j2 = invert_J2 ? outAngles[1] : -outAngles[1];
        visual_J2.localRotation = Quaternion.Euler(j2, 0, 0);

        float j3 = invert_J3 ? outAngles[2] : -outAngles[2];
        visual_J3.localRotation = Quaternion.Euler(j3, 0, 0);

        if (visual_PlatformBase)
            visual_PlatformBase.rotation = Quaternion.Euler(0, visual_J1.eulerAngles.y, 0);

        visual_J4.localRotation = Quaternion.Euler(0, outAngles[3], 0);
        visual_J5.localRotation = Quaternion.Euler(0, 0, outAngles[4]);
        visual_J6.localRotation = Quaternion.Euler(outAngles[5], 0, 0);
        visual_J7.localRotation = Quaternion.Euler(0, 0, outAngles[6]);
    }

    float NormalizeAngle(float a)
    {
        while (a > 180) a -= 360;
        while (a < -180) a += 360;
        return a;
    }
}