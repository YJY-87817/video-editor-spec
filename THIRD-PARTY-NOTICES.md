# 第三方授權聲明

本專案（MIT 授權，見 `LICENSE`）使用下列第三方元件。授權全文以各專案官方發佈為準。

## LibVLCSharp

- 版本：3.10.2（NuGet）
- 授權：GNU LGPL v2.1 或更新版本
- 專案：https://github.com/videolan/libvlcsharp

## libvlc / libvlccore（VideoLAN.LibVLC.Windows）

- 版本：3.0.24（NuGet）
- 授權：GNU LGPL v2.1 或更新版本
- 版權：VideoLAN 與 VLC 作者
- 原始碼：https://www.videolan.org/vlc/ （對應 3.0.x 版本）
- 隨 `publish\libvlc\win-x64` 以獨立動態連結檔案發佈，使用者可自行替換成相容版本。
- 部分 plugin 模組可能採其他授權（含 GPL）。發佈前請清查 `plugins` 資料夾。

## FFmpeg（不隨程式發佈）

- 本程式以獨立行程呼叫使用者自備的 `ffmpeg.exe` 與 `ffprobe.exe`，不連結其函式庫，也不打包。
- FFmpeg 為 LGPL v2.1+；含 libx264、libx265 的組建為 GPL v2+。
- 專案：https://ffmpeg.org/
- 若日後將 FFmpeg 一起散佈，須附上該組建的授權全文與對應原始碼取得方式。
