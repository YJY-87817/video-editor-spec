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
        UpdateFramingControls(Timeline.SelectedClip);
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
        if (_export == null) return;
        if (_timeline.VideoClips.Count == 0)
        {
            StatusText.Text = "影片軌沒有片段，請先加入時間軸。";
            return;
        }

        var dialog = new SaveFileDialog { Filter = "MP4 影片|*.mp4", FileName = "輸出.mp4", DefaultExt = ".mp4" };
        if (dialog.ShowDialog(this) != true) return;
        await _export.RunAsync(_timeline, new OutputSettings(SelectedRatio, SelectedMode), dialog.FileName);
    }

    // ---- IExportView：匯出流程需要的畫面更新 ----

    void IExportView.ShowProblem(string message) =>
        MessageBox.Show(this, message, "無法匯出", MessageBoxButton.OK, MessageBoxImage.Warning);

    bool IExportView.ConfirmWarnings(IReadOnlyList<string> warnings) =>
        MessageBox.Show(this, string.Join('\n', warnings) + "\n\n要繼續匯出嗎？", "匯出提醒",
            MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes;

    void IExportView.PausePreview() => _engine?.Pause();

    void IExportView.ShowStarted(string title)
    {
        ExportButton.IsEnabled = false;
        ProgressPanel.StartExport(title);
        Taskbar.ProgressState = System.Windows.Shell.TaskbarItemProgressState.Normal;
        Taskbar.ProgressValue = 0;
        StatusText.Text = title;
    }

    void IExportView.ShowProgress(double value, string text)
    {
        ProgressPanel.ReportExport(value, text);
        Taskbar.ProgressValue = value;
    }

    void IExportView.ShowSucceeded(string output, string elapsed)
    {
        StatusText.Text = $"匯出完成：{output}";
        ProgressPanel.FinishExport($"✔ 匯出完成：{output}（花費 {elapsed}）");
        Taskbar.ProgressState = System.Windows.Shell.TaskbarItemProgressState.None;
    }

    void IExportView.ShowStatus(string message) => StatusText.Text = message;

    void IExportView.ShowError(string message) =>
        MessageBox.Show(this, message, "匯出失敗", MessageBoxButton.OK, MessageBoxImage.Error);

    void IExportView.ShowEnded()
    {
        ExportButton.IsEnabled = true;
        ProgressPanel.EndExport();
        Taskbar.ProgressState = System.Windows.Shell.TaskbarItemProgressState.None;
    }

    void IExportView.HideProgress()
    {
        ProgressPanel.HideExport();
    }
}
