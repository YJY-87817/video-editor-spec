using System.Diagnostics;

namespace VideoEditor.Core.Playback;

/// <summary>
/// 解不出來的影格記錄。倒帶與逐格預先解碼都會略過記錄中的影格，免得一再重試同一個解不出來的位置（例如來源結尾附近）；
/// 但記錄會過期：短暫的失敗（磁碟忙碌、FFmpeg 被防毒軟體暫時擋下）不該讓那一格永遠解不出來。
/// 只在單一執行緒（UI 執行緒）使用。
/// </summary>
public sealed class MissingFrames<TKey> where TKey : notnull
{
    /// <summary>記錄保留多久；過了之後這一格視為「不知道結果」，會再試一次。</summary>
    public static readonly TimeSpan DefaultRetryAfter = TimeSpan.FromSeconds(30);

    private readonly Dictionary<TKey, TimeSpan> _failedAt = [];
    private readonly Func<TimeSpan> _clock;
    private readonly TimeSpan _retryAfter;

    /// <param name="clock">目前時間（單調遞增）；測試時可替換。預設用系統的高精度計時。</param>
    public MissingFrames(TimeSpan? retryAfter = null, Func<TimeSpan>? clock = null)
    {
        _retryAfter = retryAfter ?? DefaultRetryAfter;
        var start = Stopwatch.GetTimestamp();
        _clock = clock ?? (() => Stopwatch.GetElapsedTime(start));
    }

    /// <summary>目前仍保留的記錄數（含尚未被查詢清掉的過期記錄）。</summary>
    public int Count => _failedAt.Count;

    /// <summary>記下這一格解不出來；已有記錄時重新計時。</summary>
    public void Add(TKey key) => _failedAt[key] = _clock();

    /// <summary>這一格是否在記錄中且還沒過期；過期的記錄會順便清掉。</summary>
    public bool Contains(TKey key)
    {
        if (!_failedAt.TryGetValue(key, out var at)) return false;
        if (_clock() - at < _retryAfter) return true;
        _failedAt.Remove(key);
        return false;
    }

    public void Clear() => _failedAt.Clear();
}
