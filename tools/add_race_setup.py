#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
add_race_setup.py : コース定義にスタート姿勢と計測ゲートを足す。

pdf_to_course.py が作るのは「面と線」だけで、どこから走り出してどこで計測するかは
図面からは決まらない。ここでは本線のパスを 1 本選び、その上の指定した点を
スタート／ゴールとして、途中に等間隔のチェックポイントを置く。

ゲートは本線に直交する線分として作る。走行体の中心が横切ると反応する。

使い方:
    python tools/add_race_setup.py course/etrobo_2026.json -o course/etrobo_2026_L.json \\
        --line-id line_12_0 --start-at 3427 --goal-near 321 1122 --heading -90
"""

from __future__ import annotations

import argparse
import io
import json
import math
import sys


def flatten(path, steps=16):
    pts = [path["start"]]
    for seg in path["segments"]:
        if seg["type"] == "line":
            pts.append(seg["to"])
        elif seg["type"] == "cubic":
            p0 = pts[-1]
            c1, c2, p3 = seg["c1"], seg["c2"], seg["to"]
            for i in range(1, steps + 1):
                t = i / steps
                u = 1 - t
                pts.append([
                    u**3*p0[0] + 3*u*u*t*c1[0] + 3*u*t*t*c2[0] + t**3*p3[0],
                    u**3*p0[1] + 3*u*u*t*c1[1] + 3*u*t*t*c2[1] + t**3*p3[1]])
        else:
            pts.append(seg["to"])
    return pts


def cumulative(pts):
    acc = [0.0]
    for i in range(1, len(pts)):
        acc.append(acc[-1] + math.dist(pts[i - 1], pts[i]))
    return acc


def point_at(pts, acc, s):
    """弧長 s の位置の点と、その場所の進行方向を返す。"""
    s = max(0.0, min(acc[-1], s))
    for i in range(1, len(pts)):
        if acc[i] >= s:
            span = acc[i] - acc[i - 1]
            t = 0.0 if span < 1e-9 else (s - acc[i - 1]) / span
            p = [pts[i - 1][0] + (pts[i][0] - pts[i - 1][0]) * t,
                 pts[i - 1][1] + (pts[i][1] - pts[i - 1][1]) * t]
            d = [pts[i][0] - pts[i - 1][0], pts[i][1] - pts[i - 1][1]]
            n = math.hypot(*d) or 1.0
            return p, [d[0] / n, d[1] / n]
    return pts[-1], [1.0, 0.0]


def nearest_s(pts, acc, target):
    best = (float("inf"), 0.0)
    for i, p in enumerate(pts):
        d = math.dist(p, target)
        if d < best[0]:
            best = (d, acc[i])
    return best[1], best[0]


def make_gate(gid, kind, p, direction, half_len):
    """進行方向に直交する線分を作る。"""
    nx, ny = -direction[1], direction[0]
    return {
        "id": gid,
        "kind": kind,
        "from": [round(p[0] - nx * half_len, 1), round(p[1] - ny * half_len, 1)],
        "to": [round(p[0] + nx * half_len, 1), round(p[1] + ny * half_len, 1)],
    }


def run(args):
    with io.open(args.course, encoding="utf-8") as f:
        course = json.load(f)

    line = next((p for p in course["paths"] if p["id"] == args.line_id), None)
    if line is None:
        ids = [p["id"] for p in course["paths"] if p.get("width", 0) >= 15]
        print("パス '%s' が見つかりません。太い線の候補: %s" % (args.line_id, ids),
              file=sys.stderr)
        return 1

    pts = flatten(line)
    acc = cumulative(pts)
    total = acc[-1]

    # スタート: 本線の始点から少し進んだところ
    s_start, _ = nearest_s(pts, acc, args.start_near)
    start_p, start_dir = point_at(pts, acc, s_start)

    # ゴール: LAP マーカーに最も近い本線上の点
    s_goal, goal_dist = nearest_s(pts, acc, args.goal_near)
    goal_p, goal_dir = point_at(pts, acc, s_goal)

    if s_goal <= s_start:
        print("ゴールがスタートより手前です。--start-near / --goal-near を見直してください。",
              file=sys.stderr)
        return 1

    gates = [make_gate("start", "start", start_p, start_dir, args.gate_half_length)]

    for i in range(1, args.checkpoints + 1):
        s = s_start + (s_goal - s_start) * i / (args.checkpoints + 1)
        p, d = point_at(pts, acc, s)
        gates.append(make_gate("cp%d" % i, "checkpoint", p, d, args.gate_half_length))

    gates.append(make_gate("goal", "goal", goal_p, goal_dir, args.gate_half_length))

    # スタート姿勢: センサがラインの縁に乗る位置へ置く
    # 進行方向の左が +90 度側。左縁を狙うならそちらへ線幅の半分ずらす。
    side = 1.0 if args.edge == "left" else -1.0
    lx, ly = -start_dir[1] * side, start_dir[0] * side
    half_w = line.get("width", 20.0) / 2.0

    sensor = [start_p[0] + lx * half_w, start_p[1] + ly * half_w]
    robot = [sensor[0] - start_dir[0] * args.sensor_offset,
             sensor[1] - start_dir[1] * args.sensor_offset]

    heading = math.degrees(math.atan2(start_dir[1], start_dir[0]))

    course["gates"] = gates
    course["start_pose"] = {
        "position": [round(robot[0], 1), round(robot[1], 1)],
        "heading": round(heading, 1),
    }
    if args.name:
        course["name"] = args.name

    with io.open(args.output, "w", encoding="utf-8", newline="\n") as f:
        json.dump(course, f, ensure_ascii=False, indent=2)
        f.write("\n")

    print("%s を書き出しました" % args.output)
    print("  本線       : %s (全長 %.0fmm)" % (args.line_id, total))
    print("  スタート   : 走行体 (%.0f, %.0f) 方位 %.1f度  センサは (%.0f, %.0f) の %s縁"
          % (robot[0], robot[1], heading, sensor[0], sensor[1],
             "左" if args.edge == "left" else "右"))
    print("  ゴール     : (%.0f, %.0f)  LAP マーカーから %.0fmm"
          % (goal_p[0], goal_p[1], goal_dist))
    print("  計測区間   : %.0fmm  チェックポイント %d 個"
          % (s_goal - s_start, args.checkpoints))
    return 0


def main():
    p = argparse.ArgumentParser(description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("course")
    p.add_argument("-o", "--output", required=True)
    p.add_argument("--line-id", required=True, help="本線のパス id")
    p.add_argument("--start-near", nargs=2, type=float, required=True, metavar=("X", "Y"),
                   help="スタート地点に最も近い座標 [mm]")
    p.add_argument("--goal-near", nargs=2, type=float, required=True, metavar=("X", "Y"),
                   help="ゴール地点に最も近い座標 [mm]")
    p.add_argument("--checkpoints", type=int, default=3)
    p.add_argument("--gate-half-length", type=float, default=150.0,
                   help="ゲート線分の半長 [mm]")
    p.add_argument("--edge", choices=["left", "right"], default="left",
                   help="どちら側の縁をなぞる前提でスタート姿勢を作るか")
    p.add_argument("--sensor-offset", type=float, default=95.0,
                   help="車軸からカラーセンサまでの距離 [mm]")
    p.add_argument("--name")

    args = p.parse_args()
    args.start_near = tuple(args.start_near)
    args.goal_near = tuple(args.goal_near)
    sys.exit(run(args))


if __name__ == "__main__":
    main()
