using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Microsoft.Win32;
using VideoEditor.Core.Editing;
using VideoEditor.Core.Export;
using VideoEditor.Core.Logging;
using VideoEditor.Core.Models;

namespace VideoEditor.App;

public partial class MainWindow
{
    // ---- 輸出 ----

    private void OnOutputSettingChanged(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized) return;
        UpdateOutputInfo();
        UpdatePreviewLayout();
        UpdateFramingControls(TimelineView.SelectedClip);
    }

    private void UpdateOutputInfo()
    {
        // 與匯出相同的基準（見 ExportPlanner.Reference）；時間軸還是空的時，以選取的素材預估。
        var reference = ExportPlanner.Reference(_timeline)
            ?? (_selectedMedia?.Info is { HasVideo: true } s ? SelectedMediaReference(s) : null);
        if (reference == null)
        {
            OutputInfo.Text = "輸出解析度：—";
            return;
        }
        var size = OutputSize.Compute(reference.Size.Width, reference.Size.Height, SelectedRatio);
        var (codec, _) = ExportPlanner.ChooseVideoFormat(reference.Media);
        OutputInfo.Text = $"輸出解析度：{size.Width}×{size.Height}\n編碼：{(codec == "libx265" ? "H.265" : "H.264")}，CRF 18";
    }

    /// <summary>只有選取的素材、時間軸還是空的時，把它當成時間軸上唯一的片段來預估輸出（規則與 ExportPlanner.Reference 相同）。</summary>
    private static ExportReference? SelectedMediaReference(MediaInfo media)
    {
        var timeline = new Timeline();
        timeline.Append(media, 0, media.IsStill ? Timeline.StillClipDuration : media.Duration);
        return ExportPlanner.Reference(timeline);
    }

    private async void OnExportClick(object sender, RoutedEventArgs e)
    {
        if (_export == null) return;
        if (_timeline.VideoClips.Count == 0)
        {
            ShowStatus("影片軌沒有片段，請先加入時間軸。", StatusKind.Warning);
            return;
        }

        var dialog = new SaveFileDialog
        {
            Filter = "MP4 影片|*.mp4", FileName = "輸出.mp4", DefaultExt = ".mp4",
            InitialDirectory = ExistingFolder(_settings.LastExportFolder) ?? "",
        };
        if (dialog.ShowDialog(this) != true) return;
        RememberExportFolder(dialog.FileName);
        try
        {
            await _export.RunAsync(_timeline, new OutputSettings(SelectedRatio, SelectedMode), dialog.FileName);
        }
        catch (Exception ex)
        {
            // async void 的例外不會回到呼叫端；預料之外的錯誤要顯示出來，而不是丟給全域處理器。
            Log.Error("匯出時發生預料之外的錯誤", ex);
            ShowStatus($"匯出時發生錯誤：{ex.Message}", StatusKind.Error);
        }
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
        ShowStatus(title);
    }

    void IExportView.ShowProgress(double value, string text)
    {
        ProgressPanel.ReportExport(value, text);
        Taskbar.ProgressValue = value;
    }

    void IExportView.ShowSucceeded(string output, string elapsed)
    {
        ShowStatus($"匯出完成：{output}", StatusKind.Success);
        ProgressPanel.FinishExport($"✔ 匯出完成：{output}（花費 {elapsed}）");
        Taskbar.ProgressState = System.Windows.Shell.TaskbarItemProgressState.None;
    }

    void IExportView.ShowStatus(string message) => ShowStatus(message, StatusKind.Warning);

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
