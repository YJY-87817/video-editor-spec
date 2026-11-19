using VideoEditor.Core.Models;
using VideoEditor.Core.Playback;

namespace VideoEditor.Tests;

/// <summary>從 PreviewEngine 抽出的純邏輯：時鐘、漂移判斷、倒帶影格挑選、影格範圍。</summary>
public class PlaybackClockTests
{
    [Theory]
    [InlineData(10, 2, 1, 12)]
    [InlineData(10, 2, 4, 18)] // 4 倍速
    [InlineData(0, 0, 1, 0)]
    public void PositionAdvancesByElapsedTimesRate(double clockBase, double elapsed, int rate, double expected) =>
        Assert.Equal(expected, PlaybackClock.Position(clockBase, elapsed, rate, waitingForFirstFrame: false), 9);

    [Fact]
    public void ClockStaysPutUntilTheFirstFrameAppears() =>
        Assert.Equal(10, PlaybackClock.Position(10, 5, 2, waitingForFirstFrame: true));

    [Theory]
    [InlineData(10, 4, 1, 6)]
    [InlineData(10, 4, 8, -22)] // 倒帶超過開頭時為負數，由呼叫端判斷
    public void RewindGoesBackBySpeed(double rewindBase, double elapsed, int speed, double expected) =>
        Assert.Equal(expected, PlaybackClock.RewindPosition(rewindBase, elapsed, speed), 9);

    [Theory]
    [InlineData(1000, 1000, 500, 1, false)]
    [InlineData(1500, 1000, 500, 1, false)] // 剛好等於容許值不算超出（原本是 <= 就視為同步）
    [InlineData(1501, 1000, 500, 1, true)]
    [InlineData(500, 1000, 500, 1, false)]
    [InlineData(499, 1000, 500, 1, true)] // 落後也算
    [InlineData(3000, 1000, 500, 4, false)] // 4 倍速容許值放大成 2000
    [InlineData(3001, 1000, 500, 4, true)]
    public void DriftIsToleratedUpToTheLimitScaledByRate(double actual, double expected, double limit, int rate, bool exceeded) =>
        Assert.Equal(exceeded, PlaybackClock.DriftExceeded(actual, expected, limit, rate));
}

public class RewindPlanTests
{
    [Fact]
    public void ShowsTheExactFrameWhenCached() =>
        Assert.Equal(100, RewindPlan.NearestCached(100, -1, 1, f => f == 100));

    [Fact]
    public void AtHigherSpeedsTheNearestSampledFrameIsShown()
    {
        // 往回倒（direction -1）、每 4 格解一格：剛經過的是編號較大的那幾格。
        Assert.Equal(102, RewindPlan.NearestCached(100, -1, 4, f => f == 102));
        Assert.Null(RewindPlan.NearestCached(100, -1, 4, f => f == 104)); // 超過 step 格就不算
        Assert.Null(RewindPlan.NearestCached(100, -1, 4, f => f == 99)); // 還沒經過的那一側不看
    }

    [Fact]
    public void ReversedClipsLookTheOtherWay() =>
        // 倒放片段往回倒時，來源影格編號變大，剛經過的是編號較小的格。
        Assert.Equal(98, RewindPlan.NearestCached(100, 1, 4, f => f == 98));

    [Fact]
    public void NearestWinsWhenSeveralAreCached() =>
        Assert.Equal(101, RewindPlan.NearestCached(100, -1, 4, f => f is 101 or 103));

    [Fact]
    public void NothingCachedMeansNothingToShow() =>
        Assert.Null(RewindPlan.NearestCached(100, -1, 2, _ => false));

    [Fact]
    public void FetchStartsAtTheFirstUnknownSample()
    {
        var known = new HashSet<long> { 100, 98 }; // step 2、往回：100, 98, 96...
        Assert.Equal(96, RewindPlan.NextFetchTarget(100, -1, 2, 0, 200, 12, known.Contains));
    }

    [Fact]
    public void NothingToFetchWhenEverythingAheadIsKnown() =>
        Assert.Null(RewindPlan.NextFetchTarget(100, -1, 1, 0, 200, 5, _ => true));

    [Fact]
    public void FetchStopsAtTheStartOfTheClip()
    {
        // 往回只剩 3 格（100, 99, 98 在範圍內，97 已超出 first=98）。
        var known = new HashSet<long> { 100, 99, 98 };
        Assert.Null(RewindPlan.NextFetchTarget(100, -1, 1, 98, 200, 12, known.Contains));
    }

    [Fact]
    public void FetchStopsAtTheEndOfAReversedClip() =>
        Assert.Null(RewindPlan.NextFetchTarget(100, 1, 1, 0, 102, 12, f => f <= 102 && f != 103));

    [Fact]
    public void FetchOnlyLooksAHandfulOfSamplesAhead() =>
        // 只往前看 lookahead 個取樣點：前 3 個都已知就不再往後找。
        Assert.Null(RewindPlan.NextFetchTarget(100, -1, 1, 0, 200, 3, f => f >= 98));

    [Fact]
    public void StartFrameOutsideTheClipFetchesNothing() =>
        Assert.Null(RewindPlan.NextFetchTarget(300, -1, 1, 0, 200, 12, _ => false));
}

public class FrameRangeTests
{
    [Fact]
    public void RangeCoversEveryFrameInsideTheClip()
    {
        // 30 fps、來源 1.0～2.5 秒：第 30 格（1.0 秒）到第 74 格（2.4667 秒）。
        Assert.Equal((30L, 74L), FrameMath.SourceFrameRange(1.0, 2.5, 30));
    }

    [Fact]
    public void ClipShorterThanOneFrameStillHasOne() =>
        Assert.Equal((30L, 30L), FrameMath.SourceFrameRange(1.0, 1.01, 30));

    [Fact]
    public void MediaInfoFallsBackToDefaultFrameRate()
    {
        Assert.Equal(MediaInfo.DefaultFrameRate, new MediaInfo { Path = "a.mp4" }.EffectiveFrameRate);
        Assert.Equal(25, new MediaInfo { Path = "a.mp4", FrameRate = new Rational(25, 1) }.EffectiveFrameRate);
    }
}
