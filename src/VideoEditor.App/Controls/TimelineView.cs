using System.Globalization;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Media;
using VideoEditor.Core.Models;
using VideoEditor.Core.Editing;

namespace VideoEditor.App.Controls;

/// <summary>時間軸：V1（影片）、A1（影片原音，與 V1 連動）、A2（配樂）。自行繪製以維持大量片段時的流暢度。</summary>
public sealed partial class TimelineView : FrameworkElement
{
    private const double HeaderWidth = 44;
    private const double RulerHeight = 24;
    private const double VideoRowHeight = 56;
    private const double AudioRowHeight = 48;
    private const double RowGap = 4;
    private const double EdgeGrab = 7;
    private const double MinPixelsPerSecond = 2;
    private const double MaxPixelsPerSecond = 800;
    /// <summary>拉動片段邊緣時，離播放頭這麼多像素以內就貼齊播放頭；按住 Alt 可暫時關閉。</summary>
    private const double SnapPixels = 8;

    private static readonly Brush Background = Frozen("#181818");
    private static readonly Brush RowBrush = Frozen("#232323");
    private static readonly Brush HeaderBrush = Frozen("#2B2B2B");
    private static readonly Brush VideoClipBrush = Frozen("#2F6DB5");
    /// <summary>圖片片段：和影片片段同色系但偏青，一眼分得出來。</summary>
    private static readonly Brush StillClipBrush = Frozen("#2A8C9E");
    private static readonly Brush AudioClipBrush = Frozen("#2E8B6E");
    private static readonly Brush MusicClipBrush = Frozen("#8A6BBE");
    private static readonly Brush SilentClipBrush = Frozen("#3A3A3A");
    private static readonly Brush TextBrush = Frozen("#E8E8E8");
    private static readonly Brush DimTextBrush = Frozen("#9A9A9A");
    private static readonly Brush WaveBrush = Frozen("#8CFFFFFF");
    /// <summary>音量放大後超過 0 dB、匯出時會破音的部分。</summary>
    private static readonly Brush ClippingBrush = Frozen("#C8FF4040");
    /// <summary>波形上的文字墊一層深色底，才看得清楚。</summary>
    private static readonly Brush LabelBackBrush = Frozen("#B4141414");
    /// <summary>拖曳邊緣時，把會被當成停頓的靜音範圍塗暗。</summary>
    private static readonly Brush SilenceShadeBrush = Frozen("#80000000");
    private static readonly Pen TickPen = FrozenPen(Frozen("#555555"), 1);
    private static readonly Pen SelectedPen = FrozenPen(Palette.Accent, 2);
    private static readonly Pen PlayheadPen = FrozenPen(Frozen("#FF4D4D"), 1.5);
    /// <summary>片段邊緣貼齊播放頭時，播放頭改用這支筆，讓人看得出已經對齊。</summary>
    private static readonly Pen SnappedPlayheadPen = FrozenPen(Palette.Accent, 2.5);
    private static readonly Typeface Face = new("Microsoft JhengHei UI");

    private enum DragKind { None, Playhead, Move, TrimStart, TrimEnd }

    private Timeline _timeline = new();
    private double _playhead;
    private double _pixelsPerSecond = 40;
    private double _scrollOffset;
    private DragKind _drag;
    private TimelineClip? _dragClip;
    private double _dragGrabOffset;
    private SnapKind _snapKind;
    private double _snapTarget;
    // 拖曳邊緣時這個片段聲音的靜音區間（來源時間）與吸附點（時間軸時間、已排序）；開始拖曳時算一次。
    private IReadOnlyList<TimeRange>? _dragSilences;
    private IReadOnlyList<double> _dragSilenceTargets = [];
    // 拖曳時其他片段（兩軌）的開頭與結尾，排序過；開始拖曳時算一次。
    private IReadOnlyList<double> _dragClipEdges = [];

    public TimelineView()
    {
        ClipToBounds = true;
        Focusable = true;
        AllowDrop = true;
    }

    public TimelineClip? SelectedClip { get; private set; }

    public event Action<double>? PlayheadDragged;
    public event Action<TimelineClip?>? SelectionChanged;
    /// <summary>滑鼠按在片段上（選取、移動或修剪）時觸發，在 <see cref="EditStarted"/> 之後、拖曳狀態設好之後。</summary>
    public event Action? ClipPressed;
    /// <summary>開始拖曳片段前觸發，讓預覽先停下來。</summary>
    public event Action? EditStarted;

    /// <summary>拖曳片段（移動或修剪）放開滑鼠時觸發，帶操作名稱（給復原紀錄），在 <see cref="ClipsChanged"/> 之前。</summary>
    public event Action<string>? EditCompleted;

    /// <summary>正在拖曳播放頭或片段。</summary>
    public bool IsDragging => _drag != DragKind.None;
    public event Action? ClipsChanged;
    /// <summary>捲動或縮放改變。</summary>
    public event Action? ViewChanged;

    /// <summary>取得素材已產生好的波形；還沒好或沒有時回傳 null。沒設定就不畫波形。</summary>
    public Func<MediaInfo, WaveformData?>? WaveformOf { get; set; }
    /// <summary>素材的波形是否還在產生中（片段上會加註）。</summary>
    public Func<MediaInfo, bool>? WaveformPending { get; set; }

    /// <summary>修剪時是否吸附到片段聲音的靜音處（時間軸工具列的「吸附靜音」）。</summary>
    public bool SnapToSilence { get; set; } = true;

    /// <summary>
    /// 時間軸工具列的「吸附片段」。關閉時移動片段完全不吸附，修剪時不吸附其他片段的邊緣（播放頭與靜音處照舊）。
    /// </summary>
    public bool SnapToClips { get; set; } = true;

    /// <summary>時間尺的刻度：時間碼時，不到一秒的刻度寫成「分:秒:格」。</summary>
    public VideoEditor.Core.Playback.TimeDisplay RulerDisplay { get; set; } = VideoEditor.Core.Playback.TimeDisplay.Timecode;

    /// <summary>時間尺換算格數用的影格率（輸出基準）。</summary>
    public double RulerFps { get; set; } = VideoEditor.Core.Playback.Timecode.FallbackFps;

    public double PixelsPerSecond => _pixelsPerSecond;
    public double ScrollOffset => _scrollOffset;
    public double ViewportWidth => Math.Max(1, ActualWidth - HeaderWidth);
    public double ContentWidth => (_timeline.Duration + 10) * _pixelsPerSecond;

    public Timeline Timeline
    {
        get => _timeline;
        set
        {
            _timeline = value;
            InvalidateVisual();
        }
    }

    public double Playhead
    {
        get => _playhead;
        set
        {
            _playhead = value;
            KeepPlayheadVisible();
            InvalidateVisual();
        }
    }

    public void Select(TimelineClip? clip)
    {
        if (SelectedClip == clip) return;
        SelectedClip = clip;
        SelectionChanged?.Invoke(clip);
        InvalidateVisual();
    }

    public void SetScrollOffset(double offset)
    {
        _scrollOffset = Math.Clamp(offset, 0, Math.Max(0, ContentWidth - ViewportWidth));
        ViewChanged?.Invoke();
        InvalidateVisual();
    }

    /// <summary>縮放，並讓 anchorTime 留在畫面上的同一位置。</summary>
    public void SetZoom(double pixelsPerSecond, double? anchorTime = null)
    {
        var anchor = anchorTime ?? _playhead;
        var anchorX = TimeToX(anchor);
        _pixelsPerSecond = Math.Clamp(pixelsPerSecond, MinPixelsPerSecond, MaxPixelsPerSecond);
        SetScrollOffset(anchor * _pixelsPerSecond - (anchorX - HeaderWidth));
    }

    /// <summary>縮放到可看見整條時間軸。</summary>
    public void ZoomToFit()
    {
        var duration = Math.Max(1, _timeline.Duration);
        _pixelsPerSecond = Math.Clamp(ViewportWidth * 0.95 / duration, MinPixelsPerSecond, MaxPixelsPerSecond);
        SetScrollOffset(0);
    }

    private double TimeToX(double time) => HeaderWidth + time * _pixelsPerSecond - _scrollOffset;
    private double XToTime(double x) => Math.Max(0, (x - HeaderWidth + _scrollOffset) / _pixelsPerSecond);

    private static double VideoRowY => RulerHeight + RowGap;
    private static double OriginalAudioRowY => VideoRowY + VideoRowHeight + RowGap;
    private static double MusicRowY => OriginalAudioRowY + AudioRowHeight + RowGap;

    private void KeepPlayheadVisible()
    {
        var x = TimeToX(_playhead);
        if (x < HeaderWidth) SetScrollOffset(_playhead * _pixelsPerSecond);
        else if (x > ActualWidth - 10) SetScrollOffset(_playhead * _pixelsPerSecond - ViewportWidth * 0.2);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Background, null, new Rect(0, 0, ActualWidth, ActualHeight));
        var width = ActualWidth;

        DrawRow(dc, "V1", VideoRowY, VideoRowHeight, width);
        DrawRow(dc, "A1", OriginalAudioRowY, AudioRowHeight, width);
        DrawRow(dc, "A2", MusicRowY, AudioRowHeight, width);

        dc.PushClip(new RectangleGeometry(new Rect(HeaderWidth, 0, Math.Max(0, width - HeaderWidth), ActualHeight)));
        DrawRuler(dc, width);
        foreach (var clip in _timeline.VideoClips)
        {
            DrawClip(dc, clip, VideoRowY, VideoRowHeight, clip.Media.IsStill ? StillClipBrush : VideoClipBrush, ClipLabel(clip));
            var hasAudio = clip.Media.HasAudio;
            DrawClip(dc, clip, OriginalAudioRowY, AudioRowHeight, hasAudio ? AudioClipBrush : SilentClipBrush,
                hasAudio ? VolumeLabel(clip) : "無音訊", audio: hasAudio);
        }
        foreach (var clip in _timeline.MusicClips)
            DrawClip(dc, clip, MusicRowY, AudioRowHeight, MusicClipBrush, $"{clip.Media.FileName}  {VolumeLabel(clip)}", audio: true);
        if (_timeline.VideoClips.Count == 0 && _timeline.MusicClips.Count == 0) DrawEmptyHint(dc, width);

        DrawSnapMark(dc);
        DrawDropPreview(dc);
        var playheadX = TimeToX(_playhead);
        dc.DrawLine(_snapKind == SnapKind.Playhead ? SnappedPlayheadPen : PlayheadPen, new Point(playheadX, 0), new Point(playheadX, ActualHeight));
        dc.Pop();
    }

    private static string ClipLabel(TimelineClip clip)
    {
        var label = clip.Reversed ? $"⟲ 倒放  {clip.Media.FileName}" : clip.Media.IsStill ? $"圖片  {clip.Media.FileName}" : clip.Media.FileName;
        if (!clip.Transform.IsIdentity) label = $"{label}  {clip.Transform.Label}";
        return clip.Framing.IsDefault ? label : $"{label}  {clip.Framing.ShortLabel}";
    }

    private static string VolumeLabel(TimelineClip clip) => $"音量 {clip.Volume * 100:0}%";

    private void DrawRow(DrawingContext dc, string name, double y, double height, double width)
    {
        dc.DrawRectangle(RowBrush, null, new Rect(HeaderWidth, y, Math.Max(0, width - HeaderWidth), height));
        dc.DrawRectangle(HeaderBrush, null, new Rect(0, y, HeaderWidth, height));
        DrawText(dc, name, 12, y + height / 2 - 9, TextBrush, HeaderWidth);
    }

    private void DrawRuler(DrawingContext dc, double width)
    {
        var step = TickStep();
        var first = Math.Floor(XToTime(HeaderWidth) / step) * step;
        for (var t = first; TimeToX(t) < width; t += step)
        {
            var x = TimeToX(t);
            dc.DrawLine(TickPen, new Point(x, RulerHeight - 8), new Point(x, RulerHeight));
            DrawText(dc, FormatTick(t), x + 3, 3, DimTextBrush, 100);
        }
    }

    /// <summary>挑一個讓刻度間距至少 80 像素的整數間隔。</summary>
    private double TickStep()
    {
        foreach (var step in new[] { 0.1, 0.5, 1, 2, 5, 10, 30, 60, 120, 300, 600, 1800, 3600 })
            if (step * _pixelsPerSecond >= 80) return step;
        return 7200;
    }

    private string FormatTick(double t)
    {
        var span = TimeSpan.FromSeconds(Math.Round(t, 1));
        if (span.Milliseconds == 0) return $"{(int)span.TotalMinutes}:{span.Seconds:00}";
        if (RulerDisplay == VideoEditor.Core.Playback.TimeDisplay.Timecode)
            return $"{(int)span.TotalMinutes}:{span.Seconds:00}:{(int)Math.Floor(span.Milliseconds / 1000.0 * RulerFps + 1e-6):00}";
        return $"{(int)span.TotalMinutes}:{span.Seconds:00}.{span.Milliseconds / 100}";
    }

    private void DrawClip(DrawingContext dc, TimelineClip clip, double y, double height, Brush brush, string label, bool audio = false)
    {
        var x = TimeToX(clip.Start);
        var w = clip.Duration * _pixelsPerSecond;
        if (x + w < HeaderWidth || x > ActualWidth) return;
        var rect = new Rect(x, y + 1, Math.Max(1, w), height - 2);
        dc.DrawRoundedRectangle(brush, null, rect, 3, 3);
        Brush? labelBack = null;
        if (audio)
        {
            if (WaveformOf?.Invoke(clip.Media) is { } waveform)
            {
                DrawWaveform(dc, clip, waveform, rect);
                DrawSilenceShade(dc, clip, rect);
                labelBack = LabelBackBrush;
            }
            else if (WaveformPending?.Invoke(clip.Media) == true)
            {
                label += "（波形產生中）";
            }
        }
        // 選取框畫在波形上面，才不會被蓋住。
        if (clip == SelectedClip) dc.DrawRoundedRectangle(null, SelectedPen, rect, 3, 3);
        if (w > 24) DrawText(dc, label, Math.Max(x, HeaderWidth) + 6, y + 4, TextBrush, x + w - Math.Max(x, HeaderWidth) - 10, labelBack);
    }

    /// <summary>
    /// 以片段中線上下對稱畫出波形，只算畫面上看得到的部分，每像素一欄。
    /// 整段組成一個圖形一次畫出，播放中每次重畫的成本才低。超過 0 dB（會破音）的欄另外塗紅。
    /// </summary>
    private void DrawWaveform(DrawingContext dc, TimelineClip clip, WaveformData data, Rect rect)
    {
        if (rect.Width < 3) return;
        var columns = WaveformColumns.Build(clip, data, rect.Left, _pixelsPerSecond, HeaderWidth, ActualWidth);
        if (columns.Count == 0) return;
        var mid = rect.Top + rect.Height / 2;
        var half = rect.Height / 2 - 2;
        // 靜音處留一條細線，看得出片段有音訊、只是沒聲音。
        double Top(WaveformColumn c) => mid - Math.Max(0.5, c.Level * half);
        double Bottom(WaveformColumn c) => mid + Math.Max(0.5, c.Level * half);

        var points = new List<Point>(columns.Count * 2 + 2);
        foreach (var c in columns) points.Add(new Point(c.X + c.Width / 2, Top(c)));
        points.Add(new Point(columns[^1].X + columns[^1].Width, mid));
        for (var i = columns.Count - 1; i >= 0; i--) points.Add(new Point(columns[i].X + columns[i].Width / 2, Bottom(columns[i])));
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(new Point(columns[0].X, mid), isFilled: true, isClosed: true);
            context.PolyLineTo(points, isStroked: false, isSmoothJoin: false);
        }
        geometry.Freeze();
        dc.DrawGeometry(WaveBrush, null, geometry);

        for (var i = 0; i < columns.Count; i++)
        {
            if (!columns[i].Clipped) continue;
            var start = columns[i].X;
            while (i + 1 < columns.Count && columns[i + 1].Clipped) i++;
            dc.DrawRectangle(ClippingBrush, null, new Rect(start, mid - half, columns[i].X + columns[i].Width - start, half * 2));
        }
    }

    private bool IsTrimming => _drag is DragKind.TrimStart or DragKind.TrimEnd;

    /// <summary>拖曳邊緣時，在這個片段的音訊軌塗暗靜音範圍；平常不畫，以免和波形搶視線。</summary>
    private void DrawSilenceShade(DrawingContext dc, TimelineClip clip, Rect rect)
    {
        if (!IsTrimming || clip != _dragClip || _dragSilences == null || !SnapToSilence) return;
        foreach (var silence in _dragSilences)
        {
            var range = TimelineSnap.ToTimeline(clip, silence);
            var left = Math.Max(rect.Left, TimeToX(range.From));
            var right = Math.Min(rect.Right, TimeToX(range.To));
            if (right > left) dc.DrawRectangle(SilenceShadeBrush, null, new Rect(left, rect.Top, right - left, rect.Height));
        }
    }

    /// <summary>
    /// 貼齊時在貼齊的位置畫黃線（貼齊播放頭時則是播放頭變黃）：
    /// 靜音處只畫在該片段的軌道上；片段邊緣與時間軸開頭貫穿所有軌道，看得出對齊的是哪一軌的片段。
    /// </summary>
    private void DrawSnapMark(DrawingContext dc)
    {
        if (_dragClip == null || _snapKind is SnapKind.None or SnapKind.Playhead) return;
        var x = TimeToX(_snapTarget);
        double top = RulerHeight, bottom = MusicRowY + AudioRowHeight;
        if (_snapKind == SnapKind.Silence)
        {
            top = _dragClip.Track == TrackKind.Video ? VideoRowY : MusicRowY;
            bottom = _dragClip.Track == TrackKind.Video ? OriginalAudioRowY + AudioRowHeight : MusicRowY + AudioRowHeight;
        }
        dc.DrawLine(SnappedPlayheadPen, new Point(x, top), new Point(x, bottom));
    }

    /// <summary>時間軸還是空的：在軌道中央說明怎麼把素材放進來。</summary>
    private void DrawEmptyHint(DrawingContext dc, double width)
    {
        const string hint = "把素材從素材清單拖到這裡，或選好素材後按「＋ 加入時間軸」（也可以雙擊素材）。有畫面的放到 V1，只有聲音的放到 A2";
        var formatted = new FormattedText(hint, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Face, 13, DimTextBrush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip)
        {
            MaxTextWidth = Math.Max(1, width - HeaderWidth - 24),
            MaxLineCount = 2,
            TextAlignment = TextAlignment.Center,
            Trimming = TextTrimming.CharacterEllipsis,
        };
        var centerY = (VideoRowY + MusicRowY + AudioRowHeight) / 2;
        dc.DrawText(formatted, new Point(HeaderWidth + 12, centerY - formatted.Height / 2));
    }

    private void DrawText(DrawingContext dc, string text, double x, double y, Brush brush, double maxWidth, Brush? background = null)
    {
        if (maxWidth <= 0) return;
        var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Face, 11, brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip)
        {
            MaxTextWidth = maxWidth,
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
        };
        if (background != null)
            dc.DrawRoundedRectangle(background, null, new Rect(x - 3, y - 1, formatted.WidthIncludingTrailingWhitespace + 6, formatted.Height + 2), 2, 2);
        dc.DrawText(formatted, new Point(x, y));
    }

    private (TimelineClip? Clip, DragKind Kind) HitTest(Point p)
    {
        if (p.X < HeaderWidth) return (null, DragKind.None);
        if (p.Y < RulerHeight) return (null, DragKind.Playhead);

        IEnumerable<TimelineClip> clips;
        if (p.Y >= VideoRowY && p.Y < OriginalAudioRowY + AudioRowHeight) clips = _timeline.VideoClips;
        else if (p.Y >= MusicRowY && p.Y < MusicRowY + AudioRowHeight) clips = _timeline.MusicClips;
        else return (null, DragKind.None);

        foreach (var clip in clips)
        {
            var left = TimeToX(clip.Start);
            var right = TimeToX(clip.End);
            if (p.X < left || p.X > right) continue;
            return (clip, TimelineDrag.Classify(p.X, left, right, EdgeGrab) switch
            {
                ClipHit.TrimStart => DragKind.TrimStart,
                ClipHit.TrimEnd => DragKind.TrimEnd,
                _ => DragKind.Move,
            });
        }
        return (null, DragKind.None);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        var p = e.GetPosition(this);
        var (clip, kind) = HitTest(p);
        if (p.X < HeaderWidth) return;

        if (clip == null)
        {
            // 尺規或空白處：移動播放頭；點空白處同時取消選取。
            if (kind == DragKind.None) Select(null);
            _drag = DragKind.Playhead;
            PlayheadDragged?.Invoke(XToTime(p.X));
        }
        else
        {
            Select(clip);
            EditStarted?.Invoke();
            _drag = kind;
            _dragClip = clip;
            if (kind == DragKind.Move) Cursor = Cursors.SizeAll;
            _dragGrabOffset = XToTime(p.X) - clip.Start;
            if (kind is DragKind.TrimStart or DragKind.TrimEnd) PrepareSilenceSnap(clip, kind == DragKind.TrimStart ? TrimEdge.Start : TrimEdge.End);
            _dragClipEdges = TimelineSnap.ClipEdgeTargets(_timeline, clip);
            // 拖曳狀態都設好了才通知：收到的一方會切換預覽模式、重新排版，不該看到設到一半的狀態。
            ClipPressed?.Invoke();
        }
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        if (_drag == DragKind.None)
        {
            var (_, kind) = HitTest(p);
            // 邊緣可以修剪、片段中間可以抓起來移動、時間尺可以點選時間。
            Cursor = p.X < HeaderWidth ? Cursors.Arrow
                : kind switch
                {
                    DragKind.TrimStart or DragKind.TrimEnd => Cursors.SizeWE,
                    DragKind.Move => Cursors.Hand,
                    _ => p.Y < RulerHeight ? Cursors.IBeam : Cursors.Arrow,
                };
            return;
        }

        var time = XToTime(p.X);
        switch (_drag)
        {
            case DragKind.Playhead:
                PlayheadDragged?.Invoke(time);
                break;
            case DragKind.Move:
                var raw = time - _dragGrabOffset;
                var target = SnapMove(_dragClip!, raw);
                // 貼齊的位置放不下時不算貼齊，改照滑鼠原本的位置移動。
                if (!TimelineDrag.MoveWithSnap(_timeline, _dragClip!, raw, target)) _snapKind = SnapKind.None;
                break;
            case DragKind.TrimStart:
                _timeline.TrimStart(_dragClip!, Snap(time));
                KeepSnapOnlyIfReached(_dragClip!, _dragClip!.Start);
                break;
            case DragKind.TrimEnd:
                _timeline.TrimEnd(_dragClip!, Snap(time));
                KeepSnapOnlyIfReached(_dragClip!, _dragClip!.End);
                break;
        }
        InvalidateVisual();
    }

    /// <summary>
    /// 按右鍵先選取游標下的片段（空白處取消選取），右鍵選單再依選取的片段決定能做什麼。
    /// 和左鍵點片段一樣通知 <see cref="ClipPressed"/>（切到剪輯預覽），選單裡的「在播放頭分割」才用得了。
    /// </summary>
    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        if (_drag != DragKind.None) return;
        Focus();
        var p = e.GetPosition(this);
        // 軌道名稱欄不是片段：取消選取，選單只顯示時間軸的項目，不會對看不到的片段動手。
        var clip = p.X < HeaderWidth ? null : HitTest(p).Clip;
        Select(clip);
        if (clip != null) ClipPressed?.Invoke();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        EndDrag();
        ReleaseMouseCapture();
    }

    /// <summary>
    /// 拖曳到一半失去滑鼠（切換視窗、跳出對話框）時，不會收到放開按鍵的事件。
    /// 在這裡結束拖曳，否則之後滑鼠只要經過時間軸，片段或播放頭就會跟著游標移動。
    /// </summary>
    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_drag != DragKind.None) EndDrag();
    }

    private void EndDrag()
    {
        var editLabel = _drag switch
        {
            DragKind.Move => "移動片段",
            DragKind.TrimStart or DragKind.TrimEnd => "修剪片段",
            _ => null,
        };
        _drag = DragKind.None;
        _dragClip = null;
        _snapKind = SnapKind.None;
        _dragSilences = null;
        _dragSilenceTargets = [];
        _dragClipEdges = [];
        if (editLabel != null)
        {
            EditCompleted?.Invoke(editLabel);
            ClipsChanged?.Invoke();
        }
        ViewChanged?.Invoke();
        InvalidateVisual();
    }

    /// <summary>
    /// 開始拖曳邊緣時，找出這個片段聲音的靜音處與吸附點。波形還沒產生好或沒有音訊時只吸附播放頭；
    /// 拖曳中途波形才好的話，下次拖曳才用得到。
    /// </summary>
    private void PrepareSilenceSnap(TimelineClip clip, TrimEdge edge)
    {
        if (!clip.Media.HasAudio || WaveformOf?.Invoke(clip.Media) is not { } waveform) return;
        _dragSilences = SilenceDetector.Find(waveform);
        _dragSilenceTargets = TimelineSnap.SilenceTargets(clip, _dragSilences, edge);
    }

    /// <summary>
    /// 剪輯點靠近播放頭或靜音處時貼齊，取比較近的一個（一樣近時以播放頭優先）。
    /// 吸附範圍以像素計，換算成目前縮放比例下的秒數。按住 Alt 全部關閉；取消「吸附靜音」時只吸附播放頭。
    /// </summary>
    private double Snap(double time)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
        {
            _snapKind = SnapKind.None;
            return time;
        }
        var result = TimelineSnap.Snap(time, _playhead, SnapToSilence ? _dragSilenceTargets : [], SnapPixels / _pixelsPerSecond,
            SnapToClips ? _dragClipEdges : null);
        _snapKind = result.Kind;
        _snapTarget = result.Target;
        return result.Time;
    }

    /// <summary>
    /// 移動時片段的開頭或結尾貼齊播放頭、其他片段的邊緣或時間軸開頭，回傳片段新的開頭。
    /// 按住 Alt 或取消「吸附片段」時不吸附。
    /// </summary>
    private double SnapMove(TimelineClip clip, double start)
    {
        if (!SnapToClips || Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
        {
            _snapKind = SnapKind.None;
            return start;
        }
        var result = TimelineSnap.SnapMove(start, clip.Duration, _playhead, _dragClipEdges, SnapPixels / _pixelsPerSecond);
        _snapKind = result.Kind;
        _snapTarget = result.Target;
        return result.Time;
    }

    /// <summary>邊緣受來源長度或相鄰片段限制而停不到吸附點時，不算貼齊。</summary>
    private void KeepSnapOnlyIfReached(TimelineClip clip, double edge)
    {
        if (_snapKind != SnapKind.None && !TimelineSnap.IsAt(edge, _snapTarget, clip.Media.FrameRate.Value)) _snapKind = SnapKind.None;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            SetZoom(_pixelsPerSecond * (e.Delta > 0 ? 1.25 : 0.8), XToTime(e.GetPosition(this).X));
        else
            SetScrollOffset(_scrollOffset - e.Delta);
        e.Handled = true;
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        ViewChanged?.Invoke();
    }

    private static Brush Frozen(string color)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(color)!;
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen(Brush brush, double thickness)
    {
        var pen = new Pen(brush, thickness);
        pen.Freeze();
        return pen;
    }
}
