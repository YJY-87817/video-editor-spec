using VideoEditor.Core.Export;
using VideoEditor.Core.Ffmpeg;
using VideoEditor.Core.Models;
using VideoEditor.Core.Timeline;
using TimelineModel = VideoEditor.Core.Timeline.Timeline;

namespace VideoEditor.Tests;

public class ReverseTimelineTests
{
    private static MediaInfo Media(double duration = 10) => new()
    {
        Path = "a.mp4", Duration = duration, HasVideo = true, HasAudio = true,
        DisplayWidth = 1920, DisplayHeight = 1080, FrameRate = new Rational(30, 1), PixelFormat = "yuv420p",
    };

    [Fact]
    public void ReversedClipStartsAtLastSourceFrame()
    {
        var t = new TimelineModel();
        var clip = t.Append(Media(), 1, 2.5);
        t.SetReversed(clip, true);

        // 片段第一格是來源第 74 格（2.5 秒前一格），最後一格是第 30 格。
        Assert.Equal(74, FrameExtractor.FrameIndex(clip.ToSourceTime(0), 30));
        Assert.Equal(30, FrameExtractor.FrameIndex(clip.ToSourceTime(44 / 30.0), 30));
        Assert.Equal(1, clip.ToSourceTime(clip.End));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TimelineTimeIsInverseOfSourceTime(bool reversed)
    {
        var t = new TimelineModel();
        t.Append(Media(), 0, 1);
        var clip = t.Append(Media(), 3, 6);
        t.SetReversed(clip, reversed);
        foreach (var time in new[] { 1.0, 1.5, 3.2 })
            Assert.Equal(time, clip.ToTimelineTime(clip.ToSourceTime(time)), 6);
    }

    [Fact]
    public void ReversingKeepsPositionAndLength()
    {
        var t = new TimelineModel();
        var clip = t.Append(Media(), 2, 5);
        t.SetReversed(clip, true);
        Assert.Equal((0.0, 3.0, 2.0, 5.0), (clip.Start, clip.Duration, clip.SourceIn, clip.SourceOut));
        t.SetReversed(clip, false);
        Assert.False(clip.Reversed);
    }

    [Fact]
    public void TrimmingReversedClipEditsTheMatchingSourceEnd()
    {
        var t = new TimelineModel();
        var clip = t.Append(Media(), 2, 5);
        t.SetReversed(clip, true);

        // 左緣是倒放的開頭，也就是來源的結尾。
        t.TrimStart(clip, 1);
        Assert.Equal((1.0, 2.0, 4.0), (clip.Start, clip.SourceIn, clip.SourceOut));

        // 右緣是倒放的結尾，也就是來源的開頭；往右拉最多拉到來源 0 秒。
        t.TrimEnd(clip, 100);
        Assert.Equal((0.0, 4.0, 5.0), (clip.SourceIn, clip.SourceOut, clip.End));

        // 左緣往左拉，最多拉到來源結尾（10 秒）或時間軸 0 秒。
        t.TrimStart(clip, -100);
        Assert.Equal((0.0, 0.0, 5.0), (clip.Start, clip.SourceIn, clip.SourceOut));
    }

    [Fact]
    public void MusicClipsCannotBeReversed()
    {
        var t = new TimelineModel();
        var music = t.Append(new MediaInfo { Path = "m.m4a", Duration = 5, HasAudio = true }, 0, 5);
        Assert.Throws<ArgumentException>(() => t.SetReversed(music, true));
    }
}

public class ReverseGraphTests
{
    [Fact]
    public void ShortRangeIsOneChunk()
    {
        var chunk = Assert.Single(ReverseGraph.Split(1, 2.5, 30, 1000));
        // 第 30 格到第 74 格，前後各留半格。
        Assert.Equal(29.5 / 30, chunk.Start, 9);
        Assert.Equal(45 / 30.0, chunk.Length, 9);
    }

    [Fact]
    public void ChunksAreContiguousAndSplitBetweenFrames()
    {
        // 每段最多 10 格：30–39、40–49、50–59、60–69、70–74。
        var chunks = ReverseGraph.Split(1, 2.5, 30, frameBytes: 100, bufferBytes: 1000);
        Assert.Equal(5, chunks.Count);
        Assert.Equal(74.5 / 30, chunks[^1].Start + chunks[^1].Length, 9);
        for (var i = 0; i < chunks.Count; i++)
            Assert.Equal((30 + i * 10 - 0.5) / 30, chunks[i].Start, 9);
    }

    [Fact]
    public void UnalignedOutPointKeepsTheFrameBeforeIt()
    {
        // 出點 2.51 秒：第 75 格（2.5 秒）在出點之前，要包含。
        var chunk = Assert.Single(ReverseGraph.Split(0, 2.51, 30, 1000));
        Assert.Equal(75.5 / 30, chunk.Start + chunk.Length, 9);
        Assert.Equal(0, chunk.Start);
    }

    [Fact]
    public void FrameBytesFollowsChromaAndDepth()
    {
        var media = new MediaInfo { Path = "a", PixelFormat = "yuv420p" };
        Assert.Equal(1920 * 1080 * 3 / 2, ReverseGraph.FrameBytes(media, 1920, 1080));
        Assert.Equal(1920 * 1080 * 6, ReverseGraph.FrameBytes(media with { PixelFormat = "yuv444p10le", BitDepth = 10 }, 1920, 1080));
    }

    [Fact]
    public void ExportPlanReversesChunksFromLastToFirst()
    {
        var media = new MediaInfo
        {
            Path = "a.mp4", Duration = 10, HasVideo = true, HasAudio = true, DisplayWidth = 1920, DisplayHeight = 1080,
            FrameRate = new Rational(30, 1), PixelFormat = "yuv420p",
        };
        var t = new TimelineModel();
        var clip = t.Append(media, 1, 2.5);
        t.SetReversed(clip, true);
        var plan = ExportPlanner.Build(t, new OutputSettings(AspectRatio.Original, FitMode.Fill), "o.mp4",
            reverseBufferBytes: ReverseGraph.FrameBytes(media, 1920, 1080) * 20);

        var args = plan.Arguments.ToList();
        var seeks = args.Select((a, i) => (a, i)).Where(x => x.a == "-ss").Select(x => double.Parse(args[x.i + 1], System.Globalization.CultureInfo.InvariantCulture)).ToList();
        Assert.Equal(3, seeks.Count);
        Assert.True(seeks[0] > seeks[1] && seeks[1] > seeks[2]);
        Assert.Equal(29.5 / 30, seeks[2], 5);

        var graph = args[args.IndexOf("-filter_complex") + 1];
        Assert.Equal(3, graph.Split("reverse[").Length - 1 - (graph.Split("areverse[").Length - 1));
        Assert.Contains("areverse", graph);
        Assert.Contains("concat=n=3:v=1:a=1[r0v][r0a]", graph);
        Assert.Equal(1.5, plan.Duration, 6);
    }
}

/// <summary>實際倒放匯出，逐格比對來源。</summary>
[Collection("media")]
public class ReverseIntegrationTests(TestMedia media)
{
    private async Task<byte[]> Frame(string path, int index) => await ProcessRunner.RunForBytesAsync(media.Tools.Ffmpeg,
        ["-v", "error", "-i", path, "-vf", $"select=eq(n\\,{index}),scale=64:36", "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "gray", "pipe:1"]);

    private static long Difference(byte[] a, byte[] b) => a.Zip(b, (x, y) => (long)Math.Abs(x - y)).Sum();

    /// <summary>輸出第 outputIndex 格應該是來源第 sourceIndex 格，而不是它的前後格。</summary>
    private async Task AssertFrameMatches(string output, int outputIndex, int sourceIndex)
    {
        var actual = await Frame(output, outputIndex);
        var expected = await Frame(media.Landscape, sourceIndex);
        var before = await Frame(media.Landscape, sourceIndex - 1);
        var after = await Frame(media.Landscape, sourceIndex + 1);
        var d = Difference(actual, expected);
        Assert.True(d < Difference(actual, before) && d < Difference(actual, after),
            $"輸出第 {outputIndex} 格不是來源第 {sourceIndex} 格");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    public async Task ReversedExportPlaysEveryFrameBackwards(int framesPerChunk)
    {
        var source = await media.Probe(media.Landscape);
        var t = new TimelineModel();
        var clip = t.Append(source, 1, 2.5);
        t.SetReversed(clip, true);
        var output = media.Output($"reversed-{framesPerChunk}.mp4");
        var plan = ExportPlanner.Build(t, new OutputSettings(AspectRatio.Original, FitMode.Fill, Crf: 0), output,
            ReverseGraph.FrameBytes(source, source.DisplayWidth, source.DisplayHeight) * framesPerChunk);
        await new ExportService(media.Tools).ExportAsync(plan, output, null);
        var result = await media.Probe(output);

        Assert.Equal(45, await media.CountFrames(output));
        Assert.Equal(1.5, result.Duration, 1);
        Assert.Equal("aac", result.AudioCodec);
        // 頭、尾，以及每段 10 格時的切點前後。
        foreach (var k in new[] { 0, 9, 10, 11, 44 })
            await AssertFrameMatches(output, k, 74 - k);
    }

    [Fact]
    public async Task ReversedClipJoinsWithForwardClips()
    {
        var source = await media.Probe(media.Landscape);
        var t = new TimelineModel();
        t.Append(source, 0, 1);
        t.SetReversed(t.Append(source, 0, 1), true);
        var output = media.Output("forward-reverse.mp4");
        var plan = ExportPlanner.Build(t, new OutputSettings(AspectRatio.Original, FitMode.Fill, Crf: 0), output);
        await new ExportService(media.Tools).ExportAsync(plan, output, null);

        Assert.Equal(60, await media.CountFrames(output));
        await AssertFrameMatches(output, 29, 29);
        await AssertFrameMatches(output, 30, 29);
        await AssertFrameMatches(output, 58, 1);
    }

    [Fact]
    public async Task ReversePreviewFileStartsAtLastFrame()
    {
        var source = await media.Probe(media.Landscape);
        var reports = new List<double?>();
        using var cache = new ReversePreviewCache(media.Tools, Path.Combine(media.Directory, "proxy"), new SyncProgress(reports.Add));
        var path = await cache.GetAsync(source, 1, 2.5, new Size2(640, 360));
        // 從 0 開始、過程遞增、最後以 null 表示結束。
        Assert.Equal(0, reports[0]);
        Assert.Null(reports[^1]);
        Assert.True(reports.Count >= 3);
        var values = reports.Take(reports.Count - 1).Select(v => v!.Value).ToList();
        Assert.Equal(values.Order(), values);
        Assert.True(values[^1] > 0.5);
        Assert.Same(cache.GetAsync(source, 1, 2.5, new Size2(640, 360)), cache.GetAsync(source, 1, 2.5, new Size2(640, 360)));

        var info = await media.Probe(path);
        Assert.Equal((640, 360), (info.DisplayWidth, info.DisplayHeight));
        Assert.Equal(1.5, info.Duration, 1);
        Assert.True(info.HasAudio);
        Assert.Equal(45, await media.CountFrames(path));
        Assert.True(Difference(await Frame(path, 0), await Frame(media.Landscape, 74))
            < Difference(await Frame(path, 0), await Frame(media.Landscape, 30)));
    }

    [Fact]
    public async Task RangeExtractionMatchesExactFrames()
    {
        var extractor = new FrameExtractor(media.Tools);
        var size = new Size2(64, 36);
        var frames = await extractor.ExtractRangeAsync(media.Landscape, 40, 4, 3, 30, size);
        Assert.Equal(4, frames.Count);
        for (var i = 0; i < frames.Count; i++)
        {
            var exact = await ProcessRunner.RunForBytesAsync(media.Tools.Ffmpeg,
            [
                "-v", "error", "-i", media.Landscape, "-vf", $"select=eq(n\\,{40 + i * 3}),scale=64:36,setsar=1",
                "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "bgra", "pipe:1",
            ]);
            Assert.Equal(exact, frames[i].Bgra);
        }
    }
}

/// <summary>同步回報的 IProgress，測試時不必等待 SynchronizationContext。</summary>
/// <summary>關閉時刪除倒放預覽的暫存資料夾。</summary>
[Collection("media")]
public class ReversePreviewCleanupTests(TestMedia media)
{
    [Fact]
    public async Task FileStillInUseIsDeletedOnceReleased()
    {
        var directory = Path.Combine(media.Directory, "cleanup-busy");
        Directory.CreateDirectory(directory);
        // 模擬剛被停止的 FFmpeg 還佔著檔案：不允許其他人刪除，0.3 秒後才放開。
        var busy = new FileStream(Path.Combine(directory, "busy.mp4"), FileMode.Create, FileAccess.Write, FileShare.None);
        var release = Task.Run(async () =>
        {
            await Task.Delay(300);
            busy.Dispose();
        });

        new ReversePreviewCache(media.Tools, directory).Dispose();

        await release;
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public void MissingDirectoryIsFine() =>
        new ReversePreviewCache(media.Tools, Path.Combine(media.Directory, "cleanup-never-created")).Dispose();

    [Fact]
    public async Task DisposeDuringRenderStopsFfmpegAndLeavesNothing()
    {
        var source = await media.Probe(media.Landscape);
        var directory = Path.Combine(media.Directory, "cleanup-rendering");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cache = new ReversePreviewCache(media.Tools, directory, new SyncProgress(value =>
        {
            if (value != null) started.TrySetResult();
        }));

        var render = cache.GetAsync(source, 0, 4, new Size2(640, 360));
        await started.Task;
        cache.Dispose();

        // 產生被中止（或在中止前剛好完成），兩者都不能留下暫存資料夾。
        try { await render; }
        catch (OperationCanceledException) { }
        Assert.False(Directory.Exists(directory));
    }
}

internal sealed class SyncProgress(Action<double?> report) : IProgress<double?>
{
    public void Report(double? value) => report(value);
}
