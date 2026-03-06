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
    
    [Header("=== 4.5 IK 算法选择 ===")]
    [Tooltip("使用改进解析法（推荐）或原始迭代法（Ghost Rig）")]
    public bool useImprovedIK = true;
    
    [Header("=== 4.6 控制模式选择 ===")]
    [Tooltip("控制模式：InverseKinematics=逆运动学（默认），DirectAngleMapping=直接角度映射（J4根部与把手重合）")]
    public ControlMode controlMode = ControlMode.InverseKinematics;
    
    public enum ControlMode
    {
        InverseKinematics,      // 逆运动学模式（默认）
        DirectAngleMapping      // 直接角度映射模式
    }

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

    [Header("=== 9. 初始姿态设置 ===")]
    [Tooltip("启用后，启动时会将机械臂重置为下面配置的初始角度，而不是保持上次的姿态")]
    public bool resetToInitialPoseOnStart = true;
    [Tooltip("机械臂启动时的初始角度（J1-J7），单位：度")]
    public float[] initialAngles = new float[] { 0f, 45f, -90f, 0f, 0f, 0f, 0f };

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

        // 【重要】先初始化角度（重置为初始姿态或保持当前姿态）
        // 这确保后续校准在正确的姿态下进行
        InitializeAnglesFromVisuals();
        
        // 自动校准臂长（在初始化姿态之后进行，确保测量时模型在正确位置）
        if (autoCalibrate && visual_J2 && ref_J3_Pivot && ref_J4_Pivot)
        {
            // 方法1：用 InverseTransformPoint 获取 J3 Pivot 在 J2 坐标系中的位置（更精确）
            Vector3 j3PosInJ2Local = visual_J2.InverseTransformPoint(ref_J3_Pivot.position);
            L1_BigArm = j3PosInJ2Local.magnitude;
            
            // 方法2：如果 ref_J4_Pivot 是 ref_J3_Pivot 的子层级，直接用 localPosition
            if (ref_J4_Pivot.parent == ref_J3_Pivot)
            {
                L2_SmallArm = ref_J4_Pivot.localPosition.magnitude;
            }
            else
            {
                L2_SmallArm = Vector3.Distance(ref_J3_Pivot.position, ref_J4_Pivot.position);
            }
            
            // 如果测量失败（比如为0），回退到原方法
            if (L1_BigArm < 0.01f) L1_BigArm = Vector3.Distance(visual_J2.position, ref_J3_Pivot.position);
            if (L2_SmallArm < 0.01f) L2_SmallArm = Vector3.Distance(ref_J3_Pivot.position, ref_J4_Pivot.position);
            
            // 测量腕部各关节的 offset
            // 【重要】测量前需要将腕部重置为零姿态，确保测量的是链杆设计长度而不是当前姿态下的偏移
            Quaternion savedJ4Rot = visual_J4.localRotation;
            Quaternion savedJ5Rot = visual_J5.localRotation;
            Quaternion savedJ6Rot = visual_J6.localRotation;
            
            // 重置腕部为零姿态
            visual_J4.localRotation = Quaternion.identity;
            visual_J5.localRotation = Quaternion.identity;
            visual_J6.localRotation = Quaternion.identity;
            
            // 在零姿态下测量偏移
            if(visual_J4 && visual_J5) Offset_J4_to_J5 = visual_J4.InverseTransformPoint(visual_J5.position);
            if(visual_J5 && visual_J6) Offset_J5_to_J6 = visual_J5.InverseTransformPoint(visual_J6.position);
            if(visual_J6 && visual_J7) Offset_J6_to_J7 = visual_J6.InverseTransformPoint(visual_J7.position);
            
            // 恢复腕部姿态
            visual_J4.localRotation = savedJ4Rot;
            visual_J5.localRotation = savedJ5Rot;
            visual_J6.localRotation = savedJ6Rot;
            
            // 自动校准 J4_Drop_Offset（J4 Pivot 到 J3 Pivot 的垂直偏移）
            // 使用 local 坐标系计算，避免世界坐标系带来的不确定性
            if (ref_J4_Pivot && ref_J3_Pivot)
            {
                Vector3 j4InRoot = transform.InverseTransformPoint(ref_J4_Pivot.position);
                Vector3 j3InRoot = transform.InverseTransformPoint(ref_J3_Pivot.position);
                J4_Drop_Offset = j3InRoot.y - j4InRoot.y;
            }
        }
        
        // 【修复】从 J6 (腕部中心) 位置同步 smoothedInput，因为把手控制的是 J6
        if (visual_J6) 
        {
            smoothedInputPos = visual_J6.position;
            smoothedInputRot = visual_J6.rotation;
        }

        BuildGhostRig();
    }

    // 【新增】仅读取当前模型姿态（用于校准前获取当前角度）
    void ReadCurrentVisualAngles()
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

    // 初始化角度：可从配置的初始姿态或当前模型姿态读取
    void InitializeAnglesFromVisuals()
    {
        if (resetToInitialPoseOnStart && initialAngles != null && initialAngles.Length >= 7)
        {
            // 使用配置的初始角度
            for (int i = 0; i < 7; i++)
            {
                outAngles[i] = NormalizeAngle(initialAngles[i]);
                targetIKAngles[i] = outAngles[i]; // 【修复】同步设置目标角度，防止被覆盖
            }
            // 重置速度缓存，防止启动时产生运动
            Array.Clear(jointVelocities, 0, 7);
            // 立即应用到视觉模型，确保启动时显示正确
            ApplyToVisuals();
        }
        else
        {
            // 从当前模型姿态读取角度
            ReadCurrentVisualAngles();
        }
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

            // 根据设置选择 IK 算法
            if (useImprovedIK)
            {
                SolveImprovedIK(smoothedInputPos, smoothedInputRot);
            }
            else
            {
                SolveIterativeIK(smoothedInputPos, smoothedInputRot);
            }
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

    // ============================================================================
    // 【新增】改进解析法 IK（使用解析几何计算腕部偏移，替代 Ghost Rig）
    // ============================================================================
    void SolveImprovedIK(Vector3 targetPos, Quaternion targetRot)
    {
        // 步骤1：解算 J4, J5, J6
        // 【修复】J4-J6 负责匹配把手姿态，J7 独立控制不参与解算
        float currentJ1 = targetIKAngles[0];
        
        // 将目标旋转转换到底座坐标系
        Quaternion baseRot = Quaternion.Euler(0, currentJ1, 0);
        Quaternion wristLocalRot = Quaternion.Inverse(baseRot) * targetRot;
        
        // 解析分解把手姿态为 J4/J5/J6（保留基本的万向节锁保护）
        Vector3 wristAngles = DecomposeWristRotation(wristLocalRot);
        float j4 = wristAngles.y;  // Yaw
        float j5 = wristAngles.z;  // Roll
        float j6 = wristAngles.x;  // Pitch
        
        // 步骤2：解析计算腕部偏移（不使用 Ghost Rig）
        Vector3 wristOffset = CalculateWristOffsetAnalytical(j4, j5, j6);
        
        // 【调试】输出偏移值检查
        if (Time.frameCount % 60 == 0) {
            Debug.Log($"[IK] OffsetJ4J5={Offset_J4_to_J5}, OffsetJ5J6={Offset_J5_to_J6}, WristOffset={wristOffset.magnitude:F3}");
        }
        
        // 将腕部偏移转换到世界空间
        Vector3 wristOffsetWorld = baseRot * wristOffset;
        
        // 步骤3：反推 J4 平台目标位置
        Vector3 platformTarget = targetPos - wristOffsetWorld;
        
        // 【修复】考虑 J4_Drop_Offset，将 J4 平台位置转换为 J3 尖端位置
        // 注：shoulderHeight 已经在 Start() 中校准为固定值，不随运行时变化
        Vector3 j3TipTarget = platformTarget + transform.up * J4_Drop_Offset;
        
        // 步骤4：解算 J1
        Vector3 localTarget = transform.InverseTransformPoint(j3TipTarget);
        float flatDist = new Vector2(localTarget.x, localTarget.z).magnitude;
        
        // 步骤4：觧 J1
        // 【修复】当目标靠近底座中心时，使用特殊处理避免突变
        float targetJ1 = currentJ1;
        
        if (flatDist > j1DeadZoneRadius)
        {
            // 正常区域：计算 J1 角度
            float rawJ1 = Mathf.Atan2(localTarget.x, localTarget.z) * Mathf.Rad2Deg;
            float zoneFactor = Mathf.Clamp01((flatDist - j1DeadZoneRadius) / (j1SoftDeadZone - j1DeadZoneRadius + 0.001f));
            targetJ1 = Mathf.LerpAngle(currentJ1, rawJ1, zoneFactor);
        }
        else if (flatDist > 0.001f)
        {
            // 死区内但不是正中心：使用当前 J1 作为目标（冻结 J1）
            // 但保持原始目标位置不变，避免改变角度导致 J2/J3 突变
            targetJ1 = currentJ1;
        }
        // 如果 flatDist < 0.001f（正中心），保持 currentJ1 不变
        
        targetJ1 = Mathf.LerpAngle(currentJ1, targetJ1, iterationDamping);
        targetIKAngles[0] = targetJ1;
        
        // 步骤5：解算 J2/J3（使用原始未调整的目标位置）
        SolveArmPosition(localTarget, targetJ1);
        
        // 步骤6：赋值 J4-J6
        targetIKAngles[3] = j4;
        targetIKAngles[4] = NormalizeAngle(j5);
        targetIKAngles[5] = j6;
        
        lastJ4Angle = j4;
    }
    
    // 【新增】解析计算腕部偏移（基于实际机械臂几何）
    // 【修复】把手控制 J6 位置，不包含 J6→J7 偏移
    Vector3 CalculateWristOffsetAnalytical(float j4, float j5, float j6)
    {
        // 根据机械臂实际轴向构建旋转
        // J4: 绕 Y 轴旋转 (Yaw)
        // J5: 绕 Z 轴旋转 (Roll)  
        // J6: 绕 X 轴旋转 (Pitch)
        Quaternion q4 = Quaternion.Euler(0, j4, 0);
        Quaternion q5 = Quaternion.Euler(0, 0, j5);
        Quaternion q6 = Quaternion.Euler(j6, 0, 0);
        
        // 正向运动学：从 J4 平台中心计算到 J6 的偏移（不包含 J7）
        Vector3 offset = Vector3.zero;
        
        // J4 到 J5
        offset += q4 * Offset_J4_to_J5;
        
        // J5 到 J6
        offset += q4 * q5 * Offset_J5_to_J6;
        
        // 【修复】不加上 J6→J7 的偏移，因为把手控制的是 J6 位置
        // J7 独立控制，不影响位置解算
        
        return offset;
    }
    
    // 【新增】解析旋转分解（针对机械臂轴向优化）
    // 【修复】添加特异点保护，避免把手竖直时 J4 突变
    Vector3 DecomposeWristRotation(Quaternion q)
    {
        // 获取前向方向
        Vector3 forward = q * Vector3.forward;
        
        // Yaw (J4): 水平方向角
        // 【保护机制】当把手接近竖直时（万向节锁），保持上次的 J4 角度
        float yaw;
        if (Mathf.Abs(forward.y) > 0.98f) // 接近竖直（上或下）
        {
            yaw = lastJ4Angle; // 保持不变，避免突变
        }
        else
        {
            yaw = Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg;
        }
        
        // 去除 Yaw 后剩余旋转
        Quaternion qYaw = Quaternion.Euler(0, yaw, 0);
        Quaternion remainder = Quaternion.Inverse(qYaw) * q;
        
        // 从剩余旋转中提取 Pitch 和 Roll
        Vector3 right = remainder * Vector3.right;
        float roll = Mathf.Atan2(right.y, right.x) * Mathf.Rad2Deg;
        
        Quaternion qRoll = Quaternion.Euler(0, 0, roll);
        Quaternion finalRemainder = Quaternion.Inverse(qRoll) * remainder;
        float pitch = finalRemainder.eulerAngles.x;
        
        return new Vector3(NormalizeAngle(pitch), NormalizeAngle(yaw), NormalizeAngle(roll));
    }

    // ============================================================================
    // 【保留】原始迭代法 IK（使用 Ghost Rig）- 保留以便切换回去
    // ============================================================================
    void SolveIterativeIK(Vector3 targetPos, Quaternion targetRot)
    {
        float currentJ1 = targetIKAngles[0]; 

        // 【修复】从目标旋转中减去 J7 的旋转贡献，避免 J4-J6 与 J7 重复计算
        Quaternion j7Rotation = Quaternion.Euler(0, 0, targetIKAngles[6]);
        Quaternion adjustedTargetRot = targetRot * Quaternion.Inverse(j7Rotation);

        // 迭代 5 次以提高精度
        for (int i = 0; i < 5; i++)
        {
            Quaternion baseRot = Quaternion.Euler(0, currentJ1, 0);
            Quaternion wristLocal = Quaternion.Inverse(baseRot) * adjustedTargetRot;
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
            float targetJ1 = currentJ1;
            float flatDist = new Vector2(rootLocalTarget.x, rootLocalTarget.z).magnitude;

            if (flatDist > j1DeadZoneRadius) 
            {
                // 正常区域
                float rawJ1 = Mathf.Atan2(rootLocalTarget.x, rootLocalTarget.z) * Mathf.Rad2Deg;
                float zoneFactor = Mathf.Clamp01((flatDist - j1DeadZoneRadius) / (j1SoftDeadZone - j1DeadZoneRadius + 0.001f));
                targetJ1 = Mathf.LerpAngle(currentJ1, rawJ1, zoneFactor);
            }
            // 死区内保持 currentJ1 不变

            currentJ1 = Mathf.LerpAngle(currentJ1, targetJ1, iterationDamping);
            targetIKAngles[0] = currentJ1;
            
            // J2/J3 几何解算（使用原始目标）
            SolveArmPosition(rootLocalTarget, currentJ1);

            targetIKAngles[3] = j4;
            targetIKAngles[4] = NormalizeAngle(j5);
            targetIKAngles[5] = j6;
        }
        lastJ4Angle = targetIKAngles[3];
    }
    
    // 几何解算大臂小臂角度 (三角形余弦定理)
    void SolveArmPosition(Vector3 target, float j1_angle) {
        // shoulderHeight 是 J2 大臂根部在 RobotRoot 本地空间中的固定 Y 高度
        // 使用设计值 localPosition.y，不随机械臂运动而变化
        float shoulderHeight = 0f;
        if (visual_J2 != null)
        {
            shoulderHeight = visual_J2.localPosition.y;
        }
        
        float flatDist = new Vector2(target.x, target.z).magnitude; 
        float y = target.y - shoulderHeight;
        
        // 计算目标距离
        float D = Mathf.Sqrt(flatDist * flatDist + y * y);
        
        // 限制在臂长范围内
        float maxReach = L1_BigArm + L2_SmallArm;
        if (D > maxReach)
        {
            // 目标超出可达范围，缩放到最大距离
            float scale = maxReach / D;
            flatDist *= scale;
            y *= scale;
            D = maxReach;
        }
        D = Mathf.Max(D, 0.01f); // 避免除零

        float cosAlpha = (L1_BigArm * L1_BigArm + D * D - L2_SmallArm * L2_SmallArm) / (2 * L1_BigArm * D);
        float alpha = Mathf.Acos(Mathf.Clamp(cosAlpha, -1f, 1f));
        float beta = Mathf.Atan2(y, flatDist);
        float cosGamma = (L1_BigArm * L1_BigArm + L2_SmallArm * L2_SmallArm - D * D) / (2 * L1_BigArm * L2_SmallArm);
        float gamma = Mathf.Acos(Mathf.Clamp(cosGamma, -1f, 1f));

        // 调试输出
        if (Time.frameCount % 30 == 0)
        {
            float calcJ2 = (beta + alpha) * Mathf.Rad2Deg;
            float calcJ3 = (Mathf.PI - gamma) * Mathf.Rad2Deg;
            Debug.Log($"[SolveArm] shHeight={shoulderHeight:F2}, tgt=({target.x:F2},{target.y:F2}), flatD={flatDist:F2}, y={y:F2}, D={D:F2}, beta={beta*Mathf.Rad2Deg:F1}, alpha={alpha*Mathf.Rad2Deg:F1}, J2={calcJ2:F1}, J3={calcJ3:F1}");
        }

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
            
            // 【修复】J3 的限位基于与水平面的绝对角度（J2 + J3）
            if (index == 2) // J3 小臂
            {
                // 计算小臂相对于水平面的绝对角度
                float j2Angle = NormalizeAngle(outAngles[1]); // J2 大臂角度
                float absoluteAngle = j2Angle + angle; // 绝对角度 = J2 + J3
                
                // 将绝对角度限制在范围内
                float clampedAbsolute = Mathf.Clamp(absoluteAngle, limit.minAngle, limit.maxAngle);
                
                // 反推回 J3 的相对角度
                angle = clampedAbsolute - j2Angle;
            }
            else
            {
                angle = Mathf.Clamp(angle, limit.minAngle, limit.maxAngle);
            }
        }
        outAngles[index] = angle;
    }
    
    void UpdateLimitVisual(int index) {
        if (index >= jointLimits.Length) return;
        JointConfig limit = jointLimits[index];
        if (limit.meshRenderer == null) return;
        float angle = outAngles[index];
        
        // 【修复】J3 的限位检测也基于绝对角度
        if (index == 2) // J3 小臂
        {
            float j2Angle = NormalizeAngle(outAngles[1]);
            angle = j2Angle + angle; // 转换为绝对角度
        }
        
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