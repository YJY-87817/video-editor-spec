using VideoEditor.Core.Ffmpeg;
using VideoEditor.Core.Models;

namespace VideoEditor.Core.Timeline;

/// <summary>素材上標記的入點與出點。沒有標記時，入點是素材開頭、出點是素材結尾。</summary>
public sealed class InOutPoints
{
    public double? In { get; private set; }
    public double? Out { get; private set; }

    public void Clear()
    {
        In = null;
        Out = null;
    }

    /// <summary>設定入點。出點若不在入點之後，就清掉出點。</summary>
    public void SetIn(double position)
    {
        In = position;
        if (Out <= In) Out = null;
    }

    /// <summary>設定出點。入點若不在出點之前，就清掉入點。</summary>
    public void SetOut(double position)
    {
        Out = position;
        if (In >= Out) In = null;
    }

    public double InPoint => In ?? 0;

    public double OutPoint(MediaInfo media) => Out ?? media.Duration;

    /// <summary>對齊到畫面上顯示的那一格，匯出的切點才會與看到的相同。</summary>
    public static double Snap(double position, MediaInfo media) =>
        media.FrameRate.Value > 0
            ? Math.Min(media.Duration, FrameExtractor.FrameIndex(position, media.FrameRate.Value) / media.FrameRate.Value)
            : position;
}
