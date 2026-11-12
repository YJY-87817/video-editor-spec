# 時間軸聲音波形：規劃

2026-11-12 規劃並實作完成（見第七節）。目標：時間軸的 A1（影片原音）與 A2（配樂）片段上畫出聲音波形，剪輯時看得出哪裡有聲音、哪裡是靜音、拍點在哪，方便對齊切點。

## 一、範圍

| 做 | 不做（這次） |
|---|---|
| A1、A2 片段顯示波形 | 素材模式（預覽下方進度列）的波形 |
| 修剪、移動、縮放時波形跟著正確對位 | 依波形自動吸附（拍點／靜音偵測） |
| 倒放片段的波形左右翻轉 | 立體聲左右聲道分開畫 |
| 音量調整即時反映在波形高度 | 波形存到磁碟（程式關閉就丟掉，與規格「不含專案存檔」一致） |
| 波形還沒算好時，片段照常顯示與操作 | 播放時的音量表（VU meter） |

## 二、現況（與波形有關的部分）

- `TimelineView`（`src/VideoEditor.App/Controls/TimelineView.cs`）用 `OnRender` 自行繪製，A1、A2 目前是純色方塊加文字（檔名、音量）。沒有音訊的影片片段畫成灰色「無音訊」。
- 播放時每次 `Playhead` 改變都會 `InvalidateVisual()`，整條時間軸重畫，所以波形繪製必須便宜。
- `TimelineClip.ToSourceTime` 已經處理倒放的時間對應，波形可以直接沿用。
- FFmpeg 呼叫都經過 `ProcessRunner`，目前只有「全部讀完再回傳」與「讀固定大小區塊」兩種，沒有邊讀邊處理的串流介面。
- 匯入流程在 `ImportController`；倒放預覽檔 `ReversePreviewCache` 是「背景、一次一個 FFmpeg、可取消、關閉時清理」的現成範例。
- 時間軸區域高度 230（`MainWindow.xaml` 第三列），扣掉工具列與捲軸後約 180 像素；目前三列軌道共用 164 像素。

## 三、設計

### 1. 取得波形資料（Core）

新增 `VideoEditor.Core/Ffmpeg/WaveformExtractor.cs`：

```
ffmpeg -v error -i <檔案> -map 0:a:0 -vn -sn -dn -ac 2(只在超過 2 聲道時) -f s16le -acodec pcm_s16le pipe:1
```

- **不降取樣率**：降到 8 kHz 會先經過低通濾波，鼓、鈸這類高頻的尖峰會被削掉，波形看起來比實際小。原取樣率 48 kHz、雙聲道一小時約 690 MB，但邊讀邊算、不留在記憶體，實際只是幾秒鐘的管線傳輸。
- **不混成單聲道**：左右聲道相位相反時混音會互相抵消，看起來像靜音。改為每個取樣點取各聲道絕對值的最大值。5.1 等多聲道先 `-ac 2`。
- 每 **2 ms**（500 格／秒）取一個峰值（0–1 的線性振幅）。時間軸最大縮放 800 px/s 時一像素 1.25 ms，2 ms 已經夠細；一小時 180 萬格 × 4 bytes ≈ 7 MB。
- 需要在 `ProcessRunner` 加一個串流讀取標準輸出的方法（例如 `RunWithStreamAsync(exe, args, Func<Stream, CancellationToken, Task>)`），沿用現有的取消與錯誤處理、`Started` 事件（關閉程式時會結束子行程）。

新增 `VideoEditor.Core/Models/WaveformData.cs`：

- 內容：每秒格數、峰值陣列、素材長度。
- **多層級（mipmap）**：另外存每 2、4、8…格合併的最大值。縮小到 2 px/s 時一像素涵蓋 250 格，用上層資料讓每一像素只看常數個數，播放時重畫不會變慢。
- `double Peak(double sourceFrom, double sourceTo)`：來源時間區間內的最大峰值，自動挑合適的層級；區間比一格還小時取所在那一格。
- 純計算、不依賴 WPF，可以完整單元測試。

### 2. 背景產生與快取（Core）

新增 `VideoEditor.Core/Ffmpeg/WaveformCache.cs`，模式比照 `ReversePreviewCache`：

- 以素材路徑為鍵，同一素材只算一次；多個片段（同一支影片切多段）共用。
- 一次只跑一個 FFmpeg，避免拖慢預覽播放；倒放預覽產生時也不要搶（兩邊各自一個 `SemaphoreSlim`，必要時再合併）。
- **什麼時候開始算**：匯入完成後就排入（素材有音訊才算），不等加入時間軸。匯入進度面板不等待波形，波形不顯示進度（只在片段上顯示「波形產生中」）。
- 算好時觸發 `Ready(MediaInfo)` 事件，視窗收到後 `Timeline.InvalidateVisual()`（要切回 UI 執行緒）。
- 失敗（解碼錯誤、FFmpeg 被擋）不影響其他功能：片段維持現在的樣子，狀態列提示一次。
- 關閉程式時取消進行中的工作（`Dispose`）。

### 3. 繪製（App）

`TimelineView` 新增 `Func<MediaInfo, WaveformData?> WaveformOf`（由視窗設定，避免 View 依賴快取類別）。`DrawClip` 對 A1／A2 片段在色塊上、文字下畫波形：

- 只算畫面上看得到的那段（裁掉超出 `HeaderWidth` 與視窗右緣的部分）。
- 每一像素欄 x：時間軸時間 `[t, t + 1/pps)` → 用 `clip.ToSourceTime` 換成來源區間 → `Peak()` → 高度。倒放片段因為 `ToSourceTime` 已經反向，波形自動左右翻轉。
- 高度 = 峰值 × 片段音量，以軌道中線上下對稱。**刻度用分貝**：-48 dB 到 0 dB 對應到 0 到半高（線性刻度下一般說話聲只有薄薄一條，看不出起伏）。待決定，見第五節。
- 音量超過 100% 導致超過 0 dB 的部分改用醒目的顏色，提示匯出時會破音（匯出端是線性增益，沒有限幅器）。
- 整個片段的波形組成一個 `StreamGeometry`（上緣一路畫過去、下緣畫回來），`Freeze` 後一次 `DrawGeometry`，不要每像素一條線。
- 顏色：片段底色的淺色版、半透明；文字加一層深色底，避免被波形蓋住看不清。
- 片段太窄（小於約 3 像素）不畫。
- 波形還沒好時：照現在的畫法，文字後面加「（波形產生中）」。

### 4. 軌道高度

目前音訊軌 36 像素，波形上下各只有約 15 像素。建議：

- 音訊軌高度改為 48，時間軸區域由 230 增加到 254（三列共 188 像素）。
- 若預覽畫面因此太矮，替代方案是維持 36，只把文字縮到左上角一行。

## 四、實作階段

| 階段 | 內容 | 驗證 |
|---|---|---|
| 1 | `WaveformData`（峰值、多層級、`Peak` 查詢、分貝換算） | 單元測試：合成資料的峰值、跨層級查詢與逐格計算結果一致、區間小於一格、超出素材範圍 |
| 2 | `ProcessRunner` 串流讀取、`WaveformExtractor` | 整合測試（lavfi 產生）：0.5 振幅的正弦波峰值約 0.5；前 1 秒靜音後 1 秒有聲，波形在 1 秒處切換；左右聲道反相仍有波形；沒有音軌的檔案；取消時 FFmpeg 結束 |
| 3 | `WaveformCache`（背景、一次一個、去重、失敗、Dispose） | 單元測試：同一素材只產生一次、Dispose 後工作取消、失敗不拋到呼叫端 |
| 4 | 匯入後排入、`TimelineView` 繪製 A1／A2、倒放、音量、未完成提示、軌道高度 | 單元測試只涵蓋「像素欄 → 來源區間」的換算（抽到 Core，含倒放）；畫面需在 Windows 手動驗證 |
| 5 | 效能與文件 | 一小時素材：產生時間與記憶體（沿用 `TestMedia.PeakMemory`）；播放時重畫不掉格（手動）；README「規格外新增的功能」與操作說明、`video-editor-spec.md` 4.1 節補上 |

需要在 Windows 上手動確認的項目：
- 拖曳修剪、移動、縮放時波形與聲音位置一致（可用拍手聲的素材對照播放）。
- 倒放片段的波形方向。
- 音量滑桿拖動時波形即時變化。
- 長片段在最小縮放下播放，時間軸不卡。

## 五、決定（2026-11-12，全部採用建議）

1. **刻度**：分貝，-48 dB 到 0 dB 對應到 0 到半高。
2. **軌道高度**：音訊軌 36 → 48，時間軸區域 230 → 254。
3. **開始計算的時機**：匯入完成就排入背景計算。
4. **破音提示**：峰值乘上音量後超過 0 dB 的部分改用紅色。

## 六、細部步驟

| # | 檔案 | 內容 |
|---|---|---|
| 1 | `Core/Models/WaveformData.cs` | 峰值陣列＋多層級；`Peak(from, to)`；`static ToHeight(peak, volume)` 分貝換算；`FromSamples` 建構 |
| 2 | `Core/Ffmpeg/ProcessRunner.cs` | `RunWithStreamAsync`：把標準輸出交給呼叫端邊讀邊處理 |
| 3 | `Core/Ffmpeg/WaveformExtractor.cs` | FFmpeg 輸出 s16le，逐 2 ms 取各聲道絕對值最大值 |
| 4 | `Core/Ffmpeg/WaveformCache.cs` | 以路徑為鍵、一次一個、`Ready` 事件、失敗事件、`Dispose` 取消 |
| 5 | `Core/Timeline/WaveformColumns.cs` | 片段可見範圍 → 每像素欄的來源區間與高度（含倒放），供繪製與測試 |
| 6 | `App`：`MainWindow.Import.cs`、`MainWindow.xaml(.cs)` | 匯入後排入、Ready 時重畫、關閉時 Dispose、時間軸區域加高 |
| 7 | `App/Controls/TimelineView.cs` | A1／A2 畫波形、破音紅色、「波形產生中」、文字底色、音訊軌 48 |
| 8 | 測試、README、規格 4.1 | 單元與整合測試；文件補上波形 |

## 七、實作結果（2026-11-12）

照第六節完成。驗證（Linux、FFmpeg 6.1.1、.NET 10）：

- 新增 31 個測試（`WaveformTests.cs`）全部通過：峰值查詢與逐格計算一致、分貝換算、取樣點跨區塊、左右反相、5.1 聲道、靜音轉有聲的位置、倒放翻轉、只算可見範圍、音量與破音、快取去重／一次一個／失敗／Dispose 取消。
- 全部測試 354 個中 3 個失敗，都與波形無關、改動前就失敗：檔案占用與唯讀檔的測試在 Linux 以 root 執行時不成立（`StartupCleanupRemovesOnlyFoldersNobodyUses`、`FinishedVideoIsKeptWhenTheOutputCannotBeReplaced`、`PreflightRejectsReadOnlyOutput`），Windows CI 上應該正常。
- App 以 `EnableWindowsTargeting` 在 Linux 編譯通過，沒有警告。
- 一小時 AAC 立體聲素材：產生波形 12.2 秒（其中 FFmpeg 解碼本身約 11 秒），FFmpeg 最大記憶體 57 MB，波形資料約 27 MB；一個片段在畫面寬 1600 像素時，計算波形欄約 0.3–0.6 ms。
- 實作與設計的差異：倒放片段的聲音對應改用「從出點往回算」（`WaveformColumns.SourceTime`），與匯出時 areverse 的聲音一致，而不是畫面用的「出點前最後一格」，兩者差不到一格。

仍需在 Windows 上手動驗證：第四節列的四項，以及波形顏色與文字底色是否清楚。
