#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
pdf_to_course.py : 公式コース図 PDF からコース定義 JSON を作る。

PDF がベクタ図なら、線は「中心線 + 線幅」、面は「塗りパス」として入っている。
画像をトレースするより正確で、曲線もベジエのまま取り出せる。
ここでやるのは座標系の変換（Y 反転・縮尺）と、要る要素の選別だけ。

描画順は PDF の順序をそのまま z に入れる。コースによっては
「黒いラインの上にカラーサークルが乗る」ような重なりがあり、
順序を保たないとカラーセンサが読む色が変わってしまう。

必要なもの: PyMuPDF (`pip install pymupdf`)

使い方:
    # まず中身を見る
    python tools/pdf_to_course.py コース図.pdf --inspect

    # 変換（縮尺 1/2 の図面 -> 実寸）
    python tools/pdf_to_course.py コース図.pdf -o course/etrobo_2026.json --scale 2.0

    # L/R が 1 枚に入っている図面から片方だけ取る
    python tools/pdf_to_course.py コース図.pdf -o course/etrobo_2026_L.json \\
        --scale 2.0 --side left
"""

from __future__ import annotations

import argparse
import collections
import json
import math
import os
import sys

try:
    import pymupdf
except ImportError:
    try:
        import fitz as pymupdf
    except ImportError:
        print("PyMuPDF が必要です:  pip install pymupdf", file=sys.stderr)
        sys.exit(1)

PT_TO_MM = 25.4 / 72.0


def rgb_hex(color):
    if color is None:
        return None
    return "%02X%02X%02X" % tuple(int(round(v * 255)) for v in color)


# ---------------------------------------------------------------- 調査

def inspect(page):
    groups = collections.defaultdict(lambda: {"paths": 0, "segs": 0, "max_area": 0.0, "bbox": None})

    for d in page.get_drawings():
        key = (rgb_hex(d.get("color")), rgb_hex(d.get("fill")),
               round((d.get("width") or 0) * PT_TO_MM, 2))
        g = groups[key]
        g["paths"] += 1
        g["segs"] += len(d["items"])
        r = d["rect"]
        box = (r.x0 * PT_TO_MM, r.y0 * PT_TO_MM, r.x1 * PT_TO_MM, r.y1 * PT_TO_MM)
        g["max_area"] = max(g["max_area"], (box[2] - box[0]) * (box[3] - box[1]))
        g["bbox"] = box if g["bbox"] is None else (
            min(g["bbox"][0], box[0]), min(g["bbox"][1], box[1]),
            max(g["bbox"][2], box[2]), max(g["bbox"][3], box[3]))

    print("線色      塗り色    線幅mm   パス数   セグ数  最大面積mm2  外接範囲 [mm @図面]")
    print("-" * 100)
    for key, g in sorted(groups.items(), key=lambda kv: -kv[1]["max_area"]):
        stroke, fill, width = key
        b = g["bbox"]
        print("%-9s %-9s %6.2f  %7d %7d  %11.0f  [%7.1f %7.1f]-[%7.1f %7.1f]"
              % (stroke or "-", fill or "-", width, g["paths"], g["segs"],
                 g["max_area"], b[0], b[1], b[2], b[3]))

    print()
    print("塗りなしで線幅が本線幅のものが走行ライン、")
    print("ページ全体を覆う塗りがマットの地色です。")


# ---------------------------------------------------------------- 幾何

def split_subpaths(items, tol=0.05):
    """連続していないところでサブパスに切る。"""
    subpaths, current, cursor = [], [], None

    for it in items:
        op = it[0]
        if op == "l":
            p0, p1 = it[1], it[2]
        elif op == "c":
            p0, p1 = it[1], it[4]
        elif op == "re":
            if current:
                subpaths.append(current)
                current = []
            subpaths.append([it])
            cursor = None
            continue
        elif op == "qu":
            continue
        else:
            continue

        if cursor is None or abs(p0.x - cursor.x) > tol or abs(p0.y - cursor.y) > tol:
            if current:
                subpaths.append(current)
            current = []
        current.append(it)
        cursor = p1

    if current:
        subpaths.append(current)
    return subpaths


def looks_like_circle(items, rect):
    """4 本のベジエでほぼ正方形の範囲 = 円とみなす。"""
    if len(items) != 4 or any(it[0] != "c" for it in items):
        return False
    w, h = rect.width, rect.height
    if w <= 0 or h <= 0:
        return False
    return abs(w - h) / max(w, h) < 0.02


class Frame:
    """PDF 座標 (pt, Y 下向き) -> コース座標 (mm, Y 上向き)。"""

    def __init__(self, page_height_pt, scale, flip_y=True):
        self.page_height_mm = page_height_pt * PT_TO_MM
        self.scale = scale
        self.flip_y = flip_y

    def pt(self, p):
        x = p.x * PT_TO_MM
        y = p.y * PT_TO_MM
        if self.flip_y:
            y = self.page_height_mm - y
        return [round(x * self.scale, 2), round(y * self.scale, 2)]

    def length(self, v_mm_drawing):
        return round(v_mm_drawing * self.scale, 2)


def flatten_items(items, frame, steps=12):
    """塗りパスを多角形へ。"""
    pts = []
    for it in items:
        if it[0] == "re":
            r = it[1]
            corners = [(r.x0, r.y0), (r.x1, r.y0), (r.x1, r.y1), (r.x0, r.y1)]
            # PyMuPDF は ("re", Rect, 向き) を返す。向き -1 は逆回り。
            # 非ゼロ回転数規則では向きが穴かどうかを決めるので、落とせない。
            # 「ロ」の穴はこの逆回りの矩形 1 つでできている。
            if len(it) > 2 and it[2] == -1:
                corners.reverse()
            for corner in corners:
                pts.append(frame.pt(pymupdf.Point(*corner)))
        elif it[0] == "l":
            if not pts:
                pts.append(frame.pt(it[1]))
            pts.append(frame.pt(it[2]))
        elif it[0] == "c":
            p0, c1, c2, p3 = it[1], it[2], it[3], it[4]
            if not pts:
                pts.append(frame.pt(p0))
            for i in range(1, steps + 1):
                t = i / steps
                u = 1 - t
                x = u**3 * p0.x + 3*u*u*t * c1.x + 3*u*t*t * c2.x + t**3 * p3.x
                y = u**3 * p0.y + 3*u*u*t * c1.y + 3*u*t*t * c2.y + t**3 * p3.y
                pts.append(frame.pt(pymupdf.Point(x, y)))

    # 重複点を落とす
    out = []
    for p in pts:
        if not out or abs(p[0] - out[-1][0]) > 0.01 or abs(p[1] - out[-1][1]) > 0.01:
            out.append(p)
    return out


# ---------------------------------------------------------------- 変換

def convert(args):
    doc = pymupdf.open(args.pdf)
    page = doc[args.page - 1]

    if args.inspect:
        r = page.rect
        print("ページ %d: %.1f x %.1f mm (図面上)"
              % (args.page, r.width * PT_TO_MM, r.height * PT_TO_MM))
        print()
        inspect(page)
        return 0

    frame = Frame(page.rect.height, args.scale, flip_y=not args.no_flip_y)
    sheet_w = page.rect.width * PT_TO_MM * args.scale
    sheet_h = page.rect.height * PT_TO_MM * args.scale
    mid_x = sheet_w / 2.0

    def allowed(colors, value):
        if not colors:
            return True
        return value in colors

    fill_allow = {c.upper().lstrip("#") for c in args.fill_color} if args.fill_color else None
    stroke_allow = {c.upper().lstrip("#") for c in args.line_color} if args.line_color else None

    drawings = page.get_drawings()

    # ページ全体を覆う塗り = マットの地色。面としては出さず sheet color にする。
    sheet_color = args.sheet_color
    page_area = (page.rect.width * PT_TO_MM) * (page.rect.height * PT_TO_MM)
    background_index = None
    for i, d in enumerate(drawings):
        fill = rgb_hex(d.get("fill"))
        if fill is None:
            continue
        r = d["rect"]
        area = (r.width * PT_TO_MM) * (r.height * PT_TO_MM)
        if area > page_area * 0.9:
            sheet_color = "#" + fill
            background_index = i
            break

    areas, paths = [], []
    skipped_small = 0

    for z, d in enumerate(drawings):
        if z == background_index:
            continue

        r = d["rect"]
        area_mm2 = (r.width * PT_TO_MM) * (r.height * PT_TO_MM)
        fill = rgb_hex(d.get("fill"))
        stroke = rgb_hex(d.get("color"))
        width_mm = (d.get("width") or 0) * PT_TO_MM

        # 片側だけ取る
        cx = (r.x0 + r.x1) * 0.5 * PT_TO_MM * args.scale
        if args.side == "left" and cx > mid_x:
            continue
        if args.side == "right" and cx < mid_x:
            continue

        # --- 塗り -> area ---
        if fill is not None and allowed(fill_allow, fill):
            if area_mm2 < args.fill_min_area:
                skipped_small += 1
            else:
                # 1 つの塗りパスに含まれる輪郭はまとめて 1 つの領域にする。
                # 文字やロゴは外側の輪郭と内側の輪郭（穴）でできているので、
                # 輪郭ごとに別の塗りにすると穴が埋まって「ロ」が「■」になる。
                polygon_contours = []

                # PDF の塗り規則をそのまま持っていく。既定は非ゼロ回転数規則で、
                # 向きが逆の輪郭だけが穴になる。矢印などは同じ向きの輪郭が
                # 重なっているので、偶奇規則で塗ると穴があいてしまう。
                fill_rule = "evenodd" if d.get("even_odd") else "nonzero"

                subpaths = [sp for sp in split_subpaths(d["items"]) if sp]

                # 輪郭が 2 本以上あるパスは、矩形や円の輪郭も含めてすべてを
                # 1 つの多角形の輪郭として扱う。「ロ」の穴は矩形 (re) で
                # 描かれているので、これを別の塗りにすると穴が埋まる。
                # 輪郭が 1 本だけのときは矩形・円のまま出す（軽いので）。
                single = len(subpaths) == 1

                for sub_index, items in enumerate(subpaths):
                    sub_rect = pymupdf.Rect(items[0][1]) if items[0][0] == "re" else r

                    if not single:
                        poly = flatten_items(items, frame)
                        if len(poly) >= 3:
                            polygon_contours.append(poly)
                    elif items[0][0] == "re":
                        rr = items[0][1]
                        center = frame.pt(pymupdf.Point((rr.x0 + rr.x1) / 2, (rr.y0 + rr.y1) / 2))
                        areas.append({
                            "id": "fill_%d_%d" % (z, sub_index),
                            "z": z,
                            "shape": "rect",
                            "color": "#" + fill,
                            "center": center,
                            "size": [frame.length(rr.width * PT_TO_MM),
                                     frame.length(rr.height * PT_TO_MM)],
                        })
                    elif looks_like_circle(items, sub_rect):
                        center = frame.pt(pymupdf.Point((sub_rect.x0 + sub_rect.x1) / 2,
                                                        (sub_rect.y0 + sub_rect.y1) / 2))
                        areas.append({
                            "id": "fill_%d_%d" % (z, sub_index),
                            "z": z,
                            "shape": "circle",
                            "color": "#" + fill,
                            "center": center,
                            "radius": frame.length(sub_rect.width * PT_TO_MM / 2),
                        })
                    else:
                        poly = flatten_items(items, frame)
                        if len(poly) >= 3:
                            polygon_contours.append(poly)

                if len(polygon_contours) == 1:
                    areas.append({
                        "id": "fill_%d" % z,
                        "z": z,
                        "shape": "polygon",
                        "color": "#" + fill,
                        "points": polygon_contours[0],
                    })
                elif polygon_contours:
                    areas.append({
                        "id": "fill_%d" % z,
                        "z": z,
                        "shape": "polygon",
                        "color": "#" + fill,
                        "fill_rule": fill_rule,
                        "contours": polygon_contours,
                    })

        # --- ストローク -> path ---
        if stroke is not None and width_mm >= args.line_min_width and allowed(stroke_allow, stroke):
            for sub_index, items in enumerate(split_subpaths(d["items"])):
                items = [it for it in items if it[0] in ("l", "c")]
                if not items:
                    continue

                start = frame.pt(items[0][1])
                segments = []
                for it in items:
                    if it[0] == "l":
                        segments.append({"type": "line", "to": frame.pt(it[2])})
                    else:
                        segments.append({"type": "cubic",
                                         "c1": frame.pt(it[2]),
                                         "c2": frame.pt(it[3]),
                                         "to": frame.pt(it[4])})

                paths.append({
                    "id": "line_%d_%d" % (z, sub_index),
                    "z": z,
                    "color": "#" + stroke,
                    "width": frame.length(width_mm),
                    "closed": math.dist(start, segments[-1]["to"]) < 1.0,
                    "start": start,
                    "segments": segments,
                })

    if not paths and not areas:
        print("取り込める要素がありませんでした", file=sys.stderr)
        return 1

    course = {
        "name": args.name or os.path.basename(args.pdf),
        "note": ("pdf_to_course.py で生成。図面に縮尺倍率 %.3f を掛けて実寸にしてある。"
                 "z は PDF の描画順。難所・ゲート・スタート姿勢は手で足すこと。" % args.scale),
        "units": "mm",
        "sheet": {"width": round(sheet_w, 1), "height": round(sheet_h, 1), "color": sheet_color},
        "paths": paths,
        "areas": areas,
        "props": [],
        "gates": [],
        "start_pose": {"position": paths[0]["start"] if paths else [0, 0], "heading": 0},
    }

    with open(args.output, "w", encoding="utf-8", newline="\n") as f:
        json.dump(course, f, ensure_ascii=False, indent=2)
        f.write("\n")

    widths = collections.Counter(p["width"] for p in paths)
    print("%s を書き出しました" % args.output)
    print("  シート   : %.0f x %.0f mm  地色 %s" % (sheet_w, sheet_h, sheet_color))
    print("  ライン   : %d 本 / セグメント %d 個"
          % (len(paths), sum(len(p["segments"]) for p in paths)))
    print("  面       : %d 個 (面積 %.0fmm2 未満の %d 個は除外)"
          % (len(areas), args.fill_min_area, skipped_small))
    print("  線幅の内訳 (実寸):")
    for w, n in widths.most_common(6):
        print("    %8.1f mm  x %d" % (w, n))
    return 0


def main():
    p = argparse.ArgumentParser(description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("pdf")
    p.add_argument("-o", "--output", default="course_from_pdf.json")
    p.add_argument("--page", type=int, default=1)
    p.add_argument("--inspect", action="store_true", help="中身を一覧表示して終了")
    p.add_argument("--scale", type=float, default=1.0,
                   help="図面 1mm が実寸で何 mm か。縮尺 1/2 なら 2.0")
    p.add_argument("--side", choices=["left", "right", "both"], default="both")
    p.add_argument("--line-color", action="append",
                   help="取り込む線の色。繰り返し指定可。省略すると全部")
    p.add_argument("--fill-color", action="append",
                   help="取り込む塗りの色。繰り返し指定可。省略すると全部")
    p.add_argument("--line-min-width", type=float, default=1.0,
                   help="この線幅 [mm @図面] 未満のストロークは無視する")
    p.add_argument("--fill-min-area", type=float, default=300.0,
                   help="この面積 [mm2 @図面] 未満の塗りは無視する（装飾を落とす）")
    p.add_argument("--name")
    p.add_argument("--sheet-color", default="#F2F2F0")
    p.add_argument("--no-flip-y", action="store_true")

    sys.exit(convert(p.parse_args()))


if __name__ == "__main__":
    main()
