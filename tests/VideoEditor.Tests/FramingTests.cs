using VideoEditor.Core.Export;
using VideoEditor.Core.Ffmpeg;
using VideoEditor.Core.Models;
using VideoEditor.Core.Timeline;
using TimelineModel = VideoEditor.Core.Timeline.Timeline;

namespace VideoEditor.Tests;

public class ClipFramingTests
{
    private static readonly Size2 Source = new(1920, 1080);
    private static readonly Size2 Square = new(1080, 1080);
    private static readonly Size2 Portrait = new(1080, 1920);

    private static void AssertRect(Rect2 expected, Rect2 actual)
    {
        Assert.Equal(expected.X, actual.X, 6);
        Assert.Equal(expected.Y, actual.Y, 6);
        Assert.Equal(expected.Width, actual.Width, 6);
        Assert.Equal(expected.Height, actual.Height, 6);
    }

    [Fact]
    public void DefaultIsTheLargestCenteredFrame()
    {
        AssertRect(new Rect2(656.25, 0, 607.5, 1080), ClipFraming.Default.CropRect(Source, Portrait));
        AssertRect(new Rect2(420, 0, 1080, 1080), ClipFraming.Default.CropRect(Source, Square));
        Assert.True(ClipFraming.Default.IsDefault);
        Assert.True(ClipFraming.Default.Normalize(Source, Portrait).IsDefault);
    }

    [Fact]
    public void ZoomShrinksTheFrameAroundItsCenter() =>
        AssertRect(new Rect2(210, 270, 540, 540), new ClipFraming(2, 0.25, 0.5).CropRect(Source, Square));

    [Fact]
    public void FrameStaysInsideTheSource()
    {
        AssertRect(new Rect2(0, 0, 607.5, 1080), new ClipFraming(1, 0, 0).CropRect(Source, Portrait));
        AssertRect(new Rect2(1312.5, 0, 607.5, 1080), new ClipFraming(1, 2, 0.5).CropRect(Source, Portrait));
        // 放大倍數限制在 1–8。
        Assert.Equal(1080, new ClipFraming(0.2, 0.5, 0.5).CropRect(Source, Square).Width, 6);
        Assert.Equal(135, new ClipFraming(100, 0.5, 0.5).CropRect(Source, Square).Width, 6);
    }

    [Fact]
    public void FromCropIsTheInverseOfCropRect()
    {
        var framing = new ClipFraming(2, 0.25, 0.5);
        var back = ClipFraming.FromCrop(framing.CropRect(Source, Square), Source, Square);
        Assert.Equal(2, back.Zoom, 6);
        Assert.Equal(0.25, back.CenterX, 6);
        Assert.Equal(0.5, back.CenterY, 6);
    }

    [Fact]
    public void MovingPastTheEdgeAndBackRespondsRightAway()
    {
        var atEdge = ClipFraming.Default.MoveBy(2000, 0, Source, Portrait);
        Assert.Equal(1312.5, atEdge.CropRect(Source, Portrait).X, 6);
        // 存的是推回來源內的位置，所以往回拉 100 像素就立刻移動 100 像素。
        Assert.Equal(1212.5, atEdge.MoveBy(-100, 0, Source, Portrait).CropRect(Source, Portrait).X, 6);
    }

    [Fact]
    public void ZoomByKeepsTheCenterAndStaysWithinLimits()
    {
        var zoomed = new ClipFraming(2, 0.3, 0.5).ZoomBy(1.5, Source, Square);
        Assert.Equal(3, zoomed.Zoom, 6);
        Assert.Equal(0.3, zoomed.CenterX, 6);
        Assert.Equal(ClipFraming.MaxZoom, zoomed.ZoomBy(100, Source, Square).Zoom, 6);
        Assert.Equal(1, zoomed.ZoomBy(0.01, Source, Square).Zoom, 6);
    }

    [Fact]
    public void ResizingFromACornerKeepsTheOppositeCornerAndTheRatio()
    {
        var framing = ClipFraming.ResizeFromCorner(0, 0, 1, 1, 540, 10, Source, Square);
        AssertRect(new Rect2(0, 0, 540, 540), framing.CropRect(Source, Square));

        // 往左上拉：右下角固定。
        var fromBottomRight = ClipFraming.ResizeFromCorner(1500, 900, -1, -1, 1200, 850, Source, Square);
        AssertRect(new Rect2(1200, 600, 300, 300), fromBottomRight.CropRect(Source, Square));
    }

    [Fact]
    public void ResizingIsLimitedBySourceAndZoomRange()
    {
        // 拉到超出來源：最大只到放得進來源的大小。
        Assert.Equal(1080, ClipFraming.ResizeFromCorner(0, 0, 1, 1, 5000, 5000, Source, Square).CropRect(Source, Square).Width, 6);
        // 往下只剩 400 像素：寬度也被限制在 400（1:1）。
        Assert.Equal(400, ClipFraming.ResizeFromCorner(0, 680, 1, 1, 5000, 5000, Source, Square).CropRect(Source, Square).Width, 6);
        // 拉到幾乎沒有大小：最小是最大框的 1/8。
        Assert.Equal(135, ClipFraming.ResizeFromCorner(0, 0, 1, 1, 1, 1, Source, Square).CropRect(Source, Square).Width, 6);
    }

    [Fact]
    public void CropFilterUsesFractionsOfTheSource()
    {
        Assert.Equal("", ClipFraming.Default.CropFilter(Source, Square));
        Assert.Equal("crop=w=iw*0.28125:h=ih*0.5:x=iw*0.109375:y=ih*0.25,", new ClipFraming(2, 0.25, 0.5).CropFilter(Source, Square));
    }

    [Fact]
    public void PreviewFrameFollowsTheFraming()
    {
        var layout = PreviewLayout.Compute(1600, 900, Source, Square, FitMode.Fill, new ClipFraming(2, 0.25, 0.5));
        Assert.Equal(new Rect2(0, 0, 1600, 900), layout.Source);
        Assert.Equal(175, layout.Frame.X, 6);
        Assert.Equal(225, layout.Frame.Y, 6);
        Assert.Equal(450, layout.Frame.Width, 6);
        Assert.Equal(450, layout.Frame.Height, 6);

        // 加黑邊不使用畫面位置。
        var pad = PreviewLayout.Compute(1600, 900, Source, Square, FitMode.Pad, new ClipFraming(2, 0.25, 0.5));
        Assert.Equal(PreviewLayout.Compute(1600, 900, Source, Square, FitMode.Pad), pad);
    }
}

public class ClipFramingTimelineTests
{
    private static MediaInfo Media() => new()
    {
        Path = "a.mp4", Duration = 10, HasVideo = true, HasAudio = true, DisplayWidth = 1920, DisplayHeight = 1080,
        FrameRate = new Rational(30, 1), PixelFormat = "yuv420p",
    };

    private static string Graph(ExportPlan plan)
    {
        var args = plan.Arguments.ToList();
        return args[args.IndexOf("-filter_complex") + 1];
    }

    [Fact]
    public void ChangingTheDirectionResetsTheFraming()
    {
        var t = new TimelineModel();
        var clip = t.Append(Media(), 0, 2);
        var framing = new ClipFraming(2, 0.25, 0.5);
        t.SetFraming(clip, framing);

        t.SetTransform(clip, FrameTransform.Identity); // 方向沒變，保留
        Assert.Equal(framing, clip.Framing);
        t.SetTransform(clip, new FrameTransform(90));
        Assert.True(clip.Framing.IsDefault);
    }

    [Fact]
    public void MusicClipsHaveNoFraming()
    {
        var t = new TimelineModel();
        var music = t.Append(new MediaInfo { Path = "m.mp3", Duration = 5, HasAudio = true }, 0, 5);
        Assert.Throws<ArgumentException>(() => t.SetFraming(music, ClipFraming.Default));
    }

    [Fact]
    public void ExportCropsOnlyFramedClipsInFillMode()
    {
        var t = new TimelineModel();
        t.SetFraming(t.Append(Media(), 0, 1), new ClipFraming(2, 0.25, 0.5));
        t.Append(Media(), 1, 2);

        var fill = Graph(ExportPlanner.Build(t, new OutputSettings(AspectRatio.R1x1, FitMode.Fill), "o.mp4"));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(fill, @"crop=w=iw\*"));
        // 1:1 以較大邊為準：1920×1080 輸出 1920×1920；裁切比例只看長寬比。
        Assert.Contains("crop=w=iw*0.28125:h=ih*0.5:x=iw*0.109375:y=ih*0.25,scale=1920:1920", fill);

        var pad = Graph(ExportPlanner.Build(t, new OutputSettings(AspectRatio.R1x1, FitMode.Pad), "o.mp4"));
        Assert.DoesNotContain("crop=w=", pad);
    }

    [Fact]
    public void FramingIsAppliedAfterRotation()
    {
        var t = new TimelineModel();
        var clip = t.Append(Media(), 0, 1);
        t.SetTransform(clip, new FrameTransform(90));
        // 轉成直式 1080×1920 後，選上半部的正方形。
        t.SetFraming(clip, new ClipFraming(1, 0.5, 0));
        var graph = Graph(ExportPlanner.Build(t, new OutputSettings(AspectRatio.R1x1, FitMode.Fill), "o.mp4"));
        Assert.Contains("transpose=clock,crop=w=iw*1:h=ih*0.5625:x=iw*0:y=ih*0,", graph);
    }
}

[Collection("media")]
public class ClipFramingIntegrationTests(TestMedia media)
{
    private async Task<byte[]> Frame(string path, string filter) => await ProcessRunner.RunForBytesAsync(media.Tools.Ffmpeg,
        ["-v", "error", "-i", path, "-vf", $"select=eq(n\\,0),{filter}scale=64:64", "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "gray", "pipe:1"]);

    private static long Difference(byte[] a, byte[] b) => a.Zip(b, (x, y) => (long)Math.Abs(x - y)).Sum();

    [Fact]
    public async Task ExportKeepsTheChosenPartOfThePicture()
    {
        var source = await media.Probe(media.Landscape);
        var t = new TimelineModel();
        // 1920×1080 → 1:1，放大 2 倍、框中心在左邊四分之一處：取來源 (210, 270) 起的 540×540。
        t.SetFraming(t.Append(source, 0, 1), new ClipFraming(2, 0.25, 0.5));
        var result = await media.Export(t, new OutputSettings(AspectRatio.R1x1, FitMode.Fill, Crf: 0), "framing.mp4");

        Assert.Equal((1920, 1920), (result.DisplayWidth, result.DisplayHeight)); // 1:1 以較大邊為準
        var exported = await Frame(result.Path, "");
        var expected = await Frame(media.Landscape, "crop=540:540:210:270,");
        var centered = await Frame(media.Landscape, "crop=1080:1080:420:0,");
        Assert.True(Difference(exported, expected) * 4 < Difference(exported, centered));
    }
}
