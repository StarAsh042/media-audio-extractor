# Build script: compile a single-file GUI exe with the .NET Framework csc.exe.
# yt-dlp.exe and ffmpeg.exe are embedded as Win32 resources.
# Usage: powershell -ExecutionPolicy Bypass -File build.ps1
#
# NOTE: keep this file ASCII-only. Windows PowerShell 5.1 reads BOM-less .ps1
# files as ANSI and would corrupt non-ASCII literals; csc.exe also mangles
# non-ASCII command line arguments. The exe is therefore built with an ASCII
# name and can be renamed afterwards (e.g. to a Chinese display name).
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Definition
$src  = Join-Path $root 'src\MediaAudioExtractor.cs'
$asm  = Join-Path $root 'assets\app.manifest'
$ico  = Join-Path $root 'assets\icon.ico'
$yt   = Join-Path $root 'tools\yt-dlp.exe'
$ff   = Join-Path $root 'tools\ffmpeg.exe'
$out  = Join-Path $root 'dist\MediaAudioExtractor.exe'

foreach ($f in @($src, $asm, $ico)) {
    if (-not (Test-Path $f)) { throw "missing file: $f" }
}

# 缺少内置组件时自动获取（二进制不纳入版本库）
if (-not (Test-Path $yt) -or -not (Test-Path $ff)) {
    Write-Host 'tools\yt-dlp.exe / tools\ffmpeg.exe missing, fetching...' -ForegroundColor Yellow
    & (Join-Path $root 'tools\fetch-tools.ps1')
}
foreach ($f in @($yt, $ff)) {
    if (-not (Test-Path $f)) { throw "missing tool: $f (run tools\fetch-tools.ps1)" }
}
New-Item -ItemType Directory -Force -Path (Split-Path $out) | Out-Null

$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = 'C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe' }

$cscArgs = @(
    '/nologo',
    '/target:winexe',
    '/platform:anycpu',
    '/optimize+',
    '/codepage:65001',
    "/win32manifest:$asm",
    "/win32icon:$ico",
    "/resource:$yt,yt-dlp.exe",
    "/resource:$ff,ffmpeg.exe",
    '/reference:System.dll',
    '/reference:System.Drawing.dll',
    '/reference:System.Windows.Forms.dll',
    "/out:$out",
    $src
)

Write-Host 'Compiling (embedding ~84 MB ffmpeg, this takes a few seconds)...' -ForegroundColor Cyan
$sw = [Diagnostics.Stopwatch]::StartNew()
& $csc $cscArgs
if ($LASTEXITCODE -ne 0) { throw "csc failed with exit code $LASTEXITCODE" }
$sw.Stop()

$fi = Get-Item $out
$ver = $fi.VersionInfo.FileVersion
Write-Host ("OK: {0}  v{1}  ({2:N1} MB, {3:N1}s)" -f $fi.FullName, $ver, ($fi.Length / 1MB), $sw.Elapsed.TotalSeconds) -ForegroundColor Green
Write-Host 'Rename dist\MediaAudioExtractor.exe if you want a localized file name.' -ForegroundColor DarkGray
