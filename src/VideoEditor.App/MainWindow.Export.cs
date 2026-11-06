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
        var title = $"匯出中：{System.IO.Path.GetFileName(output)}（{plan.Size.Width}×{plan.Size.Height}）";
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var run = ++_exportRun;
        var finished = false;
        var succeeded = false;
        CancelButton.Visibility = Visibility.Visible;
        ExportProgress.Value = 0;
        ExportPercent.Text = "0%";
        ExportText.Text = title;
        ExportRow.Visibility = Visibility.Visible;
        UpdateProgressPanel();
        Taskbar.ProgressState = System.Windows.Shell.TaskbarItemProgressState.Normal;
        Taskbar.ProgressValue = 0;
        StatusText.Text = title;
        var progress = new Progress<double>(value =>
        {
            // Progress 是非同步送回 UI 執行緒的，匯出結束後才到的回報要忽略。
            if (finished) return;
            ExportProgress.Value = value;
            ExportPercent.Text = $"{value * 100:0}%";
            Taskbar.ProgressValue = value;
            // 進度太少時估算不準，先不顯示剩餘時間。
            ExportText.Text = value is > 0.02 and < 1
                ? $"{title}　剩餘約 {FormatSpan(clock.Elapsed * ((1 - value) / value))}"
                : title;
        });

        try
        {
            await new ExportService(_tools!).ExportAsync(plan, output, progress, _exportCancel.Token);
            finished = true;
            succeeded = true;
            StatusText.Text = $"匯出完成：{output}";
            ExportProgress.Value = 1;
            ExportPercent.Text = "100%";
            ExportText.Text = $"✔ 匯出完成：{output}（花費 {FormatSpan(clock.Elapsed)}）";
            CancelButton.Visibility = Visibility.Collapsed;
            Taskbar.ProgressState = System.Windows.Shell.TaskbarItemProgressState.None;
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "已取消匯出。";
        }
        catch (Exception error) when (error is FfmpegException or IOException or UnauthorizedAccessException)
        {
            StatusText.Text = "匯出失敗。";
            MessageBox.Show(this, (error as FfmpegException)?.Stderr ?? error.Message, "匯出失敗", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            finished = true;
            ExportButton.IsEnabled = true;
            CancelButton.IsEnabled = false;
            Taskbar.ProgressState = System.Windows.Shell.TaskbarItemProgressState.None;
            _exportCancel.Dispose();
            _exportCancel = null;
        }

        // 完成訊息留幾秒再收起；期間若又開始新的匯出，就交給新的那次處理。
        if (succeeded) await Task.Delay(ExportDoneDisplayMs);
        if (run != _exportRun) return;
        ExportRow.Visibility = Visibility.Collapsed;
        UpdateProgressPanel();
    }

    private void OnCancelExport(object sender, RoutedEventArgs e) => _exportCancel?.Cancel();
}
