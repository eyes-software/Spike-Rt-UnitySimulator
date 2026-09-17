#Requires -Version 5.1
<#
.SYNOPSIS
    Unity を開いたまま、ゲインを変えて公式コースを何度も走らせる。

.DESCRIPTION
    Unity を 1 回だけ起動して再生状態にし、あとは制御プロセス (etrobo_app.exe) を
    ゲインを変えながら繰り返し起動する。サンプルアプリは接続時に
    etsim_request_sim_reset() を呼ぶので、毎回スタート地点からやり直しになる。

    結果は Unity のログに出る次の行で判定する。
        [Etsim] ゴール 計測タイム xx.xx 秒
        [Etsim] コースアウト 位置 (x, y) mm
        [Etsim] チェックポイント通過 cpN (n/3)

.EXAMPLE
    # 配列を渡すときは PowerShell の中から呼び出し演算子で。
    # powershell -File 経由だと "40,50" が 1 個の文字列として渡って壊れます。
    & .\tools\tune_gains.ps1
    & .\tools\tune_gains.ps1 -BasePowers @(40,50) -Kps @(0.6,0.9)

    # 同じ条件を 5 回ずつ走らせて成功率で見る。FREERUN は走行ごとに結果が
    # 変わるので、速い設定を選ぶときはこちらで確かめること。
    & .\tools\tune_gains.ps1 -BasePowers @(60) -Kps @(0.9) -Kds @(0.08) -Repeat 5
#>
param(
    [int[]]$BasePowers = @(30, 40, 50),
    [double[]]$Kps = @(0.38, 0.6, 0.9),
    [double[]]$Kds = @(0.08, 0.16),
    [double]$Ki = 0.03,
    [int]$Target = 50,
    [string]$Course = "etrobo_2026_L.json",
    [int]$Repeat = 1,
    [double]$RunTimeoutSec = 90,
    [string]$UnityExe = "C:\Program Files\Unity\Hub\Editor\2022.3.41f1\Editor\Unity.exe",
    [string]$Out = "tune_gains_result.txt"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$work = Join-Path $env:TEMP "etrobo_tune"
New-Item -ItemType Directory -Force -Path $work | Out-Null

$ulog = Join-Path $work "unity.log"
$applog = Join-Path $work "app.log"
$exe = Join-Path $root "controller\bin\etrobo_app.exe"

if (-not (Test-Path $exe)) { throw "$exe がありません。先に controller\build.ps1 を実行してください。" }

Get-Process Unity -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 3
Remove-Item $ulog, $Out -ErrorAction SilentlyContinue

Write-Host "Unity を起動しています..."
Start-Process -FilePath $UnityExe -ArgumentList @(
    "-projectPath", (Join-Path $root "unity\EtroboSim"),
    "-executeMethod", "Etrobo.Sim.EditorTools.SimSceneSetup.OpenAndPlay",
    "-etsimAutoStart", "2",
    "-logFile", $ulog) | Out-Null

$deadline = (Get-Date).AddSeconds(300)
$ready = $false
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 2
    if ((Test-Path $ulog) -and
        (Get-Content $ulog -Encoding UTF8 -ErrorAction SilentlyContinue | Select-String "起動時チェック")) {
        $ready = $true; break
    }
}
if (-not $ready) { throw "Unity が再生状態になりませんでした。$ulog を見てください。" }

foreach ($base in $BasePowers) {
    foreach ($kp in $Kps) {
        foreach ($kd in $Kds) {
          # FREERUN は実時間で回るうえセンサにノイズが乗るので、走行は毎回同じにならない。
          # 1 回の成否では決められないので、同じ条件を Repeat 回走らせて成功率で見る。
          $goals = 0; $times = @(); $notStarted = 0
          for ($n = 1; $n -le $Repeat; $n++) {
            $before = (Get-Content $ulog -Encoding UTF8 -ErrorAction SilentlyContinue).Count

            $env:ETROBO_KP = $kp
            $env:ETROBO_KI = $Ki
            $env:ETROBO_KD = $kd
            $env:ETROBO_BASE_POWER = $base
            $env:ETROBO_TARGET = $Target
            # 操作量の上限は基準出力に合わせる。上限が基準出力より小さいと
            # 片輪を止めきれず、急なカーブで曲がりきれない。
            $env:ETROBO_MAX_STEERING = $base

            $app = Start-Process -FilePath $exe -PassThru -WindowStyle Hidden `
                   -RedirectStandardOutput $applog -RedirectStandardError "$applog.err"

            $outcome = "timeout"; $time = ""
            $d = (Get-Date).AddSeconds($RunTimeoutSec)
            while ((Get-Date) -lt $d) {
                Start-Sleep -Milliseconds 1500
                $txt = Get-Content $ulog -Encoding UTF8 -ErrorAction SilentlyContinue
                if ($txt.Count -le $before) { continue }
                $new = $txt[$before..($txt.Count - 1)]
                $g = $new | Select-String "\[Etsim\] ゴール 計測タイム ([\d.]+)"
                if ($g) { $outcome = "goal"; $time = $g.Matches[0].Groups[1].Value; break }
                if ($new | Select-String "\[Etsim\] コースアウト") { $outcome = "lost"; break }
                if ($app.HasExited) { $outcome = "app-exit"; break }
            }

            if (-not $app.HasExited) { try { $app.Kill() } catch {} }

            # Unity のログはバッファされて遅れて届く。走行を止めてから読み直さないと、
            # ゴールやコースアウトを取りこぼして「時間切れ」に見えてしまう。
            Start-Sleep -Seconds 2

            $txt = Get-Content $ulog -Encoding UTF8 -ErrorAction SilentlyContinue
            $new = if ($txt.Count -gt $before) { $txt[$before..($txt.Count - 1)] } else { @() }
            $cp = ($new | Select-String "チェックポイント通過").Count

            if ($outcome -ne "goal") {
                $g = $new | Select-String "\[Etsim\] ゴール 計測タイム ([\d.]+)"
                if ($g) { $outcome = "goal"; $time = $g.Matches[0].Groups[1].Value }
                elseif ($new | Select-String "\[Etsim\] コースアウト") { $outcome = "lost" }
            }

            # 走り出さなかった回は、走りの良し悪しとは関係ないので数えない。
            # 自動スタートのボタン押しを制御プロセスが取りこぼすことがまれにある。
            if (-not ($new | Select-String "自動スタートで走り出しました")) {
                $notStarted++
                Write-Host ("  [{0}/{1}] 走り出さなかったのでやり直します" -f $n, $Repeat)
                if ($notStarted -le 5) { $n-- }
                continue
            }

            if ($outcome -eq "goal") { $goals++; $times += [double]$time }

            if ($Repeat -eq 1) {
                $line = "base={0,3} Kp={1,-5} Kd={2,-5} -> {3,-8} time={4,-6} cp={5}/3" -f $base, $kp, $kd, $outcome, $time, $cp
                Write-Host $line
                Add-Content -Path $Out -Value $line -Encoding UTF8
            } else {
                Write-Host ("  [{0}/{1}] {2} {3}" -f $n, $Repeat, $outcome, $time)
            }
          }

          if ($Repeat -gt 1) {
              $avg = if ($times.Count -gt 0) { ($times | Measure-Object -Average).Average } else { 0 }
              $worst = if ($times.Count -gt 0) { ($times | Measure-Object -Maximum).Maximum } else { 0 }
              $line = "base={0,3} Kp={1,-5} Kd={2,-5} -> 完走 {3}/{4}  平均 {5:F2}s  最遅 {6:F2}s  (不発 {7} 回はやり直し)" -f $base, $kp, $kd, $goals, $Repeat, $avg, $worst, $notStarted
              Write-Host $line
              Add-Content -Path $Out -Value $line -Encoding UTF8
          }
        }
    }
}

Get-Process Unity -ErrorAction SilentlyContinue | Stop-Process -Force
Write-Host "結果: $Out"
