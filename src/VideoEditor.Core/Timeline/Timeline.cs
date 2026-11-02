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

    public double Duration => SourceOut - SourceIn;
    public double End => Start + Duration;
    public double ToSourceTime(double timelineTime) => SourceIn + (timelineTime - Start);
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

    /// <summary>拖曳左緣：片段右緣固定，受來源起點、左鄰與最短長度限制。</summary>
    public void TrimStart(TimelineClip clip, double targetStart)
    {
        var previousEnd = ListOf(clip.Track).Where(c => c != clip && c.End <= clip.Start + Epsilon)
            .Select(c => c.End).DefaultIfEmpty(0).Max();
        var earliest = Math.Max(previousEnd, clip.Start - clip.SourceIn);
        var latest = clip.End - MinClipDuration;
        var start = Math.Clamp(targetStart, earliest, Math.Max(earliest, latest));
        clip.SourceIn += start - clip.Start;
        clip.Start = start;
    }

    /// <summary>拖曳右緣：片段左緣固定，受來源長度、右鄰與最短長度限制。</summary>
    public void TrimEnd(TimelineClip clip, double targetEnd)
    {
        var nextStart = ListOf(clip.Track).Where(c => c != clip && c.Start >= clip.End - Epsilon)
            .Select(c => c.Start).DefaultIfEmpty(double.MaxValue).Min();
        var latest = Math.Min(nextStart, clip.Start + (clip.Media.Duration - clip.SourceIn));
        var earliest = clip.Start + MinClipDuration;
        var end = Math.Clamp(targetEnd, earliest, Math.Max(earliest, latest));
        clip.SourceOut = clip.SourceIn + (end - clip.Start);
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
