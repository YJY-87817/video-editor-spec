using VideoEditor.Core.Editing;
using VideoEditor.Core.Models;

namespace VideoEditor.Tests;

/// <summary>把素材拖到時間軸：依放在片段的前半或後半決定插在前面或後面，後面的片段往後移（UIUX-PLAN 決定 2）。</summary>
public class InsertTests
{
    private static MediaInfo Video(string path = "a.mp4", double duration = 10) => new()
    {
        Path = path, Duration = duration, HasVideo = true, HasAudio = true,
        DisplayWidth = 1920, DisplayHeight = 1080, FrameRate = new Rational(30, 1), PixelFormat = "yuv420p",
    };

    private static MediaInfo Music() => new() { Path = "m.m4a", Duration = 30, HasAudio = true };

    private static MediaInfo Still() => new()
    {
        Path = "p.png", Duration = Timeline.MaxStillDuration, HasVideo = true, IsStill = true,
        DisplayWidth = 800, DisplayHeight = 600, FrameRate = new Rational(0, 1),
    };

    /// <summary>影片軌：a 0–4、b 4–6、（空白 6–8）、c 8–10。配樂軌：song 0–8。</summary>
    private static (Timeline Timeline, TimelineEditor Editor, TimelineClip A, TimelineClip B, TimelineClip C, TimelineClip Song) Setup()
    {
        var timeline = new Timeline();
        var a = timeline.Append(Video("a.mp4"), 0, 4);
        var b = timeline.Append(Video("b.mp4"), 0, 2);
        var c = timeline.Append(Video("c.mp4"), 0, 2);
        timeline.Move(c, 8);
        var song = timeline.Append(Music(), 0, 8);
        return (timeline, new TimelineEditor(timeline), a, b, c, song);
    }

    private static InOutPoints Points(double inPoint, double outPoint)
    {
        var points = new InOutPoints();
        points.SetIn(inPoint);
        points.SetOut(outPoint);
        return points;
    }

    [Theory]
    [InlineData(0.0, 0.0)]   // a 的開頭
    [InlineData(1.9, 0.0)]   // a 的前半 → a 前面
    [InlineData(2.0, 4.0)]   // a 的正中間算後面
    [InlineData(3.9, 4.0)]   // a 的後半 → a 後面
    [InlineData(4.5, 4.0)]   // b 的前半 → b 前面（= a 後面）
    [InlineData(5.5, 6.0)]   // b 的後半 → b 後面
    [InlineData(7.0, 7.0)]   // 空白處：放下的位置
    [InlineData(12.0, 12.0)] // 最後一段之後
    [InlineData(-3.0, 0.0)]  // 時間軸開頭之前
    public void TargetDependsOnWhichHalfOfTheClip(double drop, double expected)
    {
        var (_, editor, _, _, _, _) = Setup();
        Assert.Equal(expected, editor.InsertTarget(TrackKind.Video, drop), 6);
    }

    [Fact]
    public void TargetOnTheMusicTrackLooksOnlyAtMusicClips()
    {
        var (_, editor, _, _, _, _) = Setup();
        Assert.Equal(8, editor.InsertTarget(TrackKind.Music, 5), 6); // song 0–8 的後半
        Assert.Equal(9, editor.InsertTarget(TrackKind.Music, 9), 6);
    }

    [Fact]
    public void InsertingBeforeAClipPushesItAndEverythingAfterKeepingGaps()
    {
        var (timeline, editor, a, b, c, song) = Setup();

        var result = editor.AddAt(Video("new.mp4"), Points(1, 4), 1); // a 的前半，長 3 秒

        Assert.NotNull(result.Clip);
        Assert.Equal(0, result.Clip!.Start, 6);
        Assert.Equal(3, result.Shift, 6);
        Assert.Equal(3, result.ShiftedCount);
        Assert.Equal([result.Clip, a, b, c], timeline.VideoClips);
        Assert.Equal(3, a.Start, 6);
        Assert.Equal(7, b.Start, 6);
        Assert.Equal(11, c.Start, 6); // 空白 2 秒維持
        Assert.Equal(0, song.Start, 6); // 配樂不動
    }

    [Fact]
    public void InsertingAfterAClipPushesOnlyTheClipsAfterIt()
    {
        var (timeline, editor, a, b, c, _) = Setup();

        var result = editor.AddAt(Video("new.mp4"), Points(0, 1), 3); // a 的後半

        Assert.Equal(4, result.Clip!.Start, 6);
        Assert.Equal(0, a.Start, 6);
        Assert.Equal(5, b.Start, 6);
        Assert.Equal(9, c.Start, 6);
        Assert.Equal([a, result.Clip, b, c], timeline.VideoClips);
    }

    [Fact]
    public void GapThatFitsLeavesEveryoneInPlace()
    {
        var (_, editor, a, b, c, _) = Setup();

        var result = editor.AddAt(Video("new.mp4"), Points(0, 1.5), 6.2); // 空白 6–8，放 6.2–7.7

        Assert.Equal(6.2, result.Clip!.Start, 6);
        Assert.Equal(0, result.Shift);
        Assert.Equal(0, result.ShiftedCount);
        Assert.Equal((0.0, 4.0, 8.0), (a.Start, b.Start, c.Start));
    }

    [Fact]
    public void GapThatDoesNotFitPushesOnlyAsMuchAsNeeded()
    {
        var (_, editor, _, b, c, _) = Setup();

        var result = editor.AddAt(Video("new.mp4"), Points(0, 3), 7); // 7–10，c 原本在 8

        Assert.Equal(7, result.Clip!.Start, 6);
        Assert.Equal(2, result.Shift, 6);
        Assert.Equal(1, result.ShiftedCount);
        Assert.Equal(10, c.Start, 6);
        Assert.Equal(4, b.Start, 6);
    }

    [Fact]
    public void InsertingVideoLeavesTheMusicTrackAlone()
    {
        var (_, editor, _, _, _, song) = Setup();
        editor.AddAt(Video("new.mp4"), Points(0, 5), 0);
        Assert.Equal((0.0, 8.0), (song.Start, song.End));
    }

    [Fact]
    public void AudioOnlyMediaGoesToTheMusicTrackAndPushesOnlyMusic()
    {
        var (timeline, editor, a, _, _, song) = Setup();

        var result = editor.AddAt(Music(), Points(0, 2), 1); // song 的前半

        Assert.Equal(TrackKind.Music, result.Clip!.Track);
        Assert.Equal(0, result.Clip.Start, 6);
        Assert.Equal(2, song.Start, 6);
        Assert.Equal(0, a.Start, 6);
        Assert.Equal(2, timeline.MusicClips.Count);
    }

    [Fact]
    public void StillImageIsFiveSecondsAndIgnoresMarks()
    {
        var (_, editor, _, _, _, _) = Setup();
        var result = editor.AddAt(Still(), Points(1, 2), 20);
        Assert.Equal(Timeline.StillClipDuration, result.Clip!.Duration, 6);
        Assert.Equal(20, result.Clip.Start, 6);
    }

    [Fact]
    public void MarksAreUsedAndThenCleared()
    {
        var (_, editor, _, _, _, _) = Setup();
        var points = Points(2, 5);

        var result = editor.AddAt(Video("new.mp4"), points, 20);

        Assert.Equal((2.0, 5.0), (result.Clip!.SourceIn, result.Clip.SourceOut));
        Assert.Null(points.In);
        Assert.Null(points.Out);
    }

    [Fact]
    public void FailureIsReportedAndNothingMoves()
    {
        var (timeline, editor, a, _, _, _) = Setup();
        var silent = new MediaInfo { Path = "x.bin", Duration = 5 }; // 沒有畫面也沒有聲音

        var result = editor.AddAt(silent, new InOutPoints(), 1);

        Assert.Null(result.Clip);
        Assert.False(string.IsNullOrEmpty(result.Error));
        Assert.Equal(3, timeline.VideoClips.Count);
        Assert.Equal(0, a.Start);
    }

    [Fact]
    public void InsertInsideAClipIsRejected()
    {
        var (timeline, _, _, _, _, _) = Setup();
        Assert.Throws<ArgumentException>(() => timeline.Insert(Video(), 0, 1, 2));
        Assert.Equal(3, timeline.VideoClips.Count);
    }

    [Fact]
    public void InsertCanBeUndone()
    {
        var (timeline, editor, a, b, c, _) = Setup();
        var history = new TimelineHistory(timeline);
        var before = history.Capture();

        editor.AddAt(Video("new.mp4"), Points(0, 3), 1);
        history.Commit(before, "加入片段");
        history.Undo();

        Assert.Equal([a, b, c], timeline.VideoClips);
        Assert.Equal((0.0, 4.0, 8.0), (a.Start, b.Start, c.Start));
    }
}
