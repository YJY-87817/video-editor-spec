using VideoEditor.Core.Models;

namespace VideoEditor.Core.Editing;

/// <summary>時間軸上一張縮圖：畫在哪個時間（時間軸時間），以及取來源的哪個時間。</summary>
public readonly record struct ThumbnailSlot(double TimelineTime, double SourceTime);

/// <summary>
/// 時間軸影片片段上要畫哪幾張縮圖（UIUX-PLAN-2 #6、決定 10）。片段夠寬時至少在開頭畫一張；
/// 放大到放得下更多張時，沿著片段每隔一段固定的秒數畫一張。間隔從一組整齊的秒數中挑
/// （放得下一張縮圖的最小間隔），同一個縮放範圍內取的來源時間都一樣，快取才用得上。
/// </summary>
public static class ThumbnailPlan
{
    /// <summary>片段在畫面上窄於這個寬度就不畫縮圖。</summary>
    public const double MinClipWidth = 24;

    private static readonly double[] Steps = [0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 1800, 3600];

    /// <summary>
    /// 片段上看得到的縮圖。<paramref name="visibleStart"/>／<paramref name="visibleEnd"/> 是畫面上看得到的時間軸範圍，
    /// 看不到的不列（不必產生）。來源時間對齊到影格；倒放的片段從出點往回取（和預覽、匯出相同）；圖片都取 0 秒。
    /// </summary>
    public static IReadOnlyList<ThumbnailSlot> Slots(TimelineClip clip, double pixelsPerSecond, double thumbWidth, double visibleStart, double visibleEnd)
    {
        if (pixelsPerSecond <= 0 || thumbWidth <= 0 || clip.Duration * pixelsPerSecond < MinClipWidth) return [];
        var step = Step(pixelsPerSecond, thumbWidth);
        var slotSeconds = thumbWidth / pixelsPerSecond;
        var slots = new List<ThumbnailSlot>();
        for (var offset = 0.0; offset < clip.Duration - 1e-9; offset += step)
        {
            var time = clip.Start + offset;
            if (time > visibleEnd) break;
            if (time + slotSeconds < visibleStart) continue;
            slots.Add(new ThumbnailSlot(time, SourceTime(clip, time)));
        }
        return slots;
    }

    /// <summary>兩張縮圖之間的秒數：放得下一張縮圖的最小整齊間隔。</summary>
    public static double Step(double pixelsPerSecond, double thumbWidth)
    {
        foreach (var step in Steps)
            if (step * pixelsPerSecond >= thumbWidth) return step;
        return Steps[^1];
    }

    private static double SourceTime(TimelineClip clip, double timelineTime)
    {
        if (clip.Media.IsStill) return 0;
        var source = clip.ToSourceTime(timelineTime);
        var fps = clip.Media.FrameRate.Value;
        if (fps <= 0) return source;
        // 對齊到那一格，並留在片段的來源範圍內（出點那一格不屬於片段）。
        var frame = Math.Floor(source * fps + 1e-6);
        var last = Math.Max(clip.SourceIn * fps, Math.Ceiling(clip.SourceOut * fps - 1e-6) - 1);
        return Math.Clamp(frame, Math.Ceiling(clip.SourceIn * fps - 1e-6), last) / fps;
    }
}
