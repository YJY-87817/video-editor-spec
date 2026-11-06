using System.Diagnostics;
using System.IO;
using VideoEditor.Core.Ffmpeg;
using VideoEditor.Core.Models;
using TimelineModel = VideoEditor.Core.Timeline.Timeline;

namespace VideoEditor.Core.Export;

/// <summary>匯出流程對畫面的需求。由視窗實作，讓匯出流程不必知道任何控制項，也方便測試。</summary>
public interface IExportView
{
    /// <summary>無法匯出（例如輸出路徑不可寫入）。</summary>
    void ShowProblem(string message);

    /// <summary>匯出前的提醒，回傳使用者是否要繼續。</summary>
    bool ConfirmWarnings(IReadOnlyList<string> warnings);

    void PausePreview();

    /// <summary>開始匯出：顯示進度與取消按鈕。</summary>
    void ShowStarted(string title);

    /// <summary>更新進度（0–1）與說明文字。</summary>
    void ShowProgress(double value, string text);

    void ShowSucceeded(string output, string elapsed);

    /// <summary>取消或失敗後的狀態訊息。</summary>
    void ShowStatus(string message);

    void ShowError(string message);

    /// <summary>無論成功、取消或失敗，匯出結束時都會呼叫：還原按鈕與工作列進度。</summary>
    void ShowEnded();

    /// <summary>成功訊息顯示幾秒後，收起進度列。</summary>
    void HideProgress();
}

/// <summary>檢查、規劃、執行匯出，並把進度與結果交給 <see cref="IExportView"/>。</summary>
public sealed class ExportController(FfmpegTools tools, IExportView view, int doneDisplayMs = ExportController.DefaultDoneDisplayMs)
{
    public const int DefaultDoneDisplayMs = 4000;

    private CancellationTokenSource? _cancel;
    private Task _ended = Task.CompletedTask;
    private int _run;

    /// <summary>是否有匯出正在執行（從開始呼叫 FFmpeg 到清掉暫存檔為止）。</summary>
    public bool IsRunning => _cancel != null;

    public void Cancel() => _cancel?.Cancel();

    /// <summary>取消匯出，並等到 FFmpeg 結束、暫存檔清掉為止。沒有匯出時立即完成。</summary>
    public Task CancelAsync()
    {
        _cancel?.Cancel();
        return _ended;
    }

    public async Task RunAsync(TimelineModel timeline, OutputSettings settings, string output)
    {
        var problem = ExportService.Preflight(timeline, output);
        if (problem != null)
        {
            view.ShowProblem(problem);
            return;
        }

        ExportPlan plan;
        try
        {
            plan = ExportPlanner.Build(timeline, settings, output);
        }
        catch (InvalidOperationException error)
        {
            // 例如片段太多、超過 Windows 命令列長度上限：訊息本身就是給使用者看的說明。
            view.ShowProblem(error.Message);
            return;
        }
        if (plan.Warnings.Count > 0 && !view.ConfirmWarnings(plan.Warnings)) return;

        view.PausePreview();
        var cancel = new CancellationTokenSource();
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _cancel = cancel;
        _ended = ended.Task;
        var title = $"匯出中：{Path.GetFileName(output)}（{plan.Size.Width}×{plan.Size.Height}）";
        var clock = Stopwatch.StartNew();
        var run = ++_run;
        var finished = false;
        var succeeded = false;
        view.ShowStarted(title);
        var progress = new Progress<double>(value =>
        {
            // Progress 是非同步送回 UI 執行緒的，匯出結束後才到的回報要忽略。
            if (finished) return;
            // 進度太少時估算不準，先不顯示剩餘時間。
            var text = value is > 0.02 and < 1
                ? $"{title}　剩餘約 {FormatSpan(clock.Elapsed * ((1 - value) / value))}"
                : title;
            view.ShowProgress(value, text);
        });

        try
        {
            await new ExportService(tools).ExportAsync(plan, output, progress, cancel.Token);
            finished = true;
            succeeded = true;
            view.ShowSucceeded(output, FormatSpan(clock.Elapsed));
        }
        catch (OperationCanceledException)
        {
            view.ShowStatus("已取消匯出。");
        }
        catch (ExportRenameException error)
        {
            view.ShowStatus($"影片已做好，但未能存成輸出檔名，保留在：{error.TemporaryPath}");
            view.ShowError(error.Message);
        }
        catch (Exception error) when (error is FfmpegException or IOException or UnauthorizedAccessException)
        {
            view.ShowStatus("匯出失敗。");
            view.ShowError((error as FfmpegException)?.Stderr ?? error.Message);
        }
        finally
        {
            finished = true;
            view.ShowEnded();
            _cancel = null;
            cancel.Dispose();
            ended.TrySetResult();
        }

        // 完成訊息留幾秒再收起；期間若又開始新的匯出，就交給新的那次處理。
        if (succeeded) await Task.Delay(doneDisplayMs);
        if (run != _run) return;
        view.HideProgress();
    }

    public static string FormatSpan(TimeSpan span) =>
        span.TotalHours >= 1 ? $"{(int)span.TotalHours} 小時 {span.Minutes} 分"
        : span.TotalMinutes >= 1 ? $"{span.Minutes} 分 {span.Seconds} 秒"
        : $"{Math.Max(1, span.Seconds)} 秒";
}
