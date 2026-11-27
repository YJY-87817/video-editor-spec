using System.Text.Json;
using VideoEditor.Core.Logging;
using VideoEditor.Core.Models;

namespace VideoEditor.Core.Ffmpeg;

/// <summary>匯入流程對畫面的需求。由視窗實作，讓匯入流程不必知道任何控制項，也方便測試。</summary>
public interface IImportView
{
    /// <summary>這個檔案是否已經在素材清單裡（已匯入的不再重複匯入）。</summary>
    bool IsAlreadyImported(string path);

    /// <summary>更新讀取進度：說明文字與整體進度（0–1）。</summary>
    void ShowProgress(string text, double value);

    /// <summary>全部匯入完成，收起進度。</summary>
    void HideProgress();

    /// <summary>讀取成功，加入素材清單。沒有畫面或縮圖產生失敗時 thumbnail 為 null。</summary>
    void AddMedia(MediaInfo info, byte[]? thumbnail);

    /// <summary>
    /// 這一輪匯入有檔案無法匯入。整輪處理完（進度收起之後）才呼叫一次，依匯入順序列出，
    /// 一次拖進很多壞檔時才不會連跳好幾個對話框。沒有失敗時不呼叫。
    /// </summary>
    void ShowFailures(IReadOnlyList<ImportFailure> failures);
}

/// <summary>一個無法匯入的檔案：檔名與錯誤說明（FFmpeg 的錯誤輸出或例外訊息）。</summary>
public sealed record ImportFailure(string Name, string Message);

/// <summary>逐一讀取素材資訊與縮圖，並把進度與結果交給 <see cref="IImportView"/>。</summary>
public sealed class ImportController(MediaProbe probe, FrameExtractor extractor, IImportView view, int thumbnailWidth = 144)
{
    // 執行緒：這個類別不做任何同步。ImportAsync、Cancel 都必須從 UI 執行緒呼叫，
    // await 之後的程式碼會回到呼叫時的 SynchronizationContext（所以不用 ConfigureAwait(false)，之後還要操作畫面），
    // 因此下面的計數與集合永遠只在 UI 執行緒讀寫。沒有 SynchronizationContext 的環境（測試）則必須自己確保不並行呼叫。
    private int _total;
    private int _done;
    // 已排入但還沒處理完的檔案：還不在素材清單裡，但也不能再排一次。
    private readonly HashSet<string> _queued = new(StringComparer.OrdinalIgnoreCase);
    // 目前這一輪匯入共用的取消來源；Cancel 之後換新的，之後才拖進來的檔案不受影響。
    private CancellationTokenSource? _cancel;
    // 這一輪匯入目前為止的失敗；整輪結束時一次交給畫面。匯入中又拖進的檔案屬於同一輪。
    private readonly List<ImportFailure> _failures = [];

    /// <summary>
    /// 取消目前的匯入：正在讀的檔案立刻停止（終止 ffprobe／ffmpeg），還沒開始的檔案略過。
    /// 取消不算失敗，不會顯示錯誤；已經加入清單的素材保留。
    /// </summary>
    public void Cancel()
    {
        _cancel?.Cancel();
        _cancel = null;
    }

    public async Task ImportAsync(IEnumerable<string> paths)
    {
        var pending = paths.Where(path => !view.IsAlreadyImported(path) && _queued.Add(path)).ToList();
        if (pending.Count == 0) return;
        // 匯入中又拖進新檔案時，併入同一個進度。
        _total += pending.Count;
        var token = (_cancel ??= new CancellationTokenSource()).Token;
        var processed = 0;
        try
        {
            foreach (var path in pending)
            {
                // 取消後還沒開始的檔案不處理；它們由下面的 finally 移出排入清單並算成已結束。
                if (token.IsCancellationRequested) break;
                var name = Path.GetFileName(path);
                try
                {
                    Report(name, "讀取資訊", 0);
                    var info = await probe.ProbeAsync(path, token);
                    Report(name, "產生縮圖", 0.5);
                    view.AddMedia(info, await LoadThumbnailAsync(info, token));
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    // 使用者取消：不是失敗，不顯示錯誤。
                }
                // Win32Exception：ffprobe 無法啟動（例如被防毒軟體擋下）。也當成這個檔案匯入失敗，不中斷其他檔案。
                catch (Exception e) when (ExpectedErrors.IsProbe(e))
                {
                    // FfmpegException 的命令列與錯誤輸出已由 ProcessRunner 記錄，這裡不再重複堆疊。
                    Log.Warn($"匯入失敗：{path}", e is FfmpegException ? null : e);
                    _failures.Add(new ImportFailure(name, (e as FfmpegException)?.Stderr ?? e.Message));
                }
                finally
                {
                    _queued.Remove(path);
                    processed++;
                    CountDone(1);
                    Report(name, "完成", 0);
                    ReportFailuresIfFinished();
                }
            }
        }
        finally
        {
            // 預料之外的錯誤中斷了迴圈：還沒處理的檔案移出排入清單（之後還能再拖進來），
            // 並把它們算成已結束，進度才不會一直卡在畫面上。
            if (processed < pending.Count)
            {
                foreach (var path in pending.Skip(processed)) _queued.Remove(path);
                CountDone(pending.Count - processed);
                if (_total == 0) view.HideProgress();
                ReportFailuresIfFinished();
            }
        }
    }

    /// <summary>整輪匯入都處理完（進度已收起）時，把累積的失敗一次交給畫面。</summary>
    private void ReportFailuresIfFinished()
    {
        if (_total != 0 || _failures.Count == 0) return;
        var failures = _failures.ToList();
        _failures.Clear();
        view.ShowFailures(failures);
    }

    private void CountDone(int count)
    {
        _done += count;
        if (_done < _total) return;
        _done = 0;
        _total = 0;
    }

    /// <summary>stage 為目前檔案內的進度（0–1）。全部完成時隱藏。</summary>
    private void Report(string name, string step, double stage)
    {
        if (_total == 0)
        {
            view.HideProgress();
            return;
        }
        view.ShowProgress($"讀取素材 {_done + 1}/{_total}：{name}（{step}）", (_done + stage) / _total);
    }

    private async Task<byte[]?> LoadThumbnailAsync(MediaInfo info, CancellationToken token)
    {
        if (!info.HasVideo) return null;
        try
        {
            var bytes = await extractor.ThumbnailAsync(info.Path, info.IsStill ? 0 : Math.Min(1, info.Duration / 10), thumbnailWidth, token, info.IsStill);
            return bytes.Length == 0 ? null : bytes;
        }
        catch (Exception e) when (ExpectedErrors.IsProcess(e))
        {
            // 沒有縮圖不影響其他功能。
            return null;
        }
    }
}
