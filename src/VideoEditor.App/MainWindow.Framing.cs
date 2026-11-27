using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using VideoEditor.Core.Models;
using VideoEditor.Core.Editing;

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
    // 拖曳開始時時間軸的樣子；放開滑鼠時記成一步復原。
    private TimelineSnapshot? _framingDragBefore;
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
        var clip = _timeline.VideoClipForStill(_engine.Position);
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
        // 播放頭走進別的片段時框跟著換，右側的說明與重設按鈕也要跟著換成那一段。
        UpdateFramingControls(TimelineView.SelectedClip);
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

    /// <summary>
    /// 畫面位置說明與重設按鈕對應的片段：預覽上有可拖曳的框時是框對應的那一段
    /// （播放頭可能已經走出選取的片段），否則是選取的片段。
    /// </summary>
    private TimelineClip? FramingTarget(TimelineClip? selected) =>
        _framingClip ?? (selected is { Track: TrackKind.Video } ? selected : null);

    private void UpdateFramingControls(TimelineClip? selected)
    {
        // 加黑邊時不裁切，用不到畫面位置，整區隱藏。
        FramingSection.Visibility = SelectedMode == FitMode.Fill ? Visibility.Visible : Visibility.Collapsed;
        var clip = FramingTarget(selected);
        ResetFramingButton.IsEnabled = clip != null && !clip.Framing.IsDefault;
        if (clip == null)
        {
            FramingText.Text = "—";
            return;
        }
        // 與片段清單的編號相同。
        var index = _timeline.VideoClips.TakeWhile(c => c != clip).Count() + 1;
        FramingText.Text = $"第 {index} 段：{clip.Framing.Label}。在剪輯預覽中拖曳黃色框移動、拖曳四角縮放，也可以用滑鼠滾輪縮放、雙擊框重設。";
    }

    /// <param name="refreshList">拖曳中每次移動都會呼叫，片段清單等放開滑鼠再更新一次就好。</param>
    private void ApplyFraming(TimelineClip clip, ClipFraming framing, bool refreshList = true)
    {
        _timeline.SetFraming(clip, framing);
        UpdatePreviewLayout();
        UpdateFramingControls(TimelineView.SelectedClip);
        TimelineView.InvalidateVisual(); // 時間軸上的片段標示
        if (refreshList) RefreshClipList();
    }

    private void OnResetFraming(object sender, RoutedEventArgs e)
    {
        if (FramingTarget(TimelineView.SelectedClip) is { } clip) ResetFraming(clip);
    }

    private void ResetFraming(TimelineClip clip)
    {
        var before = _history.Capture();
        ApplyFraming(clip, ClipFraming.Default);
        _history.Commit(before, "重設畫面位置");
    }

    private void OnFramingMoveStart(object sender, MouseButtonEventArgs e)
    {
        if (_framingClip is not { } clip) return;
        e.Handled = true;
        if (e.ClickCount == 2)
        {
            ResetFraming(clip);
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
        TimelineView.Select(clip);
        _framingDrag = kind;
        _framingDragClip = clip;
        _framingDragStart = e.GetPosition(PreviewCanvas);
        _framingAtStart = clip.Framing;
        _framingDragBefore = _history.Capture();
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
        ApplyFraming(clip, next, refreshList: false);
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
        if (_framingDrag == FramingDragKind.None) return;
        var changed = _framingDragClip is { } clip && clip.Framing != _framingAtStart;
        _framingDrag = FramingDragKind.None;
        _framingDragClip = null;
        if (_framingDragBefore is { } before) _history.Commit(before, "調整畫面位置");
        _framingDragBefore = null;
        if (changed) RefreshClipList();
    }

    private void OnFramingWheel(object sender, MouseWheelEventArgs e)
    {
        if (_framingClip is not { } clip || FramingSizes(clip) is not { } sizes) return;
        e.Handled = true;
        TimelineView.Select(clip);
        var before = _history.Capture();
        ApplyFraming(clip, clip.Framing.ZoomBy(Math.Pow(WheelZoomStep, e.Delta / 120.0), sizes.Source, sizes.Output));
        // 連續滾動併成一步。
        _history.Commit(before, "縮放畫面位置", $"framing-zoom:{clip.Id}");
    }
}
