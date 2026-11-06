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

    private void OnSurfaceChanged(WriteableBitmap? bitmap, MediaInfo? media, FrameTransform transform)
    {
        PreviewImage.Source = bitmap;
        _surfaceMedia = media;
        _surfaceTransform = transform;
        UpdatePreviewLayout();
    }

    private void OnPreviewAreaResized(object sender, SizeChangedEventArgs e) => UpdatePreviewLayout();

    /// <summary>依目前的素材、比例與模式，排好來源畫面與輸出框線。位置由 <see cref="PreviewPlanner"/> 計算，這裡只負責套用到畫面元件。</summary>
    private void UpdatePreviewLayout()
    {
        PreviewSurface? surface = _surfaceMedia == null
            ? null
            : new PreviewSurface(_surfaceMedia.HasVideo ? new Size2(_surfaceMedia.DisplayWidth, _surfaceMedia.DisplayHeight) : null, _surfaceTransform);
        var framingClip = FramingClip();
        var plan = PreviewPlanner.Compute(PreviewArea.ActualWidth, PreviewArea.ActualHeight, OutputReferenceSize(),
            SelectedRatio, SelectedMode, surface, framingClip?.Framing);
        UpdateFramingEditor(plan, framingClip);

        if (!plan.Visible)
        {
            PreviewImage.Visibility = Visibility.Collapsed;
            FrameFill.Visibility = Visibility.Collapsed;
            FrameBorder.Visibility = Visibility.Collapsed;
            CropDim.Data = null;
            return;
        }

        PreviewImage.Visibility = plan.ShowImage ? Visibility.Visible : Visibility.Collapsed;
        FrameFill.Visibility = Visibility.Visible;
        FrameBorder.Visibility = Visibility.Visible;
        PlaceImage(plan.Image, _surfaceTransform);
        Place(FrameFill, plan.Frame);
        Place(FrameBorder, plan.Frame);

        CropDim.Data = plan.DimCrop
            ? new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(ToRect(plan.Source)), new RectangleGeometry(ToRect(plan.Frame)))
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
    /// 以 rect（已依旋轉互換長寬）放置畫面，再以中心為原點旋轉與鏡像。
    /// 先旋轉、再鏡像（TransformGroup 依序套用），與匯出的濾鏡順序相同。
    /// </summary>
    private void PlaceImage(Rect2 rect, FrameTransform transform)
    {
        Canvas.SetLeft(PreviewImage, rect.X);
        Canvas.SetTop(PreviewImage, rect.Y);
        PreviewImage.Width = rect.Width;
        PreviewImage.Height = rect.Height;
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
