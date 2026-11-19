using VideoEditor.Core.Models;

namespace VideoEditor.Core.Editing;

/// <summary>Page Up／Page Down 可以跳到的剪輯點。</summary>
public static class EditPoints
{
    /// <summary>位置差在這以內視為同一點，避免 Seek 後的微小誤差讓下一次又跳回同一點。</summary>
    private const double Tolerance = 1e-3;

    /// <summary>時間軸預覽：時間軸開頭，加上影片軌每個片段的開頭與結尾（排序、去重）。</summary>
    public static IReadOnlyList<double> Timeline(Timeline timeline) =>
        Distinct(timeline.VideoClips.SelectMany(c => new[] { c.Start, c.End }).Prepend(0));

    /// <summary>素材模式：素材開頭、入點、出點（有設定時）與素材結尾（對齊到整數格）。</summary>
    public static IReadOnlyList<double> Source(MediaInfo media, InOutPoints points)
    {
        var list = new List<double> { 0, InOutPoints.MediaEnd(media) };
        if (points.In is { } i) list.Add(i);
        if (points.Out is { } o) list.Add(o);
        return Distinct(list);
    }

    /// <summary>比 position 早的最近一點；沒有時為 null。</summary>
    public static double? Previous(IReadOnlyList<double> points, double position) =>
        points.Where(p => p < position - Tolerance).Select(p => (double?)p).LastOrDefault();

    /// <summary>比 position 晚的最近一點；沒有時為 null。</summary>
    public static double? Next(IReadOnlyList<double> points, double position) =>
        points.Where(p => p > position + Tolerance).Select(p => (double?)p).FirstOrDefault();

    private static List<double> Distinct(IEnumerable<double> values)
    {
        var result = new List<double>();
        foreach (var value in values.OrderBy(v => v))
            if (result.Count == 0 || value - result[^1] > Tolerance) result.Add(value);
        return result;
    }
}
