#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
mock_sim.py : Unity を使わないヘッドレス版シミュレータ。

Unity を立ち上げずに制御プログラムを回したいとき（CI、プロトコルの確認、
PID ゲインの当たりをつける段階）に使う。物理は Unity 側と同じ意図の
モデルだが、実装は独立しているので数値は完全には一致しない。
ここで走ることは「配線が通っていること」の確認であって、
タイムの検証には Unity 側を使うこと。

使い方:
    python tools/mock_sim.py                       # 既定 (practice_oval)
    python tools/mock_sim.py --course course/etrobo_practice_full.json
    python tools/mock_sim.py --duration 20 --trace  # 走行軌跡を出す
"""

from __future__ import annotations

import argparse
import json
import math
import os
import socket
import struct
import sys
import time

# --- プロトコル (protocol/etsim_protocol.h と一致させること) -------------

PROTOCOL_VERSION = 1
MAGIC_SENSOR = 0x53535445
MAGIC_ACTUATOR = 0x41535445

SENSOR_FMT = "<IHHIII" "B3x" "ii" "3H" "H" "BB2x" "i" "BB2x" "f" "3f" "3f" "3f" "3i" "3i" "3i" "B3x" "i" "4f"
ACTUATOR_FMT = "<IHHII" "3i" "3B" "B" "3B" "B" "5B" "3x"

SENSOR_SIZE = struct.calcsize(SENSOR_FMT)
ACTUATOR_SIZE = struct.calcsize(ACTUATOR_FMT)

AFLAG_LOCKSTEP = 0x0001
AFLAG_RESET = 0x0002
AFLAG_SHUTDOWN = 0x0004

SFLAG_RUNNING = 0x0001
SFLAG_LOCKSTEP = 0x0002
SFLAG_GOAL = 0x0004
SFLAG_OUT_OF_COURSE = 0x0008

MOTOR_MODE_COAST = 0
MOTOR_MODE_BRAKE = 1
MOTOR_MODE_HOLD = 2
MOTOR_MODE_POWER = 3

COLOR_NONE, COLOR_BLACK, COLOR_GRAY, COLOR_WHITE = 0, 1, 2, 3
COLOR_RED, COLOR_ORANGE, COLOR_YELLOW, COLOR_GREEN = 4, 5, 6, 7
COLOR_CYAN, COLOR_BLUE, COLOR_VIOLET, COLOR_MAGENTA = 8, 9, 10, 11


# --- コース ------------------------------------------------------------


def _hex_to_rgb(text, fallback=(0, 0, 0)):
    if not text:
        return fallback
    t = text.lstrip("#")
    if len(t) != 6:
        return fallback
    return tuple(int(t[i:i + 2], 16) for i in (0, 2, 4))


def _flatten_arc(p0, p1, radius, large, sweep, tol=0.3):
    """SVG の endpoint 表現の円弧を折れ線にする。Unity 側と同じ式。"""
    x0, y0 = p0
    x1, y1 = p1
    dx2, dy2 = (x0 - x1) * 0.5, (y0 - y1) * 0.5
    d2 = dx2 * dx2 + dy2 * dy2
    if d2 < 1e-12:
        return []

    r = abs(radius)
    lam = d2 / (r * r)
    if lam > 1.0:
        r *= math.sqrt(lam)

    factor = math.sqrt(max(0.0, (r * r - d2) / d2))
    if large == sweep:
        factor = -factor

    cx = factor * dy2 + (x0 + x1) * 0.5
    cy = -factor * dx2 + (y0 + y1) * 0.5

    t0 = math.atan2(y0 - cy, x0 - cx)
    t1 = math.atan2(y1 - cy, x1 - cx)
    sweep_angle = t1 - t0
    if sweep and sweep_angle < 0:
        sweep_angle += 2 * math.pi
    if not sweep and sweep_angle > 0:
        sweep_angle -= 2 * math.pi

    step_angle = 2 * math.acos(max(-1.0, min(1.0, 1 - tol / max(r, 1e-3))))
    steps = max(1, min(4096, math.ceil(abs(sweep_angle) / max(step_angle, 1e-4))))

    return [(cx + r * math.cos(t0 + sweep_angle * i / steps),
             cy + r * math.sin(t0 + sweep_angle * i / steps))
            for i in range(1, steps + 1)]


def _flatten_cubic(p0, c1, c2, p3, tol=0.3):
    poly_len = (math.dist(p0, c1) + math.dist(c1, c2) + math.dist(c2, p3))
    steps = max(4, min(4096, math.ceil(math.sqrt(poly_len / max(tol, 1e-3)) * 2)))
    out = []
    for i in range(1, steps + 1):
        t = i / steps
        u = 1 - t
        x = (u ** 3 * p0[0] + 3 * u * u * t * c1[0] + 3 * u * t * t * c2[0] + t ** 3 * p3[0])
        y = (u ** 3 * p0[1] + 3 * u * u * t * c1[1] + 3 * u * t * t * c2[1] + t ** 3 * p3[1])
        out.append((x, y))
    return out


class Course:
    """コース定義を読み、任意の点の色を答える。"""

    def __init__(self, path):
        with open(path, encoding="utf-8") as f:
            data = json.load(f)

        self.name = data.get("name", "course")
        sheet = data.get("sheet", {})
        self.width = float(sheet.get("width", 4000))
        self.height = float(sheet.get("height", 2000))
        self.sheet_rgb = _hex_to_rgb(sheet.get("color"), (242, 242, 240))

        # 面と線を 1 本のリストにまとめ、z の昇順（同じなら宣言順）で持つ。
        # 公式コースは「黒いラインの上にカラーサークルが乗る」ので、
        # 「面 -> 線」の固定順だとサークルの色が読めなくなる。
        drawables = []

        for i, area in enumerate(data.get("areas", [])):
            drawables.append((area.get("z", i), i, "area", area))

        for i, path_def in enumerate(data.get("paths", [])):
            pts = self._flatten_path(path_def)
            if len(pts) < 2:
                continue
            stroke = (pts,
                      float(path_def.get("width", 20)) * 0.5,
                      _hex_to_rgb(path_def.get("color"), (16, 16, 16)))
            drawables.append((path_def.get("z", 1000 + i), i, "stroke", stroke))

        drawables.sort(key=lambda t: (t[0], t[1]))
        self.drawables = [(kind, obj) for _z, _i, kind, obj in drawables]

        # 走行体の初期化などで従来の属性を参照している箇所のために残す
        self.strokes = [obj for kind, obj in self.drawables if kind == "stroke"]
        self.areas = [obj for kind, obj in self.drawables if kind == "area"]

        # 計測ゲート。Unity の LapTimer と同じ意味で使う
        self.gates = []
        for g in data.get("gates", []):
            if "from" in g and "to" in g:
                self.gates.append((g.get("id", "gate"),
                                   g.get("kind", "checkpoint"),
                                   tuple(g["from"]), tuple(g["to"])))

        start = data.get("start_pose", {})
        pos = start.get("position", [700, 300])
        self.start_x = float(pos[0])
        self.start_y = float(pos[1])
        self.start_heading = float(start.get("heading", 0))

        self._build_index()

    # -- 格子索引 -------------------------------------------------------
    #
    # 公式コースは面が 7500 個ある。1 点の色を出すのに全部を見ていると
    # 1 サンプルで 25ms かかり、9 点のスポット平均では実時間にまるで
    # 追いつかない（制御プロセスの指令が溜まって、走行体が止まって見える）。
    # 描くものを格子に振り分けて、その升にあるものだけを見る。
    CELL_MM = 64.0

    def _build_index(self):
        # 線は「線分ごと」に入れる。1 本の線が盤面いっぱいに伸びていても、
        # 升には近くの線分だけが入るようにするため。
        items = []          # (kind, payload) を描画順に
        for kind, obj in self.drawables:
            if kind == "area":
                items.append(("area", obj, self._area_bounds(obj)))
            else:
                pts, half, color = obj
                for i in range(1, len(pts)):
                    a, b = pts[i - 1], pts[i]
                    bounds = (min(a[0], b[0]) - half, min(a[1], b[1]) - half,
                              max(a[0], b[0]) + half, max(a[1], b[1]) + half)
                    items.append(("seg", (a, b, half * half, color), bounds))

        self._items = [(kind, payload) for kind, payload, _b in items]

        self._cols = max(1, int(self.width / self.CELL_MM) + 1)
        self._rows = max(1, int(self.height / self.CELL_MM) + 1)
        self._grid = [None] * (self._cols * self._rows)

        for index, (_kind, _payload, bounds) in enumerate(items):
            if bounds is None:
                continue
            x0 = max(0, int(bounds[0] / self.CELL_MM))
            y0 = max(0, int(bounds[1] / self.CELL_MM))
            x1 = min(self._cols - 1, int(bounds[2] / self.CELL_MM))
            y1 = min(self._rows - 1, int(bounds[3] / self.CELL_MM))
            for cy in range(y0, y1 + 1):
                row = cy * self._cols
                for cx in range(x0, x1 + 1):
                    cell = self._grid[row + cx]
                    if cell is None:
                        self._grid[row + cx] = [index]
                    else:
                        cell.append(index)

    @staticmethod
    def _area_bounds(area):
        shape = area.get("shape", "rect")
        if shape == "circle":
            cx, cy = area.get("center", [0, 0])
            r = float(area.get("radius", 0))
            return (cx - r, cy - r, cx + r, cy + r)
        if shape == "rect":
            cx, cy = area.get("center", [0, 0])
            w, h = area.get("size", [0, 0])
            # 回っていても収まるように対角の長さで包む
            half = math.hypot(w, h) * 0.5
            return (cx - half, cy - half, cx + half, cy + half)

        contours = area.get("contours")
        if contours is None:
            contours = [area.get("points", [])]
        xs = [q[0] for c in contours for q in c]
        ys = [q[1] for c in contours for q in c]
        if not xs:
            return None
        return (min(xs), min(ys), max(xs), max(ys))

    @staticmethod
    def _flatten_path(path_def):
        start = tuple(path_def.get("start", [0, 0]))
        pts = [start]
        cursor = start

        for seg in path_def.get("segments", []):
            kind = seg.get("type", "line")
            to = tuple(seg.get("to", cursor))
            if kind == "line":
                pts.append(to)
            elif kind == "arc":
                pts.extend(_flatten_arc(cursor, to,
                                        float(seg.get("r", seg.get("radius", 1))),
                                        bool(seg.get("large", False)),
                                        bool(seg.get("sweep", True))))
            elif kind == "cubic":
                pts.extend(_flatten_cubic(cursor, tuple(seg["c1"]), tuple(seg["c2"]), to))
            elif kind == "quad":
                c = tuple(seg.get("c", seg.get("c1")))
                c1 = (cursor[0] + 2 / 3 * (c[0] - cursor[0]), cursor[1] + 2 / 3 * (c[1] - cursor[1]))
                c2 = (to[0] + 2 / 3 * (c[0] - to[0]), to[1] + 2 / 3 * (c[1] - to[1]))
                pts.extend(_flatten_cubic(cursor, c1, c2, to))
            cursor = to

        if path_def.get("closed") and pts[-1] != pts[0]:
            pts.append(pts[0])
        return pts

    @staticmethod
    def _dist_sq_to_segment(p, a, b):
        abx, aby = b[0] - a[0], b[1] - a[1]
        len_sq = abx * abx + aby * aby
        if len_sq < 1e-12:
            return (p[0] - a[0]) ** 2 + (p[1] - a[1]) ** 2
        t = ((p[0] - a[0]) * abx + (p[1] - a[1]) * aby) / len_sq
        t = max(0.0, min(1.0, t))
        dx = p[0] - (a[0] + t * abx)
        dy = p[1] - (a[1] + t * aby)
        return dx * dx + dy * dy

    def sample_color(self, x, y):
        """コース座標 [mm] の色。paths が areas より手前。"""
        if not (0 <= x <= self.width and 0 <= y <= self.height):
            return (24, 24, 24)

        cx = int(x / self.CELL_MM)
        cy = int(y / self.CELL_MM)
        if not (0 <= cx < self._cols and 0 <= cy < self._rows):
            return self.sheet_rgb

        cell = self._grid[cy * self._cols + cx]
        if cell is None:
            return self.sheet_rgb

        p = (x, y)
        rgb = self.sheet_rgb

        # 升の中は描画順（z の昇順）に並んでいるので、最後に当たったものが手前
        for index in cell:
            kind, payload = self._items[index]
            if kind == "area":
                if self._area_contains(payload, x, y):
                    rgb = _hex_to_rgb(payload.get("color"), (154, 154, 154))
            else:
                a, b, limit, color = payload
                if self._dist_sq_to_segment(p, a, b) <= limit:
                    rgb = color

        return rgb

    @staticmethod
    def _area_contains(area, x, y):
        shape = area.get("shape", "rect")

        if shape == "circle":
            cx, cy = area.get("center", [0, 0])
            return math.dist((x, y), (cx, cy)) <= float(area.get("radius", 0))

        if shape == "rect":
            cx, cy = area.get("center", [0, 0])
            w, h = area.get("size", [0, 0])
            rot = math.radians(float(area.get("rotation", 0)))
            dx, dy = x - cx, y - cy
            lx = dx * math.cos(rot) + dy * math.sin(rot)
            ly = -dx * math.sin(rot) + dy * math.cos(rot)
            return abs(lx) <= w * 0.5 and abs(ly) <= h * 0.5

        # 輪郭が複数あるときは内側の輪郭が穴になる。
        # 規則は fill_rule (既定は非ゼロ回転数)。Unity 側と同じにしておく。
        contours = area.get("contours")
        if contours is None:
            contours = [area.get("points", [])]
        non_zero = area.get("fill_rule", "nonzero") != "evenodd"

        winding = 0
        for pts in contours:
            if len(pts) < 3:
                continue
            j = len(pts) - 1
            for i in range(len(pts)):
                if (pts[i][1] > y) != (pts[j][1] > y):
                    xx = ((pts[j][0] - pts[i][0]) * (y - pts[i][1])
                          / (pts[j][1] - pts[i][1]) + pts[i][0])
                    if x < xx:
                        winding += (1 if pts[i][1] > pts[j][1] else -1) if non_zero else 1
                j = i
        return winding != 0 if non_zero else (winding % 2) == 1


# --- 走行体 ------------------------------------------------------------


def _segments_intersect(p1, p2, q1, q2):
    """2 本の線分が交わるか。Unity の LapTimer.SegmentsIntersect と同じ判定。"""
    def cross(a, b):
        return a[0] * b[1] - a[1] * b[0]

    def sub(a, b):
        return (a[0] - b[0], a[1] - b[1])

    def on_segment(a, b, q):
        return (min(a[0], b[0]) - 1e-6 <= q[0] <= max(a[0], b[0]) + 1e-6 and
                min(a[1], b[1]) - 1e-6 <= q[1] <= max(a[1], b[1]) + 1e-6)

    d1 = cross(sub(q2, q1), sub(p1, q1))
    d2 = cross(sub(q2, q1), sub(p2, q1))
    d3 = cross(sub(p2, p1), sub(q1, p1))
    d4 = cross(sub(p2, p1), sub(q2, p1))

    if ((d1 > 0) != (d2 > 0)) and ((d3 > 0) != (d4 > 0)):
        return True
    if abs(d1) < 1e-9 and on_segment(q1, q2, p1):
        return True
    if abs(d2) < 1e-9 and on_segment(q1, q2, p2):
        return True
    if abs(d3) < 1e-9 and on_segment(p1, p2, q1):
        return True
    if abs(d4) < 1e-9 and on_segment(p1, p2, q2):
        return True
    return False


class LapTimer:
    """Unity 側 LapTimer と同じ規則。スタート -> 全チェックポイント -> ゴール。"""

    def __init__(self, gates):
        self.gates = gates
        self.total_checkpoints = sum(1 for g in gates if g[1] == "checkpoint")
        self.reset()

    def reset(self):
        self.running = False
        self.finished = False
        self.elapsed = 0.0
        self.passed = set()
        self.last_gate = "-"
        self._prev = None

    def step(self, pos, dt):
        if self.running and not self.finished:
            self.elapsed += dt

        if self._prev is None:
            self._prev = pos
            return
        prev, self._prev = self._prev, pos
        if (pos[0] - prev[0]) ** 2 + (pos[1] - prev[1]) ** 2 < 1e-6:
            return

        for gid, kind, a, b in self.gates:
            if not _segments_intersect(prev, pos, a, b):
                continue
            self.last_gate = gid
            if kind == "start":
                if not self.running:
                    self.running = True
                    self.finished = False
                    self.elapsed = 0.0
                    self.passed.clear()
            elif kind == "goal":
                # 全チェックポイントを通っていなければゴールとみなさない
                if self.running and not self.finished and len(self.passed) >= self.total_checkpoints:
                    self.finished = True
                    self.running = False
            else:
                if self.running:
                    self.passed.add(gid)


class Robot:
    """
    二輪差動駆動の簡易モデル。
    Unity 側はすべりや接地を含む力学モデルだが、ここでは
    「モータの 1 次遅れ + すべりなしの運動学」まで落としてある。
    """

    WHEEL_RADIUS_MM = 28.0
    TREAD_MM = 120.0
    NO_LOAD_DPS = 175.0 * 360.0 / 60.0   # 175 rpm
    TIME_CONSTANT = 0.09                  # 出力変化に対する立ち上がり [s]

    SENSOR_OFFSET_MM = 95.0
    SENSOR_SPOT_MM = 12.0    # Unity 側 RobotSpec.ColorSensorSpotMm と同じ

    # モータの取り付け向き。左右は鏡像に付くので片側が反転する。
    # プロトコルが運ぶのは「モータ軸の値」なので、ここで前進向きへ直す。
    # Unity 側 RobotSpec.LeftMotorMirrored と同じ意味。
    POLARITY = (-1, 1, 1)   # 左, 右, アーム

    def __init__(self, course: Course):
        self.course = course
        self.reset()

    def reset(self):
        self.x = self.course.start_x
        self.y = self.course.start_y
        self.heading = math.radians(self.course.start_heading)
        self.speed_l = 0.0   # deg/s
        self.speed_r = 0.0
        self.count_l = 0.0   # deg
        self.count_r = 0.0
        self.count_a = 0.0
        self.yaw = 0.0       # deg
        self.power = [0, 0, 0]
        self.mode = [MOTOR_MODE_COAST] * 3

    def _target_speed(self, index):
        mode = self.mode[index]
        if mode in (MOTOR_MODE_COAST, MOTOR_MODE_BRAKE, MOTOR_MODE_HOLD):
            return 0.0
        return self.power[index] * self.POLARITY[index] / 100.0 * self.NO_LOAD_DPS

    def step(self, dt):
        alpha = 1.0 - math.exp(-dt / self.TIME_CONSTANT)
        self.speed_l += (self._target_speed(0) - self.speed_l) * alpha
        self.speed_r += (self._target_speed(1) - self.speed_r) * alpha

        self.count_l += self.speed_l * dt
        self.count_r += self.speed_r * dt

        # 車輪周速 [mm/s]
        vl = math.radians(self.speed_l) * self.WHEEL_RADIUS_MM
        vr = math.radians(self.speed_r) * self.WHEEL_RADIUS_MM

        v = (vl + vr) * 0.5
        omega = (vr - vl) / self.TREAD_MM   # [rad/s]

        self.heading += omega * dt
        self.x += v * math.cos(self.heading) * dt
        self.y += v * math.sin(self.heading) * dt
        self.yaw += math.degrees(omega) * dt

        return v, math.degrees(omega)

    def sensor_position(self):
        return (self.x + self.SENSOR_OFFSET_MM * math.cos(self.heading),
                self.y + self.SENSOR_OFFSET_MM * math.sin(self.heading))


def classify_color(h, s, v):
    if v < 12:
        return COLOR_BLACK
    if s < 20:
        return COLOR_WHITE if v > 60 else COLOR_GRAY
    if h < 20 or h >= 340:
        return COLOR_RED
    if h < 45:
        return COLOR_ORANGE
    if h < 75:
        return COLOR_YELLOW
    if h < 160:
        return COLOR_GREEN
    if h < 200:
        return COLOR_CYAN
    if h < 260:
        return COLOR_BLUE
    if h < 300:
        return COLOR_VIOLET
    return COLOR_MAGENTA


def build_spot_pattern(count=9):
    """円形スポットを面積が等しくなるように配る。Unity 側と同じ黄金角配置。"""
    pts = [(0.0, 0.0)]
    golden = 2.39996323
    for i in range(1, count):
        r = math.sqrt(i / (count - 1))
        a = i * golden
        pts.append((math.cos(a) * r, math.sin(a) * r))
    return pts


SPOT_PATTERN = build_spot_pattern(9)


def sample_spot(course, cx, cy, heading, diameter_mm):
    """センサスポット内を平均した色を返す。"""
    radius = diameter_mm * 0.5
    ch, sh = math.cos(heading), math.sin(heading)

    total = [0.0, 0.0, 0.0]
    for ox, oy in SPOT_PATTERN:
        # スポットは機体に固定されているので向きに合わせて回す
        dx = (oy * ch - ox * sh) * radius
        dy = (oy * sh + ox * ch) * radius
        r, g, b = course.sample_color(cx + dx, cy + dy)
        total[0] += r
        total[1] += g
        total[2] += b

    n = len(SPOT_PATTERN)
    return (total[0] / n, total[1] / n, total[2] / n)


def rgb_to_hsv(r, g, b):
    r, g, b = r / 255.0, g / 255.0, b / 255.0
    mx, mn = max(r, g, b), min(r, g, b)
    delta = mx - mn

    if delta < 1e-6:
        h = 0.0
    elif mx == r:
        h = 60.0 * (((g - b) / delta) % 6.0)
    elif mx == g:
        h = 60.0 * (((b - r) / delta) + 2.0)
    else:
        h = 60.0 * (((r - g) / delta) + 4.0)

    s = 0.0 if mx < 1e-6 else delta / mx
    return int(round(h)) % 360, int(round(s * 100)), int(round(mx * 100))


# --- サーバ ------------------------------------------------------------


def run(args):
    course = Course(args.course)
    robot = Robot(course)
    timer = LapTimer(course.gates)

    # 判定結果。ゴールしたか、線を見失ったか、時間切れか。
    outcome = "timeout"
    lost_for = 0.0

    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sock.bind(("127.0.0.1", args.port))
    sock.settimeout(0.5)

    print(f"[mock] コース '{course.name}' ({course.width:.0f}x{course.height:.0f}mm)")
    print(f"[mock] 127.0.0.1:{args.port} で待ち受け中 "
          f"(sensor={SENSOR_SIZE}B actuator={ACTUATOR_SIZE}B)")

    if SENSOR_SIZE != 152 or ACTUATOR_SIZE != 44:
        print(f"[mock] パケットサイズが仕様と違います: "
              f"sensor={SENSOR_SIZE} (152) actuator={ACTUATOR_SIZE} (44)", file=sys.stderr)
        return 1

    peer = None
    connected_at = None
    seq = 0
    ack = 0
    sim_time = 0.0
    step_dt = args.step_us * 1e-6
    last_step_wall = time.monotonic()
    lockstep = False
    started = time.monotonic()
    received = 0

    while True:
        if args.duration and (time.monotonic() - started) > args.duration:
            print(f"[mock] {args.duration:.0f} 秒経過したので終了します")
            break

        try:
            data, addr = sock.recvfrom(2048)
        except socket.timeout:
            if peer is None:
                continue
            data, addr = None, None
        except ConnectionResetError:
            # Windows では、直前に送った相手が居なくなると ICMP 由来の
            # WSAECONNRESET が次の recvfrom で返る。相手を忘れて待ち直す。
            print("[mock] 制御プロセスが切断されました")
            peer = None
            connected_at = None
            continue

        if data is not None:
            if len(data) < ACTUATOR_SIZE:
                continue
            fields = struct.unpack(ACTUATOR_FMT, data[:ACTUATOR_SIZE])
            magic, version, flags, aseq, _ctrl_time = fields[0:5]
            if magic != MAGIC_ACTUATOR or version != PROTOCOL_VERSION:
                continue

            if peer != addr:
                print(f"[mock] 制御プロセスが接続しました: {addr[0]}:{addr[1]}")
                connected_at = time.monotonic()
            peer = addr
            received += 1
            ack = aseq
            lockstep = bool(flags & AFLAG_LOCKSTEP)

            robot.power = list(fields[5:8])
            robot.mode = list(fields[8:11])
            reset_mask = fields[11]

            if reset_mask & 0x01:
                robot.count_l = 0.0
            if reset_mask & 0x02:
                robot.count_r = 0.0
            if reset_mask & 0x04:
                robot.count_a = 0.0
            if fields[15]:          # reset_imu
                robot.yaw = 0.0

            if flags & AFLAG_RESET:
                robot.reset()
            if flags & AFLAG_SHUTDOWN:
                print(f"[mock] 制御プロセスが終了しました (受信 {received} パケット)")
                break

        # ロックステップは 1 指令 = 1 ステップ。
        # フリーランは実時間で進める。Windows の sleep は 1ms を指定しても
        # 15ms 前後かかるので、固定幅で積分するとシミュレータだけが
        # 実時間の 1/7 の速さで進み、制御周期との対応が崩れる。
        if lockstep:
            dt = step_dt
        else:
            now = time.monotonic()
            dt = min(now - last_step_wall, 0.05)   # 大きな停止の後に飛ばない上限
            last_step_wall = now
            if dt <= 0.0:
                continue

        speed, _omega = robot.step(dt)
        sim_time += dt

        timer.step((robot.x, robot.y), dt)

        sx, sy = robot.sensor_position()
        r, g, b = sample_spot(course, sx, sy, robot.heading, Robot.SENSOR_SPOT_MM)

        # 線を見失った判定。センサがシートの地色しか見ていない状態が続いたら
        # コースアウトとみなす。反射光量だけでは緑のマットと線の縁を
        # 区別できない（どちらも 50 前後になる）ので、色で見る。
        if (r, g, b) == course.sheet_rgb:
            lost_for += dt
        else:
            lost_for = 0.0

        if timer.finished:
            outcome = "goal"
            print("[mock] ゴール  計測タイム %.2f 秒" % timer.elapsed)
            break

        if args.lost_sec > 0 and lost_for >= args.lost_sec:
            outcome = "lost"
            print("[mock] 線を見失いました  t=%.2fs 位置=(%.0f, %.0f)mm  通過ゲート=%s"
                  % (sim_time, robot.x, robot.y, timer.last_gate))
            break
        lum = (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255.0
        reflection = int(round(6 + (94 - 6) * lum))
        h, s, v = rgb_to_hsv(r, g, b)

        seq += 1
        flags_out = SFLAG_LOCKSTEP if lockstep else 0

        # 実機の中央ボタンに相当する押下を自動で入れる。
        # 起動時刻ではなく「制御プロセスが接続してから」を基準にしないと、
        # 相手がまだボタンを見ていない間に押し終わってしまう。
        button_state = 0
        if args.autostart > 0 and connected_at is not None:
            since_connect = time.monotonic() - connected_at
            if args.autostart <= since_connect < args.autostart + 0.5:
                button_state = 0x01

        packet = struct.pack(
            SENSOR_FMT,
            MAGIC_SENSOR, PROTOCOL_VERSION, flags_out, seq, ack, int(sim_time * 1e6),
            classify_color(h, s, v),
            reflection, max(0, min(100, int(lum * 12))),
            int(r / 255 * 1024), int(g / 255 * 1024), int(b / 255 * 1024),
            h, s, v,
            -1, 0, 0, 0.0,
            robot.yaw, 0.0, 0.0,
            0.0, 0.0, 0.0,
            0.0, 0.0, 9.81,
            int(robot.count_l * robot.POLARITY[0]),
            int(robot.count_r * robot.POLARITY[1]),
            int(robot.count_a * robot.POLARITY[2]),
            int(robot.speed_l * robot.POLARITY[0]),
            int(robot.speed_r * robot.POLARITY[1]), 0,
            robot.power[0], robot.power[1], robot.power[2],
            button_state, 8000,
            robot.x, robot.y, math.degrees(robot.heading), speed)

        if peer is not None:
            sock.sendto(packet, peer)

        if args.trace and seq % 250 == 0:
            heading_deg = (math.degrees(robot.heading) + 180.0) % 360.0 - 180.0
            print(f"[mock] t={sim_time:6.2f}s  位置=({robot.x:7.1f},{robot.y:7.1f})mm  "
                  f"方位={heading_deg:7.1f}deg  反射={reflection:3d}  "
                  f"出力=L{robot.power[0]:4d} R{robot.power[1]:4d}  "
                  f"CP={len(timer.passed)}/{timer.total_checkpoints}")

        if not lockstep:
            time.sleep(step_dt)

    sock.close()

    # 1 行のまとめ。ゲイン探索のスクリプトはこれを読む。
    print("[result] %s time=%.2f cp=%d/%d pos=%.0f,%.0f sim=%.2f"
          % (outcome, timer.elapsed, len(timer.passed), timer.total_checkpoints,
             robot.x, robot.y, sim_time))
    return 0 if outcome == "goal" else 1


def main():
    here = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

    parser = argparse.ArgumentParser(description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--course", default=os.path.join(here, "course", "practice_oval.json"),
                        help="コース定義 JSON")
    parser.add_argument("--port", type=int, default=54001)
    parser.add_argument("--step-us", type=int, default=2000, help="1 ステップの時間 [us]")
    parser.add_argument("--duration", type=float, default=0.0,
                        help="この秒数で自動終了 (0 = 無制限)")
    parser.add_argument("--trace", action="store_true", help="走行状態を定期表示する")
    parser.add_argument("--lost-sec", type=float, default=1.0,
                        help="センサが地色だけを見続けたらコースアウトとみなす秒数 (0 で無効)")
    parser.add_argument("--autostart", type=float, default=1.0,
                        help="接続からこの秒数後に中央ボタンを自動で押す (0 = 押さない)")

    sys.exit(run(parser.parse_args()))


if __name__ == "__main__":
    main()
