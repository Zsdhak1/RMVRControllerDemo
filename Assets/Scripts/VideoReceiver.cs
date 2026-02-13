using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;
using System.Collections.Concurrent;

public class VideoReceiver : MonoBehaviour
{
    [Header("Network Settings")]
    public int listenPort = 3334;
    public int forwardPort = 12345;
    // 根据你之前的测试，勾选或不勾选导致FPS出现的那一种设置是正确的，保持它
    public bool isBigEndian = false; 

    [Header("Debug Options")]
    public bool logFirstByte = false; // 勾选后会打印拼包后第一个字节，用于确认是否是 Annex-B

    [Header("Realtime Stats")]
    [SerializeField] private string status = "Stopped";
    [SerializeField] private float receiveRateMbps;
    [SerializeField] private int receivedFps;
    [SerializeField] private int bufferCount;

    private UdpClient udpClient;
    private UdpClient forwardClient;
    private IPEndPoint localEndPoint;
    private Thread receiveThread;
    private bool isRunning = false;

    // Annex-B Start Code
    private readonly byte[] StartCode = new byte[] { 0x00, 0x00, 0x00, 0x01 };

    // Stats
    private long bytesReceivedInSecond = 0;
    private int framesCompletedInSecond = 0;
    private float statsTimer = 0;

    private ConcurrentDictionary<ushort, FrameBuffer> frameBuffer = new ConcurrentDictionary<ushort, FrameBuffer>();
    private ushort lastProcessedFrame = 0;

    class FrameBuffer
    {
        public int TotalSize;
        public int ReceivedSize;
        public SortedDictionary<ushort, byte[]> Fragments = new SortedDictionary<ushort, byte[]>();
        public DateTime CreateTime;
    }

    void Start()
    {
        StartReceiver();
    }

    void StartReceiver()
    {
        try
        {
            udpClient = new UdpClient(listenPort);
            
            // 【核心修复1】设置为 4MB 缓冲区，解决 FPS 低/丢包问题
            udpClient.Client.ReceiveBufferSize = 4096 * 1024; 

            forwardClient = new UdpClient();
            localEndPoint = new IPEndPoint(IPAddress.Loopback, forwardPort);

            isRunning = true;
            receiveThread = new Thread(ReceiveLoop);
            receiveThread.IsBackground = true;
            receiveThread.Start();

            status = $"Active. Buffer: {udpClient.Client.ReceiveBufferSize / 1024} KB";
        }
        catch (Exception e)
        {
            status = "Error: " + e.Message;
            Debug.LogError(e.Message);
        }
    }

    void Update()
    {
        statsTimer += Time.deltaTime;
        if (statsTimer >= 1.0f)
        {
            receiveRateMbps = (bytesReceivedInSecond * 8f) / (1024f * 1024f);
            receivedFps = framesCompletedInSecond;
            bufferCount = frameBuffer.Count;

            bytesReceivedInSecond = 0;
            framesCompletedInSecond = 0;
            statsTimer = 0;
        }
    }

    void ReceiveLoop()
    {
        IPEndPoint remoteEP = new IPEndPoint(IPAddress.Any, 0);
        while (isRunning)
        {
            try
            {
                byte[] data = udpClient.Receive(ref remoteEP);
                if (data.Length <= 8) continue;

                Interlocked.Add(ref bytesReceivedInSecond, data.Length);
                ProcessPacket(data);
            }
            catch (Exception) { }
        }
    }

    void ProcessPacket(byte[] data)
    {
        ushort frameSeq;
        ushort fragIndex;
        int totalSize;

        // 大小端解析
        if (isBigEndian)
        {
            frameSeq = (ushort)((data[0] << 8) | data[1]);
            fragIndex = (ushort)((data[2] << 8) | data[3]);
            totalSize = (data[4] << 24) | (data[5] << 16) | (data[6] << 8) | data[7];
        }
        else
        {
            frameSeq = BitConverter.ToUInt16(data, 0);
            fragIndex = BitConverter.ToUInt16(data, 2);
            totalSize = BitConverter.ToInt32(data, 4);
        }

        // 丢包容错：如果总大小太小，肯定是解析错了
        if (totalSize < 50) return;

        // 丢弃旧帧
        if (frameSeq < lastProcessedFrame && (lastProcessedFrame - frameSeq < 1000)) return;

        FrameBuffer frame = frameBuffer.GetOrAdd(frameSeq, (k) => new FrameBuffer
        {
            TotalSize = totalSize,
            CreateTime = DateTime.Now
        });

        // 存入分片 (去除8字节头)
        if (!frame.Fragments.ContainsKey(fragIndex))
        {
            int payloadLen = data.Length - 8;
            byte[] payload = new byte[payloadLen];
            Buffer.BlockCopy(data, 8, payload, 0, payloadLen);

            frame.Fragments.Add(fragIndex, payload);
            frame.ReceivedSize += payloadLen;
        }

        // 【宽松组包条件】只要收到的数据 >= 预期大小，就尝试解码
        if (frame.ReceivedSize >= frame.TotalSize)
        {
            ReassembleAndForward(frame);
            
            FrameBuffer removed;
            frameBuffer.TryRemove(frameSeq, out removed);
            lastProcessedFrame = frameSeq;

            if (frameBuffer.Count > 20) CleanOldFrames(frameSeq);
        }
    }

    void ReassembleAndForward(FrameBuffer frame)
    {
        // 1. 先把所有分片拼成一个完整的大 Byte 数组
        int totalLen = 0;
        foreach (var frag in frame.Fragments.Values) totalLen += frag.Length;
        byte[] rawFrame = new byte[totalLen];
        int offset = 0;
        foreach (var frag in frame.Fragments.Values)
        {
            Buffer.BlockCopy(frag, 0, rawFrame, offset, frag.Length);
            offset += frag.Length;
        }

        // 2. 【核心修复2】智能检查：是否已经包含 Annex-B 头 (00 00 00 01)
        bool hasStartCode = false;
        if (rawFrame.Length > 4)
        {
            if (rawFrame[0] == 0x00 && rawFrame[1] == 0x00 && rawFrame[2] == 0x00 && rawFrame[3] == 0x01)
                hasStartCode = true;
            else if (rawFrame[0] == 0x00 && rawFrame[1] == 0x00 && rawFrame[2] == 0x01) // 或者是 3字节头
                hasStartCode = true;
        }

        if (logFirstByte) Debug.Log($"[Check] Frame Start: {BitConverter.ToString(rawFrame, 0, 10)} | HasCode: {hasStartCode}");

        // 3. 决定是否注入
        byte[] finalData;
        if (hasStartCode)
        {
            // 官方文档说有，那就直接用原数据
            finalData = rawFrame;
        }
        else
        {
            // 官方文档说有但实际上没有，我们帮它补上
            finalData = new byte[StartCode.Length + rawFrame.Length];
            Buffer.BlockCopy(StartCode, 0, finalData, 0, StartCode.Length);
            Buffer.BlockCopy(rawFrame, 0, finalData, StartCode.Length, rawFrame.Length);
        }

        // 4. 发送给 UMP
        if (forwardClient != null)
        {
            forwardClient.Send(finalData, finalData.Length, localEndPoint);
            Interlocked.Increment(ref framesCompletedInSecond);
        }
    }

    void CleanOldFrames(ushort currentSeq)
    {
        List<ushort> toRemove = new List<ushort>();
        foreach (var key in frameBuffer.Keys)
        {
            if (currentSeq > key && (currentSeq - key > 60)) toRemove.Add(key);
        }
        foreach (var key in toRemove) frameBuffer.TryRemove(key, out _);
    }

    void OnDestroy()
    {
        isRunning = false;
        if (udpClient != null) udpClient.Close();
        if (forwardClient != null) forwardClient.Close();
        if (receiveThread != null) receiveThread.Abort();
    }
}