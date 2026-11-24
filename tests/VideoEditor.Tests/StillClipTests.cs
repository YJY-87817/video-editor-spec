using VideoEditor.Core.Editing;
using VideoEditor.Core.Models;

namespace VideoEditor.Tests;

/// <summary>圖片片段在時間軸上的規則（docs/IMAGE-CLIP-PLAN.md 階段 3）。</summary>
public class StillClipTests
{
    private static MediaInfo Photo(string path = "photo.jpg") => new()
    {
        Path = path, IsStill = true, HasVideo = true, Duration = Timeline.MaxStillDuration,
        DisplayWidth = 4000, DisplayHeight = 3000, FrameRate = new Rational(0, 1), PixelFormat = "yuvj420p",
    };

    private static MediaInfo Video() => new()
    {
        Path = "a.mp4", Duration = 10, HasVideo = true, HasAudio = true,
        DisplayWidth = 1920, DisplayHeight = 1080, FrameRate = new Rational(30, 1), PixelFormat = "yuv420p",
    };

    [Fact]
    public void AddingAPhotoMakesAFiveSecondClipAndIgnoresMarks()
    {
        var t = new Timeline();
        var points = new InOutPoints();
        points.SetIn(1);
        points.SetOut(2);

        var result = new TimelineEditor(t).Add(Photo(), points);

        var clip = Assert.IsType<TimelineClip>(result.Clip);
        Assert.Equal(TrackKind.Video, clip.Track);
        Assert.Equal((0.0, Timeline.StillClipDuration), (clip.SourceIn, clip.SourceOut));
        Assert.Equal(5, clip.Duration);
        Assert.Null(points.In); // 加入後照常清掉入出點
    }

    [Fact]
    public void PhotoIsAppendedAfterTheLastClip()
    {
        var t = new Timeline();
        var editor = new TimelineEditor(t);
        editor.Add(Video(), new InOutPoints());
        var photo = editor.Add(Photo(), new InOutPoints()).Clip!;
        Assert.Equal(10, photo.Start, 9);
        Assert.Equal(15, photo.End, 9);
    }

    [Fact]
    public void BothEdgesCanBeExtended()
    {
        var t = new Timeline();
        var clip = t.Append(Photo(), 0, 5);
        t.Move(clip, 10);

        t.TrimStart(clip, 2); // 往左拉長，右緣不動
        Assert.Equal((2.0, 15.0), (clip.Start, clip.End));
        Assert.Equal((0.0, 13.0), (clip.SourceIn, clip.SourceOut));

        t.TrimEnd(clip, 40); // 往右拉長
        Assert.Equal((2.0, 40.0), (clip.Start, clip.End));
        Assert.Equal(0, clip.SourceIn);

        t.TrimStart(clip, 30); // 往右縮短
        Assert.Equal((30.0, 40.0), (clip.Start, clip.End));
        Assert.Equal((0.0, 10.0), (clip.SourceIn, clip.SourceOut));
    }

    [Fact]
    public void TrimmingIsNotAlignedToFrames()
    {
        // 圖片沒有影格，照時間切；匯出時才對齊輸出的影格。
        var t = new Timeline();
        var clip = t.Append(Photo(), 0, 5);
        t.TrimEnd(clip, 3.217);
        Assert.Equal(3.217, clip.End, 9);
        t.Move(clip, 4);
        t.TrimStart(clip, 1.123);
        Assert.Equal(1.123, clip.Start, 9);
    }

    [Fact]
    public void NeighboursLimitTrimming()
    {
        var t = new Timeline();
        var left = t.Append(Video(), 0, 4);
        var photo = t.Append(Photo(), 0, 5);
        t.Move(photo, 6);
        var right = t.Append(Video(), 0, 2);
        t.Move(right, 20);

        t.TrimStart(photo, 1);
        Assert.Equal(left.End, photo.Start, 9);
        t.TrimEnd(photo, 30);
        Assert.Equal(right.Start, photo.End, 9);
    }

    [Fact]
    public void ShortestAndLongestLength()
    {
        var t = new Timeline();
        var clip = t.Append(Photo(), 0, 5);

        t.TrimEnd(clip, 0);
        Assert.Equal(Timeline.MinClipDuration, clip.Duration, 9);
        t.TrimEnd(clip, 99999);
        Assert.Equal(Timeline.MaxStillDuration, clip.Duration, 9);
        t.TrimStart(clip, 10);
        t.Move(clip, 5000);
        t.TrimStart(clip, 0);
        Assert.Equal(Timeline.MaxStillDuration, clip.Duration, 9);
        t.TrimStart(clip, 99999);
        Assert.Equal(Timeline.MinClipDuration, clip.Duration, 9);
    }

    [Fact]
    public void SplittingGivesTwoClipsOfTheSamePhoto()
    {
        var t = new Timeline();
        var clip = t.Append(Photo(), 0, 5);
        t.Move(clip, 1);
        clip.Volume = 1; // 沒有聲音，但設定照樣沿用
        t.SetTransform(clip, FrameTransform.Identity.Rotate(90));

        var right = t.Split(clip, 2.333);

        Assert.Equal((1.0, 2.333), (clip.Start, clip.End));
        Assert.Equal((0.0, 1.333), (clip.SourceIn, Math.Round(clip.SourceOut, 9)));
        Assert.Equal(2.333, right.Start, 9);
        Assert.Equal(6, right.End, 9);
        Assert.Equal(0, right.SourceIn); // 右段也從 0 開始
        Assert.Same(clip.Media, right.Media);
        Assert.Equal(clip.Transform, right.Transform);

        // 分割後兩段都能照圖片的規則修剪。
        t.TrimStart(right, 2.5);
        Assert.Equal((2.5, 6.0), (right.Start, Math.Round(right.End, 9)));
    }

    [Fact]
    public void PhotosCannotBeReversed()
    {
        var t = new Timeline();
        var clip = t.Append(Photo(), 0, 5);
        Assert.Throws<ArgumentException>(() => t.SetReversed(clip, true));
        Assert.False(clip.Reversed);
    }

    [Fact]
    public void RotationAndFramingWorkAsForVideo()
    {
        var t = new Timeline();
        var clip = t.Append(Photo(), 0, 5);

        t.SetTransform(clip, FrameTransform.Identity.Rotate(90));
        Assert.Equal(new Size2(3000, 4000), clip.DisplaySize);

        var framing = ClipFraming.Default with { Zoom = 2 };
        t.SetFraming(clip, framing);
        Assert.Equal(framing, clip.Framing);
    }

    [Fact]
    public void AppendedPhotoAlwaysStartsAtSourceZero()
    {
        var clip = new Timeline().Append(Photo(), 2, 7);
        Assert.Equal((0.0, 5.0), (clip.SourceIn, clip.SourceOut));
    }

    [Fact]
    public void PhotoCannotGoOnTheMusicTrack() =>
        Assert.Throws<ArgumentException>(() => new Timeline().Append(Photo(), 0, 5, TrackKind.Music));
}
