using VideoEditor.Core.Editing;
using VideoEditor.Core.Models;

namespace VideoEditor.Tests;

public class ThumbnailPlanTests
{
    private static MediaInfo Video(double fps = 30) => new()
    {
        Path = "a.mp4", Duration = 60, HasVideo = true, HasAudio = true,
        DisplayWidth = 1920, DisplayHeight = 1080, FrameRate = new Rational((int)Math.Round(fps * 1000), 1000), PixelFormat = "yuv420p",
    };

    private static TimelineClip Clip(double sourceIn = 10, double sourceOut = 30, bool reversed = false)
    {
        var timeline = new Timeline();
        var clip = timeline.Append(Video(), sourceIn, sourceOut);
        if (reversed) timeline.SetReversed(clip, true);
        return clip;
    }

    [Theory]
    [InlineData(200, 96, 0.5)]
    [InlineData(96, 96, 1)]
    [InlineData(40, 96, 5)]
    [InlineData(2, 96, 60)]
    [InlineData(0.01, 96, 3600)]
    public void StepIsTheSmallestTidyIntervalThatFitsAThumbnail(double pixelsPerSecond, double thumbWidth, double expected) =>
        Assert.Equal(expected, ThumbnailPlan.Step(pixelsPerSecond, thumbWidth));

    [Fact]
    public void ZoomedOutClipGetsOneThumbnailAtItsStart()
    {
        var clip = Clip(); // 20 秒
        var slots = ThumbnailPlan.Slots(clip, 4, 96, 0, 1000); // 80 像素寬，間隔 30 秒

        var slot = Assert.Single(slots);
        Assert.Equal(0, slot.TimelineTime, 6);
        Assert.Equal(10, slot.SourceTime, 6);
    }

    [Fact]
    public void ZoomedInClipGetsThumbnailsAlongItsLength()
    {
        var clip = Clip(); // 20 秒、每秒 100 像素 → 間隔 1 秒，共 20 張
        var slots = ThumbnailPlan.Slots(clip, 100, 96, 0, 1000);

        Assert.Equal(20, slots.Count);
        Assert.Equal(5, slots[5].TimelineTime, 6);
        Assert.Equal(15, slots[5].SourceTime, 6);
    }

    [Fact]
    public void OnlyVisibleThumbnailsAreListed()
    {
        var clip = Clip();
        var slots = ThumbnailPlan.Slots(clip, 100, 96, 5.5, 8.2);

        // 5 秒那張（畫到 5.96 秒）還看得到一部分；8 秒開始的也看得到。
        Assert.Equal([5.0, 6.0, 7.0, 8.0], slots.Select(s => Math.Round(s.TimelineTime, 6)));
    }

    [Fact]
    public void NarrowClipGetsNoThumbnail() =>
        Assert.Empty(ThumbnailPlan.Slots(Clip(10, 10.2), 100, 96, 0, 1000)); // 20 像素

    [Fact]
    public void ReversedClipStartsFromTheLastFrame()
    {
        var clip = Clip(10, 30, reversed: true);
        var slots = ThumbnailPlan.Slots(clip, 100, 96, 0, 1000);

        Assert.Equal(30 - 1 / 30.0, slots[0].SourceTime, 6); // 出點前最後一格
        Assert.True(slots[1].SourceTime < slots[0].SourceTime);
    }

    [Fact]
    public void SourceTimesAreOnFrames()
    {
        var timeline = new Timeline();
        var clip = timeline.Append(Video(), 10.013, 30);
        foreach (var slot in ThumbnailPlan.Slots(clip, 100, 96, 0, 1000))
        {
            var frames = slot.SourceTime * 30;
            Assert.Equal(Math.Round(frames), frames, 6);
            Assert.True(slot.SourceTime >= clip.SourceIn - 1 / 30.0 && slot.SourceTime < clip.SourceOut);
        }
    }

    [Fact]
    public void StillImageAlwaysUsesTimeZero()
    {
        var timeline = new Timeline();
        var clip = timeline.Append(new MediaInfo
        {
            Path = "p.png", Duration = Timeline.MaxStillDuration, HasVideo = true, IsStill = true,
            DisplayWidth = 800, DisplayHeight = 600, FrameRate = new Rational(0, 1),
        }, 0, 5);

        Assert.All(ThumbnailPlan.Slots(clip, 100, 96, 0, 1000), slot => Assert.Equal(0, slot.SourceTime));
    }
}
