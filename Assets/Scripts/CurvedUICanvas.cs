using UnityEngine;
using UnityEngine.UI;
using System.Collections.Generic;

/// <summary>
/// 弧形UI Canvas - 使用Shader弯曲整个Canvas
/// 将此脚本挂载到要弯曲的 Canvas 上
/// </summary>
[RequireComponent(typeof(Canvas))]
public class CurvedUICanvas : MonoBehaviour
{
    [Header("弯曲设置")]
    [Range(0f, 2f)]
    [Tooltip("弯曲强度 (0=平面, 1=明显弯曲)")]
    public float curveStrength = 0.5f;
    
    [Tooltip("弯曲半径（影响弯曲曲率）")]
    public float curveRadius = 10f;

    [Header("材质")]
    [Tooltip("弧形UI Shader（留空会自动创建）")]
    public Shader curvedShader;
    
    [Tooltip("弧形材质（留空会自动创建）")]
    public Material curvedMaterial;

    [Header("性能")]
    [Tooltip("是否缓存材质")]
    public bool cacheMaterial = true;

    private Canvas canvas;
    private Graphic[] graphics;
    private Dictionary<Graphic, Material> originalMaterials = new Dictionary<Graphic, Material>();
    private bool isCurved = false;

    void Start()
    {
        canvas = GetComponent<Canvas>();
        
        // 确保Canvas是World Space
        if (canvas.renderMode != RenderMode.WorldSpace)
        {
            Debug.Log($"[{name}] 自动设置为 World Space 模式");
            canvas.renderMode = RenderMode.WorldSpace;
        }

        SetupShader();
        ApplyCurve();
    }

    void OnValidate()
    {
        if (Application.isPlaying && isCurved)
        {
            UpdateMaterialProperties();
        }
    }

    void OnDestroy()
    {
        // 恢复原始材质
        foreach (var kvp in originalMaterials)
        {
            if (kvp.Key != null)
            {
                kvp.Key.material = kvp.Value;
            }
        }

        // 清理创建的材质
        if (curvedMaterial != null && !cacheMaterial)
        {
            Destroy(curvedMaterial);
        }
    }

    /// <summary>
    /// 设置Shader
    /// </summary>
    void SetupShader()
    {
        // 查找Shader
        if (curvedShader == null)
        {
            curvedShader = Shader.Find("Custom/CurvedUI");
            if (curvedShader == null)
            {
                Debug.LogError($"[{name}] 找不到 Custom/CurvedUI Shader！请确保Shader文件在项目中。");
                return;
            }
        }

        // 创建材质
        if (curvedMaterial == null)
        {
            curvedMaterial = new Material(curvedShader);
            curvedMaterial.name = $"CurvedUI_Material_{name}";
        }

        UpdateMaterialProperties();
    }

    /// <summary>
    /// 更新材质属性
    /// </summary>
    void UpdateMaterialProperties()
    {
        if (curvedMaterial == null) return;

        curvedMaterial.SetFloat("_CurveStrength", curveStrength);
        curvedMaterial.SetFloat("_CurveRadius", curveRadius);
    }

    /// <summary>
    /// 应用弯曲效果到所有Graphic组件
    /// </summary>
    [ContextMenu("应用弯曲")]
    public void ApplyCurve()
    {
        if (curvedMaterial == null) return;

        graphics = GetComponentsInChildren<Graphic>(true);
        
        foreach (var graphic in graphics)
        {
            // 保存原始材质
            if (!originalMaterials.ContainsKey(graphic))
            {
                originalMaterials[graphic] = graphic.material;
            }

            // 创建实例材质（如果需要）
            if (cacheMaterial)
            {
                graphic.material = curvedMaterial;
            }
            else
            {
                graphic.material = new Material(curvedMaterial);
            }
            
            // 确保使用自定义材质
            graphic.material = curvedMaterial;
        }

        isCurved = true;
        Debug.Log($"[{name}] 已应用弧形效果到 {graphics.Length} 个Graphic组件");
    }

    /// <summary>
    /// 移除弯曲效果
    /// </summary>
    [ContextMenu("恢复平面")]
    public void RemoveCurve()
    {
        foreach (var kvp in originalMaterials)
        {
            if (kvp.Key != null)
            {
                kvp.Key.material = kvp.Value;
            }
        }
        
        isCurved = false;
    }

    /// <summary>
    /// 设置弯曲强度
    /// </summary>
    public void SetCurveStrength(float strength)
    {
        curveStrength = Mathf.Clamp(strength, 0f, 2f);
        UpdateMaterialProperties();
    }

    /// <summary>
    /// 切换弯曲效果
    /// </summary>
    public void ToggleCurve()
    {
        if (isCurved)
            RemoveCurve();
        else
            ApplyCurve();
    }
}
