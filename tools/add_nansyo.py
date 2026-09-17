#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
add_nansyo.py : コース定義に 2026 年度の難所（ET相撲・ボトルデリバリー・ETラリー）を置く。

置き場所はコース図に印刷済みなので、JSON から読み取って使う。手で座標を書かない。

    土俵            : 大きい円 (半径 180mm 前後)
    ピックアップポジション : 本線の上にある灰色マーカ
    ドロップゾーン      : 200x200 の赤・青・黄のエリア
    ゲートポジション     : 5x5 に並んだ灰色マーカ

ゲートポジションの名前は競技規約 5.17.1 の GX-Y（X が横、Y が縦、どちらも 1..5）。
L コースでは X が大きいほど右、Y が大きいほど下（コース座標では y が小さい）。
R コースは左右反転なので、X が大きいほど左になる。

寸法の出どころ（難所組立図 1.0.2 / 競技規約 1.0.1）:
    力士ボトル    PET 500丸 M1-354-02  φ68 x 210H  黒ラベル(幅50mm)  水入りで約105g
    キャリーボトル  PET 300丸 M1-354-01  φ62 x 150H  赤青黄ラベル(幅50mm を 2 重)  約105g
    ゲート       VP13 の塩ビ管  内寸 幅約240mm x 高さ約245mm
                 赤=横、青=縦、黄=横 の向きで、隣り合うゲートポジションに置く

使い方:
    python tools/add_nansyo.py course/etrobo_2026_L.json -o course/etrobo_2026_L_nansyo.json \\
        --gate red G2-5 --gate blue G5-3 --gate yellow G1-2 \\
        --carry-color blue
"""

from __future__ import annotations

import argparse
import copy
import io
import json
import math
import sys

# --- 難所組立図 1.0.2 の寸法 [mm] ---------------------------------------

# ラベルはどちらも幅 50mm の養生テープ。キャリーボトルの「2 重巻き」は
# 下端を合わせて重ねて巻くので、帯の高さは 50mm のまま厚みだけが増える。
RIKISHI = dict(diameter=68.0, height=210.0, mass_g=105.0, empty_g=40.0,
               label_color="#101010", label_height=50.0)
CARRY = dict(diameter=62.0, height=150.0, mass_g=105.0, empty_g=25.0,
             label_height=50.0)

GATE_INNER_WIDTH = 240.0
GATE_INNER_HEIGHT = 245.0
GATE_PIPE_DIAMETER = 18.0                 # VP13 の外径

GATE_COLORS = {"red": "#E83828", "blue": "#036EB8", "yellow": "#FFEA00"}

# 競技規約 表 5-2。赤=横、青=縦、黄=横。
GATE_ORIENTATION = {"red": "yoko", "blue": "tate", "yellow": "yoko"}

LABEL_COLORS = {"red": "#E83828", "blue": "#036EB8", "yellow": "#FFEA00"}


# --- コース図から場所を読む --------------------------------------------

def load(path):
    return json.load(io.open(path, encoding="utf-8"))


def circles(course, color=None, rmin=0.0, rmax=1e9):
    out = []
    for a in course.get("areas", []):
        if a.get("shape") != "circle":
            continue
        r = float(a.get("radius", 0))
        if not (rmin <= r <= rmax):
            continue
        if color and (a.get("color") or "").upper() != color.upper():
            continue
        out.append((a["center"][0], a["center"][1], r, a.get("color")))
    return out


def sheet_mid_x(course):
    return float(course["sheet"]["width"]) * 0.5


def side_filter(items, mid_x, side):
    if side == "left":
        return [i for i in items if i[0] < mid_x]
    return [i for i in items if i[0] >= mid_x]


def find_gate_positions(course, side):
    """5x5 のゲートポジション。戻りは {(X, Y): (x, y)}。"""
    mid = sheet_mid_x(course)
    dots = side_filter(circles(course, rmin=20.0, rmax=25.0), mid, side)

    xs = sorted(set(round(d[0]) for d in dots))
    ys = sorted(set(round(d[1]) for d in dots))

    # 本線の上にあるピックアップポジションが 1 個混ざるので、
    # 5 本そろっている列・行だけを格子とみなす。
    def keep(values, axis):
        counts = {}
        for d in dots:
            counts.setdefault(round(d[axis]), 0)
            counts[round(d[axis])] += 1
        return sorted(v for v in values if counts.get(v, 0) >= 5)

    xs = keep(xs, 0)
    ys = keep(ys, 1)
    if len(xs) != 5 or len(ys) != 5:
        raise SystemExit("ゲートポジションの 5x5 格子が見つかりません "
                         "(x %d 本 / y %d 本)" % (len(xs), len(ys)))

    # X は横方向。L は左から右、R は右から左（鏡像配置）。
    order_x = xs if side == "left" else list(reversed(xs))
    # Y は縦方向で 1 が上。コース座標の y は上ほど大きい。
    order_y = list(reversed(ys))

    return {(i + 1, j + 1): (order_x[i], order_y[j])
            for i in range(5) for j in range(5)}


def find_pickup(course, side, grid):
    """本線上のピックアップポジション。格子に含まれない灰色マーカ。"""
    mid = sheet_mid_x(course)
    dots = side_filter(circles(course, rmin=20.0, rmax=25.0), mid, side)
    grid_pts = {(round(x), round(y)) for x, y in grid.values()}
    rest = [d for d in dots if (round(d[0]), round(d[1])) not in grid_pts]
    if len(rest) != 1:
        raise SystemExit("ピックアップポジションを特定できません (候補 %d 個)" % len(rest))
    return rest[0][0], rest[0][1]


def find_dohyo(course, side):
    """土俵。コース上でいちばん大きい円。"""
    mid = sheet_mid_x(course)
    big = side_filter(circles(course, rmin=100.0), mid, side)
    if not big:
        raise SystemExit("土俵が見つかりません")
    big.sort(key=lambda c: -c[2])
    return big[0][0], big[0][1], big[0][2]


def find_drop_zones(course, side):
    """ドロップゾーン。200x200 の赤・青・黄のエリア。"""
    mid = sheet_mid_x(course)
    out = {}
    for a in course.get("areas", []):
        if a.get("shape") != "rect":
            continue
        w, h = a.get("size", [0, 0])
        if abs(w - 200) > 5 or abs(h - 200) > 5:
            continue
        if (a["center"][0] < mid) != (side == "left"):
            continue
        for name, hexv in LABEL_COLORS.items():
            if (a.get("color") or "").upper() == hexv.upper():
                out[name] = (a["center"][0], a["center"][1])
    return out


# --- 難所を組み立てる ---------------------------------------------------

def centre_of_mass_mm(spec, empty_g):
    """水の量から重心高さを出す。

    空のボトルの重さが分かれば水の量が決まり、重心が計算で出る。
    水は底に溜まるので重心はかなり低くなる。これが倒れやすさを決める。
    空容器そのものの重心は、実測が無いので全高の 0.45 倍に置いている
    （胴が主で、底と口が少し重い形なので中央よりわずかに下）。
    """
    water_g = spec["mass_g"] - empty_g
    if water_g <= 0:
        return spec["height"] * 0.45

    radius = spec["diameter"] * 0.5
    fill_h = (water_g * 1000.0) / (math.pi * radius * radius)   # 1g = 1000mm3
    water_com = fill_h * 0.5
    empty_com = spec["height"] * 0.45
    return (water_g * water_com + empty_g * empty_com) / spec["mass_g"]


def bottle_prop(pid, spec, color, position, com_height, label_color=None):
    p = {
        "id": pid,
        "type": "bottle",
        "color": color,
        "position": [round(position[0], 1), round(position[1], 1)],
        "size": [spec["diameter"], spec["diameter"], spec["height"]],
        "mass": round(spec["mass_g"] / 1000.0, 4),
        "movable": True,
        "center_of_mass_height_mm": round(com_height, 1),
    }
    if label_color:
        p["label_color"] = label_color
        p["label_height_mm"] = spec["label_height"]
        # ラベルは「ボトル表面がへこんでいる箇所」に巻く。下端の高さは実測で調整する。
        p["label_bottom_mm"] = round(spec["height"] * 0.30, 1)
        p["_empty_bottle_g"] = spec["empty_g"] if "empty_g" in spec else None
    return p


def gate_prop(pid, color_name, a, b):
    """隣り合う 2 つのゲートポジション a, b にまたがるゲート。

    組立図の内寸は「約240mm」だが、規約の設置方法は「二つのゲートポジション上に
    足元を設置する」。ゲートポジションの間隔は 250mm なので、脚の中心間を 250mm、
    内寸を 250 - 管の外径 = 232mm とする。設置方法の方に合わせてある
    （そして狭い方に倒しているので、通れる判定が甘くならない）。
    """
    cx = (a[0] + b[0]) * 0.5
    cy = (a[1] + b[1]) * 0.5
    span = math.hypot(a[0] - b[0], a[1] - b[1])
    inner = span - GATE_PIPE_DIAMETER
    # 脚が並ぶ向き。コース方位 0 は +y を向くので、heading 0 では脚が y 方向に並ぶ。
    # 横のゲート（x 方向に隣り合う）は heading 90。
    heading = 90.0 if abs(a[1] - b[1]) < 1.0 else 0.0
    return {
        "id": pid,
        "type": "gate",
        "color": GATE_COLORS[color_name],
        "position": [round(cx, 1), round(cy, 1)],
        "heading": heading,
        "size": [round(inner, 1), GATE_PIPE_DIAMETER, GATE_INNER_HEIGHT],
        "pipe_diameter_mm": GATE_PIPE_DIAMETER,
        "_nominal_inner_width_mm": GATE_INNER_WIDTH,
    }


def parse_gate_position(text):
    t = text.strip().upper().lstrip("G")
    try:
        x, y = t.split("-")
        return int(x), int(y)
    except ValueError:
        raise SystemExit("ゲートポジションの書き方が違います: %s (例 G2-5)" % text)


def neighbour(pos, orientation):
    """規約 表 5-2 の向きに従って、隣のゲートポジションを返す。"""
    x, y = pos
    if orientation == "yoko":       # 横 = X 方向に隣り合う
        return (x + 1, y) if x < 5 else (x - 1, y)
    return (x, y + 1) if y < 5 else (x, y - 1)


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("course")
    ap.add_argument("-o", "--output", required=True)
    ap.add_argument("--side", choices=["left", "right"], default=None,
                    help="既定はファイル名の _L / _R から決める")
    ap.add_argument("--gate", nargs=2, action="append", metavar=("COLOR", "POS"),
                    default=[], help="例: --gate red G2-5 （色の向きは規約どおり）")
    ap.add_argument("--carry-color", choices=["red", "blue", "yellow"], default="blue",
                    help="ピックアップポジションに置くキャリーボトルの色")
    ap.add_argument("--rikishi-offset", nargs=2, type=float, default=[0.0, 0.0],
                    metavar=("DX", "DY"), help="力士ボトルを土俵中心からずらす [mm]")
    ap.add_argument("--rikishi-empty-g", type=float, default=RIKISHI["empty_g"],
                    help="力士ボトル（500mL）の空の重さ [g]。重心はここから計算する")
    ap.add_argument("--carry-empty-g", type=float, default=CARRY["empty_g"],
                    help="キャリーボトル（300mL）の空の重さ [g]")
    args = ap.parse_args()

    side = args.side
    if side is None:
        side = "right" if "_R" in args.course.upper() else "left"

    course = load(args.course)
    grid = find_gate_positions(course, side)
    pickup = find_pickup(course, side, grid)
    dohyo = find_dohyo(course, side)
    zones = find_drop_zones(course, side)

    print("コース図から読み取りました (%s コース)" % ("L" if side == "left" else "R"))
    print("  土俵            : 中心 (%.0f, %.0f) 半径 %.1f" % dohyo)
    print("  ピックアップ位置   : (%.0f, %.0f)" % pickup)
    print("  ドロップゾーン     : " + ", ".join(
        "%s (%.0f, %.0f)" % (k, v[0], v[1]) for k, v in sorted(zones.items())))
    print("  ゲートポジション   : G1-1 (%.0f, %.0f) 〜 G5-5 (%.0f, %.0f)"
          % (grid[(1, 1)][0], grid[(1, 1)][1], grid[(5, 5)][0], grid[(5, 5)][1]))

    props = [p for p in course.get("props", [])
             if not p.get("id", "").startswith(("rikishi", "carry", "gate_"))]

    # ET相撲
    RIKISHI["empty_g"] = args.rikishi_empty_g
    CARRY["empty_g"] = args.carry_empty_g
    rikishi_com = centre_of_mass_mm(RIKISHI, args.rikishi_empty_g)
    carry_com = centre_of_mass_mm(CARRY, args.carry_empty_g)
    print("  重心 (水の量から計算) : 力士 %.1fmm / キャリー %.1fmm" % (rikishi_com, carry_com))

    props.append(bottle_prop(
        "rikishi_bottle", RIKISHI, "#F2F2F2",
        (dohyo[0] + args.rikishi_offset[0], dohyo[1] + args.rikishi_offset[1]),
        rikishi_com, RIKISHI["label_color"]))

    # ボトルデリバリー
    props.append(bottle_prop(
        "carry_bottle", CARRY, "#F2F2F2", pickup,
        carry_com, LABEL_COLORS[args.carry_color]))

    # ETラリー
    for color, pos_text in args.gate:
        color = color.lower()
        if color not in GATE_COLORS:
            raise SystemExit("ゲートの色は red / blue / yellow です: %s" % color)
        a = parse_gate_position(pos_text)
        b = neighbour(a, GATE_ORIENTATION[color])
        if a not in grid or b not in grid:
            raise SystemExit("ゲートポジションが範囲外です: G%d-%d / G%d-%d"
                             % (a[0], a[1], b[0], b[1]))
        props.append(gate_prop("gate_" + color, color, grid[a], grid[b]))
        print("  ゲート %-6s : G%d-%d + G%d-%d  (%s)"
              % (color, a[0], a[1], b[0], b[1],
                 "横" if GATE_ORIENTATION[color] == "yoko" else "縦"))

    out = copy.deepcopy(course)
    out["props"] = props
    io.open(args.output, "w", encoding="utf-8", newline="\n").write(
        json.dumps(out, ensure_ascii=False, indent=1))
    print("%s を書き出しました (props %d 個)" % (args.output, len(props)))
    return 0


if __name__ == "__main__":
    sys.exit(main())
