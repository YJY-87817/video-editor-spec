using VideoEditor.Core.Models;
using VideoEditor.Core.Timeline;
using TimelineModel = VideoEditor.Core.Timeline.Timeline;

namespace VideoEditor.Tests;

public class InOutPointsTests
{
    private static MediaInfo Media(double duration = 10, int fps = 30) => new()
    {
        Path = "a.mp4", Duration = duration, HasVideo = true, HasAudio = true,
        DisplayWidth = 1920, DisplayHeight = 1080, FrameRate = new Rational(fps, 1), PixelFormat = "yuv420p",
    };

    [Fact]
    public void WithoutMarksCoversTheWholeMedia()
    {
        var points = new InOutPoints();
        Assert.Equal(0, points.InPoint);
        Assert.Equal(10, points.OutPoint(Media()));
    }

    [Fact]
    public void SettingInAfterOutClearsOut()
    {
        var points = new InOutPoints();
        points.SetOut(3);
        points.SetIn(5);
        Assert.Equal(5, points.In);
        Assert.Null(points.Out);
    }

    [Fact]
    public void SettingOutBeforeInClearsIn()
    {
        var points = new InOutPoints();
        points.SetIn(5);
        points.SetOut(3);
        Assert.Equal(3, points.Out);
        Assert.Null(points.In);
    }

    [Fact]
    public void InAndOutAtTheSamePositionKeepOnlyTheLastOne()
    {
        var points = new InOutPoints();
        points.SetIn(3);
        points.SetOut(3);
        Assert.Null(points.In);
        Assert.Equal(3, points.Out);
    }

    [Fact]
    public void OrderedMarksAreBothKept()
    {
        var points = new InOutPoints();
        points.SetOut(8);
        points.SetIn(2);
        Assert.Equal((2.0, 8.0), (points.In, points.Out));
        Assert.Equal(8, points.OutPoint(Media()));
    }

    [Fact]
    public void ClearRemovesBothMarks()
    {
        var points = new InOutPoints();
        points.SetIn(2);
        points.SetOut(8);
        points.Clear();
        Assert.Null(points.In);
        Assert.Null(points.Out);
    }

    [Theory]
    [InlineData(1.016, 1.0)]       // 最接近第 30 格
    [InlineData(1.02, 31 / 30.0)]  // 過了半格，進到第 31 格
    [InlineData(0, 0)]
    [InlineData(99, 10)]           // 不超過素材長度
    public void SnapsToTheDisplayedFrame(double position, double expected) =>
        Assert.Equal(expected, InOutPoints.Snap(position, Media()), 9);

    [Fact]
    public void WithoutFrameRateTheExactPositionIsKept() =>
        Assert.Equal(1.234, InOutPoints.Snap(1.234, Media() with { FrameRate = new Rational(0, 1) }));
}

public class TimelineEditorTests
{
    private static MediaInfo Media(double duration = 10) => new()
    {
        Path = "a.mp4", Duration = duration, HasVideo = true, HasAudio = true,
        DisplayWidth = 1920, DisplayHeight = 1080, FrameRate = new Rational(30, 1), PixelFormat = "yuv420p",
    };

    [Fact]
    public void AddUsesTheMarksThenClearsThem()
    {
        var timeline = new TimelineModel();
        var points = new InOutPoints();
        points.SetIn(2);
        points.SetOut(5);

        var result = new TimelineEditor(timeline).Add(Media(), points);

        Assert.Null(result.Error);
        Assert.NotNull(result.Clip);
        Assert.Equal((2.0, 5.0), (result.Clip.SourceIn, result.Clip.SourceOut));
        Assert.Same(result.Clip, Assert.Single(timeline.VideoClips));
        Assert.Null(points.In);
        Assert.Null(points.Out);
    }

    [Fact]
    public void AddWithoutMarksTakesTheWholeMedia()
    {
        var timeline = new TimelineModel();

        var result = new TimelineEditor(timeline).Add(Media(7), new InOutPoints());

        Assert.Equal((0.0, 7.0), (result.Clip!.SourceIn, result.Clip.SourceOut));
    }

    [Fact]
    public void AddTooShortReportsErrorAndKeepsTheMarks()
    {
        var timeline = new TimelineModel();
        var points = new InOutPoints();
        points.SetIn(1);
        points.SetOut(1.05);

        var result = new TimelineEditor(timeline).Add(Media(), points);

        Assert.Null(result.Clip);
        Assert.Equal("片段太短。", result.Error);
        Assert.Empty(timeline.AllClips);
        Assert.Equal((1.0, 1.05), (points.In, points.Out));
    }

    [Fact]
    public void AddMediaWithoutStreamsReportsError()
    {
        var timeline = new TimelineModel();

        var result = new TimelineEditor(timeline).Add(new MediaInfo { Path = "x.bin", Duration = 5 }, new InOutPoints());

        Assert.Null(result.Clip);
        Assert.NotNull(result.Error);
        Assert.Empty(timeline.AllClips);
    }

    [Fact]
    public void TransformOutsideTimelinePreviewSwitchesToIt()
    {
        var timeline = new TimelineModel();
        var clip = timeline.Append(Media(), 0, 4);

        var followUp = new TimelineEditor(timeline).ChangeTransform(clip, t => t.Rotate(90), timelinePreview: false, playhead: 0);

        Assert.Equal(PreviewFollowUp.ShowClipInTimeline, followUp);
        Assert.Equal(90, clip.Transform.Rotation);
    }

    [Theory]
    [InlineData(5, PreviewFollowUp.None)]
    [InlineData(4, PreviewFollowUp.None)]          // 第二段的開頭
    [InlineData(1, PreviewFollowUp.SeekToClip)]    // 在第一段，不在目標片段內
    [InlineData(8, PreviewFollowUp.SeekToClip)]    // 剛好在片段結尾，已不屬於它
    public void TransformInTimelinePreviewSeeksOnlyWhenPlayheadIsOutsideTheClip(double playhead, PreviewFollowUp expected)
    {
        var timeline = new TimelineModel();
        timeline.Append(Media(), 0, 4);
        var clip = timeline.Append(Media(), 0, 4); // 位於 4–8 秒

        var followUp = new TimelineEditor(timeline).ChangeTransform(clip, t => t.ToggleHorizontal(), timelinePreview: true, playhead);

        Assert.Equal(expected, followUp);
        Assert.True(clip.Transform.FlipHorizontal);
    }

    [Fact]
    public void TransformOnMusicClipIsRejected()
    {
        var timeline = new TimelineModel();
        var music = timeline.Append(new MediaInfo { Path = "m.m4a", Duration = 5, HasAudio = true }, 0, 5);

        Assert.Throws<ArgumentException>(() =>
            new TimelineEditor(timeline).ChangeTransform(music, t => t.Rotate(90), timelinePreview: true, playhead: 0));
    }

    [Theory]
    [InlineData(100, 1.0)]
    [InlineData(0, 0.0)]
    [InlineData(200, 2.0)]
    [InlineData(49.6, 0.5)]
    [InlineData(49.4, 0.49)]
    public void VolumePercentBecomesLinearGainInWholePercents(double percent, double expected) =>
        Assert.Equal(expected, TimelineEditor.VolumeFromPercent(percent), 9);
}
