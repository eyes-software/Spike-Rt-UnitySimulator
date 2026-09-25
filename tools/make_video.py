# -*- coding: utf-8 -*-
"""run_batch.ps1 が撮ったコマを、見出しと結果つきの 1 本の mp4 にまとめる。

使い方:
    python tools/make_video.py record
    python tools/make_video.py record -o out.mp4 top bot lft rgt

第 1 引数は run_batch.ps1 の出力先。タグを並べると、その配置だけを
その順番で並べる。

動きが続くのは実時間で数秒しかないので、そのまま全部つなぐと止まっている
絵ばかりになる。コマ同士の差分から動きのある区間を見つけ、前後だけ残す。
撮影は実時間よりかなり遅い（1 コマごとにコースを描き直すため）ので、
STEP で間引いて見やすい速さにしている。
"""
import argparse
import glob
import io
import json
import math
import os
import re
import shutil
import subprocess
import sys

from PIL import Image, ImageChops, ImageDraw, ImageFont

FPS = 30            # 組み立てるときの入力 fps（撮影の実測に近い値）
STEP = 2            # 何コマに 1 枚使うか
TITLE_SEC = 1.6     # 見出しを出す秒数
LEAD = 20           # 動き出しの手前に残すコマ数
TAIL = 60           # 動きが止まったあとに残すコマ数
DIFF_LIMIT = 0.25   # 動いているとみなすコマ間の差分

# ET相撲の判定に使う寸法。NansyoJudge と同じ値。
DOHYO_CENTER = (660.3, 1701.2)
OUT_LIMIT = 218.0   # 土俵の中心からこれより外なら場外（土俵 184 + ボトル 34）
TILT_LIMIT = 30.0   # これを超えたら横倒し

FONT = "C:/Windows/Fonts/BIZ-UDGothicB.ttc"

BOTTLE_RE = re.compile(r"力士ボトル \(\s*([-\d.]+),\s*([-\d.]+)\)mm 傾き\s*([-\d.]+)")


def font(size):
    return ImageFont.truetype(FONT, size)


def find_ffmpeg():
    p = shutil.which("ffmpeg")
    if p:
        return p
    raise SystemExit("ffmpeg が PATH にありません")


def final_bottle(out_dir, tag):
    """真値ログから、最後に記録されたボトルの位置と傾きを拾う。"""
    path = os.path.join(out_dir, "%s_unity.log" % tag)
    if not os.path.exists(path):
        return None

    last = None
    for line in io.open(path, encoding="utf-8", errors="replace"):
        m = BOTTLE_RE.search(line)
        if m:
            last = (float(m.group(1)), float(m.group(2)), float(m.group(3)))
    if last is None:
        return None

    d = math.hypot(last[0] - DOHYO_CENTER[0], last[1] - DOHYO_CENTER[1])
    return d, last[2]


def verdict_text(fb):
    if fb is None:
        return "結果: ログなし", False
    dist, tilt = fb
    if tilt > TILT_LIMIT:
        v = "横倒しのため不成立"
    elif dist > OUT_LIMIT:
        v = "押し出し成立"
    else:
        v = "土俵内のため不成立"
    return "結果: 中心から %.0fmm  傾き %.1f°  → %s" % (dist, tilt, v), ("不成立" not in v)


def motion_window(files):
    """動きのあるコマの範囲を返す。小さく潰した絵の差分で見る。"""
    prev = None
    diffs = []
    for f in files:
        im = Image.open(f).convert("L").resize((160, 80))
        diffs.append(0.0 if prev is None
                     else sum(ImageChops.difference(im, prev).getdata()) / (160 * 80))
        prev = im

    moving = [i for i, d in enumerate(diffs) if d > DIFF_LIMIT]
    if not moving:
        return 0, min(len(files) - 1, int(FPS * 4))
    return max(0, moving[0] - LEAD), min(len(files) - 1, moving[-1] + TAIL)


def caption(im, label, offset, result, ok):
    """コマの下に、配置と結果を焼き込む。"""
    d = ImageDraw.Draw(im)
    w, h = im.size
    bar = 62
    d.rectangle([0, h - bar, w, h], fill=(16, 18, 22))
    d.text((14, h - bar + 8),
           "力士ボトル: %s  (中心から X%+d, Y%+d mm)" % (label, offset[0], offset[1]),
           font=font(20), fill=(236, 239, 244))
    d.text((14, h - bar + 34), result, font=font(18),
           fill=(160, 214, 160) if ok else (252, 129, 129))
    return im


def title_card(size, index, total, label, offset, result, ok):
    im = Image.new("RGB", size, (16, 18, 22))
    d = ImageDraw.Draw(im)
    w, h = size
    d.text((w // 2, h // 2 - 52), "%d / %d" % (index, total),
           font=font(26), fill=(141, 151, 165), anchor="mm")
    d.text((w // 2, h // 2 - 8), "力士ボトル: %s" % label,
           font=font(44), fill=(236, 239, 244), anchor="mm")
    d.text((w // 2, h // 2 + 42), "土俵の中心から X%+d, Y%+d mm" % offset,
           font=font(24), fill=(141, 151, 165), anchor="mm")
    d.text((w // 2, h // 2 + 84), result, font=font(22),
           fill=(160, 214, 160) if ok else (252, 129, 129), anchor="mm")
    return im


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("out_dir", help="run_batch.ps1 の出力先")
    ap.add_argument("tags", nargs="*", help="並べる配置のタグ（省略で全部）")
    ap.add_argument("-o", "--output", default=None)
    args = ap.parse_args()

    manifest = os.path.join(args.out_dir, "runs.json")
    if not os.path.exists(manifest):
        raise SystemExit("runs.json がありません: %s" % manifest)

    cases = json.loads(io.open(manifest, encoding="utf-8-sig").read())
    if isinstance(cases, dict):        # ケースが 1 つだと ConvertTo-Json が配列にしない
        cases = [cases]
    if args.tags:
        order = {t: i for i, t in enumerate(args.tags)}
        cases = sorted([c for c in cases if c["tag"] in args.tags],
                       key=lambda c: order[c["tag"]])
    if not cases:
        raise SystemExit("並べるケースがありません")

    out = args.output or os.path.join(args.out_dir, "runs.mp4")
    work = os.path.join(args.out_dir, "_video")
    if os.path.isdir(work):
        for f in glob.glob(os.path.join(work, "*.jpg")):
            os.remove(f)
    else:
        os.makedirs(work)

    n = 0
    size = None
    summary = []

    for idx, c in enumerate(cases, 1):
        tag, label = c["tag"], c["label"]
        offset = (int(c["x"]), int(c["y"]))
        files = sorted(glob.glob(os.path.join(args.out_dir, tag, "f_*.jpg")))
        if not files:
            print("!! %s のコマがありません" % tag)
            continue

        fb = final_bottle(args.out_dir, tag)
        result, ok = verdict_text(fb)
        if fb:
            summary.append((label, offset, fb[0], fb[1], ok))

        if size is None:
            size = Image.open(files[0]).size

        for _ in range(int(TITLE_SEC * FPS)):
            title_card(size, idx, len(cases), label, offset, result, ok).save(
                os.path.join(work, "v_%05d.jpg" % n), quality=90)
            n += 1

        a, b = motion_window(files)
        if b >= len(files) - 2:
            print("!! %s は最後まで動いています。録画が途中で切れている可能性があります"
                  " (RecordSeconds を延ばしてください)" % tag)
        print("%-5s コマ %d 本中 %d〜%d を使う  %s" % (tag, len(files), a, b, result))

        for f in files[a:b + 1:STEP]:
            caption(Image.open(f).convert("RGB"), label, offset, result, ok).save(
                os.path.join(work, "v_%05d.jpg" % n), quality=90)
            n += 1

    if n == 0:
        raise SystemExit("コマがありません")

    cmd = [find_ffmpeg(), "-y", "-framerate", str(FPS),
           "-i", os.path.join(work, "v_%05d.jpg"),
           "-r", "30", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-crf", "20", out]
    r = subprocess.run(cmd, capture_output=True)
    if r.returncode != 0:
        sys.stdout.write(r.stderr.decode("utf-8", "replace")[-2000:])
        return r.returncode

    print("\n%d コマ -> %s" % (n, out))
    print("%-12s %14s %10s %7s" % ("配置", "オフセット", "最終距離", "傾き"))
    for label, off, dist, tilt, ok in summary:
        print("%-12s (%+5d,%+5d) %7.0fmm %6.1f°  %s"
              % (label, off[0], off[1], dist, tilt, "成立" if ok else "不成立"))
    print("場外の境界 %.0fmm / 横倒しの限度 %.0f°" % (OUT_LIMIT, TILT_LIMIT))
    return 0


if __name__ == "__main__":
    sys.exit(main())
