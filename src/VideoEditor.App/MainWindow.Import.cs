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
using VideoEditor.Core.Logging;
using VideoEditor.Core.Models;
using VideoEditor.Core.Playback;
using VideoEditor.Core.Timeline;
using TimelineModel = VideoEditor.Core.Timeline.Timeline;

namespace VideoEditor.App;

public partial class MainWindow
{
    // ---- 匯入 ----

    private void OnFileDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files) RunImport(files);
    }

    /// <summary>匯入不等結果；預料之外的錯誤要顯示出來，否則檔案沒出現也沒有任何說明。</summary>
    private async void RunImport(IEnumerable<string> paths)
    {
        if (_import == null) return;
        try
        {
            await _import.ImportAsync(paths);
        }
        catch (Exception e)
        {
            Log.Error("匯入時發生預料之外的錯誤", e);
            StatusText.Text = $"匯入時發生錯誤：{e.Message}";
        }
    }

    private void OnImportClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Multiselect = true,
            Filter = "影音檔|*.mp4;*.mov;*.m4v;*.mkv;*.avi;*.webm;*.mts;*.m2ts;*.mp3;*.m4a;*.wav;*.aac;*.flac|所有檔案|*.*",
        };
        if (dialog.ShowDialog(this) == true) RunImport(dialog.FileNames);
    }

    // ---- IImportView：匯入流程需要的畫面更新 ----

    bool IImportView.IsAlreadyImported(string path) =>
        _media.Any(m => string.Equals(m.Info.Path, path, StringComparison.OrdinalIgnoreCase));

    void IImportView.ShowProgress(string text, double value) => ProgressPanel.ShowImport(text, value);

    void IImportView.HideProgress() => ProgressPanel.HideImport();

    void IImportView.AddMedia(MediaInfo info, byte[]? thumbnail)
    {
        var item = new MediaItem(info) { Thumbnail = thumbnail == null ? null : TryCreateThumbnail(thumbnail) };
        _media.Add(item);
        // 不等加入時間軸就先算波形，加入時多半已經好了。
        _waveforms?.Request(info);
        MediaList.SelectedItem ??= item;
    }

    void IImportView.ShowFailure(string name, string message) =>
        MessageBox.Show(this, $"無法匯入 {name}：\n{message}", "匯入失敗", MessageBoxButton.OK, MessageBoxImage.Warning);

    /// <summary>縮圖只是裝飾：解碼失敗時不要讓整批匯入中斷，回傳 null 顯示空白。</summary>
    private static BitmapSource? TryCreateThumbnail(byte[] bytes)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = new MemoryStream(bytes);
            image.EndInit();
            image.Freeze();
            return image;
        }
        // WIC 解碼失敗可能是 COMException；尺寸異常的圖可能是 OverflowException。
        catch (Exception e) when (e is NotSupportedException or FileFormatException or IOException or InvalidOperationException
            or ArgumentException or System.Runtime.InteropServices.COMException or OverflowException)
        {
            return null;
        }
    }

    private void OnMediaSelected(object sender, SelectionChangedEventArgs e)
    {
        _selectedMedia = MediaList.SelectedItem as MediaItem;
        MediaDetails.Text = _selectedMedia == null ? "" : DescribeMedia(_selectedMedia.Info);
        _points.Clear();
        if (_selectedMedia == null) return;
        EmptyHint.Visibility = Visibility.Collapsed;
        UpdateOutputInfo();
        if (SourceMode.IsChecked == true) LoadPreviewSource();
        else SourceMode.IsChecked = true;
        UpdateMarks();
    }

    private static string DescribeMedia(MediaInfo m)
    {
        var lines = new List<string> { m.FileName, $"長度：{PlaybackText.Duration(m.Duration)}" };
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
}
