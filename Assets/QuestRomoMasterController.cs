using UnityEngine;
using RenderHeads.Media.AVProVideo; // 引用 AVPro
using System.Collections;

public class QuestRomoMasterController : MonoBehaviour
{
    [Header("调试与网络 (必填)")]
    public LogModuleManager logModule; 
    public StreamForwarder streamForwarder; 

    [Header("VR 交互 (必填)")]
    public Transform headsetCamera;       
    public Transform followButton; 
    
    [Header("位置设置")]
    public Vector3 buttonOffset = new Vector3(0.0f, -0.3f, 0.8f); 

    [Header("AVPro 设置 (必填)")]
    public GameObject videoPlanePrefab; 
    
    // --- 修复点 1：恢复 public 变量，供 LogModuleManager 修改 ---
    // 默认指向本地转发器的 TCP 端口
    public string videoPath = "tcp://127.0.0.1:3335"; 

    // 运行时变量
    private GameObject _screenInstance;
    private MediaPlayer _mediaPlayer;

    void Start()
    {
        // 自动启动
        StartCoroutine(AutoStartVideo());
    }

    IEnumerator AutoStartVideo()
    {
        yield return new WaitForSeconds(1.0f);
        if(logModule) logModule.AddLog("System Ready. Auto-starting AVPro...");
        StartVideoSystem();
    }

    void Update()
    {
        // 按钮跟随
        if (followButton != null && headsetCamera != null)
        {
            Vector3 targetPos = headsetCamera.TransformPoint(buttonOffset);
            Quaternion targetRot = Quaternion.LookRotation(followButton.position - headsetCamera.position);
            
            followButton.position = Vector3.Lerp(followButton.position, targetPos, Time.deltaTime * 5f);
            followButton.rotation = Quaternion.Slerp(followButton.rotation, targetRot, Time.deltaTime * 5f);
        }
    }

    public void OnButtonClicked()
    {
        if (_screenInstance != null)
        {
            StopVideoSystem();
        }
        else
        {
            StartVideoSystem();
        }
    }

    void StartVideoSystem()
    {
        if (_screenInstance != null) return;

        if(logModule) logModule.AddLog("Starting System...");

        // 1. 启动转发器
        if (streamForwarder != null)
        {
            streamForwarder.StartForwarding();
        }

        // 2. 生成屏幕
        if (headsetCamera != null && videoPlanePrefab != null)
        {
            Vector3 spawnPos = headsetCamera.position + headsetCamera.forward * 2.0f;
            
            // 假设你的 AVProScreen 预制体已经修正了旋转 (X=90)
            Quaternion lookRot = Quaternion.LookRotation(headsetCamera.forward);
            _screenInstance = Instantiate(videoPlanePrefab, spawnPos, lookRot);
        }

        // 3. 启动 AVPro 播放
        if (_screenInstance != null)
        {
            _mediaPlayer = _screenInstance.GetComponent<MediaPlayer>();
            if (_mediaPlayer != null)
            {
                // 绑定事件
                _mediaPlayer.Events.AddListener(OnVideoEvent);

                // --- 修复点 2：使用 AVPro v2/v3 的新版 API ---
                // 旧版: OpenVideoFromFile -> 新版: OpenMedia
                // 旧版: FileLocation -> 新版: MediaPathType
                
                bool success = _mediaPlayer.OpenMedia(
                    MediaPathType.AbsolutePathOrURL, // 路径类型
                    videoPath,                       // 播放路径
                    autoPlay: true                   // 自动播放
                );

                if (success)
                {
                    if(logModule) logModule.AddLog($"AVPro Opening: {videoPath}");
                }
                else
                {
                    if(logModule) logModule.AddLog("<color=red>AVPro Open Failed!</color>");
                }
            }
            else
            {
                if(logModule) logModule.AddLog("<color=red>Error: No MediaPlayer on Prefab!</color>");
            }
        }
    }

    // AVPro 事件回调
    public void OnVideoEvent(MediaPlayer mp, MediaPlayerEvent.EventType et, ErrorCode errorCode)
    {
        switch (et)
        {
            case MediaPlayerEvent.EventType.ReadyToPlay:
                if(logModule) logModule.AddLog("<color=green>AVPro: Connected & Playing!</color>");
                break;
            case MediaPlayerEvent.EventType.Error:
                if(logModule) logModule.AddLog($"<color=red>AVPro Error: {errorCode}</color>");
                break;
            case MediaPlayerEvent.EventType.Stalled:
                // 网络流经常会有 buffering，不用太担心
                // if(logModule) logModule.AddLog("<color=orange>AVPro: Buffering...</color>");
                break;
        }
    }

    void StopVideoSystem()
    {
        if(logModule) logModule.AddLog("Stopping System...");

        if (_mediaPlayer != null)
        {
            _mediaPlayer.Events.RemoveAllListeners();
            
            // --- 修复点 3：使用新版关闭 API ---
            _mediaPlayer.CloseMedia(); 
            _mediaPlayer = null;
        }

        if (_screenInstance != null)
        {
            Destroy(_screenInstance);
            _screenInstance = null;
        }

        if (streamForwarder != null)
        {
            streamForwarder.StopForwarding();
        }
    }

    void OnDestroy()
    {
        StopVideoSystem();
    }
}