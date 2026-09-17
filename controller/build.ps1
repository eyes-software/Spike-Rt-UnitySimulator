<#
.SYNOPSIS
  制御プロセスを Visual Studio のツールチェーンでビルドする。

.DESCRIPTION
  vswhere で Visual Studio を探し、VS 同梱の CMake で build/ に生成する。
  cmake / cl に PATH を通していなくても動くようにしてある。

.EXAMPLE
  .\build.ps1
  .\build.ps1 -Config Debug
  .\build.ps1 -Clean
#>
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Config = 'Release',
    [switch]$Clean
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$buildDir = Join-Path $root 'build'

if ($Clean -and (Test-Path $buildDir)) {
    Write-Host "build/ を削除します" -ForegroundColor Yellow
    Remove-Item $buildDir -Recurse -Force
}

# --- Visual Studio と CMake を探す -------------------------------------
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path $vswhere)) {
    throw "vswhere が見つかりません。Visual Studio (C++ デスクトップ開発) を入れてください。"
}

$vsPath = & $vswhere -latest -products * `
    -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 `
    -property installationPath
if (-not $vsPath) {
    throw "C++ ビルドツールを持つ Visual Studio が見つかりません。"
}

$cmake = Get-Command cmake -ErrorAction SilentlyContinue
if ($cmake) {
    $cmakeExe = $cmake.Source
} else {
    $cmakeExe = Join-Path $vsPath 'Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe'
}
if (-not (Test-Path $cmakeExe)) {
    throw "cmake.exe が見つかりません: $cmakeExe"
}

Write-Host "Visual Studio : $vsPath"
Write-Host "CMake         : $cmakeExe"
Write-Host "構成          : $Config"
Write-Host ""

& $cmakeExe -S $root -B $buildDir -A x64 2>&1 | Write-Host
if ($LASTEXITCODE -ne 0) {
    # -A を受け付けないジェネレータ (Ninja / Makefile) 向けのフォールバック
    Write-Host "アーキテクチャ指定なしで再試行します" -ForegroundColor Yellow
    & $cmakeExe -S $root -B $buildDir 2>&1 | Write-Host
    if ($LASTEXITCODE -ne 0) { throw "CMake の構成に失敗しました" }
}

& $cmakeExe --build $buildDir --config $Config 2>&1 | Write-Host
if ($LASTEXITCODE -ne 0) {
    Write-Host ""
    Write-Host "LNK1104 (ファイルを開くことができません) が出た場合:" -ForegroundColor Yellow
    Write-Host "  セキュリティソフトが出力先をブロックしている可能性があります。"
    Write-Host "  この制御プロセスは署名なしで UDP ソケットを開くため、"
    Write-Host "  ヒューリスティック検知の対象になりやすいです。"
    Write-Host "  次のフォルダを除外に登録してください:"
    Write-Host "    $rootin" -ForegroundColor Cyan
    throw "ビルドに失敗しました"
}

$exe = Join-Path $root "bin\etrobo_app.exe"

if (-not (Test-Path $exe)) {
    throw "リンクは通ったのに $exe がありません。セキュリティソフトが削除した可能性があります。"
}

Write-Host ""
Write-Host "ビルド完了: $exe" -ForegroundColor Green
Write-Host "Unity を再生状態にしてから、この exe を実行してください。"
