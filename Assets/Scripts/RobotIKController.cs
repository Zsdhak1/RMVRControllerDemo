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
    [Header("=== 1. 运动手感设置 (SmoothDamp) ===")]
    [Tooltip("平滑时间：值越小越跟手(0.05)，值越大越有惯性(0.2)。推荐 0.1")]
    public float jointSmoothTime = 0.1f;
    [Tooltip("最大转速：设大一点(720+)以保证快速移动时能跟上")]
    public float maxJointSpeed = 720.0f;
    [Tooltip("输入平滑：减少此值(0.05)可降低手柄输入的延迟")]
    [Range(0f, 0.5f)] public float inputSmoothTime = 0.05f;

    [Header("=== 2. 机械臂参数 (自动测量) ===")]
    public bool autoCalibrate = true;
    public float L1_BigArm = 0.5f;   
    public float L2_SmallArm = 0.5f; 
    public float J4_Drop_Offset = 0.1f; 

    [Header("=== 3. 腕部连杆偏移 ===")]
    public Vector3 Offset_J4_to_J5 = new Vector3(0, 0, 0.1f);
    public Vector3 Offset_J5_to_J6 = new Vector3(0, 0, 0.1f);
    public Vector3 Offset_J6_to_J7 = new Vector3(0, 0, 0.15f);

    [Header("=== 4. IK 稳定性设置 ===")]
    [Range(0.1f, 1f)] public float iterationDamping = 0.6f; 
    public float j1DeadZoneRadius = 0.1f; 
    public float j1SoftDeadZone = 0.3f;

    [Header("=== 5. 手动控制设置 ===")]
    public float j7RotationSpeed = 90.0f;

    [Header("=== 6. 关节引用 (必填) ===")]
    public Transform visual_J1;
    public Transform visual_J2;
    public Transform visual_J3;
    public Transform visual_PlatformBase; 
    public Transform visual_J4;
    public Transform visual_J5;
    public Transform visual_J6;
    public Transform visual_J7; 

    [Header("=== 7. 参考点与设置 ===")]
    public Transform ref_J3_Pivot;
    public Transform ref_J4_Pivot;
    public Vector3 gripOffset = Vector3.zero;
    public bool invert_J2 = false;
    public bool invert_J3 = true; // 通常 J3 需要反转，视模型而定

    [Header("=== 8. 关节限位 ===")]
    public JointConfig[] jointLimits = new JointConfig[7];
    public Color normalColor = Color.white;
    public Color warningColor = Color.magenta;

    // --- 内部数据变量 ---
    [HideInInspector] public float[] outAngles = new float[7];
    private float[] targetIKAngles = new float[7];
    private float[] jointVelocities = new float[7]; // 用于 SmoothDamp 的速度缓存

    private Transform activeTarget = null;
    private bool isTracking = false;
    private GameObject ghostRoot;
    private Transform ghost_Platform, ghost_J4, ghost_J5, ghost_J6, g_J7;

    private Vector3 currentVelocityPos; 
    private Vector3 smoothedInputPos; 
    private Quaternion smoothedInputRot;
    private float lastJ4Angle = 0f;
    private MaterialPropertyBlock propBlock;

    void Start()
    {
        if (jointLimits.Length != 7) Array.Resize(ref jointLimits, 7);
        propBlock = new MaterialPropertyBlock();

        // 自动校准臂长
        if (autoCalibrate && visual_J2 && ref_J3_Pivot && ref_J4_Pivot)
        {
            L1_BigArm = Vector3.Distance(visual_J2.position, ref_J3_Pivot.position);
            L2_SmallArm = Vector3.Distance(ref_J3_Pivot.position, ref_J4_Pivot.position);
            
            if(visual_J4 && visual_J5) Offset_J4_to_J5 = visual_J4.InverseTransformPoint(visual_J5.position);
            if(visual_J5 && visual_J6) Offset_J5_to_J6 = visual_J5.InverseTransformPoint(visual_J6.position);
            if(visual_J6 && visual_J7) Offset_J6_to_J7 = visual_J6.InverseTransformPoint(visual_J7.position);
        }
        
        if (visual_J7) 
        {
            smoothedInputPos = visual_J7.position;
            smoothedInputRot = visual_J7.rotation;
        }

        InitializeAnglesFromVisuals();
        BuildGhostRig();
    }

    // 从当前模型姿态初始化角度，防止启动时机械臂乱跳
    void InitializeAnglesFromVisuals()
    {
        if(visual_J1) outAngles[0] = NormalizeAngle(visual_J1.localEulerAngles.y);
        if(visual_J2) outAngles[1] = NormalizeAngle(invert_J2 ? visual_J2.localEulerAngles.x : -visual_J2.localEulerAngles.x);
        if(visual_J3) outAngles[2] = NormalizeAngle(invert_J3 ? visual_J3.localEulerAngles.x : -visual_J3.localEulerAngles.x);
        if(visual_J4) outAngles[3] = NormalizeAngle(visual_J4.localEulerAngles.y);
        if(visual_J5) outAngles[4] = NormalizeAngle(visual_J5.localEulerAngles.z);
        if(visual_J6) outAngles[5] = NormalizeAngle(visual_J6.localEulerAngles.x);
        if(visual_J7) outAngles[6] = NormalizeAngle(visual_J7.localEulerAngles.z);
        Array.Copy(outAngles, targetIKAngles, 7);
    }

    // 设置追踪目标
    public void SetTarget(Transform target) 
    { 
        activeTarget = target; 
        isTracking = true;
        // 重置平滑参数，防止瞬移
        currentVelocityPos = Vector3.zero;
        if(activeTarget != null) {
            smoothedInputPos = activeTarget.position + activeTarget.TransformDirection(gripOffset);
            smoothedInputRot = activeTarget.rotation;
        }
        // 清空关节速度缓存
        Array.Clear(jointVelocities, 0, 7);
    }

    public void StopTracking() { activeTarget = null; isTracking = false; }
    
    // 获取发送给下位机的数据包
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
        // 1. 处理 J7 手动旋转 (手柄摇杆)
        float joystickY = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, OVRInput.Controller.RTouch).y;
        targetIKAngles[6] += joystickY * j7RotationSpeed * Time.deltaTime;

        // 2. IK 解算逻辑
        if (isTracking && activeTarget != null)
        {
            Vector3 rawPos = activeTarget.position + activeTarget.TransformDirection(gripOffset);
            
            // 安全区保护 (防止打到底座内部)
            Vector3 baseToTarget = rawPos - transform.position;
            float safeRadius = 0.3f; // 简单安全半径
            if (baseToTarget.magnitude < safeRadius)
            {
                rawPos = transform.position + baseToTarget.normalized * safeRadius;
            }

            // 【重要】已移除之前那个导致无法伸直的 maxReach 强行限制代码
            // 现在依靠数学算法自然伸展

            // 输入平滑：过滤手抖
            smoothedInputPos = Vector3.SmoothDamp(smoothedInputPos, rawPos, ref currentVelocityPos, inputSmoothTime);
            smoothedInputRot = Quaternion.Slerp(smoothedInputRot, activeTarget.rotation, Time.deltaTime * 15f);

            SolveIterativeIK(smoothedInputPos, smoothedInputRot);
        }

        // 3. 物理运动模拟 (SmoothDamp)
        SimulatePhysicsMotors();

        // 4. 应用限制和可视化
        ApplyLimitsAndWarnings();
        ApplyToVisuals();
    }

    // --- 核心修改：使用 SmoothDamp 替代匀速移动 ---
    void SimulatePhysicsMotors()
    {
        for (int i = 0; i < 7; i++)
        {
            // 底座 (J1) 通常惯性较大，可以给它单独乘个系数让它稍微慢一点点，更有重量感
            float damping = (i == 0) ? jointSmoothTime * 1.5f : jointSmoothTime;

            outAngles[i] = Mathf.SmoothDampAngle(
                outAngles[i], 
                targetIKAngles[i], 
                ref jointVelocities[i], 
                damping, 
                maxJointSpeed
            );
        }
    }

    void SolveIterativeIK(Vector3 targetPos, Quaternion targetRot)
    {
        float currentJ1 = targetIKAngles[0]; 

        // 迭代 5 次以提高精度
        for (int i = 0; i < 5; i++)
        {
            Quaternion baseRot = Quaternion.Euler(0, currentJ1, 0);
            Quaternion wristLocal = Quaternion.Inverse(baseRot) * targetRot;
            Vector3 wristAngles = RobustDecompose(wristLocal);
            
            float j4 = wristAngles.y;
            float j5 = wristAngles.z;
            float j6 = wristAngles.x;

            // 设置 Ghost Rig 姿态以计算偏移
            ghost_J4.localEulerAngles = new Vector3(0, j4, 0);
            ghost_J5.localEulerAngles = new Vector3(0, 0, j5);
            ghost_J6.localEulerAngles = new Vector3(j6, 0, 0);

            Vector3 wristOffsetLocal = ghost_Platform.InverseTransformPoint(g_J7.position);
            Vector3 wristOffsetRotated = baseRot * wristOffsetLocal;
            Vector3 wristOffsetWorld = transform.TransformDirection(wristOffsetRotated);

            // 反推 J4 目标位置
            Vector3 platformTarget = targetPos - wristOffsetWorld;
            Vector3 j3TipTarget = platformTarget + transform.up * J4_Drop_Offset;
            Vector3 rootLocalTarget = transform.InverseTransformPoint(j3TipTarget);
            
            // J1 解算 (带死区处理)
            float targetJ1 = 0;
            float flatDist = new Vector2(rootLocalTarget.x, rootLocalTarget.z).magnitude;

            if (flatDist > j1DeadZoneRadius) 
            {
                float rawJ1 = Mathf.Atan2(rootLocalTarget.x, rootLocalTarget.z) * Mathf.Rad2Deg;
                float zoneFactor = Mathf.Clamp01((flatDist - j1DeadZoneRadius) / (j1SoftDeadZone - j1DeadZoneRadius + 0.001f));
                targetJ1 = Mathf.LerpAngle(currentJ1, rawJ1, zoneFactor);
            }
            else 
            {
                targetJ1 = currentJ1; 
            }

            currentJ1 = Mathf.LerpAngle(currentJ1, targetJ1, iterationDamping);
            targetIKAngles[0] = currentJ1;
            
            // J2/J3 几何解算
            SolveArmPosition(rootLocalTarget, currentJ1);

            targetIKAngles[3] = j4;
            targetIKAngles[4] = NormalizeAngle(j5);
            targetIKAngles[5] = j6;
        }
        lastJ4Angle = targetIKAngles[3];
    }
    
    // 几何解算大臂小臂角度 (三角形余弦定理)
    void SolveArmPosition(Vector3 target, float j1_angle) {
        float shoulderHeight = visual_J2.localPosition.y;
        float flatDist = new Vector2(target.x, target.z).magnitude; 
        float y = target.y - shoulderHeight;
        
        // 计算目标距离
        float D = Mathf.Sqrt(flatDist * flatDist + y * y);
        
        // 这里的 Clamp 确保数学上不会出错，同时也起到了物理限位的作用
        // 允许 D 达到 L1+L2，从而允许手臂完全伸直
        D = Mathf.Clamp(D, 0.01f, L1_BigArm + L2_SmallArm - 0.001f);

        float cosAlpha = (L1_BigArm * L1_BigArm + D * D - L2_SmallArm * L2_SmallArm) / (2 * L1_BigArm * D);
        float alpha = Mathf.Acos(Mathf.Clamp(cosAlpha, -1f, 1f));
        float beta = Mathf.Atan2(y, flatDist);
        float cosGamma = (L1_BigArm * L1_BigArm + L2_SmallArm * L2_SmallArm - D * D) / (2 * L1_BigArm * L2_SmallArm);
        float gamma = Mathf.Acos(Mathf.Clamp(cosGamma, -1f, 1f));

        targetIKAngles[1] = (beta + alpha) * Mathf.Rad2Deg; 
        targetIKAngles[2] = (Mathf.PI - gamma) * Mathf.Rad2Deg; 
    }

    Vector3 RobustDecompose(Quaternion q) {
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

    // --- 辅助功能 ---
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

    void ApplyLimitsAndWarnings() {
        for (int i = 0; i < 7; i++) {
            ClampSingleJoint(i);
            UpdateLimitVisual(i);
        }
    }
    
    void ClampSingleJoint(int index) {
        float angle = NormalizeAngle(outAngles[index]);
        if (index < jointLimits.Length) {
            JointConfig limit = jointLimits[index];
            angle = Mathf.Clamp(angle, limit.minAngle, limit.maxAngle);
        }
        outAngles[index] = angle;
    }
    
    void UpdateLimitVisual(int index) {
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
    
    void ApplyToVisuals() {
        if(visual_J1) visual_J1.localRotation = Quaternion.Euler(0, outAngles[0], 0);
        
        if(visual_J2) {
            float j2 = invert_J2 ? outAngles[1] : -outAngles[1];
            visual_J2.localRotation = Quaternion.Euler(j2, 0, 0);
        }
        
        if(visual_J3) {
            float j3 = invert_J3 ? outAngles[2] : -outAngles[2];
            visual_J3.localRotation = Quaternion.Euler(j3, 0, 0);
        }
        
        if (visual_PlatformBase && visual_J1) 
            visual_PlatformBase.rotation = Quaternion.Euler(0, visual_J1.eulerAngles.y, 0);
            
        if(visual_J4) visual_J4.localRotation = Quaternion.Euler(0, outAngles[3], 0);
        if(visual_J5) visual_J5.localRotation = Quaternion.Euler(0, 0, outAngles[4]);
        if(visual_J6) visual_J6.localRotation = Quaternion.Euler(outAngles[5], 0, 0);
        if(visual_J7) visual_J7.localRotation = Quaternion.Euler(0, 0, outAngles[6]);
    }

    float NormalizeAngle(float a) {
        while (a > 180) a -= 360;
        while (a < -180) a += 360;
        return a;
    }
}