using System.Text;
using VideoEditor.Core.Ffmpeg;
using VideoEditor.Core.Models;
using VideoEditor.Core.Playback;
using VideoEditor.Core.Editing;

namespace VideoEditor.Tests;

public class SplitTests
{
    private static MediaInfo Media(int fps = 30) => new()
    {
        Path = "a.mp4", Duration = 10, HasVideo = true, HasAudio = true,
        DisplayWidth = 1920, DisplayHeight = 1080, FrameRate = new Rational(fps, 1), PixelFormat = "yuv420p",
    };

    private static MediaInfo Music() => new() { Path = "m.m4a", Duration = 10, HasAudio = true };

    [Fact]
    public void DisplayedFrameStartsTheRightPart()
    {
        var t = new Timeline();
        var clip = t.Append(Media(), 0.5, 4.5);
        var right = t.Split(clip, 1.51);
        // 1.51 秒顯示來源 2.01 秒，最接近第 60 格（2.0 秒）。
        Assert.Equal(0.5, clip.SourceIn, 9);
        Assert.Equal(2.0, clip.SourceOut, 9);
        Assert.Equal(2.0, right.SourceIn, 9);
        Assert.Equal(4.5, right.SourceOut, 9);
        Assert.Equal(clip.End, right.Start, 9);
        Assert.Equal(4.0, right.End, 9);
        Assert.Equal([clip, right], t.VideoClips);
    }

    [Theory]
    [InlineData(false, 0.0)]
    [InlineData(false, 0.37)]
    [InlineData(true, 0.0)]
    [InlineData(true, 0.37)]
    public void PlaybackIsUnchangedBySplitting(bool reversed, double start)
    {
        var t = new Timeline();
        var clip = t.Append(Media(), 1, 4);
        t.Move(clip, start);
        if (reversed) t.SetReversed(clip, true);
        // 取每一格的中間：時間軸上第 k 格（從片段開頭算）在匯出時就是這個位置的畫面。
        // 不取格與格之間的時間，那裡的預覽本來就是四捨五入到相鄰的某一格。
        var times = Enumerable.Range(0, 90).Select(k => start + (k + 0.5) / 30).ToList();
        var before = times.Select(time => FrameMath.FrameIndex(clip.ToSourceTime(time), 30)).ToList();
        var end = clip.End;

        var right = t.Split(clip, start + 1.234);
        Assert.Equal(clip.End, right.Start, 9);
        Assert.Equal(end, right.End, 9);
        var after = times.Select(time => FrameMath.FrameIndex((time < right.Start ? clip : right).ToSourceTime(time), 30)).ToList();
        Assert.Equal(before, after);
    }

    [Fact]
    public void ReversedSplitKeepsTheFrameAtThePlayhead()
    {
        var t = new Timeline();
        var clip = t.Append(Media(), 1, 4);
        t.SetReversed(clip, true);
        var shown = FrameMath.FrameIndex(clip.ToSourceTime(1.0), 30);
        var right = t.Split(clip, 1.0);
        Assert.Equal(3.0, clip.SourceIn, 9);
        Assert.Equal(4.0, clip.SourceOut, 9);
        Assert.Equal(1.0, right.SourceIn, 9);
        Assert.Equal(3.0, right.SourceOut, 9);
        Assert.Equal(shown, FrameMath.FrameIndex(right.ToSourceTime(right.Start), 30));
    }

    [Fact]
    public void SettingsAreCopied()
    {
        var t = new Timeline();
        var clip = t.Append(Media(), 0, 4);
        clip.Volume = 1.5;
        t.SetReversed(clip, true);
        t.SetTransform(clip, FrameTransform.Identity.Rotate(90).ToggleHorizontal());
        var framing = new ClipFraming(1.5, 0.3, 0.6);
        t.SetFraming(clip, framing);
        var right = t.Split(clip, 2);
        Assert.Equal(1.5, right.Volume);
        Assert.True(right.Reversed);
        Assert.Equal(clip.Transform, right.Transform);
        Assert.Equal(framing, right.Framing);
        Assert.NotEqual(clip.Id, right.Id);
    }

    [Fact]
    public void MusicIsSplitAtTheExactTime()
    {
        var t = new Timeline();
        var clip = t.Append(Music(), 0.5, 5);
        var right = t.Split(clip, 1.2345);
        Assert.Equal(1.7345, clip.SourceOut, 9);
        Assert.Equal(1.2345, right.Start, 9);
        Assert.Equal([clip, right], t.MusicClips);
    }

    [Theory]
    [InlineData(0.05)]
    [InlineData(3.95)]
    public void TooShortPartIsRejected(double time)
    {
        var t = new Timeline();
        var clip = t.Append(Media(), 0, 4);
        Assert.Throws<ArgumentException>(() => t.Split(clip, time));
        Assert.Equal(4, clip.Duration, 9);
        Assert.Single(t.VideoClips);
    }

    [Fact]
    public void EditorExplainsWhyItCannotSplit()
    {
        var t = new Timeline();
        var editor = new TimelineEditor(t);
        var clip = t.Append(Media(), 0, 4);
        Assert.Contains("時間軸預覽", editor.Split(clip, 2, timelinePreview: false).Error);
        Assert.Contains("選取", editor.Split(null, 2, true).Error);
        Assert.Contains("不在", editor.Split(clip, 4, true).Error);
        Assert.Contains("不在", editor.Split(clip, 0, true).Error);
        Assert.Contains("0.1", editor.Split(clip, 0.05, true).Error);
        var ok = editor.Split(clip, 2, true);
        Assert.Null(ok.Error);
        Assert.Equal(2, ok.Clip!.Start, 9);
    }
}

public class EditPointsTests
{
    private static MediaInfo Media() => new()
    {
        Path = "a.mp4", Duration = 10.02, HasVideo = true, HasAudio = true,
        DisplayWidth = 1920, DisplayHeight = 1080, FrameRate = new Rational(30, 1), PixelFormat = "yuv420p",
    };

    [Fact]
    public void TimelinePointsAreVideoClipEdges()
    {
        var t = new Timeline();
        t.Append(Media(), 0, 2);
        var second = t.Append(Media(), 0, 3);
        t.Move(second, 2.5);
        t.Append(new MediaInfo { Path = "m.m4a", Duration = 9, HasAudio = true }, 0, 9);
        Assert.Equal([0, 2, 2.5, 5.5], EditPoints.Timeline(t));
    }

    [Theory]
    [InlineData(1.0, 0.0, 2.0)]
    [InlineData(2.0, 0.0, 2.5)]       // 剛好在剪輯點上：往前找前一個、往後找下一個
    [InlineData(2.0004, 0.0, 2.5)]    // Seek 後的微小誤差視為同一點
    [InlineData(0.0, null, 2.0)]
    [InlineData(5.5, 2.5, null)]
    [InlineData(9.0, 5.5, null)]
    public void PreviousAndNext(double position, double? previous, double? next)
    {
        double[] points = [0, 2, 2.5, 5.5];
        Assert.Equal(previous, EditPoints.Previous(points, position));
        Assert.Equal(next, EditPoints.Next(points, position));
    }

    [Fact]
    public void SourcePointsIncludeMarks()
    {
        var points = new InOutPoints();
        Assert.Equal([0, 10], EditPoints.Source(Media(), points));
        points.SetIn(1.5);
        points.SetOut(4);
        Assert.Equal([0, 1.5, 4, 10], EditPoints.Source(Media(), points));
    }

    [Theory]
    [InlineData("S", PreviewCommand.SplitClip)]
    [InlineData("PageUp", PreviewCommand.PreviousEditPoint)]
    [InlineData("Prior", PreviewCommand.PreviousEditPoint)]
    [InlineData("PageDown", PreviewCommand.NextEditPoint)]
    [InlineData("Next", PreviewCommand.NextEditPoint)]
    public void Keys(string key, PreviewCommand command) => Assert.Equal(command, PreviewKeys.FromKeyName(key));
}

public class ClipSnapTests
{
    private static MediaInfo Media() => new()
    {
        Path = "a.mp4", Duration = 10, HasVideo = true, HasAudio = true,
        DisplayWidth = 1920, DisplayHeight = 1080, FrameRate = new Rational(30, 1), PixelFormat = "yuv420p",
    };

    [Theory]
    [InlineData(3.05, 3.0, SnapKind.ClipEdge, 3.0)]   // 開頭貼齊 3 秒
    [InlineData(4.95, 5.0, SnapKind.ClipEdge, 7.0)]   // 結尾（+2 秒）貼齊 7 秒
    [InlineData(0.15, 0.0, SnapKind.TimelineStart, 0)]
    [InlineData(9.85, 10.0, SnapKind.Playhead, 10.0)]  // 開頭貼齊播放頭
    [InlineData(7.95, 8.0, SnapKind.Playhead, 10.0)]   // 結尾貼齊播放頭
    [InlineData(5.6, 5.6, SnapKind.None, 5.6)]
    public void MoveSnapsEitherEdge(double start, double expected, SnapKind kind, double target)
    {
        var result = TimelineSnap.SnapMove(start, 2, 10, [3.0, 7.0, 20.0], 0.2);
        Assert.Equal(expected, result.Time, 9);
        Assert.Equal(kind, result.Kind);
        Assert.Equal(target, result.Target, 9);
    }

    [Fact]
    public void MovePriorityOnTies()
    {
        // 開頭離片段邊緣與播放頭一樣近：播放頭優先。
        var tie = TimelineSnap.SnapMove(3.1, 2, 3.2, [3.0], 0.2);
        Assert.Equal(SnapKind.Playhead, tie.Kind);
        // 片段邊緣與時間軸開頭一樣近：片段邊緣優先。
        var edge = TimelineSnap.SnapMove(0.1, 2, null, [2.2], 0.2);
        Assert.Equal(SnapKind.ClipEdge, edge.Kind);
        Assert.Equal(0.2, edge.Time, 9);
        Assert.Equal(SnapKind.None, TimelineSnap.SnapMove(3.1, 2, 3.2, [3.0], 0).Kind);
    }

    [Fact]
    public void EdgeTargetsCoverBothTracksExceptTheClipItself()
    {
        var t = new Timeline();
        var first = t.Append(Media(), 0, 2);
        var second = t.Append(Media(), 0, 3);
        t.Move(second, 2.5);
        var music = t.Append(new MediaInfo { Path = "m.m4a", Duration = 9, HasAudio = true }, 0, 1.2);
        t.Move(music, 4);
        Assert.Equal([0, 2, 4, 5.2], TimelineSnap.ClipEdgeTargets(t, second));
        Assert.Equal([2.5, 4, 5.2, 5.5], TimelineSnap.ClipEdgeTargets(t, first));
    }

    [Fact]
    public void SnappedMoveBlockedByOverlapIsNotAtTarget()
    {
        // 配樂結尾在 1.0 秒，移動的影片片段開頭吸過去，但同軌 0–1.5 已有片段，只能停到 1.5。
        var t = new Timeline();
        t.Append(Media(), 0, 1.5);
        var moving = t.Append(Media(), 0, 1);
        t.Move(moving, 4);
        t.Append(new MediaInfo { Path = "m.m4a", Duration = 9, HasAudio = true }, 0, 1);
        var snap = TimelineSnap.SnapMove(1.05, moving.Duration, null, TimelineSnap.ClipEdgeTargets(t, moving), 0.2);
        Assert.Equal(1.0, snap.Time, 9);
        var actual = t.Move(moving, snap.Time);
        Assert.Equal(1.5, actual, 9);
        Assert.False(TimelineSnap.IsAt(actual, snap.Time, 0));
    }

    [Fact]
    public void TrimSnapsToClipEdgesBeforeSilence()
    {
        // 片段邊緣與靜音點一樣近：片段邊緣優先；播放頭仍最優先。
        Assert.Equal(SnapKind.ClipEdge, TimelineSnap.Snap(5.0, null, [4.9], 0.2, [5.1]).Kind);
        Assert.Equal(SnapKind.Playhead, TimelineSnap.Snap(5.0, 4.9, [], 0.2, [5.1]).Kind);
        Assert.Equal(SnapKind.Silence, TimelineSnap.Snap(5.0, null, [4.95], 0.2, [5.1]).Kind);
        // 不傳片段邊緣（「吸附片段」關閉）時不吸。
        Assert.Equal(SnapKind.None, TimelineSnap.Snap(5.0, null, [], 0.2).Kind);
    }
}

[Collection("media")]
[Trait("Category", "Integration")]
public class SplitExportTests(TestMedia media)
{
    private async Task<string[]> FrameHashes(string path)
    {
        var bytes = await ProcessRunner.RunForBytesAsync(media.Tools.Ffmpeg,
            ["-v", "error", "-i", path, "-map", "0:v", "-f", "framemd5", "pipe:1"]);
        return Encoding.UTF8.GetString(bytes).Split('\n')
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .Select(line => line.Split(',')[^1].Trim()).ToArray();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SplitClipExportsTheSameVideo(bool reversed)
    {
        var info = await media.Probe(media.Landscape);
        var settings = new OutputSettings(AspectRatio.Original, FitMode.Fill, Crf: 0);

        var whole = new Timeline();
        var clip = whole.Append(info, 0.5, 3.5);
        if (reversed) whole.SetReversed(clip, true);
        var wholeResult = await media.Export(whole, settings, $"split-whole-{reversed}.mp4");

        var split = new Timeline();
        var part = split.Append(info, 0.5, 3.5);
        if (reversed) split.SetReversed(part, true);
        split.Split(part, 1.234);
        Assert.Equal(2, split.VideoClips.Count);
        var splitResult = await media.Export(split, settings, $"split-parts-{reversed}.mp4");

        Assert.Equal(await FrameHashes(wholeResult.Path), await FrameHashes(splitResult.Path));
        Assert.Equal(wholeResult.Duration, splitResult.Duration, 2);
    }
}

public class TimelineDragTests
{
    [Theory]
    [InlineData(100, ClipHit.TrimStart)] // 左緣上
    [InlineData(107, ClipHit.TrimStart)] // 剛好在邊緣範圍內
    [InlineData(108, ClipHit.Move)]
    [InlineData(200, ClipHit.Move)]
    [InlineData(292, ClipHit.Move)]
    [InlineData(293, ClipHit.TrimEnd)]
    [InlineData(300, ClipHit.TrimEnd)]
    public void WideClipHasEdgesAndAMiddle(double x, ClipHit expected) =>
        Assert.Equal(expected, TimelineDrag.Classify(x, 100, 300, 7));

    [Theory]
    [InlineData(100, ClipHit.TrimStart)]
    [InlineData(103, ClipHit.TrimStart)] // 寬 9：邊緣各 3
    [InlineData(104, ClipHit.Move)] // 中間仍可拖曳移動
    [InlineData(105, ClipHit.Move)]
    [InlineData(106, ClipHit.TrimEnd)]
    public void NarrowClipKeepsItsMiddleForMoving(double x, ClipHit expected) =>
        Assert.Equal(expected, TimelineDrag.Classify(x, 100, 109, 7));

    private static MediaInfo Media() => new()
    {
        Path = "a.mp4", Duration = 100, HasVideo = true, DisplayWidth = 1920, DisplayHeight = 1080, FrameRate = new Rational(30, 1),
    };

    [Fact]
    public void SnappedPositionIsUsedWhenItFits()
    {
        var t = new Timeline();
        var clip = t.Append(Media(), 0, 2);

        var snapped = TimelineDrag.MoveWithSnap(t, clip, raw: 5.3, snapped: 5.0);

        Assert.True(snapped);
        Assert.Equal(5.0, clip.Start, 6);
    }

    [Fact]
    public void FallsBackToTheMousePositionWhenTheSnapTargetIsTaken()
    {
        var t = new Timeline();
        var first = t.Append(Media(), 0, 2);   // 0～2
        var second = t.Append(Media(), 0, 2);  // 2～4
        // 貼齊的位置（1.0）與第一個片段重疊，會被推到別處；滑鼠原位（6.0）放得下。
        var snapped = TimelineDrag.MoveWithSnap(t, second, raw: 6.0, snapped: 1.0);

        Assert.False(snapped);
        Assert.Equal(6.0, second.Start, 6);
        Assert.Equal(0, first.Start, 6);
    }

    [Fact]
    public void NegativeSnapTargetFallsBackToTheMousePosition()
    {
        var t = new Timeline();
        var clip = t.Append(Media(), 0, 2);

        var snapped = TimelineDrag.MoveWithSnap(t, clip, raw: 3.0, snapped: -1.0);

        Assert.False(snapped);
        Assert.Equal(3.0, clip.Start, 6);
    }
}
