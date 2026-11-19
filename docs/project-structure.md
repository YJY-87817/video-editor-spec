# 專案結構導覽

給第一次打開這個倉庫的開發者。功能與操作見 `README.md`，需求與非目標見 `video-editor-spec.md`，已知問題與改善進度見 `docs/wpf-health-check-report.md`。

## 三個專案

```
VideoEditor.slnx
├─ src/VideoEditor.App     WPF 視窗（net10.0-windows）：畫面、LibVLC 預覽、使用者操作
├─ src/VideoEditor.Core    不含 UI 的規則與 FFmpeg 呼叫（net10.0，沒有第三方套件）
└─ tests/VideoEditor.Tests xUnit：只測 Core，不參照 App
```

設計原則：**規則放 Core、畫面放 App**。App 沒有自動化測試，所以任何不需要 WPF 或 LibVLC 就能判斷的規則（排隊、貼齊、時鐘、挑格、命令對應…）都抽到 Core 並補測試。不使用 MVVM 框架。

## Core 資料夾與依賴方向

依賴只能由右往左：`Models ← Editing ← Ffmpeg ← Export`；`Playback` 不依賴其他資料夾；`Logging` 在最底層，誰都可以用。`ArchitectureTests` 會檢查這個方向，新增資料夾時要在那裡決定它的位置。

| 資料夾 | 內容 |
|---|---|
| `Models/` | 資料與數學：`MediaInfo`、`OutputSettings`、`FrameMath`（影格與時間換算）、`ClipFraming`（填滿裁切的位置）、`FrameTransform`（旋轉鏡像）、`PreviewGeometry`（輸出尺寸、預覽版面、音量曲線）、`WaveformData`、`ReentryGuard`（避免程式改控制項時觸發事件循環） |
| `Editing/` | 時間軸：`Timeline`（模型與規則：同軌不重疊、可留空白）、`TimelineEditor`（編輯操作，回傳結果物件而不丟例外）、`TimelineSnap`／`TimelineDrag`（拖曳與貼齊）、`InOutPoints`、`EditPoints`、`SilenceDetector`、`WaveformColumns` |
| `Ffmpeg/` | 呼叫 FFmpeg／ffprobe：`FfmpegLocator`（找可執行檔）、`ProcessRunner`（啟動、逾時、取消、終止行程樹）、`MediaProbe`、`FrameExtractor`、`WaveformExtractor`／`WaveformCache`、`ImportController`、`ExpectedErrors`（預期例外的共用分類）、`TemporaryFiles` |
| `Export/` | 匯出：`ExportPlanner`（把時間軸變成 FFmpeg 參數）、`ExportService`（執行、暫存檔、改名）、`ExportController`（流程與錯誤處理）、`ReverseGraph`／`ReversePreviewCache`（倒放）、`FilterGraphFile`（命令列太長時改放檔案） |
| `Playback/` | 預覽的純規則：`PlaybackClock`（時鐘與漂移）、`RewindPlan`（倒帶挑格）、`FrameBatch`、`FrameRequestQueue`（逐格請求的排隊與插隊）、`MissingFrames`（解不出的影格，過期可重試）、`PendingSeek`、`PreviewCommand`／`PreviewKeys`（快捷鍵對應）、`PlaybackText` |
| `Logging/` | `FileLog`／`Log`：寫到 `%LOCALAPPDATA%\VideoEditor\logs` |

Core 以介面反轉對畫面的依賴：`IImportView`、`IExportView` 由 `MainWindow` 實作，Controller 不認識任何控制項（MVP 風格）。測試用 Fake 實作它們。

## App

| 檔案 | 內容 |
|---|---|
| `App.xaml(.cs)` | 啟動：初始化紀錄與 LibVLC、註冊未處理例外的處理 |
| `MainWindow.xaml` | 版面（具名元素約 54 個，事件處理約 44 個，所有控制項都有 `AutomationId`） |
| `MainWindow.*.cs`（partial） | `xaml.cs` 組裝物件（等同 Composition Root）；`Import`、`Timeline`、`Playback`、`Preview`、`Marks`、`Framing`、`Export` 各管一塊畫面邏輯。partial 只是切檔，狀態仍集中在 `MainWindow` |
| `Controls/TimelineView.cs` | 自繪的時間軸（片段、波形、播放頭、拖曳） |
| `Controls/ProgressOverlay.xaml` | 匯入、倒放預覽、匯出共用的進度面板 |
| `Preview/PreviewEngine.cs` | 預覽播放：兩個 LibVLC 播放器（影片與配樂）、倒帶、逐格擷取與快取 |
| `Preview/VlcVideoSink.cs` | 接收 LibVLC 的影格回呼，畫進 WPF 的位圖 |
| `Palette.cs` | XAML 與程式碼共用的顏色（XAML 以 `{x:Static local:Palette.Accent}` 取用） |
| `MediaItem.cs`、`ClipRow.cs` | 素材清單與片段清單的列資料 |

執行緒假設：`PreviewEngine` 除了 LibVLC 回呼與背景指令佇列外，都只在 UI 執行緒使用。

## 建置與測試

```bash
dotnet build VideoEditor.slnx
dotnet test                                      # 全部，需要 ffmpeg.exe／ffprobe.exe（含 libx264、libx265）
dotnet test --filter "Category!=Integration"     # 只跑不需要 FFmpeg 的測試
```

整合測試用 `[Collection("media")]` 共用一組由 FFmpeg 當場產生的短片（`TestMedia`），標了 `Category=Integration`。在 Linux 上可以編譯整個 solution（`-p:EnableWindowsTargeting=true`），但 WPF 畫面只能在 Windows 上執行與驗證。CI（`.github/workflows/ci.yml`）在 Windows 上建置、測試並驗證發佈資料夾。

## 規劃文件

`docs/` 裡的 `*-PLAN.md` 是各功能當時的規劃與實作結果，記錄決策的原因，內容以當時為準，不一定反映現在的程式碼：

- `FIX-PLAN.md`：早期的修正清單與各階段結果
- `EDITING-TOOLS-PLAN.md`：分割、跳到剪輯點、貼齊等編輯工具
- `SILENCE-SNAP-PLAN.md`：靜音吸附
- `WAVEFORM-PLAN.md`：波形顯示
