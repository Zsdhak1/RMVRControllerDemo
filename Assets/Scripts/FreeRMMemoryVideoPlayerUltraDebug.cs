using UnityEngine;
using LibVLCSharp.Shared;
using System;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;

class FrameReassemblyBufferDebug
{
    public int totalExpectedBytes;
    public int currentReceivedBytes;
    public byte[] completeData;
    public float createTime;
    public HashSet<int> receivedPacketIndices = new HashSet<int>();
    public bool hasPktZero = false;  // FIX: 标记是否收到 PktIdx=0
    public int lastPacketIdx = -1;   // 收到的最大 packetIdx
    public int expectedPacketCount = 0; // 期望的包数量
}

public class FreeRMMemoryVideoPlayerUltraDebug : MonoBehaviour
{
    [Header("=== Video Stream UDP Config ===")]
    public int sourcePort = 3334;
    [Range(1, 10)] public int maxReorderFrames = 3; 

    [Header("=== Video Render Target ===")]
    public Renderer targetScreen;
    public UnityEngine.UI.RawImage targetRawImage;

    [Header("=== Fix Options ===")]
    [Tooltip("Force software decoding")]
    public bool forceSoftwareDecoding = true;
    [Tooltip("Auto inject NAL start code")]
    public bool autoInjectNALStartCode = true;
    [Tooltip("Enable ultra verbose logging")]
    public bool ultraVerbose = true;
    
    public byte[] nalStartCode = new byte[] { 0x00, 0x00, 0x00, 0x01 };

    private LibVLC _libVLC;
    private MediaPlayer _mediaPlayer;
    private Texture2D _vlcTexture;
    private Media _memoryMedia;
    private StreamMediaInput _mediaInput;
    private PushStreamAdapterUltraDebug _streamAdapter;
    
    private IntPtr _vlcPixelBuffer = IntPtr.Zero;
    private uint _videoWidth = 0;
    private uint _videoHeight = 0;

    private UdpClient _udpClient;
    public volatile bool isRunning = false;

    private ConcurrentQueue<byte[]> _cleanH265StreamQueue = new ConcurrentQueue<byte[]>();

    private Dictionary<ushort, FrameReassemblyBufferDebug> _jitterBuffer;
    private ushort _latestFrameId = 0;
    private object _bufferLock = new object();

    // Stats
    public int packetsReceived = 0;
    public int packetsTooSmall = 0;
    public int packetsProcessed = 0;
    public int packetsError = 0;
    public long bytesReceived = 0;
    public int framesCompleted = 0;
    public int framesDropped = 0;
    public int nalInjected = 0;
    public int idrFramesReceived = 0;
    public int vlcRestartCount = 0;
    public int lastFrameId = -1;
    public string lastPacketInfo = "";
    public int currentBufferCount = 0;

    private long _bytesInSecond = 0;
    private float _lastLogTime;
    private float _startTime;

    // FIX: Pre-buffer mechanism - wait for IDR frame before starting VLC
    private bool _hasReceivedIDR = false;
    private float _preBufferStartTime;
    private List<byte[]> _preBuffer = new List<byte[]>();  // Cache frames before IDR
    private const float MAX_PREBUFFER_TIME = 2.0f;  // Max 2 seconds pre-buffer
    private const int MAX_PREBUFFER_FRAMES = 60;    // Max 60 frames pre-buffer
    
    // FIX: VPS/SPS/PPS cache for injection
    private byte[] _cachedVPS = null;
    private byte[] _cachedSPS = null;
    private byte[] _cachedPPS = null;

    [Header("=== File Logging ===")]
    [Tooltip("Enable file logging")]
    public bool enableFileLogging = true;
    [Tooltip("Log file path (relative to persistentDataPath, or use full path)")]
    public string logFilePath = "VideoPlayerDebug.log";
    
    private StreamWriter _logWriter;
    private object _logLock = new object();
    private string _fullLogPath;

    void Awake()
    {
        Core.Initialize(Application.dataPath);
        InitFileLogging();
    }

    private void InitFileLogging()
    {
        if (!enableFileLogging) return;

        try
        {
            // Use full path if provided, otherwise use persistentDataPath
            if (Path.IsPathRooted(logFilePath))
            {
                _fullLogPath = logFilePath;
            }
            else
            {
                string logDir = Path.Combine(Application.persistentDataPath, "VideoPlayerLogs");
                Directory.CreateDirectory(logDir);
                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                _fullLogPath = Path.Combine(logDir, $"VideoPlayerDebug_{timestamp}.log");
            }

            _logWriter = new StreamWriter(_fullLogPath, false, Encoding.UTF8);
            _logWriter.AutoFlush = true;

            string header = $"=== VideoPlayer UltraDebug Log ===\n" +
                           $"Start Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n" +
                           $"Log File: {_fullLogPath}\n" +
                           $"Unity Version: {Application.unityVersion}\n" +
                           $"Platform: {Application.platform}\n" +
                           $"====================================\n";
            _logWriter.WriteLine(header);

            Log($"[UltraDebug] File logging initialized: {_fullLogPath}");
        }
        catch (Exception ex)
        {
            Log($"[UltraDebug] Failed to initialize file logging: {ex.Message}", LogType.Error);
            _logWriter = null;
            enableFileLogging = false;
        }
    }

    private void Log(string message, LogType logType = LogType.Log)
    {
        string timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
        string logLine = $"[{timestamp}] {message}";

        // Always log to Unity Console
        switch (logType)
        {
            case LogType.Error:
                Debug.LogError(message);
                break;
            case LogType.Warning:
                Debug.LogWarning(message);
                break;
            default:
                Debug.Log(message);
                break;
        }

        // Also log to file if enabled
        if (enableFileLogging && _logWriter != null)
        {
            lock (_logLock)
            {
                try
                {
                    string prefix = logType switch
                    {
                        LogType.Error => "[ERROR] ",
                        LogType.Warning => "[WARN] ",
                        _ => "[INFO] "
                    };
                    _logWriter.WriteLine(prefix + logLine);
                }
                catch (Exception ex)
                {
                    // Don't recursively log file errors
                    Debug.LogError($"[UltraDebug] File write failed: {ex.Message}");
                }
            }
        }
    }

    void Start()
    {
        _jitterBuffer = new Dictionary<ushort, FrameReassemblyBufferDebug>();
        _startTime = Time.time;
        _preBufferStartTime = Time.time;
        
        Log("[UltraDebug] ===== STARTING ULTRA DEBUG VERSION =====");
        Log($"[UltraDebug] Target port: {sourcePort}");
        Log($"[UltraDebug] Time: {DateTime.Now:HH:mm:ss.fff}");
        Log($"[UltraDebug] Log file: {_fullLogPath}");
        
        // FIX: Start UDP first, delay VLC until we get IDR frame
        StartUdpReceiver();
        Log("[UltraDebug] Pre-buffering: Waiting for IDR frame or timeout...");
        // VLC will be started in Update() when IDR is detected or timeout
    }

    private void InitVLCEngine()
    {
        var options = new List<string>
        {
            "--network-caching=1000",  // FIX: Increased from 60 to 1000ms (matches VLC player)
            "--drop-late-frames",
            "--skip-frames",
            "--no-audio",
            "--avcodec-threads=4"  // Enable multi-threading for software decode
        };

        if (forceSoftwareDecoding)
            options.Add("--avcodec-hw=none");
        else
            options.Add("--avcodec-hw=any");

        _libVLC = new LibVLC(options.ToArray());
        CreateMediaPlayerAndPlay();
        
        Log("[UltraDebug] VLC engine initialized with network-caching=1000ms");
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
            Log("[UltraDebug] VLC ERROR event triggered!", LogType.Error);
        };
        
        // Add log callback
        _libVLC.Log += (sender, e) => 
        {
            Debug.Log($"[VLC Log] {e.Message}");
        };

        while (_cleanH265StreamQueue.TryDequeue(out _)) { }

        _streamAdapter = new PushStreamAdapterUltraDebug(_cleanH265StreamQueue, Log);
        _mediaInput = new StreamMediaInput(_streamAdapter);
        _memoryMedia = new Media(_libVLC, _mediaInput);
        // FIX: Try different options for HEVC raw stream
        _memoryMedia.AddOption(":demux=hevc");
        _memoryMedia.AddOption(":codec=hevc");
        _memoryMedia.AddOption(":fps=30");  // Assume 30fps if not specified

        _mediaPlayer.Play(_memoryMedia);
        
        Log("[UltraDebug] MediaPlayer started, waiting for format callback...");
        Log("[UltraDebug] If no 'Video format callback' appears, VLC cannot parse the stream");
    }

    private void StartUdpReceiver()
    {
        if (isRunning)
        {
            Log("[UltraDebug] UDP already running!", LogType.Warning);
            return;
        }
        
        try
        {
            _udpClient = new UdpClient();
            _udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, sourcePort));
            _udpClient.Client.ReceiveBufferSize = 1024 * 1024 * 8; 

            isRunning = true;
            _lastLogTime = Time.time;
            _udpClient.BeginReceive(new AsyncCallback(UdpReceiveCallback), null);
            
            Log($"[UltraDebug] UDP receiver STARTED on port {sourcePort}");
            Log($"[UltraDebug] Waiting for data...");
        }
        catch (Exception e)
        {
            Log($"[UltraDebug] UDP FAILED: {e.Message}", LogType.Error);
            Log($"[UltraDebug] Stack: {e.StackTrace}", LogType.Error);
        }
    }

    private void UdpReceiveCallback(IAsyncResult res)
    {
        if (!isRunning)
        {
            Log("[UltraDebug] Callback called but isRunning=false", LogType.Warning);
            return;
        }
        
        if (_udpClient == null)
        {
            Log("[UltraDebug] Callback called but _udpClient=null", LogType.Warning);
            return;
        }
        
        byte[] packetData = null;
        string errorMsg = "";
        
        // Step 1: EndReceive
        try
        {
            IPEndPoint remoteEP = new IPEndPoint(IPAddress.Any, 0);
            packetData = _udpClient.EndReceive(res, ref remoteEP);
            
            if (ultraVerbose && packetsReceived < 10)
            {
                Log($"[UltraDebug] Raw packet received: {packetData?.Length ?? 0} bytes from {remoteEP}");
            }
        }
        catch (ObjectDisposedException)
        {
            Log("[UltraDebug] EndReceive: ObjectDisposed");
            return; // Don't restart
        }
        catch (Exception ex)
        {
            errorMsg = $"EndReceive: {ex.GetType().Name}: {ex.Message}";
            Log($"[UltraDebug] {errorMsg}", LogType.Error);
        }
        
        // Step 2: ALWAYS restart receiving
        try
        {
            if (isRunning && _udpClient != null && _udpClient.Client != null)
            {
                _udpClient.BeginReceive(new AsyncCallback(UdpReceiveCallback), null);
            }
            else
            {
                Log("[UltraDebug] Cannot restart receive - client not ready", LogType.Warning);
            }
        }
        catch (Exception ex)
        {
            Log($"[UltraDebug] BeginReceive FAILED: {ex.Message}", LogType.Error);
            isRunning = false;
        }
        
        // Step 3: Process packet
        if (packetData != null)
        {
            packetsReceived++;
            bytesReceived += packetData.Length;
            _bytesInSecond += packetData.Length;
            
            // Log first few packets in detail
            if (packetsReceived <= 5)
            {
                string hexHeader = "";
                for (int i = 0; i < Math.Min(16, packetData.Length); i++)
                    hexHeader += $"{packetData[i]:X2} ";
                
                Log($"[UltraDebug] Packet #{packetsReceived}: {packetData.Length} bytes");
                Log($"[UltraDebug] First 16 bytes: {hexHeader}");
            }
            
            // Check minimum size
            if (packetData.Length <= 8)
            {
                packetsTooSmall++;
                if (packetsReceived <= 10)
                    Log($"[UltraDebug] Packet too small: {packetData.Length} bytes (need >8)", LogType.Warning);
                return;
            }
            
            // Parse header
            try
            {
                ushort frameId = (ushort)((packetData[0] << 8) | packetData[1]);
                ushort packetIdx = (ushort)((packetData[2] << 8) | packetData[3]);
                int totalFrameSize = (int)((packetData[4] << 24) | (packetData[5] << 16) | (packetData[6] << 8) | packetData[7]);
                int payloadLen = packetData.Length - 8;
                
                lastPacketInfo = $"Frame{frameId},Pkt{packetIdx},Size{totalFrameSize},Pay{payloadLen}";
                
                if (packetsReceived <= 20 || packetsReceived % 100 == 0)
                {
                    Log($"[UltraDebug] Parsed: Frame={frameId}, PktIdx={packetIdx}, TotalSize={totalFrameSize}, Payload={payloadLen}");
                }
                
                // Sanity check
                if (totalFrameSize <= 0 || totalFrameSize > 50 * 1024 * 1024) // Max 50MB
                {
                    Log($"[UltraDebug] INVALID frame size: {totalFrameSize} (packet #{packetsReceived})", LogType.Error);
                    return;
                }
                
                ProcessPacket(packetData, frameId, packetIdx, totalFrameSize, payloadLen);
                packetsProcessed++;
            }
            catch (Exception ex)
            {
                packetsError++;
                Log($"[UltraDebug] Packet parsing ERROR: {ex.Message}", LogType.Error);
            }
        }
        else if (!string.IsNullOrEmpty(errorMsg))
        {
            Log($"[UltraDebug] Packet was null due to: {errorMsg}", LogType.Error);
        }
    }

    private void ProcessPacket(byte[] data, ushort frameId, ushort packetIdx, int totalFrameSize, int payloadLen)
    {
        lock (_bufferLock)
        {
            try
            {
                currentBufferCount = _jitterBuffer.Count;
                
                int diff = frameId - _latestFrameId;
                if (diff < -30000) diff += 65536;
                
                if (diff < -maxReorderFrames)
                {
                    if (packetsReceived <= 20)
                        Log($"[UltraDebug] Dropping old frame {frameId}, latest={_latestFrameId}");
                    return;
                }

                if (diff > 0)
                {
                    _latestFrameId = frameId;
                    CleanupOldBuffers(_latestFrameId);
                }

                if (!_jitterBuffer.TryGetValue(frameId, out FrameReassemblyBufferDebug buffer))
                {
                    // FIX: 计算期望的包数量（向上取整）
                    int expectedPackets = (totalFrameSize + 1391) / 1392;
                    
                    buffer = new FrameReassemblyBufferDebug
                    {
                        totalExpectedBytes = totalFrameSize,
                        currentReceivedBytes = 0,
                        completeData = new byte[totalFrameSize],
                        createTime = (float)DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond,
                        expectedPacketCount = expectedPackets
                    };
                    _jitterBuffer[frameId] = buffer;
                    
                    if (packetsReceived <= 20)
                        Log($"[UltraDebug] Created buffer for frame {frameId}, size={totalFrameSize}, expectedPackets={expectedPackets}");
                }

                if (!buffer.receivedPacketIndices.Contains(packetIdx))
                {
                    // 计算偏移：每个包固定1392字节（除了最后一包）
                    int offset = packetIdx * 1392;
                    
                    if (offset < 0 || offset + payloadLen > buffer.totalExpectedBytes)
                    {
                        Log($"[UltraDebug] OFFSET ERROR: frame={frameId}, pkt={packetIdx}, offset={offset}, len={payloadLen}, total={buffer.totalExpectedBytes}", LogType.Error);
                        return;
                    }
                    
                    Buffer.BlockCopy(data, 8, buffer.completeData, offset, payloadLen);
                    buffer.currentReceivedBytes += payloadLen;
                    buffer.receivedPacketIndices.Add(packetIdx);
                    
                    // FIX: 标记收到了 PktIdx=0，并记录最大 packetIdx
                    if (packetIdx == 0)
                        buffer.hasPktZero = true;
                    if (packetIdx > buffer.lastPacketIdx)
                        buffer.lastPacketIdx = packetIdx;
                    
                    if (packetsReceived <= 20)
                        Log($"[UltraDebug] Frame {frameId}: copied {payloadLen} bytes at offset {offset}, progress {buffer.currentReceivedBytes}/{buffer.totalExpectedBytes}");
                }
                else
                {
                    if (packetsReceived <= 20)
                        Log($"[UltraDebug] Duplicate packet: frame {frameId}, pkt {packetIdx}");
                }

                // Check if frame complete
                // FIX: 必须收到 PktIdx=0 且数据完整，才认为帧有效
                if (buffer.currentReceivedBytes >= buffer.totalExpectedBytes && buffer.hasPktZero)
                {
                    byte[] finalFrame = PrepareFrameForVLC(buffer.completeData, frameId);
                    
                    // FIX: Pre-buffer logic - cache frames until we get IDR or timeout
                    if (!_hasReceivedIDR && _libVLC == null)
                    {
                        // Still in pre-buffer phase, cache the frame
                        byte[] cachedFrame = new byte[finalFrame.Length];
                        Buffer.BlockCopy(finalFrame, 0, cachedFrame, 0, finalFrame.Length);
                        _preBuffer.Add(cachedFrame);
                        
                        // Limit pre-buffer size
                        if (_preBuffer.Count > MAX_PREBUFFER_FRAMES)
                        {
                            _preBuffer.RemoveAt(0);
                        }
                        
                        if (framesCompleted % 10 == 0)
                            Log($"[UltraDebug] Pre-buffering: {framesCompleted} frames cached, waiting for IDR...");
                    }
                    else
                    {
                        // VLC is running, send directly to queue
                        _cleanH265StreamQueue.Enqueue(finalFrame);
                    }
                    
                    _jitterBuffer.Remove(frameId);
                    framesCompleted++;
                    
                    Log($"[UltraDebug] ✓ FRAME {frameId} COMPLETED! Queue size: {_cleanH265StreamQueue.Count}, Pre-buffer: {_preBuffer.Count}");
                }
                else if (buffer.currentReceivedBytes >= buffer.totalExpectedBytes && !buffer.hasPktZero)
                {
                    // FIX: 数据量够了但没收过 PktIdx=0，说明第一包丢失了，丢弃
                    Log($"[UltraDebug] Frame {frameId} has enough data but PktIdx=0 was lost! Dropping.", LogType.Warning);
                    _jitterBuffer.Remove(frameId);
                    framesDropped++;
                }
            }
            catch (Exception ex)
            {
                Log($"[UltraDebug] ProcessPacket ERROR: {ex.Message}", LogType.Error);
                Log($"[UltraDebug] Stack: {ex.StackTrace}", LogType.Error);
            }
        }
    }

    private byte[] PrepareFrameForVLC(byte[] frameData, ushort frameId)
    {
        if (frameData == null || frameData.Length < 4)
        {
            Log($"[UltraDebug] Frame {frameId} data too small: {frameData?.Length ?? 0}", LogType.Warning);
            return frameData;
        }

        bool hasNAL = (frameData[0] == 0x00 && frameData[1] == 0x00 && 
                       frameData[2] == 0x00 && frameData[3] == 0x01);

        if (hasNAL)
        {
            byte nalType = (byte)((frameData[4] >> 1) & 0x3F);
            // HEVC NAL types: 32=VPS, 33=SPS, 34=PPS, 19/20/21=IDR
            bool isVPS = (nalType == 32);
            bool isSPS = (nalType == 33);
            bool isPPS = (nalType == 34);
            bool isIDR = (nalType == 19 || nalType == 20 || nalType == 21);
            
            if (isIDR)
            {
                idrFramesReceived++;
                _hasReceivedIDR = true;  // FIX: Mark that we got IDR
                Log($"[UltraDebug] ✓✓✓ Frame {frameId} has IDR KEYFRAME! NAL type={nalType}");
            }
            else if (isVPS)
            {
                // FIX: Cache VPS
                _cachedVPS = new byte[frameData.Length];
                Buffer.BlockCopy(frameData, 0, _cachedVPS, 0, frameData.Length);
                Log($"[UltraDebug] ✓✓ Frame {frameId} has VPS! Caching for injection.");
            }
            else if (isSPS)
            {
                // FIX: Cache SPS
                _cachedSPS = new byte[frameData.Length];
                Buffer.BlockCopy(frameData, 0, _cachedSPS, 0, frameData.Length);
                Log($"[UltraDebug] ✓✓ Frame {frameId} has SPS! Caching for injection.");
            }
            else if (isPPS)
            {
                // FIX: Cache PPS
                _cachedPPS = new byte[frameData.Length];
                Buffer.BlockCopy(frameData, 0, _cachedPPS, 0, frameData.Length);
                Log($"[UltraDebug] ✓✓ Frame {frameId} has PPS! Caching for injection.");
            }
            else if (framesCompleted <= 10 || nalType != 1)
                Log($"[UltraDebug] Frame {frameId} has NAL type={nalType} (P-frame={nalType==1})");
            
            return frameData;
        }
        else
        {
            Log($"[UltraDebug] Frame {frameId} NO NAL start code! First bytes: {frameData[0]:X2} {frameData[1]:X2} {frameData[2]:X2} {frameData[3]:X2}");
            
            if (autoInjectNALStartCode)
            {
                try
                {
                    byte[] newFrame = new byte[frameData.Length + nalStartCode.Length];
                    Buffer.BlockCopy(nalStartCode, 0, newFrame, 0, nalStartCode.Length);
                    Buffer.BlockCopy(frameData, 0, newFrame, nalStartCode.Length, frameData.Length);
                    nalInjected++;
                    Log($"[UltraDebug] Frame {frameId}: NAL injected");
                    return newFrame;
                }
                catch (Exception ex)
                {
                    Log($"[UltraDebug] NAL inject ERROR: {ex.Message}", LogType.Error);
                    return frameData;
                }
            }
            return frameData;
        }
    }

    private void CleanupOldBuffers(ushort newId)
    {
        List<ushort> toRemove = new List<ushort>();
        foreach(var key in _jitterBuffer.Keys)
        {
            int diff = newId - key;
            if (diff < -30000) diff += 65536;
            
            float currentTime = (float)DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond;
            float age = currentTime - _jitterBuffer[key].createTime;
            var buffer = _jitterBuffer[key];
            
            // FIX: 检测 PktIdx=0 丢失（超时50ms仍未收到）
            if (!buffer.hasPktZero && age > 0.05f)
            {
                Log($"[UltraDebug] Frame {key}: PktIdx=0 lost (timeout {age*1000:F1}ms), dropping frame", LogType.Warning);
                toRemove.Add(key);
                continue;
            }
            
            if (diff > maxReorderFrames || age > 0.4f)
            {
                toRemove.Add(key);
            }
        }
        foreach(var k in toRemove) 
        {
            _jitterBuffer.Remove(k);
            framesDropped++;
        }
    }

    void Update()
    {
        // FIX: Check if we should start VLC (IDR received or timeout)
        if (_libVLC == null)
        {
            float preBufferTime = Time.time - _preBufferStartTime;
            
            if (_hasReceivedIDR)
            {
                Log($"[UltraDebug] IDR frame detected! Starting VLC after {preBufferTime:F2}s pre-buffer");
                StartVLCAndFlushPreBuffer();
            }
            else if (preBufferTime >= MAX_PREBUFFER_TIME)
            {
                Log($"[UltraDebug] Pre-buffer timeout ({MAX_PREBUFFER_TIME}s). Starting VLC anyway...");
                StartVLCAndFlushPreBuffer();
            }
            return;  // Don't do other updates until VLC is ready
        }
        
        // VLC state check and logging
        if (_mediaPlayer != null && Time.time % 3 < 0.1f)
        {
            var state = _mediaPlayer.State;
            
            Log($"[UltraDebug] VLC State: {state}, VideoSize: {_videoWidth}x{_videoHeight}");
            
            if (state == VLCState.Error)
                Log("[UltraDebug] VLC in ERROR state", LogType.Error);
            else if (state == VLCState.Playing && _videoWidth == 0)
                Log("[UltraDebug] VLC Playing but no video format yet - waiting for IDR frame?", LogType.Warning);
            
            // Check if queue is growing but VLC not consuming
            if (_cleanH265StreamQueue.Count > 50)
            {
                Log($"[UltraDebug] Queue growing ({_cleanH265StreamQueue.Count}), VLC may not be reading", LogType.Warning);
            }
        }

        // Stats logging
        if (Time.time - _lastLogTime >= 1.0f)
        {
            _lastLogTime = Time.time;
            float runtime = Time.time - _startTime;
            int rate = (int)((_bytesInSecond * 8) / 1000);
            
            Log("[UltraDebug] === STATS (t={runtime:F1}s) ===");
            Log($"[UltraDebug] Packets: Rcv={packetsReceived}, Proc={packetsProcessed}, Small={packetsTooSmall}, Err={packetsError}");
            Log($"[UltraDebug] Rate: {rate}kbps, Bytes: {bytesReceived}");
            Log($"[UltraDebug] Frames: Comp={framesCompleted}, Drop={framesDropped}, Queue={_cleanH265StreamQueue.Count}");
            Log($"[UltraDebug] NAL: Injected={nalInjected}, IDR={idrFramesReceived}");
            Log($"[UltraDebug] Buffers: {currentBufferCount}, LastPkt: {lastPacketInfo}");
            Log($"[UltraDebug] VLC: Size={_videoWidth}x{_videoHeight}, Tex={_vlcTexture != null}");
            
            if (_videoWidth == 0 && framesCompleted > 10 && _libVLC != null)
            {
                if (idrFramesReceived == 0)
                    Log("[UltraDebug] ⚠️ NO IDR FRAMES! VLC cannot decode P-frames without keyframe. Check sender is sending IDR frames.", LogType.Error);
                else
                    Log("[UltraDebug] Frames completed but no video format - VLC may be failing to decode", LogType.Warning);
            }
            
            _bytesInSecond = 0;
        }

        // VLC auto-restart if ended
        if (_mediaPlayer != null && Time.time % 5 < 0.1f)
        {
            var state = _mediaPlayer.State;
            if (state == VLCState.Ended || state == VLCState.Error || state == VLCState.Stopped)
            {
                Log($"[UltraDebug] VLC state is {state}, restarting... (restart #{vlcRestartCount + 1})", LogType.Warning);
                vlcRestartCount++;
                
                // Clear queue to start fresh
                while (_cleanH265StreamQueue.TryDequeue(out _)) { }
                
                // FIX: Re-inject VPS/SPS/PPS on restart
                if (_cachedVPS != null) _cleanH265StreamQueue.Enqueue(_cachedVPS);
                if (_cachedSPS != null) _cleanH265StreamQueue.Enqueue(_cachedSPS);
                if (_cachedPPS != null) _cleanH265StreamQueue.Enqueue(_cachedPPS);
                if (_cachedVPS != null || _cachedSPS != null || _cachedPPS != null)
                    Log("[UltraDebug] Re-injected cached parameter sets on VLC restart");
                
                // Recreate player
                CreateMediaPlayerAndPlay();
            }
        }

        // Texture update
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
            
            Log($"[UltraDebug] Texture created: {_videoWidth}x{_videoHeight}");
        }

        _vlcTexture.LoadRawTextureData(_vlcPixelBuffer, (int)(_videoWidth * _videoHeight * 4));
        _vlcTexture.Apply();
    }

    private void StartVLCAndFlushPreBuffer()
    {
        // Start VLC engine
        InitVLCEngine();
        
        // FIX: Inject cached VPS/SPS/PPS before pre-buffered frames
        int injectedParams = 0;
        if (_cachedVPS != null)
        {
            _cleanH265StreamQueue.Enqueue(_cachedVPS);
            injectedParams++;
            Log("[UltraDebug] Injected cached VPS");
        }
        if (_cachedSPS != null)
        {
            _cleanH265StreamQueue.Enqueue(_cachedSPS);
            injectedParams++;
            Log("[UltraDebug] Injected cached SPS");
        }
        if (_cachedPPS != null)
        {
            _cleanH265StreamQueue.Enqueue(_cachedPPS);
            injectedParams++;
            Log("[UltraDebug] Injected cached PPS");
        }
        
        // Flush pre-buffered frames
        int flushedCount = 0;
        foreach (var frame in _preBuffer)
        {
            _cleanH265StreamQueue.Enqueue(frame);
            flushedCount++;
        }
        _preBuffer.Clear();
        
        Log($"[UltraDebug] VLC started! Injected {injectedParams} parameter sets, flushed {flushedCount} pre-buffered frames");
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

        Log($"[UltraDebug] Video format callback: {width}x{height}");
        return 1;
    }

    private IntPtr LockideoCallback(IntPtr opaque, IntPtr planes)
    {
        Marshal.WriteIntPtr(planes, _vlcPixelBuffer);
        return IntPtr.Zero;
    }

    void OnDestroy()
    {
        Log("[UltraDebug] OnDestroy called");
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

        // Close log file
        if (_logWriter != null)
        {
            lock (_logLock)
            {
                try
                {
                    _logWriter.WriteLine($"\n=== Log ended at {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
                    _logWriter.Close();
                    _logWriter = null;
                }
                catch { }
            }
        }
    }
}

public class PushStreamAdapterUltraDebug : System.IO.Stream
{
    private ConcurrentQueue<byte[]> _queue;
    private byte[] _leftover;
    private int _leftoverOffset;
    private int _readCount = 0;
    private Action<string, LogType> _logCallback;

    public PushStreamAdapterUltraDebug(ConcurrentQueue<byte[]> queue, Action<string, LogType> logCallback)
    {
        _queue = queue;
        _logCallback = logCallback;
    }

    private void Log(string message, LogType logType = LogType.Log)
    {
        _logCallback?.Invoke(message, logType);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        _readCount++;
        int bytesRead = 0;
        int originalCount = count;

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
        
        if (_readCount <= 20 || _readCount % 50 == 0 || bytesRead == 0)
        {
            Log($"[UltraDebug] VLC.Read #{_readCount}: req={originalCount}, ret={bytesRead}, queue={_queue.Count}");
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
