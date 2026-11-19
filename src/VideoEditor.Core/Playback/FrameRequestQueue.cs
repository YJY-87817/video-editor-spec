namespace VideoEditor.Core.Playback;

/// <summary>
/// 逐格預覽的請求規則：一次只跑一個擷取、只保留最新的請求、背景預先解碼可以被要顯示的畫面插隊，以及解好的影格該不該顯示。
/// 從 <c>PreviewEngine</c> 抽出來，讓這些規則不必碰 LibVLC、計時器與位圖就能測試。
/// 只在單一執行緒（UI 執行緒）使用，不做任何同步。
/// <para>
/// 使用方式：<see cref="Submit"/>／<see cref="TrySubmitPrefetch"/> 傳回 true 時啟動工作迴圈；
/// 迴圈以 <see cref="StartRun"/> 開始、<see cref="TryTake"/> 取請求、<see cref="BeginExtract"/>／<see cref="EndExtract"/> 包住每次擷取、<see cref="EndRun"/> 結束。
/// </para>
/// </summary>
/// <typeparam name="T">請求內容；擷取是否可被中止由呼叫端在 <see cref="BeginExtract"/> 指定。</typeparam>
public sealed class FrameRequestQueue<T> where T : struct
{
    private T? _pending;
    private CancellationTokenSource? _prefetchCancel;
    private long _latest;

    /// <summary>已經顯示出來的最新請求編號。</summary>
    public long Shown { get; private set; }

    /// <summary>工作迴圈是否正在跑。</summary>
    public bool Running { get; private set; }

    /// <summary>有沒有排著等待處理的請求。</summary>
    public bool HasPending => _pending != null;

    /// <summary>目前這次擷取能不能被插隊中止（只有背景預先解碼可以）。</summary>
    public bool CanInterrupt => _prefetchCancel != null;

    /// <summary>領一個新的請求編號；編號越大越新。</summary>
    public long NextRequestId() => ++_latest;

    /// <summary>
    /// 排入一個要顯示的請求，取代尚未處理的舊請求，並中止正在跑的背景預先解碼（一次只跑一個 FFmpeg，
    /// 不中止的話要等那一批解完才輪到這一格）。回傳 true 表示沒有工作迴圈在跑，呼叫端要啟動一個。
    /// </summary>
    public bool Submit(T request)
    {
        _pending = request;
        _prefetchCancel?.Cancel();
        return !Running;
    }

    /// <summary>
    /// 排入一個背景預先解碼的請求。已有工作在跑，或已有別的請求在排隊時不插隊，回傳 false。
    /// 回傳 true 表示請求已排入，呼叫端要啟動工作迴圈。
    /// </summary>
    public bool TrySubmitPrefetch(T request)
    {
        if (Running || _pending != null) return false;
        _pending = request;
        return true;
    }

    /// <summary>丟掉排著的請求（例如目前位置沒有畫面，改顯示黑畫面時），並把這個編號記成已顯示。</summary>
    public void Drop(long requestId)
    {
        _pending = null;
        MarkShown(requestId);
    }

    /// <summary>記下這個編號的畫面已經顯示出來。</summary>
    public void MarkShown(long requestId) => Shown = requestId;

    public void StartRun() => Running = true;

    public void EndRun() => Running = false;

    /// <summary>取出排著的請求（取出後就不再排著）；沒有時回傳 false。</summary>
    public bool TryTake(out T request)
    {
        if (_pending is { } pending)
        {
            request = pending;
            _pending = null;
            return true;
        }
        request = default;
        return false;
    }

    /// <summary>開始一次擷取。<paramref name="interruptible"/> 為 true 時回傳的權杖會在 <see cref="Submit"/> 時被取消。</summary>
    public CancellationToken BeginExtract(bool interruptible)
    {
        _prefetchCancel?.Dispose();
        _prefetchCancel = interruptible ? new CancellationTokenSource() : null;
        return _prefetchCancel?.Token ?? CancellationToken.None;
    }

    /// <summary>結束一次擷取，釋放它的權杖來源；之後的 <see cref="Submit"/> 不會再中止它。</summary>
    public void EndExtract()
    {
        _prefetchCancel?.Dispose();
        _prefetchCancel = null;
    }

    /// <summary>
    /// 解好的影格該不該顯示：背景預先解碼的只進快取；播放與倒帶時畫面由別的路徑更新；
    /// 也不可蓋掉較新的畫面（例如空白處已經顯示的黑畫面）。拖曳中解好的影格則要顯示，畫面才會跟著動。
    /// </summary>
    public bool ShouldShow(long requestId, bool show, bool playing, bool rewinding) =>
        show && !playing && !rewinding && requestId >= Shown;
}
