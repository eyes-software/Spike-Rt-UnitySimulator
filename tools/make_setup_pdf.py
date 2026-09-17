#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
make_setup_pdf.py : 新しい PC への導入手順を PDF にする。

README.md の要点を、移行のときに見る順番へ並べ直したもの。
中身は README を正とする。食い違ったら README を直してからここを直すこと。

PDF の生成には PyMuPDF (pymupdf) を使う。コース図の変換で既に使っているので
依存は増えない。日本語は Windows 同梱の BIZ UDゴシック を埋め込む。

使い方:
    python tools/make_setup_pdf.py -o docs/セットアップ手順.pdf
"""

from __future__ import annotations

import argparse
import datetime
import io
import os
import sys

import pymupdf

FONT_DIR = r"C:\Windows\Fonts"
FONT_REGULAR = "BIZ-UDGothicR.ttc"
FONT_BOLD = "BIZ-UDGothicB.ttc"
FONT_MONO = "msgothic.ttc"          # 等幅で日本語も出る

CSS = """
@font-face { font-family: jp;   src: url(fonts/%(reg)s); }
@font-face { font-family: jpb;  src: url(fonts/%(bold)s); }
@font-face { font-family: mono; src: url(fonts/%(mono)s); }

body    { font-family: jp; font-size: 9.5px; line-height: 1.55; color: #1a1a1a; }
h1      { font-family: jpb; font-size: 19px; margin: 0 0 2px 0; color: #12395c; }
h2      { font-family: jpb; font-size: 13px; margin: 16px 0 5px 0; color: #12395c; }
h3      { font-family: jpb; font-size: 10.5px; margin: 10px 0 3px 0; color: #24506f; }
p       { margin: 3px 0; }
ul, ol  { margin: 3px 0 3px 16px; padding: 0; }
li      { margin: 1px 0; }
b, strong { font-family: jpb; }
code    { font-family: mono; font-size: 9px; }

.lead   { font-size: 9px; color: #555; margin: 0 0 2px 0; }
.note   { font-size: 8.5px; color: #555; }

table   { width: 100%%; border-collapse: collapse; margin: 4px 0 6px 0; font-size: 9px; }
/* th に背景を敷くと、表がページをまたいだときに空の帯だけが残る。
   下線を太くして見出しに見せる。 */
th      { font-family: jpb; text-align: left; color: #12395c;
          border: 1px solid #b9c6d2; border-bottom: 2px solid #8fa6ba;
          padding: 3px 5px; }
td      { border: 1px solid #b9c6d2; padding: 3px 5px; vertical-align: top; }

/* Story は th/td のインライン width を見ないので、幅はクラスで決める */
.w-soft { width: 96px; }
.w-ver  { width: 104px; }
.w-key  { width: 96px; }
.w-sym  { width: 210px; }

pre     { font-family: mono; font-size: 8.5px; background: #f4f5f7;
          border-left: 3px solid #8fa6ba; padding: 5px 7px; margin: 4px 0;
          line-height: 1.4; }
"""


def build_html() -> str:
    today = datetime.date.today().strftime("%Y-%m-%d")
    return f"""
<body>

<h1>ETロボコン Unity シミュレータ<br/>新しい PC への導入と日々の使い方</h1>
<p class="lead">{today} 版 ／ 詳細は EtroboUnitySim/README.md を参照</p>

<h2>1. 必要なもの</h2>
<table>
<tr><th class="w-soft">ソフト</th><th class="w-ver">バージョン</th><th>備考</th></tr>
<tr><td class="w-soft">Windows</td><td class="w-ver">10 / 11 (64bit)</td><td>&nbsp;</td></tr>
<tr><td class="w-soft">Unity Hub<br/>Unity</td>
    <td class="w-ver">2022.3 LTS<br/>（確認済み 2022.3.41f1）</td>
    <td>外部アセットは使わない。コースも走行体も寸法から生成する</td></tr>
<tr><td class="w-soft">Visual Studio</td><td class="w-ver">2022 以降</td>
    <td>ワークロード「<b>C++ によるデスクトップ開発</b>」が必須。
        CMake は VS 同梱のものを自動で使うので PATH 設定は不要</td></tr>
<tr><td class="w-soft">Python</td><td class="w-ver">3.8 以上（任意）</td>
    <td>コース変換ツールとヘッドレス版シミュレータ用。走らせるだけなら不要</td></tr>
<tr><td class="w-soft">Git</td><td class="w-ver">任意</td>
    <td>チームの走行プログラムを clone する場合</td></tr>
</table>
<ul>
<li>コース図 PDF からコースを作り直すときだけ <code>pip install pymupdf</code> が要ります。</li>
<li>通信は <b>localhost の UDP 54001 のみ</b>。外には出ないのでファイアウォールの許可は通常不要です。</li>
<li>ウイルス対策ソフトが自作 exe を検疫することがあります（署名なし・毎回ビルドし直す・
    UDP ソケットを開く、が揃うため）。必要なら <code>controller\\bin</code> と
    <code>controller\\bin_eyes</code> を除外に登録してください（設定変更はご自身の判断で）。</li>
</ul>

<h2>2. 置き場所</h2>
<pre>C:\\Users\\&lt;ユーザ&gt;\\Documents\\MyProjects\\
├─ EtroboUnitySim\\          シミュレータ本体
│   ├─ controller\\          制御プロセス (C/C++)
│   ├─ course\\              コース JSON・走行体パラメータ
│   ├─ protocol\\            C と C# が共有する通信仕様
│   ├─ tools\\               Python ツール
│   └─ unity\\EtroboSim\\     Unity プロジェクト
└─ eyes-et2026\\             チームの走行プログラム (git clone)</pre>
<p>置き場所は自由ですが、パスに空白や日本語を含めないほうが無難です。</p>

<h3>コピーしなくてよいもの（新しい PC で作り直されます）</h3>
<ul>
<li><code>unity\\EtroboSim\\</code> の <code>Library</code> <code>Temp</code> <code>Obj</code>
    <code>Logs</code> <code>UserSettings</code>、<code>*.csproj</code>、<code>*.sln</code></li>
<li><code>controller\\</code> の <code>build</code> <code>build_eyes</code>
    <code>bin</code> <code>bin_eyes</code></li>
</ul>
<p class="note">これらを除くと 25MB 程度です（Library を含めると 66MB）。
Library は Unity が初回起動時に作り直すので、コピーしても速くなりません。</p>

<h2>3. 新しい PC への移し方</h2>
<ol>
<li><b>Unity Hub をインストール</b>し、Unity <b>2022.3 LTS</b> を追加する。</li>
<li><b>Visual Studio 2022 以降をインストール</b>する。ワークロードは
    「C++ によるデスクトップ開発」。</li>
<li><b>EtroboUnitySim フォルダをコピー</b>する（上の「コピーしなくてよいもの」は除く）。</li>
<li><b>チームのコードを取得</b>する。
<pre>git clone https://github.com/m-sugimoto-eyes/eyes-et2026.git</pre></li>
<li><b>制御プロセスをビルド</b>する。
<pre>cd &lt;EtroboUnitySim&gt;\\controller
.\\build.ps1</pre>
<code>controller\\bin\\etrobo_app.exe</code> ができます（同梱のサンプル走行プログラム）。</li>
<li><b>Unity プロジェクトを開く</b>。Unity Hub → Add → Add project from disk で
    <code>&lt;EtroboUnitySim&gt;\\unity\\EtroboSim</code> を追加して開く。
    初回は Library の再生成で数分かかります。</li>
<li><b>移行できたか確かめる</b>。Unity のメニュー
    <b>「ETロボコン → 自己テストを実行」</b>を実行し、最後に
    「すべて通りました」と出れば成功です。</li>
</ol>

<h2>4. 自分たちのプログラムを更新して反映する</h2>
<p>チームのソースには手を入れずにそのまま動きます。</p>
<ol>
<li><b>最新を取る。</b>
<pre>cd &lt;eyes-et2026&gt;
git pull</pre></li>
<li><b>初回だけ CMake の構成をする。</b>（PowerShell）
<pre>cd &lt;EtroboUnitySim&gt;\\controller
$eyes = "&lt;eyes-et2026 のパス&gt;"
cmake -S . -B build_eyes -DETROBO_APP=eyes2026 -DEYES2026_DIR=$eyes -DETROBO_BIN_DIR=bin_eyes</pre>
<p class="note">cmake に PATH が通っていないときは VS 同梱のものを使います（<code>cmake</code> の代わりに <code>&amp; $cmake</code> と書く）。<br/>
<code>$cmake = "C:\\Program Files\\Microsoft Visual Studio\\&lt;版&gt;\\&lt;エディション&gt;\\Common7\\IDE\\
CommonExtensions\\Microsoft\\CMake\\CMake\\bin\\cmake.exe"</code></p></li>
<li><b>2 回目以降はビルドだけ。</b>ソースを直したらここだけ回します。
<pre>cmake --build build_eyes --config Release</pre>
<code>controller\\bin_eyes\\etrobo_app.exe</code> が更新されます。</li>
<li><b>Unity を再生状態にしてから</b>、<b>走行プログラムの置き場で</b>実行する。
<pre>cd &lt;eyes-et2026&gt;
&lt;EtroboUnitySim&gt;\\controller\\bin_eyes\\etrobo_app.exe</pre>
<code>Scenario.csv</code> を相対パスで読むので、置き場で実行するのが肝心です。</li>
<li><b>前の制御プロセスが残っていないか確かめる。</b>残っていると 2 つが同時に指令を送り、
    テレメトリが 0 のまま固まるなど紛らわしい状態になります。</li>
</ol>

<h2>5. Unity で Play する</h2>
<ol>
<li>Unity で <code>Assets/Scenes/SimMain.unity</code> を開く。</li>
<li><b>再生ボタン（▶）を押す。</b>コース・走行体・カメラ・HUD は再生時に生成されます
    （シーンに置いてあるのは <code>SimBootstrap</code> 1 個だけ）。</li>
<li>別のターミナルで<b>制御プロセスを起動する</b>。HUD の「制御プロセス」が
    <b>接続中</b>に変わります。</li>
<li>パネルの <b>中央ボタン</b> か <b>フォース</b>、またはキーボードの
    <b>Space</b> でスタートします。</li>
</ol>
<table>
<tr><th class="w-key">キー</th><th>動作</th></tr>
<tr><td class="w-key">Space</td><td>スタートの合図（中央ボタンとフォースセンサの両方に入る）</td></tr>
<tr><td class="w-key">R</td><td>走行体を<b>開始位置</b>へ戻す（ボトルも設定した位置へ立て直す）</td></tr>
<tr><td class="w-key">P</td><td>配置モード。矢印キーで走行体を動かして開始位置を決める</td></tr>
<tr><td class="w-key">Tab</td><td>視点モードの切り替え</td></tr>
<tr><td class="w-key">F / G</td><td>走行体に寄る / コース全体を映す</td></tr>
</table>
<h3>パネル（HUD）</h3>
<p>走行中の状態と設定はパネルにまとまっています。扱えるのは、
<b>コースの L/R 切り替え</b>、スタートの合図、開始位置、キャリーボトルの色、
ゲートと力士ボトルの配置、難所とフィニッシュの成立判定です。
パネルは走行体が出るのと反対側に出ます。</p>
<ul>
<li>行が多いので、<b>見出し（<code>- センサ</code> など）をクリックすると節をたためます</b>。
    たたんだ状態は次に起動したときも残ります。収まらないぶんはパネルの中をスクロールできます。</li>
<li>カーソルがパネルの上にある間はカメラが動かないので、
    ドラッグやホイールで視点が変わってしまうことはありません。</li>
</ul>

<h3>任意の位置から始める</h3>
<p>難所の手前だけを何度も試したいときは開始位置を動かします。
<code>R</code> で戻る場所が変わるだけで、走らせ方は今までと同じです。</p>
<ul>
<li><b>矢印キーで合わせる</b> — <code>P</code> か パネルの <code>配置(P)</code> で配置モードへ。
    <code>↑</code><code>↓</code> で前後、<code>←</code><code>→</code> で左右、
    カンマ <code>,</code> で左回り、ピリオド <code>.</code> で右回り
    （<code>Shift</code> を押しながらで 10 倍）。
    座標を計算せずに「スタートラインのぎりぎり手前」を出せます。</li>
<li><b>走らせた場所を使う</b> — <code>現在地に</code> で、いま走行体がいる場所が開始位置になります。</li>
<li><b>コースに焼く</b> — <code>保存</code> でコース JSON の <code>start_pose</code> を書き換えます
    （<code>StreamingAssets</code> と <code>course/</code> の両方）。</li>
<li><b>起動時に指定</b> — <code>-etsimStart X,Y[,方位]</code>。方位は 0 が +y、90 が -x です。</li>
</ul>
<p class="note">計測ゲートは動きません。スタートラインより先に置くと計測が始まらないので、
タイムを見たいときはコース既定の位置から走らせてください。<br/>
コースを固定で変えたいときは <code>SimBootstrap</code> の <b>Course File</b> を
書き換えます（既定は <code>etrobo_2026_L.json</code>）。</p>

<h2>6. よくあるつまずき</h2>
<table>
<tr><th class="w-sym">症状</th><th>見るところ</th></tr>
<tr><td class="w-sym">ビルドは通るのに <code>LNK1104</code>／実行した exe が消える</td>
    <td>ウイルス対策ソフトの検疫。<code>controller\\bin</code> を除外に登録する</td></tr>
<tr><td class="w-sym">Unity 側が「待機中」のまま</td>
    <td>制御プロセスが起動していない／ポート違い／前のプロセスが残っている</td></tr>
<tr><td class="w-sym">走行体がその場で回る</td>
    <td>モータの極性かポート割り当ての取り違え。
        既定は A=右 B=左 C=アーム D=フォース E=カラー F=超音波</td></tr>
<tr><td class="w-sym">制御プロセスの日本語が化ける</td>
    <td>パイプやファイルに流したとき。PowerShell なら
        <code>[Console]::OutputEncoding = [Text.Encoding]::UTF8</code>、
        ファイルは <code>Get-Content -Encoding UTF8</code></td></tr>
<tr><td class="w-sym">難所（ボトル・ゲート）が出ない</td>
    <td>難所の入ったコースか確認する（<code>_nansyo</code> が付いたもの）。
        起動ログの <code>props=5/5</code> が<b>組み立てた数／JSON の数</b></td></tr>
<tr><td class="w-sym">新しく足した .cs が反映されない</td>
    <td>Unity を開き直す。初回のアセット取り込みに間に合わないことがある</td></tr>
</table>
<p class="note">詳しくは README.md の「9. うまくいかないとき」を参照してください。</p>

</body>
"""


def make_pdf(out_path: str) -> None:
    for name in (FONT_REGULAR, FONT_BOLD, FONT_MONO):
        if not os.path.exists(os.path.join(FONT_DIR, name)):
            sys.exit("フォントが見つかりません: %s" % os.path.join(FONT_DIR, name))

    archive = pymupdf.Archive()
    archive.add(FONT_DIR, "fonts")

    css = CSS % {"reg": FONT_REGULAR, "bold": FONT_BOLD, "mono": FONT_MONO}
    story = pymupdf.Story(html=build_html(), user_css=css, archive=archive)

    page = pymupdf.paper_rect("a4")
    body = pymupdf.Rect(48, 46, page.width - 48, page.height - 46)

    buf = io.BytesIO()
    writer = pymupdf.DocumentWriter(buf)
    more = 1
    while more:
        device = writer.begin_page(page)
        more, _ = story.place(body)
        story.draw(device)
        writer.end_page()
    writer.close()

    # ページ番号は Story の外。書き終えてから入れる。
    doc = pymupdf.open("pdf", buf.getvalue())
    font = pymupdf.Font(fontfile=os.path.join(FONT_DIR, FONT_REGULAR))
    total = doc.page_count
    for i, pg in enumerate(doc, start=1):
        pg.insert_font(fontname="jp", fontbuffer=font.buffer)
        pg.insert_text((page.width - 48 - 60, page.height - 28),
                       "%d / %d" % (i, total), fontname="jp", fontsize=8,
                       color=(0.4, 0.4, 0.4))

    # 日本語フォントは丸ごとだと 10MB を超える。使った字だけに削る
    # （fontTools が要る。無ければそのまま出す）。
    try:
        doc.subset_fonts(verbose=False)
    except Exception as e:
        print("フォントを削れませんでした (%s)。そのまま出します。" % e, file=sys.stderr)

    os.makedirs(os.path.dirname(os.path.abspath(out_path)), exist_ok=True)
    doc.save(out_path, garbage=4, deflate=True)
    print("%s を書き出しました (%d ページ)" % (out_path, total))


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("-o", "--output", default="docs/セットアップ手順.pdf")
    args = ap.parse_args()
    make_pdf(args.output)
    return 0


if __name__ == "__main__":
    sys.exit(main())
