using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Microsoft.Win32;
using VideoEditor.App.Preview;
using VideoEditor.Core.Export;
using VideoEditor.Core.Ffmpeg;
using VideoEditor.Core.Logging;
using VideoEditor.Core.Models;
using VideoEditor.Core.Editing;

namespace VideoEditor.App;

public partial class MainWindow : Window, IExportView, IImportView
{
    private const double ZoomStep = 1.5;

    private readonly Timeline _timeline = new();
    private readonly TimelineEditor _editor;
    private readonly ObservableCollection<MediaItem> _media = [];
    private readonly FfmpegTools? _tools;
    private readonly ImportController? _import;
    private readonly PreviewEngine? _engine;
    private readonly WaveformCache? _waveforms;
    private Timeline? _sourcePreview;
    private MediaItem? _selectedMedia;
    private readonly InOutPoints _points = new();
    private MediaInfo? _surfaceMedia;
    private FrameTransform _surfaceTransform;
    private readonly ReentryGuard _sliderGuard = new();
    private readonly ReentryGuard _volumeGuard = new();
    private readonly ReentryGuard _clipListGuard = new();
    private readonly ReentryGuard _reverseGuard = new();
    private readonly ExportController? _export;

    public MainWindow()
    {
        _editor = new TimelineEditor(_timeline);
        InitializeComponent();
        UpdateModeIndicator();
        VolumeSlider.Value = 100;
        MediaList.ItemsSource = _media;
        TimelineView.Timeline = _timeline;
        TimelineView.PlayheadDragged += OnTimelinePlayheadDragged;
        TimelineView.SelectionChanged += OnTimelineSelectionChanged;
        TimelineView.ClipPressed += SwitchToTimelinePreview;
        TimelineView.EditStarted += () => _engine?.Pause();
        TimelineView.ClipsChanged += OnClipsChanged;
        TimelineView.ViewChanged += UpdateTimelineScrollBar;
        ProgressPanel.CancelExportRequested += () => _export?.Cancel();
        ProgressPanel.CancelImportRequested += () => _import?.Cancel();

        try
        {
            _tools = FfmpegLocator.Locate();
            _ = ShowFfmpegAsync(_tools);
        }
        catch (FfmpegNotFoundException e)
        {
            FatalText.Text = e.Message;
            FatalPanel.Visibility = Visibility.Visible;
            return;
        }

        // 清掉上次當掉或被強制結束時留下的倒放預覽檔。可能有數 GB，放到背景，視窗才不會等它。
        _ = Task.Run(() =>
        {
            try { ReversePreviewCache.CleanupStale(); }
            catch (Exception e) { Log.Warn("清理上次遺留的預覽暫存失敗", e); }
        });
        var extractor = new FrameExtractor(_tools);
        _import = new ImportController(new MediaProbe(_tools), extractor, this);
        _export = new ExportController(_tools, this);
        _engine = new PreviewEngine(extractor, new ReversePreviewCache(_tools, progress: new Progress<double?>(ProgressPanel.ShowReverse)), Dispatcher);
        _engine.PositionChanged += OnEnginePosition;
        _engine.PlayingChanged += UpdatePlaybackButtons;
        _engine.Status += message => ShowStatus(message);
        _engine.SurfaceChanged += OnSurfaceChanged;
        _engine.Error += message =>
        {
            Log.Warn(message);
            ShowStatus(message, StatusKind.Error);
        };
        // 聲音波形：匯入後在背景產生，好了就重畫時間軸。這兩個事件在背景執行緒觸發。
        _waveforms = new WaveformCache(new WaveformExtractor(_tools));
        _waveforms.Ready += _ => Dispatcher.BeginInvoke(TimelineView.InvalidateVisual);
        _waveforms.Failed += (media, error) => Dispatcher.BeginInvoke(() =>
        {
            var stderr = (error as FfmpegException)?.Stderr;
            var detail = FirstLine(string.IsNullOrWhiteSpace(stderr) ? error.Message : stderr);
            ShowStatus($"無法產生 {media.FileName} 的聲音波形：{detail}", StatusKind.Warning);
            TimelineView.InvalidateVisual();
        });
        TimelineView.WaveformOf = _waveforms.Get;
        TimelineView.WaveformPending = _waveforms.IsPending;
        UpdateOutputInfo();
        UpdateMarks();

        Loaded += async (_, _) =>
        {
            // 可由命令列直接開啟素材。轉成絕對路徑，以 - 開頭的相對路徑才不會被當成選項。
            try
            {
                await _import!.ImportAsync(Environment.GetCommandLineArgs().Skip(1).Where(File.Exists).Select(System.IO.Path.GetFullPath));
            }
            catch (Exception ex)
            {
                Log.Error("開啟命令列素材時發生錯誤", ex);
                ShowStatus($"匯入時發生錯誤：{ex.Message}", StatusKind.Error);
            }
        };
    }

    /// <summary>
    /// 讀出實際使用的 FFmpeg 路徑與版本，寫進紀錄，並固定顯示在狀態列右側（不和其他訊息搶位置）。
    /// 使用者自備 FFmpeg 時，才看得出程式到底用的是哪一份。
    /// </summary>
    private async Task ShowFfmpegAsync(FfmpegTools tools)
    {
        var version = await FfmpegVersion.ReadAsync(tools);
        var text = version == null ? $"使用的 FFmpeg：{tools.Ffmpeg}" : $"使用的 FFmpeg：{tools.Ffmpeg}（{version}）";
        Log.Info(text);
        FfmpegText.Text = version == null ? "FFmpeg" : $"FFmpeg {version}";
        FfmpegText.ToolTip = text;
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

    /// <summary>
    /// 關閉視窗前確認，避免剪輯成果不見：
    /// 匯出中關閉會取消匯出，確定後等暫存檔清掉再關，不留下半成品；
    /// 沒在匯出但時間軸有片段時，提醒目前不能存檔、關閉後會遺失。兩者同時成立時只問一次（匯出中的訊息）。
    /// </summary>
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closingAfterExport) return;
        if (_export is not { IsRunning: true } export)
        {
            if (!_timeline.AllClips.Any()) return;
            var discard = MessageBox.Show(this, "時間軸上的剪輯會遺失（目前不支援存檔）。\n\n確定要關閉嗎？", "關閉影片編輯器",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (discard != MessageBoxResult.Yes) e.Cancel = true;
            return;
        }
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
