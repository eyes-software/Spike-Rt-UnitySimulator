#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
preview_course.py : コース定義 JSON を PNG に描く。

Unity を起動せずに「コース定義が意図した絵になっているか」を確かめるためのもの。
PDF から変換した直後の検算や、手で難所を足したあとの確認に使う。
描画順は Unity 側 (CourseField) と同じく z の昇順。

使い方:
    python tools/preview_course.py course/etrobo_2026.json -o preview.png
    python tools/preview_course.py course/etrobo_2026.json --width 1600 --gates
"""

from __future__ import annotations

import argparse
import io
import json
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


def hex_to_rgb01(text, fallback=(1, 1, 1)):
    if not text:
        return fallback
    t = text.lstrip("#")
    if len(t) != 6:
        return fallback
    return tuple(int(t[i:i + 2], 16) / 255.0 for i in (0, 2, 4))


def flatten_path(path, steps=16):
    pts = [path["start"]]
    for seg in path["segments"]:
        kind = seg.get("type", "line")
        if kind == "line":
            pts.append(seg["to"])
        elif kind == "cubic":
            p0 = pts[-1]
            c1, c2, p3 = seg["c1"], seg["c2"], seg["to"]
            for i in range(1, steps + 1):
                t = i / steps
                u = 1 - t
                pts.append([
                    u**3 * p0[0] + 3*u*u*t*c1[0] + 3*u*t*t*c2[0] + t**3*p3[0],
                    u**3 * p0[1] + 3*u*u*t*c1[1] + 3*u*t*t*c2[1] + t**3*p3[1],
                ])
        elif kind == "quad":
            p0 = pts[-1]
            c = seg.get("c", seg.get("c1"))
            p2 = seg["to"]
            for i in range(1, steps + 1):
                t = i / steps
                u = 1 - t
                pts.append([
                    u*u*p0[0] + 2*u*t*c[0] + t*t*p2[0],
                    u*u*p0[1] + 2*u*t*c[1] + t*t*p2[1],
                ])
        elif kind == "arc":
            # プレビューなので円弧は弦で代用する（形の確認には足りる）
            pts.append(seg["to"])
    if path.get("closed") and pts[-1] != pts[0]:
        pts.append(pts[0])
    return pts


def render(args):
    with io.open(args.course, encoding="utf-8") as f:
        course = json.load(f)

    sheet = course.get("sheet", {})
    W = float(sheet.get("width", 4000))
    H = float(sheet.get("height", 2000))

    out_w = args.width
    out_h = max(1, int(round(out_w * H / W)))
    sx = out_w / W
    sy = out_h / H

    def to_px(p):
        # コース座標は Y 上向き、画像は Y 下向き
        return pymupdf.Point(p[0] * sx, out_h - p[1] * sy)

    doc = pymupdf.open()
    page = doc.new_page(width=out_w, height=out_h)

    # 地色
    bg = page.new_shape()
    bg.draw_rect(pymupdf.Rect(0, 0, out_w, out_h))
    bg.finish(fill=hex_to_rgb01(sheet.get("color"), (0.95, 0.95, 0.94)), color=None)
    bg.commit()

    # areas と paths を z 順に混ぜて描く（Unity 側 CourseField と同じ順序）
    items = []
    for i, a in enumerate(course.get("areas", [])):
        items.append((a.get("z", i), i, "area", a))
    for i, p in enumerate(course.get("paths", [])):
        items.append((p.get("z", 1000 + i), i, "path", p))
    items.sort(key=lambda t: (t[0], t[1]))

    # 同じ見た目が続く区間は 1 つの Shape にまとめる。
    # 要素ごとに commit すると、要素数に対して二次で遅くなる
    # （公式コースは AR マーカーだけで 7000 個以上ある）。
    def style_key(kind, obj):
        if kind == "area":
            # 塗り規則が違うものを 1 つの Shape にまとめると塗りが変わる
            return ("area", obj.get("color"), obj.get("fill_rule", "nonzero"))
        return ("path", obj.get("color"), round(float(obj.get("width", 20)), 2))

    shape = None
    current_key = None

    def commit(shape, key):
        if shape is None:
            return
        if key[0] == "area":
            shape.finish(fill=hex_to_rgb01(key[1], (0.6, 0.6, 0.6)), color=None,
                         even_odd=(key[2] == "evenodd"))
        else:
            shape.finish(color=hex_to_rgb01(key[1], (0, 0, 0)),
                         width=max(0.4, key[2] * sx),
                         lineCap=1, lineJoin=1, closePath=False)
        shape.commit()

    for _z, _i, kind, obj in items:
        key = style_key(kind, obj)
        if key != current_key:
            commit(shape, current_key)
            shape = page.new_shape()
            current_key = key

        if kind == "area":
            s = obj.get("shape", "rect")
            if s == "rect":
                cx, cy = obj["center"]
                w, h = obj["size"]
                shape.draw_rect(pymupdf.Rect(
                    (cx - w / 2) * sx, out_h - (cy + h / 2) * sy,
                    (cx + w / 2) * sx, out_h - (cy - h / 2) * sy))
            elif s == "circle":
                shape.draw_circle(to_px(obj["center"]), obj["radius"] * sx)
            else:
                # 輪郭が複数あるものは穴あき。すべて描いてから
                # fill_rule に従って塗る（規則は commit 側で渡している）。
                contours = obj.get("contours")
                if contours is None:
                    contours = [obj.get("points", [])]

                drew = False
                for contour in contours:
                    pts = [to_px(p) for p in contour]
                    if len(pts) < 3:
                        continue
                    for i in range(1, len(pts)):
                        shape.draw_line(pts[i - 1], pts[i])
                    shape.draw_line(pts[-1], pts[0])
                    drew = True
                if not drew:
                    continue
        else:
            pts = flatten_path(obj)
            for i in range(1, len(pts)):
                shape.draw_line(to_px(pts[i - 1]), to_px(pts[i]))

    commit(shape, current_key)

    if args.gates:
        g = page.new_shape()
        for gate in course.get("gates", []):
            g.draw_line(to_px(gate["from"]), to_px(gate["to"]))
        g.finish(color=(1, 0, 1), width=3, dashes="[4 4] 0")
        g.commit()

    start = course.get("start_pose", {}).get("position")
    if start and args.gates:
        s = page.new_shape()
        s.draw_circle(to_px(start), 8)
        s.finish(color=(1, 0, 0), fill=(1, 0, 0))
        s.commit()

    pix = page.get_pixmap()
    pix.save(args.output)
    print("%s (%dx%d px, コース %.0f x %.0f mm)" % (args.output, pix.width, pix.height, W, H))
    print("  面 %d 個 / ライン %d 本"
          % (len(course.get("areas", [])), len(course.get("paths", []))))
    return 0


def main():
    p = argparse.ArgumentParser(description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("course", help="コース定義 JSON")
    p.add_argument("-o", "--output", default="course_preview.png")
    p.add_argument("--width", type=int, default=1200, help="出力画像の横幅 [px]")
    p.add_argument("--gates", action="store_true", help="ゲートとスタート位置も描く")
    sys.exit(render(p.parse_args()))


if __name__ == "__main__":
    main()
