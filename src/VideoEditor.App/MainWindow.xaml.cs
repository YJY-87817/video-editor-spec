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

public sealed class MediaItem(MediaInfo info)
{
    public MediaInfo Info { get; } = info;
    public BitmapSource? Thumbnail { get; set; }
    public string Summary => Info.HasVideo
        ? $"{Info.DisplayWidth}×{Info.DisplayHeight}  {MainWindow.FormatDuration(Info.Duration)}"
        : $"音訊  {MainWindow.FormatDuration(Info.Duration)}";
}

public sealed record ClipRow(TimelineClip Clip, int Number)
{
    public string Label => $"{Number}. {Clip.Media.FileName}  {MainWindow.FormatDuration(Clip.Duration)}{(Clip.Reversed ? "  ⟲ 倒放" : "")}{(Clip.Transform.IsIdentity ? "" : "  " + Clip.Transform.Label)}";
}

public partial class MainWindow : Window
{
    private const int ThumbnailWidth = 144;
    private const double ZoomStep = 1.5;

    private readonly TimelineModel _timeline = new();
    private readonly ObservableCollection<MediaItem> _media = [];
    private readonly FfmpegTools? _tools;
    private readonly MediaProbe? _probe;
    private readonly FrameExtractor? _extractor;
    private readonly PreviewEngine? _engine;
    private TimelineModel? _sourcePreview;
    private MediaItem? _selectedMedia;
    private double? _markIn;
    private double? _markOut;
    private MediaInfo? _surfaceMedia;
    private FrameTransform _surfaceTransform;
    private bool _updatingSlider;
    private bool _updatingVolume;
    private bool _updatingClipList;
    private bool _updatingReverse;
    private CancellationTokenSource? _exportCancel;

    public MainWindow()
    {
        InitializeComponent();
        VolumeSlider.Value = 100;
        MediaList.ItemsSource = _media;
        Timeline.Timeline = _timeline;
        Timeline.PlayheadDragged += OnTimelinePlayheadDragged;
        Timeline.SelectionChanged += OnTimelineSelectionChanged;
        Timeline.EditStarted += () => _engine?.Pause();
        Timeline.ClipsChanged += OnClipsChanged;
        Timeline.ViewChanged += UpdateTimelineScrollBar;

        try
        {
            _tools = FfmpegLocator.Locate();
        }
        catch (FfmpegNotFoundException e)
        {
            FatalText.Text = e.Message;
            FatalPanel.Visibility = Visibility.Visible;
            return;
        }

        _probe = new MediaProbe(_tools);
        _extractor = new FrameExtractor(_tools);
        _engine = new PreviewEngine(_extractor, new ReversePreviewCache(_tools), Dispatcher);
        _engine.PositionChanged += OnEnginePosition;
        _engine.PlayingChanged += () =>
        {
            PlayButton.Content = _engine.IsPlaying || _engine.IsRewinding ? "⏸ 暫停" : "▶ 播放";
            RewindButton.Content = _engine.IsRewinding ? $"◀◀ {_engine.RewindSpeed}×" : "◀◀ 倒帶";
        };
        _engine.Status += message => StatusText.Text = message;
        _engine.SurfaceChanged += OnSurfaceChanged;
        _engine.Error += message => StatusText.Text = message;
        UpdateOutputInfo();
        UpdateMarks();

        Loaded += async (_, _) =>
        {
            // 可由命令列直接開啟素材。
            await ImportAsync(Environment.GetCommandLineArgs().Skip(1).Where(File.Exists));
        };
    }

    private bool IsTimelinePreview => TimelineMode.IsChecked == true;

    private AspectRatio SelectedRatio =>
        Ratio9x16.IsChecked == true ? AspectRatio.R9x16
        : Ratio1x1.IsChecked == true ? AspectRatio.R1x1
        : Ratio4x5.IsChecked == true ? AspectRatio.R4x5
        : Ratio16x9.IsChecked == true ? AspectRatio.R16x9
        : AspectRatio.Original;

    private FitMode SelectedMode => ModePad.IsChecked == true ? FitMode.Pad : FitMode.Fill;

    public static string FormatDuration(double seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return $"{(int)span.TotalHours:00}:{span.Minutes:00}:{span.Seconds:00}.{span.Milliseconds / 10:00}";
    }

    // ---- 匯入 ----

    private void OnFileDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files) _ = ImportAsync(files);
    }

    private void OnImportClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Multiselect = true,
            Filter = "影音檔|*.mp4;*.mov;*.m4v;*.mkv;*.avi;*.webm;*.mts;*.m2ts;*.mp3;*.m4a;*.wav;*.aac;*.flac|所有檔案|*.*",
        };
        if (dialog.ShowDialog(this) == true) _ = ImportAsync(dialog.FileNames);
    }

    private async Task ImportAsync(IEnumerable<string> paths)
    {
        if (_probe == null) return;
        foreach (var path in paths)
        {
            if (_media.Any(m => string.Equals(m.Info.Path, path, StringComparison.OrdinalIgnoreCase))) continue;
            try
            {
                var info = await _probe.ProbeAsync(path);
                var item = new MediaItem(info) { Thumbnail = await LoadThumbnailAsync(info) };
                _media.Add(item);
                MediaList.SelectedItem ??= item;
            }
            catch (Exception e) when (e is FfmpegException or InvalidDataException or System.Text.Json.JsonException)
            {
                MessageBox.Show(this, $"無法匯入 {System.IO.Path.GetFileName(path)}：\n{(e as FfmpegException)?.Stderr ?? e.Message}", "匯入失敗",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }

    private async Task<BitmapSource?> LoadThumbnailAsync(MediaInfo info)
    {
        if (!info.HasVideo) return null;
        try
        {
            var bytes = await _extractor!.ThumbnailAsync(info.Path, Math.Min(1, info.Duration / 10), ThumbnailWidth);
            if (bytes.Length == 0) return null;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = new MemoryStream(bytes);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (FfmpegException)
        {
            // 沒有縮圖不影響其他功能。
            return null;
        }
    }

    private void OnMediaSelected(object sender, SelectionChangedEventArgs e)
    {
        _selectedMedia = MediaList.SelectedItem as MediaItem;
        MediaDetails.Text = _selectedMedia == null ? "" : DescribeMedia(_selectedMedia.Info);
        _markIn = null;
        _markOut = null;
        if (_selectedMedia == null) return;
        EmptyHint.Visibility = Visibility.Collapsed;
        UpdateOutputInfo();
        if (SourceMode.IsChecked == true) LoadPreviewSource();
        else SourceMode.IsChecked = true;
        UpdateMarks();
    }

    private static string DescribeMedia(MediaInfo m)
    {
        var lines = new List<string> { m.FileName, $"長度：{FormatDuration(m.Duration)}" };
        if (m.BitRate > 0) lines.Add($"位元率：{m.BitRate / 1000.0:0} kb/s");
        if (m.HasVideo)
        {
            lines.Add($"解析度：{m.DisplayWidth}×{m.DisplayHeight}");
            lines.Add($"畫面比例：{m.AspectLabel}");
            lines.Add($"影格率：{m.FrameRate.Value:0.###} fps{(m.IsVariableFrameRate ? "（可變）" : "")}");
            lines.Add($"影像編碼：{m.VideoCodec}，{m.BitDepth}-bit{(m.IsHdr ? "，HDR" : "")}");
        }
        lines.Add(m.HasAudio ? $"音訊編碼：{m.AudioCodec}，{m.AudioBitRate / 1000.0:0} kb/s" : "音訊：無");
        return string.Join('\n', lines);
    }

    // ---- 預覽 ----

    private void LoadPreviewSource()
    {
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
    private void OnStepBack(object sender, RoutedEventArgs e) => _engine?.StepFrame(-1);
    private void OnStepForward(object sender, RoutedEventArgs e) => _engine?.StepFrame(1);

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Space: _engine?.Toggle(); break;
            case Key.J: _engine?.Rewind(); break;
            case Key.K: _engine?.Pause(); break;
            case Key.L: _engine?.Play(); break;
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

    // ---- 入出點 ----

    private void OnMarkIn(object sender, RoutedEventArgs e) => MarkIn();
    private void OnMarkOut(object sender, RoutedEventArgs e) => MarkOut();

    /// <summary>對齊到畫面上顯示的那一格，匯出的切點才會與看到的相同。</summary>
    private double SnappedSourcePosition(MediaInfo media)
    {
        var position = _engine!.Position;
        return media.FrameRate.Value > 0
            ? Math.Min(media.Duration, FrameExtractor.FrameIndex(position, media.FrameRate.Value) / media.FrameRate.Value)
            : position;
    }

    private void MarkIn()
    {
        if (_engine == null || _selectedMedia == null || IsTimelinePreview) return;
        _markIn = SnappedSourcePosition(_selectedMedia.Info);
        if (_markOut <= _markIn) _markOut = null;
        UpdateMarks();
    }

    private void MarkOut()
    {
        if (_engine == null || _selectedMedia == null || IsTimelinePreview) return;
        _markOut = SnappedSourcePosition(_selectedMedia.Info);
        if (_markIn >= _markOut) _markIn = null;
        UpdateMarks();
    }

    private void UpdateMarks()
    {
        MarkText.Text = _selectedMedia == null || IsTimelinePreview
            ? ""
            : $"I {FormatDuration(_markIn ?? 0)}  O {FormatDuration(_markOut ?? _selectedMedia.Info.Duration)}";
        UpdateMarkRange();
    }

    private void UpdateMarkRange()
    {
        if (_selectedMedia == null || IsTimelinePreview || _selectedMedia.Info.Duration <= 0)
        {
            MarkRange.Visibility = Visibility.Collapsed;
            return;
        }
        var duration = _selectedMedia.Info.Duration;
        var inPoint = _markIn ?? 0;
        var outPoint = _markOut ?? duration;
        MarkRange.Visibility = Visibility.Visible;
        Canvas.SetLeft(MarkRange, inPoint / duration * ScrubSlider.ActualWidth);
        MarkRange.Width = Math.Max(1, (outPoint - inPoint) / duration * ScrubSlider.ActualWidth);
    }

    private void OnAddToTimeline(object sender, RoutedEventArgs e)
    {
        if (_selectedMedia == null) return;
        var media = _selectedMedia.Info;
        try
        {
            var clip = _timeline.Append(media, _markIn ?? 0, _markOut ?? media.Duration);
            Timeline.Select(clip);
        }
        catch (ArgumentException error)
        {
            StatusText.Text = error.Message;
            return;
        }
        _markIn = null;
        _markOut = null;
        UpdateMarks();
        OnClipsChanged();
    }

    // ---- 時間軸與片段 ----

    private void OnClipsChanged()
    {
        RefreshClipList();
        Timeline.InvalidateVisual();
        UpdateTimelineScrollBar();
        UpdateOutputInfo();
        UpdatePreviewLayout();
        if (IsTimelinePreview) _engine?.Refresh();
    }

    private void RefreshClipList()
    {
        _updatingClipList = true;
        var rows = _timeline.VideoClips.Select((clip, index) => new ClipRow(clip, index + 1)).ToList();
        ClipList.ItemsSource = rows;
        ClipList.SelectedItem = rows.FirstOrDefault(r => r.Clip == Timeline.SelectedClip);
        _updatingClipList = false;
    }

    private void OnClipListSelected(object sender, SelectionChangedEventArgs e)
    {
        if (!_updatingClipList && ClipList.SelectedItem is ClipRow row) Timeline.Select(row.Clip);
    }

    private void OnTimelineSelectionChanged(TimelineClip? clip)
    {
        _updatingVolume = true;
        VolumeSlider.IsEnabled = clip is { Media.HasAudio: true };
        VolumeSlider.Value = (clip?.Volume ?? 1) * 100;
        VolumeText.Text = clip == null ? "—" : $"{VolumeSlider.Value:0}%";
        _updatingVolume = false;

        _updatingClipList = true;
        ClipList.SelectedItem = (ClipList.ItemsSource as IEnumerable<ClipRow>)?.FirstOrDefault(r => r.Clip == clip);
        _updatingClipList = false;

        _updatingReverse = true;
        ReverseCheck.IsEnabled = clip is { Track: TrackKind.Video };
        ReverseCheck.IsChecked = clip?.Reversed == true;
        _updatingReverse = false;
        UpdateTransformControls(clip);
    }

    // ---- 旋轉與鏡像 ----

    private void UpdateTransformControls(TimelineClip? clip)
    {
        TransformPanel.IsEnabled = clip is { Track: TrackKind.Video };
        TransformText.Text = clip is { Track: TrackKind.Video } ? clip.Transform.Label : "—";
    }

    private void OnRotateLeft(object sender, RoutedEventArgs e) => ChangeTransform(t => t.Rotate(-90));
    private void OnRotateRight(object sender, RoutedEventArgs e) => ChangeTransform(t => t.Rotate(90));
    private void OnFlipHorizontal(object sender, RoutedEventArgs e) => ChangeTransform(t => t.ToggleHorizontal());
    private void OnFlipVertical(object sender, RoutedEventArgs e) => ChangeTransform(t => t.ToggleVertical());
    private void OnResetTransform(object sender, RoutedEventArgs e) => ChangeTransform(_ => FrameTransform.Identity);

    private void ChangeTransform(Func<FrameTransform, FrameTransform> change)
    {
        if (Timeline.SelectedClip is not { Track: TrackKind.Video } clip) return;
        _timeline.SetTransform(clip, change(clip.Transform));
        UpdateTransformControls(clip);
        // 看得到效果：切到時間軸預覽，並把播放頭移進這個片段。
        OnClipsChanged();
        if (!IsTimelinePreview)
        {
            Timeline.Playhead = clip.Start;
            SwitchToTimelinePreview();
        }
        else if (Timeline.Playhead < clip.Start || Timeline.Playhead >= clip.End)
        {
            _engine?.Seek(clip.Start);
        }
    }

    private void OnReverseChanged(object sender, RoutedEventArgs e)
    {
        if (_updatingReverse || Timeline.SelectedClip is not { Track: TrackKind.Video } clip) return;
        _engine?.Pause();
        _timeline.SetReversed(clip, ReverseCheck.IsChecked == true);
        // 先在背景產生倒放預覽檔，按播放時多半已經好了。
        _engine?.PrepareReverse(clip);
        OnClipsChanged();
    }

    private void OnVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingVolume || Timeline.SelectedClip is not { } clip) return;
        clip.Volume = Math.Round(e.NewValue) / 100;
        VolumeText.Text = $"{e.NewValue:0}%";
        Timeline.InvalidateVisual();
    }

    private void OnMoveUp(object sender, RoutedEventArgs e) => SwapSelected(-1);
    private void OnMoveDown(object sender, RoutedEventArgs e) => SwapSelected(1);

    private void SwapSelected(int direction)
    {
        if (Timeline.SelectedClip is { } clip && _timeline.Swap(clip, direction)) OnClipsChanged();
    }

    private void OnDeleteClip(object sender, RoutedEventArgs e) => DeleteSelectedClip();

    private void DeleteSelectedClip()
    {
        if (Timeline.SelectedClip is not { } clip) return;
        _timeline.Remove(clip);
        Timeline.Select(null);
        OnClipsChanged();
    }

    private void OnZoomIn(object sender, RoutedEventArgs e) => Timeline.SetZoom(Timeline.PixelsPerSecond * ZoomStep);
    private void OnZoomOut(object sender, RoutedEventArgs e) => Timeline.SetZoom(Timeline.PixelsPerSecond / ZoomStep);
    private void OnZoomFit(object sender, RoutedEventArgs e) => Timeline.ZoomToFit();

    private void OnTimelineScroll(object sender, ScrollEventArgs e) => Timeline.SetScrollOffset(e.NewValue);

    private void UpdateTimelineScrollBar()
    {
        TimelineScroll.Minimum = 0;
        TimelineScroll.Maximum = Math.Max(0, Timeline.ContentWidth - Timeline.ViewportWidth);
        TimelineScroll.ViewportSize = Timeline.ViewportWidth;
        TimelineScroll.SmallChange = 40;
        TimelineScroll.LargeChange = Timeline.ViewportWidth * 0.8;
        TimelineScroll.Value = Timeline.ScrollOffset;
    }

    // ---- 輸出 ----

    private void OnOutputSettingChanged(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized) return;
        UpdateOutputInfo();
        UpdatePreviewLayout();
    }

    private void UpdateOutputInfo()
    {
        var first = _timeline.VideoClips.FirstOrDefault();
        var reference = first?.Media ?? (_selectedMedia?.Info is { HasVideo: true } s ? s : null);
        if (reference == null)
        {
            OutputInfo.Text = "輸出解析度：—";
            return;
        }
        var referenceSize = first?.DisplaySize ?? new Size2(reference.DisplayWidth, reference.DisplayHeight);
        var size = OutputSize.Compute(referenceSize.Width, referenceSize.Height, SelectedRatio);
        var (codec, _) = ExportPlanner.ChooseVideoFormat(reference);
        OutputInfo.Text = $"輸出解析度：{size.Width}×{size.Height}\n編碼：{(codec == "libx265" ? "H.265" : "H.264")}，CRF 18";
    }

    private async void OnExportClick(object sender, RoutedEventArgs e)
    {
        if (_tools == null) return;
        if (_timeline.VideoClips.Count == 0)
        {
            StatusText.Text = "影片軌沒有片段，請先加入時間軸。";
            return;
        }

        var dialog = new SaveFileDialog { Filter = "MP4 影片|*.mp4", FileName = "輸出.mp4", DefaultExt = ".mp4" };
        if (dialog.ShowDialog(this) != true) return;
        await ExportToAsync(dialog.FileName);
    }

    private async Task ExportToAsync(string output)
    {
        var problem = ExportService.Preflight(_timeline, output);
        if (problem != null)
        {
            MessageBox.Show(this, problem, "無法匯出", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var plan = ExportPlanner.Build(_timeline, new OutputSettings(SelectedRatio, SelectedMode), output);
        if (plan.Warnings.Count > 0)
        {
            var answer = MessageBox.Show(this, string.Join('\n', plan.Warnings) + "\n\n要繼續匯出嗎？", "匯出提醒",
                MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (answer != MessageBoxResult.Yes) return;
        }

        _engine?.Pause();
        _exportCancel = new CancellationTokenSource();
        ExportButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        ExportProgress.Value = 0;
        StatusText.Text = $"匯出中：{plan.Size.Width}×{plan.Size.Height}…";
        var progress = new Progress<double>(value =>
        {
            ExportProgress.Value = value;
            StatusText.Text = $"匯出中：{value * 100:0}%";
        });

        try
        {
            await new ExportService(_tools!).ExportAsync(plan, output, progress, _exportCancel.Token);
            StatusText.Text = $"匯出完成：{output}";
        }
        catch (OperationCanceledException)
        {
            ExportProgress.Value = 0;
            StatusText.Text = "已取消匯出。";
        }
        catch (Exception error) when (error is FfmpegException or IOException or UnauthorizedAccessException)
        {
            ExportProgress.Value = 0;
            StatusText.Text = "匯出失敗。";
            MessageBox.Show(this, (error as FfmpegException)?.Stderr ?? error.Message, "匯出失敗", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            ExportButton.IsEnabled = true;
            CancelButton.IsEnabled = false;
            _exportCancel.Dispose();
            _exportCancel = null;
        }
    }

    private void OnCancelExport(object sender, RoutedEventArgs e) => _exportCancel?.Cancel();

    private void OnClosed(object? sender, EventArgs e)
    {
        _exportCancel?.Cancel();
        _engine?.Dispose();
    }
}
