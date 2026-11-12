using VideoEditor.Core.Models;

namespace VideoEditor.Core.Timeline;

/// <summary>時間軸上一欄波形：從 X 開始、寬 Width 像素，Level 是半高的比例（0–1），Clipped 表示這裡會破音。</summary>
public readonly record struct WaveformColumn(double X, double Width, double Level, bool Clipped);

/// <summary>把片段在畫面上看得到的部分切成一欄一欄，算出每欄的波形高度。與畫面無關，方便測試。</summary>
public static class WaveformColumns
{
    /// <param name="clipX">片段開頭（clip.Start）在畫面上的 x。</param>
    /// <param name="visibleLeft">畫面可見範圍的左緣 x。</param>
    /// <param name="visibleRight">畫面可見範圍的右緣 x。</param>
    /// <param name="columnWidth">每欄的像素寬。</param>
    public static List<WaveformColumn> Build(TimelineClip clip, WaveformData data, double clipX, double pixelsPerSecond,
        double visibleLeft, double visibleRight, double columnWidth = 1)
    {
        var columns = new List<WaveformColumn>();
        if (pixelsPerSecond <= 0 || columnWidth <= 0) return columns;
        var left = Math.Max(clipX, visibleLeft);
        var right = Math.Min(clipX + clip.Duration * pixelsPerSecond, visibleRight);
        for (var x = left; x < right; x += columnWidth)
        {
            var width = Math.Min(columnWidth, right - x);
            var from = (x - clipX) / pixelsPerSecond;
            var to = (x + width - clipX) / pixelsPerSecond;
            var peak = data.Peak(SourceTime(clip, from), SourceTime(clip, to));
            columns.Add(new WaveformColumn(x, width, WaveformData.Level(peak, clip.Volume), WaveformData.Clips(peak, clip.Volume)));
        }
        return columns;
    }

    /// <summary>
    /// 片段內 offset 秒的聲音在來源的哪個時間。倒放時聲音整段從出點倒著播（匯出用 areverse），
    /// 所以從 SourceOut 往回算，不是畫面用的「出點前最後一格」。
    /// </summary>
    public static double SourceTime(TimelineClip clip, double offset)
    {
        offset = Math.Clamp(offset, 0, clip.Duration);
        return clip.Reversed ? clip.SourceOut - offset : clip.SourceIn + offset;
    }
}
