using VideoEditor.Core.Export;
using VideoEditor.Core.Ffmpeg;
using VideoEditor.Core.Models;
using VideoEditor.Core.Timeline;
using TimelineModel = VideoEditor.Core.Timeline.Timeline;

namespace VideoEditor.Tests;

/// <summary>實際呼叫 FFmpeg，並用 ffprobe 驗證輸出結果。</summary>
[Collection("media")]
public class ExportIntegrationTests(TestMedia media)
{
    private static readonly OutputSettings Default = new(AspectRatio.R16x9, FitMode.Fill);

    [Fact]
    public async Task ProbeReadsSourceInformation()
    {
        var info = await media.Probe(media.Landscape);
        Assert.Equal((1920, 1080), (info.DisplayWidth, info.DisplayHeight));
        Assert.Equal(30, info.FrameRate.Value);
        Assert.Equal("h264", info.VideoCodec);
        Assert.Equal("16:9", info.AspectLabel);
        Assert.True(info.BitRate > 0);
        Assert.Equal(4, info.Duration, 1);
        Assert.False(info.IsVariableFrameRate);

        var rotated = await media.Probe(media.Rotated);
        Assert.Equal((1080, 1920), (rotated.DisplayWidth, rotated.DisplayHeight));
    }

    [Fact]
    public async Task ThumbnailIsJpeg()
    {
        var bytes = await new FrameExtractor(media.Tools).ThumbnailAsync(media.Landscape, 1, 160);
        Assert.Equal(0xFF, bytes[0]);
        Assert.Equal(0xD8, bytes[1]);
    }

    [Fact]
    public async Task FrameExtractionMatchesExactFrame()
    {
        var extractor = new FrameExtractor(media.Tools);
        var size = new Size2(64, 36);
        var frame = await extractor.ExtractAsync(media.Landscape, FrameExtractor.FrameSeekTime(45, 30), size);
        var exact = await ProcessRunner.RunForBytesAsync(media.Tools.Ffmpeg,
        [
            "-v", "error", "-i", media.Landscape, "-vf", "select=eq(n\\,45),scale=64:36,setsar=1",
            "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "bgra", "pipe:1",
        ]);
        Assert.NotNull(frame);
        Assert.Equal(exact, frame.Bgra);
    }

    [Fact]
    public async Task SingleClipCutIsAccurate()
    {
        var t = new TimelineModel();
        t.Append(await media.Probe(media.Landscape), 1, 2.5);
        var result = await media.Export(t, Default, "single.mp4");
        Assert.Equal(45, await media.CountFrames(result.Path));
        Assert.Equal(1.5, result.Duration, 1);
        Assert.Equal("h264", result.VideoCodec);
        Assert.Equal("aac", result.AudioCodec);
        Assert.Equal(30, result.FrameRate.Value);
    }

    [Fact]
    public async Task CutStartsAtTheExactFrame()
    {
        var t = new TimelineModel();
        t.Append(await media.Probe(media.Landscape), 1, 1.5);
        var result = await media.Export(t, new OutputSettings(AspectRatio.R16x9, FitMode.Fill, Crf: 0), "cut.mp4");

        async Task<byte[]> FirstFrame(string path, string filter) => await ProcessRunner.RunForBytesAsync(media.Tools.Ffmpeg,
            ["-v", "error", "-i", path, "-vf", filter + "scale=64:36", "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "gray", "pipe:1"]);

        var exported = await FirstFrame(result.Path, "");
        var expected = await FirstFrame(media.Landscape, "select=eq(n\\,30),");
        var previous = await FirstFrame(media.Landscape, "select=eq(n\\,29),");
        static long Difference(byte[] a, byte[] b) => a.Zip(b, (x, y) => (long)Math.Abs(x - y)).Sum();
        Assert.True(Difference(exported, expected) < Difference(exported, previous));
    }

    [Fact]
    public async Task MultipleSegmentsFromSameFileAreJoined()
    {
        var source = await media.Probe(media.Landscape);
        var t = new TimelineModel();
        t.Append(source, 0, 1);
        t.Append(source, 2, 3);
        t.Append(source, 1, 1.5);
        var result = await media.Export(t, Default, "multi.mp4");
        Assert.Equal(75, await media.CountFrames(result.Path));
    }

    [Fact]
    public async Task TrimmedJoinHasNoSilenceAndKeepsTimelineLength()
    {
        var source = await media.Probe(media.Landscape);
        var t = new TimelineModel();
        var a = t.Append(source, 0, 2);
        var b = t.Append(source, 2, 3);
        t.TrimEnd(a, 1.015); // 拖曳邊緣：30.45 格
        t.Move(b, a.End);
        var result = await media.Export(t, Default, "trimmed-join.mp4");

        // 畫面格數與時間軸長度一致，後面的片段沒有被延後。
        Assert.Equal((int)Math.Round(t.Duration * 30), await media.CountFrames(result.Path));
        // 接點之後馬上是下一段的聲音，不是補上的靜音。
        Assert.True(await media.MeanVolume(result.Path, a.End, 0.015) > -40);
    }

    [Fact]
    public async Task MixedSourcesAreConformedToFirstClip()
    {
        var t = new TimelineModel();
        t.Append(await media.Probe(media.Landscape), 0, 1);
        t.Append(await media.Probe(media.NoAudio), 0, 1);
        t.Append(await media.Probe(media.Portrait), 0, 1);
        var result = await media.Export(t, new OutputSettings(AspectRatio.R16x9, FitMode.Pad), "mixed.mp4");
        Assert.Equal((1920, 1080), (result.DisplayWidth, result.DisplayHeight));
        Assert.Equal(30, result.FrameRate.Value);
        Assert.True(result.HasAudio);
        Assert.Equal(3, result.Duration, 0);
        Assert.Equal(90, await media.CountFrames(result.Path));
    }

    [Fact]
    public async Task NoAudioSourceStaysSilentOutput()
    {
        var t = new TimelineModel();
        t.Append(await media.Probe(media.NoAudio), 0, 1);
        var result = await media.Export(t, Default, "noaudio-out.mp4");
        Assert.False(result.HasAudio);
    }

    public static TheoryData<int, int, FitMode, int, int> RatioCases() => new()
    {
        { 16, 9, FitMode.Fill, 1920, 1080 }, { 16, 9, FitMode.Pad, 1920, 1080 },
        { 9, 16, FitMode.Fill, 1080, 1920 }, { 9, 16, FitMode.Pad, 1080, 1920 },
        { 1, 1, FitMode.Fill, 1920, 1920 }, { 1, 1, FitMode.Pad, 1920, 1920 },
        { 4, 5, FitMode.Fill, 1536, 1920 }, { 4, 5, FitMode.Pad, 1536, 1920 },
    };

    [Theory]
    [MemberData(nameof(RatioCases))]
    public async Task RatiosAndModesProduceExpectedOutput(int rw, int rh, FitMode mode, int w, int h)
    {
        var t = new TimelineModel();
        t.Append(await media.Probe(media.Landscape), 0, 0.5);
        var result = await media.Export(t, new OutputSettings(new AspectRatio(rw, rh), mode), $"ratio-{rw}x{rh}-{mode}.mp4");
        Assert.Equal((w, h), (result.DisplayWidth, result.DisplayHeight));

        // 加黑邊模式：來源比例不同時，畫面上下或左右的邊緣應為黑色；填滿裁切則不會。
        var edge = await ProcessRunner.RunForBytesAsync(media.Tools.Ffmpeg,
            ["-v", "error", "-i", result.Path, "-vf", "scale=16:16:flags=neighbor", "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "gray", "pipe:1"]);
        var isBlackCorner = edge[0] < 24;
        var ratioDiffers = rw * 9 != rh * 16;
        if (mode == FitMode.Pad && ratioDiffers) Assert.True(isBlackCorner);
        if (mode == FitMode.Fill && ratioDiffers) Assert.False(isBlackCorner);
    }

    [Fact]
    public async Task RotatedSourceExportsUpright()
    {
        var t = new TimelineModel();
        t.Append(await media.Probe(media.Rotated), 0, 0.5);
        var result = await media.Export(t, new OutputSettings(AspectRatio.R9x16, FitMode.Fill), "rotated-out.mp4");
        Assert.Equal((1080, 1920), (result.DisplayWidth, result.DisplayHeight));
        Assert.Equal(0, result.Rotation);
    }

    [Fact]
    public async Task OriginalKeepsEachReferenceSize()
    {
        var landscape = new TimelineModel();
        landscape.Append(await media.Probe(media.Landscape), 0, 0.5);
        var wide = await media.Export(landscape, new OutputSettings(AspectRatio.Original, FitMode.Fill), "original-wide.mp4");
        Assert.Equal((1920, 1080), (wide.DisplayWidth, wide.DisplayHeight));

        var rotated = new TimelineModel();
        rotated.Append(await media.Probe(media.Rotated), 0, 0.5);
        var tall = await media.Export(rotated, new OutputSettings(AspectRatio.Original, FitMode.Fill), "original-tall.mp4");
        Assert.Equal((1080, 1920), (tall.DisplayWidth, tall.DisplayHeight));
    }

    [Fact]
    public async Task OriginalFitsOtherClipsToFirstClip()
    {
        var t = new TimelineModel();
        t.Append(await media.Probe(media.Portrait), 0, 0.5);
        t.Append(await media.Probe(media.Landscape), 0, 0.5);
        var result = await media.Export(t, new OutputSettings(AspectRatio.Original, FitMode.Pad), "original-mixed.mp4");
        Assert.Equal((1080, 1920), (result.DisplayWidth, result.DisplayHeight));
        Assert.Equal(30, await media.CountFrames(result.Path));
    }

    [Theory]
    [InlineData("white", 255)]
    [InlineData("black", 0)]
    public async Task FullRangeSourceKeepsItsPixelValues(string color, int expected)
    {
        var source = await media.Probe(media.MakeSolid($"full-{color}.mp4", color, "yuvj420p"));
        Assert.Equal("pc", source.ColorRange);
        var t = new TimelineModel();
        t.Append(source, 0, 0.5);
        var result = await media.Export(t, Default, $"full-{color}-out.mp4");

        // 像素值與標記一致：仍是全範圍，純白 255、純黑 0，不是被壓成 235／16。
        Assert.Equal("pc", result.ColorRange);
        Assert.Equal((expected, expected), await media.LumaRange(result.Path, 0.1));
    }

    [Fact]
    public async Task LimitedRangeClipIsConvertedWhenTheOutputIsFullRange()
    {
        var t = new TimelineModel();
        t.Append(await media.Probe(media.MakeSolid("mixed-full.mp4", "black", "yuvj420p")), 0, 0.5);
        t.Append(await media.Probe(media.MakeSolid("mixed-limited.mp4", "white", "yuv420p")), 0, 0.5);
        var result = await media.Export(t, Default, "mixed-range-out.mp4");

        // 輸出以第一段的全範圍為準；第二段的白色（limited 235）要轉成 255，否則會變灰。
        Assert.Equal("pc", result.ColorRange);
        Assert.Equal((255, 255), await media.LumaRange(result.Path, 0.75));
    }

    [Fact]
    public async Task HdrSourceKeepsBitDepthAndColor()
    {
        var t = new TimelineModel();
        t.Append(await media.Probe(media.Hdr), 0, 0.5);
        var result = await media.Export(t, Default, "hdr-out.mp4");
        Assert.Equal("hevc", result.VideoCodec);
        Assert.Equal("yuv420p10le", result.PixelFormat);
        Assert.Equal("bt2020", result.ColorPrimaries);
        Assert.Equal("arib-std-b67", result.ColorTransfer);
        Assert.Equal("bt2020nc", result.ColorSpace);
    }

    [Fact]
    public async Task ClipVolumeIsAppliedAsLinearGain()
    {
        var source = await media.Probe(media.Landscape);
        var t = new TimelineModel();
        t.Append(source, 0, 2);
        t.Append(source, 0, 2).Volume = 0.5;
        var result = await media.Export(t, Default, "volume.mp4");
        var full = await media.MeanVolume(result.Path, 0.5, 1);
        var half = await media.MeanVolume(result.Path, 2.5, 1);
        Assert.Equal(-6.0, half - full, 0);
    }

    [Fact]
    public async Task MusicIsMixedAtItsPositionAndGapsAreSilent()
    {
        var t = new TimelineModel();
        var clip = t.Append(await media.Probe(media.Landscape), 0, 1);
        clip.Volume = 0;
        var music = t.Append(await media.Probe(media.Music), 0, 3);
        t.Move(music, 1);
        var result = await media.Export(t, Default, "music.mp4");
        Assert.Equal(4, result.Duration, 0);
        Assert.True(await media.MeanVolume(result.Path, 0.1, 0.7) < -80);
        Assert.True(await media.MeanVolume(result.Path, 1.5, 1) > -40);
        // 配樂超出影片的部分應為黑畫面，且總長到配樂結束。
        Assert.Equal(120, await media.CountFrames(result.Path));
    }

    [Fact]
    public async Task CancelLeavesNoFiles()
    {
        var t = new TimelineModel();
        t.Append(await media.Probe(media.Landscape), 0, 4);
        var output = media.Output("cancel.mp4");
        var plan = ExportPlanner.Build(t, Default, output);
        using var cts = new CancellationTokenSource();
        var progress = new SyncProgress(_ => cts.Cancel());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new ExportService(media.Tools).ExportAsync(plan, output, progress, cts.Token));
        Assert.False(File.Exists(output));
        Assert.Empty(Directory.GetFiles(media.Directory, "cancel.*.part.mp4"));
    }

    [Fact]
    public async Task FailedExportReportsStderr()
    {
        var source = await media.Probe(media.Landscape);
        var t = new TimelineModel();
        t.Append(source with { Path = media.Output("missing.mp4") }, 0, 1);
        var output = media.Output("failed.mp4");
        var plan = ExportPlanner.Build(t, Default, output);
        var error = await Assert.ThrowsAsync<FfmpegException>(() => new ExportService(media.Tools).ExportAsync(plan, output, null));
        Assert.Contains("missing.mp4", error.Stderr);
    }

    [Fact]
    public async Task PreflightRejectsOverwritingSource()
    {
        var t = new TimelineModel();
        t.Append(await media.Probe(media.Landscape), 0, 1);
        Assert.NotNull(ExportService.Preflight(t, media.Landscape));
        Assert.Null(ExportService.Preflight(t, media.Output("ok.mp4")));
    }

    [Fact]
    public async Task ExportNeverTouchesAFileNamedLikeTheOldTemporaryFile()
    {
        // 舊版固定寫到「檔名.part.mp4」：同名的檔案會被覆蓋，結束時又被刪掉。
        var output = media.Output("keep.mp4");
        var bystander = media.Output("keep.part.mp4");
        await File.WriteAllTextAsync(bystander, "使用者自己的檔案");
        var t = new TimelineModel();
        t.Append(await media.Probe(media.Landscape), 0, 0.5);

        await new ExportService(media.Tools).ExportAsync(ExportPlanner.Build(t, Default, output), output, null);

        Assert.Equal("使用者自己的檔案", await File.ReadAllTextAsync(bystander));
        Assert.True(File.Exists(output));
        Assert.Empty(Directory.GetFiles(media.Directory, "keep.*.part.mp4"));
    }

    [Fact]
    public void TemporaryPathIsBesideTheOutputAndUnique()
    {
        var output = media.Output("temp-name.mp4");
        var first = ExportService.TemporaryPath(output);
        Assert.Equal(media.Directory, Path.GetDirectoryName(first));
        Assert.Matches(@"^temp-name\.[0-9a-f]{8}\.part\.mp4$", Path.GetFileName(first));
        Assert.NotEqual(first, ExportService.TemporaryPath(output));
    }

    [Fact]
    public async Task FinishedVideoIsKeptWhenTheOutputCannotBeReplaced()
    {
        var t = new TimelineModel();
        t.Append(await media.Probe(media.Landscape), 0, 0.5);
        var output = media.Output("locked-at-end.mp4");

        ExportRenameException error;
        // 模擬匯出途中用播放器打開了舊的輸出檔：改名那一步會失敗。
        using (new FileStream(output, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            error = await Assert.ThrowsAsync<ExportRenameException>(() =>
                new ExportService(media.Tools).ExportAsync(ExportPlanner.Build(t, Default, output), output, null));

        try
        {
            // 做好的影片沒被刪掉，而且是完整可播放的檔案。
            Assert.True(File.Exists(error.TemporaryPath));
            Assert.Contains(error.TemporaryPath, error.Message);
            Assert.True((await media.Probe(error.TemporaryPath)).Duration > 0.3);
        }
        finally
        {
            File.Delete(error.TemporaryPath);
        }
    }

    [Fact]
    public async Task PreflightRejectsOutputOpenedByAnotherProgram()
    {
        var t = new TimelineModel();
        t.Append(await media.Probe(media.Landscape), 0, 1);
        var output = media.Output("open-in-player.mp4");

        // 模擬播放器開著上次匯出的檔案。
        using (new FileStream(output, FileMode.Create, FileAccess.Write, FileShare.Read))
            Assert.Contains("正被其他程式使用", ExportService.Preflight(t, output));

        // 關掉之後就可以覆蓋。
        Assert.Null(ExportService.Preflight(t, output));
    }

    [Fact]
    public async Task PreflightRejectsReadOnlyOutput()
    {
        var t = new TimelineModel();
        t.Append(await media.Probe(media.Landscape), 0, 1);
        var output = media.Output("read-only.mp4");
        await File.WriteAllBytesAsync(output, [0]);
        File.SetAttributes(output, FileAttributes.ReadOnly);
        try
        {
            Assert.Contains("唯讀", ExportService.Preflight(t, output));
        }
        finally
        {
            File.SetAttributes(output, FileAttributes.Normal);
        }
    }

    [Fact]
    public void FreeSpaceIsKnownLocallyAndUnknownForNetworkPaths()
    {
        Assert.NotNull(ExportService.FreeSpace(media.Output("local.mp4")));
        // UNC 網路路徑查不到剩餘空間：回傳 null 而不是丟出例外，匯出前檢查會略過這一項。
        Assert.Null(ExportService.FreeSpace(@"\\no-such-server\share\out.mp4"));
    }

    private sealed class SyncProgress(Action<double> action) : IProgress<double>
    {
        public void Report(double value) => action(value);
    }
}
