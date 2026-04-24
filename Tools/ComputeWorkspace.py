#!/usr/bin/env python3
"""
ComputeWorkspace.py
预计算机械臂位置工作空间（J1-J3 部分），导出为 JSON 供 Unity 导入。

用法：
    python ComputeWorkspace.py --j2min -90 --j2max 90 --j3min -140 --j3max 0 \
        --l1 0.5 --l2 0.5 --invert-j2 --output workspace.json

然后在 Unity 中使用 WorkspaceBoundaryImporter 导入生成的 JSON。
"""

import argparse
import json
import math
import sys


def compute_workspace(j2_min, j2_max, j3_min, j3_max,
                      l1, l2, j4_drop_offset,
                      invert_j2, invert_j3,
                      j2_step=1.0, j3_step=1.0,
                      height_step=0.005):
    """
    在 J2/J3 限位范围内均匀采样，计算 J3 尖端（J4 平台根部）可达的位置集合。
    返回：按高度分层的最大水平 reach 表。
    """
    reach_map = {}  # height -> set of horizontal distances

    j2_rad_min = math.radians(j2_min)
    j2_rad_max = math.radians(j2_max)
    j3_rad_min = math.radians(j3_min)
    j3_rad_max = math.radians(j3_max)

    j2 = j2_rad_min
    while j2 <= j2_rad_max + 1e-6:
        j3 = j3_rad_min
        while j3 <= j3_rad_max + 1e-6:
            # 与 RobotIKController 的 FK 计算保持一致
            j2_eff = j2 if invert_j2 else -j2
            j3_eff = j3 if invert_j3 else -j3

            # 大臂方向 (0, -sin(j2), cos(j2))
            # 小臂方向 (0, -sin(j2+j3), cos(j2+j3))
            j3_tip_y = -l1 * math.sin(j2_eff) - l2 * math.sin(j2_eff + j3_eff)
            j3_tip_z =  l1 * math.cos(j2_eff) + l2 * math.cos(j2_eff + j3_eff)

            # J4 平台根部 = J3 尖端 - J4_Drop_Offset (垂直向下)
            platform_y = j3_tip_y - j4_drop_offset
            platform_xz = abs(j3_tip_z)  # 水平距离（X-Z平面半径，J1=0 时 X=0）

            # 离散化高度
            h_bucket = round(platform_y / height_step) * height_step
            h_bucket = round(h_bucket, 6)

            if h_bucket not in reach_map:
                reach_map[h_bucket] = []
            reach_map[h_bucket].append(platform_xz)

            j3 += math.radians(j3_step)
        j2 += math.radians(j2_step)

    if not reach_map:
        print("[错误] 采样结果为空，请检查关节限位和步长设置。", file=sys.stderr)
        sys.exit(1)

    # 按高度排序，计算每层最大 reach
    sorted_heights = sorted(reach_map.keys())
    max_reach_per_height = []
    for h in sorted_heights:
        max_reach = max(reach_map[h])
        max_reach_per_height.append({
            "height": round(h, 6),
            "maxReach": round(max_reach, 6)
        })

    return {
        "j2Min": j2_min,
        "j2Max": j2_max,
        "j3Min": j3_min,
        "j3Max": j3_max,
        "l1BigArm": l1,
        "l2SmallArm": l2,
        "j4DropOffset": j4_drop_offset,
        "heightMin": round(sorted_heights[0], 6),
        "heightMax": round(sorted_heights[-1], 6),
        "heightStep": round(height_step, 6),
        "maxReachPerHeight": max_reach_per_height
    }


def main():
    parser = argparse.ArgumentParser(
        description="预计算机械臂工作空间并导出为 JSON"
    )
    parser.add_argument("--j2min", type=float, default=-90, help="J2 下限（度）")
    parser.add_argument("--j2max", type=float, default=90, help="J2 上限（度）")
    parser.add_argument("--j3min", type=float, default=-140, help="J3 下限（度）")
    parser.add_argument("--j3max", type=float, default=0, help="J3 上限（度）")
    parser.add_argument("--l1", type=float, default=0.5, help="大臂长度 L1（米）")
    parser.add_argument("--l2", type=float, default=0.5, help="小臂长度 L2（米）")
    parser.add_argument("--j4-drop", type=float, default=0.1, help="J4 下沉偏移（米）")
    parser.add_argument("--invert-j2", action="store_true", help="J2 是否反转")
    parser.add_argument("--invert-j3", action="store_true", help="J3 是否反转")
    parser.add_argument("--j2-step", type=float, default=1.0, help="J2 采样步长（度）")
    parser.add_argument("--j3-step", type=float, default=1.0, help="J3 采样步长（度）")
    parser.add_argument("--height-step", type=float, default=0.005, help="高度分层步长（米）")
    parser.add_argument("-o", "--output", type=str, default="workspace_boundary.json",
                        help="输出 JSON 文件路径")
    args = parser.parse_args()

    print(f"[开始计算] J2=[{args.j2min}, {args.j2max}], J3=[{args.j3min}, {args.j3max}]")
    print(f"[参数] L1={args.l1}, L2={args.l2}, J4Drop={args.j4_drop}")
    print(f"[采样步长] J2={args.j2_step}°, J3={args.j3_step}°, Height={args.height_step}m")

    result = compute_workspace(
        args.j2min, args.j2max, args.j3min, args.j3max,
        args.l1, args.l2, args.j4_drop,
        args.invert_j2, args.invert_j3,
        args.j2_step, args.j3_step, args.height_step
    )

    with open(args.output, "w", encoding="utf-8") as f:
        json.dump(result, f, indent=2, ensure_ascii=False)

    print(f"[完成] 已导出到 {args.output}")
    print(f"[统计] 高度范围: {result['heightMin']:.3f}m ~ {result['heightMax']:.3f}m")
    print(f"[统计] 共 {len(result['maxReachPerHeight'])} 个高度层")


if __name__ == "__main__":
    main()
