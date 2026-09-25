# ETロボコン Unity シミュレータ

ETロボコンのコースと SPIKE 走行体を Unity 上に再現し、**実機 (SPIKE-RT) 向けに書いた
C 言語の走行プログラムをそのまま走らせる**ための環境です。

```
┌──────────────────────────────┐   UDP (localhost:54001)   ┌───────────────────────────┐
│ 制御プロセス  etrobo_app.exe  │ ────── アクチュエータ ───► │ Unity シミュレータ         │
│                              │                            │                           │
│  app/etrobo_app.c   走行制御  │ ◄────── センサ値 ───────── │  コース生成 (JSON から)    │
│  spike/*            SPIKE-RT  │                            │  物理 (すべり・接地)       │
│  kernel.h           ASP3 shim │      152B / 44B の固定長    │  センサ模擬 (反射・測距)   │
└──────────────────────────────┘                            └───────────────────────────┘
```

走行プログラムから見えるのは `spike/pup/motor.h` などの **SPIKE-RT の公開 API** と
TOPPERS/ASP3 のサービスコールだけです。シミュレータとの通信はその裏に隠れているので、
同じソースを実機のビルドに回せます。

---

新しい PC へ移すときの手順は [`docs/セットアップ手順.pdf`](docs/セットアップ手順.pdf)
にまとめてあります（`python tools/make_setup_pdf.py` で作り直せます）。

---

## 1. 必要なもの

| | バージョン | 備考 |
|---|---|---|
| Unity | 2022.3 LTS | 2021.3 でも動くが 2022.3 で確認 |
| Visual Studio | 2022 以降 (C++ デスクトップ開発) | CMake は VS 同梱のものを自動で使う |
| Python | 3.8 以上 | コース変換とヘッドレス版シミュレータ用。無くても本体は動く |

外部の Unity パッケージ・3D アセットは使いません。コースも走行体も寸法から生成します。

---

## 2. 動かす

### 2-1. Unity 側

1. Unity Hub でこのフォルダの `unity/EtroboSim` を開く
2. `Assets/Scenes/SimMain.unity` を開く
3. 再生ボタンを押す

コース・走行体・カメラ・HUD はすべて再生時に生成されるので、シーンには
`SimBootstrap` が 1 個置いてあるだけです。既定は **2026 年度の公式競技コース (L 側)**
です。コースを変えるときは `SimBootstrap` の **Course File** を書き換えます。

| Course File | 内容 |
|---|---|
| `etrobo_2026_L.json` | 2026 公式競技コース L 側（既定） |
| `etrobo_2026_R.json` | 同 R 側 |
| `practice_oval.json` | 立ち上げ確認用の単純な周回コース |
| `etrobo_practice_full.json` | 難所を並べた練習コース（寸法は暫定） |

再生中の操作:

| キー | 動作 |
|---|---|
| `Space` | スタートの合図（ハブの中央ボタンとフォースセンサの両方に入る）<br>パネルの `中央ボタン` / `フォース` なら片方だけ押せる |
| `R` | 走行体を**開始位置**へ戻す（ボトルも設定した位置へ立て直す） |
| `Tab` | 視点モードの切り替え |
| `F` | 走行体に寄る |
| `G` | コース全体を映す |

### パネル（HUD）

走行中の状態と設定はパネルにまとまっています。行が多いので、
**見出し（`- センサ` など）をクリックすると節をたためます**。
たたんだ状態は次に起動したときも残ります。収まらないぶんはパネルの中を
スクロールできるので、行が隠れて消えることはありません。
カーソルがパネルの上にある間はカメラが動かないので、
ドラッグやホイールで視点が変わってしまうこともありません。

パネルからできること: コースの L/R 切り替え、スタートの合図、開始位置の変更、
キャリーボトルの色、ゲートと力士ボトルの配置、難所とフィニッシュの成立判定の確認。

視点モードは 4 つあります。現在のモードと操作は HUD に出ます。

| モード | 動き | 操作 |
|---|---|---|
| `ORBIT`（既定） | 注視点のまわりを回る | 右ドラッグ: 回転（ヨー・ピッチ）<br>中ドラッグ or Shift+右ドラッグ: 平行移動<br>ホイール: ズーム |
| `CHASE` | 走行体を追う | 右ドラッグ: 回り込み（走行体基準）<br>ホイール: 距離 |
| `ONBOARD` | 走行体の視点 | 右ドラッグ: 首振り |
| `FREE` | 自由飛行 | 右ドラッグ: 向き<br>`WASD`: 前後左右、`Q`/`E`: 上下、`Shift`: 高速 |

`ORBIT` のピッチは真上（89°）から水平近く（1°）まで連続で変えられます。
0 を跨がないよう止めてあるのは、上下が反転すると操作できなくなるためです。

### 任意の位置から始める

難所の手前だけを何度も試したいときは、開始位置を動かします。`R` で戻る場所が
変わるだけなので、走らせ方は今までと同じです。既定はコースの `start_pose` です。

- **矢印キーで動かす** — パネルの `配置(P)` を押すか `P` キーで配置モードに入り、
  見ながら合わせます。座標を計算せずに「スタートラインのぎりぎり手前」を出せます

  | キー | 動き |
  |---|---|
  | `↑` `↓` | 前後（走行体の向き基準）25mm/s |
  | `←` `→` | 左右 25mm/s |
  | `,` `.` | 左回り / 右回り 15°/s |
  | `Shift` 併用 | 10 倍 |

  配置モードの間だけ、矢印キーはハブの左右ボタンではなく走行体の移動に使います。
- **走らせた場所を使う** — `現在地に` を押すと、いま走行体がいる場所が開始位置に
  なります。難所の直前まで走らせてから押すのが手っ取り早いです
- **コースに焼く** — `保存` でコース JSON の `start_pose` を書き換えます。
  `StreamingAssets` と `course/` の**両方**を直すので、次に `course/` から
  コピーしても消えません。start_pose 以外はテキストのまま残します
- **既定に戻す** — `既定へ`（既定から動かしていると `*` が付きます）
- **起動時に指定** — `-etsimStart X,Y[,方位]`。方位はコース座標で、0 が +y、90 が -x です

```bash
# 土俵の手前に、力士ボトルの方を向けて置く
"$UNITY" -projectPath unity/EtroboSim -executeMethod Etrobo.Sim.EditorTools.SimSceneSetup.OpenAndPlay     -etsimCourse etrobo_2026_L_nansyo.json -etsimStart 1000,1701,180
```

自動スタートの「走り出したか」の判定もこの開始位置を基準にするので、
移動させても `-etsimAutoStart` はそのまま使えます。

なお計測ゲートは動きません。スタートラインより先に置くと計測が始まらないので、
タイムを見たいときはコース既定の位置から走らせてください。

### 2-2. 制御プロセス側

```bash
cd controller
.\build.ps1
```

`controller\bin\etrobo_app.exe` ができます。**Unity を再生状態にしてから**実行してください。

```bash
.\bin\etrobo_app.exe
```

つながると Unity の HUD が「接続中」になり、`Space` を押すと走り出します。
Unity を止めると制御プロセスも 3 秒後に自分で終了します。

主なオプション（環境変数でも同じ指定ができます）:

| オプション | 環境変数 | 既定 | 意味 |
|---|---|---|---|
| `--host` | `ETSIM_HOST` | 127.0.0.1 | シミュレータのアドレス |
| `--port` | `ETSIM_PORT` | 54001 | UDP ポート |
| `--lockstep` | `ETSIM_LOCKSTEP` | off | 決定論モード（後述） |
| `--step-us` | `ETSIM_STEP_US` | 2000 | ロックステップの 1 ステップ幅 |
| `--tx-period` | `ETSIM_TX_PERIOD_US` | 2000 | フリーラン時の送信周期 |

### 2-3. Unity を使わずに動かす

Unity を立ち上げずに制御プログラムだけ回したいとき（CI、プロトコルの確認、
PID ゲインの当たりをつける段階）は、ヘッドレス版が使えます。

```bash
python tools/mock_sim.py --trace
# 別のターミナルで
.\controller\bin\etrobo_app.exe
```

物理は Unity 側より粗い（すべり・接地・段差を持たない運動学モデル）ので、
**タイムの検証には使えません**。「配線が通っているか」の確認用です。

---

## 3. 走行プログラムを書く

`controller/app/etrobo_app.c` がサンプルです。これを差し替えるか、
`CMakeLists.txt` の `ETROBO_APP_SOURCES` に自分のソースを指定します。

```c
#include "kernel.h"
#include <spike/pup/motor.h>
#include <spike/pup/colorsensor.h>

void main_task(intptr_t exinf)
{
    pup_motor_t *left  = pup_motor_get_device(PBIO_PORT_ID_C);
    pup_motor_t *right = pup_motor_get_device(PBIO_PORT_ID_B);
    pup_device_t *color = pup_color_sensor_get_device(PBIO_PORT_ID_E);

    pup_motor_setup(left,  PUP_DIRECTION_COUNTERCLOCKWISE, true);
    pup_motor_setup(right, PUP_DIRECTION_CLOCKWISE, true);

    while (1) {
        int32_t reflection = pup_color_sensor_reflection(color);
        /* ... 制御 ... */
        pup_motor_set_power(left, 30);
        pup_motor_set_power(right, 30);
        dly_tsk(4 * 1000);   /* ASP3 の RELTIM はマイクロ秒 */
    }
}
```

### 使える API

`controller/include/` に置いてある範囲がそのまま使えます。

- `spike/pup/motor.h` — `pup_motor_get_device` / `setup` / `set_power` / `set_speed` /
  `get_count` / `get_speed` / `stop` / `brake` / `hold` / `is_stalled` / `set_duty_limit`
- `spike/pup/colorsensor.h` — `reflection` / `ambient` / `rgb` / `hsv` / `color`
- `spike/pup/ultrasonicsensor.h` — `distance` / `presence`
- `spike/pup/forcesensor.h` — （シミュレータは力を模擬していないので常に 0 を返します）
- `spike/hub/` — `button` / `imu` / `display` / `light` / `battery` / `system`
- `kernel.h` — `dly_tsk` / `tslp_tsk` / `act_tsk` / `slp_tsk` / `wup_tsk` /
  `sta_cyc` / `stp_cyc` / `wai_sem` / `sig_sem` / `get_tim` / `syslog` / `SVC_PERROR`

宣言は spike-rt 本体 (`drivers/include/spike/`) に合わせてあります。
実機側の API が変わったときに直すのは `controller/src/etsim_device.c` の 1 ファイルだけです。

### 実機との違い（必ず読む）

| | シミュレータ | 実機 |
|---|---|---|
| タスク | OS スレッド。優先度順のスケジューリングはしない | ASP3 の優先度ベース |
| `.cfg` の静的 API | 使わない。`etsim_register_task()` 等で実行時に登録 | `CRE_TSK` / `CRE_CYC` / `CRE_SEM` |
| `loc_cpu` / `dis_dsp` | 何もしない（排他にならない） | 実際に割込み・ディスパッチを禁止 |
| フォースセンサ | `Space` / パネル / 自動スタートで 2N が入る | 実測 |
| Bluetooth | 常に「未接続」を返す。ログは標準出力へ | 実際に接続する |

**優先度に依存した排他**（高優先度タスクが走っている間は低優先度が動かない、という前提）は
シミュレータでは成立しません。共有データはセマフォで明示的に守ってください。

`main_task` 以外のタスクを使う場合は、`.cfg` に書く内容と同じものを実行時に登録します。

```c
etsim_register_task(ID_SUB_TASK, sub_task, 0);
etsim_register_cyclic(ID_CYC, cyclic_handler, 0, 4 * 1000, 0);  /* 周期 4ms */
etsim_register_semaphore(ID_SEM, 1, 1);
```

### ポート割り当て

既定は走行体規定書 M6.2「４ ケーブルの配線」の配線例に合わせてあります。
規定書にも「この配線方法は一例です。使用するポートなどは任意に選択可能です」とあるので、
実機の配線が違う場合は環境変数で変えられます。

| ポート | デバイス | 環境変数 |
|---|---|---|
| A | 右モータ | `ETSIM_PORT_RIGHT` |
| B | 左モータ | `ETSIM_PORT_LEFT` |
| C | アームモータ | `ETSIM_PORT_ARM` |
| D | フォースセンサ | `ETSIM_PORT_FORCE` |
| E | カラーセンサ | `ETSIM_PORT_COLOR` |
| F | 超音波センサ | `ETSIM_PORT_ULTRASONIC` |

既定値は `protocol/etsim_ports.h` の 1 か所に置いてあり、シミュレータ側
（`controller/src/etsim_device.c`）とサンプルアプリ（`controller/app/etrobo_app.c`）の
両方がそこを見ます。**片方だけ変えるとコンパイルが通りません**（`_Static_assert`）。

これはわざとです。ポートがずれても「モータは見つかる」ので、
アームモータを左輪として回して**その場で回るだけ**になり、
実行してみるまで気づけません。

---

### 自分たちの走行プログラムを動かす

ETロボコン環境（`~/etrobo/workspace/` に置いて `make app=...` するもの）の
C++ プロジェクトを、**ソースに手を入れずに**ここで走らせられます。

```powershell
cd controller
cmake -S . -B build_eyes -DETROBO_APP=eyes2026 -DEYES2026_DIR=<リポジトリのパス> \
      -DETROBO_BIN_DIR=bin_eyes
cmake --build build_eyes --config Release

# Unity を再生状態にしてから、走行プログラムの置き場で実行する
# （Scenario.csv などを相対パスで読むため）
cd <リポジトリのパス>
<EtroboSim>/controller/bin_eyes/etrobo_app.exe
```

前の走行プログラムが残っていると、2 つが同時に指令を送り合って
おかしな動きになります。走らせ直す前に終了しているか確かめてください。

成り立つ理由は、**`spikeapi` が C の `pup_*` API を薄く包んだインラインのラッパ**
だからです。`spikeapi::Motor::getCount()` の中身は `pup_motor_get_count()` の
呼び出しだけで、その `pup_*` はこのシミュレータが実装しています。
公式ヘッダ（`etrobocon/spike-rt-RasPike-ART` の `drivers/include/libcpp/spike/`）を
`controller/include/libcpp/spike/` に置いてあり、ヘッダに実装が無い分
（`IMU::getAcceleration` など）は上流の `drivers/libcpp/spike/*.cpp` を
`controller/src/libcpp/` に置いています。

**ソースの一覧は `Makefile.inc` の `APPL_CXXOBJS` から読みます。**
向こうでファイルが増えても CMake を直す必要はありません
（直書きにしていたときは、増えるたびに未定義参照になりました）。
構成時に「走行プログラムのユニット: N 本 (Makefile.inc より)」と出ます。

**app.cfg の代わり**は `controller/apps/<アプリ名>/` に置きます。
実機ではカーネルコンフィギュレータが `CRE_TSK` / `CRE_CYC` を処理しますが、
PC ではその工程が無いので、`kernel_cfg.h` で ID を定義し、
`etsim_app_register_objects()` の中で `etsim_register_task()` /
`etsim_register_cyclic()` を呼びます。`apps/eyes2026/` が実例です。

**スタートの合図**は、キーボードの `Space` なら中央ボタンとフォースセンサの
**両方**に入ります。走行体を始動させる合図はチームによってどちらかに分かれており、
実機ではどちらも人が押すからです（`SimManager.StartSignalForceN` = 2N）。
HUD のパネルからは**片方だけ**押せるので、どちらで待っているのかを切り分けられます。

押す時刻を決めておくこともできます。`-etsimPressForce 3,12` で、制御プロセスの接続から
3 秒後と 12 秒後にフォースセンサが押されます。**走っている途中でもう一度押したい**とき
（接続待ちのキャンセルとシナリオ中のボタン待ちで 2 回押すなど、実機では人が複数回押すもの）に使います。
自動スタートは走り出したら押すのをやめるので、2 回目はこちらで出します。

**Bluetooth の接続状態**（`hub_bluetooth_is_connected`）は**常に「未接続」**を返します。
シミュレータに相手がいないためです。接続を待ってから走り出す作りだとここで止まるので、
**フォースセンサで待ちを抜ける**逃げ道を用意しているか確かめてください
（実機でも人が押して抜けるはずです）。広告の開始・停止は成功したことにします。

**Bluetooth のログ**（`serial_wri_dat`）は**制御プロセスの標準出力へそのまま出ます**。
シミュレータに Bluetooth の相手はいないので、ポートの代わりに標準出力へ流しています。
コンソールから起動していればその場に流れ、ファイルに残したいときは
`ETSIM_SERIAL_LOG=<パス>` を指定します。

```powershell
# 画面で見る（走行プログラムの置き場で実行する）
& <EtroboSim>\controllerin_eyes\etrobo_app.exe

# ファイルに残す
$env:ETSIM_SERIAL_LOG = "C:	empt.csv"
& <EtroboSim>\controllerin_eyes\etrobo_app.exe
```

`-WindowStyle Hidden` の `Start-Process` で動かすと画面には出ないので、
`-RedirectStandardOutput` を付けるか `ETSIM_SERIAL_LOG` を使ってください。

**文字化けするとき**: 制御プロセスの出力は UTF-8 です（`/utf-8` でビルドしています）。
コンソールに直接出す分は、起動時に出力コードページを UTF-8 に切り替えるので化けません
（終了時に元へ戻します。強制終了したときだけ戻らないので `chcp 932` で戻してください）。
**パイプやファイルに流すとコンソールを通らない**ので、受け側を UTF-8 にします。

```powershell
# PowerShell 5.1 は既定で CP932 として読むので、捕まえる前に UTF-8 にする
[Console]::OutputEncoding = [Text.Encoding]::UTF8

# ファイルに落としたものを読むとき
Get-Content app.log -Encoding UTF8
```
出てこないときは、走行プログラムが `serial_opn_por()` を呼べているかを見ます。
成功していれば `[etsim] シリアルポート 2 を開きました (Bluetooth の代わり)` が出ます。

**「接続していないとログを出さない」作りのとき**は、環境変数
`ETSIM_BT_CONNECTED=1` を立てて起動します。`hub_bluetooth_is_connected()` が
「接続済み」を返すようになり、待ち構えている側がログを出し始めます。
既定は未接続のままです（相手がいないので、接続を待つ作りのアプリを
フォースセンサで抜けさせるため）。シリアルの出力先はどのみちこちらの
標準出力なので、接続済みと返しても嘘にはなりません。

```powershell
$env:ETSIM_BT_CONNECTED = "1"
.\controller\bin_eyes\etrobo_app.exe
```

実機では本物の Bluetooth SPP なので、走行体をペアリングして割り当てられた
COM ポートを端末ソフトで開きます。シミュレータと違って**送信が詰まります**。
実機で送りきれずに捨てているログは、シミュレータでは全部出る点に注意してください。

gcc と MSVC の差（`M_PI` が出ない、`int32_t` が入らない）は
CMake 側で吸収しているので、**走行プログラムには手を入れません**。

---

## 4. コースを差し替える

コースは JSON 1 ファイルで完全に記述します。書式は [`course/README.md`](course/README.md) を参照。

### 公式コース図 (PDF) から作る

公式配布のコース図がベクタ PDF なら、**トレースせずそのまま取り込めます**。
線は「中心線 + 線幅」、面は「塗りパス」として PDF に入っているので、
画像をトレースするより正確で、曲線もベジエのまま取り出せます。

```bash
# 1. 何が入っているか見る（色・線幅・面積の一覧）
python tools/pdf_to_course.py コース図.pdf --inspect

# 2. 変換する。--scale は「図面の 1mm が実寸で何 mm か」
#    縮尺 1/2 の図面なら 2.0。線幅が実寸 20mm になるかで検算できる
python tools/pdf_to_course.py コース図.pdf -o course/etrobo_2027.json --scale 2.0

# 3. スタート姿勢と計測ゲートを足す
python tools/add_race_setup.py course/etrobo_2027.json -o course/etrobo_2027_L.json \
    --line-id line_12_0 --start-near 2520 3427 --goal-near 321 1122 --edge left

# 4. 絵にして確認する（Unity を起動しなくてよい）
python tools/preview_course.py course/etrobo_2027_L.json -o preview.png --gates
```

2026 年度のコースは取り込み済みです（`course/etrobo_2026_L.json` / `_R.json`）。
作り直すときは**このままの引数**で実行してください。`--fill-min-area` を上げると
AR マーカーが落ち、`--line-min-width` を上げると 2mm の赤い指示線が落ちます。

`--line-min-width` は**図面上の線幅**で見ます（実寸ではありません）。既定の 1.0 だと
実寸 1.8mm のガレージの枠（図面では 0.9mm）が落ちて、**フィニッシュが判定できなくなる**ので
`0.5` を指定しています。この値で増えるのは L/R のガレージの枠 2 本だけです。

```bash
PDF=2026_competition_course_50pct_v0.3.pdf

python tools/pdf_to_course.py $PDF -o course/etrobo_2026.json \
    --scale 2.0 --fill-min-area 0.5 --line-min-width 0.5 \
    --name "ETロボコン 2026 競技コース (公式データ v0.3)"

# --sensor-offset は走行体の color_sensor_x_mm と揃えること。
# ここがずれると、スタート時にセンサが本線の縁に乗らない。
python tools/add_race_setup.py course/etrobo_2026.json -o course/etrobo_2026_L.json \
    --line-id line_12_0 --start-near 2520 3427 --goal-near 321 1122 --edge left \
    --sensor-offset 40 \
    --name "ETロボコン 2026 競技コース L (公式データ v0.3)"

python tools/add_race_setup.py course/etrobo_2026.json -o course/etrobo_2026_R.json \
    --line-id line_10_0 --start-near 2940 3427 --goal-near 5139 1122 --edge right \
    --sensor-offset 40 \
    --name "ETロボコン 2026 競技コース R (公式データ v0.3)"

cp course/etrobo_2026*.json unity/EtroboSim/Assets/StreamingAssets/
```

### 塗り規則 (`fill_rule`)

文字やロゴは**外側の輪郭と内側の輪郭 (穴)** でできています。これを別々の塗りに
すると穴が埋まり、「ロ」が「■」になります。そこで `shape: "polygon"` は
`contours`（輪郭の配列）を取れるようにしてあり、どこが穴になるかは
`fill_rule` で決まります。

- `"nonzero"`（既定）— 向きが逆の輪郭だけが穴になる。PDF の既定と同じ。
- `"evenodd"` — 輪郭が重なったところが必ず穴になる。

矢印のように**同じ向きの輪郭が重なっている**図形を偶奇規則で塗ると穴があくので、
PDF の塗り規則をそのまま持ってきています。矩形の向き（PyMuPDF の
`("re", Rect, 向き)`）も落とすと穴が埋まるので、頂点順に反映しています。

### SVG から作る

PDF がラスタ画像だった場合や、自分で形を描く場合は SVG 経由も使えます。

```bash
python tools/svg_to_course.py コース図.svg -o course/my_course.json --line-width 20
```

コース定義のセグメント表現は SVG のパスコマンドと同じ意味に揃えてあるので、
形は変わりません（Y 軸の向きだけ変換時に反転します）。
どちらの経路でも、難所・ゲート・スタート姿勢は自動生成されないので手で足します。

### 難所（2026 公式）

ET相撲・ボトルデリバリー・ETラリーは `tools/add_nansyo.py` で置きます。
**置き場所はコース図に印刷済み**なので、座標は手で書かずに JSON から読み取ります。

```bash
python tools/add_nansyo.py course/etrobo_2026_L.json -o course/etrobo_2026_L_nansyo.json \
    --gate red G2-5 --gate blue G5-3 --gate yellow G1-2 --carry-color blue
```

力士ボトルは既定で土俵の中心に置きます。規約 5.15.1 は「土俵の任意の位置」なので、
`--rikishi-offset DX DY` でずらせます（走行中に変えるならパネルから）。

ゲートポジションの名前は競技規約 5.17.1 の `GX-Y`（X が横、Y が縦、どちらも 1〜5）。
L コースは X が大きいほど右、Y が大きいほど下で、R コースは左右反転です。
向き（赤=横 / 青=縦 / 黄=横）は規約 表 5-2 のとおり自動で決まるので、
片方のゲートポジションだけ指定します。

寸法は難所組立図 1.0.2 と競技規約 1.0.1 から取っています。

| 物 | 寸法 |
|---|---|
| 力士ボトル | PET 500丸 (M1-354-02) φ68 × 210H、黒ラベル、水入りで **約 105 g** |
| キャリーボトル | PET 300丸 (M1-354-01) φ62 × 150H、赤青黄ラベル、**約 105 g** |
| ゲート | VP13 塩ビ管、内寸 **232 × 245 mm**（脚をゲートポジションの上に置くため） |

ゲートの内寸は組立図では「約 240mm」ですが、規約の設置方法は
「二つのゲートポジション上に足元を設置する」で、ゲートポジションの間隔は 250mm です。
**設置方法の方に合わせて** 250 − 管の外径 18 = 232mm にしてあります
（狭い方に倒しているので、通れる判定が甘くなりません）。全幅 136mm の走行体に対して
片側 48mm の余裕です。

**重心は空瓶の重さから計算しています。** 総重量 105g のうち水が何 g かで決まり、
水は底に溜まるので重心はかなり下がります。空瓶 40g / 25g のとき、
力士ボトルは **41.5mm**、キャリーボトルは **26.2mm** です
（`--rikishi-empty-g` / `--carry-empty-g` で変えられます）。

見た目も実物に寄せてあります。

- **ボトル**: 円柱ではなく、胴・肩・首・キャップの断面を回した回転体。
  実物と同じく**接地円は胴より小さい**（胴の 0.8 倍）ので、
  倒れやすさ（接地円の半径 ÷ 重心高さ）が実物に近くなります。
  PET なので**半透明**で描き、養生テープのラベルだけ不透明です。
- **ゲート**: 色付きの管（ビニテープ）と**灰色の TS 継手**の塗り分け。
  上の角は TS エルボ、足元は TS チーズを**ゲートの面と直交に寝かせて**います。
  これが自立する脚で、組立図 図5-2 の側面図どおりです。

**ボトルには転がり減衰を入れてあります。** PhysX は転がり抵抗を持たないので、
これが無いと横倒しになったボトルがコース上をいつまでも転がり続けます
（実測で 100 秒たっても止まりませんでした）。判定の「底面が接地して静止していること」が
いつまでも確定しないので、実物どおり 1 秒ほどで止まるようにしてあります。
立っている間はほとんど効きません。

当たり判定はボトルが凸 MeshCollider、ゲートは管ごとのカプセルです。
ボトルにカプセルを使うと底が丸くて自立せず、
「底面が接地して静止していること」という競技の判定が再現できません。

### 難所の配置を走行中に変える

**L コースと R コースはパネルの `L` `R` で切り替えられます。**
コースは再生開始時に 1 度だけ組み立てるので、押すとシーンを読み直します
（走っていた分は破棄されます）。難所の配置は引き継ぎ、力士ボトルのずれだけは
鏡像に合わせて左右を反転します。ファイル名の `_L` / `_R` を入れ替えたものを探すので、
`etrobo_2026_L_nansyo.json` の相方は `etrobo_2026_R_nansyo.json` です。

キャリーボトルの色とゲートの配置は、**大会当日にならないと分かりません**
（規約 5.16.1「参加者は、ボトルの色をリクエストすることはできない」、
6.3.1「ゲートの配置パターンは大会当日の試走前に公開される」）。
コースを作り直さなくても、HUD のパネルからその場で変えられます。

- **キャリーボトルの色** — `赤` `青` `黄` のボタン。いま置かれている色が `[青]` のように囲まれます
- **ゲートの配置** — 色ごとに `G3-3+G4-3` のように現在位置が出ます。
  `X-` `X+` `Y-` `Y+` で 1 マスずつ動かします。向き（赤=横 / 青=縦 / 黄=横）は
  規約 表 5-2 のとおり色で決まるので、指定するのは片方のゲートポジションだけです。
  端まで行ったときは相方が内側に折り返るので、5 列目でも置けます
- **コース** — `L` `R` で走行体を出す側を選びます（上記）
- **力士ボトルの位置** — 規約 5.15.1 は「スタート準備時に**土俵の任意の位置**に
  設置される」としか言っていません。土俵の中心からのずれ [mm] を `X-` `X+` `Y-` `Y+`
  で 20mm ずつ動かし、`中央` で戻します。`X+` はシートの右、`Y+` は奥です
  （ゲートと違い、R コースでも反転しません）。
  ボトルの底が土俵からはみ出す手前（中心から 149.8mm）で止まります

`R` キーでリセットすると、走行体だけでなく**ボトルも設定した位置へ立て直します**。
これが無いと、一度押し出した力士ボトルが土俵の外に転がったままで走り直せません。

起動時に決めておくこともできます。`add_nansyo.py` と同じ書き方です。

```bash
"$UNITY" -projectPath unity/EtroboSim -executeMethod Etrobo.Sim.EditorTools.SimSceneSetup.OpenAndPlay \
    -etsimCourse etrobo_2026_L_nansyo.json \
    -etsimGates red=G4-1,blue=G1-1,yellow=G3-3 -etsimCarryColor red     -etsimRikishi 100,-80
```

**パネルは走行体が出てくる側と反対に出ます。** L コーススタートならパネルは右、
R コーススタートなら左です（`SimBootstrap` のコースから自動で決まります）。
スタート地点の上にパネルが重なって、走り出しが見えないのを避けるためです。

なお HUD は IMGUI なので、`-etsimScreenshot` で撮った画像には**写りません**
（カメラの RenderTexture を保存しているため）。パネルの状態を確かめるときは
Unity のログに出る `[Etsim] 赤ゲートを G4-1 へ置きました` 等を見てください。

### 難所の成立判定

3 つの課題の成立を判定して、HUD とログに出します。判定は競技規約 1.0.1 の
5.15 / 5.16.2 / 5.17.3 のとおりです。どれも「リザルトポイント提示時」（5.4.7）に
判定するものなので、走行中はいつでもひっくり返ります。ゴールした時点の内容を
`[Etsim] 難所 …` の 1 行でログに残します。

| 課題 | 成立の条件 | ポイント |
|---|---|---|
| ボトル押し出し | 力士ボトルの**底面が土俵の外に完全に**出ていて、接地して静止している。横倒しは不可 | 5 |
| デリバリー | キャリーボトルの底面がドロップゾーンに**一部でも**かかって立っている | 1 |
| 〃（色一致） | 置いたゾーンの色がボトルの色と同じ | 5 |
| ETラリー | 赤→青→黄 の順に**走行体全体**が通る。1/2/3 周回 | 5 / 10 / 15 |
| フィニッシュ | 走行体の一部がガレージ内で 3 秒間完全停止 | 3 |

フィニッシュ（5.14）も判定します。**ガレージ**（5.2.4、「ガレージ手前の黒線から、
灰色に囲まれた白色の領域」）に走行体の一部が入った状態で **3 秒間完全停止**すると成立し、
同時に競技終了です。ガレージの灰色の枠もコース図に印刷されているので、座標は手で書かずに
読み取っています（L コースで 250 × 320mm、中心 (321, 200)）。

**完全停止**（5.11）は「ハブに直接つながっている全てのモータが停止または保持を続け、
走行体が機械的に停止している状態」。指令ではなく実際の動きで見るので、
止めていても保持していても車輪が回っていなければ成立します。
最大計測時間は模擬していないので、その制限だけは入っていません。

判定の細かいところ:

- **「完全に押し出す」** は底面の円で見ます。土俵の線にかかっているうちは成立しません
- **「静止している」** は 10mm/s 以下、**「横倒しでない」** は傾き 30 度以内としました。
  規約に数値は無いので、ここは実際の審判より機械的です
- **ゲート通過**は「走行体の四隅がゲートの面をまたぎ切ったこと」で見ます。
  またいでいる間に 1 つでも脚の外へ出たらその通過は数えません。
  方向と接触は不問（規約どおり）、**同色の連続通過は 1 回**、
  順番を外すと 1 番目からやり直しで、成立した周回数は累積します
- 走行体の外形は車体だけでなく**キャスタと車輪まで**含めて見ます（「走行体全体」）

**成立しないときは理由も出ます。**「不成立」だけだと、まだ土俵の中なのか、
倒したのか、転がっている最中なのかが分かりません。押し出しなら
`不成立（土俵にかかっている（中心から 133mm、外まであと 85mm））`、
デリバリーなら `不成立（ドロップゾーンの外）` のように出ます。
`横倒し` と `まだ動いている（N mm/s）` は両方に共通です。

判定していないもの: フライングスタート。
HUD に出る「難所 N ポイント」は上の表の 3 つだけの合計で、
チェックポイント・LAP ゲート・フィニッシュのポイントは含みません。

### 難所（自作）

`props` に置くと立体物として生成され、走行体と物理的に当たります。

- `block` / `cylinder` — `movable: true` にすると押して動かせる
- `wall` — 固定の壁（ガレージなど）
- `ramp` — 斜面
- `seesaw` — 支点まわりに傾く板（ヒンジで実装）

### 計測

`gates` に置いた線分を走行体の中心が横切ると反応します。
`start` で計測開始、`checkpoint` を全部通ってから `goal` を跨ぐと完走扱いです。
チェックポイントを設けているのは、スタート直後にゴール線をまたいで
即完走になるのを防ぐためです。

### 表示の細かさ

コースの形は**ベクタのまま**保持していて、カラーセンサはその幾何を直接サンプリングします。
つまり**センサの精度は表示解像度と無関係**です。

画面に見えるコースは、その幾何を 1 枚のテクスチャに焼いたものです。
細かさは `SimBootstrap` の 2 つで決まります。

| 設定 | 既定 | 意味 |
|---|---|---|
| `Sheet Texture Pixels Per Mm` | 1.0 | 1mm あたりの画素数 |
| `Max Sheet Texture Pixels` | 20,000,000 | 総画素数の上限。超える場合は自動で解像度を落とす |

既定では公式コース (5460 x 3640mm) が 5460 x 3640 画素、等倍で焼かれます（約 0.7 秒）。
ラリーエリアの AR マーカーは 1 片 2.4mm なので、この解像度で 2〜3 画素になります。
もっと寄って見たい場合は両方を上げてください（メモリと起動時間が増えます）。

コースシートは**陰影を付けずに**描いています。印刷物なのでテクスチャの色が
そのまま出るのが正しく、かつ「画面で見える色 = センサが読む色」になります。
陰影を付けると白 (255) と薄い灰 (220) の差が露出で飽和して消え、
ラリーエリアの灰色の丸が見えなくなります。

### 画面を画像に保存する

見え方の確認を目視の報告に頼らず記録できるよう、起動時に画面を撮れます。

```bash
UNITY="C:\Program Files\Unity\Hub\Editor\2022.3.41f1\Editor\Unity.exe"
```

```bash
"$UNITY" -projectPath unity/EtroboSim -executeMethod Etrobo.Sim.EditorTools.SimSceneSetup.OpenAndPlay -etsimScreenshot shot.png -etsimScreenshotDelay 3
```

床テクスチャだけを書き出すこともできます（原図との突き合わせ用）。

```bash
"$UNITY" -batchmode -nographics -projectPath unity/EtroboSim -executeMethod Etrobo.Sim.EditorTools.SimSceneSetup.ExportCourseTexture -etsimCourse etrobo_2026_L.json -etsimExport texture.png -etsimPixelsPerMm 1
```

---

## 5. 走行体のパラメータを合わせ込む

`course/robot_spike.json` が走行体の寸法・特性です。ここを実測値へ寄せるほど、
シミュレータでのチューニング結果が実機で再現します。

特に効くもの:

| 項目 | 意味 |
|---|---|
| `wheel_diameter_mm` / `tread_mm` | 旋回半径とオドメトリが直接ずれる |
| `no_load_rpm` / `stall_torque_nm` | 加速と最高速。モータの実測から入れる |
| `color_sensor_x_mm` | センサが車軸より前にあるほど安定する（先読み効果） |
| `color_sensor_spot_mm` | 反射光量が黒→白へ変わる幅。ライントレースの応答に直結 |
| `reflection_black` / `reflection_white` | 実機のキャリブレーション値をそのまま入れる |
| `left_motor_mirrored` | 左右モータの取り付け向き。間違えると走行体がその場で回る |
| `slip_stiffness` / `wheel_inertia` | 駆動力の立ち上がり。**下の注意を読んでから触ること** |
| `rolling_resistance` | 転がり抵抗。惰行がどれだけで止まるか |

`slip_stiffness` を上げる、または `wheel_inertia` を下げるときは注意してください。
車輪の回転と駆動力は互いに効き合うので、

    slip_stiffness × 車輪半径² × 物理ステップ幅 ÷ wheel_inertia

が 1 を超えると、1 ステップごとに符号が反転する振動が出ます。摩擦円で頭打ちに
なるため発散はせず、**モータ指令が 0 でも走行体がじりじり動く**という形で出ます。
いまは車輪の角速度を陰解法で先に解いているのでこの比が 1 を超えても安定ですが、
極端な値では精度が落ちます。指令 0 のまま動いていないかは、
Unity のログに出る `[Etsim] モータ指令が 0 のまま ... 動きました` で見張っています。

`left_motor_mirrored` について: プロトコルが運ぶ `motor_power` は
**モータ軸そのものの値**であって「前進が正」ではありません。実機では左右のモータが
鏡像に付くので、アプリが `pup_motor_setup()` で片側を反転させて初めて前進が正になります。
シミュレータ側がこの取り付け向きを持っていないと、アプリ側の反転がそのまま残って
走行体が前に進まなくなります。

### 正式な走行体 (2026 ベーシック: Re-HackS)

**既定は `course/robot_rehacks_2026.json`** です。寸法は公式の LDraw モデルから実測しました。

組立図は LDraw 形式で公開されています。PDF の絵を測るのではなく、
**モデルを展開して部品の絶対座標を出す**ほうが厳密です（1 LDU = 0.4mm）。

```bash
git clone https://github.com/ETrobocon/HackSPi_BuildingInstructions
# ReHackSM6.ldr が Re-HackS の最上位モデル。
# LDraw の部品ライブラリ (https://library.ldraw.org/library/updates/complete.zip) と
# 合わせて展開すると、頂点まで含めた外形が出せます。
```

座標系は「左右中心 / 車軸中心 / 床面」を原点、+x が前方。

| 項目 | 実測値 | 備考 |
|---|---|---|
| 車輪 | Φ56.0 / 幅 14.0 mm | |
| トレッド | **112.0 mm** | 左右タイヤの中心間 |
| 全長 / 全幅 / 全高 | 205.6 / 136.0 / 159.6 mm | 全長は前へ伸びるアームを含む |
| カラーセンサ | 前方 **40.0** mm / 床から **8.4** mm | レンズ面。真下向き |
| 距離センサ | 前方 **17.6** mm / 床から **148.0** mm | 発信面。走行体の最上部で水平前向き |
| キャスタボール | 後方 **86.4** mm / 半径 11.2 mm | 組立図では床から 3.2mm 浮いた姿勢 |
| フォースセンサ | 後方 74.8〜98.4 mm | |

**距離センサが床から 148mm ある**点に注意してください。水平前向きなので、
コース上の 55mm のブロックは視野に入りません。

### 走行体の見た目

走行体は**組立図そのもの**を描いています。箱で近似するのではなく、LDraw の
部品データを三角形に展開したメッシュ（`course/robot_rehacks_2026.etmesh`、
347,904 三角形）を起動時に読み込みます。FBX などの外部アセットは使わず、
寸法データと同じ扱いで StreamingAssets に置く方針は変えていません。

```bash
python tools/ldraw_to_mesh.py HackSPi.mpd -o course/robot_rehacks_2026.etmesh \
    --model ReHackSM6.ldr --ldraw-lib <LDraw ライブラリ>/ldraw
cp course/robot_rehacks_2026.etmesh unity/EtroboSim/Assets/StreamingAssets/
```

メッシュは body / wheel_l / wheel_r / arm の 4 つに分かれていて、車輪とアームは
Unity 側で回ります。アームの回転中心も組立図から取った実際の軸位置です。
`visual_mesh` を空にするか、ファイルが無ければ従来どおり箱で描きます
（**見た目だけの話で、物理とセンサは一切変わりません**）。

LDraw に質量の情報は無いので、**質量と重心だけは推定値**です（`_mass_note` 参照）。
ここだけは実機で測って差し替えてください。

| キー | 測り方 |
|---|---|
| `mass_kg` | 電池込みの実測 |
| `center_of_mass_x_mm` | 車軸中心から重心まで（+ が前）。指で支えて釣り合う位置で足りる |
| `center_of_mass_height_mm` | 床から重心まで |
| `reflection_black` / `reflection_white` | コースの黒線と白帯の上で `pup_color_sensor_reflection()` を読んだ値 |
| `color_sensor_spot_mm` | 黒線を横切らせて、反射光量が変わりきるまでの移動距離 |

寸法を変えたら、**スタート姿勢も作り直してください**。`add_race_setup.py` は
カラーセンサが本線の縁に乗るように走行体を置くので、`--sensor-offset` が
`color_sensor_x_mm` と一致していないとスタート位置がずれます。

```bash
python tools/add_race_setup.py course/etrobo_2026.json -o course/etrobo_2026_L.json \
    --line-id line_12_0 --start-near 2520 3427 --goal-near 321 1122 --edge left \
    --sensor-offset 40
```

### 止まっているときに動かないこと

指令が 0 のときは、位置も**向きも**動かないのが正しい状態です。
`-etsimTruthLog <秒>` を付けると真の位置と方位を定期的にログへ出すので、
じわじわした流れを数字で追えます。**ボトルの位置・傾き・速さ・回転も一緒に出る**ので、
ぶつけたあとちゃんと落ち着いたかもここで確かめられます。

```bash
"$UNITY" -projectPath unity/EtroboSim -executeMethod Etrobo.Sim.EditorTools.SimSceneSetup.OpenAndPlay     -etsimCourse etrobo_2026_L_nansyo.json -etsimAutoStart 0 -etsimTruthLog 20
```

`-etsimRobot <ファイル>` で走行体パラメータを差し替えられるので、
どのパラメータが効いているかは値を振って比べられます。

再生中は常駐のウォッチドッグも見ていて、指令 0 のまま動くと
`[Etsim] モータ指令が 0 のまま … 動き … 回りました` と警告します。

### ソナーが何を見ているかを調べる

`-etsimSonarSweep` は、開始位置で走行体をその場で 1 周させながら
超音波センサを読み、方位と距離を CSV でログに出します。
しきい値をいくつにすれば狙った物だけを拾えるか、
関係ない物（ゲートや壁）が何 mm のところに居るかを、
走らせる前に数字で決められます。

```bash
# 1 度刻みで一瞬で測る（数字だけ）
"$UNITY" -projectPath unity/EtroboSim -executeMethod Etrobo.Sim.EditorTools.SimSceneSetup.OpenAndPlay     -etsimCourse etrobo_2026_L_nansyo.json -etsimStart 970.3,1700.8,0 -etsimAutoStart 0 -etsimSonarSweep 1

# 2 度刻みで 20 秒かけて回す。ビームと当たった点が画面に残る
… -etsimSonarSweep 2,20

# さらにコマを連番 PNG で保存する（3 つ目がその出力先）
… -etsimSonarSweep 2,20,out/frames
```

`[Etsim] 掃引CSV heading_deg,distance_mm` の行に続いて 1 点ずつ出ます。
距離が `-1` の点は計測できなかった（範囲外・入射角が浅すぎる）ところです。
`-etsimRikishi X,Y` と組み合わせると、ボトルの置き場所を変えたときに
距離がどう変わるかを比べられます。

### ゲインを探索する

`tools/tune_gains.ps1` は Unity を 1 回だけ起動して再生状態にし、
あとは制御プロセスをゲインを変えながら繰り返し起動します。
サンプルアプリは接続時にシミュレータをリセットするので、毎回スタート地点からです。
Unity を開いたまま exe を起動し直すだけで走り直せます
（接続し直すと `[Etsim] 制御プロセスが接続しました (第 N 世代)` が出ます）。

```powershell
# 配列は呼び出し演算子で渡す (-File 経由だと 40,50 が 1 個の文字列になる)
& .\tools\tune_gains.ps1
& .\tools\tune_gains.ps1 -BasePowers @(40,50) -Kps @(0.6,0.9)

# 同じ条件を 5 回ずつ走らせて成功率で見る
& .\tools\tune_gains.ps1 -BasePowers @(60) -Kps @(0.9) -Kds @(0.08) -Repeat 5
```

ゲインは**再ビルド無しで**環境変数から変えられます（`ETROBO_ON_SIMULATOR` のときだけ有効）。

| 環境変数 | 既定 |
|---|---|
| `ETROBO_KP` / `ETROBO_KI` / `ETROBO_KD` | 0.38 / 0.030 / 0.080 |
| `ETROBO_TARGET` | 50（目標反射光量） |
| `ETROBO_BASE_POWER` | 30（直進時の出力 %） |
| `ETROBO_MAX_STEERING` | 25（操作量の上限） |

**`ETROBO_MAX_STEERING` は基準出力と揃えてください。** 上限が基準出力より小さいと
片輪を止めきれず、急なカーブで曲がりきれません。

Unity のログに出る次の行で合否を判定しています。

```
[Etsim] チェックポイント通過 cp1 (1/3) t=8.76s
[Etsim] ゴール 計測タイム 34.58 秒
[Etsim] コースアウト 位置 (1234, 567) mm  最寄りの線まで 312 mm
```

**完走率で見てください。** FREERUN は実時間で非同期に回るのでゲイン以外の要因でも
結果が変わります。2026 公式コース (L) の実測は次のとおりで、完走時のタイムは
ほとんどばらつきませんが、完走率は基準出力で変わり、**速いほど良いわけではありません**。

走行体 Re-HackS の実寸での実測（`-Repeat 8`、走り出さなかった回はやり直し）:

| 基準出力 | 完走 | タイム |
|---|---|---|
| **40** | **8/8** | **26.18 s** (26.17〜26.20) |
| 50 | 6/7 | 20.89 s |

既定値は Kp 0.9 / Ki 0.03 / Kd 0.08 / 基準出力 40 / 上限 40 です。
それでも基準出力を上げると取りこぼしが出ます。
**これはゲインの問題ではありません。** 計測区間の本線には、走行体が狙う左縁の上に
ゲートポジションの灰色の丸（反射 64）と青い区間標識（反射 38、長さ 100mm）が乗っており、
目標値 50 に対して ±13 の外乱になります。さらに緑マットの反射は 48 で目標値とほぼ同じなので、
一度白帯の外へ出ると偏差が消えて戻れません。安定して完走させるには、反射光量だけでなく
色 (HSV) を見るか、外乱の区間を検出して無視する必要があります。

Unity を立ち上げずに当たりを付けたいときは `tools/mock_sim.py` を使います。
こちらも同じ規則でゲートを判定し、最後に 1 行でまとめを出します。

```
[result] goal time=33.36 cp=3/3 pos=330,1121 sim=35.64
```

---

## 6. 2 つの動作モード

### FREERUN（既定）

Unity は自分のペースで物理を進め、制御プロセスは最新のセンサ値を読みます。
実機と同じ非同期動作で、通信の遅れやセンサの取りこぼしも起きます。
**普段はこちら**を使ってください。

### LOCKSTEP

制御プロセスが指令を送るたびに Unity が物理を 1 ステップだけ進めます。
実行するたびに同じ結果になるので、回帰テストや「この変更でタイムが縮んだか」の
比較に使えます。実時間より速くも遅くもなります。

```bash
.\controller\bin\etrobo_app.exe --lockstep
```

---

## 7. 検証

### プロトコルのバイト配置

C 側と C# 側は「同じバイト列」でしか合意していません。どちらかを変更したら必ず両方直します。

```bash
cd controller
.\build.ps1
.\bin\protocol_check.exe
```

### Unity 側の自己テスト

コース JSON の解釈、円弧・ベジエの折れ線化、カラーセンサが読む色、
計測ゲートの判定、パケットのバイト配置を再生なしで検証します。

エディタのメニュー **ETロボコン → 自己テストを実行**、またはコマンドラインから:

```bash
"C:\Program Files\Unity\Hub\Editor\2022.3.41f1\Editor\Unity.exe" -batchmode -nographics -projectPath unity\EtroboSim -executeMethod Etrobo.Sim.EditorTools.SimSelfTest.RunFromCommandLine -logFile selftest.log
```

---

## 8. ファイル構成

```
EtroboUnitySim/
├─ protocol/etsim_protocol.h      ← C と C# が共有する唯一の仕様
├─ controller/                     制御プロセス (PC 上で動く走行プログラム)
│   ├─ include/spike/              SPIKE-RT 互換ヘッダ
│   ├─ include/kernel.h            TOPPERS/ASP3 の shim
│   ├─ include/etsim/              シミュレータ接続ランタイム
│   ├─ src/etsim_device.c          ★ SPIKE-RT API の実装。API 変更時はここだけ直す
│   ├─ src/etsim_runtime.c         UDP 通信・時間進行
│   ├─ src/asp3_shim.c             タスク・周期ハンドラ・セマフォ
│   ├─ app/etrobo_app.c            ★ 走行プログラム（差し替える）
│   └─ tests/protocol_check.c      パケット配置の検証
├─ course/
│   ├─ README.md                   コース定義の書式
│   ├─ etrobo_2026_L.json          2026 公式競技コース L 側（既定）
│   ├─ etrobo_2026_R.json          2026 公式競技コース R 側
│   ├─ practice_oval.json          立ち上げ確認用の単純な周回コース
│   ├─ etrobo_practice_full.json   難所つきの練習コース（寸法は暫定）
│   └─ robot_spike.json            走行体のパラメータ
├─ tools/
│   ├─ pdf_to_course.py            公式コース図 PDF → コース定義（推奨）
│   ├─ svg_to_course.py            SVG → コース定義
│   ├─ add_race_setup.py           スタート姿勢と計測ゲートを足す
│   ├─ preview_course.py           コース定義 → PNG（Unity なしで確認）
│   └─ mock_sim.py                 Unity 不要のヘッドレス版シミュレータ
└─ unity/EtroboSim/                Unity プロジェクト
    └─ Assets/
        ├─ Scenes/SimMain.unity    SimBootstrap を 1 個置いただけのシーン
        ├─ Editor/                 メニュー・自己テスト
        └─ Scripts/
            ├─ Course/             コース JSON の解釈・幾何・色サンプリング・生成
            ├─ Net/                パケットと UDP サーバ
            ├─ Sensors/            カラー・超音波・IMU のモデル
            ├─ Vehicle/            走行体の物理とモータモデル
            ├─ Sim/                進行役・計測・シーン構築
            └─ UI/                 HUD とカメラ
```

---

## 9. うまくいかないとき

### ビルドは通るのに `LNK1104: ファイルを開くことができません` になる / 実行した exe が消える

**ウイルス対策ソフトが `etrobo_app.exe` を検疫しています。**

この制御プロセスは

- 署名がない
- ビルドのたびに中身が変わる（実績・評判がゼロ）
- 起動直後に UDP ソケットを開き、スレッドを 2 本立てて常駐する

という、ヒューリスティック検知の条件を全部満たしてしまいます。実行した瞬間に
検疫されてファイルが消え、以後そのパスへの書き込みがブロックされるため、
次のビルドが `LNK1104` で失敗します。

見分け方:

```powershell
cd controller
.\build.ps1
Test-Path .\bin\etrobo_app.exe     # ビルド直後は True
.\bin\etrobo_app.exe               # 実行してみる
Test-Path .\bin\etrobo_app.exe     # 消えていたら検疫されている
```

同じフォルダの `protocol_check.exe` が残るのは、こちらが通信も常駐もしないためです。

**対処**: ウイルス対策ソフトに `controller\bin` を除外登録してください。
出力先を 1 フォルダに固定してあるのは、この登録を 1 回で済ませるためです。

ノートン 360 の場合、**除外リストが 2 つある**点に注意してください。両方に登録が必要です。

1. 設定 → ウイルス対策 → 「スキャンとリスク」タブ
2. **スキャンから除外する項目** → 構成 → フォルダを追加 → `controller\bin`
3. **自動保護、SONAR、ダウンロードインテリジェンスの検出から除外する項目**
   → 構成 → フォルダを追加 → `controller\bin`

3 番目が本体です。実行中のプロセスを落としているのは SONAR（挙動検知）なので、
スキャン除外だけでは止まりません。

**除外登録だけでは足りません。** 一度検疫されたパスは、除外を入れたあとも
そのファイル名だけ作成できないままになります（同じフォルダの別名は作成できるので
フォルダ権限と誤診しやすい）。検疫の記録を消してください。

```
ノートン 360 → セキュリティ → 履歴（セキュリティ履歴）
 → 表示を「検疫」または「解決済みのセキュリティリスク」に切り替え
 → etrobo_app.exe の項目を選択
 → オプション → 「このファイルを復元して除外する」（または履歴から削除）
```

除外と検疫履歴の削除が両方済めば、以後は
「ビルド → 実行 → 再ビルド」が普通に回ります。検出名が `IDC:Generic` の場合は
ウイルス定義に一致したのではなく、**レピュテーション（評判）判定**による検出です。
自前ビルドの実行ファイルはビルドのたびにハッシュが変わるため、評判が蓄積せず
必ずこの条件に該当します。

> 業務用 PC で除外登録の権限がない場合は、情報システム部門へ
> 「自作の未署名 exe がローカル UDP 通信のため SONAR に検知される」と伝えてください。

### 代わりに WSL で動かす

Windows 側のウイルス対策を触れない場合、制御プロセスだけ WSL (Linux) で動かす手もあります。
ソースは POSIX でもそのままビルドできます。

```bash
cd controller
cmake -S . -B build && cmake --build build
ETSIM_HOST=<Windows 側の IP> ./bin/etrobo_app
```

Unity は Windows 側で動かしたまま、`--host` に Windows のアドレスを渡します。
WSL2 から Windows の localhost へは既定では届かないので、
`.wslconfig` でミラーモード (`networkingMode=mirrored`) にするか、
`/etc/resolv.conf` に載っているホスト IP を使ってください。

### Unity 側が「待機中」のまま

- Unity を**再生状態にしてから**制御プロセスを起動してください（逆だと 10 秒でタイムアウトします）
- ポートを変えている場合は両方揃えてください（Unity は `SimBootstrap` の Port、制御側は `--port`）
- 既定ではループバックのみ待ち受けます。別マシンから繋ぐ場合は
  `SimBootstrap` の **Allow Remote Controller** を有効にしてください

### 走行体がその場で回る

左右モータの取り付け向きが合っていません。
`course/robot_spike.json` の `left_motor_mirrored` と、走行プログラムの
`pup_motor_setup()` の方向指定のどちらかを反転させてください（詳細は 5 章）。

### 難所（ボトル・ゲート）が出ない

難所の入ったコース（`_nansyo` が付いたもの）を読んでいるか確かめてください。
起動ログの `props=5/5` は「**組み立てた数／JSON に書いてある数**」で、
合わないと警告が出ます。パネルの難所欄にも
「ボトルとゲートがありません」と赤で出ます。

---

## 10. 既知の制約

- **`etrobo_practice_full.json` の寸法は暫定**です。こちらは自作の練習用で、
  競技寸法ではありません。公式コースは `etrobo_2026_L.json` / `_R.json` を使ってください。
- 公式コースは **マットが緑**で、ライン沿いの幅 320mm の白帯を外れると緑になります。
  緑の反射光量は約 48 でライン縁の値に近いため、反射光量だけを見る制御では
  「白帯を外れたこと」に気づけません。実機と同じ性質なので、そのまま再現しています。
- 公式コースの装飾（ロゴ・ドット格子など）も面として取り込んでいます。
  面積 400mm² 未満の要素は除外しているので、原図と完全に同じ絵ではありません。
- フォースセンサは模擬していません（常に 0）。
- 走行体の見た目は寸法どおりのプリミティブです。LEGO パーツの外観は再現していません。
- シミュレータの物理は実機と完全には一致しません。`course/robot_spike.json` を
  実測値へ寄せたうえで、**最終確認は必ず実機で**行ってください。
