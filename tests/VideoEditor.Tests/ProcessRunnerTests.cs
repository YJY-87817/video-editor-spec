using VideoEditor.Core.Ffmpeg;

namespace VideoEditor.Tests;

/// <summary>把 FFmpeg 輸出切成固定大小區塊（rawvideo 影格）的讀取方式。</summary>
[Collection("media")]
public class ProcessRunnerTests(TestMedia media)
{
    // 10 格 4×2 灰階影格，每格 8 個位元組。
    private static readonly string[] Gray =
        ["-hide_banner", "-v", "error", "-f", "lavfi", "-i", "color=c=gray:s=4x2:r=10:d=1", "-f", "rawvideo", "-pix_fmt", "gray", "pipe:1"];

    [Fact]
    public async Task ChunksMatchTheWholeOutput()
    {
        var whole = await ProcessRunner.RunForBytesAsync(media.Tools.Ffmpeg, Gray);
        var chunks = await ProcessRunner.RunForChunksAsync(media.Tools.Ffmpeg, Gray, 8, 100);

        Assert.True(whole.Length >= 16);
        Assert.Equal(whole.Length / 8, chunks.Count);
        Assert.Equal(whole, chunks.SelectMany(c => c).ToArray());
        Assert.NotSame(chunks[0], chunks[1]); // 每塊是各自的陣列，不會被下一塊覆寫
    }

    [Fact]
    public async Task StopsAtTheLimitAndStillFinishes()
    {
        var whole = await ProcessRunner.RunForBytesAsync(media.Tools.Ffmpeg, Gray);
        var chunks = await ProcessRunner.RunForChunksAsync(media.Tools.Ffmpeg, Gray, 8, 3);

        Assert.Equal(3, chunks.Count);
        Assert.Equal(whole.Take(24), chunks.SelectMany(c => c));
    }

    [Fact]
    public async Task IncompleteTailIsDropped()
    {
        var whole = await ProcessRunner.RunForBytesAsync(media.Tools.Ffmpeg, Gray);
        var chunks = await ProcessRunner.RunForChunksAsync(media.Tools.Ffmpeg, Gray, 24, 100);

        Assert.Equal(whole.Length / 24, chunks.Count);
        Assert.All(chunks, c => Assert.Equal(24, c.Length));
    }

    [Fact]
    public async Task FailureStillReportsStderr()
    {
        var error = await Assert.ThrowsAsync<FfmpegException>(() =>
            ProcessRunner.RunForChunksAsync(media.Tools.Ffmpeg, ["-v", "error", "-i", media.Output("missing.mp4"), "-f", "rawvideo", "pipe:1"], 8, 1));
        Assert.False(string.IsNullOrWhiteSpace(error.Stderr));
    }

    // 以 -re 照即時速度讀取 30 秒的來源：不終止的話要跑 30 秒。
    private static readonly string[] Slow =
        ["-hide_banner", "-v", "error", "-progress", "pipe:1", "-re", "-f", "lavfi", "-i", "color=c=gray:s=64x64:r=10:d=30", "-f", "rawvideo", "pipe:1"];

    /// <summary>執行 action 期間記下啟動的程序 ID（ProcessRunner 結束時會釋放 Process 物件，事後只能用 ID 查）。</summary>
    private static async Task<List<int>> Watching(Func<Task> action)
    {
        var started = new List<int>();
        void OnStarted(System.Diagnostics.Process process) { lock (started) started.Add(process.Id); }
        ProcessRunner.Started += OnStarted;
        try { await action(); }
        catch (Exception) { /* 例外由呼叫端檢查，這裡只關心程序 */ }
        finally { ProcessRunner.Started -= OnStarted; }
        return started;
    }

    private static bool IsRunning(int id)
    {
        try { return !System.Diagnostics.Process.GetProcessById(id).HasExited; }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException) { return false; }
    }

    private static async Task AssertGone(int id)
    {
        for (var i = 0; i < 50 && IsRunning(id); i++) await Task.Delay(100);
        Assert.False(IsRunning(id), "FFmpeg 應該已被終止");
    }

    [Fact]
    public async Task TimeoutStopsTheProcessAndReportsIt()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var thrown = false;
        var started = await Watching(async () =>
        {
            try { await ProcessRunner.RunForBytesAsync(media.Tools.Ffmpeg, Slow, timeout: TimeSpan.FromSeconds(1)); }
            catch (FfmpegTimeoutException) { thrown = true; throw; }
        });

        Assert.True(thrown);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(15), $"逾時後應該很快結束，實際 {clock.Elapsed}");
        await AssertGone(Assert.Single(started));
    }

    [Fact]
    public async Task CallersCanOverrideTheDefaultTimeouts()
    {
        // 1 毫秒連啟動程序都來不及：確認傳入的時間真的被使用，而不是固定值。
        var tight = new FfmpegTimeouts { Probe = TimeSpan.FromMilliseconds(1), Frame = TimeSpan.FromMilliseconds(1), Thumbnail = TimeSpan.FromMilliseconds(1) };
        await Assert.ThrowsAsync<FfmpegTimeoutException>(() => new MediaProbe(media.Tools, tight).ProbeAsync(media.Landscape));
        await Assert.ThrowsAsync<FfmpegTimeoutException>(() =>
            new FrameExtractor(media.Tools, tight).ExtractAsync(media.Landscape, 0, new VideoEditor.Core.Models.Size2(64, 36)));
        await Assert.ThrowsAsync<FfmpegTimeoutException>(() => new FrameExtractor(media.Tools, tight).ThumbnailAsync(media.Landscape, 0, 64));
    }

    [Fact]
    public void DefaultTimeoutsAreGenerousForSlowDisks()
    {
        Assert.True(FfmpegTimeouts.Default.Probe >= TimeSpan.FromMinutes(2));
        Assert.True(FfmpegTimeouts.Default.Frame >= TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task TimeoutIsAnFfmpegException()
    {
        // 既有的 catch (FfmpegException) 不必改就能涵蓋逾時。
        await Assert.ThrowsAsync<FfmpegTimeoutException>(() =>
            ProcessRunner.RunForChunksAsync(media.Tools.Ffmpeg, Slow, 64 * 64, 1000, timeout: TimeSpan.FromSeconds(1)));
        Assert.True(typeof(FfmpegException).IsAssignableFrom(typeof(FfmpegTimeoutException)));
    }

    [Fact]
    public async Task CallerCancellationIsNotReportedAsTimeout()
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ProcessRunner.RunForBytesAsync(media.Tools.Ffmpeg, Slow, cancel.Token, TimeSpan.FromSeconds(60)));
    }

    [Fact]
    public async Task NoTimeoutMeansNoLimit()
    {
        var bytes = await ProcessRunner.RunForBytesAsync(media.Tools.Ffmpeg, Gray, timeout: TimeSpan.FromSeconds(60));
        Assert.NotEmpty(bytes);
    }

    [Fact]
    public async Task ProcessIsKilledWhenACallbackThrows()
    {
        var started = await Watching(() => ProcessRunner.RunWithLinesAsync(media.Tools.Ffmpeg, Slow,
            _ => throw new InvalidOperationException("callback failed")));

        await AssertGone(Assert.Single(started));
    }
}
