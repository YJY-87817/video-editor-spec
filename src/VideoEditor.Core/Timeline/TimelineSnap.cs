namespace VideoEditor.Core.Timeline;

/// <summary>拉的是片段的哪一邊。</summary>
public enum TrimEdge { Start, End }

/// <summary>這次吸附到什麼。數值小的優先：同樣近時取前面的。</summary>
public enum SnapKind { None, Playhead, ClipEdge, TimelineStart, Silence }

public readonly record struct SnapResult(double Time, SnapKind Kind, double Target);

/// <summary>
/// 拖曳片段時的吸附規則。修剪：播放頭、其他片段的邊緣、片段自己聲音裡的靜音處；
/// 移動：片段的開頭或結尾貼齊播放頭、其他片段的邊緣、時間軸開頭。
/// 同樣近時依 <see cref="SnapKind"/> 的順序：播放頭 → 片段邊緣 → 時間軸開頭 → 靜音處。
/// </summary>
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
    /// 修剪：播放頭、片段邊緣與靜音吸附點裡，離 time 最近、而且在 tolerance 以內的那一個；同樣近時依 <see cref="SnapKind"/> 的順序。
    /// playhead 為 null 表示不吸附播放頭；silenceTargets 與 clipEdges 必須已排序，clipEdges 為 null 表示不吸附片段邊緣。
    /// </summary>
    public static SnapResult Snap(double time, double? playhead, IReadOnlyList<double> silenceTargets, double tolerance,
        IReadOnlyList<double>? clipEdges = null)
    {
        var candidates = new List<Candidate>(3);
        AddCandidates(candidates, time, 0, playhead, clipEdges, silenceTargets, timelineStart: false);
        return Best(candidates, tolerance) is { } b ? new SnapResult(b.Target, b.Kind, b.Target) : new SnapResult(time, SnapKind.None, time);
    }

    /// <summary>其他所有片段（兩軌都算，不含 clip 自己）的開頭與結尾，排序去重。</summary>
    public static List<double> ClipEdgeTargets(Timeline timeline, TimelineClip clip) =>
        timeline.AllClips.Where(c => c != clip).SelectMany(c => new[] { c.Start, c.End }).Distinct().Order().ToList();

    /// <summary>
    /// 移動：片段的開頭或結尾貼齊播放頭、其他片段的邊緣（已排序）或時間軸開頭，取位移最小、而且在 tolerance 以內的那一個。
    /// 回傳的 Time 是片段新的開頭，Target 是貼齊的位置（結尾貼齊時與 Time 差一個片段長度）。
    /// </summary>
    public static SnapResult SnapMove(double start, double duration, double? playhead, IReadOnlyList<double> clipEdges, double tolerance)
    {
        // Offset：貼齊的是開頭（0）還是結尾（片段長度）。
        var candidates = new List<Candidate>(6);
        AddCandidates(candidates, start, 0, playhead, clipEdges, null, timelineStart: true);
        AddCandidates(candidates, start + duration, duration, playhead, clipEdges, null, timelineStart: true);
        return Best(candidates, tolerance) is { } b
            ? new SnapResult(b.Target - b.Offset, b.Kind, b.Target)
            : new SnapResult(start, SnapKind.None, start);
    }

    /// <summary>一個吸附候選：Distance 是 edge 到 Target 的距離，Offset 是 edge 離片段開頭多遠。</summary>
    private readonly record struct Candidate(double Target, SnapKind Kind, double Distance, double Offset);

    /// <summary>把 edge 能貼齊的播放頭、最近的片段邊緣、最近的靜音處與時間軸開頭加進候選。</summary>
    private static void AddCandidates(List<Candidate> list, double edge, double offset, double? playhead,
        IReadOnlyList<double>? clipEdges, IReadOnlyList<double>? silenceTargets, bool timelineStart)
    {
        if (playhead is { } p) list.Add(new Candidate(p, SnapKind.Playhead, Math.Abs(edge - p), offset));
        if (clipEdges != null && Nearest(clipEdges, edge) is { } other) list.Add(new Candidate(other, SnapKind.ClipEdge, Math.Abs(edge - other), offset));
        if (timelineStart) list.Add(new Candidate(0, SnapKind.TimelineStart, Math.Abs(edge), offset));
        if (silenceTargets != null && Nearest(silenceTargets, edge) is { } silence) list.Add(new Candidate(silence, SnapKind.Silence, Math.Abs(edge - silence), offset));
    }

    private static Candidate? Best(List<Candidate> candidates, double tolerance)
    {
        if (tolerance <= 0) return null;
        Candidate? best = null;
        foreach (var c in candidates)
        {
            if (c.Distance > tolerance) continue;
            if (best is not { } b || c.Distance < b.Distance - 1e-9 || (Math.Abs(c.Distance - b.Distance) <= 1e-9 && c.Kind < b.Kind))
                best = c;
        }
        return best;
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
