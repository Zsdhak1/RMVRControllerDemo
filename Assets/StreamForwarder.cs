using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Collections.Generic;
using System.Collections.Concurrent;
using UnityEngine;

// 帧重组缓冲区结构
class FrameReassemblyBuffer
{
    public int totalExpectedBytes;
    public int currentReceivedBytes;
    public byte[] completeData;     // 预分配好的完整帧内存
    public float createTime;
}

public class StreamForwarder : MonoBehaviour
{
    [Header("Network Settings")]
    public int sourcePort = 3334;
    public int targetPort = 3335;
    
    // 【核心参数】最大允许缓冲多少帧（抗抖动能力）
    // 在弱网下，我们宁愿直接丢弃旧帧也不要花屏，所以保留 3-5 帧缓冲区即可
    [Range(1, 10)] public int maxReorderFrames = 5;

    public string listenAddress = "0.0.0.0"; 

    private UdpClient _udpClient;
    private TcpListener _tcpListener;
    private TcpClient _currentTcpClient;
    private NetworkStream _tcpStream;
    private Thread _tcpThread;

    public volatile bool isRunning = false;
    
    // 监控数据
    public long probeTotalBytesReceived = 0;
    public int probeTotalPacketsReceived = 0;
    public int currentRateKbps = 0;
    public int droppedFramesCount = 0; // 丢帧统计

    private long bytesInSecond = 0;
    private float lastLogTime;

    // --- 弱网对抗核心数据结构 ---
    // Key: FrameID (ushort), Value: Buffer
    private Dictionary<ushort, FrameReassemblyBuffer> _jitterBuffer;
    private ushort _latestFrameId = 0; // 记录收到的最新一帧的 ID
    private object _bufferLock = new object();

    void Start()
    {
        _jitterBuffer = new Dictionary<ushort, FrameReassemblyBuffer>();
        StartForwarding();
    }

    public void StartForwarding()
    {
        if (isRunning) return;
        isRunning = true;
        lastLogTime = Time.time;

        try
        {
            // UDP 接收端初始化
            _udpClient = new UdpClient();
            _udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, sourcePort));
            _udpClient.Client.ReceiveBufferSize = 1024 * 1024 * 8; // 8MB 超大内核缓冲

            _udpClient.BeginReceive(new AsyncCallback(UdpReceiveCallback), null);
            Debug.Log($"[图传内核] UDP {sourcePort} 抗弱网模式启动");
        }
        catch (Exception e)
        {
            Debug.LogError($"[图传内核] UDP 启动失败: {e.Message}");
        }

        // TCP 发送端（给 VLC）
        try
        {
            IPAddress ip = (listenAddress == "0.0.0.0" || listenAddress.ToLower() == "any") ? IPAddress.Any : IPAddress.Parse(listenAddress);
            _tcpListener = new TcpListener(ip, targetPort);
            _tcpListener.Start();
            
            _tcpThread = new Thread(TcpAcceptLoop) { IsBackground = true };
            _tcpThread.Start();
        }
        catch (Exception e)
        {
            Debug.LogError($"[图传内核] TCP 启动失败: {e.Message}");
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
                ProcessPacket(packetData);
            }

            if (isRunning)
            {
                _udpClient.BeginReceive(new AsyncCallback(UdpReceiveCallback), null);
            }
        }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            if (isRunning) Debug.LogWarning($"[UDP] {ex.Message}");
        }
    }

    // --- 核心：分片重组逻辑 ---
    private void ProcessPacket(byte[] data)
    {
        // 1. 解析 RMUC 协议头 (Big Endian)
        ushort frameId = (ushort)((data[0] << 8) | data[1]);
        ushort packetIdx = (ushort)((data[2] << 8) | data[3]);
        int totalFrameSize = (int)((data[4] << 24) | (data[5] << 16) | (data[6] << 8) | data[7]);

        int payloadLen = data.Length - 8;

        lock (_bufferLock)
        {
            // A. 脏帧清洗：如果是很老的 ID，直接丢弃
            int diff = frameId - _latestFrameId;
            if (diff < -30000) diff += 65536; 
            
            if (diff < -maxReorderFrames) return; 

            // 更新最新帧 ID
            if (diff > 0)
            {
                _latestFrameId = frameId;
                CleanupOldBuffers(_latestFrameId);
            }

            // B. 获取或创建缓冲区
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

            // C. 写入数据 payload
            // 假设你的服务端 MaxPacketSize=1400，即 payload=1392
            int offset = packetIdx * 1392; 
            
            if (offset + payloadLen <= buffer.totalExpectedBytes)
            {
                Buffer.BlockCopy(data, 8, buffer.completeData, offset, payloadLen);
                buffer.currentReceivedBytes += payloadLen;
            }

            // D. 完整性检查
            if (buffer.currentReceivedBytes >= buffer.totalExpectedBytes)
            {
                PushToVLC(buffer.completeData);
                _jitterBuffer.Remove(frameId);
                
                probeTotalBytesReceived += buffer.totalExpectedBytes;
                probeTotalPacketsReceived++; 
                bytesInSecond += buffer.totalExpectedBytes;
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

            if (diff > maxReorderFrames || (Time.realtimeSinceStartup - _jitterBuffer[key].createTime > 0.5f))
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

    private void PushToVLC(byte[] frameData)
    {
        if (_tcpStream != null)
        {
            try
            {
                _tcpStream.Write(frameData, 0, frameData.Length);
            }
            catch
            {
                CloseCurrentTcpClient();
            }
        }
    }

    private void TcpAcceptLoop()
    {
        while (isRunning)
        {
            try
            {
                if (!_tcpListener.Pending())
                {
                    Thread.Sleep(50);
                    continue;
                }
                CloseCurrentTcpClient();
                _currentTcpClient = _tcpListener.AcceptTcpClient();
                _currentTcpClient.NoDelay = true;
                _currentTcpClient.SendBufferSize = 1024 * 1024 * 2;
                _tcpStream = _currentTcpClient.GetStream();
                UnityMainThreadDispatcher.Instance().Enqueue(() => Debug.Log("<color=cyan>VLC Reconnected</color>"));
                while (isRunning && _currentTcpClient.Connected)
                {
                    if (_currentTcpClient.Client.Poll(0, SelectMode.SelectRead))
                    {
                        if (_currentTcpClient.Client.Receive(new byte[1], SocketFlags.Peek) == 0) break;
                    }
                    Thread.Sleep(100);
                }
                CloseCurrentTcpClient();
            }
            catch { }
        }
    }
    
    private void CloseCurrentTcpClient()
    {
        if (_tcpStream != null) { try { _tcpStream.Close(); } catch { } _tcpStream = null; }
        if (_currentTcpClient != null) { try { _currentTcpClient.Close(); } catch { } _currentTcpClient = null; }
    }
    
    void Update()
    {
        if (!isRunning) return;
        if (Time.time - lastLogTime >= 1.0f)
        {
            currentRateKbps = (int)((bytesInSecond * 8) / 1000);
            if (currentRateKbps > 0 || droppedFramesCount > 0)
            {
                UnityMainThreadDispatcher.Instance().Enqueue(() => {
                     Debug.Log($"[Stream] Bitrate: {currentRateKbps}kbps | Dropped Bad Frames: {droppedFramesCount}");
                });
            }
            bytesInSecond = 0;
            droppedFramesCount = 0;
            lastLogTime = Time.time;
        }
    }
    
    public void StopForwarding()
    {
        isRunning = false;

        if (_udpClient != null) 
        { 
            try { _udpClient.Close(); } catch { } 
            _udpClient = null; 
        }
        
        CloseCurrentTcpClient();
        
        if (_tcpListener != null) 
        { 
            try { _tcpListener.Stop(); } catch { } 
            _tcpListener = null; 
        }
    }

    void OnDestroy()
    {
        StopForwarding();
    }

    void OnApplicationQuit()
    {
        StopForwarding();
    }
}

public class UnityMainThreadDispatcher : MonoBehaviour
{
    private static UnityMainThreadDispatcher _instance;
    private readonly ConcurrentQueue<Action> _executionQueue = new ConcurrentQueue<Action>();

    public static UnityMainThreadDispatcher Instance()
    {
        if (!_instance)
        {
            GameObject go = GameObject.Find("UnityMainThreadDispatcher");
            if (!go)
            {
                go = new GameObject("UnityMainThreadDispatcher");
                _instance = go.AddComponent<UnityMainThreadDispatcher>();
                DontDestroyOnLoad(go);
            }
            else
            {
                _instance = go.GetComponent<UnityMainThreadDispatcher>();
            }
        }
        return _instance;
    }

    public void Enqueue(Action action)
    {
        _executionQueue.Enqueue(action);
    }

    void Update()
    {
        while (_executionQueue.TryDequeue(out var action))
        {
            action.Invoke();
        }
    }
}