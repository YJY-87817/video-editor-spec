namespace VideoEditor.Core.Models;

/// <summary>影格與時間的換算。純數學，時間軸、預覽、匯出共用。</summary>
public static class FrameMath
{
    /// <summary>時間所在的影格編號（四捨五入到最近的一格）。</summary>
    public static long FrameIndex(double time, double fps) => (long)Math.Floor(time * fps + 0.5);

    /// <summary>時間戳不早於 time 的第一格。</summary>
    public static long FirstFrameAtOrAfter(double time, double fps) => (long)Math.Ceiling(time * fps - 1e-6);

    /// <summary>
    /// 片段在來源中的第一格與最後一格編號（含）。出點之前的最後一格；片段短於一格時第一格與最後一格相同。
    /// </summary>
    public static (long First, long Last) SourceFrameRange(double sourceIn, double sourceOut, double fps)
    {
        var first = FirstFrameAtOrAfter(sourceIn, fps);
        return (first, Math.Max(first, FirstFrameAtOrAfter(sourceOut, fps) - 1));
    }

    /// <summary>第 n 格的定位時間。略早於該格時間戳，避免捨入後落到下一格。</summary>
    public static double FrameSeekTime(long frame, double fps) => Math.Max(0, (frame - 0.25) / fps);
}
