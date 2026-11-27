using VideoEditor.Core.Models;

namespace VideoEditor.Core.Editing;

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
public sealed record AddResult(TimelineClip? Clip, string? Error, double Shift = 0, int ShiftedCount = 0);

/// <summary>在播放頭分割的結果。成功時 Clip 是右段，失敗時有 Error（給狀態列的提示）。</summary>
public sealed record SplitResult(TimelineClip? Clip, string? Error);

/// <summary>時間軸上的編輯動作與它們的規則，不碰任何畫面元件。</summary>
public sealed class TimelineEditor(Timeline timeline)
{
    /// <summary>把素材依入出點接到時間軸尾端。圖片沒有入出點，一律加 <see cref="Timeline.StillClipDuration"/> 秒。成功後清掉入出點。</summary>
    public AddResult Add(MediaInfo media, InOutPoints points)
    {
        try
        {
            var clip = media.IsStill
                ? timeline.Append(media, 0, Timeline.StillClipDuration)
                : timeline.Append(media, points.InPoint, points.OutPoint(media));
            points.Clear();
            return new AddResult(clip, null);
        }
        catch (ArgumentException error)
        {
            return new AddResult(null, error.Message);
        }
    }

    /// <summary>
    /// 素材拖到時間軸 <paramref name="dropTime"/> 時要插入的位置：落在某個片段上時，
    /// 在片段的前半（左半邊）就插在它前面（它的開頭），後半（含正中間）就插在它後面（它的結尾）；
    /// 落在空白處時就是放下的位置（不小於 0）。
    /// </summary>
    public double InsertTarget(TrackKind track, double dropTime)
    {
        var clips = track == TrackKind.Video ? timeline.VideoClips : timeline.MusicClips;
        foreach (var clip in clips)
        {
            if (dropTime < clip.Start || dropTime >= clip.End) continue;
            return dropTime < (clip.Start + clip.End) / 2 ? clip.Start : clip.End;
        }
        return Math.Max(0, dropTime);
    }

    /// <summary>
    /// 把素材拖到時間軸：依 <see cref="InsertTarget"/> 決定位置插入，後面的片段往後移（見 <see cref="Timeline.Insert"/>）。
    /// 入出點的規則與 <see cref="Add"/> 相同（圖片固定 5 秒），成功後清掉入出點。
    /// </summary>
    public AddResult AddAt(MediaInfo media, InOutPoints points, double dropTime)
    {
        try
        {
            var start = InsertTarget(Timeline.DefaultTrack(media), dropTime);
            var (clip, shift, count) = media.IsStill
                ? timeline.Insert(media, 0, Timeline.StillClipDuration, start)
                : timeline.Insert(media, points.InPoint, points.OutPoint(media), start);
            points.Clear();
            return new AddResult(clip, null, shift, count);
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

    /// <summary>
    /// 在播放頭分割選取的片段。只在時間軸預覽時作用（素材模式下看不到時間軸的播放頭），
    /// 播放頭要在片段範圍內（剛好在邊緣不算）。成功時回傳右段。
    /// </summary>
    public SplitResult Split(TimelineClip? selected, double playhead, bool timelinePreview)
    {
        if (!timelinePreview) return new SplitResult(null, "請先切到時間軸預覽，再在播放頭分割。");
        if (selected == null) return new SplitResult(null, "請先選取要分割的片段。");
        if (playhead <= selected.Start + 1e-6 || playhead >= selected.End - 1e-6)
            return new SplitResult(null, "播放頭不在選取的片段內。");
        try
        {
            return new SplitResult(timeline.Split(selected, playhead), null);
        }
        catch (ArgumentException error)
        {
            return new SplitResult(null, error.Message);
        }
    }

    /// <summary>音量滑桿的百分比（0–200）轉成線性增益，取整數百分比。</summary>
    public static double VolumeFromPercent(double percent) => Math.Round(percent) / 100;
}
