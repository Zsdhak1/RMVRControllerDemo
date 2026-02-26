using UnityEngine;
using LibVLCSharp.Shared;
using System;
using System.Runtime.InteropServices;

public class FreeRMVideoPlayer : MonoBehaviour
{
    [Header("视频目标")]
    public string streamUrl = "tcp://127.0.0.1:3335";
    public Renderer targetScreen; // 将你的图传面片拖给它

    private LibVLC _libVLC;
    private MediaPlayer _mediaPlayer;
    private Texture2D _vlcTexture;
    
    private IntPtr _vlcPixelBuffer = IntPtr.Zero;
    private uint _videoWidth = 0;
    private uint _videoHeight = 0;

    void Awake()
    {
        // 核心库初始化
        Core.Initialize(Application.dataPath);
    }

    void Start()
    {
        // 硬解、零延迟、去封装
        _libVLC = new LibVLC(
            "--network-caching=100",
            "--avcodec-hw=any",
            "--drop-late-frames",
            "--skip-frames",
            "--no-audio"
        );

        _mediaPlayer = new MediaPlayer(_libVLC);
        
        // 关键回调：当底层解码出格式时，我们要为它分配指针内存
        _mediaPlayer.SetVideoFormatCallbacks(VideoFormatCallback, null);
        // 关键回调：把解码出的每一帧画面，刷进我们的指针里
        _mediaPlayer.SetVideoCallbacks(LockideoCallback, null, null);

        // 创建加载流
        var media = new Media(_libVLC, streamUrl, FromType.FromLocation);
        media.AddOption(":demux=hevc");

        // 直接点火
        _mediaPlayer.Play(media);
    }

    void Update()
    {
        if (_vlcPixelBuffer == IntPtr.Zero || _videoWidth == 0) return;

        // 如果还没创建材质，立刻用底层的内存指针强制渲染一张图
        if (_vlcTexture == null)
        {
            _vlcTexture = new Texture2D((int)_videoWidth, (int)_videoHeight, TextureFormat.BGRA32, false);
            if (targetScreen != null)
            {
                targetScreen.material.mainTexture = _vlcTexture;
                // Quest OpenGL翻转修复
                targetScreen.material.mainTextureScale = new Vector2(1, -1);
            }
        }

        // 把 C++ 指针里疯狂涌进来的 H265 解码帧，直接刷上材质单！
        _vlcTexture.LoadRawTextureData(_vlcPixelBuffer, (int)(_videoWidth * _videoHeight * 4));
        _vlcTexture.Apply();
    }

    // ====== 底层 LibVLCSharp C++ 到 C# 内存映射桥 ======
    // 这里就是跨过了人家收费服务的地方，我们自己手搓！

    private uint VideoFormatCallback(ref IntPtr opaque, IntPtr chroma, ref uint width, ref uint height, ref uint pitches, ref uint lines)
    {
        // 告诉 VLC，我们要的是纯正的 BGRA 色彩排列（Unity Texture 支持的最佳形式）
        byte[] format = System.Text.Encoding.ASCII.GetBytes("RV32");
        Marshal.Copy(format, 0, chroma, format.Length);

        _videoWidth = width;
        _videoHeight = height;
        pitches = width * 4;
        lines = height;

        int frameSize = (int)(width * height * 4);
        
        // 分配一段死锁的内存常驻给 VLC 把图画上去
        if (_vlcPixelBuffer != IntPtr.Zero) Marshal.FreeHGlobal(_vlcPixelBuffer);
        _vlcPixelBuffer = Marshal.AllocHGlobal(frameSize);

        return 1;
    }

    private IntPtr LockideoCallback(IntPtr opaque, IntPtr planes)
    {
        // 当它要画图前，把我们买好的那块画布指给他
        Marshal.WriteIntPtr(planes, _vlcPixelBuffer);
        return IntPtr.Zero;
    }

    void OnDestroy()
    {
        if (_mediaPlayer != null)
        {
            _mediaPlayer.Stop();
            _mediaPlayer.Dispose();
        }
        if (_libVLC != null) _libVLC.Dispose();
        if (_vlcPixelBuffer != IntPtr.Zero) Marshal.FreeHGlobal(_vlcPixelBuffer);
    }
} 