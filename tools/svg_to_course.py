#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
svg_to_course.py : SVG のパスをコース定義 JSON に変換する。

公式配布のコース図（PDF）を Inkscape 等で SVG にして、ライン中心線を
パスとしてトレースすれば、そのままシミュレータのコースにできる。
コース定義のセグメント表現は SVG のパスコマンドと同じ意味に揃えてあるので、
ここでは座標変換と単位合わせだけを行い、形は変えない。

使い方:
    python tools/svg_to_course.py course.svg -o course/etrobo_2026.json \\
        --line-width 20 --sheet 4000x2000

    # 特定の id のパスだけを本線として取り込む
    python tools/svg_to_course.py course.svg --path-id main -o out.json

注意:
  - SVG は Y 軸が下向き、コース定義は上向きなので既定で反転する (--no-flip-y で無効)。
  - transform 属性は translate / scale / matrix のみ解釈する。
    Inkscape で「変形をパスに適用」してから書き出すのが確実。
  - 単位は viewBox の座標をそのまま mm とみなす。違う場合は --scale を使う。
"""

from __future__ import annotations

import argparse
import json
import math
import re
import sys
import xml.etree.ElementTree as ET

SVG_NS = "{http://www.w3.org/2000/svg}"

# パスコマンドと数値を切り出す
TOKEN_RE = re.compile(r"[MmZzLlHhVvCcSsQqTtAa]|[-+]?(?:\d*\.\d+|\d+\.?)(?:[eE][-+]?\d+)?")


def tokenize(d: str):
    return TOKEN_RE.findall(d)


class PathParser:
    """SVG のパスデータを (始点, セグメント列) へ展開する。"""

    def __init__(self, d: str):
        self.tokens = tokenize(d)
        self.index = 0
        self.current = (0.0, 0.0)
        self.start = (0.0, 0.0)
        self.prev_cubic_ctrl = None
        self.prev_quad_ctrl = None
        self.subpaths = []      # [(start, [segment, ...], closed)]
        self._segments = []
        self._has_subpath = False

    def _number(self):
        tok = self.tokens[self.index]
        self.index += 1
        return float(tok)

    def _point(self, relative):
        x = self._number()
        y = self._number()
        if relative:
            return (self.current[0] + x, self.current[1] + y)
        return (x, y)

    def _peek_is_number(self):
        return (self.index < len(self.tokens)
                and not self.tokens[self.index].isalpha())

    def _flush(self, closed):
        if self._has_subpath and self._segments:
            self.subpaths.append((self.start, self._segments, closed))
        self._segments = []

    def parse(self):
        command = None
        while self.index < len(self.tokens):
            tok = self.tokens[self.index]

            if tok.isalpha():
                command = tok
                self.index += 1
            elif command is None:
                raise ValueError(f"パスがコマンドで始まっていません: {tok}")
            elif command in "Mm":
                # M のあとに座標が続く場合、2 個目以降は L 扱い（SVG の規定）
                command = "L" if command == "M" else "l"

            self._dispatch(command)

        self._flush(closed=False)
        return self.subpaths

    def _dispatch(self, command):
        rel = command.islower()
        c = command.upper()

        if c == "M":
            self._flush(closed=False)
            self.current = self._point(rel)
            self.start = self.current
            self._has_subpath = True
            self.prev_cubic_ctrl = None
            self.prev_quad_ctrl = None

        elif c == "Z":
            self._flush(closed=True)
            self.current = self.start
            self._has_subpath = False

        elif c == "L":
            self.current = self._point(rel)
            self._segments.append({"type": "line", "to": self.current})
            self.prev_cubic_ctrl = self.prev_quad_ctrl = None

        elif c == "H":
            x = self._number()
            self.current = ((self.current[0] + x) if rel else x, self.current[1])
            self._segments.append({"type": "line", "to": self.current})
            self.prev_cubic_ctrl = self.prev_quad_ctrl = None

        elif c == "V":
            y = self._number()
            self.current = (self.current[0], (self.current[1] + y) if rel else y)
            self._segments.append({"type": "line", "to": self.current})
            self.prev_cubic_ctrl = self.prev_quad_ctrl = None

        elif c == "C":
            c1 = self._point(rel)
            c2 = self._point(rel)
            end = self._point(rel)
            self._segments.append({"type": "cubic", "c1": c1, "c2": c2, "to": end})
            self.current = end
            self.prev_cubic_ctrl = c2
            self.prev_quad_ctrl = None

        elif c == "S":
            # 直前の制御点を反射させる
            c1 = self._reflect(self.prev_cubic_ctrl)
            c2 = self._point(rel)
            end = self._point(rel)
            self._segments.append({"type": "cubic", "c1": c1, "c2": c2, "to": end})
            self.current = end
            self.prev_cubic_ctrl = c2
            self.prev_quad_ctrl = None

        elif c == "Q":
            ctrl = self._point(rel)
            end = self._point(rel)
            self._segments.append({"type": "quad", "c": ctrl, "to": end})
            self.current = end
            self.prev_quad_ctrl = ctrl
            self.prev_cubic_ctrl = None

        elif c == "T":
            ctrl = self._reflect(self.prev_quad_ctrl)
            end = self._point(rel)
            self._segments.append({"type": "quad", "c": ctrl, "to": end})
            self.current = end
            self.prev_quad_ctrl = ctrl
            self.prev_cubic_ctrl = None

        elif c == "A":
            rx = self._number()
            ry = self._number()
            self._number()                      # x-axis-rotation
            large = self._number() != 0
            sweep = self._number() != 0
            end = self._point(rel)

            if abs(rx - ry) > 1e-6:
                # 楕円弧はコース定義に無いので、真円に丸めたことを知らせる
                print(f"  警告: 楕円弧 (rx={rx}, ry={ry}) を半径 {(rx + ry) / 2:.1f} の"
                      f"円弧に丸めました", file=sys.stderr)

            self._segments.append({
                "type": "arc",
                "to": end,
                "r": (rx + ry) / 2.0,
                "large": large,
                "sweep": sweep,
            })
            self.current = end
            self.prev_cubic_ctrl = self.prev_quad_ctrl = None

        else:
            raise ValueError(f"未対応のパスコマンド: {command}")

    def _reflect(self, ctrl):
        if ctrl is None:
            return self.current
        return (2 * self.current[0] - ctrl[0], 2 * self.current[1] - ctrl[1])


def parse_transform(text):
    """translate / scale / matrix を 1 つの行列 (a, b, c, d, e, f) にまとめる。"""
    if not text:
        return (1.0, 0.0, 0.0, 1.0, 0.0, 0.0)

    result = (1.0, 0.0, 0.0, 1.0, 0.0, 0.0)
    for name, args in re.findall(r"(\w+)\s*\(([^)]*)\)", text):
        nums = [float(v) for v in re.findall(r"[-+]?[\d.eE+-]+", args)]
        if name == "translate":
            m = (1.0, 0.0, 0.0, 1.0, nums[0], nums[1] if len(nums) > 1 else 0.0)
        elif name == "scale":
            sx = nums[0]
            sy = nums[1] if len(nums) > 1 else sx
            m = (sx, 0.0, 0.0, sy, 0.0, 0.0)
        elif name == "matrix" and len(nums) >= 6:
            m = tuple(nums[:6])
        else:
            print(f"  警告: transform '{name}' は無視しました", file=sys.stderr)
            continue
        result = multiply(result, m)
    return result


def multiply(m1, m2):
    a1, b1, c1, d1, e1, f1 = m1
    a2, b2, c2, d2, e2, f2 = m2
    return (a1 * a2 + c1 * b2,
            b1 * a2 + d1 * b2,
            a1 * c2 + c1 * d2,
            b1 * c2 + d1 * d2,
            a1 * e2 + c1 * f2 + e1,
            b1 * e2 + d1 * f2 + f1)


def apply(m, point):
    a, b, c, d, e, f = m
    x, y = point
    return (a * x + c * y + e, b * x + d * y + f)


def collect_paths(root, path_id=None):
    """SVG 中のパスを (id, d, transform) で集める。祖先の transform も畳み込む。"""
    found = []

    def walk(node, parent_matrix):
        matrix = multiply(parent_matrix, parse_transform(node.get("transform")))
        tag = node.tag
        if tag == SVG_NS + "path" or tag == "path":
            d = node.get("d")
            if d:
                found.append((node.get("id") or f"path{len(found)}", d, matrix))
        for child in node:
            walk(child, matrix)

    walk(root, (1.0, 0.0, 0.0, 1.0, 0.0, 0.0))

    if path_id:
        found = [p for p in found if p[0] == path_id]
    return found


def convert(args):
    tree = ET.parse(args.svg)
    root = tree.getroot()

    paths = collect_paths(root, args.path_id)
    if not paths:
        print(f"パスが見つかりません (--path-id {args.path_id})" if args.path_id
              else "SVG にパスがありません", file=sys.stderr)
        return 1

    print(f"{len(paths)} 本のパスを読み込みます")

    # シート寸法
    if args.sheet:
        sheet_w, sheet_h = (float(v) for v in args.sheet.lower().split("x"))
    else:
        vb = root.get("viewBox")
        if vb:
            _, _, sheet_w, sheet_h = (float(v) for v in vb.replace(",", " ").split())
            sheet_w *= args.scale
            sheet_h *= args.scale
        else:
            sheet_w, sheet_h = 4000.0, 2000.0
            print("  警告: viewBox が無いのでシートを 4000x2000mm としました", file=sys.stderr)

    def to_course(point):
        x, y = point
        x *= args.scale
        y *= args.scale
        if args.flip_y:
            y = sheet_h - y
        return [round(x, 2), round(y, 2)]

    out_paths = []
    for name, d, matrix in paths:
        try:
            subpaths = PathParser(d).parse()
        except ValueError as e:
            print(f"  '{name}' を解釈できませんでした: {e}", file=sys.stderr)
            continue

        for i, (start, segments, closed) in enumerate(subpaths):
            converted = []
            for seg in segments:
                s = {"type": seg["type"], "to": to_course(apply(matrix, seg["to"]))}
                if seg["type"] == "cubic":
                    s["c1"] = to_course(apply(matrix, seg["c1"]))
                    s["c2"] = to_course(apply(matrix, seg["c2"]))
                elif seg["type"] == "quad":
                    s["c"] = to_course(apply(matrix, seg["c"]))
                elif seg["type"] == "arc":
                    # 行列のスケールぶんだけ半径も変える
                    a, b, c, dd = matrix[0], matrix[1], matrix[2], matrix[3]
                    scale = math.sqrt(abs(a * dd - b * c)) or 1.0
                    s["r"] = round(seg["r"] * scale * args.scale, 2)
                    s["large"] = 1 if seg["large"] else 0
                    # Y を反転すると回り方が逆になる
                    sweep = seg["sweep"] != args.flip_y
                    s["sweep"] = 1 if sweep else 0
                converted.append(s)

            if not converted:
                continue

            out_paths.append({
                "id": name if len(subpaths) == 1 else f"{name}_{i}",
                "color": args.color,
                "width": args.line_width,
                "closed": closed,
                "start": to_course(apply(matrix, start)),
                "segments": converted,
            })

    if not out_paths:
        print("変換できたパスがありません", file=sys.stderr)
        return 1

    first = out_paths[0]
    course = {
        "name": args.name or f"{args.svg} から変換",
        "note": "svg_to_course.py で生成。難所やゲートは手で追加すること。",
        "units": "mm",
        "sheet": {"width": round(sheet_w, 1), "height": round(sheet_h, 1), "color": args.sheet_color},
        "paths": out_paths,
        "areas": [],
        "props": [],
        "gates": [],
        "start_pose": {"position": first["start"], "heading": 0},
    }

    with open(args.output, "w", encoding="utf-8", newline="\n") as f:
        json.dump(course, f, ensure_ascii=False, indent=2)
        f.write("\n")

    total = sum(len(p["segments"]) for p in out_paths)
    print(f"{args.output} を書き出しました "
          f"(パス {len(out_paths)} 本 / セグメント {total} 個 / "
          f"シート {sheet_w:.0f}x{sheet_h:.0f}mm)")
    print("スタート姿勢・ゲート・難所は生成されないので、course/README.md を見て手で足してください。")
    return 0


def main():
    parser = argparse.ArgumentParser(description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("svg", help="入力の SVG ファイル")
    parser.add_argument("-o", "--output", default="course_from_svg.json", help="出力する JSON")
    parser.add_argument("--path-id", help="この id のパスだけを取り込む")
    parser.add_argument("--name", help="コース名")
    parser.add_argument("--line-width", type=float, default=20.0, help="線幅 [mm] (既定 20)")
    parser.add_argument("--color", default="#101010", help="線の色 (既定 #101010)")
    parser.add_argument("--sheet-color", default="#F2F2F0", help="シートの色")
    parser.add_argument("--sheet", help="シート寸法を WxH [mm] で明示する (例 4000x2000)")
    parser.add_argument("--scale", type=float, default=1.0,
                        help="SVG 座標から mm への倍率 (既定 1.0)")
    parser.add_argument("--no-flip-y", dest="flip_y", action="store_false",
                        help="Y 軸の反転をしない")
    parser.set_defaults(flip_y=True)

    sys.exit(convert(parser.parse_args()))


if __name__ == "__main__":
    main()
