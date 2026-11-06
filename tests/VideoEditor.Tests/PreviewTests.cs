using VideoEditor.Core.Models;
using VideoEditor.Core.Playback;

namespace VideoEditor.Tests;

public class PreviewPlannerTests
{
    private static readonly Size2 Landscape = new(1920, 1080);
    private static readonly PreviewSurface Video = new(Landscape, FrameTransform.Identity);

    private static void AssertRect(Rect2 expected, Rect2 actual)
    {
        Assert.Equal(expected.X, actual.X, 6);
        Assert.Equal(expected.Y, actual.Y, 6);
        Assert.Equal(expected.Width, actual.Width, 6);
        Assert.Equal(expected.Height, actual.Height, 6);
    }

    [Fact]
    public void HiddenWithoutAReferenceSize()
    {
        var plan = PreviewPlanner.Compute(1000, 600, null, AspectRatio.R16x9, FitMode.Fill, Video);
        Assert.Equal(PreviewPlan.Hidden, plan);
        Assert.False(plan.Visible);
    }

    [Theory]
    [InlineData(0, 600)]
    [InlineData(1000, 0)]
    public void HiddenWhenTheAreaHasNoSize(double width, double height) =>
        Assert.False(PreviewPlanner.Compute(width, height, Landscape, AspectRatio.R16x9, FitMode.Fill, Video).Visible);

    [Fact]
    public void FillShowsTheWholeSourceAndShrinksTheFrameInsideIt()
    {
        var plan = PreviewPlanner.Compute(1000, 600, Landscape, AspectRatio.R9x16, FitMode.Fill, Video);

        Assert.True(plan.Visible);
        Assert.True(plan.ShowImage);
        Assert.True(plan.DimCrop);
        AssertRect(new Rect2(0, 18.75, 1000, 562.5), plan.Source);
        Assert.Equal(plan.Source.Height, plan.Frame.Height, 6);
        Assert.Equal(316.40625, plan.Frame.Width, 6);
        AssertRect(plan.Source, plan.Image); // 沒有旋轉，畫面就是來源位置
    }

    [Fact]
    public void PadShowsTheWholeFrameAndFitsTheSourceInsideIt()
    {
        var plan = PreviewPlanner.Compute(1000, 600, Landscape, AspectRatio.R9x16, FitMode.Pad, Video);

        Assert.False(plan.DimCrop); // 加黑邊沒有被裁掉的範圍
        Assert.Equal(600, plan.Frame.Height, 6);
        Assert.Equal(337.5, plan.Frame.Width, 6);
        Assert.Equal(plan.Frame.Width, plan.Source.Width, 6);
        Assert.Equal(189.84375, plan.Source.Height, 6);
    }

    [Fact]
    public void WithoutASurfaceOnlyTheBlackFrameIsShown()
    {
        var plan = PreviewPlanner.Compute(1000, 600, Landscape, AspectRatio.R16x9, FitMode.Fill, null);

        Assert.True(plan.Visible);
        Assert.False(plan.ShowImage);
        Assert.False(plan.DimCrop);
        AssertRect(plan.Frame, plan.Source); // 用輸出尺寸排版
    }

    [Fact]
    public void AudioOnlySurfaceKeepsTheImageButLaysOutByOutputSize()
    {
        var plan = PreviewPlanner.Compute(1000, 600, Landscape, AspectRatio.R9x16, FitMode.Fill, new PreviewSurface(null, FrameTransform.Identity));

        Assert.True(plan.ShowImage);
        AssertRect(plan.Frame, plan.Source);
    }

    [Fact]
    public void RotatedSourceLaysOutAsPortrait()
    {
        var rotated = new PreviewSurface(Landscape, new FrameTransform(Rotation: 90));

        var plan = PreviewPlanner.Compute(1000, 600, new Size2(1080, 1920), AspectRatio.Original, FitMode.Fill, rotated);

        // 旋轉後的來源是 1080×1920，高度受限：600 / 1920 = 0.3125。
        Assert.Equal(337.5, plan.Source.Width, 6);
        Assert.Equal(600, plan.Source.Height, 6);
        // 畫面本身以原始方向（橫向）放置，長寬互換，旋轉後剛好落在來源範圍內。
        Assert.Equal(plan.Source.Height, plan.Image.Width, 6);
        Assert.Equal(plan.Source.Width, plan.Image.Height, 6);
    }

    [Theory]
    [InlineData(0, 10, 20, 100, 50)]
    [InlineData(90, 35, -5, 50, 100)]
    [InlineData(180, 10, 20, 100, 50)]
    [InlineData(270, 35, -5, 50, 100)]
    public void ImageRectSwapsSizeOnlyForQuarterTurnsAndStaysCentered(int rotation, double x, double y, double width, double height) =>
        AssertRect(new Rect2(x, y, width, height), PreviewPlanner.ImageRect(new Rect2(10, 20, 100, 50), new FrameTransform(rotation)));
}

public class PlaybackTextTests
{
    [Theory]
    [InlineData(3661.5, "01:01:01.50")]
    [InlineData(0, "00:00:00.00")]
    [InlineData(-5, "00:00:00.00")]
    [InlineData(0.999, "00:00:00.99")]
    [InlineData(65.5, "00:01:05.50")]
    public void DurationShowsHoursMinutesSecondsAndHundredths(double seconds, string expected) =>
        Assert.Equal(expected, PlaybackText.Duration(seconds));

    [Fact]
    public void PositionShowsCurrentAndTotal() =>
        Assert.Equal("00:01:05.50 / 00:02:00.00", PlaybackText.Position(65.5, 120));

    [Theory]
    [InlineData(false, false, "▶ 播放")]
    [InlineData(true, false, "⏸ 暫停")]
    [InlineData(false, true, "⏸ 暫停")] // 倒帶中也可以按來暫停
    public void PlayButtonOffersPauseWhilePlayingOrRewinding(bool playing, bool rewinding, string expected) =>
        Assert.Equal(expected, PlaybackText.Play(playing, rewinding));

    [Fact]
    public void RewindAndFastForwardShowTheirSpeedOnlyWhileActive()
    {
        Assert.Equal("◀◀ 倒帶", PlaybackText.Rewind(false, 0));
        Assert.Equal("◀◀ 4×", PlaybackText.Rewind(true, 4));
        Assert.Equal("▶▶ 快轉", PlaybackText.FastForward(false, 1));
        Assert.Equal("▶▶ 2×", PlaybackText.FastForward(true, 2));
    }
}

public class PreviewKeysTests
{
    [Theory]
    [InlineData("Space", PreviewCommand.TogglePlay)]
    [InlineData("J", PreviewCommand.Rewind)]
    [InlineData("K", PreviewCommand.Pause)]
    [InlineData("L", PreviewCommand.PlayOrFaster)]
    [InlineData("Left", PreviewCommand.StepBack)]
    [InlineData("Right", PreviewCommand.StepForward)]
    [InlineData("I", PreviewCommand.MarkIn)]
    [InlineData("O", PreviewCommand.MarkOut)]
    [InlineData("Delete", PreviewCommand.DeleteClip)]
    public void ShortcutsMapToCommands(string key, PreviewCommand expected) =>
        Assert.Equal(expected, PreviewKeys.FromKeyName(key));

    [Theory]
    [InlineData("A")]
    [InlineData("F1")]
    [InlineData("j")] // WPF 的按鍵名稱一律大寫
    [InlineData("")]
    public void OtherKeysAreIgnored(string key) => Assert.Null(PreviewKeys.FromKeyName(key));
}

public class FrameBatchTests
{
    [Theory]
    [InlineData(10, 1, 0, 100, 12, 10, 12)]  // 往後：從目前這格起
    [InlineData(10, -1, 0, 100, 12, 0, 11)]  // 往前：到目前這格為止，碰到片段開頭就停
    [InlineData(50, -1, 0, 100, 12, 39, 12)]
    [InlineData(95, 1, 0, 100, 12, 95, 6)]   // 碰到片段結尾就停
    [InlineData(100, 1, 0, 100, 12, 100, 1)]
    public void BatchIncludesTheFrameAndExtendsInTheStepDirection(long frame, int direction, long first, long last, int size,
        long expectedStart, int expectedCount)
    {
        Assert.Equal((expectedStart, expectedCount), FrameBatch.Toward(frame, direction, first, last, size));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void FrameOutsideTheClipHasNoBatch(long frame) => Assert.Null(FrameBatch.Toward(frame, 1, 0, 100, 12));

    [Fact]
    public void FirstMissingLooksOnlyWithinTheLeadInTheStepDirection()
    {
        var available = new HashSet<long> { 11, 12, 13, 9 };
        Assert.Equal(14, FrameBatch.FirstMissing(10, 1, 0, 100, 4, available.Contains));
        Assert.Null(FrameBatch.FirstMissing(10, 1, 0, 100, 3, available.Contains)); // 11–13 都有了
        Assert.Equal(8, FrameBatch.FirstMissing(10, -1, 0, 100, 4, available.Contains));
    }

    [Fact]
    public void FirstMissingStopsAtTheClipEdge()
    {
        Assert.Null(FrameBatch.FirstMissing(100, 1, 0, 100, 4, _ => false));
        Assert.Null(FrameBatch.FirstMissing(0, -1, 0, 100, 4, _ => false));
        Assert.Equal(99, FrameBatch.FirstMissing(98, 1, 0, 99, 4, f => f == 98));
    }
}
