namespace VideoEditor.Core.Playback;

/// <summary>
/// 把連續的「跳到某個位置」要求合併成最多一個待執行的指令。
/// 預覽的計時器每 16 毫秒檢查一次配樂是否偏離，偏離時就要求跳轉；LibVLC 的呼叫比這慢時，
/// 若每次都排一個指令，佇列會累積一堆過期的跳轉，之後一口氣依序執行，配樂反覆跳來跳去。
/// 這裡只在沒有待執行的跳轉時才排入，執行時再取走最新的目標，所以過期的位置不會被用到。
/// 要求端在 UI 執行緒，執行端在背景指令佇列，所以兩邊都是執行緒安全的。
/// </summary>
public sealed class PendingSeek
{
    private long _targetMs;
    private int _queued;

    /// <summary>
    /// 要求跳到 targetMs（毫秒）。回傳 true 表示呼叫端要排入一個執行跳轉的指令（指令裡呼叫 <see cref="Take"/>）；
    /// 回傳 false 表示已經有一個還沒執行的，只更新了它的目標，不必再排。
    /// </summary>
    public bool Request(long targetMs)
    {
        Interlocked.Exchange(ref _targetMs, targetMs);
        return Interlocked.Exchange(ref _queued, 1) == 0;
    }

    /// <summary>執行跳轉時呼叫：取走最新的目標，之後的要求會再排入新的指令。</summary>
    public long Take()
    {
        Interlocked.Exchange(ref _queued, 0);
        return Interlocked.Read(ref _targetMs);
    }
}
