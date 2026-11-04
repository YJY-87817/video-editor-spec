<#
.SYNOPSIS
  把影片編輯器打包到同一個資料夾（程式 + ffmpeg），可選擇再壓成 zip。

.EXAMPLE
  .\scripts\pack.ps1
  .\scripts\pack.ps1 -FfmpegDir C:\ffmpeg\bin -Zip
#>
param(
    # 輸出資料夾（預設為專案根目錄下的 dist\VideoEditor）
    [string]$Output,
    # 內含 ffmpeg.exe 與 ffprobe.exe 的資料夾；未指定時從 PATH 尋找
    [string]$FfmpegDir,
    [string]$Runtime = 'win-x64',
    # 不內含 .NET 執行階段（對方電腦需安裝 .NET 10 Desktop Runtime）
    [switch]$FrameworkDependent,
    # 打包後再壓成 zip（與輸出資料夾同層）
    [switch]$Zip,
    # 不複製 ffmpeg（使用者自行放到 tools\ffmpeg 或 PATH）
    [switch]$SkipFfmpeg
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $Output) { $Output = Join-Path $root 'dist\VideoEditor' }
$Output = [System.IO.Path]::GetFullPath($Output)

# 安全檢查：避免誤刪專案根目錄或磁碟根目錄
if ($Output -eq $root -or $Output -eq [System.IO.Path]::GetPathRoot($Output)) {
    throw "輸出資料夾不可以是專案根目錄或磁碟根目錄：$Output"
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw '找不到 dotnet，請先安裝 .NET 10 SDK。'
}

# 找 ffmpeg / ffprobe
$ffmpeg = $null; $ffprobe = $null
if (-not $SkipFfmpeg) {
    if ($FfmpegDir) {
        $ffmpeg = Join-Path $FfmpegDir 'ffmpeg.exe'
        $ffprobe = Join-Path $FfmpegDir 'ffprobe.exe'
    } else {
        $ffmpeg = (Get-Command ffmpeg.exe -ErrorAction SilentlyContinue).Source
        $ffprobe = (Get-Command ffprobe.exe -ErrorAction SilentlyContinue).Source
    }
    if (-not $ffmpeg -or -not $ffprobe -or -not (Test-Path $ffmpeg) -or -not (Test-Path $ffprobe)) {
        throw '找不到 ffmpeg.exe 與 ffprobe.exe。請用 -FfmpegDir 指定，或加 -SkipFfmpeg 略過。'
    }
}

if (Test-Path $Output) { Remove-Item $Output -Recurse -Force }
New-Item -ItemType Directory -Path $Output | Out-Null

$selfContained = if ($FrameworkDependent) { 'false' } else { 'true' }
Write-Host "發佈到 $Output（$Runtime，self-contained=$selfContained）..."
dotnet publish (Join-Path $root 'src\VideoEditor.App\VideoEditor.App.csproj') `
    -c Release -r $Runtime --self-contained $selfContained -o $Output
if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失敗（結束碼 $LASTEXITCODE）" }

# 清掉除錯符號
Get-ChildItem $Output -Filter *.pdb -Recurse | Remove-Item -Force

if (-not $SkipFfmpeg) {
    $toolsDir = Join-Path $Output 'tools\ffmpeg'
    New-Item -ItemType Directory -Path $toolsDir -Force | Out-Null
    Copy-Item $ffmpeg, $ffprobe -Destination $toolsDir
    Write-Host "已複製 ffmpeg 到 $toolsDir"
}

if ($Zip) {
    $zipPath = "$Output.zip"
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    Compress-Archive -Path (Join-Path $Output '*') -DestinationPath $zipPath
    Write-Host "已建立 $zipPath"
}

Write-Host "完成：$Output"
