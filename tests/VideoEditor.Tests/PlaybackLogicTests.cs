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
    // 快取裡有哪幾格；查得到就回傳「第 n 格」的內容，查不到為 null。
    private static Func<long, string?> Cache(params long[] frames) => f => frames.Contains(f) ? $"frame {f}" : null;

    [Fact]
    public void ShowsTheExactFrameWhenCached() =>
        Assert.Equal("frame 100", RewindPlan.NearestCached(100, -1, 1, Cache(100)));

    [Fact]
    public void AtHigherSpeedsTheNearestSampledFrameIsShown()
    {
        // 往回倒（direction -1）、每 4 格解一格：剛經過的是編號較大的那幾格。
        Assert.Equal("frame 102", RewindPlan.NearestCached(100, -1, 4, Cache(102)));
        Assert.Null(RewindPlan.NearestCached(100, -1, 4, Cache(104))); // 超過 step 格就不算
        Assert.Null(RewindPlan.NearestCached(100, -1, 4, Cache(99))); // 還沒經過的那一側不看
    }

    [Fact]
    public void ReversedClipsLookTheOtherWay() =>
        // 倒放片段往回倒時，來源影格編號變大，剛經過的是編號較小的格。
        Assert.Equal("frame 98", RewindPlan.NearestCached(100, 1, 4, Cache(98)));

    [Fact]
    public void NearestWinsWhenSeveralAreCached() =>
        Assert.Equal("frame 101", RewindPlan.NearestCached(100, -1, 4, Cache(101, 103)));

    [Fact]
    public void NothingCachedMeansNothingToShow() =>
        Assert.Null(RewindPlan.NearestCached(100, -1, 2, Cache()));

    [Fact]
    public void TheFoundContentIsReturnedWithoutALookupSecondTime()
    {
        var lookups = new List<long>();
        var found = RewindPlan.NearestCached(100, -1, 4, f => { lookups.Add(f); return f == 102 ? "x" : null; });

        Assert.Equal("x", found);
        Assert.Equal([100L, 101L, 102L], lookups); // 找到就停，不再往後查
    }

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

public class PendingSeekTests
{
    [Fact]
    public void OnlyTheFirstRequestQueuesACommand()
    {
        var seek = new PendingSeek();

        Assert.True(seek.Request(1000));
        Assert.False(seek.Request(1100)); // 已經有一個還沒執行的
        Assert.False(seek.Request(1200));
    }

    [Fact]
    public void TheCommandSeeksToTheLatestTargetNotTheOneItWasQueuedFor()
    {
        var seek = new PendingSeek();
        seek.Request(1000);
        seek.Request(1100);
        seek.Request(1300);

        Assert.Equal(1300, seek.Take()); // 執行時才取，過期的位置不會被用到
    }

    [Fact]
    public void AfterTheCommandRunsTheNextRequestQueuesAgain()
    {
        var seek = new PendingSeek();
        seek.Request(1000);
        seek.Take();

        Assert.True(seek.Request(5000));
        Assert.Equal(5000, seek.Take());
    }

    [Fact]
    public void TakeWithoutARequestIsHarmless()
    {
        var seek = new PendingSeek();
        Assert.Equal(0, seek.Take());
        Assert.True(seek.Request(7)); // 之後照常運作
    }

    [Fact]
    public async Task ManyRequestsWhileTheQueueIsBusyQueueOnlyOneCommand()
    {
        // 模擬 LibVLC 很慢：指令佇列還沒開始處理，計時器已經連續要求了上千次。
        var seek = new PendingSeek();
        var queued = 0;
        var tasks = Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
        {
            for (var i = 0; i < 1000; i++)
                if (seek.Request(worker * 10_000 + i)) Interlocked.Increment(ref queued);
        }));
        await Task.WhenAll(tasks);

        Assert.Equal(1, queued);
        Assert.InRange(seek.Take(), 0, 7 * 10_000 + 999);
    }

    [Fact]
    public async Task NoRequestIsLostWhenTheCommandRunsConcurrently()
    {
        // 要求端不斷要求、執行端不斷取走：每次要求之後，一定有一個指令會（再）取走最新的目標。
        var seek = new PendingSeek();
        var pendingCommands = 0;   // 已排入、還沒執行的指令數
        var maxPending = 0;
        long lastTaken = -1;
        const int requests = 20_000;
        var done = false;

        var executor = Task.Run(() =>
        {
            while (!Volatile.Read(ref done) || Volatile.Read(ref pendingCommands) > 0)
            {
                if (Volatile.Read(ref pendingCommands) == 0) { Thread.Yield(); continue; }
                lastTaken = seek.Take();
                Interlocked.Decrement(ref pendingCommands);
            }
        });
        for (var i = 1; i <= requests; i++)
        {
            if (seek.Request(i))
            {
                var now = Interlocked.Increment(ref pendingCommands);
                if (now > maxPending) maxPending = now;
            }
        }
        Volatile.Write(ref done, true);
        await executor;

        Assert.Equal(requests, lastTaken); // 最後一次要求的目標一定被取走
        Assert.True(maxPending <= 2, $"待執行的指令最多 2 個（目標競爭時可能多排一個），實際 {maxPending}");
    }
}
