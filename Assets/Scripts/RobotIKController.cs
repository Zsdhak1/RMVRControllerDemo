using UnityEngine;
using UnityEngine.UI; // 新增：用于访问 Slider 组件
using System;
using TMPro; 

[Serializable]
public class JointConfig
{
    public string name = "Joint";
    [Range(-360, 360)] public float minAngle = -170f;
    [Range(-360, 360)] public float maxAngle = 170f;
    public Renderer meshRenderer;
}

[Serializable]
public class PIDController
{
    [Tooltip("P (Proportional): 弹簧力度。数值越大，关节去目标越快，但容易过冲。")]
    public float Kp = 10.0f; 
    [Tooltip("D (Derivative): 阻尼（像在蜂蜜里动）。数值越大，刹车越稳，防止抖动。")]
    public float Kd = 2.0f;
    
    [HideInInspector] public float currentVelocity = 0f; 
    [HideInInspector] public float lastError = 0f;       
}

public class RobotIKController : MonoBehaviour
{
    [Header("=== 1. UI 交互设置 ===")]
    [Tooltip("请把场景里的 TextMeshPro 按钮的文字组件拖到这里")]
    public TMP_Text modeButtonText; 
    public bool useDirectWristMapping = false; 

    // -------- 新增 PID 滑块控制部分 --------
    [Header("PID 实时调节 (可选)")]
    [Tooltip("控制 Kp (力度) 的滑块")]
    public Slider kpSlider;
    [Tooltip("显示 Kp 当前数值的文本")]
    public TMP_Text kpValueText;
    
    [Tooltip("控制 Kd (阻尼) 的滑块")]
    public Slider kdSlider;
    [Tooltip("显示 Kd 当前数值的文本")]
    public TMP_Text kdValueText;
    // ------------------------------------

    [Header("=== 2. 高级 PID 物理引擎 ===")]
    public bool usePID = true; 
    [Tooltip("全局 PID 参数配置")]
    public PIDController globalPID = new PIDController();
    private PIDController[] jointPIDs = new PIDController[7];

    [Header("=== 3. 安全保护系统 ===")]
    [Tooltip("防打脸：底座周围多少米内禁止抓手进入")]
    public float safeZoneRadius = 0.4f;
    [Tooltip("奇异点保护：防止手臂完全伸直卡死 (通常设为 99%)")]
    [Range(0.9f, 0.999f)] public float maxReachPercentage = 0.98f;

    [Header("=== 4. 机械臂参数 ===")]
    public float L1_BigArm = 0.5f;   
    public float L2_SmallArm = 0.5f; 
    public float J4_Drop_Offset = 0.1f; 

    [Header("=== 5. 腕部连杆偏移 ===")]
    public Vector3 Offset_J4_to_J5 = new Vector3(0, 0, 0.1f);
    public Vector3 Offset_J5_to_J6 = new Vector3(0, 0, 0.1f);
    public Vector3 Offset_J6_to_J7 = new Vector3(0, 0, 0.15f);

    [Header("=== 6. IK 稳定性设置 ===")]
    [Range(0.1f, 1f)] public float iterationDamping = 0.6f; 
    public float j1DeadZoneRadius = 0.1f; 
    public float j1SoftDeadZone = 0.3f;
    [Range(0f, 0.5f)] public float inputSmoothTime = 0.1f;

    [Header("=== 7. 手动控制设置 ===")]
    public float manualJ7Speed = 90.0f; 

    [Header("=== 8. 关节限位配置 ===")]
    public JointConfig[] jointLimits = new JointConfig[7];
    public Color normalColor = Color.white;
    public Color warningColor = Color.magenta;

    [Header("=== 9. 关节引用 ===")]
    public Transform visual_J1;
    public Transform visual_J2;
    public Transform visual_J3;
    public Transform visual_PlatformBase; 
    public Transform visual_J4;
    public Transform visual_J5;
    public Transform visual_J6;
    public Transform visual_J7; 

    [Header("=== 10. 其他设置 ===")]
    public bool autoCalibrate = true;
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

        // 初始化 PID 控制器数组
        for(int i=0; i<7; i++) {
            jointPIDs[i] = new PIDController();
            jointPIDs[i].Kp = globalPID.Kp;
            jointPIDs[i].Kd = globalPID.Kd;
        }

        // -------- 新增：初始化滑块与事件监听 --------
        InitializePIDSliders();
        // -----------------------------------------

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
        UpdateButtonText();
    }

    // -------- 新增方法：初始化滑块 --------
    void InitializePIDSliders()
    {
        if (kpSlider != null)
        {
            kpSlider.value = globalPID.Kp;
            // 注册监听事件，当滑块值改变时调用 OnPIDConfigChanged
            kpSlider.onValueChanged.AddListener(delegate { OnPIDConfigChanged(); });
        }

        if (kdSlider != null)
        {
            kdSlider.value = globalPID.Kd;
            kdSlider.onValueChanged.AddListener(delegate { OnPIDConfigChanged(); });
        }

        // 初始化一次文本显示
        UpdatePIDText();
    }

    // -------- 新增方法：处理 PID 变更 --------
    public void OnPIDConfigChanged()
    {
        // 1. 从滑块读取新值
        if (kpSlider != null) globalPID.Kp = kpSlider.value;
        if (kdSlider != null) globalPID.Kd = kdSlider.value;

        // 2. 将新值应用到所有关节的控制器上
        for (int i = 0; i < 7; i++)
        {
            if (jointPIDs[i] != null)
            {
                jointPIDs[i].Kp = globalPID.Kp;
                jointPIDs[i].Kd = globalPID.Kd;
            }
        }

        // 3. 更新 UI 文本显示
        UpdatePIDText();
    }

    void UpdatePIDText()
    {
        if (kpValueText != null) kpValueText.text = $"Kp: {globalPID.Kp:F1}";
        if (kdValueText != null) kdValueText.text = $"Kd: {globalPID.Kd:F1}";
    }
    // -----------------------------------------

    public void ToggleControlMode()
    {
        useDirectWristMapping = !useDirectWristMapping;
        UpdateButtonText();
    }

    void UpdateButtonText()
    {
        if (modeButtonText != null)
        {
            modeButtonText.text = useDirectWristMapping ? "Mode: Manual (Wrist)" : "Mode: Auto IK";
            modeButtonText.color = useDirectWristMapping ? Color.yellow : Color.green;
        }
    }

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
        for(int i=0; i<7; i++) jointPIDs[i].currentVelocity = 0f;
    }

    public void StopTracking() { activeTarget = null; isTracking = false; }

    void Update()
    {
        float joystickY = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, OVRInput.Controller.RTouch).y;
        targetIKAngles[6] += joystickY * manualJ7Speed * Time.deltaTime;

        if (isTracking && activeTarget != null)
        {
            Vector3 rawPos = activeTarget.position + activeTarget.TransformDirection(gripOffset);
            
            // 安全区逻辑
            Vector3 baseToTarget = rawPos - transform.position;
            if (baseToTarget.magnitude < safeZoneRadius)
            {
                rawPos = transform.position + baseToTarget.normalized * safeZoneRadius;
            }

            // 奇异点保护逻辑
            float maxReach = (L1_BigArm + L2_SmallArm) * maxReachPercentage;
            Vector3 shoulderPos = visual_J2.position;
            Vector3 shoulderToTarget = rawPos - shoulderPos;
            if (shoulderToTarget.magnitude > maxReach)
            {
                rawPos = shoulderPos + shoulderToTarget.normalized * maxReach;
            }

            smoothedInputPos = Vector3.SmoothDamp(smoothedInputPos, rawPos, ref currentVelocityPos, inputSmoothTime);
            smoothedInputRot = Quaternion.Slerp(smoothedInputRot, activeTarget.rotation, Time.deltaTime * 20f);

            SolveIterativeIK(smoothedInputPos, smoothedInputRot);
        }

        UpdatePhysicsMotors();
        ApplyLimitsAndWarnings();
        ApplyToVisuals();
    }

    void UpdatePhysicsMotors()
    {
        float dt = Time.deltaTime;
        for (int i = 0; i < 7; i++)
        {
            float target = targetIKAngles[i];
            float current = outAngles[i];

            if (usePID)
            {
                float error = Mathf.DeltaAngle(current, target);
                // 这里使用 jointPIDs[i] 的值，这些值现在会被滑块实时更新
                float force = error * jointPIDs[i].Kp - jointPIDs[i].currentVelocity * jointPIDs[i].Kd;
                jointPIDs[i].currentVelocity += force * dt;
                outAngles[i] += jointPIDs[i].currentVelocity * dt;
            }
            else
            {
                outAngles[i] = Mathf.MoveTowardsAngle(current, target, 120f * dt);
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
            
            float j4 = 0, j5 = 0, j6 = 0;

            if (useDirectWristMapping)
            {
                Vector3 directAngles = activeTarget.localEulerAngles;
                j4 = NormalizeAngle(directAngles.y); 
                j5 = NormalizeAngle(directAngles.z); 
                j6 = NormalizeAngle(directAngles.x);
            }
            else
            {
                Vector3 wristAngles = RobustDecompose(wristLocal);
                j4 = wristAngles.y;
                j5 = wristAngles.z;
                j6 = wristAngles.x;
            }

            ghost_J4.localEulerAngles = new Vector3(0, j4, 0);
            ghost_J5.localEulerAngles = new Vector3(0, 0, j5);
            ghost_J6.localEulerAngles = new Vector3(j6, 0, 0);

            Vector3 wristOffsetLocal = ghost_Platform.InverseTransformPoint(g_J7.position);
            Vector3 wristOffsetRotated = baseRot * wristOffsetLocal;
            Vector3 wristOffsetWorld = transform.TransformDirection(wristOffsetRotated);
            Vector3 platformTarget = targetPos - wristOffsetWorld;
            Vector3 j3TipTarget = platformTarget + transform.up * J4_Drop_Offset;
            Vector3 rootLocalTarget = transform.InverseTransformPoint(j3TipTarget);
            
            float flatDist = new Vector2(rootLocalTarget.x, rootLocalTarget.z).magnitude;
            float targetJ1 = 0;

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