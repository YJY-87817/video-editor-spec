using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Microsoft.Win32;
using VideoEditor.App.Preview;
using VideoEditor.Core.Export;
using VideoEditor.Core.Ffmpeg;
using VideoEditor.Core.Models;
using VideoEditor.Core.Timeline;
using TimelineModel = VideoEditor.Core.Timeline.Timeline;

namespace VideoEditor.App;

public partial class MainWindow
{
    // ---- 預覽 ----

    private static readonly Brush SourceModeBrush = FrozenBrush(0x4A, 0x90, 0xD9);
    private static readonly Brush SourceModeBadge = FrozenBrush(0xCC, 0x2E, 0x6F, 0xB5);
    private static readonly Brush TimelineModeBrush = FrozenBrush(0xF0, 0x88, 0x3E);
    private static readonly Brush TimelineModeBadge = FrozenBrush(0xCC, 0xC8, 0x65, 0x1B);

    private static Brush FrozenBrush(byte r, byte g, byte b) => FrozenBrush(0xFF, r, g, b);

    private static Brush FrozenBrush(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    /// <summary>預覽區的色框與標籤，讓人一眼分辨目前看的是匯入的原始影片，還是時間軸上剪好的結果。</summary>
    private void UpdateModeIndicator()
    {
        var timeline = IsTimelinePreview;
        ModeFrame.BorderBrush = timeline ? TimelineModeBrush : SourceModeBrush;
        ModeBadge.Background = timeline ? TimelineModeBadge : SourceModeBadge;
        ModeBadgeText.Text = timeline ? "剪輯預覽　時間軸上的成品"
            : _selectedMedia == null ? "匯入影片預覽"
            : $"匯入影片預覽　{_selectedMedia.Info.FileName}";
    }

    private void LoadPreviewSource()
    {
        UpdateModeIndicator();
        if (_engine == null) return;
        if (IsTimelinePreview)
        {
            _sourcePreview = null;
            _engine.Load(_timeline, Timeline.Playhead);
            return;
        }

        _sourcePreview = null;
        if (_selectedMedia == null)
        {
            _engine.Load(new TimelineModel());
            return;
        }
        _sourcePreview = new TimelineModel();
        _sourcePreview.Append(_selectedMedia.Info, 0, _selectedMedia.Info.Duration);
        _engine.Load(_sourcePreview);
    }

    private void OnPreviewModeChecked(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized) return;
        LoadPreviewSource();
        UpdateMarks();
        UpdatePreviewLayout();
    }

    private void SwitchToTimelinePreview() => TimelineMode.IsChecked = true;

    private void OnEnginePosition()
    {
        var position = _engine!.Position;
        _updatingSlider = true;
        ScrubSlider.Maximum = Math.Max(0.001, _engine.Duration);
        ScrubSlider.Value = position;
        _updatingSlider = false;
        TimeText.Text = $"{FormatDuration(position)} / {FormatDuration(_engine.Duration)}";
        if (IsTimelinePreview) Timeline.Playhead = position;
        UpdateMarkRange();
    }

    private void OnSurfaceChanged(WriteableBitmap? bitmap, MediaInfo? media, FrameTransform transform)
    {
        PreviewImage.Source = bitmap;
        _surfaceMedia = media;
        _surfaceTransform = transform;
        UpdatePreviewLayout();
    }

    private void OnScrubChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_updatingSlider) _engine?.Seek(e.NewValue);
    }

    private void OnTimelinePlayheadDragged(double time)
    {
        SwitchToTimelinePreview();
        _engine?.Seek(time);
    }

    private void OnPlayClick(object sender, RoutedEventArgs e) => _engine?.Toggle();
    private void OnRewindClick(object sender, RoutedEventArgs e) => _engine?.Rewind();
    private void OnFastForwardClick(object sender, RoutedEventArgs e) => _engine?.FastForward();
    private void OnStepBack(object sender, RoutedEventArgs e) => _engine?.StepFrame(-1);
    private void OnStepForward(object sender, RoutedEventArgs e) => _engine?.StepFrame(1);

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Space: _engine?.Toggle(); break;
            case Key.J: _engine?.Rewind(); break;
            case Key.K: _engine?.Pause(); break;
            case Key.L: _engine?.PlayOrFaster(); break;
            case Key.Left: _engine?.StepFrame(-1); break;
            case Key.Right: _engine?.StepFrame(1); break;
            case Key.I: MarkIn(); break;
            case Key.O: MarkOut(); break;
            case Key.Delete: DeleteSelectedClip(); break;
            default: return;
        }
        e.Handled = true;
    }

    private void OnPreviewAreaResized(object sender, SizeChangedEventArgs e) => UpdatePreviewLayout();

    /// <summary>依目前的素材、比例與模式，排好來源畫面與輸出框線。</summary>
    private void UpdatePreviewLayout()
    {
        var area = new Size(PreviewArea.ActualWidth, PreviewArea.ActualHeight);
        var reference = OutputReferenceSize();
        if (reference == null || area.Width <= 0 || area.Height <= 0)
        {
            PreviewImage.Visibility = Visibility.Collapsed;
            FrameFill.Visibility = Visibility.Collapsed;
            FrameBorder.Visibility = Visibility.Collapsed;
            CropDim.Data = null;
            return;
        }

        var output = OutputSize.Compute(reference.Value.Width, reference.Value.Height, SelectedRatio);
        // 時間軸空白處沒有來源畫面，用輸出尺寸排版，只顯示黑色的輸出框。
        var source = _surfaceMedia is { HasVideo: true } s ? _surfaceTransform.Apply(new Size2(s.DisplayWidth, s.DisplayHeight)) : output;
        var layout = PreviewLayout.Compute(area.Width, area.Height, source, output, SelectedMode);

        PreviewImage.Visibility = _surfaceMedia == null ? Visibility.Collapsed : Visibility.Visible;
        FrameFill.Visibility = Visibility.Visible;
        FrameBorder.Visibility = Visibility.Visible;
        PlaceImage(layout.Source, _surfaceTransform);
        Place(FrameFill, layout.Frame);
        Place(FrameBorder, layout.Frame);

        // 填滿裁切：把框外（會被裁掉的範圍）調暗。
        CropDim.Data = SelectedMode == FitMode.Fill && _surfaceMedia != null
            ? new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(ToRect(layout.Source)), new RectangleGeometry(ToRect(layout.Frame)))
            : null;
    }

    private static Rect ToRect(Rect2 r) => new(r.X, r.Y, r.Width, r.Height);

    private static void Place(FrameworkElement element, Rect2 rect)
    {
        Canvas.SetLeft(element, rect.X);
        Canvas.SetTop(element, rect.Y);
        element.Width = rect.Width;
        element.Height = rect.Height;
    }

    /// <summary>
    /// 預覽畫面以原始方向放置，再以中心為原點旋轉與鏡像，轉完後剛好落在 rect 內。
    /// 先旋轉、再鏡像（TransformGroup 依序套用），與匯出的濾鏡順序相同。
    /// </summary>
    private void PlaceImage(Rect2 rect, FrameTransform transform)
    {
        var (width, height) = transform.SwapsSize ? (rect.Height, rect.Width) : (rect.Width, rect.Height);
        Canvas.SetLeft(PreviewImage, rect.X + (rect.Width - width) / 2);
        Canvas.SetTop(PreviewImage, rect.Y + (rect.Height - height) / 2);
        PreviewImage.Width = width;
        PreviewImage.Height = height;
        PreviewImage.RenderTransformOrigin = new Point(0.5, 0.5);
        PreviewImage.RenderTransform = transform.IsIdentity
            ? Transform.Identity
            : new TransformGroup
            {
                Children =
                {
                    new RotateTransform(transform.Rotation),
                    new ScaleTransform(transform.FlipHorizontal ? -1 : 1, transform.FlipVertical ? -1 : 1),
                },
            };
    }

    /// <summary>決定輸出尺寸的畫面：時間軸模式是第一個影片片段（含旋轉），素材模式是目前的素材。</summary>
    private Size2? OutputReferenceSize()
    {
        if (IsTimelinePreview) return _timeline.VideoClips.FirstOrDefault()?.DisplaySize;
        return _selectedMedia?.Info is { HasVideo: true } info ? new Size2(info.DisplayWidth, info.DisplayHeight) : null;
    }
}
