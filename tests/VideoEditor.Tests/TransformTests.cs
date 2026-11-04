using VideoEditor.Core.Export;
using VideoEditor.Core.Ffmpeg;
using VideoEditor.Core.Models;
using VideoEditor.Core.Timeline;
using TimelineModel = VideoEditor.Core.Timeline.Timeline;

namespace VideoEditor.Tests;

public class FrameTransformTests
{
    [Fact]
    public void RotationWrapsAround()
    {
        var t = FrameTransform.Identity.Rotate(-90);
        Assert.Equal(270, t.Rotation);
        Assert.Equal(0, t.Rotate(90).Rotation);
        Assert.Equal(180, FrameTransform.Identity.Rotate(90).Rotate(90).Rotation);
        Assert.True(t.SwapsSize);
        Assert.Equal(new Size2(1080, 1920), t.Apply(new Size2(1920, 1080)));
    }

    [Fact]
    public void RotatingAMirroredFrameTurnsTheWayItLooks()
    {
        // 水平鏡像後再「畫面上」右轉 90°，等於原畫面左轉 90° 再鏡像。
        var t = FrameTransform.Identity.ToggleHorizontal().Rotate(90);
        Assert.Equal(270, t.Rotation);
        Assert.True(t.FlipHorizontal);
        // 兩個方向都鏡像（等於轉 180°）時不必反向。
        Assert.Equal(90, FrameTransform.Identity.ToggleHorizontal().ToggleVertical().Rotate(90).Rotation);
    }

    [Theory]
    [InlineData(0, false, false, "")]
    [InlineData(90, false, false, "transpose=clock,")]
    [InlineData(180, false, false, "hflip,vflip,")]
    [InlineData(270, true, false, "transpose=cclock,hflip,")]
    [InlineData(0, false, true, "vflip,")]
    public void FilterRotatesThenMirrors(int rotation, bool h, bool v, string expected) =>
        Assert.Equal(expected, new FrameTransform(rotation, h, v).Filter());

    [Fact]
    public void MusicClipsCannotBeTransformed()
    {
        var t = new TimelineModel();
        var music = t.Append(new MediaInfo { Path = "m.m4a", Duration = 5, HasAudio = true }, 0, 5);
        Assert.Throws<ArgumentException>(() => t.SetTransform(music, new FrameTransform(90)));
    }

    [Fact]
    public void RotatedFirstClipTurnsOriginalOutputPortrait()
    {
        var media = new MediaInfo
        {
            Path = "a.mp4", Duration = 10, HasVideo = true, DisplayWidth = 1920, DisplayHeight = 1080,
            FrameRate = new Rational(30, 1), PixelFormat = "yuv420p", SampleAspect = new Rational(4, 3),
        };
        var t = new TimelineModel();
        var clip = t.Append(media, 0, 2);
        t.SetTransform(clip, new FrameTransform(90));
        var plan = ExportPlanner.Build(t, new OutputSettings(AspectRatio.Original, FitMode.Fill), "o.mp4");
        Assert.Equal(new Size2(1080, 1920), plan.Size);

        var args = plan.Arguments.ToList();
        var graph = args[args.IndexOf("-filter_complex") + 1];
        // 像素比在旋轉前修正。
        Assert.True(graph.IndexOf("setsar=1,transpose=clock", StringComparison.Ordinal) > 0);
        Assert.Empty(plan.Warnings);

        // 16:9 以較大邊為準，不受旋轉影響。
        Assert.Equal(new Size2(1920, 1080), ExportPlanner.Build(t, new OutputSettings(AspectRatio.R16x9, FitMode.Pad), "o.mp4").Size);
    }

    [Fact]
    public void MixedOrientationWarns()
    {
        var media = new MediaInfo
        {
            Path = "a.mp4", Duration = 10, HasVideo = true, DisplayWidth = 1920, DisplayHeight = 1080,
            FrameRate = new Rational(30, 1), PixelFormat = "yuv420p",
        };
        var t = new TimelineModel();
        t.Append(media, 0, 1);
        t.SetTransform(t.Append(media, 1, 2), new FrameTransform(270));
        Assert.Contains(ExportPlanner.Build(t, new OutputSettings(AspectRatio.Original, FitMode.Pad), "o.mp4").Warnings,
            w => w.Contains("解析度不一致"));
    }
}

[Collection("media")]
public class TransformIntegrationTests(TestMedia media)
{
    private async Task<byte[]> Frame(string path, string filter) => await ProcessRunner.RunForBytesAsync(media.Tools.Ffmpeg,
        ["-v", "error", "-i", path, "-vf", $"select=eq(n\\,0),{filter}scale=64:64", "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "gray", "pipe:1"]);

    private static long Difference(byte[] a, byte[] b) => a.Zip(b, (x, y) => (long)Math.Abs(x - y)).Sum();

    [Theory]
    [InlineData(90, false, false, "transpose=clock,", 1080, 1920)]
    [InlineData(0, true, false, "hflip,", 1920, 1080)]
    [InlineData(180, false, true, "hflip,", 1920, 1080)]
    public async Task ExportedFrameIsRotatedAndMirrored(int rotation, bool h, bool v, string equivalent, int width, int height)
    {
        var source = await media.Probe(media.Landscape);
        var t = new TimelineModel();
        t.SetTransform(t.Append(source, 0, 1), new FrameTransform(rotation, h, v));
        var result = await media.Export(t, new OutputSettings(AspectRatio.Original, FitMode.Fill, Crf: 0), $"transform-{rotation}-{h}-{v}.mp4");

        Assert.Equal((width, height), (result.DisplayWidth, result.DisplayHeight));
        var exported = await Frame(result.Path, "");
        var expected = await Frame(media.Landscape, equivalent);
        var untouched = await Frame(media.Landscape, "");
        Assert.True(Difference(exported, expected) * 4 < Difference(exported, untouched));
    }

    [Fact]
    public async Task ReversedClipCanAlsoBeRotated()
    {
        var source = await media.Probe(media.Landscape);
        var t = new TimelineModel();
        var clip = t.Append(source, 0, 1);
        t.SetReversed(clip, true);
        t.SetTransform(clip, new FrameTransform(270));
        var result = await media.Export(t, new OutputSettings(AspectRatio.Original, FitMode.Fill), "reverse-rotate.mp4");
        Assert.Equal((1080, 1920), (result.DisplayWidth, result.DisplayHeight));
        Assert.Equal(30, await media.CountFrames(result.Path));
    }
}
