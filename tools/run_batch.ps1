#Requires -Version 5.1
<#
.SYNOPSIS
    力士ボトルの置き場所を変えながら同じ走行を繰り返し、様子を録画する。

.DESCRIPTION
    1 ケースにつき Unity を起動 → 再生 → 制御プロセス (etrobo_app.exe) を接続 →
    録画、を通しで行う。Unity は毎回起動し直す。開始位置とボトルの配置は
    コマンドラインで渡すので、シーンを触らずに条件を振れる。

    出力先には次が残る。
        <出力先>\runs.json          ケースの一覧（make_video.py が読む）
        <出力先>\<tag>\f_*.jpg      録画のコマ
        <出力先>\<tag>_unity.log    Unity のログ（真値・難所の判定）
        <出力先>\<tag>_app.log      走行プログラムの出力

    動画にするのは tools\make_video.py。

    録画の癖（README「走行の様子を動画に残す」も参照）
      ・撮っている間はシミュレータの進みが実時間の 1/4 以下まで落ちる。
        RecordSeconds は動きの長さの 4〜5 倍を見ておくこと。足りないと
        走行そのものは成功しているのに、録画だけが途中で切れる。
      ・Unity が一瞬止まって制御プロセスの見切りに掛かることがあるので、
        ETSIM_LOST_TIMEOUT_MS を延ばしてから起動している。

.EXAMPLE
    # 既定（ET相撲：中心・上下左右のぎりぎり・ランダム 4 か所）
    & .\tools\run_batch.ps1

    # 1 ケースだけ撮り直す
    & .\tools\run_batch.ps1 -Only top

    # 配置を指定する。"tag=X,Y:見出し" の並び
    & .\tools\run_batch.ps1 -Cases @("c=0,0:中心", "a=150,0:右端")
#>
param(
    # 走らせるケース。"tag=X,Y:見出し"（X,Y は土俵の中心からのずれ [mm]）
    [string[]]$Cases = @(
        "mid=0,0:中心",
        "rgt=150,0:右ぎりぎり",
        "lft=-150,0:左ぎりぎり",
        "top=0,150:上ぎりぎり",
        "bot=0,-150:下ぎりぎり",
        "rnd1=-26,47:ランダム1",
        "rnd2=-98,-6:ランダム2",
        "rnd3=126,-44:ランダム3",
        "rnd4=-85,-74:ランダム4"
    ),
    [string]$Only = "",
    [string]$OutDir = "",
    [string]$TeamDir = "$PSScriptRoot\..\..\eyes-et2026",
    [string]$AppExe = "$PSScriptRoot\..\controller\bin_eyes\etrobo_app.exe",
    [string]$UnityExe = "",
    [string]$Course = "etrobo_2026_L_nansyo.json",
    [string]$Start = "970.3,1700.8,0",
    [string]$Frame = "670,1700,0.78,40",
    [double]$RunSeconds = 90,
    [double]$RecordSeconds = 26,
    [int]$RecordFps = 30,
    [int]$RecordWidth = 720,
    [double]$SettleSeconds = 25
)

$root = Resolve-Path "$PSScriptRoot\.."
if ($OutDir -eq "") { $OutDir = Join-Path $root "record" }
if ($UnityExe -eq "") { $UnityExe = Join-Path $env:ProgramFiles "Unity\Hub\Editor\2022.3.41f1\Editor\Unity.exe" }

foreach ($p in @($AppExe, $UnityExe)) {
    if (-not (Test-Path $p)) { throw "見つかりません: $p" }
}
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

# ケースの一覧を解析して、make_video.py が読める形で残す
$parsed = @()
foreach ($c in $Cases) {
    if ($c -notmatch '^([^=]+)=(-?[\d.]+),(-?[\d.]+):(.+)$') { throw "読めないケース指定: $c" }
    $parsed += [pscustomobject]@{
        tag = $Matches[1]; x = [double]$Matches[2]; y = [double]$Matches[3]; label = $Matches[4]
    }
}
$parsed | ConvertTo-Json | Out-File (Join-Path $OutDir "runs.json") -Encoding utf8

foreach ($e in $parsed) {
    if ($Only -ne "" -and $e.tag -ne $Only) { continue }

    $ulog = Join-Path $OutDir "$($e.tag)_unity.log"
    $applog = Join-Path $OutDir "$($e.tag)_app.log"
    $frames = Join-Path $OutDir $e.tag

    Get-Process Unity -ErrorAction SilentlyContinue | Stop-Process -Force
    Get-Process etrobo_app -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 4
    Remove-Item $ulog, $applog, "$applog.err" -ErrorAction SilentlyContinue
    Remove-Item $frames -Recurse -Force -ErrorAction SilentlyContinue

    Start-Process -FilePath $UnityExe -ArgumentList @(
        "-projectPath", "$root\unity\EtroboSim",
        "-executeMethod", "Etrobo.Sim.EditorTools.SimSceneSetup.OpenAndPlay",
        "-etsimCourse", $Course,
        "-etsimStart", $Start,
        "-etsimRikishi", "$($e.x),$($e.y)",
        "-etsimAutoStart", "3", "-etsimPressForce", "3,10", "-etsimTruthLog", "0.5",
        "-etsimScreenshotFrame", $Frame,
        "-etsimRecord", "$frames,$RecordFps,$RecordSeconds,connect,$RecordWidth",
        "-logFile", $ulog) | Out-Null

    # 再生に入るまで待つ。Unity の起動時間は毎回違う
    $deadline = (Get-Date).AddSeconds(420)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 3
        if ((Test-Path $ulog) -and
            (Get-Content $ulog -Encoding UTF8 -EA SilentlyContinue | Select-String "起動時チェック")) { break }
    }

    $env:ETSIM_BT_CONNECTED = "1"       # 接続していないとログを出さないアプリ向け
    $env:ETSIM_LOST_TIMEOUT_MS = "15000" # 録画で Unity が止まっても見切らせない
    $app = Start-Process -FilePath $AppExe -PassThru -WindowStyle Hidden `
           -WorkingDirectory $TeamDir -RedirectStandardOutput $applog -RedirectStandardError "$applog.err"
    Start-Sleep -Seconds $RunSeconds
    if (-not $app.HasExited) { try { $app.Kill() } catch {} }
    Start-Sleep -Seconds $SettleSeconds   # ボトルが止まるのを待つ
    Get-Process Unity -ErrorAction SilentlyContinue | Stop-Process -Force

    $n = (Get-ChildItem "$frames\f_*.jpg" -EA SilentlyContinue | Measure-Object).Count
    "$($e.tag) : $n コマ"
}
"BATCH DONE  -> $OutDir"
