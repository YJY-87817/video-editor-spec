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
        var pending = paths.Where(path => !_media.Any(m => string.Equals(m.Info.Path, path, StringComparison.OrdinalIgnoreCase))).ToList();
        if (pending.Count == 0) return;
        // 匯入中又拖進新檔案時，併入同一個進度。
        _importTotal += pending.Count;
        foreach (var path in pending)
        {
            var name = System.IO.Path.GetFileName(path);
            try
            {
                ShowImportProgress(name, "讀取資訊", 0);
                var info = await _probe.ProbeAsync(path);
                ShowImportProgress(name, "產生縮圖", 0.5);
                var item = new MediaItem(info) { Thumbnail = await LoadThumbnailAsync(info) };
                _media.Add(item);
                MediaList.SelectedItem ??= item;
            }
            catch (Exception e) when (e is FfmpegException or InvalidDataException or System.Text.Json.JsonException)
            {
                MessageBox.Show(this, $"無法匯入 {name}：\n{(e as FfmpegException)?.Stderr ?? e.Message}", "匯入失敗",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                _importDone++;
                if (_importDone >= _importTotal)
                {
                    _importDone = 0;
                    _importTotal = 0;
                }
                ShowImportProgress(name, "完成", 0);
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
}
