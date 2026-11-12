namespace VideoEditor.Core.Timeline;

/// <summary>拉的是片段的哪一邊。</summary>
public enum TrimEdge { Start, End }

/// <summary>這次吸附到什麼。</summary>
public enum SnapKind { None, Playhead, Silence }

public readonly record struct SnapResult(double Time, SnapKind Kind, double Target);

/// <summary>拉動片段邊緣時的吸附規則：播放頭，以及片段自己聲音裡的靜音處。</summary>
public static class TimelineSnap
{
    /// <summary>吸附到靜音處時，往靜音裡多留這麼多，避免切掉字頭的子音或字尾的尾音。</summary>
    public const double SilencePadding = 0.1;

    /// <summary>
    /// 時間在播放頭前後 tolerance 秒以內時，改成播放頭的時間，讓剪輯點能準確對齊預覽的位置。
    /// tolerance 由畫面上的像素距離換算，所以時間軸放大時吸附範圍跟著變小。
    /// </summary>
    public static (double Time, bool Snapped) ToPlayhead(double time, double playhead, double tolerance) =>
        tolerance > 0 && Math.Abs(time - playhead) <= tolerance ? (playhead, true) : (time, false);

    /// <summary>
    /// 吸附後，邊緣是否真的落在播放頭上（用來決定要不要顯示「已貼齊」）。
    /// 修剪的切點會對齊來源影格，所以邊緣與播放頭最多可以差半格；
    /// 差得更多表示邊緣被來源長度或相鄰片段擋住，沒有貼到。沒有影格率的素材（純音訊）不對齊，要求完全相同。
    /// </summary>
    public static bool IsAtPlayhead(double edge, double playhead, double fps) => IsAt(edge, playhead, fps);

    /// <summary>邊緣是否落在吸附點上（播放頭或靜音處），規則同 <see cref="IsAtPlayhead"/>。</summary>
    public static bool IsAt(double edge, double target, double fps) =>
        Math.Abs(edge - target) <= (fps > 0 ? 0.5 / fps : 0) + 1e-6;

    /// <summary>
    /// 片段聲音的來源時間在時間軸上的位置（倒放時聲音從出點往回播）。不限制在片段目前的範圍內：
    /// 修剪時邊緣會拉到片段外面，而修剪不會改變這個對應關係。
    /// </summary>
    public static double ToTimeline(TimelineClip clip, double source) =>
        clip.Reversed ? clip.Start + (clip.SourceOut - source) : clip.Start + (source - clip.SourceIn);

    /// <summary>來源上的靜音區間在時間軸上的範圍。</summary>
    public static TimeRange ToTimeline(TimelineClip clip, TimeRange source)
    {
        var a = ToTimeline(clip, source.From);
        var b = ToTimeline(clip, source.To);
        return new TimeRange(Math.Min(a, b), Math.Max(a, b));
    }

    /// <summary>
    /// 靜音處的吸附點（依時間排序），依拉的方向決定：
    /// 拉左緣停在「聲音開始前」（靜音結束往前退 <see cref="SilencePadding"/>），用來剪掉開頭的空白；
    /// 拉右緣停在「聲音結束後」（靜音開始往後留 <see cref="SilencePadding"/>），用來剪掉結尾的空白。
    /// 方向依時間軸計算，倒放片段也正確。靜音短到留不下兩邊的餘裕時，改用中點。
    /// </summary>
    public static List<double> SilenceTargets(TimelineClip clip, IEnumerable<TimeRange> silences, TrimEdge edge)
    {
        var targets = new List<double>();
        foreach (var silence in silences)
        {
            var range = ToTimeline(clip, silence);
            targets.Add(range.Length < 2 * SilencePadding ? (range.From + range.To) / 2
                : edge == TrimEdge.Start ? range.To - SilencePadding : range.From + SilencePadding);
        }
        targets.Sort();
        return targets;
    }

    /// <summary>
    /// 播放頭與靜音吸附點裡，離 time 最近、而且在 tolerance 以內的那一個；兩者一樣近時以播放頭優先（使用者刻意放的位置）。
    /// playhead 為 null 表示不吸附播放頭；silenceTargets 必須已排序。
    /// </summary>
    public static SnapResult Snap(double time, double? playhead, IReadOnlyList<double> silenceTargets, double tolerance)
    {
        if (tolerance <= 0) return new SnapResult(time, SnapKind.None, time);
        var playheadDistance = playhead is { } p ? Math.Abs(time - p) : double.PositiveInfinity;
        var silence = Nearest(silenceTargets, time);
        var silenceDistance = silence is { } s ? Math.Abs(time - s) : double.PositiveInfinity;
        if (playheadDistance <= tolerance && playheadDistance <= silenceDistance)
            return new SnapResult(playhead!.Value, SnapKind.Playhead, playhead.Value);
        if (silenceDistance <= tolerance) return new SnapResult(silence!.Value, SnapKind.Silence, silence.Value);
        return new SnapResult(time, SnapKind.None, time);
    }

    private static double? Nearest(IReadOnlyList<double> sorted, double time)
    {
        if (sorted.Count == 0) return null;
        int lo = 0, hi = sorted.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (sorted[mid] < time) lo = mid + 1;
            else hi = mid;
        }
        if (lo == 0) return sorted[0];
        if (lo == sorted.Count) return sorted[^1];
        return time - sorted[lo - 1] <= sorted[lo] - time ? sorted[lo - 1] : sorted[lo];
    }
}
