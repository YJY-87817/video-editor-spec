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
        Assert.False(File.Exists(media.Output("cancel.part.mp4")));
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

    private sealed class SyncProgress(Action<double> action) : IProgress<double>
    {
        public void Report(double value) => action(value);
    }
}
