using VideoEditor.Core.Models;
using TimelineModel = VideoEditor.Core.Timeline.Timeline;

namespace VideoEditor.Core.Timeline;

/// <summary>編輯後預覽要怎麼跟上。</summary>
public enum PreviewFollowUp
{
    /// <summary>不用動。</summary>
    None,

    /// <summary>切到時間軸預覽，並把播放頭移到片段開頭。</summary>
    ShowClipInTimeline,

    /// <summary>已在時間軸預覽，但播放頭不在片段內，移到片段開頭。</summary>
    SeekToClip,
}

/// <summary>把標記的素材放上時間軸的結果。成功時有 Clip，失敗時有 Error。</summary>
public sealed record AddResult(TimelineClip? Clip, string? Error);

/// <summary>時間軸上的編輯動作與它們的規則，不碰任何畫面元件。</summary>
public sealed class TimelineEditor(TimelineModel timeline)
{
    /// <summary>把素材依入出點接到時間軸尾端。成功後清掉入出點。</summary>
    public AddResult Add(MediaInfo media, InOutPoints points)
    {
        try
        {
            var clip = timeline.Append(media, points.InPoint, points.OutPoint(media));
            points.Clear();
            return new AddResult(clip, null);
        }
        catch (ArgumentException error)
        {
            return new AddResult(null, error.Message);
        }
    }

    /// <summary>
    /// 旋轉或鏡像影片軌的片段。為了讓人看得到效果，
    /// 回傳預覽該如何跟上：不在時間軸預覽就切過去，播放頭不在片段內就移進去。
    /// </summary>
    public PreviewFollowUp ChangeTransform(TimelineClip clip, Func<FrameTransform, FrameTransform> change,
        bool timelinePreview, double playhead)
    {
        timeline.SetTransform(clip, change(clip.Transform));
        if (!timelinePreview) return PreviewFollowUp.ShowClipInTimeline;
        return playhead < clip.Start || playhead >= clip.End ? PreviewFollowUp.SeekToClip : PreviewFollowUp.None;
    }

    /// <summary>音量滑桿的百分比（0–200）轉成線性增益，取整數百分比。</summary>
    public static double VolumeFromPercent(double percent) => Math.Round(percent) / 100;
}
