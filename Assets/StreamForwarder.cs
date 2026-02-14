using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;
using System.Collections.Concurrent;

/// <summary>
/// 功能：将 UDP 3334 端口的数据 剥离前8字节头后 转发至 TCP 3335 端口
/// 特点：完全独立运行，无外部依赖
/// </summary>
public class StreamForwarder : MonoBehaviour
{
    [Header("Network Settings")]
    public int sourcePort = 3334; // UDP 输入
    public int targetPort = 3335; // TCP 输出
    public string listenAddress = "127.0.0.1";

    [Header("Debug Info")]
    [SerializeField] private bool showBitrate = true;

    private Socket _udpSocket;
    private TcpListener _tcpListener;
    private Thread _forwardThread;
    private volatile bool _isRunning = false;

    // 64KB 缓冲区
    private readonly byte[] _buffer = new byte[65536];

    void Start()
    {
        StartForwarding();
    }

    public void StartForwarding()
    {
        if (_isRunning) return;
        _isRunning = true;

        _forwardThread = new Thread(ForwardLoop)
        {
            IsBackground = true,
            Priority = System.Threading.ThreadPriority.AboveNormal,
            Name = "StreamForwarderThread"
        };
        _forwardThread.Start();

        LogToUI($"<color=green>Service Started: UDP {sourcePort} -> TCP {targetPort}</color>");
    }

    public void StopForwarding()
    {
        _isRunning = false;

        if (_udpSocket != null) { try { _udpSocket.Close(); } catch { } _udpSocket = null; }
        if (_tcpListener != null) { try { _tcpListener.Stop(); } catch { } _tcpListener = null; }
        
        if (_forwardThread != null && _forwardThread.IsAlive)
        {
            // 给线程一点时间自行退出，如果不成则强行中断
            if (!_forwardThread.Join(500)) _forwardThread.Abort();
            _forwardThread = null;
        }
        LogToUI("<color=yellow>Service Stopped.</color>");
    }

    private void ForwardLoop()
    {
        // 1. 初始化 UDP
        try
        {
            _udpSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            _udpSocket.Bind(new IPEndPoint(IPAddress.Any, sourcePort));
            _udpSocket.ReceiveBufferSize = 1024 * 1024 * 2; // 2MB 接收缓冲
        }
        catch (Exception e)
        {
            LogToUI($"<color=red>UDP Bind Error: {e.Message}</color>");
            return;
        }

        // 2. 初始化 TCP
        try
        {
            _tcpListener = new TcpListener(IPAddress.Parse(listenAddress), targetPort);
            _tcpListener.Start();
        }
        catch (Exception e)
        {
            LogToUI($"<color=red>TCP Start Error: {e.Message}</color>");
            return;
        }

        LogToUI($"<color=white>Waiting for Connection (e.g. VLC open tcp://{listenAddress}:{targetPort})...</color>");

        while (_isRunning)
        {
            TcpClient client = null;
            NetworkStream stream = null;

            try
            {
                // 等待 TCP 连接
                if (!_tcpListener.Pending())
                {
                    Thread.Sleep(100); // 避免 CPU 空转
                    continue;
                }

                client = _tcpListener.AcceptTcpClient();
                client.NoDelay = true;
                client.SendBufferSize = 1024 * 1024;
                stream = client.GetStream();

                LogToUI("<color=cyan>Client Connected! Forwarding data...</color>");

                long bytesInSecond = 0;
                DateTime lastLogTime = DateTime.Now;
                EndPoint remoteEP = new IPEndPoint(IPAddress.Any, 0);

                while (_isRunning && client.Connected)
                {
                    if (_udpSocket.Available > 0)
                    {
                        int recvLen = _udpSocket.ReceiveFrom(_buffer, ref remoteEP);

                        // 关键逻辑：剥离前8个字节（通常是某些协议的私有头）
                        if (recvLen > 8)
                        {
                            try
                            {
                                stream.Write(_buffer, 8, recvLen - 8);
                                bytesInSecond += (recvLen - 8);
                            }
                            catch
                            {
                                break; // 写入失败，断开 TCP
                            }
                        }
                    }
                    else
                    {
                        Thread.Sleep(1); // 降低 CPU 占用
                    }

                    // 统计比特率
                    if (showBitrate && (DateTime.Now - lastLogTime).TotalSeconds >= 1.0)
                    {
                        double kbps = (bytesInSecond * 8) / 1000.0;
                        LogToUI($"Bitrate: {kbps:F0} kbps");
                        bytesInSecond = 0;
                        lastLogTime = DateTime.Now;
                    }
                }
            }
            catch (Exception e)
            {
                if (_isRunning) LogToUI($"Loop Error: {e.Message}");
            }
            finally
            {
                stream?.Close();
                client?.Close();
                if (_isRunning) LogToUI("<color=orange>Client Disconnected.</color>");
            }
        }
    }

    private void LogToUI(string msg)
    {
        // 现在直接打印到 Unity 控制台，不再依赖 LogModuleManager
        UnityMainThreadDispatcher.Instance().Enqueue(() => {
            Debug.Log($"[StreamForwarder] {msg}");
        });
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

// ---------------------------------------------------------
// 内部辅助类：确保在主线程执行代码（如 Debug.Log）
// ---------------------------------------------------------
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

    public void Enqueue(Action action) => _executionQueue.Enqueue(action);

    void Update()
    {
        while (_executionQueue.TryDequeue(out var action))
        {
            action.Invoke();
        }
    }
}