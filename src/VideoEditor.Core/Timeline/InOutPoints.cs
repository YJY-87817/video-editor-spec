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

    public double OutPoint(MediaInfo media) => Out ?? MediaEnd(media);

    /// <summary>
    /// 素材結尾可以當出點的位置：素材長度往下對齊到整數格。
    /// 素材長度常常不是整數格（聲音比畫面長一點），直接拿來當出點的話片段長度就不是整數格，
    /// 接合時下一段會落在兩格之間，最壞的情況（剛好差半格）整段的影格時間戳記會兩兩重複。
    /// 代價是最多少掉不到一格的尾端聲音。沒有影格率的素材（純音訊）維持素材長度。
    /// </summary>
    public static double MediaEnd(MediaInfo media)
    {
        var fps = media.FrameRate.Value;
        return fps > 0 ? Math.Floor(media.Duration * fps + 1e-6) / fps : media.Duration;
    }

    /// <summary>對齊到畫面上顯示的那一格，匯出的切點才會與看到的相同。不超過素材結尾的最後一個整數格。</summary>
    public static double Snap(double position, MediaInfo media) =>
        media.FrameRate.Value > 0
            ? Math.Min(MediaEnd(media), FrameExtractor.FrameIndex(position, media.FrameRate.Value) / media.FrameRate.Value)
            : position;
}
