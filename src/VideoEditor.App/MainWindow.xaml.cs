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

public partial class MainWindow : Window, IExportView, IImportView
{
    private const double ZoomStep = 1.5;

    private readonly TimelineModel _timeline = new();
    private readonly ObservableCollection<MediaItem> _media = [];
    private readonly FfmpegTools? _tools;
    private readonly ImportController? _import;
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
    private readonly ExportController? _export;

    public MainWindow()
    {
        InitializeComponent();
        UpdateModeIndicator();
        VolumeSlider.Value = 100;
        MediaList.ItemsSource = _media;
        Timeline.Timeline = _timeline;
        Timeline.PlayheadDragged += OnTimelinePlayheadDragged;
        Timeline.SelectionChanged += OnTimelineSelectionChanged;
        Timeline.EditStarted += () => _engine?.Pause();
        Timeline.ClipsChanged += OnClipsChanged;
        Timeline.ViewChanged += UpdateTimelineScrollBar;
        ProgressPanel.CancelExportRequested += () => _export?.Cancel();

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

        var extractor = new FrameExtractor(_tools);
        _import = new ImportController(new MediaProbe(_tools), extractor, this);
        _export = new ExportController(_tools, this);
        _engine = new PreviewEngine(extractor, new ReversePreviewCache(_tools, progress: new Progress<double?>(ProgressPanel.ShowReverse)), Dispatcher);
        _engine.PositionChanged += OnEnginePosition;
        _engine.PlayingChanged += () =>
        {
            PlayButton.Content = _engine.IsPlaying || _engine.IsRewinding ? "⏸ 暫停" : "▶ 播放";
            RewindButton.Content = _engine.IsRewinding ? $"◀◀ {_engine.RewindSpeed}×" : "◀◀ 倒帶";
            FastForwardButton.Content = _engine.IsFastForwarding ? $"▶▶ {_engine.PlaybackRate}×" : "▶▶ 快轉";
        };
        _engine.Status += message => StatusText.Text = message;
        _engine.SurfaceChanged += OnSurfaceChanged;
        _engine.Error += message => StatusText.Text = message;
        UpdateOutputInfo();
        UpdateMarks();

        Loaded += async (_, _) =>
        {
            // 可由命令列直接開啟素材。
            await _import!.ImportAsync(Environment.GetCommandLineArgs().Skip(1).Where(File.Exists));
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

    private void OnClosed(object? sender, EventArgs e)
    {
        _export?.Cancel();
        _engine?.Dispose();
    }
}
