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
    public void ChunkAudioCoversTheDisplayedTimeOfItsFrames()
    {
        // 第 30–39 格顯示在 1.0–1.333 秒；影像定位提前半格，聲音不提前。
        var chunk = ReverseGraph.Split(1, 2.5, 30, frameBytes: 100, bufferBytes: 1000)[0];
        Assert.Equal(29.5 / 30, chunk.Start, 9);
        Assert.Equal(1.0, chunk.AudioStart, 9);
        Assert.Equal(10 / 30.0, chunk.AudioLength, 9);
    }

    private static readonly MediaInfo PlanMedia = new()
    {
        Path = "a.mp4", Duration = 10, HasVideo = true, HasAudio = true, DisplayWidth = 1920, DisplayHeight = 1080,
        FrameRate = new Rational(30, 1), PixelFormat = "yuv420p",
    };

    [Fact]
    public void EachChunkIsItsOwnFfmpegRunFromLastToFirst()
    {
        var t = new TimelineModel();
        var clip = t.Append(PlanMedia, 1, 2.5);
        t.SetReversed(clip, true);
        var plan = ExportPlanner.Build(t, new OutputSettings(AspectRatio.Original, FitMode.Fill), "o.mp4",
            reverseBufferBytes: ReverseGraph.FrameBytes(PlanMedia, 1920, 1080) * 20);

        // 45 格、每段 20 格 → 三個步驟，各自一個指令、只讀一小段。
        var job = Assert.Single(plan.ReverseJobs);
        Assert.Equal(3, job.Steps.Count);
        var seeks = job.Steps.Select(step => double.Parse(step.Arguments[step.Arguments.ToList().IndexOf("-ss") + 1],
            System.Globalization.CultureInfo.InvariantCulture)).ToList();
        Assert.True(seeks[0] > seeks[1] && seeks[1] > seeks[2]);
        Assert.Equal(29.5 / 30, seeks[2], 5);
        foreach (var step in job.Steps)
        {
            var graph = step.Arguments[step.Arguments.ToList().IndexOf("-filter_complex") + 1];
            Assert.Contains("reverse[v]", graph);
            Assert.Contains("areverse[a]", graph);
            Assert.Equal(2, step.Arguments.Count(a => a == "-i"));
            Assert.StartsWith(ReverseGraph.WorkToken + "/r0-", step.Arguments[^1]);
        }
        Assert.Equal(1.5, job.Steps.Sum(step => step.Duration), 6);

        // 接合清單由後往前列出中間檔。
        var files = job.ListContent.Split('\n').Where(line => line.StartsWith("file ")).ToList();
        Assert.Equal(["file r0-00002.nut", "file r0-00001.nut", "file r0-00000.nut"], files);

        // 最後的指令只讀接合清單，不再有 reverse 濾鏡（否則整段影格又會同時留在記憶體裡）。
        var args = plan.Arguments.ToList();
        Assert.Equal(job.ListPath, args[args.IndexOf("concat") + 2]);
        Assert.DoesNotContain("reverse", args[args.IndexOf("-filter_complex") + 1]);
        Assert.Equal(1, args.Count(a => a == "-i"));
        Assert.Equal(1.5, plan.Duration, 6);
        Assert.Equal(3.0, plan.TotalWork, 6);
    }

    [Fact]
    public void ListDurationsDoNotAccumulateRoundingError()
    {
        // 29.97 fps、每段 1 格：每格 33366.67 微秒，逐段捨入會累積誤差。
        var media = PlanMedia with { FrameRate = new Rational(30000, 1001) };
        var job = ReverseGraph.Plan("r", media, 0, 3000 * 1001 / 30000.0, false, "", frameBytes: 100, bufferBytes: 100);
        var durations = job.ListContent.Split('\n').Where(line => line.StartsWith("duration "))
            .Select(line => double.Parse(line[9..], System.Globalization.CultureInfo.InvariantCulture)).ToList();
        Assert.Equal(3000, durations.Count);
        Assert.Equal(3000 * 1001 / 30000.0, durations.Sum(), 6);
    }

    [Fact]
    public void FullRangeSourceKeepsItsRangeThroughTheIntermediateFile()
    {
        var media = PlanMedia with { PixelFormat = "yuvj420p", ColorRange = "pc" };
        var job = ReverseGraph.Plan("r", media, 0, 1, false, "", 100);
        var graph = job.Steps[0].Arguments[job.Steps[0].Arguments.ToList().IndexOf("-filter_complex") + 1];
        Assert.Contains("scale=in_range=pc:out_range=pc,format=yuv420p", graph);
        Assert.Equal("setparams=range=pc,", ReverseGraph.InputFilter(media));
        Assert.Equal("", ReverseGraph.InputFilter(PlanMedia));
    }

    [Fact]
    public void VariableFrameRateSourceIsConformedBeforeReversing()
    {
        // 影格間隔不固定時，用平均影格率算的切點切不準，所以先轉成固定影格率的中間檔，再對它分段倒放。
        var media = PlanMedia with { IsVariableFrameRate = true, FrameRate = new Rational(24, 1) };
        var job = ReverseGraph.Plan("r", media, 1, 3, true, "scale=640:360", frameBytes: 100, bufferBytes: 1600);

        var conform = job.Steps[0].Arguments.ToList();
        Assert.Equal("a.mp4", conform[conform.IndexOf("-i") + 1]);
        Assert.Equal("1", conform[conform.IndexOf("-ss") + 1]);
        Assert.Equal("2", conform[conform.IndexOf("-t") + 1]);
        var graph = conform[conform.IndexOf("-filter_complex") + 1];
        Assert.Contains("scale=640:360,fps=24/1:start_time=0,tpad=stop_mode=clone:stop_duration=1,trim=end_frame=48", graph);
        Assert.DoesNotContain("reverse", graph);
        Assert.Equal(ReverseGraph.WorkToken + "/r-cfr.nut", conform[^1]);

        // 48 格、每段 16 格：三段，都讀固定影格率的中間檔，從 0 秒算起，不再套縮放。
        Assert.Equal(4, job.Steps.Count);
        foreach (var step in job.Steps.Skip(1))
        {
            var args = step.Arguments.ToList();
            Assert.Equal(ReverseGraph.WorkToken + "/r-cfr.nut", args[args.IndexOf("-i") + 1]);
            Assert.DoesNotContain("scale=640:360", args[args.IndexOf("-filter_complex") + 1]);
        }
        var last = job.Steps[^1].Arguments.ToList();
        Assert.Equal("0", last[last.IndexOf("-ss") + 1]);
        // 進度包含轉檔那一步：2 秒轉檔加 2 秒倒放。
        Assert.Equal(4, job.Duration, 6);
    }

    [Fact]
    public void ConstantFrameRateSourceIsReversedDirectly()
    {
        var job = ReverseGraph.Plan("r", PlanMedia, 1, 3, true, "", frameBytes: 100, bufferBytes: 100_000);
        var step = Assert.Single(job.Steps);
        Assert.Equal("a.mp4", step.Arguments[step.Arguments.ToList().IndexOf("-i") + 1]);
        Assert.Equal(2, job.Duration, 6);
    }

    [Fact]
    public void WorkTokenIsReplacedByTheDirectory()
    {
        var resolved = ReverseGraph.Resolve(["-i", "a.mp4", ReverseGraph.WorkToken + "/r0.ffconcat"], Path.Combine("x", "work")).ToList();
        Assert.Equal(["-i", "a.mp4", Path.Combine("x", "work", "r0.ffconcat")], resolved);
    }

    [Fact]
    public void DiskEstimateIncludesReverseIntermediateFiles()
    {
        var t = new TimelineModel();
        var clip = t.Append(PlanMedia with { BitRate = 8_000_000 }, 0, 2);
        var forward = ExportService.EstimateBytes(t);
        t.SetReversed(clip, true);
        // 60 格 1080p（解碼後每格約 3 MB）：中間檔遠大於輸出檔本身。
        Assert.True(ExportService.EstimateBytes(t) - forward > 60L * 1920 * 1080 / 2);
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
    public async Task ReversedExportMemoryDoesNotGrowWithClipLength()
    {
        var source = await media.Probe(media.Landscape);
        var settings = new OutputSettings(AspectRatio.Original, FitMode.Fill);
        // 每小段 10 格（約 31 MB）；整段 4 秒 120 格，全部留在記憶體裡約 370 MB。
        var buffer = ReverseGraph.FrameBytes(source, source.DisplayWidth, source.DisplayHeight) * 10;

        async Task<long> Peak(string name, bool reversed)
        {
            var t = new TimelineModel();
            t.SetReversed(t.Append(source, 0, 4), reversed);
            var directory = Directory.CreateDirectory(Path.Combine(media.Directory, name)).FullName;
            var output = Path.Combine(directory, "out.mp4");
            var plan = ExportPlanner.Build(t, settings, output, buffer);
            return await TestMedia.PeakMemory(name, () => new ExportService(media.Tools).ExportAsync(plan, output, null));
        }

        var forward = await Peak("memory-forward", false);
        var reversed = await Peak("memory-reversed", true);
        // 倒放只比正放多出一小段的緩衝，而不是整段影格。
        Assert.True(forward > 0 && reversed > 0, "沒有量到 FFmpeg 的記憶體用量");
        Assert.True(reversed < forward + 150L * 1024 * 1024,
            $"倒放匯出的記憶體峰值 {reversed / 1048576} MB，正放 {forward / 1048576} MB");
    }

    [Fact]
    public async Task ReversedExportCleansUpItsIntermediateFiles()
    {
        var source = await media.Probe(media.Landscape);
        var t = new TimelineModel();
        t.SetReversed(t.Append(source, 0, 1), true);
        var directory = Directory.CreateDirectory(Path.Combine(media.Directory, "reverse-cleanup")).FullName;
        var output = Path.Combine(directory, "out.mp4");
        var plan = ExportPlanner.Build(t, new OutputSettings(AspectRatio.Original, FitMode.Fill), output,
            ReverseGraph.FrameBytes(source, source.DisplayWidth, source.DisplayHeight) * 10);

        // 進度涵蓋倒放與編碼兩個階段，全程遞增。
        var reports = new List<double>();
        await new ExportService(media.Tools).ExportAsync(plan, output, new SyncProgressValue(reports.Add));
        Assert.Equal(reports.Order(), reports);
        Assert.Equal(1, reports[^1]);
        Assert.Contains(reports, value => value is > 0.05 and < 0.5);
        Assert.Equal([output], Directory.GetFileSystemEntries(directory));

        // 取消：停在倒放階段也不留下任何檔案。
        using var cancel = new CancellationTokenSource();
        var cancelled = new ExportService(media.Tools).ExportAsync(plan, Path.Combine(directory, "cancelled.mp4"),
            new SyncProgressValue(_ => cancel.Cancel()), cancel.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        Assert.Equal([output], Directory.GetFileSystemEntries(directory));
    }

    [Fact]
    public async Task ReversedExportKeepsSourceTimestamps()
    {
        // 29.97 fps：每格 1001/30000 秒，中間檔與接合都不能讓時間戳記走樣。
        var source = await media.Probe(media.MakeClip("ntsc.mp4", "testsrc2=s=640x360:r=30000/1001:d=2", "sine=f=440:d=2", "-pix_fmt", "yuv420p"));
        var t = new TimelineModel();
        t.SetReversed(t.Append(source, 0, 40 * 1001 / 30000.0), true);
        var output = media.Output("ntsc-reversed.mp4");
        var plan = ExportPlanner.Build(t, new OutputSettings(AspectRatio.Original, FitMode.Fill), output,
            ReverseGraph.FrameBytes(source, source.DisplayWidth, source.DisplayHeight) * 7);
        await new ExportService(media.Tools).ExportAsync(plan, output, null);

        var result = await media.Probe(output);
        Assert.Equal(new Rational(30000, 1001), result.FrameRate);
        Assert.False(result.IsVariableFrameRate);
        var json = await ProcessRunner.RunForBytesAsync(media.Tools.Ffprobe,
            ["-v", "error", "-select_streams", "v:0", "-show_entries", "packet=pts_time", "-of", "csv=p=0", output]);
        var times = System.Text.Encoding.ASCII.GetString(json).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => double.Parse(line.TrimEnd(','), System.Globalization.CultureInfo.InvariantCulture)).Order().ToList();
        Assert.Equal(40, times.Count);
        for (var i = 0; i < times.Count; i++) Assert.Equal(times[0] + i * 1001 / 30000.0, times[i], 5);
    }

    [Theory]
    [InlineData("white", 255)]
    [InlineData("black", 0)]
    public async Task ReversedFullRangeSourceKeepsItsPixelValues(string color, int expected)
    {
        var source = await media.Probe(media.MakeSolid($"reverse-full-{color}.mp4", color, "yuvj420p"));
        var t = new TimelineModel();
        t.SetReversed(t.Append(source, 0, 0.5), true);
        var result = await media.Export(t, new OutputSettings(AspectRatio.R16x9, FitMode.Fill), $"reverse-full-{color}-out.mp4");
        Assert.Equal("pc", result.ColorRange);
        Assert.Equal((expected, expected), await media.LumaRange(result.Path, 0.1));
    }

    [Theory]
    [InlineData("white", 255)]
    [InlineData("black", 0)]
    public async Task IntermediateFileKeepsFullRangePixelValues(string color, int expected)
    {
        // 直接檢查中間檔：全範圍來源的像素值在倒放這一步就不能被轉成 limited（235／16）。
        var source = await media.Probe(media.MakeSolid($"intermediate-{color}.mp4", color, "yuvj420p"));
        var job = ReverseGraph.Plan("r", source, 0, 0.5, false, "", ReverseGraph.FrameBytes(source, source.DisplayWidth, source.DisplayHeight) * 100);
        var work = Directory.CreateDirectory(Path.Combine(media.Directory, $"intermediate-{color}")).FullName;
        await ReverseGraph.RunAsync(media.Tools, job, work, null, CancellationToken.None);

        var chunk = Assert.Single(Directory.GetFiles(work, "*.nut"));
        var info = await media.Probe(chunk);
        var luma = await media.LumaRange(chunk, 0.1);
        Assert.True(luma == (expected, expected),
            $"中間檔的 Y 值是 {luma}，應為 {expected}（像素格式 {info.PixelFormat}，色彩範圍標記 {info.ColorRange ?? "無"}）");
    }

    [Theory]
    [InlineData(1000)]
    [InlineData(7)]
    public async Task VariableFrameRateSourceReversesWithoutDuplicateOrMissingFrames(int framesPerChunk)
    {
        // 前 1 秒 30 fps、後 1 秒 15 fps。
        var source = await media.Probe(media.MakeClip("vfr.mp4", "testsrc2=s=640x360:r=30:d=2", "sine=f=440:d=2",
            "-vf", "select='lt(t,1)+not(mod(n,2))'", "-fps_mode", "vfr", "-pix_fmt", "yuv420p"));
        Assert.True(source.IsVariableFrameRate);
        var t = new TimelineModel();
        t.SetReversed(t.Append(source, 0, 2), true);
        var output = media.Output($"vfr-reversed-{framesPerChunk}.mp4");
        var plan = ExportPlanner.Build(t, new OutputSettings(AspectRatio.Original, FitMode.Fill), output,
            ReverseGraph.FrameBytes(source, source.DisplayWidth, source.DisplayHeight) * framesPerChunk);
        await new ExportService(media.Tools).ExportAsync(plan, output, null);

        var bytes = await ProcessRunner.RunForBytesAsync(media.Tools.Ffprobe,
            ["-v", "error", "-select_streams", "v:0", "-show_entries", "packet=pts_time", "-of", "csv=p=0", output]);
        var times = System.Text.Encoding.ASCII.GetString(bytes).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => double.Parse(line.TrimEnd(','), System.Globalization.CultureInfo.InvariantCulture)).Order().ToList();
        var steps = times.Zip(times.Skip(1), (a, b) => b - a).ToList();
        // 固定影格率：每格間隔相同，沒有重複的時間戳記，格數等於片段長度 × 影格率。
        var fps = source.FrameRate.Value;
        Assert.Equal((int)Math.Round(2 * fps), times.Count);
        Assert.All(steps, step => Assert.Equal(1 / fps, step, 3));

        // 倒放後開頭是來源的結尾（慢的那一段），結尾是來源的開頭。
        async Task<byte[]> At(string path, double time) => await ProcessRunner.RunForBytesAsync(media.Tools.Ffmpeg,
            ["-v", "error", "-ss", time.ToString(System.Globalization.CultureInfo.InvariantCulture), "-i", path, "-vf", "scale=64:36", "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "gray", "pipe:1"]);
        var first = await At(output, 0.1);
        Assert.True(Difference(first, await At(source.Path, 1.85)) < Difference(first, await At(source.Path, 0.15)));
        var last = await At(output, 1.8);
        Assert.True(Difference(last, await At(source.Path, 0.15)) < Difference(last, await At(source.Path, 1.85)));
    }

    [Fact]
    public async Task ReversedClipWhoseLastChunkHasNoPictureStillExports()
    {
        // 畫面 2 秒（60 格）、聲音 2.1 秒：整段加入時片段是 63 格，最後 3 格只有聲音。
        // 每段 20 格 → 最後一小段（第 60–62 格）完全沒有畫面。
        var source = await media.Probe(media.MakeClip("tail-audio.mp4", "testsrc2=s=640x360:r=30:d=2", "sine=f=440:d=2.1", "-pix_fmt", "yuv420p"));
        var t = new TimelineModel();
        var clip = new TimelineEditor(t).Add(source, new InOutPoints()).Clip!;
        t.SetReversed(clip, true);
        Assert.Equal(63, (int)Math.Round(clip.Duration * 30));
        var output = media.Output("tail-audio-reversed.mp4");
        var plan = ExportPlanner.Build(t, new OutputSettings(AspectRatio.Original, FitMode.Fill), output,
            ReverseGraph.FrameBytes(source, source.DisplayWidth, source.DisplayHeight) * 20);
        await new ExportService(media.Tools).ExportAsync(plan, output, null);

        var result = await media.Probe(output);
        Assert.InRange(await media.CountFrames(output), 60, 63);
        Assert.Equal(2.1, result.Duration, 1);
    }

    [Fact]
    public async Task ReversedExportWorksInAFolderWithNonAsciiNameSpacesAndQuotes()
    {
        // 預設檔名「輸出.mp4」就含中文；倒放的中間檔與接合清單都放在輸出檔旁邊。
        var source = await media.Probe(media.Landscape);
        var directory = Directory.CreateDirectory(Path.Combine(media.Directory, "輸出 資料夾's (1)")).FullName;
        var output = Path.Combine(directory, "輸出 影片's.mp4");
        var t = new TimelineModel();
        t.SetReversed(t.Append(source, 0, 1), true);
        var plan = ExportPlanner.Build(t, new OutputSettings(AspectRatio.Original, FitMode.Fill), output,
            ReverseGraph.FrameBytes(source, source.DisplayWidth, source.DisplayHeight) * 10);
        await new ExportService(media.Tools).ExportAsync(plan, output, null);

        Assert.Equal(30, await media.CountFrames(output));
        Assert.Equal([output], Directory.GetFileSystemEntries(directory));
    }

    [Fact]
    public async Task ReversedAudioIsMirroredAcrossChunks()
    {
        // 來源只有 0.5–1.0 秒有聲音；2 秒的片段倒放後，聲音應該在 1.0–1.5 秒。
        var source = await media.Probe(media.MakeClip("beep.mp4", "testsrc2=s=640x360:r=30:d=2",
            "sine=f=440:d=2,volume=enable='not(between(t,0.5,1))':volume=0", "-pix_fmt", "yuv420p"));
        var t = new TimelineModel();
        t.SetReversed(t.Append(source, 0, 2), true);
        var output = media.Output("beep-reversed.mp4");
        var plan = ExportPlanner.Build(t, new OutputSettings(AspectRatio.Original, FitMode.Fill), output,
            ReverseGraph.FrameBytes(source, source.DisplayWidth, source.DisplayHeight) * 7);
        await new ExportService(media.Tools).ExportAsync(plan, output, null);

        Assert.True(await media.MeanVolume(output, 1.05, 0.4) > -40);
        Assert.True(await media.MeanVolume(output, 0.1, 0.8) < -60);
        Assert.True(await media.MeanVolume(output, 1.6, 0.35) < -60);
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
    public async Task StartupCleanupRemovesOnlyFoldersNobodyUses()
    {
        var root = Path.Combine(media.Directory, "cleanup-root");
        // 上次當掉時留下的資料夾。
        var stale = Directory.CreateDirectory(Path.Combine(root, "reverse-stale")).FullName;
        await File.WriteAllTextAsync(Path.Combine(stale, "old.mp4"), "x");
        await File.WriteAllTextAsync(Path.Combine(stale, ".in-use"), ""); // 沒有人開著
        // 別的資料夾不碰。
        var other = Directory.CreateDirectory(Path.Combine(root, "something-else")).FullName;

        // 另一個正在執行的程式：已產生過預覽檔，使用中標記一直開著。
        var source = await media.Probe(media.Landscape);
        var live = Path.Combine(root, "reverse-live");
        var cache = new ReversePreviewCache(media.Tools, live);
        var proxy = await cache.GetAsync(source, 0, 0.5, new Size2(320, 180));
        try
        {
            Assert.Equal(1, ReversePreviewCache.CleanupStale(root));
            Assert.False(Directory.Exists(stale));
            Assert.True(Directory.Exists(other));
            Assert.True(File.Exists(proxy)); // 使用中的預覽檔還在
        }
        finally
        {
            cache.Dispose();
        }
        Assert.False(Directory.Exists(live));
        Assert.Equal(0, ReversePreviewCache.CleanupStale(Path.Combine(media.Directory, "cleanup-no-root")));
    }

    [Fact]
    public async Task SyncStopsAndDeletesPreviewFilesThatAreNoLongerNeeded()
    {
        var source = await media.Probe(media.Landscape);
        var directory = Path.Combine(media.Directory, "sync");
        var size = new Size2(320, 180);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cache = new ReversePreviewCache(media.Tools, directory, new SyncProgress(value =>
        {
            if (value != null) started.TrySetResult();
        }));

        // 修剪前的範圍：正在產生，另一個還在排隊。
        var old = cache.GetAsync(source, 0, 4, size);
        var queued = cache.GetAsync(source, 0, 3.5, size);
        await started.Task;

        // 修剪後：只需要新範圍。舊的兩個都停掉，新的不必等到播放就開始產生。
        cache.Sync([new ReversePreviewRequest(source, 1, 2, size)]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        try { await old; }
        catch (OperationCanceledException) { } // 也可能在中止前剛好完成，那個檔案會被刪掉
        var current = await cache.GetAsync(source, 1, 2, size);
        Assert.Equal(30, await media.CountFrames(current));
        await WaitUntil(() => PreviewFiles(directory).SequenceEqual([current]));

        // 同一個範圍再同步一次：沿用，不重做。
        cache.Sync([new ReversePreviewRequest(source, 1, 2, size)]);
        Assert.Same(current, await cache.GetAsync(source, 1, 2, size));
        Assert.True(File.Exists(current));

        // 取消倒放或刪除片段：已做好的預覽檔也刪掉。
        cache.Sync([]);
        await WaitUntil(() => PreviewFiles(directory).Count == 0);
    }

    /// <summary>資料夾裡的預覽檔與中間檔（不含使用中標記）。</summary>
    private static List<string> PreviewFiles(string directory) =>
        Directory.GetFileSystemEntries(directory).Where(path => Path.GetFileName(path) != ".in-use").ToList();

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(50);
        Assert.True(condition());
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

internal sealed class SyncProgressValue(Action<double> report) : IProgress<double>
{
    public void Report(double value) => report(value);
}
