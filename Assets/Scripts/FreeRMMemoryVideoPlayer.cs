using UnityEngine;
using LibVLCSharp.Shared;
using System;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Collections.Concurrent;
using System.Collections.Generic;

// === 增强型帧重组器 ===
class FrameReassemblyBuffer
{
    public int totalExpectedBytes;
    public int currentReceivedBytes;
    public byte[] completeData;
    public float createTime;
    // 使用这个记录到底哪些碎片到了，防止重复收包和混乱
    public HashSet<int> receivedPacketIndices = new HashSet<int>();
}

public class FreeRMMemoryVideoPlayer : MonoBehaviour
{
    [Header("=== 图传 UDP 接收配置 ===")]
    public int sourcePort = 3334;
    [Tooltip("缓存等待的最大旧帧数量容忍度（抗网抖动极限）")]
    [Range(1, 10)] public int maxReorderFrames = 3; 

    [Header("=== 视频渲染目标 ===")]
    public Renderer targetScreen;
    public UnityEngine.UI.RawImage targetRawImage;

    private LibVLC _libVLC;
    private MediaPlayer _mediaPlayer;
    private Texture2D _vlcTexture;
    private Media _memoryMedia;
    private StreamMediaInput _mediaInput;
    private PushStreamAdapter _streamAdapter;
    
    private IntPtr _vlcPixelBuffer = IntPtr.Zero;
    private uint _videoWidth = 0;
    private uint _videoHeight = 0;

    private UdpClient _udpClient;
    public volatile bool isRunning = false;

    // 拼合干净的完整大帧，在这里排队等地张嘴
    private ConcurrentQueue<byte[]> _cleanH265StreamQueue = new ConcurrentQueue<byte[]>();

    private Dictionary<ushort, FrameReassemblyBuffer> _jitterBuffer;
    private ushort _latestFrameId = 0;
    private object _bufferLock = new object();

    // 大盘监控数据
    public int probeTotalPacketsReceived = 0;    
    public long probeTotalBytesReceived = 0;     
    public int currentRateKbps = 0;              
    public int vlcRestartCount = 0; 
    public int droppedFramesCount = 0; // 拼不齐被迫扔掉的帧

    private long _bytesInSecond = 0;
    private float _lastLogTime;

    private bool _isVLCRunning = false;
    private float _lastVLCCheckTime = 0f;
    private bool _needsRestart = false;

    void Awake()
    {
        Core.Initialize(Application.dataPath);
    }

    void Start()
    {
        _jitterBuffer = new Dictionary<ushort, FrameReassemblyBuffer>();
        StartUdpReceiver();
        InitVLCEngine();
    }

    private void InitVLCEngine()
    {
        _libVLC = new LibVLC(
            "--network-caching=60",  // 60ms缓存，容忍微小迟延
            "--avcodec-hw=any",
            "--drop-late-frames",
            "--skip-frames",
            "--no-audio"
        );
        CreateMediaPlayerAndPlay();
    }

    private void CreateMediaPlayerAndPlay()
    {
        if (_mediaPlayer != null)
        {
            _mediaPlayer.Stop();
            _mediaPlayer.Dispose();
        }

        _mediaPlayer = new MediaPlayer(_libVLC);
        _mediaPlayer.SetVideoFormatCallbacks(VideoFormatCallback, null);
        _mediaPlayer.SetVideoCallbacks(LockideoCallback, null, null);

        // 如果 VLC 遭遇硬解码解不了的废数据，重启标志
        _mediaPlayer.EncounteredError += (sender, e) => { _needsRestart = true; };
        _mediaPlayer.EndReached += (sender, e) => { _needsRestart = true; };

        // 清理一下老队列
        while (_cleanH265StreamQueue.TryDequeue(out _)) { }

        _streamAdapter = new PushStreamAdapter(_cleanH265StreamQueue);
        _mediaInput = new StreamMediaInput(_streamAdapter);
        _memoryMedia = new Media(_libVLC, _mediaInput);
        _memoryMedia.AddOption(":demux=hevc");

        _mediaPlayer.Play(_memoryMedia);
        _isVLCRunning = true;
    }

    private void StartUdpReceiver()
    {
        if (isRunning) return;
        try
        {
            _udpClient = new UdpClient();
            _udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, sourcePort));
            _udpClient.Client.ReceiveBufferSize = 1024 * 1024 * 8; 

            isRunning = true;
            _lastLogTime = Time.time;
            _udpClient.BeginReceive(new AsyncCallback(UdpReceiveCallback), null);
            Debug.Log($"[MemoryPlayer] 重组队列启动");
        }
        catch (Exception e)
        {
            Debug.LogError($"[MemoryPlayer] UDP 启动失败: {e.Message}");
        }
    }

    private void UdpReceiveCallback(IAsyncResult res)
    {
        if (!isRunning || _udpClient == null) return;
        try
        {
            IPEndPoint remoteEP = new IPEndPoint(IPAddress.Any, 0);
            byte[] packetData = _udpClient.EndReceive(res, ref remoteEP);

            if (packetData != null && packetData.Length > 8)
            {
                probeTotalPacketsReceived++;
                probeTotalBytesReceived += packetData.Length;
                _bytesInSecond += packetData.Length;

                ProcessPacket(packetData); // 交给分拣大师
            }

            if (isRunning) _udpClient.BeginReceive(new AsyncCallback(UdpReceiveCallback), null);
        }
        catch (ObjectDisposedException) { }
    }

    /// <summary>
    /// 【绝对核心：重组装】确保送入 VLC 的包完美还原 Node.js 那边的排列位置
    /// </summary>
    private void ProcessPacket(byte[] data)
    {
        ushort frameId = (ushort)((data[0] << 8) | data[1]);
        ushort packetIdx = (ushort)((data[2] << 8) | data[3]);
        int totalFrameSize = (int)((data[4] << 24) | (data[5] << 16) | (data[6] << 8) | data[7]);

        int payloadLen = data.Length - 8;

        lock (_bufferLock)
        {
            int diff = frameId - _latestFrameId;
            if (diff < -30000) diff += 65536; 
            
            // 如果来的包连最新帧的尾气都吃不到，直接当不可燃垃圾清走
            if (diff < -maxReorderFrames) return; 

            if (diff > 0)
            {
                _latestFrameId = frameId;
                CleanupOldBuffers(_latestFrameId);
            }

            if (!_jitterBuffer.TryGetValue(frameId, out FrameReassemblyBuffer buffer))
            {
                buffer = new FrameReassemblyBuffer
                {
                    totalExpectedBytes = totalFrameSize,
                    currentReceivedBytes = 0,
                    completeData = new byte[totalFrameSize],
                    createTime = Time.realtimeSinceStartup
                };
                _jitterBuffer[frameId] = buffer;
            }

            // 防重发设计：UDP偶尔会发两遍一模一样的包，我们要忽略掉它，不重复计算
            if (!buffer.receivedPacketIndices.Contains(packetIdx))
            {
                // 基于你的服务端设计计算偏移量！
                // maxPacketSize = 1400，它的 payload 最大就是 1400 - 8 = 1392
                int offset = packetIdx * 1392; 
                
                if (offset + payloadLen <= buffer.totalExpectedBytes)
                {
                    Buffer.BlockCopy(data, 8, buffer.completeData, offset, payloadLen);
                    buffer.currentReceivedBytes += payloadLen;
                    buffer.receivedPacketIndices.Add(packetIdx);
                }
            }

            // 【判定时刻】如果所有肉都被拼成了一块完美的排骨！
            if (buffer.currentReceivedBytes >= buffer.totalExpectedBytes)
            {
                // 完美！整整齐齐地推进 VLC 口中，绝不会出现 SPS 不认识的情况了
                _cleanH265StreamQueue.Enqueue(buffer.completeData);
                _jitterBuffer.Remove(frameId);
            }
        }
    }

    private void CleanupOldBuffers(ushort newId)
    {
        List<ushort> toRemove = new List<ushort>();
        foreach(var key in _jitterBuffer.Keys)
        {
            int diff = newId - key;
            if (diff < -30000) diff += 65536;
            
            // 若过期太久或超时 0.4秒都凑不齐，含泪销毁，不拖累大盘
            if (diff > maxReorderFrames || (Time.realtimeSinceStartup - _jitterBuffer[key].createTime > 0.4f))
            {
                toRemove.Add(key);
            }
        }
        foreach(var k in toRemove) 
        {
            _jitterBuffer.Remove(k);
            droppedFramesCount++; // 因为凑不齐而产生的死包丢弃
        }
    }

    void Update()
    {
        PerformVLCResuscitation();

        if (isRunning && Time.time - _lastLogTime >= 1.0f)
        {
            currentRateKbps = (int)((_bytesInSecond * 8) / 1000);
            _bytesInSecond = 0;
            _lastLogTime = Time.time;
        }

        if (_vlcPixelBuffer == IntPtr.Zero || _videoWidth == 0) return;

        if (_vlcTexture == null)
        {
            _vlcTexture = new Texture2D((int)_videoWidth, (int)_videoHeight, TextureFormat.BGRA32, false);
            
            if (targetRawImage != null)
            {
                targetRawImage.texture = _vlcTexture;
                targetRawImage.uvRect = new Rect(0, 1, 1, -1);
            }

            if (targetScreen != null)
            {
                targetScreen.material.mainTexture = _vlcTexture;
                targetScreen.material.mainTextureScale = new Vector2(1, -1);
            }
        }

        _vlcTexture.LoadRawTextureData(_vlcPixelBuffer, (int)(_videoWidth * _videoHeight * 4));
        _vlcTexture.Apply();
    }

    private void PerformVLCResuscitation()
    {
        if (!isRunning || probeTotalPacketsReceived < 10) return;

        if (Time.time - _lastVLCCheckTime > 1.5f)
        {
            _lastVLCCheckTime = Time.time;

            if (_mediaPlayer != null)
            {
                var state = _mediaPlayer.State;
                if (state == VLCState.Error || state == VLCState.Ended || state == VLCState.Stopped)
                {
                    _needsRestart = true;
                }
            }

            if (_needsRestart)
            {
                Debug.LogWarning($"[MemoryPlayer] 探测到解析瘫痪！强行拉起新生引擎！(已执行 {++vlcRestartCount} 次重生)");
                _needsRestart = false;
                CreateMediaPlayerAndPlay();
            }
        }
    }

    private uint VideoFormatCallback(ref IntPtr opaque, IntPtr chroma, ref uint width, ref uint height, ref uint pitches, ref uint lines)
    {
        byte[] format = System.Text.Encoding.ASCII.GetBytes("RV32");
        Marshal.Copy(format, 0, chroma, format.Length);
        _videoWidth = width;
        _videoHeight = height;
        pitches = width * 4;
        lines = height;

        int frameSize = (int)(width * height * 4);
        if (_vlcPixelBuffer != IntPtr.Zero) Marshal.FreeHGlobal(_vlcPixelBuffer);
        _vlcPixelBuffer = Marshal.AllocHGlobal(frameSize);

        return 1;
    }

    private IntPtr LockideoCallback(IntPtr opaque, IntPtr planes)
    {
        Marshal.WriteIntPtr(planes, _vlcPixelBuffer);
        return IntPtr.Zero;
    }

    void OnDestroy()
    {
        isRunning = false;
        if (_udpClient != null) { try { _udpClient.Close(); } catch { } }

        if (_mediaPlayer != null)
        {
            _mediaPlayer.Stop();
            _mediaPlayer.Dispose();
        }
        if (_memoryMedia != null) _memoryMedia.Dispose();
        if (_libVLC != null) _libVLC.Dispose();
        if (_vlcPixelBuffer != IntPtr.Zero) Marshal.FreeHGlobal(_vlcPixelBuffer);
    }
}

// -------------------------------------------------------------------------
// 适配器：将 ConcurrentQueue 中排好序的“大肉块”缓慢送喂给 LibVLC 的读取齿轮
// -------------------------------------------------------------------------
public class PushStreamAdapter : System.IO.Stream
{
    private ConcurrentQueue<byte[]> _queue;
    private byte[] _leftover;
    private int _leftoverOffset;

    public PushStreamAdapter(ConcurrentQueue<byte[]> queue)
    {
        _queue = queue;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        int bytesRead = 0;

        if (_leftover != null)
        {
            int available = _leftover.Length - _leftoverOffset;
            int toCopy = Math.Min(available, count);
            Buffer.BlockCopy(_leftover, _leftoverOffset, buffer, offset, toCopy);
            
            bytesRead += toCopy;
            _leftoverOffset += toCopy;
            offset += toCopy;
            count -= toCopy;

            if (_leftoverOffset >= _leftover.Length)
            {
                _leftover = null;
                _leftoverOffset = 0;
            }
        }

        while (count > 0 && _queue.TryDequeue(out byte[] frame))
        {
            int toCopy = Math.Min(frame.Length, count);
            Buffer.BlockCopy(frame, 0, buffer, offset, toCopy);

            bytesRead += toCopy;
            offset += toCopy;
            count -= toCopy;

            if (toCopy < frame.Length)
            {
                _leftover = frame;
                _leftoverOffset = toCopy;
            }
        }
        return bytesRead;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, System.IO.SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}