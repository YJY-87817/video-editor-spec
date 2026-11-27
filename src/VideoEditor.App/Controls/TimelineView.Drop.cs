using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using VideoEditor.Core.Editing;
using VideoEditor.Core.Models;

namespace VideoEditor.App.Controls;

/// <summary>
/// 把素材清單的素材、或檔案總管的檔案拖到時間軸（UIUX-PLAN 決定 2）：
/// 放在片段的左半邊就插在它前面，右半邊插在它後面；後面的片段往後移，配樂軌不動。
/// 拖曳中畫出插入線、新片段會佔的位置，以及被推動的片段移動後的位置。
/// </summary>
public sealed partial class TimelineView
{
    private const double Epsilon = 1e-6;
    private static readonly Brush DropGhostBrush = Frozen("#40FFD24A");
    private static readonly Pen DropLinePen = FrozenPen(Palette.Accent, 2);
    private static readonly Pen ShiftedPen = CreateDashedPen();

    /// <summary>拖曳中的資料是不是素材清單的素材；是的話回傳素材與加入後的長度，不是回傳 null。</summary>
    public Func<IDataObject, (MediaInfo Media, double Duration)?>? DraggedMedia { get; set; }

    /// <summary>素材放下。時間是吸附後、還沒套用「插在前面或後面」規則的時間（由 <see cref="TimelineEditor.AddAt"/> 套用）。</summary>
    public event Action<MediaInfo, double>? MediaDropped;

    /// <summary>從檔案總管拖進檔案並放下：先匯入，再放到這個時間。</summary>
    public event Action<string[], double>? FilesDropped;

    /// <param name="Track">新片段的軌道；拖檔案時還不知道是影片還是聲音，為 null。</param>
    /// <param name="Shifted">會被往後推的片段。</param>
    private sealed record DropPreview(TrackKind? Track, double Start, double Duration, double Shift, IReadOnlyList<TimelineClip> Shifted, string Label);

    private DropPreview? _drop;

    protected override void OnDragEnter(DragEventArgs e) => UpdateDrop(e);

    protected override void OnDragOver(DragEventArgs e) => UpdateDrop(e);

    protected override void OnDragLeave(DragEventArgs e)
    {
        base.OnDragLeave(e);
        StopAutoScroll();
        _drop = null;
        InvalidateVisual();
    }

    protected override void OnDrop(DragEventArgs e)
    {
        var time = DropTime(e);
        StopAutoScroll();
        _drop = null;
        InvalidateVisual();
        if (DraggedMedia?.Invoke(e.Data) is { } dragged) MediaDropped?.Invoke(dragged.Media, time);
        else if (e.Data.GetData(DataFormats.FileDrop) is string[] files) FilesDropped?.Invoke(files, time);
        else return;
        // 不再傳給視窗（視窗收到檔案只會匯入，不會放到時間軸）。
        e.Handled = true;
    }

    private void UpdateDrop(DragEventArgs e)
    {
        UpdateAutoScroll(e.GetPosition(this), dropping: true);
        var time = DropTime(e);
        if (DraggedMedia?.Invoke(e.Data) is { } dragged)
        {
            _drop = Preview(dragged.Media, dragged.Duration, time);
            e.Effects = DragDropEffects.Copy;
        }
        else if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            _drop = new DropPreview(null, time, 0, 0, [], "匯入後放在這裡");
            e.Effects = DragDropEffects.Copy;
        }
        else
        {
            _drop = null;
            e.Effects = DragDropEffects.None;
        }
        e.Handled = true;
        InvalidateVisual();
    }

    /// <summary>放下的時間：靠近片段邊緣、播放頭或時間軸開頭時貼齊（和移動片段相同，按住 Alt 或關閉「吸附片段」時不吸附）。</summary>
    private double DropTime(DragEventArgs e)
    {
        var time = XToTime(e.GetPosition(this).X);
        if (!SnapToClips || e.KeyStates.HasFlag(DragDropKeyStates.AltKey)) return time;
        var tolerance = SnapPixels / _pixelsPerSecond;
        var best = time;
        var bestDistance = tolerance;
        foreach (var target in _timeline.AllClips.SelectMany(c => new[] { c.Start, c.End }).Append(_playhead).Append(0))
        {
            var distance = Math.Abs(target - time);
            if (distance > bestDistance) continue;
            best = target;
            bestDistance = distance;
        }
        return best;
    }

    /// <summary>和 <see cref="Timeline.Insert"/> 相同的規則，算出放下後會怎樣，給拖曳中畫預覽用。</summary>
    private DropPreview Preview(MediaInfo media, double duration, double time)
    {
        var track = Timeline.DefaultTrack(media);
        var clips = track == TrackKind.Video ? _timeline.VideoClips : _timeline.MusicClips;
        var start = new TimelineEditor(_timeline).InsertTarget(track, time);
        var later = clips.Where(c => c.Start >= start - Epsilon).ToList();
        var shift = later.Count == 0 ? 0 : Math.Max(0, start + duration - later.Min(c => c.Start));
        var shifted = shift > Epsilon ? later : [];

        var onClip = clips.FirstOrDefault(c => time >= c.Start && time < c.End);
        var label = onClip == null ? "放在這裡"
            : $"插入到 {onClip.Media.FileName} {(Math.Abs(start - onClip.Start) < Epsilon ? "前面" : "後面")}";
        if (shifted.Count > 0) label += $"，後面 {shifted.Count} 段往後移 {shift.ToString("0.##", CultureInfo.InvariantCulture)} 秒";
        return new DropPreview(track, start, duration, shift, shifted, label);
    }

    private void DrawDropPreview(DrawingContext dc)
    {
        if (_drop is not { } drop) return;
        var x = TimeToX(drop.Start);
        double top, bottom;
        switch (drop.Track)
        {
            case TrackKind.Video:
                top = VideoRowY;
                bottom = OriginalAudioRowY + AudioRowHeight;
                break;
            case TrackKind.Music:
                top = MusicRowY;
                bottom = MusicRowY + AudioRowHeight;
                break;
            default:
                top = VideoRowY;
                bottom = MusicRowY + AudioRowHeight;
                break;
        }

        // 被推動的片段：在移動後的位置畫虛線框。
        foreach (var clip in drop.Shifted)
        {
            var left = TimeToX(clip.Start + drop.Shift);
            dc.DrawRoundedRectangle(null, ShiftedPen, new Rect(left, top + 2, Math.Max(1, clip.Duration * _pixelsPerSecond), bottom - top - 4), 3, 3);
        }
        // 新片段會佔的位置。
        if (drop.Duration > 0)
            dc.DrawRoundedRectangle(DropGhostBrush, ShiftedPen, new Rect(x, top + 1, Math.Max(1, drop.Duration * _pixelsPerSecond), bottom - top - 2), 3, 3);
        dc.DrawLine(DropLinePen, new Point(x, RulerHeight), new Point(x, bottom));
        DrawText(dc, drop.Label, x + 4, RulerHeight - 16, TextBrush, Math.Max(0, ActualWidth - x - 8), LabelBackBrush);
    }

    private static Pen CreateDashedPen()
    {
        var pen = new Pen(Palette.Accent, 1.5) { DashStyle = DashStyles.Dash };
        pen.Freeze();
        return pen;
    }
}
