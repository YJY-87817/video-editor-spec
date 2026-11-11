using VideoEditor.Core.Models;

namespace VideoEditor.Core.Timeline;

public enum TrackKind
{
    /// <summary>V1 與 A1：影片片段與它的原音，兩者一起移動。</summary>
    Video,
    /// <summary>A2：獨立的配樂片段。</summary>
    Music,
}

public sealed class TimelineClip(MediaInfo media, TrackKind track, double sourceIn, double sourceOut, double start)
{
    public Guid Id { get; } = Guid.NewGuid();
    public MediaInfo Media { get; } = media;
    public TrackKind Track { get; } = track;
    public double SourceIn { get; internal set; } = sourceIn;
    public double SourceOut { get; internal set; } = sourceOut;
    public double Start { get; internal set; } = start;
    /// <summary>線性增益，1 = 原音量。</summary>
    public double Volume { get; set; } = 1.0;
    /// <summary>倒放：片段從 SourceOut 往 SourceIn 播放，原音也一起倒放。只用於影片軌。</summary>
    public bool Reversed { get; internal set; }
    /// <summary>旋轉與鏡像。只用於影片軌。</summary>
    public FrameTransform Transform { get; internal set; }
    /// <summary>填滿裁切時取用畫面的哪一部分。只用於影片軌。</summary>
    public ClipFraming Framing { get; internal set; } = ClipFraming.Default;

    /// <summary>套用旋轉後的畫面尺寸。</summary>
    public Size2 DisplaySize => Transform.Apply(new Size2(Media.DisplayWidth, Media.DisplayHeight));

    public double Duration => SourceOut - SourceIn;
    public double End => Start + Duration;

    /// <summary>
    /// 時間軸時間對應的來源時間。倒放時第一格是出點前的最後一格，
    /// 與匯出時 FFmpeg reverse 濾鏡的輸出相同。
    /// </summary>
    public double ToSourceTime(double timelineTime)
    {
        var offset = timelineTime - Start;
        return Reversed ? Math.Max(SourceIn, LastFrameTime - offset) : SourceIn + offset;
    }

    /// <summary>ToSourceTime 的反函數。</summary>
    public double ToTimelineTime(double sourceTime) =>
        Reversed ? Start + (LastFrameTime - sourceTime) : Start + (sourceTime - SourceIn);

    /// <summary>出點前最後一格的時間戳。</summary>
    private double LastFrameTime
    {
        get
        {
            var fps = Media.FrameRate.Value;
            if (fps <= 0) return SourceOut;
            return Math.Max(SourceIn, (Export.ReverseGraph.FirstFrameAtOrAfter(SourceOut, fps) - 1) / fps);
        }
    }
}

/// <summary>時間軸模型。規則：同軌不可重疊、可留空白、拖曳不推動其他片段。</summary>
public sealed class Timeline
{
    public const double MinClipDuration = 0.1;
    private const double Epsilon = 1e-6;

    private readonly List<TimelineClip> _video = [];
    private readonly List<TimelineClip> _music = [];

    public IReadOnlyList<TimelineClip> VideoClips => _video;
    public IReadOnlyList<TimelineClip> MusicClips => _music;
    public IEnumerable<TimelineClip> AllClips => _video.Concat(_music);

    public double Duration => AllClips.Select(c => c.End).DefaultIfEmpty(0).Max();

    private List<TimelineClip> ListOf(TrackKind track) => track == TrackKind.Video ? _video : _music;

    public TimelineClip Append(MediaInfo media, double sourceIn, double sourceOut, TrackKind? track = null)
    {
        var kind = track ?? (media.HasVideo ? TrackKind.Video : TrackKind.Music);
        if (kind == TrackKind.Video && !media.HasVideo) throw new ArgumentException("此素材沒有影像，無法放到影片軌。");
        if (kind == TrackKind.Music && !media.HasAudio) throw new ArgumentException("此素材沒有音訊，無法放到音訊軌。");
        if (sourceOut - sourceIn < MinClipDuration) throw new ArgumentException("片段太短。");
        if (sourceIn < 0 || sourceOut > media.Duration + Epsilon) throw new ArgumentOutOfRangeException(nameof(sourceOut));

        var list = ListOf(kind);
        var start = list.Count == 0 ? 0 : list[^1].End;
        var clip = new TimelineClip(media, kind, sourceIn, sourceOut, start);
        list.Add(clip);
        return clip;
    }

    public void Remove(TimelineClip clip) => ListOf(clip.Track).Remove(clip);

    /// <summary>
    /// 設定旋轉與鏡像。位置與長度不變。方向改變時畫面位置回到預設：
    /// 畫面位置是以轉完的方向記錄的，換了方向就對不上原本選的範圍。
    /// </summary>
    public void SetTransform(TimelineClip clip, FrameTransform transform)
    {
        if (clip.Track != TrackKind.Video) throw new ArgumentException("只有影片軌的片段可以旋轉或鏡像。");
        if (clip.Transform != transform) clip.Framing = ClipFraming.Default;
        clip.Transform = transform;
    }

    /// <summary>設定填滿裁切時的畫面位置。位置與長度不變。</summary>
    public void SetFraming(TimelineClip clip, ClipFraming framing)
    {
        if (clip.Track != TrackKind.Video) throw new ArgumentException("只有影片軌的片段可以調整畫面位置。");
        clip.Framing = framing;
    }

    /// <summary>切換倒放。位置與長度不變，所以不會與其他片段重疊。</summary>
    public void SetReversed(TimelineClip clip, bool reversed)
    {
        if (clip.Track != TrackKind.Video) throw new ArgumentException("只有影片軌的片段可以倒放。");
        clip.Reversed = reversed;
    }

    public TimelineClip? VideoClipAt(double time) => ClipAt(_video, time);
    public TimelineClip? MusicClipAt(double time) => ClipAt(_music, time);

    private static TimelineClip? ClipAt(List<TimelineClip> list, double time)
    {
        foreach (var clip in list)
            if (time >= clip.Start - Epsilon && time < clip.End - Epsilon) return clip;
        return null;
    }

    /// <summary>將片段移到最接近目標、且不與其他片段重疊的位置，回傳實際位置。</summary>
    public double Move(TimelineClip clip, double targetStart)
    {
        var list = ListOf(clip.Track);
        var others = list.Where(c => c != clip).ToList();
        var candidates = new List<double> { Math.Max(0, targetStart), 0 };
        foreach (var other in others)
        {
            candidates.Add(other.End);
            candidates.Add(other.Start - clip.Duration);
        }

        // 最後一段之後永遠有空位，所以一定找得到候選位置。
        clip.Start = candidates
            .Where(s => s >= -Epsilon && others.All(o => s + clip.Duration <= o.Start + Epsilon || s >= o.End - Epsilon))
            .OrderBy(s => Math.Abs(s - targetStart))
            .First();
        Sort(list);
        return clip.Start;
    }

    /// <summary>
    /// 拖曳左緣：片段右緣固定，受來源範圍、左鄰與最短長度限制。
    /// 倒放時左緣是來源的結尾，所以改的是 SourceOut。改動的入出點會對齊來源影格（見 AlignToFrame）。
    /// </summary>
    public void TrimStart(TimelineClip clip, double targetStart)
    {
        var previousEnd = ListOf(clip.Track).Where(c => c != clip && c.End <= clip.Start + Epsilon)
            .Select(c => c.End).DefaultIfEmpty(0).Max();
        var available = clip.Reversed ? clip.Media.Duration - clip.SourceOut : clip.SourceIn;
        var earliest = Math.Max(previousEnd, clip.Start - available);
        var latest = Math.Max(earliest, clip.End - MinClipDuration);
        var start = Math.Clamp(targetStart, earliest, latest);
        // 左緣往右移 d 秒：正放時 SourceIn 加 d，倒放時 SourceOut 減 d。
        var sign = clip.Reversed ? -1 : 1;
        var edge = clip.Reversed ? clip.SourceOut : clip.SourceIn;
        double Source(double time) => edge + sign * (time - clip.Start);
        var source = AlignToFrame(Source(start), Math.Min(Source(earliest), Source(latest)),
            Math.Max(Source(earliest), Source(latest)), clip.Media);
        start = clip.Start + sign * (source - edge);
        if (clip.Reversed) clip.SourceOut = source;
        else clip.SourceIn = source;
        clip.Start = start;
    }

    /// <summary>
    /// 拖曳右緣：片段左緣固定，受來源範圍、右鄰與最短長度限制。
    /// 倒放時右緣是來源的開頭，所以改的是 SourceIn。改動的入出點會對齊來源影格（見 AlignToFrame）。
    /// </summary>
    public void TrimEnd(TimelineClip clip, double targetEnd)
    {
        var nextStart = ListOf(clip.Track).Where(c => c != clip && c.Start >= clip.End - Epsilon)
            .Select(c => c.Start).DefaultIfEmpty(double.MaxValue).Min();
        var available = clip.Reversed ? clip.SourceOut : clip.Media.Duration - clip.SourceIn;
        var earliest = clip.Start + MinClipDuration;
        var latest = Math.Max(earliest, Math.Min(nextStart, clip.Start + available));
        var end = Math.Clamp(targetEnd, earliest, latest);
        // 右緣位置對應的來源時間：正放是出點，倒放是入點。
        var sign = clip.Reversed ? -1 : 1;
        var edge = clip.Reversed ? clip.SourceOut : clip.SourceIn;
        double Source(double time) => edge + sign * (time - clip.Start);
        var source = AlignToFrame(Source(end), Math.Min(Source(earliest), Source(latest)),
            Math.Max(Source(earliest), Source(latest)), clip.Media);
        if (clip.Reversed) clip.SourceIn = source;
        else clip.SourceOut = source;
    }

    /// <summary>
    /// 把拖曳修剪的切點對齊來源影格：與 InOutPoints.Snap 相同，取畫面上顯示的那一格（四捨五入）。
    /// 畫面長度只能是整數格，切點沒對齊時聲音會比畫面短，接合時以靜音補齊、後面的片段跟著延後。
    /// 四捨五入後超出允許範圍（會蓋到相鄰片段、短於最短長度或超出素材）時，改取範圍內最近的一格；
    /// 範圍內沒有任何一格，或素材沒有影格率（純音訊）時，維持原本的時間。
    /// </summary>
    private static double AlignToFrame(double source, double min, double max, MediaInfo media)
    {
        var fps = media.FrameRate.Value;
        if (fps <= 0) return source;
        double frame = Ffmpeg.FrameExtractor.FrameIndex(source, fps);
        if (frame / fps < min - Epsilon) frame = Math.Ceiling(min * fps - Epsilon);
        if (frame / fps > max + Epsilon) frame = Math.Floor(max * fps + Epsilon);
        var aligned = frame / fps;
        return aligned >= min - Epsilon && aligned <= max + Epsilon ? aligned : source;
    }

    /// <summary>與相鄰片段交換順序；兩段合占的範圍與中間空白不變，不影響其他片段。</summary>
    public bool Swap(TimelineClip clip, int direction)
    {
        var list = ListOf(clip.Track);
        var index = list.IndexOf(clip);
        var other = index + direction;
        if (index < 0 || other < 0 || other >= list.Count) return false;

        var first = list[Math.Min(index, other)];
        var second = list[Math.Max(index, other)];
        var secondOldEnd = second.End;
        second.Start = first.Start;
        first.Start = secondOldEnd - first.Duration;
        Sort(list);
        return true;
    }

    private static void Sort(List<TimelineClip> list) => list.Sort((a, b) => a.Start.CompareTo(b.Start));
}
