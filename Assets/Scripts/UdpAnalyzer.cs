using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

/// <summary>
/// UDP数据包分析工具 - 用于分析RM图传UDP包格式
/// </summary>
class UdpAnalyzer
{
    static void Main(string[] args)
    {
        int port = 3334;
        if (args.Length > 0 && int.TryParse(args[0], out int p))
            port = p;

        Console.WriteLine($"=== UDP Packet Analyzer ===");
        Console.WriteLine($"Listening on port: {port}");
        Console.WriteLine($"Press Ctrl+C to exit");
        Console.WriteLine();

        UdpClient udpClient = null;
        
        try
        {
            udpClient = new UdpClient();
            udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, port));
            udpClient.Client.ReceiveBufferSize = 1024 * 1024 * 16;
            
            Console.WriteLine("[OK] UDP socket created successfully");
            Console.WriteLine();
            
            IPEndPoint remoteEP = new IPEndPoint(IPAddress.Any, 0);
            long packetCount = 0;
            long totalBytes = 0;
            DateTime startTime = DateTime.Now;
            
            // 用于跟踪帧
            ushort lastFrameNum = 0;
            ushort lastSeq = 0;
            
            while (true)
            {
                try
                {
                    byte[] data = udpClient.Receive(ref remoteEP);
                    packetCount++;
                    totalBytes += data.Length;
                    
                    // 打印每个包（前20个和后每100个）
                    bool printDetail = packetCount <= 20 || packetCount % 100 == 0;
                    
                    if (printDetail)
                    {
                        Console.WriteLine($"=== Packet #{packetCount} ({data.Length} bytes) from {remoteEP} ===");
                        
                        // 1. 打印原始Hex数据（前16字节）
                        Console.Write("Raw Hex (first 16 bytes): ");
                        for (int i = 0; i < Math.Min(16, data.Length); i++)
                        {
                            Console.Write($"{data[i]:X2} ");
                        }
                        Console.WriteLine();
                        
                        // 2. 如果数据大于8字节，解析头部
                        if (data.Length > 8)
                        {
                            // 尝试多种解析方式
                            
                            // 方式1: 大端序 (文档说明的方式)
                            ushort frameNumBE = (ushort)((data[0] << 8) | data[1]);
                            ushort seqBE = (ushort)((data[2] << 8) | data[3]);
                            uint totalSizeBE = ((uint)data[4] << 24) | ((uint)data[5] << 16) | 
                                               ((uint)data[6] << 8) | data[7];
                            
                            // 方式2: 小端序
                            ushort frameNumLE = (ushort)(data[0] | (data[1] << 8));
                            ushort seqLE = (ushort)(data[2] | (data[3] << 8));
                            uint totalSizeLE = (uint)(data[4] | (data[5] << 8) | 
                                               (data[6] << 16) | (data[7] << 24));
                            
                            // 方式3: 混合（帧号大端，其他小端）
                            uint totalSizeMixed = (uint)(data[4] << 24) | (uint)(data[5] << 16) | 
                                                  (uint)(data[6] << 8) | data[7];
                            
                            Console.WriteLine($"Big Endian:    Frame={frameNumBE}, Seq/Offset={seqBE}, TotalSize={totalSizeBE}");
                            Console.WriteLine($"Little Endian: Frame={frameNumLE}, Seq/Offset={seqLE}, TotalSize={totalSizeLE}");
                            Console.WriteLine($"Payload size: {data.Length - 8}");
                            
                            // 计算帧号差值
                            if (packetCount > 1)
                            {
                                int frameDiff = (frameNumBE - lastFrameNum) & 0xFFFF;
                                int seqDiff = (seqBE - lastSeq) & 0xFFFF;
                                Console.WriteLine($"Frame diff: {frameDiff}, Seq diff: {seqDiff}");
                                
                                if (frameDiff > 1 && frameDiff < 100)
                                {
                                    Console.WriteLine($"  [!] Frame jump detected: {lastFrameNum} -> {frameNumBE}");
                                }
                            }
                            
                            lastFrameNum = frameNumBE;
                            lastSeq = seqBE;
                            
                            // 判断哪种字节序更合理
                            bool beReasonable = totalSizeBE < 10 * 1024 * 1024; // < 10MB
                            bool leReasonable = totalSizeLE < 10 * 1024 * 1024;
                            
                            if (beReasonable && !leReasonable)
                            {
                                Console.WriteLine($"  [>] Big Endian seems correct (size {totalSizeBE} is reasonable)");
                            }
                            else if (!beReasonable && leReasonable)
                            {
                                Console.WriteLine($"  [>] Little Endian seems correct (size {totalSizeLE} is reasonable)");
                            }
                            else if (beReasonable && leReasonable)
                            {
                                // 都合理，看哪个更接近payload的整数倍
                                int payloadSize = data.Length - 8;
                                if (totalSizeBE % payloadSize == 0 || totalSizeBE / payloadSize < 100)
                                {
                                    Console.WriteLine($"  [>] Big Endian looks better (size {totalSizeBE} relates to payload {payloadSize})");
                                }
                                if (totalSizeLE % payloadSize == 0 || totalSizeLE / payloadSize < 100)
                                {
                                    Console.WriteLine($"  [>] Little Endian looks better (size {totalSizeLE} relates to payload {payloadSize})");
                                }
                            }
                            
                            // 检查是否是HEVC的起始码 (00 00 00 01 或 00 00 01)
                            Console.Write("Payload start: ");
                            for (int i = 8; i < Math.Min(20, data.Length); i++)
                            {
                                Console.Write($"{data[i]:X2} ");
                            }
                            Console.WriteLine();
                            
                            // 检查HEVC NAL单元类型
                            if (data.Length > 12)
                            {
                                // HEVC NAL头通常在前几个字节
                                // NAL单元头格式: 2字节，包含 nal_unit_type
                                byte nalHeader = data[8]; // 假设payload[0]是NAL头
                                byte nalType = (byte)((nalHeader >> 1) & 0x3F);
                                Console.WriteLine($"  HEVC NAL type guess: 0x{nalType:X2} ({GetNALTypeName(nalType)})");
                            }
                        }
                        
                        Console.WriteLine();
                    }
                    
                    // 每100个包打印一次统计
                    if (packetCount % 100 == 0)
                    {
                        TimeSpan elapsed = DateTime.Now - startTime;
                        double mb = totalBytes / (1024.0 * 1024.0);
                        double rate = mb / elapsed.TotalSeconds;
                        Console.WriteLine($"[Stats] Packets: {packetCount}, MB: {mb:F2}, Rate: {rate:F2} MB/s, Runtime: {elapsed.TotalSeconds:F1}s");
                    }
                }
                catch (SocketException ex)
                {
                    Console.WriteLine($"[Socket Error] {ex.Message}");
                    Thread.Sleep(100);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Error] {ex.Message}");
            Console.WriteLine($"{ex.StackTrace}");
        }
        finally
        {
            udpClient?.Close();
        }
    }
    
    static string GetNALTypeName(byte nalType)
    {
        // HEVC NAL单元类型
        switch (nalType)
        {
            case 0x20: return "VPS";
            case 0x21: return "SPS";
            case 0x22: return "PPS";
            case 0x26: return "IDR_W_RADL";
            case 0x27: return "IDR_N_LP";
            case 0x28: return "CRA_NUT";
            case 0x01: return "TRAIL_R";
            case 0x00: return "TRAIL_N";
            default: return $"Unknown(0x{nalType:X2})";
        }
    }
}
