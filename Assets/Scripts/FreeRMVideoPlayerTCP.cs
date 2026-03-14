using UnityEngine;
using System;
using System.Net;
using System.Net.Sockets;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Linq;

/// <summary>
/// HEVC 视频流 UDP 接收器 - 支持帧重排和原版直通两种模式
/// 协议格式（大端序）：帧编号(2B) + 包序号(2B) + 帧总大小(4B) + HEVC数据
/// </summary>
public class FreeRMVideoPlayerTCP : MonoBehaviour
{
    [Header("=== Network Config ===")]
    public int udpPort = 3334;
    public int tcpPort = 3335;
    public int tcpSendBufferSize = 1024 * 1024 * 8;
    
    [Header("=== Mode ===")]
    [Tooltip("启用帧重排模式（解决乱序花屏）。关闭则使用原版直通模式。")]
    public bool enableFrameReordering = true;
    
    [Header("=== Buffer ===")]
    public int maxBufferSize = 500;
    [Tooltip("帧重排模式：最大缓存帧数")]
    public int maxFrameBufferSize = 16;
    [Tooltip("帧重排模式：帧超时时间(秒)")]
    public float frameTimeoutSeconds = 0.1f;
    [Tooltip("帧重排模式：包到达间隔阈值(秒)")]
    public float packetArrivalInterval = 0.02f;
    
    [Header("=== Debug ===")]
    public bool enableDebugLog = true;
    public bool enableFileLogging = true;

    // === Network ===
    private UdpClient _udpClient;
    private TcpListener _tcpListener;
    private TcpClient _tcpClient;
    private NetworkStream _tcpStream;
    private readonly object _tcpLock = new object();
    private bool _isRunning = false;
    private bool _tcpConnected = false;
    
    // === Queues ===
    private Queue<byte[]> _sendQueue = new Queue<byte[]>();
    private readonly object _queueLock = new object();
    
    // === Frame Reordering ===
    private class FramePacket
    {
        public ushort Sequence;
        public byte[] Data;
    }
    
    private class FrameBuffer
    {
        public ushort FrameNumber;
        public uint TotalSize;
        public Dictionary<ushort, FramePacket> Packets;
        public float FirstReceiveTime;
        public float LastReceiveTime;
        public int ReceivedSize;
        public ushort HighestSeq;
    }
    
    private Dictionary<ushort, FrameBuffer> _frameBuffers = new Dictionary<ushort, FrameBuffer>();
    private readonly object _frameBufferLock = new object();
    private ushort _lastSentFrameNumber = 0;
    private bool _hasReceivedFirstFrame = false;
    
    // === Stats ===
    private long _totalUdpPackets = 0;
    private long _totalTcpPackets = 0;
    private long _totalBytesSent = 0;
    private long _droppedPackets = 0;
    private long _reassembledFrames = 0;
    private float _lastLogTime;
    
    // === Time ===
    private float _startupTime = 0f;
    private long _bytesInLastSecond = 0;
    private float _rateUpdateTimer = 0f;
    
    // === Logging ===
    private StreamWriter _logWriter;
    private readonly object _logLock = new object();

    // === Public Properties ===
    public bool isRunning => _isRunning;
    public long probeTotalPacketsReceived => _totalUdpPackets;
    public int currentRateKbps { get; private set; }
    public bool isTcpConnected => _tcpConnected;
    public long totalBytesSent => _totalBytesSent;
    public long droppedPackets => _droppedPackets;
    public long reassembledFrames => _reassembledFrames;
    public int queueSize { get { lock (_queueLock) { return _sendQueue.Count; } } }
    public int bufferedFrameCount { get { lock (_frameBufferLock) { return _frameBuffers.Count; } } }

    void Awake()
    {
        InitLogging();
    }
    
    void Start()
    {
        _startupTime = Time.time;
        _isRunning = true;
        
        string mode = enableFrameReordering ? "Frame Reordering" : "Direct Pass-through";
        Log($"[HEVC] Starting - {mode} Mode");
        
        StartTCPServer();
        StartUDPReceiver();
    }

    void Update()
    {
        lock (_tcpLock) { _tcpConnected = _tcpStream != null && _tcpStream.CanWrite; }
        
        if (enableFrameReordering)
        {
            ProcessFrames();
        }
        
        SendQueuedData();
        
        _rateUpdateTimer += Time.deltaTime;
        if (_rateUpdateTimer >= 1.0f)
        {
            currentRateKbps = (int)(_bytesInLastSecond * 8 / 1024);
            _bytesInLastSecond = 0;
            _rateUpdateTimer = 0f;
        }
        
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
            Log($"[Log] {logFilePath}");
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

    // === Byte Order Helpers ===
    private ushort ReadUInt16BE(byte[] data, int offset)
    {
        return (ushort)((data[offset] << 8) | data[offset + 1]);
    }
    
    private uint ReadUInt32BE(byte[] data, int offset)
    {
        return ((uint)data[offset] << 24) | ((uint)data[offset + 1] << 16) | 
               ((uint)data[offset + 2] << 8) | data[offset + 3];
    }

    // === TCP ===
    private void StartTCPServer()
    {
        try
        {
            _tcpListener = new TcpListener(IPAddress.Any, tcpPort);
            _tcpListener.Start();
            Log($"[TCP] Port {tcpPort}");
            _ = AcceptLoop();
        }
        catch (Exception ex) { Log($"[TCP Error] {ex.Message}"); }
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
                    _tcpClient.NoDelay = true;
                    _tcpStream = _tcpClient.GetStream();
                    _tcpConnected = true;
                }
                Log("[TCP] Connected");
            }
            catch { await Task.Delay(1000); }
        }
    }

    // === UDP ===
    private void StartUDPReceiver()
    {
        try
        {
            _udpClient = new UdpClient();
            _udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, udpPort));
            _udpClient.Client.ReceiveBufferSize = 1024 * 1024 * 16;
            _udpClient.BeginReceive(UDPCallback, null);
            Log($"[UDP] Port {udpPort}");
        }
        catch (Exception e) { Log($"[UDP Error] {e.Message}"); }
    }

    private void UDPCallback(IAsyncResult res)
    {
        if (!_isRunning || _udpClient == null) return;
        
        byte[] data = null;
        IPEndPoint ep = null;
        try
        {
            ep = new IPEndPoint(IPAddress.Any, 0);
            data = _udpClient.EndReceive(res, ref ep);
        }
        catch { }
        
        try { if (_isRunning && _udpClient != null) _udpClient.BeginReceive(new AsyncCallback(UDPCallback), null); }
        catch { }
        
        if (data == null || data.Length <= 8) return;
        
        _totalUdpPackets++;
        _bytesInLastSecond += data.Length;
        
        // 解析头部
        ushort frameNumber = ReadUInt16BE(data, 0);
        ushort packetSeq = ReadUInt16BE(data, 2);
        uint frameTotalSize = ReadUInt32BE(data, 4);
        
        int payloadLen = data.Length - 8;
        
        if (enableFrameReordering)
        {
            // 帧重排模式：提取payload，按帧重组
            byte[] payload = new byte[payloadLen];
            Buffer.BlockCopy(data, 8, payload, 0, payloadLen);
            AddToFrameBuffer(frameNumber, packetSeq, frameTotalSize, payload, payloadLen);
        }
        else
        {
            // 原版直通模式：直接去掉8字节头，加入队列
            byte[] payload = new byte[payloadLen];
            Buffer.BlockCopy(data, 8, payload, 0, payloadLen);
            
            lock (_queueLock)
            {
                if (_sendQueue.Count >= maxBufferSize)
                {
                    // 丢弃一半
                    int toDrop = maxBufferSize / 2;
                    for (int i = 0; i < toDrop; i++)
                    {
                        if (_sendQueue.Count > 0)
                        {
                            _sendQueue.Dequeue();
                            _droppedPackets++;
                        }
                    }
                    if (enableDebugLog)
                        Log($"[Buffer] Dropped {toDrop} old packets");
                }
                _sendQueue.Enqueue(payload);
            }
        }
        
        // 每100个包打印一次
        if (_totalUdpPackets % 100 == 0 && enableDebugLog)
        {
            Log($"[UDP] Packet #{_totalUdpPackets}: Frame={frameNumber}, Seq={packetSeq}, TotalSize={frameTotalSize}, Payload={payloadLen}");
        }
    }
    
    // === Frame Reordering ===
    private void AddToFrameBuffer(ushort frameNumber, ushort packetSeq, uint totalSize, byte[] data, int dataLen)
    {
        lock (_frameBufferLock)
        {
            float currentTime = Time.time - _startupTime;
            
            if (!_hasReceivedFirstFrame)
            {
                _hasReceivedFirstFrame = true;
                _lastSentFrameNumber = (ushort)((frameNumber - 1) & 0xFFFF);
                Log($"[First] Frame #{frameNumber}");
            }
            
            if (!_frameBuffers.TryGetValue(frameNumber, out FrameBuffer frame))
            {
                // 缓冲区满，发送最旧的
                if (_frameBuffers.Count >= maxFrameBufferSize)
                {
                    var oldest = _frameBuffers.OrderBy(f => f.Key).FirstOrDefault();
                    if (oldest.Value != null)
                    {
                        TrySendFrame(oldest.Value);
                        _frameBuffers.Remove(oldest.Key);
                    }
                }
                
                frame = new FrameBuffer
                {
                    FrameNumber = frameNumber,
                    TotalSize = totalSize,
                    Packets = new Dictionary<ushort, FramePacket>(),
                    FirstReceiveTime = currentTime,
                    LastReceiveTime = currentTime,
                    ReceivedSize = 0,
                    HighestSeq = 0
                };
                _frameBuffers.Add(frameNumber, frame);
            }
            
            if (!frame.Packets.ContainsKey(packetSeq))
            {
                frame.Packets[packetSeq] = new FramePacket
                {
                    Sequence = packetSeq,
                    Data = data
                };
                frame.ReceivedSize += dataLen;
                frame.LastReceiveTime = currentTime;
                if (packetSeq > frame.HighestSeq) frame.HighestSeq = packetSeq;
            }
        }
    }

    private void ProcessFrames()
    {
        float now = Time.time - _startupTime;
        
        lock (_frameBufferLock)
        {
            if (_frameBuffers.Count == 0) return;
            
            var toRemove = new List<ushort>();
            var sortedFrames = _frameBuffers.OrderBy(f => (f.Key - _lastSentFrameNumber) & 0xFFFF).ToList();
            
            foreach (var kvp in sortedFrames)
            {
                ushort frameNum = kvp.Key;
                FrameBuffer frame = kvp.Value;
                
                float age = now - frame.FirstReceiveTime;
                float idle = now - frame.LastReceiveTime;
                
                bool send = false;
                
                if (frame.TotalSize > 0 && frame.ReceivedSize >= frame.TotalSize)
                    send = true;
                else if (idle > packetArrivalInterval && frame.Packets.Count > 0)
                    send = true;
                else if (age > frameTimeoutSeconds)
                    send = true;
                
                if (send)
                {
                    TrySendFrame(frame);
                    toRemove.Add(frameNum);
                    _lastSentFrameNumber = frameNum;
                }
            }
            
            foreach (var num in toRemove)
            {
                _frameBuffers.Remove(num);
            }
        }
    }
    
    private void TrySendFrame(FrameBuffer frame)
    {
        if (frame.Packets.Count == 0) return;
        
        var sorted = frame.Packets.OrderBy(p => p.Key).ToList();
        int totalSize = sorted.Sum(p => p.Value.Data.Length);
        
        byte[] frameData = new byte[totalSize];
        int offset = 0;
        foreach (var kvp in sorted)
        {
            var pkt = kvp.Value;
            Buffer.BlockCopy(pkt.Data, 0, frameData, offset, pkt.Data.Length);
            offset += pkt.Data.Length;
        }
        
        lock (_queueLock)
        {
            if (_sendQueue.Count >= maxBufferSize)
            {
                int drop = maxBufferSize / 2;
                for (int i = 0; i < drop; i++)
                {
                    if (_sendQueue.Count > 0) 
                    {
                        _sendQueue.Dequeue();
                        _droppedPackets++;
                    }
                }
            }
            _sendQueue.Enqueue(frameData);
            _reassembledFrames++;
        }
        
        if (enableDebugLog && (frame.FrameNumber % 60 == 0 || frame.FrameNumber < 5))
        {
            float pct = frame.TotalSize > 0 ? (frame.ReceivedSize * 100f / frame.TotalSize) : 100f;
            Log($"[Frame] #{frame.FrameNumber}: {sorted.Count} pkts, {totalSize} bytes, {pct:F0}%");
        }
    }

    // === Send ===
    private void SendQueuedData()
    {
        if (!_tcpConnected)
        {
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

        lock (_queueLock)
        {
            lock (_tcpLock)
            {
                if (_tcpStream == null || !_tcpStream.CanWrite)
                {
                    _tcpConnected = false;
                    return;
                }
                
                while (_sendQueue.Count > 0)
                {
                    byte[] data = _sendQueue.Dequeue();
                    try
                    {
                        _tcpStream.Write(data, 0, data.Length);
                        _totalBytesSent += data.Length;
                        _bytesInLastSecond += data.Length;
                        _totalTcpPackets++;
                    }
                    catch (Exception ex)
                    {
                        Log($"[TCP Error] {ex.Message}");
                        _tcpConnected = false;
                        _tcpStream = null;
                        _tcpClient = null;
                        break;
                    }
                }
            }
        }
    }

    // === Stats ===
    private void PrintStats()
    {
        int queueSize, frameBufCount;
        lock (_queueLock) { queueSize = _sendQueue.Count; }
        lock (_frameBufferLock) { frameBufCount = _frameBuffers.Count; }
        
        float dropRate = _totalUdpPackets > 0 ? (float)_droppedPackets / _totalUdpPackets * 100 : 0;
        string mode = enableFrameReordering ? "REORDER" : "DIRECT";
        
        Log($"[Stats] {mode} | TCP:{(_tcpConnected ? "OK" : "Wait")} | UDP:{_totalUdpPackets} | " +
            $"Frames:{_reassembledFrames} | MB:{_totalBytesSent / 1024.0 / 1024.0:F1} | " +
            $"Drop:{_droppedPackets}({dropRate:F1}%) | Queue:{queueSize} | FBuf:{frameBufCount}");
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
