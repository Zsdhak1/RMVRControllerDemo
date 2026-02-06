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
    [Header("=== 1. 物理拟真 ===")]
    public float maxJointSpeed = 120.0f;
    [Range(0.1f, 1.0f)] public float j1SpeedMultiplier = 0.6f;
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
    
    // 【修复点】补回了这两个变量定义
    [Tooltip("J1 硬死区：小于此距离直接不转")]
    public float j1DeadZoneRadius = 0.1f; 
    [Tooltip("J1 软死区：小于此距离开始降低转速，防止抽搐")]
    public float j1SoftDeadZone = 0.3f;

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

    // 数据层
    [HideInInspector] public float[] outAngles = new float[7];
    private float[] targetIKAngles = new float[7];

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

    public void SetTarget(Transform target) 
    { 
        activeTarget = target; 
        isTracking = true;
        Vector3 rawTargetPos = target.position + target.TransformDirection(gripOffset);
        smoothedInputPos = rawTargetPos;
        smoothedInputRot = target.rotation;
        currentVelocityPos = Vector3.zero;
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
        float joystickY = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, OVRInput.Controller.RTouch).y;
        targetIKAngles[6] += joystickY * j7RotationSpeed * Time.deltaTime;

        if (isTracking && activeTarget != null)
        {
            Vector3 rawPos = activeTarget.position + activeTarget.TransformDirection(gripOffset);
            smoothedInputPos = Vector3.SmoothDamp(smoothedInputPos, rawPos, ref currentVelocityPos, inputSmoothTime);
            smoothedInputRot = Quaternion.Slerp(smoothedInputRot, activeTarget.rotation, Time.deltaTime * 20f);

            SolveIterativeIK(smoothedInputPos, smoothedInputRot);
        }

        SimulatePhysicsMotors();
        ApplyLimitsAndWarnings();
        ApplyToVisuals();
    }

    void SimulatePhysicsMotors()
    {
        float dt = Time.deltaTime;
        for (int i = 0; i < 7; i++)
        {
            if (enablePhysicsSmoothing)
            {
                float speed = (i == 0) ? (maxJointSpeed * j1SpeedMultiplier) : maxJointSpeed;
                outAngles[i] = Mathf.MoveTowardsAngle(outAngles[i], targetIKAngles[i], speed * dt);
            }
            else
            {
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
        float currentJ1 = targetIKAngles[0]; 

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

            // 1. 获取腕部形状向量 (Ghost Rig J7 相对于 Platform)
            Vector3 wristOffsetLocal = ghost_Platform.InverseTransformPoint(g_J7.position);
            
            // 2. 旋转并变换到世界空间
            Vector3 wristOffsetRotated = baseRot * wristOffsetLocal;
            Vector3 wristOffsetWorld = transform.TransformDirection(wristOffsetRotated);

            // 3. 反推 J4 平台目标
            Vector3 platformTarget = targetPos - wristOffsetWorld;

            // 4. 大臂定位解算
            Vector3 j3TipTarget = platformTarget + transform.up * J4_Drop_Offset;
            Vector3 rootLocalTarget = transform.InverseTransformPoint(j3TipTarget);
            
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
            
            SolveArmPosition(rootLocalTarget, currentJ1);

            targetIKAngles[3] = j4;
            targetIKAngles[4] = NormalizeAngle(j5);
            targetIKAngles[5] = j6;
        }
        lastJ4Angle = targetIKAngles[3];
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
    
    void SolveArmPosition(Vector3 target, float j1_angle) {
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
        visual_J1.localRotation = Quaternion.Euler(0, outAngles[0], 0);
        float j2 = invert_J2 ? outAngles[1] : -outAngles[1];
        visual_J2.localRotation = Quaternion.Euler(j2, 0, 0);
        float j3 = invert_J3 ? outAngles[2] : -outAngles[2];
        visual_J3.localRotation = Quaternion.Euler(j3, 0, 0);
        if (visual_PlatformBase) visual_PlatformBase.rotation = Quaternion.Euler(0, visual_J1.eulerAngles.y, 0);
        visual_J4.localRotation = Quaternion.Euler(0, outAngles[3], 0);
        visual_J5.localRotation = Quaternion.Euler(0, 0, outAngles[4]);
        visual_J6.localRotation = Quaternion.Euler(outAngles[5], 0, 0);
        visual_J7.localRotation = Quaternion.Euler(0, 0, outAngles[6]);
    }

    float NormalizeAngle(float a) {
        while (a > 180) a -= 360;
        while (a < -180) a += 360;
        return a;
    }
}