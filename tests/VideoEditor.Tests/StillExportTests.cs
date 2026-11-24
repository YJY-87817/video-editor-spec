using VideoEditor.Core.Editing;
using VideoEditor.Core.Export;
using VideoEditor.Core.Ffmpeg;
using VideoEditor.Core.Models;

namespace VideoEditor.Tests;

/// <summary>圖片片段的匯出參數（docs/IMAGE-CLIP-PLAN.md 階段 4），不執行 FFmpeg。</summary>
public class StillExportPlanTests
{
    private static MediaInfo Photo(int width = 4000, int height = 3000, string path = "photo.jpg") => new()
    {
        Path = path, IsStill = true, HasVideo = true, Duration = Timeline.MaxStillDuration,
        DisplayWidth = width, DisplayHeight = height, FrameRate = new Rational(0, 1), PixelFormat = "yuvj420p", BitDepth = 8,
    };

    private static MediaInfo Video(int fps = 25, bool hdr = false) => new()
    {
        Path = "a.mp4", Duration = 10, HasVideo = true, HasAudio = true, AudioBitRate = 128_000, AudioChannels = 2,
        DisplayWidth = 1920, DisplayHeight = 1080, FrameRate = new Rational(fps, 1), PixelFormat = hdr ? "yuv420p10le" : "yuv420p",
        BitDepth = hdr ? 10 : 8, ColorTransfer = hdr ? "arib-std-b67" : "bt709", ColorSpace = hdr ? "bt2020nc" : "bt709",
        ColorPrimaries = hdr ? "bt2020" : "bt709", ColorRange = "tv",
    };

    private static readonly OutputSettings Original = new(AspectRatio.Original, FitMode.Fill);

    [Fact]
    public void FirstVideoClipIsTheReferenceEvenAfterAPhoto()
    {
        var t = new Timeline();
        t.Append(Photo(), 0, 3);
        t.Append(Video(25), 0, 2);

        var plan = ExportPlanner.Build(t, Original, "out.mp4");

        Assert.Equal(new Size2(1920, 1080), plan.Size);
        Assert.Contains("-framerate 25/1", string.Join(' ', plan.Arguments));
    }

    [Fact]
    public void PhotosOnlyUseThirtyFpsH264AndAt4K()
    {
        var t = new Timeline();
        t.Append(Photo(6000, 4000), 0, 3);
        t.Append(Photo(1000, 1000, "b.png"), 0, 3);

        var plan = ExportPlanner.Build(t, Original, "out.mp4");

        Assert.Equal(new Size2(3840, 2560), plan.Size); // 較大邊 6000 → 3840
        Assert.Equal(("libx264", "yuv420p"), (plan.VideoCodec, plan.PixelFormat));
        var args = string.Join(' ', plan.Arguments);
        Assert.Contains("-framerate 30/1", args);
        Assert.Contains("colorspace=bt709", args);
        Assert.DoesNotContain("-c:a", args); // 沒有聲音
    }

    [Fact]
    public void SmallPhotosOnlyKeepTheirSize()
    {
        var t = new Timeline();
        t.Append(Photo(1201, 900), 0, 3);
        Assert.Equal(new Size2(1202, 900), ExportPlanner.Build(t, Original, "out.mp4").Size);
    }

    [Fact]
    public void PhotoIsReadOnceAndRepeatedAfterProcessing()
    {
        var t = new Timeline();
        t.Append(Video(30), 0, 2);
        t.Append(Photo(), 0, 1.5);

        var plan = ExportPlanner.Build(t, Original, "out.mp4");
        var args = plan.Arguments.ToList();
        var photoInput = args.LastIndexOf("photo.jpg");

        Assert.Equal(["-framerate", "30/1", "-protocol_whitelist", "file", "-i"], args[(photoInput - 5)..photoInput]);
        Assert.DoesNotContain("-ss", args[(args.LastIndexOf("a.mp4") + 1)..photoInput]); // JPEG 加 -ss 會解不出畫面
        Assert.DoesNotContain("-loop", args); // 不要每格重新解碼
        var graph = args[args.IndexOf("-filter_complex") + 1];
        var still = graph.Split(';').Single(f => f.StartsWith("[1:v]", StringComparison.Ordinal));
        Assert.StartsWith("[1:v]format=rgba,premultiply=inplace=1,format=rgb24,", still);
        Assert.Contains("scale=out_color_matrix=bt709:out_range=tv,format=yuv420p", still);
        Assert.EndsWith(",loop=loop=44:size=1:start=0,setpts=N[v1]", still); // 45 格，縮放後才重複
    }

    [Fact]
    public void PhotoUsesTheDefaultRateWhenTheReferenceHasNone()
    {
        var t = new Timeline();
        t.Append(Video() with { FrameRate = new Rational(0, 1) }, 0, 2);
        t.Append(Photo(), 0, 1);
        Assert.Contains("-framerate 30", string.Join(' ', ExportPlanner.Build(t, Original, "out.mp4").Arguments));
    }

    [Fact]
    public void GapUsesTheDefaultRateWhenTheReferenceHasNone()
    {
        var t = new Timeline();
        var clip = t.Append(Video() with { FrameRate = new Rational(0, 1) }, 0, 2);
        t.Move(clip, 1);
        Assert.Contains("color=c=black:s=1920x1080:r=30:", string.Join(' ', ExportPlanner.Build(t, Original, "out.mp4").Arguments));
    }

    [Theory]
    [InlineData("smpte170m", "bt601")]
    [InlineData("bt470bg", "bt601")]
    [InlineData("bt2020nc", "bt2020")]
    [InlineData(null, "bt709")]
    public void PhotoMatrixFollowsTheReference(string? colorSpace, string matrix)
    {
        var t = new Timeline();
        t.Append(Video() with { ColorSpace = colorSpace }, 0, 2);
        t.Append(Photo(), 0, 1);
        var graph = string.Join(' ', ExportPlanner.Build(t, Original, "out.mp4").Arguments);
        Assert.Contains($"out_color_matrix={matrix}:", graph);
    }

    [Fact]
    public void PhotosDoNotTriggerVideoMismatchWarningsOrAnFpsFilter()
    {
        var t = new Timeline();
        t.Append(Video(30), 0, 2);
        t.Append(Photo(4000, 3000), 0, 2);

        var plan = ExportPlanner.Build(t, new OutputSettings(AspectRatio.R16x9, FitMode.Fill), "out.mp4");

        Assert.Empty(plan.Warnings);
        Assert.DoesNotContain("fps=", string.Join(' ', plan.Arguments));
    }

    [Theory]
    [InlineData(FitMode.Fill, 1920, 800, true)] // 填滿看較短的邊：800 < 1080
    [InlineData(FitMode.Pad, 1920, 800, false)] // 加黑邊看較長的邊：1920 剛好
    [InlineData(FitMode.Pad, 1280, 720, true)]
    [InlineData(FitMode.Fill, 4000, 3000, false)]
    public void LowResolutionPhotoWarnsAboutUpscaling(FitMode mode, int width, int height, bool warns)
    {
        var t = new Timeline();
        t.Append(Video(), 0, 2);
        t.Append(Photo(width, height), 0, 2);

        var warnings = ExportPlanner.Build(t, new OutputSettings(AspectRatio.Original, mode), "out.mp4").Warnings;

        Assert.Equal(warns, warnings.Any(w => w.Contains("第 2 段圖片") && w.Contains("模糊")));
    }

    [Fact]
    public void PhotoInHdrVideoWarnsAboutColours()
    {
        var t = new Timeline();
        t.Append(Video(hdr: true), 0, 2);
        t.Append(Photo(), 0, 2);
        Assert.Contains(ExportPlanner.Build(t, Original, "out.mp4").Warnings, w => w.Contains("SDR"));
    }

    [Fact]
    public void SizeEstimateIgnoresThePhotoFileBitRate()
    {
        // ffprobe 給圖片的位元率是檔案大小除以 0.04 秒，若照算會估出好幾 GB。
        var t = new Timeline();
        t.Append(Photo() with { BitRate = 2_000_000_000 }, 0, 10);
        Assert.True(ExportService.EstimateBytes(t) == 0);
    }
}

/// <summary>實際匯出含圖片的時間軸。</summary>
[Collection("media")]
[Trait("Category", "Integration")]
public class StillExportIntegrationTests(TestMedia media)
{
    private async Task<string> Image(string name, string source)
    {
        var path = media.Output(name);
        await ProcessRunner.RunForBytesAsync(media.Tools.Ffmpeg,
            ["-hide_banner", "-y", "-v", "error", "-f", "lavfi", "-i", source, "-frames:v", "1", path]);
        return path;
    }

    private async Task<string> ExportTimeline(Timeline t, OutputSettings settings, string name)
    {
        var output = media.Output(name);
        await new ExportService(media.Tools).ExportAsync(ExportPlanner.Build(t, settings, output), null);
        return output;
    }

    /// <summary>指定時間、指定位置那一像素的原始 Y、U、V 值。</summary>
    private async Task<(int Y, int U, int V)> Yuv(string path, double time, string position = "iw/2:ih/2")
    {
        var bytes = await ProcessRunner.RunForBytesAsync(media.Tools.Ffmpeg,
            ["-v", "error", "-ss", time.ToString(System.Globalization.CultureInfo.InvariantCulture), "-i", path,
             "-vf", $"crop=2:2:{position}", "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "yuv444p", "pipe:1"]);
        return (bytes[0], bytes[4], bytes[8]);
    }

    [Fact]
    public async Task PhotosOnlyExportHasTheExactFrameCount()
    {
        var photo = await Image("export-a.png", "testsrc2=s=640x480");
        var t = new Timeline();
        t.Append(await media.Probe(photo), 0, 2);
        t.Append(await media.Probe(photo), 0, 1.01); // 不是整數格：對齊到最近的一格

        var output = await ExportTimeline(t, new OutputSettings(AspectRatio.Original, FitMode.Fill), "photos.mp4");
        var info = await media.Probe(output);

        Assert.Equal((640, 480), (info.DisplayWidth, info.DisplayHeight));
        Assert.Equal(30, info.FrameRate.Value, 3);
        Assert.Equal(60 + 30, await media.CountFrames(output));
        Assert.False(info.HasAudio);
    }

    [Theory]
    [InlineData("30000/1001")]
    [InlineData("25")]
    public async Task PhotoFramesContinueTheVideoTimestamps(string rate)
    {
        // 照片接在影片前後：每一格的時間都要剛好隔一格，不能重複或跳格。
        var video = media.MakeClip($"rate-{rate.Replace('/', '_')}.mp4", $"testsrc2=s=320x240:r={rate}:d=1", null, "-pix_fmt", "yuv420p");
        var photo = await Image($"rate-{rate.Replace('/', '_')}.jpg", "testsrc2=s=320x240");
        var t = new Timeline();
        t.Append(await media.Probe(photo), 0, 1);
        t.Append(await media.Probe(video), 0, 1);
        t.Append(await media.Probe(photo), 0, 1);

        var output = await ExportTimeline(t, new OutputSettings(AspectRatio.Original, FitMode.Fill), $"rate-{rate.Replace('/', '_')}.mp4");

        var fps = Rational.Parse(rate.Contains('/') ? rate : rate + "/1").Value;
        var times = System.Text.Encoding.UTF8.GetString(await ProcessRunner.RunForBytesAsync(media.Tools.Ffprobe,
                ["-v", "error", "-select_streams", "v", "-show_entries", "frame=pts_time", "-of", "csv=p=0", output]))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => double.Parse(s.Trim().TrimEnd(','), System.Globalization.CultureInfo.InvariantCulture)).ToList();
        var expected = (int)Math.Round(3 * fps);
        Assert.InRange(times.Count, expected - 1, expected + 1);
        for (var i = 1; i < times.Count; i++) Assert.Equal(1 / fps, times[i] - times[i - 1], 3);
    }

    [Fact]
    public async Task PhotoBetweenVideosKeepsTheTimingAndAddsSilence()
    {
        var photo = await Image("between.jpg", "color=c=red:s=800x600");
        var video = await media.Probe(media.Landscape);
        var t = new Timeline();
        t.Append(video, 0, 1);
        t.Append(await media.Probe(photo), 0, 1.5);
        t.Append(video, 1, 2);

        var output = await ExportTimeline(t, new OutputSettings(AspectRatio.Original, FitMode.Pad), "between.mp4");
        var info = await media.Probe(output);

        Assert.Equal((1920, 1080), (info.DisplayWidth, info.DisplayHeight));
        Assert.Equal(30 + 45 + 30, await media.CountFrames(output));
        Assert.Equal(3.5, info.Duration, 1);
        Assert.True(await media.MeanVolume(output, 1.2, 1.0) < -60); // 圖片那段是靜音
        Assert.True(await media.MeanVolume(output, 0.1, 0.8) > -40);
        // 圖片那段是紅色（BT.709 的紅：Y≈63、V≈240）。
        var (y, _, v) = await Yuv(output, 1.75);
        Assert.InRange(y, 58, 68);
        Assert.InRange(v, 230, 245);
    }

    [Theory]
    [InlineData("red.png")]
    [InlineData("red.jpg")]
    public async Task PhotoColoursUseBt709(string name)
    {
        // BT.709 limited 的純紅：Y 63、U 102、V 240；如果誤用 BT.601 會是 Y 81、U 90。
        var photo = await Image(name, "color=c=0xFF0000:s=320x240");
        var t = new Timeline();
        t.Append(await media.Probe(photo), 0, 1);

        var output = await ExportTimeline(t, new OutputSettings(AspectRatio.Original, FitMode.Fill), "color-" + name + ".mp4");
        var (y, u, v) = await Yuv(output, 0.5);
        var info = await media.Probe(output);

        Assert.InRange(y, 60, 66);
        Assert.InRange(u, 98, 106);
        Assert.InRange(v, 236, 244);
        Assert.Equal(("bt709", "tv"), (info.ColorSpace, info.ColorRange));
    }

    [Fact]
    public async Task TransparentAreasBecomeBlack()
    {
        // 左半不透明的白、右半完全透明（透明處的顏色也是白）。
        var photo = await Image("alpha.png", "color=c=white:s=200x100,format=rgba,geq=r=255:g=255:b=255:a='if(lt(X,100),255,0)'");
        var t = new Timeline();
        t.Append(await media.Probe(photo), 0, 1);

        var output = await ExportTimeline(t, new OutputSettings(AspectRatio.Original, FitMode.Fill), "alpha.mp4");

        Assert.InRange((await Yuv(output, 0.5, "40:50")).Y, 230, 240); // 白
        Assert.InRange((await Yuv(output, 0.5, "160:50")).Y, 14, 20); // 黑
    }

    [Fact]
    public async Task PortraitPhotoFromExifIsExportedUpright()
    {
        var plain = await File.ReadAllBytesAsync(await Image("exif-src.jpg", "color=c=gray:s=320x240"));
        var photo = media.Output("exif-6.jpg");
        byte[] tiff = [(byte)'M', (byte)'M', 0, 42, 0, 0, 0, 8, 0, 1, 0x01, 0x12, 0, 3, 0, 0, 0, 1, 0, 6, 0, 0, 0, 0, 0, 0];
        byte[] header = [(byte)'E', (byte)'x', (byte)'i', (byte)'f', 0, 0];
        var length = 2 + header.Length + tiff.Length;
        await File.WriteAllBytesAsync(photo, [.. plain[..2], 0xFF, 0xE1, (byte)(length >> 8), (byte)length, .. header, .. tiff, .. plain[2..]]);
        var t = new Timeline();
        t.Append(await media.Probe(photo), 0, 1);

        var output = await ExportTimeline(t, new OutputSettings(AspectRatio.Original, FitMode.Fill), "exif.mp4");
        var info = await media.Probe(output);

        Assert.Equal((240, 320), (info.DisplayWidth, info.DisplayHeight));
    }

    [Fact]
    public async Task PaddedPhotoHasBlackBars()
    {
        var photo = await Image("square.png", "color=c=white:s=600x600");
        var t = new Timeline();
        t.Append(await media.Probe(media.Landscape), 0, 1);
        t.Append(await media.Probe(photo), 0, 1);

        var output = await ExportTimeline(t, new OutputSettings(AspectRatio.Original, FitMode.Pad), "padded.mp4");

        Assert.InRange((await Yuv(output, 1.5, "10:540")).Y, 14, 20); // 左側黑邊
        Assert.InRange((await Yuv(output, 1.5)).Y, 230, 240); // 中間是圖片
    }
}
