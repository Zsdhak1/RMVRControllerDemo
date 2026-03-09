using System;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEngine;
using LibVLCSharp.Shared;

[RequireComponent(typeof(MeshRenderer))]
public class HEVCStreamReceiverOptimized : MonoBehaviour
{
    [Header("Stream Settings")]
    public string streamUrl = "tcp://127.0.0.1:3335";
    public int networkCachingMs = 300;

    private LibVLC _libVLC;
    private MediaPlayer _mediaPlayer;
    private Texture2D _texture;

    // 委托缓存
    private MediaPlayer.LibVLCVideoFormatCb _videoFormatCb;
    private MediaPlayer.LibVLCVideoLockCb _videoLockCb;
    private MediaPlayer.LibVLCVideoDisplayCb _videoDisplayCb;

    // ========== 三缓冲区设计（无锁交换） ==========
    // 索引 0: VLC 写入
    // 索引 1: 准备渲染
    // 索引 2: 备用
    private IntPtr[] _buffers = new IntPtr[3];
    private int _writeIndex = 0;      // VLC 写入的索引
    private int _readIndex = 1;       // Unity 读取的索引
    private int _pendingIndex = -1;   // 待交换的索引
    private int _bufferSize = 0;
    
    // 使用 Interlocked 进行无锁状态同步
    private int _frameReady = 0;      // 0=无新帧, 1=有新帧
    
    private uint _videoWidth = 0;
    private uint _videoHeight = 0;
    private uint _videoPitch = 0;

    private Material _quadMaterial;
    private bool _isInitialized = false;

    void Awake()
    {
        // Android 特定：使用正确的初始化路径
        #if UNITY_ANDROID && !UNITY_EDITOR
        Core.Initialize(Application.dataPath + "!/assets");
        #else
        Core.Initialize(Application.dataPath);
        #endif

        // 优化参数：Android 禁用某些高消耗功能
        _libVLC = new LibVLC(
            "--no-osd", 
            "--drop-late-frames",
            "--avcodec-hw=dxva2",  // 使用更稳定的硬件解码
            "--no-video-title-show",
            "--no-stats",
            "--verbose=0"
        );
        _mediaPlayer = new MediaPlayer(_libVLC);

        // 设置缓冲区数量，减少延迟
        _mediaPlayer.NsObject = IntPtr.Zero;

        _videoFormatCb = VideoFormat;
        _videoLockCb = VideoLock;
        _videoDisplayCb = VideoDisplay;

        _mediaPlayer.SetVideoFormatCallbacks(_videoFormatCb, null);
        _mediaPlayer.SetVideoCallbacks(_videoLockCb, null, _videoDisplayCb);

        _quadMaterial = GetComponent<MeshRenderer>().material;
        _quadMaterial.mainTextureScale = new Vector2(1, -1);
    }

    void Start()
    {
        using (var media = new Media(_libVLC, streamUrl, FromType.FromLocation))
        {
            media.AddOption(":demux=hevc");
            media.AddOption($":network-caching={networkCachingMs}");
            media.AddOption(":clock-jitter=0");
            media.AddOption(":clock-synchro=0");
            
            // Android 优化：限制解码线程数
            #if UNITY_ANDROID
            media.AddOption(":avcodec-threads=2");
            media.AddOption(":file-caching=100");
            #endif

            _mediaPlayer.Play(media);
        }
    }

    void Update()
    {
        if (!_isInitialized) return;

        // 快速检查，无锁
        if (Interlocked.CompareExchange(ref _frameReady, 0, 1) == 1)
        {
            // 有新帧，交换缓冲区
            int newRead = _writeIndex;
            _writeIndex = _readIndex;
            _readIndex = newRead;

            // 初始化或更新纹理
            if (_texture == null || _texture.width != (int)_videoWidth || _texture.height != (int)_videoHeight)
            {
                CreateTexture();
            }

            // 上传数据到 GPU
            if (_texture != null && _buffers[_readIndex] != IntPtr.Zero)
            {
                _texture.LoadRawTextureData(_buffers[_readIndex], _bufferSize);
                _texture.Apply(false); // false = 异步上传
            }
        }
    }

    private void CreateTexture()
    {
        if (_texture != null)
        {
            Destroy(_texture);
        }

        // 尝试使用兼容性格式
        TextureFormat format = TextureFormat.RGBA32;
        
        _texture = new Texture2D((int)_videoWidth, (int)_videoHeight, format, false);
        _quadMaterial.mainTexture = _texture;
        
        Debug.Log($"[HEVC] 纹理创建: {_videoWidth}x{_videoHeight}");
    }

    #region LibVLC 回调（这些回调在独立线程运行，不能阻塞！）

    private uint VideoFormat(ref IntPtr opaque, IntPtr chroma, ref uint width, ref uint height, ref uint pitches, ref uint lines)
    {
        _videoWidth = width;
        _videoHeight = height;

        // 使用 RGBA 格式
        byte[] chromaBytes = System.Text.Encoding.ASCII.GetBytes("RGBA");
        Marshal.Copy(chromaBytes, 0, chroma, 4);

        _videoPitch = width * 4;
        _bufferSize = (int)(_videoPitch * height);
        pitches = _videoPitch;
        lines = height;

        // 分配三缓冲区
        for (int i = 0; i < 3; i++)
        {
            if (_buffers[i] != IntPtr.Zero)
                Marshal.FreeHGlobal(_buffers[i]);
            
            _buffers[i] = Marshal.AllocHGlobal(_bufferSize);
            // 清零
            byte[] clear = new byte[_bufferSize];
            Marshal.Copy(clear, 0, _buffers[i], _bufferSize);
        }

        _writeIndex = 0;
        _readIndex = 1;
        _frameReady = 0;
        _isInitialized = true;

        Debug.Log($"[HEVC] 格式设置: {width}x{height}");
        return _videoPitch;
    }

    private IntPtr VideoLock(IntPtr opaque, IntPtr planes)
    {
        // 绝对不能在这里加锁或阻塞！
        // 直接返回当前写入缓冲区
        Marshal.WriteIntPtr(planes, _buffers[_writeIndex]);
        return IntPtr.Zero;
    }

    private void VideoDisplay(IntPtr opaque, IntPtr picture)
    {
        // 简单标记新帧就绪，使用原子操作
        // 如果上一帧还没被读取，直接覆盖（丢帧策略）
        Interlocked.Exchange(ref _frameReady, 1);
    }

    #endregion

    void OnDestroy()
    {
        _isInitialized = false;

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

        for (int i = 0; i < 3; i++)
        {
            if (_buffers[i] != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_buffers[i]);
                _buffers[i] = IntPtr.Zero;
            }
        }

        if (_texture != null)
        {
            Destroy(_texture);
        }
    }
}
