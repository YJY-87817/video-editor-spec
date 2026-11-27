using VideoEditor.Core.Editing;
using VideoEditor.Core.Models;
using VideoEditor.Core.Playback;

namespace VideoEditor.Tests;

public class ClipDescriptionTests
{
    private static Func<double, double, string> Format(TimeDisplay display) => (seconds, fps) => Timecode.Display(seconds, fps, display);

    private static MediaInfo Video() => new()
    {
        Path = @"/clips/trip.mp4", Duration = 20, HasVideo = true, HasAudio = true,
        DisplayWidth = 1920, DisplayHeight = 1080, FrameRate = new Rational(30, 1), PixelFormat = "yuv420p",
    };

    [Fact]
    public void PlainVideoClipShowsPositionLengthAndSource()
    {
        var timeline = new Timeline();
        var clip = timeline.Append(Video(), 1.5, 4.5);

        var text = ClipDescription.Describe(clip, 30, Format(TimeDisplay.Timecode));

        Assert.Equal(string.Join('\n',
            "trip.mp4",
            "時間軸：00:00:00:00 – 00:00:03:00",
            "長度：00:00:03:00",
            "來源：00:00:01:15 – 00:00:04:15"), text);
    }

    [Fact]
    public void ChangedSettingsAreListed()
    {
        var timeline = new Timeline();
        var clip = timeline.Append(Video(), 0, 2);
        clip.Volume = 1.5;
        timeline.SetReversed(clip, true);
        timeline.SetTransform(clip, clip.Transform.Rotate(90));

        var text = ClipDescription.Describe(clip, 30, Format(TimeDisplay.Decimal));

        Assert.Contains("音量：150%（+3.5 dB）", text);
        Assert.Contains("倒放", text);
        Assert.Contains("旋轉鏡像：", text);
        Assert.Contains("長度：00:00:02.00", text);
    }

    [Fact]
    public void MusicClipIsMarkedAndUnchangedVolumeIsNotListed()
    {
        var timeline = new Timeline();
        var clip = timeline.Append(new MediaInfo { Path = "/a/song.m4a", Duration = 30, HasAudio = true }, 0, 10);

        var text = ClipDescription.Describe(clip, 30, Format(TimeDisplay.Timecode));

        Assert.StartsWith("song.m4a（配樂）", text);
        Assert.DoesNotContain("音量", text);
    }

    [Fact]
    public void StillImageHasNoSourceRange()
    {
        var timeline = new Timeline();
        var clip = timeline.Append(new MediaInfo
        {
            Path = "/a/photo.png", Duration = Timeline.MaxStillDuration, HasVideo = true, IsStill = true,
            DisplayWidth = 800, DisplayHeight = 600, FrameRate = new Rational(0, 1),
        }, 0, Timeline.StillClipDuration);

        var text = ClipDescription.Describe(clip, 30, Format(TimeDisplay.Timecode));

        Assert.StartsWith("photo.png（圖片）", text);
        Assert.DoesNotContain("來源", text);
    }
}
