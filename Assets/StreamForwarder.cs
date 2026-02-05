using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;
using System.Collections.Concurrent;

public class StreamForwarder : MonoBehaviour
{
    private const int SourcePort = 3334; // UDP 入口
    private const int TargetPort = 3335; // TCP 出口

    private Socket _udpSocket;
    private TcpListener _tcpListener;
    private Thread _forwardThread;
    private volatile bool _isRunning = false; 

    public LogModuleManager logModule;

    // 64KB 缓冲区
    private byte[] _buffer = new byte[65536]; 

    public void StartForwarding()
    {
        if (_isRunning) return;
        _isRunning = true;

        _forwardThread = new Thread(ForwardLoop);
        _forwardThread.IsBackground = true;
        _forwardThread.Priority = System.Threading.ThreadPriority.AboveNormal;
        _forwardThread.Start();

        LogToUI($"<color=green>Service Started: UDP {SourcePort} -> TCP {TargetPort}</color>");
    }

    public void StopForwarding()
    {
        _isRunning = false;
        // 强制关闭 Socket
        if (_udpSocket != null) { try { _udpSocket.Close(); } catch { } }
        if (_tcpListener != null) { try { _tcpListener.Stop(); } catch { } }
        if (_forwardThread != null && _forwardThread.IsAlive) _forwardThread.Abort();
    }

    private void ForwardLoop()
    {
        // 1. 初始化 UDP (使用底层 Socket)
        _udpSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            _udpSocket.Bind(new IPEndPoint(IPAddress.Any, SourcePort));
            _udpSocket.ReceiveBufferSize = 1024 * 1024 * 2; // 2MB UDP Buffer
        }
        catch (Exception e)
        {
            LogToUI($"<color=red>UDP Bind Error: {e.Message}</color>");
            return;
        }

        // 2. 初始化 TCP
        try 
        {
            _tcpListener = new TcpListener(IPAddress.Parse("127.0.0.1"), TargetPort);
            _tcpListener.Start();
        }
        catch (Exception e)
        {
            LogToUI($"<color=red>TCP Start Error: {e.Message}</color>");
            return;
        }

        LogToUI("Waiting for Client (VLC/UMP)...");

        // --- 外层循环：重连机制 ---
        while (_isRunning)
        {
            TcpClient client = null;
            NetworkStream stream = null;

            try
            {
                // 阻塞等待连接
                client = _tcpListener.AcceptTcpClient();
                client.NoDelay = true; 
                client.SendBufferSize = 1024 * 1024; 
                stream = client.GetStream();

                LogToUI("<color=cyan>Client Connected! Streaming...</color>");

                long bytesInSecond = 0;
                DateTime lastLogTime = DateTime.Now;
                EndPoint remoteEP = new IPEndPoint(IPAddress.Any, 0);

                // --- 内层循环：转发数据 ---
                while (_isRunning && client.Connected)
                {
                    // 接收 UDP
                    int recvLen = _udpSocket.ReceiveFrom(_buffer, ref remoteEP);

                    if (recvLen > 8)
                    {
                        try 
                        {
                            // 切掉前8字节，写入 TCP
                            stream.Write(_buffer, 8, recvLen - 8);
                            bytesInSecond += (recvLen - 8);
                        }
                        catch
                        {
                            LogToUI("<color=orange>Client Disconnected.</color>");
                            break; 
                        }
                    }

                    // 每秒更新一次 UI
                    if ((DateTime.Now - lastLogTime).TotalSeconds >= 1.0)
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
                if (_isRunning) LogToUI($"Error: {e.Message}");
            }
            finally
            {
                if (stream != null) stream.Close();
                if (client != null) client.Close();
            }
        }
    }

    private void LogToUI(string msg)
    {
        // 调用下面的辅助类
        UnityMainThreadDispatcher.Instance().Enqueue(() => {
            if (logModule != null) logModule.AddLog(msg);
        });
    }

    void OnDestroy()
    {
        StopForwarding();
    }
}

// ↓↓↓↓↓↓↓↓↓↓↓↓↓↓↓↓↓↓↓↓↓↓↓↓↓↓↓↓↓↓↓↓↓↓↓↓↓↓↓↓↓↓↓↓↓↓↓↓↓↓
// ！！！ 这一段就是你之前报错缺失的部分 ！！！
// ↓↓↓↓↓↓↓↓↓↓↓↓↓↓↓↓↓↓↓↓↓↓↓↓↓↓↓↓↓↓↓↓↓↓↓↓↓↓↓↓↓↓↓↓↓↓↓↓↓↓

public class UnityMainThreadDispatcher : MonoBehaviour
{
    private static UnityMainThreadDispatcher _instance;
    private readonly ConcurrentQueue<Action> _executionQueue = new ConcurrentQueue<Action>();

    public static UnityMainThreadDispatcher Instance()
    {
        if (!_instance)
        {
            // 在场景里创建一个隐藏的物体来承载这个组件
            GameObject go = new GameObject("UnityMainThreadDispatcher");
            _instance = go.AddComponent<UnityMainThreadDispatcher>();
            DontDestroyOnLoad(go);
        }
        return _instance;
    }

    public void Enqueue(Action action) => _executionQueue.Enqueue(action);

    void Update()
    {
        while (_executionQueue.TryDequeue(out var action)) action.Invoke();
    }
}