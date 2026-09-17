# コース定義フォーマット

コースは JSON 1 ファイルで完全に記述する。Unity は起動時にこれを読み、
床メッシュ・ライン・難所オブジェクト・走行体の初期姿勢をすべて生成する。
FBX などの 3D アセットは不要。

## 座標系

- 単位は **mm**。
- 原点はコースシートの左下隅。**X は右方向、Y は奥方向**（真上から見た図と一致）。
- 方位 `heading` は **+X 軸から反時計回りに測った角度[deg]**。0=右、90=奥。
- Unity へは `position = (X/1000, 高さ, Y/1000)` として写像される（1 Unity unit = 1 m）。

## 収録しているコース

| ファイル | 内容 |
|---|---|
| `etrobo_2026_L.json` | **2026 公式競技コース の L 側**。公式 PDF (縮尺 1/2) から変換した実寸データ |
| `etrobo_2026_R.json` | 同じシートの R 側。スタート姿勢とゲートだけが L と異なる |
| `etrobo_2026.json` | 上記の元データ（スタート姿勢・ゲートなし） |
| `practice_oval.json` | 立ち上げ確認用の単純な周回コース |
| `etrobo_practice_full.json` | 難所を並べた練習コース（**寸法は暫定**） |
| `robot_spike.json` | 走行体のパラメータ（コースではない） |

公式コースのシートは **5460 x 3640 mm**、マットは緑 (`#149E4E`)、
ライン沿いに幅 **320mm の白帯**が敷かれ、その上に幅 **20mm の黒線**が乗っています。
L と R は同じ 1 枚のシートに左右対称で配置されています。

> ライン外が白とはかぎりません。白帯を外れると緑になり、緑の反射光量は
> 約 48 でライン縁の値に近くなります。反射光量だけを見る制御だと、
> 白帯を外れたことに気づけない場合があります。

## トップレベル

```jsonc
{
  "name": "コース名",
  "units": "mm",                       // 現在は "mm" のみ
  "sheet":   { "width": 4000, "height": 2000, "color": "#F2F2F0" },
  "paths":   [ /* ライン */ ],
  "areas":   [ /* 塗り領域 */ ],
  "props":   [ /* 難所などの立体物 */ ],
  "gates":   [ /* 計測用の通過判定線 */ ],
  "start_pose": { "position": [900, 300], "heading": 0 }
}
```

## paths — ライン

`segments` は **SVG のパスコマンドと同じ意味**を持つ。SVG から変換した際に
形が変わらないことを保証するためで、`tools/svg_to_course.py` はこの対応をそのまま使う。

```jsonc
{
  "id": "main",
  "color": "#101010",
  "width": 20,            // 線幅[mm]。ETロボコンの本線は 20mm
  "z": 1000,              // 描画順。省略可（下記）
  "closed": true,
  "start": [900, 300],
  "segments": [
    { "type": "line",  "to": [3100, 300] },
    { "type": "arc",   "to": [3500, 700], "r": 400, "sweep": 1, "large": 0 },
    { "type": "cubic", "c1": [x,y], "c2": [x,y], "to": [x,y] },
    { "type": "quad",  "c":  [x,y], "to": [x,y] }
  ]
}
```

- `arc` の `sweep` は SVG と同じく **1 = 角度が増える向き（この座標系では反時計回り）**、
  `large` は 1 で優弧を選ぶ。始点・終点・半径から中心を解いて描く。
### 描画順 (z)

`areas` と `paths` は **`z` の昇順**に重ねて描かれ、最後に描かれたものの色が
その位置の色になる（カラーセンサが読む色もこれで決まる）。

`z` を省略した場合は「`areas` を宣言順 -> `paths` を宣言順」という順序が
自動で割り当てられるので、手書きのコースでは気にしなくてよい。

`z` が要るのは **線の上に面を重ねる**場合。公式コースのブロックエリアは
黒いライン (`z=172`) の上にカラーサークル (`z=173` 以降) が乗っており、
この順序がないとサークルが線に隠れて色を読めなくなる。

`tools/pdf_to_course.py` は PDF の描画順をそのまま `z` に入れる。

## areas — 塗り領域

グレーゾーン、カラーサークル、ゴール判定面など。`paths` より下に描かれる。

```jsonc
{ "id": "block_area", "shape": "rect",    "color": "#9A9A9A",
  "center": [2000, 1000], "size": [900, 900], "rotation": 0 }
{ "id": "circle_r",   "shape": "circle",  "color": "#C8202A", "center": [1700, 1000], "radius": 75 }
{ "id": "zone",       "shape": "polygon", "color": "#2E7D32", "points": [[x,y],[x,y],...] }

// 穴のある図形（文字・ロゴ）は輪郭を並べる。fill_rule は "nonzero"（既定）か "evenodd"
{ "id": "glyph",      "shape": "polygon", "color": "#FFFFFF", "fill_rule": "nonzero",
  "contours": [ [[x,y],...],   // 外側
                [[x,y],...] ]  // 内側（向きが逆なら穴になる）
}
```

## props — 立体物（難所）

```jsonc
{ "id": "block_red", "type": "block",   "color": "#C8202A",
  "position": [1700, 1000], "size": [55, 55, 55], "mass": 0.02, "movable": true }

{ "id": "seesaw",    "type": "seesaw",  "position": [3200, 1000], "heading": 90,
  "size": [600, 300, 12], "pivot_height": 30, "mass": 0.3 }

{ "id": "ramp",      "type": "ramp",    "position": [800, 1500], "heading": 0,
  "size": [400, 300, 60] }                    // 幅・奥行・高さ

{ "id": "wall",      "type": "wall",    "position": [500, 1800], "heading": 0,
  "size": [600, 20, 100] }

{ "id": "bottle",    "type": "cylinder","color": "#3060C0",
  "position": [2400, 600], "size": [70, 70, 210], "mass": 0.05, "movable": true }
```

`type` は `block` / `cylinder` / `wall` / `ramp` / `seesaw` / `gate` のいずれか。
`movable: true` のものは Rigidbody を持ち、走行体に押される。

## gates — 計測区間

走行体の中心が線分を横切った瞬間にイベントを発火する。ラップタイム計測に使う。

```jsonc
{ "id": "start", "from": [700, 150], "to": [700, 450], "kind": "start" }
{ "id": "goal",  "from": [1500, 150], "to": [1500, 450], "kind": "goal" }
```

`kind` は `start` / `goal` / `checkpoint`。

## 公式コース図の取り込み

1. 公式配布の コース図 PDF を Inkscape 等で SVG として保存する。
2. ライン中心線を単一のパスとしてトレースし、`id="main"` を付ける。
3. 次を実行する。

```bash
python tools/svg_to_course.py path/to/course.svg -o course/etrobo_2025.json --line-width 20
```

SVG は Y 軸が下向きなので、変換時に `--flip-y`（既定で有効）でこの座標系へ直す。

## 走行体パラメータ

`robot_spike.json` / `robot_rehacks_2026.json` が走行体の寸法・特性です。
`SimBootstrap.RobotSpecFile` で選びます（既定は Re-HackS）。

- `robot_rehacks_2026.json` — 2026 ベーシッククラスの Re-HackS。寸法は公式 LDraw
  モデル（`ReHackSM6.ldr`）を展開して実測した値。質量と重心だけは推定。
- `robot_spike.json` — 汎用の SPIKE 走行体。練習コース用の暫定値。

座標の原点は「左右中心 / 車軸中心 / 床面」で、`*_x_mm` は + が前方です。
キー名を間違えると既定値のまま静かに走るので、自己テストが
「読まれていないキー」を検出するようになっています。

`color_sensor_x_mm` を変えたら、`tools/add_race_setup.py --sensor-offset` を
同じ値にしてスタート姿勢を作り直してください。

### 見た目のメッシュ (.etmesh)

`visual_mesh` に `.etmesh` を指定すると、走行体をその形で描きます。
`tools/ldraw_to_mesh.py` が LDraw の組立図から作る独自形式で、
中身は「まとまり（body / wheel_l / wheel_r / arm）ごとの頂点と、色ごとの三角形」です。
車輪とアームはそれぞれの回転中心を原点にしてあり、Unity 側で回ります。

指定しない、あるいはファイルが無ければ箱で描きます。
**見た目だけで、物理・センサ・当たり判定には一切関係しません。**
