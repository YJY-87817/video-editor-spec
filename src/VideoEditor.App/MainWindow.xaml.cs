using System.Collections.ObjectModel;
using System.ComponentModel;
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
using VideoEditor.Core.Playback;
using VideoEditor.Core.Timeline;
using TimelineModel = VideoEditor.Core.Timeline.Timeline;

namespace VideoEditor.App;

public partial class MainWindow : Window, IExportView, IImportView
{
    private const double ZoomStep = 1.5;

    private readonly TimelineModel _timeline = new();
    private readonly TimelineEditor _editor;
    private readonly ObservableCollection<MediaItem> _media = [];
    private readonly FfmpegTools? _tools;
    private readonly ImportController? _import;
    private readonly PreviewEngine? _engine;
    private readonly WaveformCache? _waveforms;
    private TimelineModel? _sourcePreview;
    private MediaItem? _selectedMedia;
    private readonly InOutPoints _points = new();
    private MediaInfo? _surfaceMedia;
    private FrameTransform _surfaceTransform;
    private bool _updatingSlider;
    private bool _updatingVolume;
    private bool _updatingClipList;
    private bool _updatingReverse;
    private readonly ExportController? _export;

    public MainWindow()
    {
        _editor = new TimelineEditor(_timeline);
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

        // 清掉上次當掉或被強制結束時留下的倒放預覽檔。
        ReversePreviewCache.CleanupStale();
        var extractor = new FrameExtractor(_tools);
        _import = new ImportController(new MediaProbe(_tools), extractor, this);
        _export = new ExportController(_tools, this);
        _engine = new PreviewEngine(extractor, new ReversePreviewCache(_tools, progress: new Progress<double?>(ProgressPanel.ShowReverse)), Dispatcher);
        _engine.PositionChanged += OnEnginePosition;
        _engine.PlayingChanged += UpdatePlaybackButtons;
        _engine.Status += message => StatusText.Text = message;
        _engine.SurfaceChanged += OnSurfaceChanged;
        _engine.Error += message => StatusText.Text = message;
        // 聲音波形：匯入後在背景產生，好了就重畫時間軸。這兩個事件在背景執行緒觸發。
        _waveforms = new WaveformCache(new WaveformExtractor(_tools));
        _waveforms.Ready += _ => Dispatcher.BeginInvoke(Timeline.InvalidateVisual);
        _waveforms.Failed += (media, error) => Dispatcher.BeginInvoke(() =>
        {
            var stderr = (error as FfmpegException)?.Stderr;
            var detail = (string.IsNullOrWhiteSpace(stderr) ? error.Message : stderr).Split('\n')[0];
            StatusText.Text = $"無法產生 {media.FileName} 的聲音波形：{detail}";
            Timeline.InvalidateVisual();
        });
        Timeline.WaveformOf = _waveforms.Get;
        Timeline.WaveformPending = _waveforms.IsPending;
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

    private bool _closingAfterExport;

    /// <summary>匯出中關閉視窗時先確認；確定關閉就取消匯出，等暫存檔清掉後再關，不留下半成品。</summary>
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closingAfterExport || _export is not { IsRunning: true } export) return;
        e.Cancel = true;
        var answer = MessageBox.Show(this, "匯出還沒完成，關閉視窗會取消匯出。\n\n確定要關閉嗎？", "匯出中",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;
        _closingAfterExport = true;
        await export.CancelAsync();
        Close();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _export?.Cancel();
        _engine?.Dispose();
        _waveforms?.Dispose();
    }
}
