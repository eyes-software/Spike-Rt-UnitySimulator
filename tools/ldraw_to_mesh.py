#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
ldraw_to_mesh.py : LDraw の組立図データから走行体の表示用メッシュを作る。

ETロボコンの走行体は LDraw 形式で組立図が公開されているので、
外観を「それらしく作る」のではなく、実際の部品そのものを描ける。

    https://github.com/ETrobocon/HackSPi_BuildingInstructions
    https://library.ldraw.org/library/updates/complete.zip  (部品ライブラリ)

出力は Unity が起動時に読む独自形式 (.etmesh)。FBX などの外部アセットを
使わない方針は崩さず、寸法データと同じ扱いで StreamingAssets に置く。

使い方:
    python tools/ldraw_to_mesh.py HackSPi.mpd -o robot_rehacks_2026.etmesh \\
        --model ReHackSM6.ldr --ldraw-lib /path/to/ldraw

座標系:
    LDraw は X 右 / Y 下 / Z 奥 の右手系で 1 LDU = 0.4mm。
    このモデルでは +X が左、-Z が前。Unity 側の走行体ローカルは
    X 右 / Y 上 / Z 前 なので、3 軸とも符号を反転して渡す。
    原点は左右中心・車軸中心・車軸高さ（Unity 側 RobotController と同じ）。
"""

from __future__ import annotations

import argparse
import functools
import io
import os
import struct
import sys

LDU_MM = 0.4

# 車軸の位置 [LDU]。タイヤ原点 y=38、車軸面 z=-116。
AXLE_Y_LDU = 38.0
AXLE_Z_LDU = -116.0


# ---------------------------------------------------------------- 読み込み

def load_mpd(path):
    """マルチパートの .mpd を {ファイル名: 行} に割る。"""
    files, cur = {}, None
    for raw in io.open(path, encoding="utf-8", errors="replace"):
        line = raw.rstrip("\r\n")
        if line.strip().lower().startswith("0 file "):
            cur = line.strip()[7:].strip().lower()
            files[cur] = []
            continue
        if cur is not None:
            files[cur].append(line)
    if not files:
        files[os.path.basename(path).lower()] = io.open(
            path, encoding="utf-8", errors="replace").read().splitlines()
    return files


class Library:
    """mpd 内のサブモデルと、LDraw 部品ライブラリの両方から行を引く。"""

    def __init__(self, mpd_files, lib_root):
        self.mpd = mpd_files
        self.dirs = []
        if lib_root:
            for sub in ("parts", "p", os.path.join("parts", "s"),
                        os.path.join("p", "48"), os.path.join("p", "8"), ""):
                d = os.path.join(lib_root, sub) if sub else lib_root
                if os.path.isdir(d):
                    self.dirs.append(d)

    @functools.lru_cache(maxsize=None)
    def read(self, name):
        name = name.lower().replace("\\", "/")
        if name in self.mpd:
            return self.mpd[name]
        for d in self.dirs:
            p = os.path.join(d, *name.split("/"))
            if os.path.exists(p):
                return io.open(p, encoding="utf-8", errors="replace").read().splitlines()
        return None


def load_colours(lib_root):
    """LDConfig.ldr から色番号 -> RGBA を作る。"""
    table = {}
    path = os.path.join(lib_root, "LDConfig.ldr")
    if not os.path.exists(path):
        return table
    for line in io.open(path, encoding="utf-8", errors="replace"):
        p = line.split()
        if len(p) < 4 or p[0] != "0" or p[1] != "!COLOUR":
            continue
        try:
            code = int(p[p.index("CODE") + 1])
            value = p[p.index("VALUE") + 1]
        except (ValueError, IndexError):
            continue
        alpha = 255
        if "ALPHA" in p:
            try:
                alpha = int(p[p.index("ALPHA") + 1])
            except (ValueError, IndexError):
                pass
        v = value.lstrip("#")
        if len(v) != 6:
            continue
        table[code] = (int(v[0:2], 16), int(v[2:4], 16), int(v[4:6], 16), alpha)
    return table


# ---------------------------------------------------------------- 行列

def mat_mul(m, n):
    return tuple(tuple(sum(m[i][k] * n[k][j] for k in range(3)) for j in range(3))
                 for i in range(3))


def mat_apply(m, v):
    return tuple(sum(m[i][k] * v[k] for k in range(3)) for i in range(3))


def mat_det(m):
    return (m[0][0] * (m[1][1] * m[2][2] - m[1][2] * m[2][1])
            - m[0][1] * (m[1][0] * m[2][2] - m[1][2] * m[2][0])
            + m[0][2] * (m[1][0] * m[2][1] - m[1][1] * m[2][0]))


# ---------------------------------------------------------------- 展開

class Builder:
    """三角形を「まとまり」ごとに集める。

    まとまりは body / wheel_l / wheel_r / arm の 4 つ。車輪とアームは
    Unity 側で回るので、それぞれの回転中心を原点にして出す。
    """

    WHEEL_PARTS = {"39367p01.dat"}
    ARM_FILES = {"hackspi_dachsm5_arm.ldr"}

    # 回転中心 [LDU]（モデル座標）。車輪は x のみ実際の値を使う。
    ARM_PIVOT_LDU = (0.0, -22.0, -196.0)     # y=-8.8mm, z=-78.4mm

    def __init__(self, lib, colours, flip_winding):
        self.lib = lib
        self.colours = colours
        self.flip_winding = flip_winding
        self.groups = {}     # name -> {colour: [(v0,v1,v2), ...]}

    # -- 座標変換 ---------------------------------------------------
    @staticmethod
    def to_robot(v):
        """LDraw [LDU] -> 走行体ローカル [m]。X 右 / Y 上 / Z 前。"""
        return (-v[0] * LDU_MM * 0.001,
                -(v[1] - AXLE_Y_LDU) * LDU_MM * 0.001,
                -(v[2] - AXLE_Z_LDU) * LDU_MM * 0.001)

    def add(self, group, colour, tri, invert):
        a, b, c = (self.to_robot(p) for p in tri)
        if invert != self.flip_winding:
            b, c = c, b
        self.groups.setdefault(group, {}).setdefault(colour, []).append((a, b, c))

    # -- 再帰 -------------------------------------------------------
    def walk(self, name, pos, mat, colour, invert, group):
        lines = self.lib.read(name)
        if lines is None:
            return

        ccw = True            # BFC の巻き方向。既定は CCW
        invert_next = False

        for line in lines:
            p = line.split()
            if not p:
                continue

            if p[0] == "0":
                if len(p) >= 3 and p[1].upper() == "BFC":
                    for token in (t.upper() for t in p[2:]):
                        if token == "CW":
                            ccw = False
                        elif token == "CCW":
                            ccw = True
                        elif token == "INVERTNEXT":
                            invert_next = True
                continue

            if p[0] == "1" and len(p) >= 15:
                c = int(p[1])
                c = colour if c == 16 else c
                x, y, z = float(p[2]), float(p[3]), float(p[4])
                m = ((float(p[5]), float(p[6]), float(p[7])),
                     (float(p[8]), float(p[9]), float(p[10])),
                     (float(p[11]), float(p[12]), float(p[13])))
                sub = " ".join(p[14:]).strip().lower()

                wpos = tuple(pos[k] + mat_apply(mat, (x, y, z))[k] for k in range(3))
                wmat = mat_mul(mat, m)

                sub_invert = invert ^ invert_next ^ (mat_det(m) < 0)
                invert_next = False

                sub_group = group
                if sub in self.ARM_FILES:
                    sub_group = "arm"
                elif sub in self.WHEEL_PARTS:
                    sub_group = "wheel_l" if wpos[0] > 0 else "wheel_r"

                self.walk(sub, wpos, wmat, c, sub_invert, sub_group)
                continue

            if p[0] in ("3", "4"):
                c = int(p[1])
                c = colour if c == 16 else c
                n = 3 if p[0] == "3" else 4
                pts = []
                for i in range(n):
                    v = (float(p[2 + i * 3]), float(p[3 + i * 3]), float(p[4 + i * 3]))
                    pts.append(tuple(pos[k] + mat_apply(mat, v)[k] for k in range(3)))
                flip = invert ^ (not ccw)
                self.add(group, c, (pts[0], pts[1], pts[2]), flip)
                if n == 4:
                    self.add(group, c, (pts[0], pts[2], pts[3]), flip)


# ---------------------------------------------------------------- 書き出し

def write_mesh(path, builder, colours):
    order = [g for g in ("body", "wheel_l", "wheel_r", "arm") if g in builder.groups]

    out = io.open(path, "wb")
    out.write(b"ETMS")
    out.write(struct.pack("<II", 1, len(order)))

    total_tris = 0
    for name in order:
        by_colour = builder.groups[name]

        # 回転中心を原点にずらす
        if name == "arm":
            o = Builder.to_robot(Builder.ARM_PIVOT_LDU)
        elif name.startswith("wheel"):
            xs = [v[0] for tris in by_colour.values() for t in tris for v in t]
            o = ((min(xs) + max(xs)) * 0.5, 0.0, 0.0)
        else:
            o = (0.0, 0.0, 0.0)

        # 頂点をまとめる（同じ座標は 1 つに）
        index_of = {}
        verts = []
        subs = []
        for colour, tris in sorted(by_colour.items()):
            idx = []
            for tri in tris:
                for v in tri:
                    key = (round(v[0] - o[0], 6), round(v[1] - o[1], 6), round(v[2] - o[2], 6))
                    i = index_of.get(key)
                    if i is None:
                        i = len(verts)
                        index_of[key] = i
                        verts.append(key)
                    idx.append(i)
            subs.append((colour, idx))
            total_tris += len(tris)

        raw = name.encode("ascii")
        out.write(struct.pack("<B", len(raw)))
        out.write(raw)
        out.write(struct.pack("<fff", *o))
        out.write(struct.pack("<I", len(verts)))
        out.write(struct.pack("<%df" % (len(verts) * 3),
                              *[c for v in verts for c in v]))
        out.write(struct.pack("<I", len(subs)))
        for colour, idx in subs:
            rgba = colours.get(colour, (160, 160, 160, 255))
            out.write(struct.pack("<BBBB", *rgba))
            out.write(struct.pack("<I", len(idx)))
            out.write(struct.pack("<%dI" % len(idx), *idx))

        print("  %-8s 頂点 %7d  色 %2d  三角形 %7d" %
              (name, len(verts), len(subs), sum(len(i) for _, i in subs) // 3))

    out.close()
    return total_tris


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("mpd", help="HackSPi.mpd などのモデルファイル")
    ap.add_argument("-o", "--output", required=True)
    ap.add_argument("--model", default="ReHackSM6.ldr", help="mpd 内の最上位モデル名")
    ap.add_argument("--ldraw-lib", required=True, help="LDraw 部品ライブラリの ldraw ディレクトリ")
    ap.add_argument("--flip-winding", action="store_true",
                    help="表裏が逆に見えるときに指定する")
    args = ap.parse_args()

    lib = Library(load_mpd(args.mpd), args.ldraw_lib)
    colours = load_colours(args.ldraw_lib)
    if not colours:
        print("警告: LDConfig.ldr が読めませんでした。色は灰色になります", file=sys.stderr)

    b = Builder(lib, colours, args.flip_winding)
    b.walk(args.model.lower(), (0.0, 0.0, 0.0),
           ((1, 0, 0), (0, 1, 0), (0, 0, 1)), 16, False, "body")

    if not b.groups:
        print("モデルを展開できませんでした: %s" % args.model, file=sys.stderr)
        return 1

    print("%s を書き出します" % args.output)
    total = write_mesh(args.output, b, colours)
    print("  合計 %d 三角形  %.1f MB" % (total, os.path.getsize(args.output) / 1e6))
    return 0


if __name__ == "__main__":
    sys.exit(main())
