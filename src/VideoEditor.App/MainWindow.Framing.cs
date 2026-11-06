using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using VideoEditor.Core.Models;
using VideoEditor.Core.Timeline;

namespace VideoEditor.App;

public partial class MainWindow
{
    // ---- 畫面位置：剪輯預覽＋填滿裁切時，拖曳黃色框選取要保留的範圍 ----

    private const double HandleSize = 12;
    private const double WheelZoomStep = 1.1;

    private enum FramingDragKind { None, Move, Resize }

    /// <summary>目前預覽框對應、可以拖曳調整的片段；不可調整時為 null。</summary>
    private TimelineClip? _framingClip;
    private PreviewPlan _previewPlan = PreviewPlan.Hidden;
    private FramingDragKind _framingDrag;
    private TimelineClip? _framingDragClip;
    private Point _framingDragStart;
    private ClipFraming _framingAtStart = ClipFraming.Default;
    // 縮放時固定不動的對角（來源像素），與拖曳的角往哪個方向延伸。
    private double _framingAnchorX, _framingAnchorY;
    private int _framingDirectionX, _framingDirectionY;

    /// <summary>
    /// 預覽上正在顯示、可調整畫面位置的片段：剪輯預覽、填滿裁切，且畫面確實是這個片段
    /// （切換片段時新畫面還沒出來前，不讓框對到別的片段）。
    /// </summary>
    private TimelineClip? FramingClip()
    {
        if (!IsTimelinePreview || SelectedMode != FitMode.Fill || _engine == null || _surfaceMedia is not { HasVideo: true }) return null;
        var clip = _timeline.VideoClipAt(_engine.Position);
        return clip != null && clip.Media == _surfaceMedia && clip.Transform == _surfaceTransform ? clip : null;
    }

    /// <summary>片段轉完方向後的畫面尺寸，與目前設定的輸出尺寸。</summary>
    private (Size2 Source, Size2 Output)? FramingSizes(TimelineClip clip) =>
        OutputReferenceSize() is { } reference
            ? (clip.DisplaySize, OutputSize.Compute(reference.Width, reference.Height, SelectedRatio))
            : null;

    /// <summary>依排版結果顯示或收起拖曳用的框與四個角。</summary>
    private void UpdateFramingEditor(PreviewPlan plan, TimelineClip? clip)
    {
        _previewPlan = plan;
        _framingClip = plan.Visible && plan.DimCrop ? clip : null;
        var editable = _framingClip != null;

        FrameBorder.IsHitTestVisible = editable;
        FrameBorder.Fill = editable ? Brushes.Transparent : null; // 透明填色，框內任何位置都能按住拖曳
        FrameBorder.Cursor = editable ? Cursors.SizeAll : null;
        FrameBorder.ToolTip = editable ? "拖曳移動；拖曳四角縮放；滾輪縮放；雙擊重設" : null;

        var frame = plan.Frame;
        PlaceHandle(FrameHandleTopLeft, frame.X, frame.Y, editable);
        PlaceHandle(FrameHandleTopRight, frame.X + frame.Width, frame.Y, editable);
        PlaceHandle(FrameHandleBottomLeft, frame.X, frame.Y + frame.Height, editable);
        PlaceHandle(FrameHandleBottomRight, frame.X + frame.Width, frame.Y + frame.Height, editable);
    }

    private static void PlaceHandle(Rectangle handle, double x, double y, bool visible)
    {
        handle.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        Canvas.SetLeft(handle, x - HandleSize / 2);
        Canvas.SetTop(handle, y - HandleSize / 2);
    }

    /// <summary>播放或移動播放頭進到另一個片段時，框要換成那個片段的畫面位置。</summary>
    private void UpdateFramingForPosition()
    {
        if (IsTimelinePreview && FramingClip() != _framingClip) UpdatePreviewLayout();
    }

    private void UpdateFramingControls(TimelineClip? clip)
    {
        var video = clip is { Track: TrackKind.Video };
        ResetFramingButton.IsEnabled = video && !clip!.Framing.IsDefault;
        FramingText.Text = !video ? "—"
            : SelectedMode != FitMode.Fill ? "加黑邊時不裁切，不使用畫面位置。"
            : $"{clip!.Framing.Label}。在剪輯預覽中拖曳黃色框移動、拖曳四角縮放，也可以用滑鼠滾輪縮放、雙擊框重設。";
    }

    private void ApplyFraming(TimelineClip clip, ClipFraming framing)
    {
        _timeline.SetFraming(clip, framing);
        UpdatePreviewLayout();
        UpdateFramingControls(Timeline.SelectedClip);
    }

    private void OnResetFraming(object sender, RoutedEventArgs e)
    {
        if (Timeline.SelectedClip is { Track: TrackKind.Video } clip) ApplyFraming(clip, ClipFraming.Default);
    }

    private void OnFramingMoveStart(object sender, MouseButtonEventArgs e)
    {
        if (_framingClip is not { } clip) return;
        e.Handled = true;
        if (e.ClickCount == 2)
        {
            ApplyFraming(clip, ClipFraming.Default);
            return;
        }
        BeginFramingDrag(clip, FramingDragKind.Move, e);
    }

    private void OnFramingResizeStart(object sender, MouseButtonEventArgs e)
    {
        if (_framingClip is not { } clip || FramingSizes(clip) is not { } sizes || sender is not FrameworkElement { Tag: string tag }) return;
        e.Handled = true;
        var parts = tag.Split(',');
        _framingDirectionX = int.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
        _framingDirectionY = int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
        // 對角固定：往右拉時固定左緣，往下拉時固定上緣。
        var crop = clip.Framing.CropRect(sizes.Source, sizes.Output);
        _framingAnchorX = _framingDirectionX > 0 ? crop.X : crop.X + crop.Width;
        _framingAnchorY = _framingDirectionY > 0 ? crop.Y : crop.Y + crop.Height;
        BeginFramingDrag(clip, FramingDragKind.Resize, e);
    }

    private void BeginFramingDrag(TimelineClip clip, FramingDragKind kind, MouseButtonEventArgs e)
    {
        // 播放中拖曳，播放頭會走進別的片段；先暫停，調整的就是畫面上這一格的片段。
        _engine?.Pause();
        Timeline.Select(clip);
        _framingDrag = kind;
        _framingDragClip = clip;
        _framingDragStart = e.GetPosition(PreviewCanvas);
        _framingAtStart = clip.Framing;
        PreviewCanvas.CaptureMouse();
    }

    private void OnFramingDragMove(object sender, MouseEventArgs e)
    {
        if (_framingDrag == FramingDragKind.None || _framingDragClip is not { } clip || FramingSizes(clip) is not { } sizes
            || _previewPlan.Source.Width <= 0) return;
        var (source, output) = sizes;
        var point = e.GetPosition(PreviewCanvas);
        // 預覽上的距離換算成來源像素。
        var scale = source.Width / _previewPlan.Source.Width;
        var next = _framingDrag == FramingDragKind.Move
            ? _framingAtStart.MoveBy((point.X - _framingDragStart.X) * scale, (point.Y - _framingDragStart.Y) * scale, source, output)
            : ClipFraming.ResizeFromCorner(_framingAnchorX, _framingAnchorY, _framingDirectionX, _framingDirectionY,
                (point.X - _previewPlan.Source.X) * scale, (point.Y - _previewPlan.Source.Y) * scale, source, output);
        ApplyFraming(clip, next);
    }

    private void OnFramingDragEnd(object sender, MouseButtonEventArgs e)
    {
        if (_framingDrag == FramingDragKind.None) return;
        EndFramingDrag();
        PreviewCanvas.ReleaseMouseCapture();
    }

    /// <summary>拖曳中切換視窗或跳出對話框而失去滑鼠時，同樣結束拖曳，框才不會跟著游標跑。</summary>
    private void OnFramingCaptureLost(object sender, MouseEventArgs e) => EndFramingDrag();

    private void EndFramingDrag()
    {
        _framingDrag = FramingDragKind.None;
        _framingDragClip = null;
    }

    private void OnFramingWheel(object sender, MouseWheelEventArgs e)
    {
        if (_framingClip is not { } clip || FramingSizes(clip) is not { } sizes) return;
        e.Handled = true;
        Timeline.Select(clip);
        ApplyFraming(clip, clip.Framing.ZoomBy(Math.Pow(WheelZoomStep, e.Delta / 120.0), sizes.Source, sizes.Output));
    }
}
