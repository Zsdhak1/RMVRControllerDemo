using UnityEngine;
using LibVLCSharp.Shared;
using System;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Collections.Concurrent;
using System.Collections.Generic;

// === 修复版：FreeRMMemoryVideoPlayer ===
// 修复内容：
// 1. 禁用硬件解码，使用软件解码避免兼容性问题
// 2. 添加NAL起始码检查和自动注入
// 3. 增强日志输出，便于调试
// 4. 添加HEVC流格式验证

class FrameReassemblyBuffer
{
    public int totalExpectedBytes;
    public int currentReceivedBytes;
    public byte[] completeData;
    public float createTime;
    public HashSet<int> receivedPacketIndices = new HashSet<int>();
}

public class FreeRMMemoryVideoPlayerFixed : MonoBehaviour
{
    [Header("=== 图传 UDP 接收配置 ===")]
    public int sourcePort = 3334;
    [Range(1, 10)] public int maxReorderFrames = 3; 

    [Header("=== 视频渲染目标 ===")]
    public Renderer targetScreen;
    public UnityEngine.UI.RawImage targetRawImage;

    [Header("=== 修复选项 ===")]
    [Tooltip("强制软件解码，避免硬件解码兼容性问题")]
    public bool forceSoftwareDecoding = true;
    
    [Tooltip("自动添加NAL起始码（如果缺失）")]
    public bool autoInjectNALStartCode = true;
    
    [Tooltip("NAL起始码字节（HEVC Annex-B格式）")]
    public byte[] nalStartCode = new byte[] { 0x00, 0x00, 0x00, 0x01 };

    private LibVLC _libVLC;
    private MediaPlayer _mediaPlayer;
    private Texture2D _vlcTexture;
    private Media _memoryMedia;
    private StreamMediaInput _mediaInput;
    private PushStreamAdapterFixed _streamAdapter;
    
    private IntPtr _vlcPixelBuffer = IntPtr.Zero;
    private uint _videoWidth = 0;
    private uint _videoHeight = 0;

    private UdpClient _udpClient;
    public volatile bool isRunning = false;

    private ConcurrentQueue<byte[]> _cleanH265StreamQueue = new ConcurrentQueue<byte[]>();

    private Dictionary<ushort, FrameReassemblyBuffer> _jitterBuffer;
    private ushort _latestFrameId = 0;
    private object _bufferLock = new object();

    // 监控数据
    public int probeTotalPacketsReceived = 0;    
    public long probeTotalBytesReceived = 0;     
    public int currentRateKbps = 0;              
    public int vlcRestartCount = 0; 
    public int droppedFramesCount = 0;
    public int nalInjectedCount = 0; // 新增：NAL注入计数
    public int framesWithNAL = 0;    // 新增：已有NAL的帧数
    public int framesWithoutNAL = 0; // 新增：缺少NAL的帧数

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
        
        Debug.Log("[MemoryPlayerFixed] 修复版播放器启动");
        Debug.Log($"[MemoryPlayerFixed] 强制软件解码: {forceSoftwareDecoding}");
        Debug.Log($"[MemoryPlayerFixed] 自动注入NAL: {autoInjectNALStartCode}");
    }

    private void InitVLCEngine()
    {
        var options = new List<string>
        {
            "--network-caching=60",
            "--drop-late-frames",
            "--skip-frames",
            "--no-audio"
        };

        // 修复1: 根据设置选择硬件或软件解码
        if (forceSoftwareDecoding)
        {
            options.Add("--avcodec-hw=none");
            Debug.Log("[MemoryPlayerFixed] 使用软件解码（修复硬件兼容性问题）");
        }
        else
        {
            options.Add("--avcodec-hw=any");
        }

        _libVLC = new LibVLC(options.ToArray());
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

        _mediaPlayer.EncounteredError += (sender, e) => 
        { 
            Debug.LogError("[MemoryPlayerFixed] VLC遇到错误!");
            _needsRestart = true; 
        };
        _mediaPlayer.EndReached += (sender, e) => 
        { 
            Debug.LogWarning("[MemoryPlayerFixed] VLC播放结束");
            _needsRestart = true; 
        };

        while (_cleanH265StreamQueue.TryDequeue(out _)) { }

        _streamAdapter = new PushStreamAdapterFixed(_cleanH265StreamQueue);
        _mediaInput = new StreamMediaInput(_streamAdapter);
        _memoryMedia = new Media(_libVLC, _mediaInput);
        _memoryMedia.AddOption(":demux=hevc");

        _mediaPlayer.Play(_memoryMedia);
        _isVLCRunning = true;
        
        Debug.Log("[MemoryPlayerFixed] VLC播放器已启动");
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
            Debug.Log($"[MemoryPlayerFixed] UDP接收器启动在端口 {sourcePort}");
        }
        catch (Exception e)
        {
            Debug.LogError($"[MemoryPlayerFixed] UDP 启动失败: {e.Message}");
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

                ProcessPacket(packetData);
            }

            if (isRunning) _udpClient.BeginReceive(new AsyncCallback(UdpReceiveCallback), null);
        }
        catch (ObjectDisposedException) { }
    }

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

            if (!buffer.receivedPacketIndices.Contains(packetIdx))
            {
                int offset = packetIdx * 1392; 
                
                if (offset + payloadLen <= buffer.totalExpectedBytes)
                {
                    Buffer.BlockCopy(data, 8, buffer.completeData, offset, payloadLen);
                    buffer.currentReceivedBytes += payloadLen;
                    buffer.receivedPacketIndices.Add(packetIdx);
                }
            }

            // 帧完整接收
            if (buffer.currentReceivedBytes >= buffer.totalExpectedBytes)
            {
                byte[] finalFrame = PrepareFrameForVLC(buffer.completeData, frameId);
                _cleanH265StreamQueue.Enqueue(finalFrame);
                _jitterBuffer.Remove(frameId);
            }
        }
    }

    /// <summary>
    /// 修复2: 准备帧数据，检查并处理NAL起始码
    /// </summary>
    private byte[] PrepareFrameForVLC(byte[] frameData, ushort frameId)
    {
        if (frameData == null || frameData.Length < 4)
        {
            return frameData;
        }

        // 检查是否已有NAL起始码 (0x00 0x00 0x00 0x01)
        bool hasNALStartCode = (frameData[0] == 0x00 && frameData[1] == 0x00 && 
                                frameData[2] == 0x00 && frameData[3] == 0x01);

        if (hasNALStartCode)
        {
            framesWithNAL++;
            
            // 每100帧打印一次日志
            if (framesWithNAL % 100 == 0)
            {
                byte nalType = (byte)((frameData[4] >> 1) & 0x3F);
                Debug.Log($"[MemoryPlayerFixed] 帧{frameId}已有NAL起始码，NAL类型: {nalType}");
            }
            
            return frameData;
        }
        else
        {
            framesWithoutNAL++;
            
            // 修复3: 自动注入NAL起始码
            if (autoInjectNALStartCode)
            {
                byte[] newFrame = new byte[frameData.Length + nalStartCode.Length];
                Buffer.BlockCopy(nalStartCode, 0, newFrame, 0, nalStartCode.Length);
                Buffer.BlockCopy(frameData, 0, newFrame, nalStartCode.Length, frameData.Length);
                
                nalInjectedCount++;
                
                // 打印前几次注入，便于调试
                if (nalInjectedCount <= 5)
                {
                    Debug.LogWarning($"[MemoryPlayerFixed] 帧{frameId}缺少NAL起始码，已自动注入! " +
                                   $"前4字节: {frameData[0]:X2} {frameData[1]:X2} {frameData[2]:X2} {frameData[3]:X2}");
                }
                
                return newFrame;
            }
            else
            {
                // 不注入，但打印警告
                if (framesWithoutNAL <= 5)
                {
                    Debug.LogWarning($"[MemoryPlayerFixed] 帧{frameId}缺少NAL起始码! " +
                                   $"前4字节: {frameData[0]:X2} {frameData[1]:X2} {frameData[2]:X2} {frameData[3]:X2}");
                }
                return frameData;
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
            
            if (diff > maxReorderFrames || (Time.realtimeSinceStartup - _jitterBuffer[key].createTime > 0.4f))
            {
                toRemove.Add(key);
            }
        }
        foreach(var k in toRemove) 
        {
            _jitterBuffer.Remove(k);
            droppedFramesCount++;
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
            
            // 每秒打印一次统计信息
            if (probeTotalPacketsReceived > 0)
            {
                Debug.Log($"[MemoryPlayerFixed] 速率: {currentRateKbps}kbps, " +
                         $"包: {probeTotalPacketsReceived}, " +
                         $"NAL注入: {nalInjectedCount}, " +
                         $"有NAL: {framesWithNAL}, 无NAL: {framesWithoutNAL}, " +
                         $"VLC重启: {vlcRestartCount}");
            }
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
                Debug.LogWarning($"[MemoryPlayerFixed] VLC重启 #{++vlcRestartCount}");
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
// 修复版适配器
// -------------------------------------------------------------------------
public class PushStreamAdapterFixed : System.IO.Stream
{
    private ConcurrentQueue<byte[]> _queue;
    private byte[] _leftover;
    private int _leftoverOffset;

    public PushStreamAdapterFixed(ConcurrentQueue<byte[]> queue)
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
