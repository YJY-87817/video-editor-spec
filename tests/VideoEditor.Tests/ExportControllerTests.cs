using System.Collections.Concurrent;
using VideoEditor.Core.Export;
using VideoEditor.Core.Models;
using TimelineModel = VideoEditor.Core.Timeline.Timeline;

namespace VideoEditor.Tests;

/// <summary>驗證匯出流程對畫面的通知順序與結果；實際呼叫 FFmpeg。</summary>
[Collection("media")]
public class ExportControllerTests(TestMedia media)
{
    private static readonly OutputSettings Default = new(AspectRatio.R16x9, FitMode.Fill);

    [Fact]
    public async Task ProblemStopsBeforeAnythingStarts()
    {
        var t = new TimelineModel();
        t.Append(await media.Probe(media.Landscape), 0, 1);
        var view = new FakeView();

        // 輸出檔與來源相同，預檢會拒絕。
        await new ExportController(media.Tools, view, 0).RunAsync(t, Default, media.Landscape);

        Assert.Equal("Problem", string.Join(",", view.Calls));
        Assert.NotNull(view.Problem);
    }

    [Fact]
    public async Task DeclinedWarningsDoNotStartTheExport()
    {
        var t = new TimelineModel();
        t.Append(await media.Probe(media.Landscape), 0, 1);
        t.Append(await media.Probe(media.NoAudio), 0, 1); // 影格率不同，會提醒
        var view = new FakeView { Confirm = false };
        var output = media.Output("declined.mp4");

        await new ExportController(media.Tools, view, 0).RunAsync(t, Default, output);

        Assert.Equal("ConfirmWarnings", string.Join(",", view.Calls));
        Assert.NotEmpty(view.Warnings);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task SuccessReportsProgressThenEndsAndHides()
    {
        var t = new TimelineModel();
        t.Append(await media.Probe(media.Landscape), 0, 1);
        var view = new FakeView();
        var output = media.Output("controller-ok.mp4");

        await new ExportController(media.Tools, view, 0).RunAsync(t, Default, output);

        Assert.Equal("PausePreview,Started,Succeeded,Ended,HideProgress", string.Join(",", view.Calls.Where(c => c != "Progress")));
        Assert.Contains("controller-ok.mp4", view.StartedTitle);
        Assert.Equal(output, view.SucceededOutput);
        Assert.True(File.Exists(output));
    }

    [Fact]
    public async Task CancelReportsStatusAndLeavesNoFile()
    {
        var t = new TimelineModel();
        t.Append(await media.Probe(media.Landscape), 0, 4);
        var output = media.Output("controller-cancel.mp4");
        ExportController? controller = null;
        // 在「開始匯出」通知時就取消：這時 FFmpeg 還沒跑完，結果固定。
        // 不能等進度回報才取消：進度是非同步送回的，機器快的時候匯出會先完成，取消就來不及。
        var view = new FakeView { OnStarted = () => controller!.Cancel() };
        controller = new ExportController(media.Tools, view, 0);

        await controller.RunAsync(t, Default, output);

        Assert.Equal("已取消匯出。", string.Join(",", view.Statuses));
        Assert.Contains("Ended", view.Calls);
        Assert.DoesNotContain("Succeeded", view.Calls);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task FailureShowsStderrAndEnds()
    {
        var source = await media.Probe(media.Landscape);
        var t = new TimelineModel();
        t.Append(source with { Path = media.Output("missing-source.mp4") }, 0, 1);
        var view = new FakeView();

        await new ExportController(media.Tools, view, 0).RunAsync(t, Default, media.Output("controller-fail.mp4"));

        Assert.Equal("匯出失敗。", string.Join(",", view.Statuses));
        Assert.Contains("missing-source.mp4", view.Error);
        Assert.Contains("Ended", view.Calls);
        Assert.DoesNotContain("Succeeded", view.Calls);
    }

    [Fact]
    public async Task TooManyClipsShowsAProblemInsteadOfThrowing()
    {
        // 片段多到超過 Windows 命令列長度上限時，規劃會失敗；要以一般提示告訴使用者，不能變成未處理的例外。
        var source = new MediaInfo
        {
            Path = media.Output("many.mp4"), Duration = 10, HasVideo = true, HasAudio = true,
            DisplayWidth = 1920, DisplayHeight = 1080, FrameRate = new Rational(30, 1), PixelFormat = "yuv420p",
        };
        var t = new TimelineModel();
        for (var i = 0; i < 300; i++) t.Append(source, 0, 1);
        var view = new FakeView();
        var output = media.Output("too-many.mp4");

        await new ExportController(media.Tools, view, 0).RunAsync(t, Default, output);

        Assert.Equal("Problem", string.Join(",", view.Calls));
        Assert.Contains("命令列長度上限", view.Problem);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task CancelAsyncWaitsUntilTheTemporaryFileIsGone()
    {
        var t = new TimelineModel();
        t.Append(await media.Probe(media.Landscape), 0, 4);
        var output = media.Output("controller-close.mp4");
        ExportController? controller = null;
        Task? cancelled = null;
        var runningWhenStarted = false;
        // 與視窗關閉時相同：匯出進行中呼叫 CancelAsync，並且只等它（不等 RunAsync）。
        var view = new FakeView
        {
            OnStarted = () =>
            {
                runningWhenStarted = controller!.IsRunning;
                cancelled = controller.CancelAsync();
            },
        };
        controller = new ExportController(media.Tools, view, 0);
        Assert.False(controller.IsRunning);

        var run = controller.RunAsync(t, Default, output);
        Assert.True(runningWhenStarted);
        Assert.NotNull(cancelled);
        await cancelled;

        // CancelAsync 完成時，FFmpeg 已結束、暫存檔已清掉、畫面已收到結束通知。
        Assert.False(controller.IsRunning);
        Assert.Contains("Ended", view.Calls);
        Assert.False(File.Exists(media.Output("controller-close.part.mp4")));
        Assert.False(File.Exists(output));
        await run;
        Assert.Equal("已取消匯出。", string.Join(",", view.Statuses));
    }

    [Fact]
    public async Task CancelAsyncWithoutAnExportCompletesImmediately()
    {
        var controller = new ExportController(media.Tools, new FakeView(), 0);
        Assert.True(controller.CancelAsync().IsCompleted);
        await controller.CancelAsync();
    }

    [Fact]
    public void FormatSpanUsesTheLargestUnit()
    {
        Assert.Equal("1 秒", ExportController.FormatSpan(TimeSpan.FromMilliseconds(200)));
        Assert.Equal("45 秒", ExportController.FormatSpan(TimeSpan.FromSeconds(45)));
        Assert.Equal("2 分 5 秒", ExportController.FormatSpan(TimeSpan.FromSeconds(125)));
        Assert.Equal("1 小時 1 分", ExportController.FormatSpan(TimeSpan.FromSeconds(3700)));
    }

    private sealed class FakeView : IExportView
    {
        private readonly ConcurrentQueue<string> _calls = new();
        private readonly ConcurrentQueue<string> _statuses = new();

        public bool Confirm { get; init; } = true;
        public Action? OnProgress { get; init; }
        public Action? OnStarted { get; init; }
        public IReadOnlyList<string> Calls => [.. _calls];
        public IReadOnlyList<string> Statuses => [.. _statuses];
        public IReadOnlyList<string> Warnings { get; private set; } = [];
        public string? Problem { get; private set; }
        public string? Error { get; private set; }
        public string? StartedTitle { get; private set; }
        public string? SucceededOutput { get; private set; }

        public void ShowProblem(string message) { _calls.Enqueue("Problem"); Problem = message; }

        public bool ConfirmWarnings(IReadOnlyList<string> warnings)
        {
            _calls.Enqueue("ConfirmWarnings");
            Warnings = warnings;
            return Confirm;
        }

        public void PausePreview() => _calls.Enqueue("PausePreview");
        public void ShowStarted(string title)
        {
            _calls.Enqueue("Started");
            StartedTitle = title;
            OnStarted?.Invoke();
        }

        public void ShowProgress(double value, string text)
        {
            _calls.Enqueue("Progress");
            OnProgress?.Invoke();
        }

        public void ShowSucceeded(string output, string elapsed) { _calls.Enqueue("Succeeded"); SucceededOutput = output; }
        public void ShowStatus(string message) => _statuses.Enqueue(message);
        public void ShowError(string message) { _calls.Enqueue("Error"); Error = message; }
        public void ShowEnded() => _calls.Enqueue("Ended");
        public void HideProgress() => _calls.Enqueue("HideProgress");
    }
}
