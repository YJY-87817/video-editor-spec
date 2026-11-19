using VideoEditor.Core.Playback;

namespace VideoEditor.Tests;

public class FrameRequestQueueTests
{
    private static FrameRequestQueue<int> Queue() => new();

    [Fact]
    public void RequestIdsIncrease()
    {
        var queue = Queue();
        Assert.True(queue.NextRequestId() < queue.NextRequestId());
    }

    [Fact]
    public void SubmitAsksForAWorkerOnlyWhenNoneIsRunning()
    {
        var queue = Queue();
        Assert.True(queue.Submit(1));
        queue.StartRun();
        Assert.False(queue.Submit(2));
        queue.EndRun();
        Assert.True(queue.Submit(3));
    }

    [Fact]
    public void OnlyTheLatestRequestIsKept()
    {
        var queue = Queue();
        queue.Submit(1);
        queue.Submit(2);
        queue.Submit(3);
        Assert.True(queue.TryTake(out var request));
        Assert.Equal(3, request);
        Assert.False(queue.TryTake(out _));
        Assert.False(queue.HasPending);
    }

    [Fact]
    public void TakingClearsThePendingRequestSoANewOneIsNotLost()
    {
        var queue = Queue();
        queue.StartRun();
        queue.Submit(1);
        Assert.True(queue.TryTake(out _));
        queue.Submit(2); // 擷取期間排進來的請求
        Assert.True(queue.TryTake(out var next));
        Assert.Equal(2, next);
    }

    [Fact]
    public void SubmitInterruptsABackgroundExtract()
    {
        var queue = Queue();
        queue.StartRun();
        var token = queue.BeginExtract(interruptible: true);
        Assert.True(queue.CanInterrupt);
        Assert.False(token.IsCancellationRequested);
        queue.Submit(1);
        Assert.True(token.IsCancellationRequested);
    }

    [Fact]
    public void ExtractsForDisplayAreNotInterrupted()
    {
        var queue = Queue();
        queue.StartRun();
        var token = queue.BeginExtract(interruptible: false);
        Assert.False(queue.CanInterrupt);
        queue.Submit(1);
        Assert.False(token.IsCancellationRequested);
        Assert.False(token.CanBeCanceled);
    }

    [Fact]
    public void FinishedExtractIsNotCancelledByLaterRequests()
    {
        var queue = Queue();
        queue.StartRun();
        var token = queue.BeginExtract(interruptible: true);
        queue.EndExtract();
        queue.Submit(1); // 不能丟 ObjectDisposedException，也不會影響已結束的那次
        Assert.False(token.IsCancellationRequested);
        Assert.False(queue.CanInterrupt);
    }

    [Fact]
    public void PrefetchDoesNotJumpTheQueue()
    {
        var queue = Queue();
        Assert.True(queue.TrySubmitPrefetch(1));      // 閒置時可以
        Assert.False(queue.TrySubmitPrefetch(2));     // 已有排著的請求
        queue.TryTake(out _);
        queue.StartRun();
        Assert.False(queue.TrySubmitPrefetch(3));     // 正在跑
        queue.EndRun();
        Assert.True(queue.TrySubmitPrefetch(4));
        queue.TryTake(out var request);
        Assert.Equal(4, request);
    }

    [Fact]
    public void DropDiscardsPendingAndMarksShown()
    {
        var queue = Queue();
        queue.Submit(1);
        var id = queue.NextRequestId();
        queue.Drop(id);
        Assert.False(queue.HasPending);
        Assert.Equal(id, queue.Shown);
    }

    [Theory]
    [InlineData(5, true, false, false, 3, true)]    // 一般：顯示
    [InlineData(5, true, false, false, 5, true)]    // 同一個編號也顯示
    [InlineData(2, true, false, false, 3, false)]   // 已經顯示更新的畫面
    [InlineData(5, false, false, false, 3, false)]  // 背景預先解碼只進快取
    [InlineData(5, true, true, false, 3, false)]    // 播放中
    [InlineData(5, true, false, true, 3, false)]     // 倒帶中
    public void ShouldShow(long id, bool show, bool playing, bool rewinding, long shown, bool expected)
    {
        var queue = Queue();
        queue.MarkShown(shown);
        Assert.Equal(expected, queue.ShouldShow(id, show, playing, rewinding));
    }
}
