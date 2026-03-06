using System;
using System.Runtime.InteropServices;
using UnityEngine;
using LibVLCSharp.Shared;

[RequireComponent(typeof(MeshRenderer))]
public class HEVCStreamReceiver : MonoBehaviour
{
    [Header("Stream Settings")]
    public string streamUrl = "tcp://127.0.0.1:3335";
    public int networkCachingMs = 300;

    private LibVLC _libVLC;
    private MediaPlayer _mediaPlayer;
    private Texture2D _texture;

    // 重要：必须将委托保存为类的成员变量，防止被C#的垃圾回收器(GC)意外回收导致崩溃
    private MediaPlayer.LibVLCVideoFormatCb _videoFormatCb;
    private MediaPlayer.LibVLCVideoLockCb _videoLockCb;
    private MediaPlayer.LibVLCVideoDisplayCb _videoDisplayCb;

    // 存放视频帧数据的非托管内存指针
    private IntPtr _pixelBuffer = IntPtr.Zero;
    private uint _videoWidth = 0;
    private uint _videoHeight = 0;
    private uint _videoPitch = 0;
    private int _bufferSize = 0;

    // 标记是否有新的一帧准备好在 Unity 主线程中渲染
    private bool _frameUpdated = false;

    // 缓存材质
    private Material _quadMaterial;

    void Awake()
    {
        // 1. 初始化 LibVLCSharp
        Core.Initialize(Application.dataPath);

        // 2. 实例化 LibVLC
        _libVLC = new LibVLC("--no-osd", "--drop-late-frames","--avcodec-hw=any");
        _mediaPlayer = new MediaPlayer(_libVLC);

        // 3. 绑定回调委托 (缓存委托实例避免 GC 回收)
        _videoFormatCb = VideoFormat;
        _videoLockCb = VideoLock;
        _videoDisplayCb = VideoDisplay;

        // 设置视频格式及帧渲染回调
        _mediaPlayer.SetVideoFormatCallbacks(_videoFormatCb, null);
        _mediaPlayer.SetVideoCallbacks(_videoLockCb, null, _videoDisplayCb);

        // 获取材质，用于稍后赋值 Texture
        _quadMaterial = GetComponent<MeshRenderer>().material;
        
        // 翻转Y轴：VLC 解码的画面通常在 Unity 中是倒置的，直接翻转 UV 即可
        _quadMaterial.mainTextureScale = new Vector2(1, -1);
    }

    void Start()
    {
        // 创建 Media 对象，指明网络地址
        using (var media = new Media(_libVLC, streamUrl, FromType.FromLocation))
        {
            // 【核心】强制指定 demuxer 为 hevc，以便 LibVLC 能解析纯裸流
            media.AddOption(":demux=hevc");
            
            // 针对网络流降低延迟的优化参数
            media.AddOption($":network-caching={networkCachingMs}");
            media.AddOption(":clock-jitter=0");
            media.AddOption(":clock-synchro=0");

            _mediaPlayer.Play(media);
        }
    }

    void Update()
    {
        // 如果指针为空或者宽高未确定，说明视频还未开始解码，直接跳过
        if (_pixelBuffer == IntPtr.Zero || _videoWidth == 0 || _videoHeight == 0) return;

        // 初始化或重建 Texture2D
        if (_texture == null || _texture.width != _videoWidth || _texture.height != _videoHeight)
        {
            // LibVLC 的 "RV32" 对应 Unity 的 BGRA32 格式
            _texture = new Texture2D((int)_videoWidth, (int)_videoHeight, TextureFormat.BGRA32, false);
            _quadMaterial.mainTexture = _texture;
        }

        // 如果 VLC 已经在后台回调中写好了新的一帧
        if (_frameUpdated)
        {
            _frameUpdated = false;
            
            // 将非托管内存中的像素数据加载到 Texture 中，并应用到 GPU
            _texture.LoadRawTextureData(_pixelBuffer, _bufferSize);
            _texture.Apply();
        }
    }

    #region LibVLC 视频回调方法 (在非主线程触发)

    // LibVLC 请求确定视频格式时被调用
    private uint VideoFormat(ref IntPtr opaque, IntPtr chroma, ref uint width, ref uint height, ref uint pitches, ref uint lines)
    {
        _videoWidth = width;
        _videoHeight = height;

        // 设置像素格式为 RV32 (32位色，对应 BGRA)
        byte[] chromaBytes = System.Text.Encoding.ASCII.GetBytes("RV32");
        Marshal.Copy(chromaBytes, 0, chroma, chromaBytes.Length);

        // 计算每行字节数 (宽 x 4 字节) 和总缓冲大小
        _videoPitch = width * 4;
        _bufferSize = (int)(_videoPitch * height);
        pitches = _videoPitch;
        lines = height;

        // 重新分配非托管内存
        if (_pixelBuffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_pixelBuffer);
        }
        _pixelBuffer = Marshal.AllocHGlobal(_bufferSize);

        return _videoPitch;
    }

    // LibVLC 准备解码新的一帧，需要我们提供内存地址
    private IntPtr VideoLock(IntPtr opaque, IntPtr planes)
    {
        // 将非托管内存的地址告诉 VLC，让其把视频帧数据写入我们分配的内存中
        Marshal.WriteIntPtr(planes, _pixelBuffer);
        return IntPtr.Zero;
    }

    // LibVLC 已经将数据写入内存，通知我们进行显示
    private void VideoDisplay(IntPtr opaque, IntPtr picture)
    {
        // 设置标记位，让 Unity 在下一次 Update 时拉取数据 (只能在主线程操作 Texture)
        _frameUpdated = true;
    }

    #endregion

    void OnDestroy()
    {
        // 释放 MediaPlayer 和 LibVLC 实例
        if (_mediaPlayer != null)
        {
            _mediaPlayer.Stop();
            _mediaPlayer.Dispose();
            _mediaPlayer = null;
        }

        if (_libVLC != null)
        {
            _libVLC.Dispose();
            _libVLC = null;
        }

        // 释放非托管内存
        if (_pixelBuffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_pixelBuffer);
            _pixelBuffer = IntPtr.Zero;
        }

        if (_texture != null)
        {
            Destroy(_texture);
        }
    }
}