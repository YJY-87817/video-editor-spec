using System.Diagnostics;

namespace VideoEditor.Core.Playback;

/// <summary>
/// 解不出來的影格記錄。倒帶與逐格預先解碼都會略過記錄中的影格，免得一再重試同一個解不出來的位置。
/// 兩種記錄：<see cref="AddPermanent"/> 是 FFmpeg 正常結束卻沒有影格（例如來源結尾之外），再試也一樣，永遠保留；
/// <see cref="Add"/> 是失敗（磁碟忙碌、FFmpeg 被防毒軟體暫時擋下），會過期，不該讓那一格永遠解不出來。
/// 只在單一執行緒（UI 執行緒）使用。
/// </summary>
public sealed class MissingFrames<TKey> where TKey : notnull
{
    /// <summary>記錄保留多久；過了之後這一格視為「不知道結果」，會再試一次。</summary>
    public static readonly TimeSpan DefaultRetryAfter = TimeSpan.FromSeconds(30);

    private readonly Dictionary<TKey, TimeSpan> _failedAt = [];
    private readonly HashSet<TKey> _permanent = [];
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
    public int Count => _failedAt.Count + _permanent.Count;

    /// <summary>記下這一格沒有影格可解（再試也一樣），不會過期。</summary>
    public void AddPermanent(TKey key) => _permanent.Add(key);

    /// <summary>記下這一格解失敗；已有記錄時重新計時。</summary>
    public void Add(TKey key) => _failedAt[key] = _clock();

    /// <summary>這一格是否在記錄中且還沒過期；過期的記錄會順便清掉。</summary>
    public bool Contains(TKey key)
    {
        if (_permanent.Contains(key)) return true;
        if (!_failedAt.TryGetValue(key, out var at)) return false;
        if (_clock() - at < _retryAfter) return true;
        _failedAt.Remove(key);
        return false;
    }

    public void Clear()
    {
        _failedAt.Clear();
        _permanent.Clear();
    }
}
