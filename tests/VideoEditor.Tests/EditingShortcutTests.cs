using VideoEditor.Core.Editing;
using VideoEditor.Core.Models;
using VideoEditor.Core.Playback;

namespace VideoEditor.Tests;

/// <summary>UIUX-PLAN-2 階段 3：自動捲動、刪除並補齊、鍵盤選取片段、新快捷鍵。</summary>
public class EditingShortcutTests
{
    private static MediaInfo Video(string path) => new()
    {
        Path = path, Duration = 20, HasVideo = true, HasAudio = true,
        DisplayWidth = 1920, DisplayHeight = 1080, FrameRate = new Rational(30, 1), PixelFormat = "yuv420p",
    };

    private static MediaInfo Music() => new() { Path = "m.m4a", Duration = 30, HasAudio = true };

    /// <summary>影片軌 a 0–4、b 4–6、（空白 6–8）、c 8–10；配樂 song 0–8。</summary>
    private static (Timeline Timeline, TimelineClip A, TimelineClip B, TimelineClip C, TimelineClip Song) Setup()
    {
        var timeline = new Timeline();
        var a = timeline.Append(Video("a.mp4"), 0, 4);
        var b = timeline.Append(Video("b.mp4"), 0, 2);
        var c = timeline.Append(Video("c.mp4"), 0, 2);
        timeline.Move(c, 8);
        var song = timeline.Append(Music(), 0, 8);
        return (timeline, a, b, c, song);
    }

    [Theory]
    [InlineData(500, 0)]       // 中間不捲
    [InlineData(100, -1000)]   // 左邊緣（含）以外用最快速度
    [InlineData(120, -500)]    // 左邊 40 像素的一半
    [InlineData(139.9, -2.5)]  // 剛進入邊緣，很慢
    [InlineData(900, 1000)]
    [InlineData(1200, 1000)]   // 超出右邊緣
    [InlineData(880, 500)]
    public void AutoScrollGetsFasterNearTheEdge(double x, double expected) =>
        Assert.Equal(expected, TimelineDrag.AutoScrollSpeed(x, 100, 900, 40, 1000), 6);

    [Fact]
    public void NoAutoScrollWhenTheViewIsTooNarrow() =>
        Assert.Equal(0, TimelineDrag.AutoScrollSpeed(10, 0, 60, 40, 1000));

    [Fact]
    public void RemoveAndCloseMovesLaterClipsBackKeepingGaps()
    {
        var (timeline, a, b, c, song) = Setup();

        Assert.Equal(2, timeline.RemoveAndClose(a));

        Assert.Equal([b, c], timeline.VideoClips);
        Assert.Equal(0, b.Start, 6);
        Assert.Equal(4, c.Start, 6);   // 空白 2 秒維持
        Assert.Equal(0, song.Start, 6); // 配樂不動
    }

    [Fact]
    public void RemoveAndCloseTheLastClipMovesNothing()
    {
        var (timeline, a, b, c, _) = Setup();
        Assert.Equal(0, timeline.RemoveAndClose(c));
        Assert.Equal((0.0, 4.0), (a.Start, b.Start));
    }

    [Fact]
    public void RemoveAndCloseOnTheMusicTrackLeavesVideoAlone()
    {
        var (timeline, a, _, _, song) = Setup();
        var second = timeline.Append(Music(), 0, 3);
        timeline.RemoveAndClose(song);
        Assert.Equal(0, second.Start, 6);
        Assert.Equal(0, a.Start, 6);
    }

    [Fact]
    public void RemoveAndCloseCanBeUndone()
    {
        var (timeline, a, b, c, _) = Setup();
        var history = new TimelineHistory(timeline);
        var before = history.Capture();
        timeline.RemoveAndClose(b);
        history.Commit(before, "刪除並補齊");

        history.Undo();

        Assert.Equal([a, b, c], timeline.VideoClips);
        Assert.Equal((0.0, 4.0, 8.0), (a.Start, b.Start, c.Start));
    }

    [Fact]
    public void AdjacentMovesAlongTheSameTrack()
    {
        var (timeline, a, b, c, _) = Setup();
        Assert.Same(c, TimelineNavigation.Adjacent(timeline.VideoClips, b, 0, 1));
        Assert.Same(a, TimelineNavigation.Adjacent(timeline.VideoClips, b, 0, -1));
        Assert.Null(TimelineNavigation.Adjacent(timeline.VideoClips, a, 0, -1));
        Assert.Null(TimelineNavigation.Adjacent(timeline.VideoClips, c, 0, 1));
    }

    [Theory]
    [InlineData(5, 1, "b.mp4")]   // 播放頭在 b 上
    [InlineData(7, 1, "c.mp4")]   // 在空白處往後
    [InlineData(7, -1, "b.mp4")]  // 在空白處往前
    [InlineData(11, 1, null)]     // 最後一段之後往後
    public void AdjacentWithoutSelectionStartsFromThePlayhead(double playhead, int direction, string? expected)
    {
        var (timeline, _, _, _, _) = Setup();
        Assert.Equal(expected, TimelineNavigation.Adjacent(timeline.VideoClips, null, playhead, direction)?.Media.Path);
    }

    [Fact]
    public void AdjacentOnAnEmptyTrackIsNull() =>
        Assert.Null(TimelineNavigation.Adjacent([], null, 0, 1));

    [Theory]
    [InlineData(PreviewKey.Home, PreviewCommand.GoToStart)]
    [InlineData(PreviewKey.End, PreviewCommand.GoToEnd)]
    [InlineData(PreviewKey.E, PreviewCommand.AddToTimeline)]
    public void NewKeysMapToCommands(PreviewKey key, PreviewCommand command) =>
        Assert.Equal(command, PreviewKeys.FromKey(key));
}
