using UnityEngine;
using System;
using System.Net;
using System.Net.Sockets;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;

/// <summary>
/// HEVC 视频流 UDP 接收器 - 无限制直通模式
/// 尽可能快地将 UDP 数据转发到 TCP，不人为限制速率
/// </summary>
public class FreeRMVideoPlayerTCP : MonoBehaviour
{
    [Header("=== Network Config ===")]
    public int udpPort = 3334;
    public int tcpPort = 3335;
    public int tcpSendBufferSize = 1024 * 1024 * 8;
    
    [Header("=== Buffer (防内存溢出) ===")]
    [Tooltip("最大缓冲包数，超过将丢弃最旧数据")]
    public int maxBufferSize = 500;
    
    [Header("=== Debug ===")]
    public bool enableDebugLog = true;
    public bool enableFileLogging = true;

    private UdpClient _udpClient;
    private TcpListener _tcpListener;
    private TcpClient _tcpClient;
    private NetworkStream _tcpStream;
    private readonly object _tcpLock = new object();
    private bool _isRunning = false;
    private bool _tcpConnected = false;
    
    // 数据队列
    private Queue<byte[]> _sendQueue = new Queue<byte[]>();
    private readonly object _queueLock = new object();
    
    // Stats
    private long _totalUdpPackets = 0;
    private long _totalTcpPackets = 0;
    private long _totalBytesSent = 0;
    private long _droppedPackets = 0;
    private float _lastLogTime;
    private int _queueSizeAtLastLog = 0;
    
    // Logging
    private StreamWriter _logWriter;
    private readonly object _logLock = new object();

    void Awake()
    {
        InitLogging();
    }
    
    void Start()
    {
        Log("[HEVC] Starting (Unlimited Mode)...");
        _isRunning = true;
        
        StartTCPServer();
        StartUDPReceiver();
    }

    void Update()
    {
        // 检查TCP状态
        lock (_tcpLock)
        {
            _tcpConnected = _tcpStream != null && _tcpStream.CanWrite;
        }
        
        // 发送所有 queued 数据
        SendAllQueuedData();
        
        // 统计
        if (Time.time - _lastLogTime >= 3.0f)
        {
            _lastLogTime = Time.time;
            PrintStats();
        }
    }
    
    private void InitLogging()
    {
        if (!enableFileLogging) return;
        try
        {
            string logDir = Path.Combine(Application.persistentDataPath, "VideoPlayerLogs");
            Directory.CreateDirectory(logDir);
            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string logFilePath = Path.Combine(logDir, $"HEVC_{timestamp}.log");
            _logWriter = new StreamWriter(logFilePath, false, Encoding.UTF8);
            _logWriter.AutoFlush = true;
            Log($"[HEVC] Log: {logFilePath}");
        }
        catch { enableFileLogging = false; }
    }
    
    private void Log(string message)
    {
        string timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
        string logLine = $"[{timestamp}] {message}";
        if (enableDebugLog) Debug.Log(logLine);
        if (enableFileLogging && _logWriter != null)
        {
            lock (_logLock) { try { _logWriter.WriteLine(logLine); } catch { } }
        }
    }

    #region TCP

    private void StartTCPServer()
    {
        try
        {
            _tcpListener = new TcpListener(IPAddress.Any, tcpPort);
            _tcpListener.Start();
            Log($"[TCP] Server on port {tcpPort}");
            _ = AcceptLoop();
        }
        catch (Exception ex) { Log($"[TCP] Failed: {ex.Message}"); }
    }

    private async Task AcceptLoop()
    {
        while (_isRunning)
        {
            try
            {
                TcpClient client = await _tcpListener.AcceptTcpClientAsync();
                lock (_tcpLock)
                {
                    if (_tcpClient != null) try { _tcpClient.Close(); } catch { }
                    _tcpClient = client;
                    _tcpClient.SendBufferSize = tcpSendBufferSize;
                    _tcpClient.NoDelay = true; // 禁用Nagle，降低延迟
                    _tcpStream = _tcpClient.GetStream();
                    _tcpConnected = true;
                }
                Log("[TCP] Client connected");
            }
            catch { await Task.Delay(1000); }
        }
    }

    #endregion

    #region UDP

    private void StartUDPReceiver()
    {
        try
        {
            _udpClient = new UdpClient();
            _udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, udpPort));
            _udpClient.Client.ReceiveBufferSize = 1024 * 1024 * 16;
            _udpClient.BeginReceive(UDPCallback, null);
            Log($"[UDP] Receiver on port {udpPort}");
        }
        catch (Exception e) { Log($"[UDP] Failed: {e.Message}"); }
    }

    private void UDPCallback(IAsyncResult res)
    {
        if (!_isRunning || _udpClient == null) return;
        
        byte[] data = null;
        try
        {
            IPEndPoint ep = new IPEndPoint(IPAddress.Any, 0);
            data = _udpClient.EndReceive(res, ref ep);
        }
        catch { }
        
        try { if (_isRunning && _udpClient != null) _udpClient.BeginReceive(new AsyncCallback(UDPCallback), null); }
        catch { }
        
        if (data != null && data.Length > 8)
        {
            _totalUdpPackets++;
            
            // 去掉8字节头
            int len = data.Length - 8;
            byte[] payload = new byte[len];
            Buffer.BlockCopy(data, 8, payload, 0, len);
            
            lock (_queueLock)
            {
                // 如果队列满，丢弃最旧的（保持最新数据）
                if (_sendQueue.Count >= maxBufferSize)
                {
                    // 只丢弃一半，避免频繁丢弃
                    int toDrop = maxBufferSize / 2;
                    for (int i = 0; i < toDrop; i++)
                    {
                        if (_sendQueue.Count > 0)
                        {
                            _sendQueue.Dequeue();
                            _droppedPackets++;
                        }
                    }
                    Log($"[Buffer] Dropped {toDrop} old packets, queue was full");
                }
                _sendQueue.Enqueue(payload);
            }
        }
    }

    #endregion

    #region Send

    private void SendAllQueuedData()
    {
        if (!_tcpConnected)
        {
            // TCP断开时清空队列
            lock (_queueLock)
            {
                while (_sendQueue.Count > 0)
                {
                    _sendQueue.Dequeue();
                    _droppedPackets++;
                }
            }
            return;
        }

        // 一次性发送所有 queued 数据，不限制数量
        int sentThisFrame = 0;
        
        lock (_queueLock)
        {
            lock (_tcpLock)
            {
                if (_tcpStream == null || !_tcpStream.CanWrite)
                {
                    _tcpConnected = false;
                    return;
                }
                
                // 发送队列中所有数据
                while (_sendQueue.Count > 0)
                {
                    byte[] data = _sendQueue.Dequeue();
                    try
                    {
                        _tcpStream.Write(data, 0, data.Length);
                        _totalBytesSent += data.Length;
                        _totalTcpPackets++;
                        sentThisFrame++;
                    }
                    catch (Exception ex)
                    {
                        Log($"[Send Error] {ex.Message}");
                        _tcpConnected = false;
                        _tcpStream = null;
                        _tcpClient = null;
                        break;
                    }
                }
            }
        }
        
        // 如果发送了数据，记录一下
        if (sentThisFrame > 0 && sentThisFrame > 100)
        {
            Log($"[Send] Sent {sentThisFrame} packets this frame");
        }
    }

    #endregion

    private void PrintStats()
    {
        int queueSize = 0;
        lock (_queueLock) { queueSize = _sendQueue.Count; }
        
        float dropRate = _totalUdpPackets > 0 ? (float)_droppedPackets / _totalUdpPackets * 100 : 0;
        
        Log($"[Stats] TCP:{(_tcpConnected ? "OK" : "Wait")} | " +
            $"UDP:{_totalUdpPackets} | TCP:{_totalTcpPackets} | " +
            $"MB:{_totalBytesSent / 1024.0 / 1024.0:F1} | " +
            $"Drop:{_droppedPackets}({dropRate:F1}%) | Queue:{queueSize}");
    }

    void OnDestroy()
    {
        _isRunning = false;
        _udpClient?.Close();
        lock (_tcpLock) { _tcpStream?.Close(); _tcpClient?.Close(); }
        _tcpListener?.Stop();
        if (_logWriter != null) { lock (_logLock) { try { _logWriter.Close(); } catch { } } }
    }
}
