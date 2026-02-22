using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Collections.Concurrent;
using UnityEngine;

public class StreamForwarder : MonoBehaviour
{
    [Header("Network Settings")]
    public int sourcePort = 3334;
    public int targetPort = 3335;
    
    // Unity内部视频插件去连本机
    public string listenAddress = "0.0.0.0"; 

    private UdpClient _udpClient;
    private TcpListener _tcpListener;
    private TcpClient _currentTcpClient;
    private NetworkStream _tcpStream;
    private Thread _tcpThread;

    public volatile bool isRunning = false;
    
    // 监控大盘数据
    public long probeTotalBytesReceived = 0;
    public int probeTotalPacketsReceived = 0;
    public int currentRateKbps = 0;

    private long bytesInSecond = 0;
    private float lastLogTime;

    void Start()
    {
        StartForwarding();
    }

    public void StartForwarding()
    {
        if (isRunning) return;
        isRunning = true;
        lastLogTime = Time.time;

        // 1. 开启 UDP 异步接水管 (Android 防假死核心)
        try
        {
            _udpClient = new UdpClient();
            _udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, sourcePort));
            _udpClient.Client.ReceiveBufferSize = 1024 * 1024 * 4; 

            _udpClient.BeginReceive(new AsyncCallback(UdpReceiveCallback), null);
            Debug.Log($"[图传内核] UDP {sourcePort} 接水管成功");
        }
        catch (Exception e)
        {
            Debug.LogError($"[图传内核] UDP 启动失败: {e.Message}");
        }

        // 2. 开启 TCP 服务器，等待 Unity 自带播放器来连
        try
        {
            IPAddress ip = (listenAddress == "0.0.0.0" || listenAddress.ToLower() == "any") ? IPAddress.Any : IPAddress.Parse(listenAddress);
            _tcpListener = new TcpListener(ip, targetPort);
            _tcpListener.Start();
            
            // 开一个专门处理 TCP 生命周期的后台线程，防止阻碍 Unity 主线程
            _tcpThread = new Thread(TcpAcceptLoop) { IsBackground = true };
            _tcpThread.Start();
            
            Debug.Log($"[图传内核] TCP {targetPort} 本地源已开启，等待内部播放器连接...");
        }
        catch (Exception e)
        {
            Debug.LogError($"[图传内核] TCP 启动失败: {e.Message}");
        }
    }

    // ====== UDP 异步高频接收与推流 ======
    private void UdpReceiveCallback(IAsyncResult res)
    {
        if (!isRunning || _udpClient == null) return;

        try
        {
            IPEndPoint remoteEP = new IPEndPoint(IPAddress.Any, 0);
            byte[] data = _udpClient.EndReceive(res, ref remoteEP);

            if (data != null && data.Length > 8)
            {
                probeTotalBytesReceived += data.Length;
                probeTotalPacketsReceived++;
                
                // 去除 RMUC 的 8 字节包头，获取纯净 H265
                int payloadLength = data.Length - 8;
                bytesInSecond += payloadLength;

                // 若 Unity 内有播放器连接了，火速把纯净帧推给他
                if (_tcpStream != null)
                {
                    try
                    {
                        // 强制写流，如果对方断开了这里会抛出异常
                        _tcpStream.Write(data, 8, payloadLength);
                    }
                    catch
                    {
                        // 发现播放器端断开了，立刻斩断流引用
                        CloseCurrentTcpClient();
                    }
                }
            }

            // 立刻开始接下一滴水
            if (isRunning)
            {
                _udpClient.BeginReceive(new AsyncCallback(UdpReceiveCallback), null);
            }
        }
        catch (ObjectDisposedException) 
        { }
        catch (Exception ex)
        {
            if (isRunning) Debug.LogWarning($"[图传内核] UDP 错误: {ex.Message}");
        }
    }

    // ====== TCP 连接生命周期控制与清理 ======
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

                // 一旦来老客户，先清退上一个
                CloseCurrentTcpClient();

                _currentTcpClient = _tcpListener.AcceptTcpClient();
                // 配置 0 延迟推送参数
                _currentTcpClient.NoDelay = true;
                _currentTcpClient.SendBufferSize = 1024 * 1024 * 2;
                _tcpStream = _currentTcpClient.GetStream();

                UnityMainThreadDispatcher.Instance().Enqueue(() => {
                    Debug.Log("<color=cyan>[图传内核] Unity 内部播放器连接成功！引擎点火！</color>");
                });
                
                // 死循环探活，一旦发现 TCP Client 死亡，强制关闭并回到最上层等待
                while (isRunning && _currentTcpClient.Connected)
                {
                    if (_currentTcpClient.Client.Poll(0, SelectMode.SelectRead))
                    {
                        byte[] checkBuf = new byte[1];
                        if (_currentTcpClient.Client.Receive(checkBuf, SocketFlags.Peek) == 0)
                            break; 
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

    // ====== 提供主界面速率日志 ======
    void Update()
    {
        if (!isRunning) return;

        if (Time.time - lastLogTime >= 1.0f)
        {
            currentRateKbps = (int)((bytesInSecond * 8) / 1000);
            
            if (currentRateKbps > 0)
            {
                UnityMainThreadDispatcher.Instance().Enqueue(() => {
                     Debug.Log($"[StreamForwarder] Bitrate: {currentRateKbps} kbps");
                });
            }

            bytesInSecond = 0;
            lastLogTime = Time.time;
        }
    }

    void OnDestroy() => StopForwarding();
    void OnApplicationQuit() => StopForwarding();

    public void StopForwarding()
    {
        isRunning = false;
        if (_udpClient != null) { try { _udpClient.Close(); } catch { } _udpClient = null; }
        CloseCurrentTcpClient();
        if (_tcpListener != null) { try { _tcpListener.Stop(); } catch { } _tcpListener = null; }
    }
}

// 内部单例列队保护，保持原样
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
            else { _instance = go.GetComponent<UnityMainThreadDispatcher>(); }
        }
        return _instance;
    }

    public void Enqueue(Action action) => _executionQueue.Enqueue(action);
    void Update() { while (_executionQueue.TryDequeue(out var action)) { action.Invoke(); } }
}