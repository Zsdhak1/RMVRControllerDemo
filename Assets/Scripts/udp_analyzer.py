#!/usr/bin/env python3
"""
UDP Packet Analyzer for RM Video Stream
"""

import socket
import struct
import sys
import time
import os

def get_local_ips():
    """获取本机所有IP地址"""
    ips = []
    try:
        import subprocess
        result = subprocess.run(['ipconfig'], capture_output=True, text=True, encoding='gbk', errors='ignore')
        output = result.stdout
        for line in output.split('\n'):
            if 'IPv4' in line and ':' in line:
                ip = line.split(':')[-1].strip()
                if ip and not ip.startswith('127.'):
                    ips.append(ip)
    except:
        pass
    return ips

def test_udp_port(port):
    """测试UDP端口是否被占用"""
    try:
        test_sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        test_sock.bind(('0.0.0.0', port))
        test_sock.close()
        return True, None
    except Exception as e:
        return False, str(e)

def main():
    port = 3334
    if len(sys.argv) > 1:
        port = int(sys.argv[1])
    
    print("=" * 60)
    print("UDP Packet Analyzer for RM Video Stream")
    print("=" * 60)
    
    # 显示本机IP
    print("\n本机IP地址:")
    local_ips = get_local_ips()
    if local_ips:
        for ip in local_ips:
            print(f"  - {ip}")
    else:
        print("  (无法获取，请手动检查ipconfig)")
    
    print(f"\n监听端口: {port}")
    
    # 测试端口
    can_bind, error = test_udp_port(port)
    if not can_bind:
        print(f"[警告] 端口 {port} 可能已被占用: {error}")
        print("尝试强制绑定...")
    
    # 创建UDP socket
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    
    # 设置选项
    try:
        sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    except:
        pass
    
    # 尝试绑定
    bound = False
    bind_errors = []
    
    # 尝试绑定到所有接口
    try:
        sock.bind(('0.0.0.0', port))
        print(f"[OK] 成功绑定到 0.0.0.0:{port}")
        bound = True
    except Exception as e:
        bind_errors.append(f"0.0.0.0:{port} - {e}")
    
    # 如果失败，尝试本地IP
    if not bound:
        for ip in local_ips:
            try:
                sock.bind((ip, port))
                print(f"[OK] 成功绑定到 {ip}:{port}")
                bound = True
                break
            except Exception as e:
                bind_errors.append(f"{ip}:{port} - {e}")
    
    if not bound:
        print("[错误] 无法绑定到任何地址:")
        for err in bind_errors:
            print(f"  {err}")
        print(f"\n请检查端口 {port} 是否被其他程序占用")
        input("按Enter键退出...")
        return
    
    # 设置接收缓冲区
    try:
        sock.setsockopt(socket.SOL_SOCKET, socket.SO_RCVBUF, 16 * 1024 * 1024)
        rcvbuf = sock.getsockopt(socket.SOL_SOCKET, socket.SO_RCVBUF)
        print(f"[OK] 接收缓冲区大小: {rcvbuf} bytes")
    except Exception as e:
        print(f"[警告] 设置接收缓冲区失败: {e}")
    
    # 设置非阻塞
    sock.setblocking(False)
    
    print("\n" + "=" * 60)
    print("等待数据包... (按Ctrl+C停止)")
    print("=" * 60)
    print()
    
    packet_count = 0
    total_bytes = 0
    start_time = time.time()
    last_packet_time = None
    
    # 存储帧信息
    frames = {}
    
    try:
        while True:
            received = False
            
            # 尝试接收数据（非阻塞）
            try:
                data, addr = sock.recvfrom(65535)
                received = True
            except BlockingIOError:
                # 没有数据，等待一下
                time.sleep(0.001)
                continue
            except socket.error as e:
                # 其他错误
                time.sleep(0.001)
                continue
            
            if not received:
                continue
            
            packet_count += 1
            total_bytes += len(data)
            last_packet_time = time.time()
            
            # 打印详情（前30个包）
            if packet_count <= 30:
                print(f"\n=== Packet #{packet_count} | {len(data)} bytes | From {addr} ===")
                
                # Hex dump
                print("Raw Hex (first 16 bytes): ", end='')
                for i in range(min(16, len(data))):
                    print(f"{data[i]:02X} ", end='')
                print()
                
                if len(data) > 8:
                    # 解析大端序
                    frame_num_be = struct.unpack('>H', data[0:2])[0]
                    seq_offset_be = struct.unpack('>H', data[2:4])[0]  # 可能是序号或偏移
                    total_size_be = struct.unpack('>I', data[4:8])[0]
                    
                    # 解析小端序
                    frame_num_le = struct.unpack('<H', data[0:2])[0]
                    seq_offset_le = struct.unpack('<H', data[2:4])[0]
                    total_size_le = struct.unpack('<I', data[4:8])[0]
                    
                    payload_len = len(data) - 8
                    
                    print(f"Big Endian:  Frame={frame_num_be}, Seq/Offset={seq_offset_be}, TotalSize={total_size_be}")
                    print(f"Little Endian: Frame={frame_num_le}, Seq/Offset={seq_offset_le}, TotalSize={total_size_le}")
                    
                    # 判断哪个合理
                    be_ok = 0 < total_size_be < 2*1024*1024  # < 2MB
                    le_ok = 0 < total_size_le < 2*1024*1024
                    
                    if be_ok and not le_ok:
                        print(f"  -> Big Endian合理 (TotalSize={total_size_be})")
                        frame_num = frame_num_be
                        seq_offset = seq_offset_be
                        total_size = total_size_be
                    elif le_ok and not be_ok:
                        print(f"  -> Little Endian合理 (TotalSize={total_size_le})")
                        frame_num = frame_num_le
                        seq_offset = seq_offset_le
                        total_size = total_size_le
                    elif be_ok and le_ok:
                        # 都合理，看payload是否是TotalSize的约数
                        if total_size_be % payload_len == 0 or total_size_be < payload_len * 100:
                            print(f"  -> Big Endian可能正确")
                            frame_num = frame_num_be
                            seq_offset = seq_offset_be
                            total_size = total_size_be
                        else:
                            print(f"  -> Little Endian可能正确")
                            frame_num = frame_num_le
                            seq_offset = seq_offset_le
                            total_size = total_size_le
                    else:
                        print(f"  -> 两种字节序都不太合理")
                        frame_num = frame_num_be
                        seq_offset = seq_offset_be
                        total_size = total_size_be
                    
                    # 跟踪帧
                    if frame_num not in frames:
                        frames[frame_num] = {
                            'packets': [],
                            'total_size': total_size,
                            'received_size': 0,
                            'first_seq': seq_offset,
                            'min_seq': seq_offset,
                            'max_seq': seq_offset
                        }
                    
                    frames[frame_num]['packets'].append({
                        'seq': seq_offset,
                        'size': payload_len
                    })
                    frames[frame_num]['received_size'] += payload_len
                    if seq_offset < frames[frame_num]['min_seq']:
                        frames[frame_num]['min_seq'] = seq_offset
                    if seq_offset > frames[frame_num]['max_seq']:
                        frames[frame_num]['max_seq'] = seq_offset
                    
                    # Payload前几个字节
                    print(f"Payload start: ", end='')
                    for i in range(8, min(20, len(data))):
                        print(f"{data[i]:02X} ", end='')
                    print()
                    
                    # 检查HEVC start code
                    if len(data) > 11:
                        if data[8] == 0x00 and data[9] == 0x00 and data[10] == 0x00 and data[11] == 0x01:
                            print("  [HEVC] 找到start code: 00 00 00 01")
                            if len(data) > 12:
                                nal_type = (data[12] >> 1) & 0x3F
                                print(f"  [HEVC] NAL类型: 0x{nal_type:02X}")
                        elif data[8] == 0x00 and data[9] == 0x00 and data[10] == 0x01:
                            print("  [HEVC] 找到start code: 00 00 01")
            
            # 每100个包打印统计
            if packet_count % 100 == 0:
                elapsed = time.time() - start_time
                mb = total_bytes / (1024 * 1024)
                rate = mb / elapsed if elapsed > 0 else 0
                
                print(f"\n[Stats] Packets: {packet_count}, MB: {mb:.2f}, Rate: {rate:.2f} MB/s, "
                      f"Frames tracked: {len(frames)}, Runtime: {elapsed:.1f}s")
                
                # 显示最近几个帧的重组情况
                if frames:
                    recent_frames = sorted(frames.keys())[-5:]
                    print("Recent frames:")
                    for fn in recent_frames:
                        f = frames[fn]
                        pct = 100.0 * f['received_size'] / f['total_size'] if f['total_size'] > 0 else 0
                        print(f"  Frame #{fn}: {len(f['packets'])} packets, "
                              f"seq {f['min_seq']}-{f['max_seq']}, "
                              f"{f['received_size']}/{f['total_size']} bytes ({pct:.1f}%)")
    
    except KeyboardInterrupt:
        print("\n\n用户中断")
    except Exception as e:
        print(f"\n[错误] {e}")
        import traceback
        traceback.print_exc()
    finally:
        sock.close()
        
        # 最终报告
        print("\n" + "=" * 60)
        print("最终报告")
        print("=" * 60)
        print(f"总包数: {packet_count}")
        print(f"总字节数: {total_bytes}")
        print(f"跟踪的帧数: {len(frames)}")
        
        if frames:
            print("\n帧详情:")
            for fn in sorted(frames.keys()):
                f = frames[fn]
                seqs = sorted([p['seq'] for p in f['packets']])
                print(f"  Frame #{fn}: {len(f['packets'])} packets, "
                      f"seqs {seqs}, total {f['received_size']} bytes")
        
        input("\n按Enter键退出...")

if __name__ == "__main__":
    main()
