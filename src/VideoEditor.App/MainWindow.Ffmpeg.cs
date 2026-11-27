using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Navigation;
using Microsoft.Win32;
using VideoEditor.Core.Ffmpeg;
using VideoEditor.Core.Logging;
using VideoEditor.Core.Models;

namespace VideoEditor.App;

public partial class MainWindow
{
    // ---- 選擇 FFmpeg 的位置（找不到時，或點狀態列的 FFmpeg 版本） ----

    private bool _restartAfterClose;

    private void ShowFfmpegMissing(string message)
    {
        FatalText.Text = message;
        FatalPlaces.Text = "找過這些地方（依序）：\n" + string.Join("\n", FfmpegLocator.SearchedPlaces(preferredDirectory: _settings.FfmpegFolder).Select(p => "・" + p));
        FatalPanel.Visibility = Visibility.Visible;
    }

    private void OnChooseFfmpegClick(object sender, RoutedEventArgs e) => ChooseFfmpegFolder();

    private void OnFfmpegTextClick(object sender, MouseButtonEventArgs e) => ChooseFfmpegFolder();

    /// <summary>
    /// 選 ffmpeg.exe 與 ffprobe.exe 所在的資料夾，存到設定檔後重新啟動程式（FFmpeg 的位置在啟動時決定，
    /// 匯入、預覽、波形都已經拿著舊的位置）。關閉時會照常詢問是否放棄時間軸，取消的話就不重新啟動。
    /// </summary>
    private void ChooseFfmpegFolder()
    {
        var dialog = new OpenFolderDialog { Title = "選擇 ffmpeg.exe 與 ffprobe.exe 所在的資料夾" };
        if (ExistingFolder(_settings.FfmpegFolder) is { } current) dialog.InitialDirectory = current;
        if (dialog.ShowDialog(this) != true) return;
        var folder = dialog.FolderName;
        if (!FfmpegLocator.ContainsTools(folder))
        {
            const string problem = "這個資料夾裡沒有同時找到 ffmpeg.exe 與 ffprobe.exe。解壓縮後通常在 bin 資料夾裡。";
            if (FatalPanel.Visibility == Visibility.Visible)
            {
                FatalChooseResult.Text = problem;
                FatalChooseResult.Visibility = Visibility.Visible;
            }
            else
            {
                ShowStatus(problem, StatusKind.Warning);
            }
            return;
        }

        _settings = _settings with { FfmpegFolder = folder };
        Log.Info($"改用自己選的 FFmpeg 資料夾：{folder}，重新啟動");
        _restartAfterClose = true;
        Close();
        // 使用者在關閉確認中按了取消：視窗還在，這次不重新啟動，但選的資料夾已經記住，下次開啟時生效。
        if (IsVisible)
        {
            _restartAfterClose = false;
            _settingsStore.Save(_settings);
            ShowStatus("已記住新的 FFmpeg 資料夾，下次開啟程式時生效。", StatusKind.Info);
        }
    }

    private static void RestartApplication()
    {
        try
        {
            if (Environment.ProcessPath is { } exe) Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false });
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Error("重新啟動失敗", e);
        }
    }

    private void OnFfmpegDownloadLink(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Warn("開啟下載說明失敗", error);
        }
        e.Handled = true;
    }
}
