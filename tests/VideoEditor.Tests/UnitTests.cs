using VideoEditor.Core.Export;
using VideoEditor.Core.Ffmpeg;
using VideoEditor.Core.Models;
using VideoEditor.Core.Timeline;
using TimelineModel = VideoEditor.Core.Timeline.Timeline;

namespace VideoEditor.Tests;

public class OutputSizeTests
{
    [Theory]
    [InlineData(1920, 1080, 16, 9, 1920, 1080)]
    [InlineData(1920, 1080, 9, 16, 1080, 1920)]
    [InlineData(1920, 1080, 1, 1, 1920, 1920)]
    [InlineData(1920, 1080, 4, 5, 1536, 1920)]
    [InlineData(1080, 1920, 16, 9, 1920, 1080)]
    [InlineData(3840, 2160, 4, 5, 3072, 3840)]
    [InlineData(1281, 721, 16, 9, 1282, 722)]
    public void FollowsLongSideRule(int w, int h, int rw, int rh, int ew, int eh)
    {
        var size = OutputSize.Compute(w, h, new AspectRatio(rw, rh));
        Assert.Equal(new Size2(ew, eh), size);
        Assert.Equal(0, size.Width % 2);
        Assert.Equal(0, size.Height % 2);
    }

    [Theory]
    [InlineData(1920, 1080, 1920, 1080)]
    [InlineData(1080, 1920, 1080, 1920)]
    [InlineData(1440, 1080, 1440, 1080)]
    [InlineData(1281, 721, 1282, 722)]
    public void OriginalKeepsSourceSize(int w, int h, int ew, int eh) =>
        Assert.Equal(new Size2(ew, eh), OutputSize.Compute(w, h, AspectRatio.Original));
}

public class PreviewLayoutTests
{
    [Fact]
    public void FillShowsWholeSourceWithFrameInside()
    {
        var layout = PreviewLayout.Compute(1600, 900, new Size2(1920, 1080), new Size2(1080, 1920), FitMode.Fill);
        Assert.Equal(new Rect2(0, 0, 1600, 900), layout.Source);
        Assert.Equal(900, layout.Frame.Height, 3);
        Assert.Equal(506.25, layout.Frame.Width, 3);
        Assert.Equal(546.875, layout.Frame.X, 3);
    }

    [Fact]
    public void PadKeepsSourceInsideFrame()
    {
        var layout = PreviewLayout.Compute(1600, 900, new Size2(1920, 1080), new Size2(1080, 1920), FitMode.Pad);
        Assert.Equal(506.25, layout.Frame.Width, 3);
        Assert.Equal(506.25, layout.Source.Width, 3);
        Assert.Equal(284.765625, layout.Source.Height, 3);
        Assert.Equal((900 - 284.765625) / 2, layout.Source.Y, 3);
    }
}

public class VolumeCurveTests
{
    [Theory]
    [InlineData(1.0, 100)]
    [InlineData(0.0, 0)]
    [InlineData(0.125, 50)]
    [InlineData(8.0, 126)] // 超過上限時以 2 倍計
    public void IsCubeRootOfGain(double gain, int expected) => Assert.Equal(expected, VolumeCurve.ToVlcVolume(gain));
}

public class FrameMathTests
{
    [Theory]
    [InlineData(0, 30, 0)]
    [InlineData(1.0 / 30, 30, 1)]
    [InlineData(1.49, 30, 45)]
    public void FrameIndexRounds(double time, double fps, long expected) =>
        Assert.Equal(expected, FrameExtractor.FrameIndex(time, fps));

    [Fact]
    public void SeekTimeIsJustBeforeFrame()
    {
        Assert.Equal(0, FrameExtractor.FrameSeekTime(0, 30));
        Assert.InRange(FrameExtractor.FrameSeekTime(30, 30), 0.99, 1.0 - 1e-9);
    }

    [Fact]
    public void PreviewSizeLimitsLongSide()
    {
        Assert.Equal(new Size2(1280, 720), FrameExtractor.PreviewSize(3840, 2160, 1280));
        Assert.Equal(new Size2(720, 1280), FrameExtractor.PreviewSize(1080, 1920, 1280));
        Assert.Equal(new Size2(640, 360), FrameExtractor.PreviewSize(640, 360, 1280));
    }
}

public class LruCacheTests
{
    [Fact]
    public void DropsLeastRecentlyUsed()
    {
        var cache = new LruCache<int, string>(2);
        cache.Add(1, "a");
        cache.Add(2, "b");
        Assert.True(cache.TryGet(1, out _)); // 1 變成最近使用
        cache.Add(3, "c");
        Assert.False(cache.TryGet(2, out _));
        Assert.True(cache.TryGet(1, out var value));
        Assert.Equal("a", value);
        Assert.True(cache.TryGet(3, out _));
    }

    [Fact]
    public void ReplacingKeyDoesNotGrow()
    {
        var cache = new LruCache<int, string>(2);
        cache.Add(1, "a");
        cache.Add(1, "b");
        cache.Add(2, "c");
        Assert.True(cache.TryGet(1, out var value));
        Assert.Equal("b", value);
        Assert.True(cache.TryGet(2, out _));
    }
}

public class TimelineTests
{
    private static MediaInfo Media(double duration = 10) => new()
    {
        Path = "a.mp4", Duration = duration, HasVideo = true, HasAudio = true,
        DisplayWidth = 1920, DisplayHeight = 1080, FrameRate = new Rational(30, 1), PixelFormat = "yuv420p",
    };

    [Fact]
    public void AppendPlacesClipsInOrder()
    {
        var t = new TimelineModel();
        var a = t.Append(Media(), 1, 3);
        var b = t.Append(Media(), 5, 9);
        Assert.Equal(0, a.Start);
        Assert.Equal(2, b.Start);
        Assert.Equal(6, t.Duration);
    }

    [Fact]
    public void MoveCannotOverlap()
    {
        var t = new TimelineModel();
        var a = t.Append(Media(), 0, 2);
        var b = t.Append(Media(), 0, 2);
        t.Move(b, 1); // 與 a 重疊，應貼到 a 的右邊
        Assert.Equal(2, b.Start);
        t.Move(b, 5);
        Assert.Equal(5, b.Start);
        t.Move(a, 4.5); // 目標與 b 重疊，應找最近的空位
        Assert.Equal(3, a.Start);
        Assert.Equal(a, t.VideoClips[0]);
    }

    [Fact]
    public void MoveKeepsListSortedAndAllowsReordering()
    {
        var t = new TimelineModel();
        var a = t.Append(Media(), 0, 2);
        var b = t.Append(Media(), 0, 2);
        t.Move(a, 10);
        Assert.Equal(b, t.VideoClips[0]);
        Assert.Equal(a, t.VideoClips[1]);
    }

    [Fact]
    public void TrimStartRespectsSourceAndNeighbours()
    {
        var t = new TimelineModel();
        var a = t.Append(Media(), 0, 2);
        var b = t.Append(Media(), 3, 5);
        t.TrimStart(b, 2.5);
        Assert.Equal(2.5, b.Start, 6);
        Assert.Equal(3.5, b.SourceIn, 6);
        Assert.Equal(4, b.End, 6);
        t.TrimStart(b, 0); // 受左鄰限制
        Assert.Equal(a.End, b.Start, 6);
        Assert.Equal(3, b.SourceIn, 6);
        t.TrimStart(b, 100); // 受最短長度限制
        Assert.Equal(TimelineModel.MinClipDuration, b.Duration, 6);
    }

    [Fact]
    public void TrimStartCannotPassSourceBeginning()
    {
        var t = new TimelineModel();
        var a = t.Append(Media(), 1, 3);
        t.Move(a, 5);
        t.TrimStart(a, 0);
        Assert.Equal(4, a.Start, 6);
        Assert.Equal(0, a.SourceIn, 6);
    }

    [Fact]
    public void TrimEndRespectsSourceAndNeighbours()
    {
        var t = new TimelineModel();
        var a = t.Append(Media(4), 1, 2);
        t.TrimEnd(a, 100); // 受來源長度限制
        Assert.Equal(4, a.SourceOut, 6);
        var b = t.Append(Media(), 0, 1);
        t.Move(b, 5);
        t.TrimEnd(a, 2);
        Assert.Equal(2, a.End, 6);
        Assert.Equal(3, a.SourceOut, 6);
    }

    [Fact]
    public void SwapKeepsGapAndOuterClips()
    {
        var t = new TimelineModel();
        var a = t.Append(Media(), 0, 1);
        var b = t.Append(Media(), 0, 3);
        var c = t.Append(Media(), 0, 1);
        t.Move(c, 10);
        t.Move(b, 2); // a:0-1，空白 1-2，b:2-5
        Assert.True(t.Swap(b, -1));
        Assert.Equal(0, b.Start, 6);
        Assert.Equal(4, a.Start, 6);
        Assert.Equal(5, a.End, 6);
        Assert.Equal(10, c.Start, 6);
        Assert.False(t.Swap(b, -1));
    }

    [Fact]
    public void ClipAtFindsAndMapsTime()
    {
        var t = new TimelineModel();
        var a = t.Append(Media(), 2, 4);
        Assert.Equal(a, t.VideoClipAt(1));
        Assert.Equal(3, a.ToSourceTime(1), 6);
        Assert.Null(t.VideoClipAt(2));
    }

    [Fact]
    public void AudioOnlyMediaGoesToMusicTrack()
    {
        var t = new TimelineModel();
        var music = new MediaInfo { Path = "m.mp3", Duration = 5, HasAudio = true };
        Assert.Equal(TrackKind.Music, t.Append(music, 0, 5).Track);
        Assert.Throws<ArgumentException>(() => t.Append(music, 0, 5, TrackKind.Video));
    }
}

public class ProbeParseTests
{
    private const string Rotated = """
        {"streams":[{"codec_type":"video","codec_name":"h264","width":1920,"height":1080,"pix_fmt":"yuv420p",
          "r_frame_rate":"30/1","avg_frame_rate":"30/1","side_data_list":[{"rotation":-90}]},
          {"codec_type":"audio","codec_name":"aac","bit_rate":"192000","sample_rate":"48000","channels":2}],
         "format":{"duration":"10.5","bit_rate":"8000000"}}
        """;

    [Fact]
    public void SwapsDimensionsWhenRotated()
    {
        var info = MediaProbe.Parse(Rotated, "x.mp4");
        Assert.Equal(1080, info.DisplayWidth);
        Assert.Equal(1920, info.DisplayHeight);
        Assert.Equal(10.5, info.Duration);
        Assert.Equal(192000, info.AudioBitRate);
        Assert.Equal("9:16", info.AspectLabel);
    }

    [Fact]
    public void DetectsVariableFrameRate()
    {
        const string json = """
            {"streams":[{"codec_type":"video","width":100,"height":100,"pix_fmt":"yuv420p10le",
              "r_frame_rate":"60/1","avg_frame_rate":"29/1","color_transfer":"smpte2084"}],"format":{"duration":"1"}}
            """;
        var info = MediaProbe.Parse(json, "x.mov");
        Assert.True(info.IsVariableFrameRate);
        Assert.Equal(29, info.FrameRate.Value);
        Assert.Equal(10, info.BitDepth);
        Assert.True(info.IsHdr);
    }

    [Fact]
    public void AttachedPictureIsNotVideo()
    {
        const string json = """
            {"streams":[{"codec_type":"video","width":100,"height":100,"disposition":{"attached_pic":1}},
              {"codec_type":"audio","codec_name":"mp3","sample_rate":"44100","channels":2}],"format":{"duration":"3"}}
            """;
        var info = MediaProbe.Parse(json, "x.mp3");
        Assert.False(info.HasVideo);
        Assert.True(info.HasAudio);
    }

    [Theory]
    [InlineData("\"0.040000\"", "太短")] // ffprobe 讀靜態圖片的結果
    [InlineData("\"0.05\"", "太短")]
    [InlineData("\"N/A\"", "無法讀取檔案長度")]
    public void RejectsFilesTooShortForTheTimeline(string duration, string expected)
    {
        var json = $$"""
            {"streams":[{"codec_type":"video","codec_name":"png","width":64,"height":64,"r_frame_rate":"25/1","avg_frame_rate":"25/1"}],
             "format":{"duration":{{duration}}}}
            """;
        var error = Assert.Throws<InvalidDataException>(() => MediaProbe.Parse(json, "still.png"));
        Assert.Contains(expected, error.Message);
    }

    [Fact]
    public void AcceptsTheShortestClipTheTimelineAllows()
    {
        const string json = """
            {"streams":[{"codec_type":"audio","codec_name":"aac","sample_rate":"48000","channels":2}],"format":{"duration":"0.1"}}
            """;
        var info = MediaProbe.Parse(json, "short.m4a");
        Assert.Equal(0.1, info.Duration);
        Assert.Equal(TrackKind.Music, new TimelineModel().Append(info, 0, info.Duration).Track);
    }

    [Fact]
    public void RejectsFilesWithoutStreams() =>
        Assert.Throws<InvalidDataException>(() => MediaProbe.Parse("""{"streams":[],"format":{}}""", "x"));
}

public class ExportPlannerTests
{
    private static MediaInfo Media(string pixelFormat = "yuv420p", int bitDepth = 8, string? transfer = null, long audioBitRate = 128000) => new()
    {
        Path = "a.mp4", Duration = 10, HasVideo = true, HasAudio = true, DisplayWidth = 1920, DisplayHeight = 1080,
        FrameRate = new Rational(30, 1), PixelFormat = pixelFormat, BitDepth = bitDepth, ColorTransfer = transfer,
        AudioBitRate = audioBitRate,
    };

    private static string Graph(ExportPlan plan) =>
        plan.Arguments[plan.Arguments.ToList().IndexOf("-filter_complex") + 1];

    [Fact]
    public void Uses8BitH264AndCrf18ByDefault()
    {
        var t = new TimelineModel();
        t.Append(Media(), 0, 2);
        var plan = ExportPlanner.Build(t, new OutputSettings(AspectRatio.R16x9, FitMode.Fill), "o.mp4");
        Assert.Equal("libx264", plan.VideoCodec);
        Assert.Equal("yuv420p", plan.PixelFormat);
        var crf = plan.Arguments.ToList().IndexOf("-crf");
        Assert.Equal("18", plan.Arguments[crf + 1]);
        Assert.DoesNotContain("fps=", Graph(plan));
    }

    [Fact]
    public void Uses265For10BitAndHdr()
    {
        Assert.Equal(("libx265", "yuv420p10le"), ExportPlanner.ChooseVideoFormat(Media("yuv420p10le", 10)));
        Assert.Equal(("libx265", "yuv422p10le"), ExportPlanner.ChooseVideoFormat(Media("yuv422p10le", 10)));
        Assert.Equal(("libx264", "yuv444p"), ExportPlanner.ChooseVideoFormat(Media("yuv444p")));
    }

    [Theory]
    [InlineData(128000, 192000)]
    [InlineData(320000, 320000)]
    [InlineData(1536000, 512000)]
    public void AudioBitRateIsNotBelowSourceUpToLimit(long source, long expected)
    {
        var t = new TimelineModel();
        t.Append(Media(audioBitRate: source), 0, 2);
        Assert.Equal(expected, ExportPlanner.AudioBitRate(t));
    }

    [Fact]
    public void GapsBecomeBlackAndSilence()
    {
        var t = new TimelineModel();
        t.Append(Media(), 0, 1);
        var b = t.Append(Media(), 0, 1);
        t.Move(b, 3);
        var graph = Graph(ExportPlanner.Build(t, new OutputSettings(AspectRatio.R16x9, FitMode.Pad), "o.mp4"));
        Assert.Contains("color=c=black:s=1920x1080:r=30/1:d=2", graph);
        Assert.Contains("anullsrc=r=48000:cl=stereo:d=2", graph);
        Assert.Contains("concat=n=3:v=1:a=1", graph);
    }

    [Fact]
    public void DifferentRatesAreConformedAndWarned()
    {
        var t = new TimelineModel();
        t.Append(Media(), 0, 1);
        t.Append(Media() with { Path = "b.mp4", FrameRate = new Rational(25, 1) }, 0, 1);
        var plan = ExportPlanner.Build(t, new OutputSettings(AspectRatio.R16x9, FitMode.Pad), "o.mp4");
        Assert.Contains("fps=30/1,", Graph(plan));
        Assert.Contains(plan.Warnings, w => w.Contains("影格率"));
    }

    [Fact]
    public void EmptyTimelineCannotExport() =>
        Assert.Throws<InvalidOperationException>(() =>
            ExportPlanner.Build(new TimelineModel(), new OutputSettings(AspectRatio.R16x9, FitMode.Pad), "o.mp4"));

    [Theory]
    [InlineData("out_time_us=5000000", 10, 0.5)]
    [InlineData("out_time_us=99000000", 10, 1.0)]
    public void ProgressIsParsed(string line, double duration, double expected) =>
        Assert.Equal(expected, ExportService.ParseProgress(line, duration));

    [Theory]
    [InlineData("frame=10")]
    [InlineData("out_time_us=N/A")]
    public void IrrelevantProgressLinesAreIgnored(string line) =>
        Assert.Null(ExportService.ParseProgress(line, 10));
}
