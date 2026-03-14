using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 弧形UI Mesh - 将RawImage渲染到弯曲的Mesh上
/// 适用于显示弯曲的UI纹理
/// </summary>
[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class CurvedUIMesh : MonoBehaviour
{
    [Header("弧形参数")]
    [Range(30f, 180f)]
    public float arcAngle = 60f;
    
    public float radius = 1.2f;
    public float height = 0.4f;
    
    [Tooltip("Mesh细分段数（越多越平滑）")]
    [Range(4, 32)]
    public int segments = 16;

    [Header("UI纹理")]
    [Tooltip("UI渲染纹理（来自RenderTexture）")]
    public Texture uiTexture;
    
    [Tooltip("UI颜色")]
    public Color color = Color.white;

    private Mesh mesh;
    private MeshFilter meshFilter;
    private MeshRenderer meshRenderer;
    private Material material;

    void Start()
    {
        meshFilter = GetComponent<MeshFilter>();
        meshRenderer = GetComponent<MeshRenderer>();
        
        CreateMesh();
        CreateMaterial();
    }

    void OnValidate()
    {
        if (Application.isEditor && meshFilter != null)
        {
            CreateMesh();
        }
    }

    /// <summary>
    /// 创建弯曲Mesh
    /// </summary>
    void CreateMesh()
    {
        if (mesh == null)
        {
            mesh = new Mesh();
            mesh.name = "CurvedUI";
        }
        mesh.Clear();

        int hSegments = segments;
        int vSegments = 2;

        Vector3[] vertices = new Vector3[(hSegments + 1) * (vSegments + 1)];
        Vector2[] uvs = new Vector2[vertices.Length];
        int[] triangles = new int[hSegments * vSegments * 6];

        float halfHeight = height / 2f;
        float startAngle = -arcAngle / 2f * Mathf.Deg2Rad;
        float endAngle = arcAngle / 2f * Mathf.Deg2Rad;

        for (int y = 0; y <= vSegments; y++)
        {
            float v = (float)y / vSegments;
            float yPos = Mathf.Lerp(-halfHeight, halfHeight, v);

            for (int x = 0; x <= hSegments; x++)
            {
                float u = (float)x / hSegments;
                float angle = Mathf.Lerp(startAngle, endAngle, u);

                // 计算弧形位置
                float xPos = Mathf.Sin(angle) * radius;
                float zPos = Mathf.Cos(angle) * radius - radius; // 让中心点在0

                int index = y * (hSegments + 1) + x;
                vertices[index] = new Vector3(xPos, yPos, zPos);
                uvs[index] = new Vector2(u, v);
            }
        }

        // 生成三角形
        int triIndex = 0;
        for (int y = 0; y < vSegments; y++)
        {
            for (int x = 0; x < hSegments; x++)
            {
                int i0 = y * (hSegments + 1) + x;
                int i1 = i0 + 1;
                int i2 = (y + 1) * (hSegments + 1) + x;
                int i3 = i2 + 1;

                triangles[triIndex++] = i0;
                triangles[triIndex++] = i2;
                triangles[triIndex++] = i1;

                triangles[triIndex++] = i1;
                triangles[triIndex++] = i2;
                triangles[triIndex++] = i3;
            }
        }

        mesh.vertices = vertices;
        mesh.uv = uvs;
        mesh.triangles = triangles;
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();

        meshFilter.mesh = mesh;
    }

    void CreateMaterial()
    {
        if (material == null)
        {
            material = new Material(Shader.Find("Unlit/Transparent"));
            material.name = "CurvedUI_Material";
        }

        if (uiTexture != null)
        {
            material.mainTexture = uiTexture;
        }
        material.color = color;

        meshRenderer.material = material;
    }

    /// <summary>
    /// 设置UI纹理
    /// </summary>
    public void SetTexture(Texture texture)
    {
        uiTexture = texture;
        if (material != null)
        {
            material.mainTexture = texture;
        }
    }

    /// <summary>
    /// 设置弧形参数
    /// </summary>
    public void SetArc(float angle, float newRadius)
    {
        arcAngle = angle;
        radius = newRadius;
        CreateMesh();
    }

    void OnDrawGizmos()
    {
        // 绘制弧形参考线
        Gizmos.color = Color.cyan;
        
        float startAngle = -arcAngle / 2f * Mathf.Deg2Rad;
        float endAngle = arcAngle / 2f * Mathf.Deg2Rad;
        float halfHeight = height / 2f;

        // 上弧线
        Vector3 prev = transform.TransformPoint(new Vector3(
            Mathf.Sin(startAngle) * radius,
            halfHeight,
            Mathf.Cos(startAngle) * radius - radius
        ));

        for (int i = 1; i <= segments; i++)
        {
            float t = (float)i / segments;
            float angle = Mathf.Lerp(startAngle, endAngle, t);
            
            Vector3 pos = transform.TransformPoint(new Vector3(
                Mathf.Sin(angle) * radius,
                halfHeight,
                Mathf.Cos(angle) * radius - radius
            ));
            
            Gizmos.DrawLine(prev, pos);
            prev = pos;
        }

        // 下弧线
        prev = transform.TransformPoint(new Vector3(
            Mathf.Sin(startAngle) * radius,
            -halfHeight,
            Mathf.Cos(startAngle) * radius - radius
        ));

        for (int i = 1; i <= segments; i++)
        {
            float t = (float)i / segments;
            float angle = Mathf.Lerp(startAngle, endAngle, t);
            
            Vector3 pos = transform.TransformPoint(new Vector3(
                Mathf.Sin(angle) * radius,
                -halfHeight,
                Mathf.Cos(angle) * radius - radius
            ));
            
            Gizmos.DrawLine(prev, pos);
            prev = pos;
        }
    }
}
