using UnityEngine;
using UnityEngine.UI;
using System.Collections.Generic;

/// <summary>
/// 弧形HUD渲染器 - 将平面UI渲染到弯曲曲面
/// 类似《半衰期：Alyx》的真正弯曲HUD效果
/// </summary>
public class CurvedHUDRenderer : MonoBehaviour
{
    [Header("源UI（保持原有布局）")]
    [Tooltip("源Canvas（保持Screen Space - Overlay或Camera模式）")]
    public Canvas sourceCanvas;
    
    [Tooltip("渲染UI用的相机（可选，会自动创建）")]
    public Camera uiCamera;

    [Header("弧形显示")]
    [Tooltip("弧形段数（越多越平滑，建议8-16）")]
    [Range(4, 32)]
    public int segmentCount = 10;
    
    [Tooltip("弧形段预制体（带Renderer的Quad，可选）")]
    public GameObject segmentPrefab;

    [Header("弧形参数")]
    public float radius = 1.2f;
    [Range(60f, 240f)]
    public float arcAngle = 160f;
    public float height = -0.2f;
    public Vector2 panelSize = new Vector2(0.6f, 0.4f);

    [Header("跟随头部")]
    public Transform headTransform;
    public float followSpeed = 8f;

    private RenderTexture renderTexture;
    private List<GameObject> segments = new List<GameObject>();
    private Material sharedMaterial;

    void Start()
    {
        SetupRenderTexture();
        CreateSegments();
        FindHead();
    }

    void LateUpdate()
    {
        FollowHead();
    }

    void OnDestroy()
    {
        if (renderTexture != null)
        {
            renderTexture.Release();
            Destroy(renderTexture);
        }
        if (sharedMaterial != null)
        {
            Destroy(sharedMaterial);
        }
    }

    void SetupRenderTexture()
    {
        // 创建RenderTexture
        int width = 2048;
        int height = 512;
        renderTexture = new RenderTexture(width, height, 16, RenderTextureFormat.ARGB32);
        renderTexture.Create();

        // 创建材质
        sharedMaterial = new Material(Shader.Find("Unlit/Transparent"));
        sharedMaterial.mainTexture = renderTexture;

        // 设置相机
        if (uiCamera == null)
        {
            GameObject camObj = new GameObject("UI Render Camera");
            camObj.transform.SetParent(transform);
            uiCamera = camObj.AddComponent<Camera>();
            uiCamera.clearFlags = CameraClearFlags.SolidColor;
            uiCamera.backgroundColor = new Color(0, 0, 0, 0);
            uiCamera.orthographic = true;
            uiCamera.orthographicSize = 5;
            uiCamera.nearClipPlane = 0.01f;
            uiCamera.farClipPlane = 10;
        }
        
        uiCamera.targetTexture = renderTexture;

        // 设置源Canvas
        if (sourceCanvas != null)
        {
            // 将源Canvas移到相机前方渲染
            sourceCanvas.renderMode = RenderMode.ScreenSpaceCamera;
            sourceCanvas.worldCamera = uiCamera;
            sourceCanvas.planeDistance = 1;
            
            // 调整Canvas大小适配RenderTexture比例
            RectTransform rt = sourceCanvas.GetComponent<RectTransform>();
            if (rt != null)
            {
                rt.sizeDelta = new Vector2(2000, 500);
            }
        }
    }

    void CreateSegments()
    {
        // 清理旧段
        foreach (var seg in segments)
        {
            if (seg != null) Destroy(seg);
        }
        segments.Clear();

        float angleStep = arcAngle / segmentCount;
        float startAngle = -arcAngle / 2f;
        float segmentAngle = angleStep;

        for (int i = 0; i < segmentCount; i++)
        {
            float centerAngle = startAngle + angleStep * (i + 0.5f);
            CreateSegment(i, centerAngle, segmentAngle);
        }
    }

    void CreateSegment(int index, float centerAngle, float angleWidth)
    {
        GameObject segment;
        
        if (segmentPrefab != null)
        {
            segment = Instantiate(segmentPrefab, transform);
        }
        else
        {
            // 创建弧形Mesh
            segment = CreateCurvedMesh("Segment_" + index, index, centerAngle, angleWidth);
        }

        // 定位
        float rad = centerAngle * Mathf.Deg2Rad;
        Vector3 pos = new Vector3(
            Mathf.Sin(rad) * radius,
            height,
            Mathf.Cos(rad) * radius
        );

        segment.transform.localPosition = pos;
        segment.transform.localRotation = Quaternion.Euler(0, -centerAngle, 0);

        // 应用材质
        Renderer rend = segment.GetComponent<Renderer>();
        if (rend != null)
        {
            rend.material = sharedMaterial;
        }

        segments.Add(segment);
    }

    GameObject CreateCurvedMesh(string name, int index, float centerAngle, float angleWidth)
    {
        GameObject go = new GameObject(name);
        go.transform.SetParent(transform, false);

        MeshFilter mf = go.AddComponent<MeshFilter>();
        MeshRenderer mr = go.AddComponent<MeshRenderer>();

        Mesh mesh = new Mesh();
        mesh.name = name;

        // 创建弯曲的平面
        int hSegments = 4; // 水平分段
        int vSegments = 2; // 垂直分段

        Vector3[] vertices = new Vector3[(hSegments + 1) * (vSegments + 1)];
        Vector2[] uvs = new Vector2[vertices.Length];
        int[] triangles = new int[hSegments * vSegments * 6];

        float halfWidth = panelSize.x / segmentCount;
        float halfHeight = panelSize.y / 2;

        float startAngle = centerAngle - angleWidth / 2f;
        float angleStep = angleWidth / hSegments;

        for (int y = 0; y <= vSegments; y++)
        {
            float v = (float)y / vSegments;
            float yPos = Mathf.Lerp(-halfHeight, halfHeight, v);

            for (int x = 0; x <= hSegments; x++)
            {
                float u = (float)x / hSegments;
                float angle = startAngle + angleStep * x;
                float rad = angle * Mathf.Deg2Rad;

                // 在弧形上计算位置
                float r = radius; // 可以添加深度变化
                vertices[y * (hSegments + 1) + x] = new Vector3(
                    Mathf.Sin(rad) * r - Mathf.Sin(centerAngle * Mathf.Deg2Rad) * r,
                    yPos,
                    Mathf.Cos(rad) * r - Mathf.Cos(centerAngle * Mathf.Deg2Rad) * r
                );

                // UV映射 - 水平方向对应弧形段
                float uStart = (float)index / segmentCount;
                float uEnd = (float)(index + 1) / segmentCount;
                uvs[y * (hSegments + 1) + x] = new Vector2(Mathf.Lerp(uStart, uEnd, u), v);
            }
        }

        // 生成三角形
        int triIndex = 0;
        for (int y = 0; y < vSegments; y++)
        {
            for (int x = 0; x < hSegments; x++)
            {
                int i0 = y * (hSegments + 1) + x;
                int i1 = y * (hSegments + 1) + x + 1;
                int i2 = (y + 1) * (hSegments + 1) + x;
                int i3 = (y + 1) * (hSegments + 1) + x + 1;

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

        mf.mesh = mesh;

        return go;
    }

    void FindHead()
    {
        if (headTransform != null) return;
        
        var ovr = FindObjectOfType<OVRCameraRig>();
        if (ovr != null)
        {
            headTransform = ovr.centerEyeAnchor;
        }
        else if (Camera.main != null)
        {
            headTransform = Camera.main.transform;
        }
    }

    void FollowHead()
    {
        if (headTransform == null) return;

        Vector3 forward = headTransform.forward;
        forward.y = 0;
        forward.Normalize();

        Vector3 targetPos = headTransform.position + forward * radius;
        targetPos.y = headTransform.position.y + height;

        Quaternion targetRot = Quaternion.Euler(0, headTransform.eulerAngles.y, 0);

        transform.position = Vector3.Lerp(transform.position, targetPos, Time.deltaTime * followSpeed);
        transform.rotation = Quaternion.Slerp(transform.rotation, targetRot, Time.deltaTime * followSpeed);
    }

    [ContextMenu("刷新弧形")]
    public void Refresh()
    {
        CreateSegments();
    }

    void OnDrawGizmos()
    {
        // 绘制弧形参考
        Gizmos.color = Color.cyan;
        
        Vector3 center = Application.isPlaying ? transform.position : 
            (headTransform != null ? headTransform.position : transform.position);
        
        Quaternion rot = Application.isPlaying ? transform.rotation : 
            (headTransform != null ? Quaternion.Euler(0, headTransform.eulerAngles.y, 0) : Quaternion.identity);

        float startA = -arcAngle / 2f * Mathf.Deg2Rad;
        float endA = arcAngle / 2f * Mathf.Deg2Rad;
        
        Vector3 prev = center + rot * new Vector3(
            Mathf.Sin(startA) * radius, height, Mathf.Cos(startA) * radius);

        for (int i = 1; i <= 24; i++)
        {
            float t = i / 24f;
            float a = Mathf.Lerp(startA, endA, t);
            
            Vector3 pos = center + rot * new Vector3(
                Mathf.Sin(a) * radius, height, Mathf.Cos(a) * radius);
            
            Gizmos.DrawLine(prev, pos);
            prev = pos;
        }
    }
}
