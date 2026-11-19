# WPF 專案完整程式碼健檢報告

- **專案**：個人影片編輯器（`video-editor-spec` 倉庫，3 個專案）
- **報告日期**：2026-11-18
- **性質**：原始分析是唯讀的靜態分析：沒有修改、新增、刪除或移動任何專案檔案，也沒有建置或執行測試（分析環境是 Linux，無法建置 WPF）。之後依本報告做的修改見「處理進度」。
- **處理進度**：2026-11-19 起，已處理的項目已從本報告移除（階段 1 到階段 4 的主要項目，見 PR #39、#43），已移除的 ID：A-2、Q-2、Q-3、Q-4、Q-5、Q-6、W-1、P-1、P-2、P-5、S-3、S-4、S-6、S-7、T-1。只處理一部分的項目（Q-1、T-2、S-2、S-8、P-4）只保留尚待處理的部分。完整歷史見 git。
- **嚴重程度**：Critical（嚴重錯誤或安全風險）／High（可能造成錯誤、資料遺失或重大維護問題）／Medium（架構或品質問題）／Low（改善建議）
- **分類**：已確認問題／潛在風險／待確認事項

---

## 目錄

1. [專案基本資訊](#1-專案基本資訊)
2. [專案架構總覽](#2-專案架構總覽)
3. [WPF／MVVM 架構評估](#3-wpfmvvm-架構評估)
4. [程式碼品質報告](#4-程式碼品質報告)
5. [穩定性與安全性報告](#5-穩定性與安全性報告)
6. [測試與維護性報告](#6-測試與維護性報告)
7. [問題清單與改善優先順序](#7-問題清單與改善優先順序)
8. [問題詳細說明](#8-問題詳細說明)
9. [建議後續改善路線](#9-建議後續改善路線)
10. [待確認事項彙整](#10-待確認事項彙整)
11. [分析範圍與限制](#11-分析範圍與限制)

---

## 結論摘要

| 項目 | 結果 |
|---|---|
| Critical／High | **0 項** |
| Medium | 4 項：Q-1、T-2、A-1、W-2（Q-1、T-2 已部分處理；主要是 `PreviewEngine`／`MainWindow` 規模、App 層測試、播放頭重繪成本） |
| 整體評價 | 程式品質偏高：例外處理有紀律、資源釋放周全、方法長度控制良好、Core 與 UI 分離確實、測試量大且多為真實 FFmpeg 的整合測試 |
| 最值得優先處理 | ① 在 Windows 上實機驗證並量測播放頭重繪成本（W-2） ② 繼續把 `PreviewEngine`／`MainWindow` 的規則抽進 Core 並補測試（Q-1、T-2、A-1） |
| 不建議做的事 | 為了形式全面導入 MVVM 框架；對規模約 6,200 行的單視窗工具，收益有限 |

---

## 1. 專案基本資訊

| 項目 | 內容 |
|---|---|
| 類型 | 個人影片編輯器（Windows 桌面，WPF） |
| 框架 | .NET 10（App：`net10.0-windows`；Core 與 Tests：`net10.0`），不是 .NET Framework |
| Solution | `VideoEditor.slnx`（新式 XML 格式），3 個專案 |
| 專案 | `VideoEditor.App`（WPF、`WinExe`）、`VideoEditor.Core`（類別庫）、`VideoEditor.Tests`（xUnit） |
| 規模 | C# 約 6,180 行（src）、XAML 約 280 行、測試約 4,950 行（行數含註解與空行） |
| 公開型別 | 約 70 個，其中介面只有 2 個（`IImportView`、`IExportView`） |
| Nullable／ImplicitUsings | 三個專案皆啟用 |
| 啟動入口 | `App.xaml` 的 `StartupUri="MainWindow.xaml"`；`App.xaml.cs` 的 `OnStartup` 初始化 LibVLC 並註冊 `DispatcherUnhandledException` |
| 資源與設定 | 只有 `app.ico`；沒有 `appsettings`、`app.config`、`ResourceDictionary`；`App.xaml` 的 `Application.Resources` 是空的 |
| 持久化 | 無資料庫、無設定檔、無專案存檔（規格書明列「專案存檔與復原／重做」為非目標） |
| 外部相依 | 使用者自備的 `ffmpeg.exe` 與 `ffprobe.exe`（需含 libx264／libx265） |
| CI | GitHub Actions、`windows-latest`：建置、測試、發佈驗證（授權檔、無 GPL 的 libvlc 模組、僅含 win-x64）；FFmpeg 固定為 9.0.2 |

### NuGet 套件

| 專案 | 套件 | 版本 |
|---|---|---|
| App | `LibVLCSharp` | 3.10.2 |
| App | `VideoLAN.LibVLC.Windows` | 3.0.24 |
| Tests | `xunit` | 2.9.3 |
| Tests | `xunit.runner.visualstudio` | 3.1.4 |
| Tests | `Microsoft.NET.Test.Sdk` | 17.14.1 |
| Tests | `coverlet.collector` | 6.0.4 |

Core 沒有任何第三方套件。專案沒有 MVVM 框架、DI 容器、Logging 套件，也沒有 `packages.lock.json`、`Directory.Build.props`、`global.json`、`.editorconfig`。

---

## 2. 專案架構總覽

```
┌─ VideoEditor.App（WPF，net10.0-windows）──────────────────────┐
│  MainWindow : Window, IExportView, IImportView                │
│    7 個 partial（共 87 個方法）＋ MainWindow.xaml             │
│    （44 個事件處理、54 個具名元素）                           │
│  Controls/  TimelineView（自繪，544 行）、ProgressOverlay     │
│  Preview/   PreviewEngine（765 行，LibVLC＋FFmpeg）、VlcVideoSink │
└──────────────┬───────────────────────────────────────────────┘
               │ 參照
┌─ VideoEditor.Core（net10.0，無 UI、無第三方套件）─────────────┐
│  Models/    MediaInfo、OutputSettings、ClipFraming、FrameMath、…│
│  Timeline/  Timeline、TimelineEditor、TimelineSnap、InOutPoints│
│  Ffmpeg/    ProcessRunner、MediaProbe、FrameExtractor、各種 Cache│
│  Export/    ExportController、ExportPlanner、ExportService、   │
│             ReverseGraph、ReversePreviewCache                 │
│  Playback/  PreviewCommand、FrameBatch、PlaybackClock、RewindPlan│
│  Logging/   FileLog、Log                                       │
└──────────────────────────────────────────────────────────────┘
               ▲ 參照
        VideoEditor.Tests（只測 Core；未參照 App）
        Core 資料夾的依賴方向：Models ← Timeline ← Ffmpeg ← Export（Playback 獨立、Logging 在最底層），由 ArchitectureTests 鎖住
```

### 結構特徵

- **View 與業務邏輯**：Core 以 `IImportView`／`IExportView` 反轉對畫面的依賴，由 `MainWindow` 實作。這是 MVP 風格，Controller 不認識任何控制項。
- **Service 與業務邏輯**：`TimelineEditor` 承載編輯規則，回傳 `SplitResult`／`AddResult` 這類結果物件，不丟例外給 UI。
- **依賴注入**：沒有 DI 容器。`MainWindow` 建構子手動組裝所有物件（等同 Composition Root）。Core 內部以建構子注入。
- **執行緒假設**：`PreviewEngine` 的類別註解寫明「除了 LibVLC 回呼之外，所有成員都只在 UI 執行緒使用」。
- **資料存取**：沒有資料庫。外部資源只有 FFmpeg 子行程與 `%TEMP%\VideoEditor` 下的倒放暫存檔。

### 結構弱點

`MainWindow`（持有 13 個以上的狀態欄位）與 `PreviewEngine`（約 35 個欄位）是兩個過大的類別（A-1、Q-1）。

---

## 3. WPF／MVVM 架構評估

| 面向 | 評估 |
|---|---|
| 是否採用 MVVM | 否。採用 Code-behind 加 Core 分離，部分是 MVP 風格 |
| 是否因此判定為錯誤 | **否**。單視窗、約 5,700 行、互動以自繪與播放為主，MVVM 的收益有限 |
| Data Binding | 只有 3 處（`MainWindow.xaml:71-74`，素材清單項目），預設 OneWay，沒有誤用 `TwoWay`，沒有 Converter |
| `INotifyPropertyChanged`／`ICommand`／`DataContext` | 全專案沒有使用 |
| 命令模式 | 有「輕量版」：按鈕與快捷鍵共用 `Execute(PreviewCommand)`，鍵位對應表放在 Core 的 `PreviewKeys` 並被測試 |
| 做得好 | Core 與 UI 的分離；View 介面反轉；滑鼠擷取（`CaptureMouse`／`OnLostMouseCapture`）；凍結的靜態筆刷；`Progress<T>` 與 `Dispatcher.BeginInvoke` 的跨執行緒回報；`IsInitialized` 防護；所有控制項都有 `AutomationId` |
| 風險 | `MainWindow` 持有過多狀態；4 個 `_updatingXxx` 布林旗標手動避免事件循環（不具例外安全）；UI 邏輯難以單元測試；狀態同步依賴手動呼叫 `UpdateXxx()` |
| 建議 | **不要全面導入 MVVM 框架。** 持續把規則抽進 Core 並補測試；必要時只為最複雜的區塊（預覽與時間軸互動）引入輕量的 Presenter／ViewModel |

### 其他 WPF 觀察

- **Event Handler 與記憶體洩漏**：所有事件訂閱的雙方壽命都與視窗相同；`DispatcherTimer` 在 `Dispose` 有 `Stop`；沒有靜態事件被訂閱。**未發現洩漏風險。**
- **視窗生命週期**：`OnClosing` 在匯出中會先詢問並等待取消與清理完成，設計周全；`OnClosed` 釋放引擎與波形快取。
- **ObservableCollection**：`_media` 只在 UI 執行緒更新。片段清單則每次以新 `List<ClipRow>` 重新指派 `ItemsSource`，片段數量少時沒有問題。
- **ResourceDictionary／Style**：樣式只寫在 `Window.Resources`；顏色在 XAML 與 C# 重複硬編碼（見 W-3）。

---

## 4. 程式碼品質報告

| 面向 | 評價 | 重點 |
|---|---|---|
| 命名與風格 | 良好 | 命名一致（`_camelCase` 欄位、PascalCase）；註解以「為什麼」為主，XML 說明註解約 549 行；唯一問題是命名空間與類別同名 `Timeline`（Q-7） |
| 類別與方法職責 | 方法良好，少數類別過大 | 最長的方法依序為 `ExportController.RunAsync` 98 行、`MediaProbe.Parse` 68 行、`MainWindow` 建構子 57 行、`PreviewEngine.Tick` 與 `ExtractPendingFramesAsync` 各 54 行；`PreviewEngine` 與 `MainWindow` 過大（Q-1、A-1） |
| 重複程式碼 | 輕微 | 預期例外清單在 7 處各寫一遍（Q-8）；顏色常數在 XAML 與 C# 重複（W-3） |
| 複雜條件 | 良好 | 未見過度巢狀；`ClipRow.Label` 單行有 5 個條件運算（A-5） |
| Nullable | 良好 | 全專案啟用；`!` 抽樣僅 3 處；FFmpeg 缺失時 `_engine` 等欄位為 `null`，使用點都有防護（A-3 已下調） |
| 例外處理 | 優秀 | 以 `catch (...) when (e is …)` 的窄過濾為主；寬鬆 `catch` 只有 3 處且都有理由；3 處空 `catch` 都有註解；背景指令佇列與 `async void` 的最外層都會攔下例外並記錄 |
| IDisposable／資源釋放 | 良好 | 行程取消會終止整個行程樹；先寫暫存檔再改名；鎖定檔避免多程式互相清除；少數 `CancellationTokenSource`／`SemaphoreSlim` 未釋放（Q-11） |
| Async／Await | 良好 | 沒有 `.GetAwaiter().GetResult()`；`.Result` 僅 2 處且都先檢查完成；`.Wait` 只有 `Dispose` 一處（有逾時，逾時時略過原生資源釋放）；`async void` 只有 3 處且都是事件處理器並有 `try/catch`；`ProcessRunner` 與匯出收尾已用 `ConfigureAwait(false)`，其餘 Core 類別沒有（P-4） |
| 靜態與全域狀態 | 良好 | 沒有可變的全域單例；只有 `ProcessRunner.Started` 靜態事件（供測試使用） |

---

## 5. 穩定性與安全性報告

### 5.1 穩定性

**設計優點（已確認）**

- 記憶體上限有被控制：倒放依 512 MiB 緩衝預算分段處理；`ProcessRunner` 的錯誤輸出只保留最後 40 行；聲音波形用串流處理；`WaveformData` 預建多層最大值表，查詢為 O(log n)。
- FFmpeg 並行度受控：波形與倒放預覽各以 `SemaphoreSlim(1,1)` 限制為一次一個；拖曳預覽「只保留最新請求」；背景預取可被前景請求中止。
- 匯出流程完整：先檢查素材存在、輸出可寫入與可覆蓋、磁碟空間（提醒但不強制）；先寫暫存檔再改名；取消時終止行程樹並清理；關閉視窗時等待清理完成。
- 沒有 `.Result`／`.Wait` 造成的死結風險。
- 背景工作錯誤處理：影格擷取透過 `Observe()`、波形透過 `Failed` 事件，都有轉成使用者可見的訊息。

**主要缺口**

1. 播放頭重繪成本與預覽影格的記憶體都還沒有在 Windows 上實測（W-2、P-3）。
2. 匯出的片段數量仍受每個片段輸入參數的長度限制（約 260 到 450 個，P-6）。

### 5.2 安全性

| 檢查項目 | 結果 |
|---|---|
| 敏感資訊硬編碼 | **未發現**（關鍵字搜尋只命中 `CancellationToken` 與 XAML 命名空間）；`.gitignore` 排除 `.env`、`*.pfx` |
| 設定檔與機敏資料 | 不適用（沒有設定檔、登錄檔存取、持久化使用者資料） |
| SQL Injection | 不適用（沒有資料庫） |
| 網路行為 | 程式碼本身沒有任何網路呼叫、自動更新或遙測；外部 FFmpeg／LibVLC 見 S-2 |
| 外部指令執行 | 只執行 `ffmpeg.exe`／`ffprobe.exe`；使用 `ArgumentList` 與 `UseShellExecute=false`，**沒有 Shell 字串拼接** |
| 不安全程式碼 | 無 `unsafe`、`DllImport`；只有 `VlcVideoSink` 的 `Marshal.AllocHGlobal`（W-8） |
| 濾鏡字串注入 | **未發現可行路徑**：進入 `-filter_complex` 的值都是數字、列舉，或 ffprobe 從固定名稱表輸出的字串；`FrameTransform.Filter()` 與 `ClipFraming.CropFilter()` 只輸出固定字串或數值 |
| 檔案路徑 | 輸出不能與來源相同；暫存檔名含隨機碼並以 `File.Exists` 迴圈避免撞名；倒放清單只含程式自己產生的檔名 |
| 使用者輸入驗證 | 輸入面很窄（檔案路徑與固定選項），沒有文字輸入欄位 |
| 授權合規 | `THIRD-PARTY-NOTICES.md` 完整；發佈時移除 4 個 GPL 的 libvlc plugin，CI 會掃描驗證（非法律意見） |

**結論**：沒有 Critical 或 High。主要風險屬於這類應用共通的：不受信任媒體交給原生解碼器與使用者自備的 FFmpeg（S-2）、相依套件更新（S-8）、FFmpeg 搜尋順序（S-1）。

---

## 6. 測試與維護性報告

### 6.1 現況

| 項目 | 結果 |
|---|---|
| 測試數量 | 約 320 個 `[Fact]`／`[Theory]`（`InlineData` 案例不另計；執行時約 500 個案例），分在 17 個檔案，約 4,950 行 |
| 測試性質 | 多數為真實 FFmpeg 的整合測試：匯出後以 `ffprobe` 量測影格數、亮度範圍、音量；有 FFmpeg 記憶體峰值量測；有取消、缺少編碼器、磁碟空間不足、素材被刪除等失敗路徑 |
| 可測試性設計 | 良好：`IExportView`／`IImportView` 搭配 Fake；建構子可注入 `listEncoders`、`freeSpace`；規則集中在 Core |
| 覆蓋較多的類別（以測試中的引用次數估計） | `TimelineSnap` 40、`ExportPlanner` 34、`ReverseGraph` 30、`ProcessRunner` 26、`ClipFraming` 15 |
| Logging | 有：`FileLog`／`Log` 寫到 `%LOCALAPPDATA%\VideoEditor\logs`，記錄未處理的例外、FFmpeg 失敗時的命令列與錯誤輸出、匯出的開始與結果（README 有說明） |
| 文件 | `README`（執行、發佈、操作、設計決定、已知限制、授權）、規格書（含非目標）、4 份規劃文件（記錄決策與實作結果） |
| CI | Windows 建置、測試、發佈驗證；固定 FFmpeg 版本；最小權限；沒有收集覆蓋率；已啟用 Dependabot |

### 6.2 需要注意

- **App 層（約 2,700 行，約占 src C# 的 44%）沒有自動化測試**，而最大的風險集中在這裡（逐格擷取狀態機、視窗事件接線）；播放時鐘、倒帶挑格、拖曳命中與貼齊的規則已抽到 Core 並有測試（見 T-2）。
- **沒有 Undo／存檔**是規格明列的非目標，不算缺陷。

---

## 7. 問題清單與改善優先順序

成本：S 小、M 中、L 大。依「風險 × 修正成本」排序。

| 優先 | ID | 問題 | 嚴重度 | 分類 | 成本 |
|---|---|---|---|---|---|
| 1 | Q-1／T-2 | `PreviewEngine` 職責過多，App 層仍無測試 | Medium | 已確認（部分已處理） | L |
| 2 | A-1 | `MainWindow` 持有過多狀態、手動同步 | Medium | 已確認 | L |
| 3 | W-2 | 播放時每個 tick 重繪整個時間軸 | Medium | 潛在，待實測 | M |
| 4 | S-2 | 白名單只擋網路協定；LibVLC 未處理 | Low~Medium | 部分已處理，待確認 | S~M |
| 5 | S-8 | 相依套件漏洞（待查 CVE）、無鎖定檔、Actions 未以 SHA 固定 | Low~Medium | 部分已處理，待確認 | S |
| 6 | P-3 | 影格 LOH 配置、快取上限 | Low~Medium | 潛在，待實測 | M |
| 7 | S-1 | FFmpeg 搜尋順序 | Low~Medium | 已確認 | S |
| 8 | Q-9 | 配樂漂移校正可能重複排入佇列 | Low~Medium | 潛在，待確認 | S |
| 9 | P-4 | 其餘 Core 類別沒有 `ConfigureAwait(false)` | Low | 已確認（部分已處理） | S |
| 10 | P-6 | 每個片段的輸入參數仍受命令列長度限制（約 260 到 450 個） | Low~Medium | 已確認（部分已處理） | L | 其餘 Core 類別沒有 `ConfigureAwait(false)` | Low | 已確認（部分已處理） | S |
| 11 | 其餘 | A-3、A-5、A-6、Q-7、Q-8、Q-10、Q-11、Q-12、W-3~W-9、P-7、P-9、S-5、T-3~T-7 | Low | — | S |

**已關閉或下調**：A-4 事件洩漏（不成立）；A-3 下調為 Low；`PreviewEngine.cs:326`／`:384` 的 `.Result`（不會阻塞，不成立）。

---

## 8. 問題詳細說明

> 每個問題的格式：嚴重程度、分類、位置、實際問題、影響、建議。行號為檢視當時的檔案內容。

### 8.1 架構（A）

#### A-1　`MainWindow` 是 God Object，且沒有 ViewModel
- **Medium／已確認**
- **位置**：`MainWindow.xaml.cs:27-43`、`MainWindow.*.cs`（7 個 partial、87 個方法）、`MainWindow.xaml`（44 個事件處理、54 個具名元素）
- **問題**：視窗同時持有 Timeline 模型、Editor、媒體清單、入出點、預覽引擎、波形快取、匯出與匯入控制器，以及 4 個避免事件循環的 `_updatingXxx` 旗標。partial 只是切分檔案，不是切分職責。
- **影響**：狀態同步靠手動呼叫（`UpdateMarks()`、`UpdateOutputInfo()`…），新增功能時漏呼叫就會畫面不同步；UI 邏輯無法單元測試。
- **建議**：先抽「狀態＋規則」到 Core（例如 `_selectedMedia`、`_points`、`_surface*` 與模式判斷）並補測試。不建議全面導入 MVVM。

#### A-3　FFmpeg 缺失時欄位為 null（已下調）
- **Low／已確認（部分驗證）**
- **位置**：`MainWindow.xaml.cs:60-69`、`:30-33`、`:43`
- **問題**：找不到 FFmpeg 時建構子提前 `return`，`_engine`、`_export`、`_import`、`_waveforms` 維持 `null`。
- **驗證結果**：約 25 個使用點中，只有 2 個 `!`（`OnEnginePosition`、`UpdatePlaybackButtons`），它們只掛在 `_engine` 自己的事件上，不會在 null 時被觸發；其餘都有 `?.` 或 `if (_x == null) return`。`MainWindow.Framing/Timeline/Preview` 沒有逐行讀完，結論限於已讀範圍。

#### A-4　事件掛載沒有解除（已關閉）
- 所有訂閱的雙方壽命與視窗相同，`DispatcherTimer` 在 `Dispose` 有 `Stop`，沒有靜態事件被訂閱。**不成立。**

#### A-5　`ClipModels.cs` 的小問題
- **Low／已確認**：複製了整份 `using`（約 11 行未使用）；`ClipRow.Label`（第 33 行）單行 5 個條件運算且超過 200 字元；檔名與內容（`MediaItem`、`ClipRow`）不符。

#### A-6　預設路徑與環境資訊散落在 Core
- **Low／已確認**：`ReversePreviewCache.DefaultRoot`（`Path.GetTempPath()`）與 `FfmpegLocator`（`AppContext.BaseDirectory`、`PATH`）。兩者都有參數可覆寫，測試友善，不構成問題。

### 8.2 程式碼品質（Q）

#### Q-1　`PreviewEngine` 職責過多
- **Medium／已確認（部分已處理）**
- **位置**：`Preview/PreviewEngine.cs`（約 765 行，約 35 個欄位、12 個常數）
- **問題**：同時負責播放時鐘與漂移校正、LibVLC 兩個播放器的指令佇列、倒帶（FFmpeg 成批解碼）、逐格擷取與預取與 LRU 快取、倒放預覽檔等待。
- **已處理**：播放位置、倒帶位置與漂移判斷（`PlaybackClock`）、倒帶時要顯示哪一格與下一批要解哪一格（`RewindPlan`）、影格範圍與預設影格率（`FrameMath`、`MediaInfo.EffectiveFrameRate`）已抽到 Core 並有測試。
- **仍待處理**：LibVLC 指令佇列、逐格擷取與預取的狀態（`_extracting`、`_pendingFrame`、`_prefetchCancel`）、快取與播放狀態仍在同一個類別，直接依賴 `LibVLC`、`DispatcherTimer`、`WriteableBitmap`，無法單元測試。
- **建議**：繼續把與 UI／VLC 無關的規則抽到 Core（逐格請求的合併與取消規則可以先做）；涉及執行緒與 LibVLC 的部分建議等 Windows 實測後再動。

#### Q-7　命名空間 `VideoEditor.Core.Timeline` 與類別 `Timeline` 同名
- **Low／已確認**：每個檔案都要加 `using TimelineModel = …` 別名；`MainWindow.xaml` 的 `x:Name="Timeline"` 也與類別同名。建議改名（機械式重構）。

#### Q-8　預期例外清單在多處各寫一遍
- **Low／已確認**
- **位置**：`ImportController.cs:54-55`、`:108`；`ExportController.cs:139`、`:145`、`:176`；`PreviewEngine.cs:566`、`:680`
- **問題**：`FfmpegException or Win32Exception or IOException or UnauthorizedAccessException` 的組合在至少 7 處各有不同子集。建議抽成共用判斷。

#### Q-9　配樂漂移校正可能重複排入佇列
- **Low~Medium／潛在，待確認**
- **位置**：`PreviewEngine.cs:419-421`（`UpdateMusic` 內 `Enqueue(() => _musicPlayer.Time = …)`）
- **問題**：`Tick` 每 16 ms 一次，偏差超過門檻時每個 tick 都再排入一次 seek，佇列沒有去重或上限。LibVLC 呼叫較慢時可能累積過期指令。
- **建議**：加入「已有待處理 seek」旗標。

#### Q-10　執行緒假設與實際不完全一致
- **Low／待確認**：`PreviewEngine` 類別註解說除 LibVLC 回呼外只在 UI 執行緒使用，但背景佇列會存取 `_videoPlayer`、`_musicPlayer`、`_sink` 與 `PlaybackRate`（`:469`）；`ImportController` 的 `_total`／`_done`／`_queued` 非執行緒安全，依賴一律由 UI 執行緒呼叫，但沒有文件說明。

#### Q-11　少數 `CancellationTokenSource` 與 `SemaphoreSlim` 未釋放
- **Low／已確認**：`WaveformCache`、`ReversePreviewCache` 的 `_gate`、`_disposed`；`WaveformCache` 失敗的工作會留在 `_tasks` 內，同一素材不會重試（可能是刻意設計，但沒有文件）。

#### Q-12　其他小項目
- **Low／已確認**：`TimelineClip.Volume` 是 `public set` 且無範圍驗證（其他欄位為 `internal set`）；`ClipModels.cs`、`MainWindow.Export.cs` 等複製整份 `using`；`ProcessRunner.Started` 靜態事件僅供測試使用；`Timeline.Duration` 每次存取都用 LINQ 重算。

### 8.3 WPF（W）

#### W-2　播放時每個 tick 都重繪整個時間軸
- **Medium／潛在，待實測**
- **位置**：`TimelineView.cs:110-119`（`Playhead` setter 呼叫 `InvalidateVisual()`）、`MainWindow.Playback.cs:34`、`:283`（`new StreamGeometry()`）、`:336`（`new FormattedText(...)`）
- **問題**：時間軸預覽播放時 `PositionChanged` 約每 16 ms 觸發，`OnRender` 對每個片段重畫波形與標籤；`OnEnginePosition` 另外每個 tick 設定 `ScrubSlider`、格式化 `TimeText`、更新標記與畫面位置框。
- **影響**：片段多、時間軸放大時播放可能掉格；需要實測確認。
- **建議**（待量測後決定）：把播放頭畫在獨立疊加元素；快取波形幾何與標籤；播放頭沒移動一個像素時不重繪。

#### W-3　顏色與樣式硬編碼，且在 XAML 與 C# 重複
- **Low~Medium／已確認**
- **位置**：`MainWindow.xaml`（`#FFD24A`：16、90、113、129 行；`#4A90D9`：97 行）、`ProgressOverlay.xaml`（`#4A90D9`：112、122 行）、`MainWindow.Preview.cs:25-28`、`TimelineView.cs:26-47`；`App.xaml` 的資源是空的
- **問題**：同一個顏色在 XAML 與程式碼各寫一份，沒有集中的 `ResourceDictionary` 或主題。另外所有 UI 文字都硬編碼為繁體中文，沒有 `.resx`。
- **建議**：色票抽到 `App.xaml`，C# 端以 `FindResource` 取用。

#### W-4　`ProgressOverlay` 隱性依賴宿主視窗的隱含樣式
- **Low／已確認**：`ProgressOverlay.xaml:121、133、146` 的文字沒有 `Foreground`，顏色來自 `MainWindow.xaml:21-23` 的隱含 `TextBlock` 樣式；換到其他視窗會在深色背景上變成預設黑字。

#### W-5　全域把 `Button`／`RadioButton` 設為 `Focusable=False`
- **Low／設計取捨**：避免按鈕搶走焦點而吃掉空白鍵與方向鍵，但代價是無法用 Tab 操作按鈕，不利鍵盤無障礙。

#### W-6　`_updatingXxx` 旗標不是例外安全的
- **Low／已確認**：`MainWindow.Playback.cs:29-32`、`MainWindow.Timeline.cs:34-47`、`:20-24`。旗標「設 true → 改控制項 → 設 false」沒有 `try/finally`；中間丟例外會讓對應事件處理器永遠失效。目前改的都是簡單屬性，機率很低。

#### W-7　資料繫結只有基本用法
- **Low／已確認**：`MediaItem` 沒有 `INotifyPropertyChanged`，目前縮圖在建立時就設好所以正常；`ClipList` 每次編輯重新指派 `ItemsSource` 並以旗標避免循環。

#### W-8　`VlcVideoSink` 的原生記憶體與回呼生命週期
- **Low／待確認**：`Dispose` 沒有「已釋放」旗標，之後若再觸發 `OnFormat` 會重新配置且不會釋放（`PreviewEngine.Dispose` 的順序正確，實務上不太會發生）；`CopyLatest` 在持有 `_gate` 鎖時呼叫 `consume`；傳給 `SetVideoCallbacks` 的實例方法委派是否被 LibVLCSharp 內部持有，取決於套件實作。

#### W-9　其他小項目
- **Low／已確認**：以 `e.Key.ToString()` 傳字串進 Core，每次按鍵都配置字串且拼寫錯誤編譯期抓不到；`FatalPanel` 只擋滑鼠，不擋鍵盤（已確認指令在 `_engine == null` 時都安全）。

### 8.4 穩定性與效能（P）

#### P-3　預覽影格的記憶體：LOH 配置與快取上限
- **Low~Medium／潛在，待實測**
- **位置**：`ProcessRunner.cs:86`、`:92`（每格 `new byte[chunkSize]`）、`PreviewEngine.cs` 常數（`MaxPreviewLongSide = 1280`、`FrameCacheCapacity = 64`）
- **問題**：每格配置寬 × 高 × 4 位元組（1280×720 約 3.5 MiB），遠超 LOH 門檻 85,000 位元組；倒帶與逐格移動時不斷產生與丟棄，容易引發第 2 代 GC。快取上限（由常數估算）16:9 約 225 MiB、1:1 約 400 MiB。
- **建議**：先量測；確認後改用 `ArrayPool<byte>` 或重用被淘汰的緩衝區，並考慮調降快取上限。

#### P-4　Core 類別庫大多沒有使用 `ConfigureAwait(false)`
- **Low／已確認（部分已處理）**
- **已處理**：`ProcessRunner` 內部與匯出收尾已加，逐格讀取不再每次回到 UI 執行緒，匯出收尾的暫存刪除也移到背景。
- **仍待處理**：其他 Core 類別沒有加。其中 `ImportController`、`ExportController` 在 await 之後要操作畫面，必須留在 UI 執行緒，所以不建議用 CA2007 全域強制。目前沒有死結（沒有同步等待）。

#### P-6　匯出的片段數量有上限
- **Low~Medium／已確認（部分已處理）**
- **位置**：`Export/ExportPlanner.cs`（`MaxCommandLength = 30000`）、`Export/FilterGraphFile.cs`、`Export/ExportService.cs`
- **已處理**：命令列超過 20,000 字元時，濾鏡圖改放進暫存檔再交給 FFmpeg。讀檔的選項不比對版本號，而是匯出前試跑一個極小的濾鏡圖：支援 `-/filter_complex`（FFmpeg 7 以上）就用它，否則用 `-filter_complex_script`。上限從約 80 個片段提高到：路徑 8 字元約 450 個、55 字元約 260 個。
- **仍待處理**：每個片段各有一組 `-ss -t -i 路徑` 輸入參數仍在命令列上，超過約 30,000 字元時匯出前會丟出 `InvalidOperationException("片段數量過多…")`（`ExportController` 會捕捉並顯示成訊息，不會當機）。要真正解除，得改成「同一素材只開一次輸入」，會改變解碼與定位行為，不建議。
- **待確認**：新語法路徑只在 CI 的 FFmpeg 9.0.2 驗證過；舊語法路徑在 FFmpeg 6.1.1 驗證過。

#### P-7　影格快取沒有失效機制，失敗的影格永遠不會重試
- **Low／已確認**：`_missingFrames`（`PreviewEngine.cs:562-564`、`:686`、`:696`）只增不減；短暫失敗會讓該格被永久標記；來源檔案被外部覆蓋後會顯示舊影格。

#### P-8　背景工作的錯誤處理盤點

| 背景工作 | 錯誤處理 | 評估 |
|---|---|---|
| 影格擷取（`ExtractPendingFramesAsync`、`FetchRewindFramesAsync`） | 透過 `Observe()` 轉成錯誤訊息，已知例外各自處理 | 良好 |
| 波形（`WaveformCache.RunAsync`） | 全部攔截並觸發 `Failed` | 良好 |
| 倒放預覽產生 | 失敗存在 Task 內，播放等待時才回報；`Sync` 對被捨棄項目的 `ContinueWith` 未觀察例外 | 可接受 |
| `PreviewEngine.Enqueue` | 攔截所有例外並記錄、回報 | 良好 |
| `async void`（匯出、載入） | 有 `try/catch`，錯誤顯示在狀態列並記錄 | 良好 |
| 全域 | `DispatcherUnhandledException`、`AppDomain.UnhandledException`、`UnobservedTaskException` 都會寫入紀錄 | 良好 |

#### P-9　其他效能觀察
- **Low／已確認**：`Timeline.Move`（`Timeline.cs:171-189`）每次滑鼠移動為 O(n²) 並用 LINQ 配置，100 個片段時約 2 萬次比較，影響很小；`Timeline.Duration`、`VideoClipAt` 每次呼叫都重新走訪，片段數少時可忽略。

### 8.5 安全性（S）

#### S-1　FFmpeg 可執行檔的搜尋順序與信任來源
- **Low~Medium／已確認行為**
- **位置**：`Core/Ffmpeg/FfmpegLocator.cs:10-25`
- **問題**：搜尋順序為 `程式目錄\tools\ffmpeg`、程式目錄、`PATH` 的每個項目，找到第一個同時有 `ffmpeg.exe` 與 `ffprobe.exe` 的目錄；沒有簽章或雜湊驗證、也沒有版本檢查；`PATH` 中的相對路徑項目會依工作目錄解析。
- **影響**：攻擊者必須已能寫入程式目錄、`PATH` 目錄或工作目錄，這通常已等同於可執行程式，增加的風險有限。
- **建議**：忽略相對路徑的 `PATH` 項目；在 UI 顯示實際使用的 FFmpeg 路徑與版本。

#### S-2　不受信任的媒體檔直接交給 FFmpeg 與 LibVLC 解析
- **Low~Medium／部分已處理**
- **已處理**：所有 FFmpeg／ffprobe 的輸入都加了 `-protocol_whitelist file`，偽裝成 .mp4 的播放清單不能再把 FFmpeg 導向網路。
- **仍待處理**：(1) 白名單只擋網路協定，播放清單類檔案仍可能被導向讀取其他本機檔案（是否可行取決於 FFmpeg 版本與組建）；(2) LibVLC 是原生程式碼，預設載入網路存取模組，沒有處理；(3) 使用者自備的 FFmpeg 沒有版本管理。
- **建議**：查證 LibVLC 3.0.x 能否以啟動選項停用網路模組；文件說明風險並建議使用最新版 FFmpeg。

#### S-5　`Preflight` 的路徑檢查有小缺口
- **Low／已確認**
- **位置**：`Export/ExportService.cs:26`、`:36`、`:39-42`、`:33`
- **問題**：`Path.GetFullPath` 與 `GetDirectoryName(...)!` 在 `try` 之外，格式不合法時可能丟出未被攔截的例外（實際上 `SaveFileDialog` 已先過濾）；可寫入探測建立 `.write-test-<guid>` 後若 `Delete` 失敗會留下殘檔；「輸出不能與來源相同」只比對完整路徑字串，不處理硬連結、符號連結、8.3 短檔名；`CreateDirectory` 在使用者確認前就建立資料夾。

#### S-8　相依套件與供應鏈
- **Low~Medium／部分已處理**
- **已處理**：已啟用 Dependabot（NuGet 與 GitHub Actions，每週）。
- **仍待處理**：`LibVLCSharp 3.10.2` 與 `VideoLAN.LibVLC.Windows 3.0.24` 是否有已知漏洞待查（LibVLC 是原生解碼器，攻擊面較大）；沒有 `packages.lock.json`；CI 的 Actions 以版本標籤（`@v7`、`@v6`）而非 commit SHA 引用；FFmpeg 透過 Chocolatey 固定版本安裝。

### 8.6 測試與維護性（T）

#### T-2　App 層沒有自動化測試
- **Medium／已確認（部分已處理）**
- **現況**：`PreviewEngine`、`TimelineView`、`MainWindow.*`、`VlcVideoSink`、`ProgressOverlay` 約 2,700 行仍沒有測試。不需要引入 UI 自動化，延續目前做法，把規則抽進 Core 再測試即可。
- **已處理**：預覽時鐘與漂移判斷、倒帶挑格、快捷鍵修飾鍵判斷、時間軸的命中區域與移動貼齊規則已抽到 Core 並補測試。
- **仍待處理**：`MainWindow` 的狀態與視窗事件接線、逐格擷取狀態機、波形與倒放預覽的 UI 協調。

#### T-3　部分 Core 類別沒有直接的測試引用
- **Low／待確認**：以類別名稱搜尋，`PreviewGeometry`（64 行）與 `TemporaryFiles` 在測試目錄 0 個檔案出現（後者可能被間接覆蓋）。請以覆蓋率報告確認。

#### T-4　整合測試依賴 FFmpeg 且沒有分類
- **Low／已確認**：`TestMedia.cs:12`、`:21`。沒有 FFmpeg 時使用 `media` 集合的測試類別會因 fixture 建立失敗而整批失敗，而非略過；沒有 `[Trait]`。建議加 `Category=Integration` 或 skippable 測試。

#### T-5　CI 沒有覆蓋率與靜態分析
- **Low／已確認**：`dotnet test` 沒有 `--collect`（雖已引用 `coverlet.collector`）；沒有 `.editorconfig`、格式檢查、`TreatWarningsAsErrors`。

#### T-6　少數測試依賴時間
- **Low／已確認**：`ReverseTests.cs:574`（`Task.Delay(300)`）、`:657`（輪詢最多 5 秒）、`WaveformTests.cs:271`（`Task.Delay(30)`）；CI 較慢時可能偶發失敗。

#### T-7　文件與維護性的小項目
- **Low／已確認**：沒有給開發者的專案結構導覽（規格第 5 節可部分替代，未讀完）；4 份規劃文件放在倉庫根目錄，`FIX-PLAN.md` 各階段多已完成，之後可移到 `docs/`。

---

## 9. 建議後續改善路線

> 階段 1 到階段 4 的主要項目已完成（PR #39、#43），這裡只留尚未做的。每一階段完成後建議重新執行建置與測試。

### 階段 0：在 Windows 上驗證（不改程式碼）
1. `dotnet build` 與 `dotnet test` 已由 CI（`windows-latest`、FFmpeg 9.0.2）執行並通過；可再記錄警告數。
2. 執行 `dotnet list package --vulnerable --include-transitive`（S-8）。
3. 用 Visual Studio 診斷工具量測播放時的 CPU 與 GC（W-2、P-3）。
4. 手動測試：Ctrl+S 不分割片段、匯入中途取消、損毀檔案不中斷整批匯入、播放／倒帶／拖曳時間軸、強制結束程式後 FFmpeg 是否結束、多片段（200 個以上）匯出時濾鏡圖檔案的新語法（P-6）。
5. 執行覆蓋率報告（T-3）。

### 階段 4（剩餘）：結構改善
- Q-1、T-2：繼續把與 UI／VLC 無關的規則抽進 Core 並補測試。

### 階段 5：視需要
- W-2：依階段 0 的量測結果，決定是否分離播放頭的繪製。
- A-1：若功能持續增加，才考慮輕量的 Presenter／ViewModel。

---

## 10. 待確認事項彙整

| # | 內容 | 如何確認 |
|---|---|---|
| 1 | LibVLCSharp 與 libvlc 套件的已知漏洞 | `dotnet list package --vulnerable --include-transitive` |
| 2 | 預覽影格的 GC 暫停是否可感、快取實際記憶體（P-3） | `dotnet-counters` 或 Visual Studio 診斷工具 |
| 3 | 播放時時間軸重繪的實際成本（W-2） | Profiler |
| 4 | 主程式被強制結束時，匯出中的 FFmpeg 是否自行結束（必要時再考慮 Job Object） | 匯出中結束程式後觀察 `ffmpeg.exe` |
| 5 | FFmpeg 是否會跟隨偽裝成 `.mp4` 的播放清單去讀本機檔案或連網（S-2） | 在隔離環境用使用者的 FFmpeg 版本測試 |
| 6 | LibVLC 3.0.x 能否以啟動選項停用網路模組（S-2） | 查閱 VLC 3.0 文件 |
| 7 | 匯出同時使用預覽時，兩個 FFmpeg 各最多約 512 MiB 緩衝是否使低記憶體電腦過載 | 實測 |
| 8 | 連按兩次關閉鈕時，第二次會略過取消等待（`_closingAfterExport` 已為 `true`），視窗可能在清理 `.part` 暫存檔前結束 | 實際操作 |
| 9 | `PreviewGeometry`、`TemporaryFiles` 是否被間接覆蓋（T-3） | 覆蓋率報告 |
| 10 | 傳給 `SetVideoCallbacks` 的委派是否被 LibVLCSharp 內部持有（W-8） | 查閱 LibVLCSharp 3.10.2 原始碼 |

---

## 11. 分析範圍與限制

- **方法**：分七個階段（基本資訊、架構、程式碼品質、WPF、穩定性與效能、安全性、測試與維護性），逐階段讀取檔案並回報，沒有一次載入整個專案。
- **未讀取的目錄**：`bin`、`obj`、`.vs`、`packages`、`node_modules`、`.git`。
- **這是靜態分析**：沒有建置、執行或目視 UI，所以效能與外部工具行為都標為「待實測」或「待確認」。
- **沒有逐行讀完的檔案**：`TimelineView.cs` 的 200–372、466–544 行；`MainWindow.Timeline/Framing/Marks/Preview.cs` 的大部分；`ReversePreviewCache.cs` 的 1–25 與 100–130 行；`ExportService.cs` 的 100–148 行；`TimelineSnap`、`SilenceDetector`、`MediaInfo`、`InOutPoints`、`EditPoints`、`WaveformColumns`、`PreviewPlanner`、`PreviewGeometry`、`OutputSettings`、`FrameBatch`、`PlaybackText` 等 Core 小檔案；測試檔多半只讀了結構與統計。這些位置可能還有未發現的問題。
- **行數與覆蓋比例是粗估**：行數含註解與空行；App 層「約 46% 無測試」是以檔案行數粗估，不是覆蓋率量測。
- **套件漏洞、FFmpeg 的行為、LibVLC 的選項**都需要線上或實機確認。
- **不是法律意見**：授權合規的觀察只是閱讀文件與設定的摘要。
