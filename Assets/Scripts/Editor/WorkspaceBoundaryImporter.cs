using UnityEngine;
using UnityEditor;
using System.IO;

/// <summary>
/// 编辑器工具：将 ComputeWorkspace.py 生成的 JSON 导入为 WorkspaceBoundaryData ScriptableObject
/// </summary>
public class WorkspaceBoundaryImporter : EditorWindow
{
    private TextAsset jsonAsset;
    private string outputPath = "Assets/WorkspaceBoundaryData.asset";

    [MenuItem("RMVR/Import Workspace Boundary JSON")]
    public static void ShowWindow()
    {
        GetWindow<WorkspaceBoundaryImporter>("Workspace Importer");
    }

    private void OnGUI()
    {
        EditorGUILayout.LabelField("工作空间边界数据导入", EditorStyles.boldLabel);
        EditorGUILayout.Space();

        jsonAsset = EditorGUILayout.ObjectField("JSON 文件", jsonAsset, typeof(TextAsset), false) as TextAsset;
        outputPath = EditorGUILayout.TextField("输出路径", outputPath);

        EditorGUILayout.Space();

        GUI.enabled = jsonAsset != null;
        if (GUILayout.Button("导入", GUILayout.Height(30)))
        {
            Import();
        }
        GUI.enabled = true;
    }

    private void Import()
    {
        if (jsonAsset == null)
        {
            EditorUtility.DisplayDialog("错误", "请先选择 JSON 文件", "确定");
            return;
        }

        var json = JsonUtility.FromJson<WorkspaceBoundaryJson>(jsonAsset.text);
        if (json == null || json.maxReachPerHeight == null)
        {
            EditorUtility.DisplayDialog("错误", "JSON 解析失败，请检查文件格式", "确定");
            return;
        }

        var data = ScriptableObject.CreateInstance<WorkspaceBoundaryData>();
        data.j2Min = json.j2Min;
        data.j2Max = json.j2Max;
        data.j3Min = json.j3Min;
        data.j3Max = json.j3Max;
        data.l1BigArm = json.l1BigArm;
        data.l2SmallArm = json.l2SmallArm;
        data.heightMin = json.heightMin;
        data.heightMax = json.heightMax;
        data.heightStep = json.heightStep;

        data.maxReachPerHeight = new float[json.maxReachPerHeight.Length];
        for (int i = 0; i < json.maxReachPerHeight.Length; i++)
        {
            data.maxReachPerHeight[i] = json.maxReachPerHeight[i].maxReach;
        }

        // 确保输出目录存在
        string dir = Path.GetDirectoryName(outputPath);
        if (!Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        AssetDatabase.CreateAsset(data, outputPath);
        AssetDatabase.SaveAssets();
        EditorUtility.DisplayDialog("完成", $"已导入到 {outputPath}", "确定");
        Selection.activeObject = data;
    }

    [System.Serializable]
    private class ReachEntry
    {
        public float height;
        public float maxReach;
    }

    [System.Serializable]
    private class WorkspaceBoundaryJson
    {
        public float j2Min;
        public float j2Max;
        public float j3Min;
        public float j3Max;
        public float l1BigArm;
        public float l2SmallArm;
        public float heightMin;
        public float heightMax;
        public float heightStep;
        public ReachEntry[] maxReachPerHeight;
    }
}
