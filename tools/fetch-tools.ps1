# 获取内置组件：yt-dlp.exe 与 ffmpeg.exe
# 用法: powershell -ExecutionPolicy Bypass -File tools\fetch-tools.ps1 [-Force] [-NoRun]
#
# 说明：这两个二进制不纳入版本库（合计约 100 MB），构建前用本脚本获取。
#
# 安全约定：
#   · 下载地址**固定到具体版本**（不再用 latest），保证可复现；
#   · 下载后与解压后都**校验 SHA-256**，不匹配立即中止。
#     上游若发布了新版本，这里会失败并提示更新 —— 这是刻意的「fail closed」，
#     避免悄悄换成一个未经审阅的二进制。
#   · -NoRun 只下载与校验、不执行这些二进制（CI 上处理不可信 PR 时使用）。
param(
    [switch]$Force,
    [switch]$NoRun
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$tools = Split-Path -Parent $MyInvocation.MyCommand.Definition
$ytdlp = Join-Path $tools 'yt-dlp.exe'
$ffmpeg = Join-Path $tools 'ffmpeg.exe'

# ---- 固定版本与校验值（升级时同步改这四处，并重新跑一次本脚本核对） ----
$YtDlpVersion = '2026.08.19'
$YtDlpSha256 = '66674953FE251B89F4D08C5F0E35E0728679BD67AB3D7D05C0562AF101DD3E7A'
$FfmpegTag = '7.1'
$FfmpegSha256 = '2CE797A0F88D7F067180338FB227F7B1928EA727BD9A4D7A1D022F7C52AF71A3'

$YtDlpUrl = "https://github.com/yt-dlp/yt-dlp/releases/download/$YtDlpVersion/yt-dlp.exe"
$FfmpegUrl = "https://github.com/GyanD/codexffmpeg/releases/download/$FfmpegTag/ffmpeg-$FfmpegTag-essentials_build.zip"

function Get-Sha256([string]$path) {
    return (Get-FileHash -Path $path -Algorithm SHA256).Hash.ToUpperInvariant()
}

function Assert-Hash([string]$path, [string]$expect, [string]$what) {
    $actual = Get-Sha256 $path
    if ($actual -ne $expect.ToUpperInvariant()) {
        # 注意：-f 的优先级高于 +，必须给整个拼接加括号，否则占位符不会被替换
        $msg = ("{0} 校验失败`n  期望: {1}`n  实际: {2}`n" +
                "请确认下载源是否被篡改；若是上游发布了新版本，请更新本脚本里固定的版本号与校验值。") `
               -f $what, $expect, $actual
        throw $msg
    }
    Write-Host ("[校验] {0} SHA-256 匹配" -f $what) -ForegroundColor Green
}

function Test-Ready([string]$path, [int]$minMB) {
    if (-not (Test-Path $path)) { return $false }
    return ((Get-Item $path).Length / 1MB) -ge $minMB
}

Write-Host '== 获取内置组件 ==' -ForegroundColor Cyan

# ---------- yt-dlp ----------
if ((Test-Ready $ytdlp 5) -and -not $Force) {
    Assert-Hash $ytdlp $YtDlpSha256 'yt-dlp.exe（已存在）'
    Write-Host ("[跳过] yt-dlp.exe 已存在且校验通过 ({0:N1} MB)" -f ((Get-Item $ytdlp).Length / 1MB)) -ForegroundColor DarkGray
} else {
    Write-Host ("[下载] yt-dlp.exe {0} ..." -f $YtDlpVersion) -ForegroundColor Yellow
    Invoke-WebRequest -Uri $YtDlpUrl -OutFile "$ytdlp.download" -UseBasicParsing -TimeoutSec 600
    Assert-Hash "$ytdlp.download" $YtDlpSha256 'yt-dlp.exe'
    Move-Item "$ytdlp.download" $ytdlp -Force
    Write-Host ("[完成] yt-dlp.exe ({0:N1} MB)" -f ((Get-Item $ytdlp).Length / 1MB)) -ForegroundColor Green
}

# ---------- ffmpeg（从官方构建压缩包中取出 ffmpeg.exe） ----------
if ((Test-Ready $ffmpeg 20) -and -not $Force) {
    Assert-Hash $ffmpeg $FfmpegSha256 'ffmpeg.exe（已存在）'
    Write-Host ("[跳过] ffmpeg.exe 已存在且校验通过 ({0:N1} MB)" -f ((Get-Item $ffmpeg).Length / 1MB)) -ForegroundColor DarkGray
} else {
    $tmp = Join-Path $tools 'tmp'
    if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $tmp | Out-Null
    $zip = Join-Path $tmp 'ffmpeg.zip'
    try {
        Write-Host ("[下载] ffmpeg {0} essentials（约 80-100 MB，请稍候）..." -f $FfmpegTag) -ForegroundColor Yellow
        Invoke-WebRequest -Uri $FfmpegUrl -OutFile $zip -UseBasicParsing -TimeoutSec 1800

        Write-Host '[解压] 提取 ffmpeg.exe ...' -ForegroundColor Yellow
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $archive = [System.IO.Compression.ZipFile]::OpenRead($zip)
        try {
            $entry = $archive.Entries | Where-Object { $_.FullName -match 'bin/ffmpeg\.exe$' } | Select-Object -First 1
            if ($null -eq $entry) { throw '压缩包中未找到 bin/ffmpeg.exe' }
            [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $ffmpeg, $true)
        } finally {
            $archive.Dispose()
        }
        # 校验解压出来的可执行文件本身 —— 它才是会被内置、被执行的那份产物
        Assert-Hash $ffmpeg $FfmpegSha256 'ffmpeg.exe'
        Write-Host ("[完成] ffmpeg.exe ({0:N1} MB)" -f ((Get-Item $ffmpeg).Length / 1MB)) -ForegroundColor Green
    } finally {
        if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }
    }
}

# ---------- 自检 ----------
if ($NoRun) {
    Write-Host '[跳过] -NoRun：已按要求不执行这些二进制' -ForegroundColor DarkGray
} else {
    & $ytdlp --version | ForEach-Object { Write-Host "yt-dlp 版本: $_" }
    & $ffmpeg -hide_banner -version | Select-Object -First 1 | ForEach-Object { Write-Host "ffmpeg: $_" }
}
Write-Host '组件就绪，可以运行 build.ps1 了。' -ForegroundColor Cyan
