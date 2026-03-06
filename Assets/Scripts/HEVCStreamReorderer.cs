using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using UnityEngine;

/// <summary>
/// HEVC 视频流 UDP 包重排序器
/// 根据 RoboMaster 2026 通信协议，重新组装乱序的 UDP 视频包，然后转发到 TCP
/// </summary>
public class HEVCStreamReorderer : MonoBehaviour
{
    [Header("Network Settings")]
    public int udpPort = 3334;
    public int tcpPort = 3335;
    
    [Header("Buffer Settings")]
    [Tooltip("最大缓存帧数，超过将丢弃最旧的帧")]
    public int maxCachedFrames = 5;
    [Tooltip("帧超时时间(秒)，超过此时间未收齐的帧将被丢弃")]
    public float frameTimeout = 0.1f;
    [Tooltip("发送缓冲区大小(字节)")]
    public int tcpSendBufferSize = 1024 * 1024 * 2; // 2MB
    
    [Header("Debug")]
    public bool enableDebugLog = true;

    // UDP 接收
    private UdpClient _udpClient;
    private volatile bool _isRunning = false;
    
    // TCP 服务器
    private TcpListener _tcpListener;
    private TcpClient _tcpClient;
    private NetworkStream _tcpStream;
    private object _tcpLock = new object();
    
    // 帧缓存: 帧编号 -> 帧数据
    private Dictionary<ushort, FrameBuffer> _frameBuffers = new Dictionary<ushort, FrameBuffer>();
    private object _frameLock = new object();
    
    // 统计
    private long _totalUdpPackets = 0;
    private long _totalFramesSent = 0;
    private long _totalBytesSent = 0;
    private long _droppedFrames = 0;
    private long _droppedPackets = 0;

    /// <summary>
    /// 帧缓存结构
    /// </summary>
    private class FrameBuffer
    {
        public ushort FrameId;           // 帧编号
        public uint TotalSize;           // 帧总字节数
        public float ReceiveTime;        // 首次接收时间
        public Dictionary<ushort, byte[]> Slices = new Dictionary<ushort, byte[]>(); // 分片序号 -> 数据
        public HashSet<ushort> ReceivedSlices = new HashSet<ushort>(); // 已接收的分片序号
        public bool IsComplete = false;  // 是否已完整接收
        public bool IsSent = false;      // 是否已发送
    }

    void Start()
    {
        StartTCPServer();
        StartUDPReceiver();
        
        if (enableDebugLog)
            Debug.Log("[HEVCReorderer] 启动完成，等待连接...");
    }

    void Update()
    {
        // 清理超时的帧
        CleanupTimeoutFrames();
        
        // 尝试发送已完整的帧
        TrySendCompleteFrames();
        
        // 打印统计信息
        if (enableDebugLog && Time.frameCount % 300 == 0)
        {
            PrintStats();
        }
    }

    #region TCP Server

    private void StartTCPServer()
    {
        try
        {
            _tcpListener = new TcpListener(IPAddress.Any, tcpPort);
            _tcpListener.Start();
            
            _ = Task.Run(AcceptTCPClients);
            
            if (enableDebugLog)
                Debug.Log($"[TCP] 服务器启动，监听端口 {tcpPort}");
        }
        catch (Exception ex)
        {
            Debug.LogError($"[TCP] 启动失败: {ex.Message}");
        }
    }

    private async Task AcceptTCPClients()
    {
        while (true)
        {
            try
            {
                if (_tcpClient == null || !_tcpClient.Connected)
                {
                    if (enableDebugLog)
                        Debug.Log("[TCP] 等待客户端连接...");
                    
                    _tcpClient = await _tcpListener.AcceptTcpClientAsync();
                    _tcpClient.SendBufferSize = tcpSendBufferSize;
                    _tcpClient.NoDelay = true; // 禁用 Nagle 算法，降低延迟
                    
                    lock (_tcpLock)
                    {
                        _tcpStream = _tcpClient.GetStream();
                    }
                    
                    if (enableDebugLog)
                        Debug.Log("[TCP] 客户端已连接");
                }
                await Task.Delay(100);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[TCP] 接受连接错误: {ex.Message}");
                await Task.Delay(1000);
            }
        }
    }

    #endregion

    #region UDP Receiver

    private void StartUDPReceiver()
    {
        try
        {
            _udpClient = new UdpClient();
            _udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, udpPort));
            _udpClient.Client.ReceiveBufferSize = 1024 * 1024 * 8; // 8MB 接收缓冲区

            _isRunning = true;
            _udpClient.BeginReceive(UDPReceiveCallback, null);
            
            if (enableDebugLog)
                Debug.Log($"[UDP] 接收器启动，监听端口 {udpPort}");
        }
        catch (Exception e)
        {
            Debug.LogError($"[UDP] 启动失败: {e.Message}");
        }
    }

    private void UDPReceiveCallback(IAsyncResult res)
    {
        if (!_isRunning || _udpClient == null) return;

        byte[] packetData = null;
        IPEndPoint remoteEP = new IPEndPoint(IPAddress.Any, 0);

        try
        {
            packetData = _udpClient.EndReceive(res, ref remoteEP);
            _totalUdpPackets++;
        }
        catch { }

        // 继续接收下一个包
        try
        {
            if (_isRunning && _udpClient != null)
                _udpClient.BeginReceive(new AsyncCallback(UDPReceiveCallback), null);
        }
        catch { }

        // 处理包
        if (packetData != null && packetData.Length > 8)
        {
            ProcessUDPPacket(packetData);
        }
    }

    /// <summary>
    /// 处理单个 UDP 包，解析头部并缓存
    /// </summary>
    private void ProcessUDPPacket(byte[] packetData)
    {
        // 解析头部 (小端序)
        ushort frameId = (ushort)(packetData[0] | (packetData[1] << 8));
        ushort sliceIndex = (ushort)(packetData[2] | (packetData[3] << 8));
        uint totalSize = (uint)(packetData[4] | (packetData[5] << 8) | (packetData[6] << 16) | (packetData[7] << 24));

        // 提取 HEVC 数据
        int payloadLen = packetData.Length - 8;
        byte[] payload = new byte[payloadLen];
        Buffer.BlockCopy(packetData, 8, payload, 0, payloadLen);

        lock (_frameLock)
        {
            // 检查是否超过最大缓存帧数
            if (_frameBuffers.Count >= maxCachedFrames && !_frameBuffers.ContainsKey(frameId))
            {
                // 找到最旧的帧并丢弃
                ushort oldestFrame = FindOldestFrame();
                if (oldestFrame != frameId)
                {
                    _frameBuffers.Remove(oldestFrame);
                    _droppedFrames++;
                    if (enableDebugLog)
                        Debug.LogWarning($"[UDP] 帧缓存已满，丢弃旧帧 #{oldestFrame}");
                }
            }

            // 获取或创建帧缓存
            if (!_frameBuffers.TryGetValue(frameId, out FrameBuffer frameBuffer))
            {
                frameBuffer = new FrameBuffer
                {
                    FrameId = frameId,
                    TotalSize = totalSize,
                    ReceiveTime = Time.time
                };
                _frameBuffers[frameId] = frameBuffer;
            }

            // 检查分片是否已存在（重复包）
            if (frameBuffer.ReceivedSlices.Contains(sliceIndex))
            {
                return; // 忽略重复包
            }

            // 存储分片
            frameBuffer.Slices[sliceIndex] = payload;
            frameBuffer.ReceivedSlices.Add(sliceIndex);

            // 检查是否完整（简化判断：实际应该根据分片数量和总大小判断）
            // 这里假设如果收到的数据量接近总大小，就认为完整
            uint receivedSize = 0;
            foreach (var slice in frameBuffer.Slices.Values)
            {
                receivedSize += (uint)slice.Length;
            }

            // 如果收到的数据量达到或超过总大小，标记为完整
            if (receivedSize >= totalSize * 0.95f) // 允许 5% 的丢包
            {
                frameBuffer.IsComplete = true;
            }
        }
    }

    private ushort FindOldestFrame()
    {
        ushort oldest = 0;
        float oldestTime = float.MaxValue;

        foreach (var kvp in _frameBuffers)
        {
            if (kvp.Value.ReceiveTime < oldestTime)
            {
                oldestTime = kvp.Value.ReceiveTime;
                oldest = kvp.Key;
            }
        }

        return oldest;
    }

    #endregion

    #region Frame Processing

    /// <summary>
    /// 尝试发送已完整的帧
    /// </summary>
    private void TrySendCompleteFrames()
    {
        lock (_tcpLock)
        {
            if (_tcpStream == null || !_tcpStream.CanWrite) return;
        }

        List<ushort> framesToRemove = new List<ushort>();

        lock (_frameLock)
        {
            // 找到所有完整且未发送的帧，按帧编号排序
            var completeFrames = new List<FrameBuffer>();
            foreach (var kvp in _frameBuffers)
            {
                if (kvp.Value.IsComplete && !kvp.Value.IsSent)
                {
                    completeFrames.Add(kvp.Value);
                }
            }

            // 按帧编号排序（处理 ushort 回绕）
            completeFrames.Sort((a, b) => CompareFrameIds(a.FrameId, b.FrameId));

            // 发送帧
            foreach (var frame in completeFrames)
            {
                if (SendFrame(frame))
                {
                    frame.IsSent = true;
                    framesToRemove.Add(frame.FrameId);
                }
                else
                {
                    break; // 发送失败，停止发送
                }
            }

            // 清理已发送的帧
            foreach (var frameId in framesToRemove)
            {
                _frameBuffers.Remove(frameId);
            }
        }
    }

    /// <summary>
    /// 比较帧编号，处理 ushort 回绕 (0-65535)
    /// </summary>
    private int CompareFrameIds(ushort a, ushort b)
    {
        // 考虑 ushort 回绕的情况
        // 如果差值大于 32768，说明发生了回绕
        short diff = (short)(a - b);
        return diff;
    }

    /// <summary>
    /// 发送完整帧到 TCP
    /// </summary>
    private bool SendFrame(FrameBuffer frame)
    {
        try
        {
            // 按分片序号排序
            var sortedSlices = new List<ushort>(frame.Slices.Keys);
            sortedSlices.Sort();

            // 计算总大小
            int totalSize = 0;
            foreach (var sliceIndex in sortedSlices)
            {
                totalSize += frame.Slices[sliceIndex].Length;
            }

            // 合并分片
            byte[] completeFrame = new byte[totalSize];
            int offset = 0;
            foreach (var sliceIndex in sortedSlices)
            {
                byte[] sliceData = frame.Slices[sliceIndex];
                Buffer.BlockCopy(sliceData, 0, completeFrame, offset, sliceData.Length);
                offset += sliceData.Length;
            }

            // 发送到 TCP
            lock (_tcpLock)
            {
                if (_tcpStream != null && _tcpStream.CanWrite)
                {
                    _tcpStream.Write(completeFrame, 0, completeFrame.Length);
                    _totalFramesSent++;
                    _totalBytesSent += completeFrame.Length;
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"[TCP] 发送帧 #{frame.FrameId} 失败: {ex.Message}");
            lock (_tcpLock)
            {
                _tcpStream = null;
                _tcpClient = null;
            }
        }

        return false;
    }

    /// <summary>
    /// 清理超时的帧
    /// </summary>
    private void CleanupTimeoutFrames()
    {
        List<ushort> toRemove = new List<ushort>();

        lock (_frameLock)
        {
            foreach (var kvp in _frameBuffers)
            {
                if (Time.time - kvp.Value.ReceiveTime > frameTimeout)
                {
                    toRemove.Add(kvp.Key);
                }
            }

            foreach (var frameId in toRemove)
            {
                _frameBuffers.Remove(frameId);
                _droppedFrames++;
                if (enableDebugLog)
                    Debug.LogWarning($"[UDP] 帧 #{frameId} 超时，已丢弃");
            }
        }
    }

    #endregion

    #region Stats & Debug

    private void PrintStats()
    {
        int pendingFrames = 0;
        int completeFrames = 0;

        lock (_frameLock)
        {
            pendingFrames = _frameBuffers.Count;
            foreach (var kvp in _frameBuffers)
            {
                if (kvp.Value.IsComplete) completeFrames++;
            }
        }

        Debug.Log($"[Stats] UDP包: {_totalUdpPackets}, 发送帧: {_totalFramesSent}, " +
                  $"发送字节: {_totalBytesSent/1024/1024:F2}MB, 丢弃帧: {_droppedFrames}, " +
                  $"缓存中: {pendingFrames}(完整:{completeFrames})");
    }

    void OnDestroy()
    {
        _isRunning = false;

        _udpClient?.Close();
        
        lock (_tcpLock)
        {
            _tcpStream?.Close();
            _tcpClient?.Close();
        }
        _tcpListener?.Stop();

        if (enableDebugLog)
            Debug.Log("[HEVCReorderer] 已停止");
    }

    #endregion
}
