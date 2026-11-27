using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using VideoEditor.Core.Editing;
using VideoEditor.Core.Export;
using VideoEditor.Core.Ffmpeg;
using VideoEditor.Core.Logging;
using VideoEditor.Core.Models;
using VideoEditor.Core.Playback;

namespace VideoEditor.App;

public partial class MainWindow
{
    // ---- 匯入 ----

    /// <summary>視窗只接受檔案（匯入）；素材清單的素材只能放到時間軸（時間軸自己處理，不會傳到這裡）。</summary>
    private void OnWindowDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

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
            ShowStatus($"匯入時發生錯誤：{e.Message}", StatusKind.Error);
        }
    }

    private void OnImportClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Multiselect = true,
            Filter = "影音與圖片|*.mp4;*.mov;*.m4v;*.mkv;*.avi;*.webm;*.mts;*.m2ts;*.mp3;*.m4a;*.wav;*.aac;*.flac;*.jpg;*.jpeg;*.png;*.bmp;*.webp;*.tif;*.tiff"
                + "|影音檔|*.mp4;*.mov;*.m4v;*.mkv;*.avi;*.webm;*.mts;*.m2ts;*.mp3;*.m4a;*.wav;*.aac;*.flac"
                + "|圖片|*.jpg;*.jpeg;*.png;*.bmp;*.webp;*.tif;*.tiff|所有檔案|*.*",
            InitialDirectory = ExistingFolder(_settings.LastImportFolder) ?? "",
        };
        if (dialog.ShowDialog(this) != true) return;
        RememberImportFolder(dialog.FileName);
        RunImport(dialog.FileNames);
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

    /// <summary>
    /// 一個檔案失敗時照舊顯示完整的錯誤；好幾個時合成一個對話框，每個檔案只列第一行錯誤（完整內容在紀錄檔），
    /// 列太多時截斷，對話框才不會超出螢幕。
    /// </summary>
    void IImportView.ShowFailures(IReadOnlyList<ImportFailure> failures)
    {
        ShowStatus(failures.Count == 1 ? $"無法匯入 {failures[0].Name}" : $"{failures.Count} 個檔案無法匯入", StatusKind.Warning);
        string text;
        if (failures.Count == 1)
        {
            text = $"無法匯入 {failures[0].Name}：\n{failures[0].Message}";
        }
        else
        {
            const int shown = 10;
            var lines = failures.Take(shown).Select(f => $"• {f.Name}：{FirstLine(f.Message)}");
            var more = failures.Count > shown ? $"\n…還有 {failures.Count - shown} 個" : "";
            text = $"以下 {failures.Count} 個檔案無法匯入：\n\n{string.Join('\n', lines)}{more}\n\n完整的錯誤訊息記錄在紀錄檔。";
        }
        MessageBox.Show(this, text, "匯入失敗", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    /// <summary>縮圖只是裝飾：解碼失敗時不要讓整批匯入中斷，回傳 null 顯示空白。</summary>
    internal static BitmapSource? TryCreateThumbnail(byte[] bytes)
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

    // ---- 移除素材 ----

    private void OnRemoveMediaClick(object sender, RoutedEventArgs e) => RemoveSelectedMedia();

    /// <summary>
    /// 從素材清單移除選取的素材（不刪除硬碟上的檔案）。時間軸上有用到它的片段時先確認，確定後一併移除這些片段；
    /// 移除片段算一步，可以用 Ctrl+Z 把片段找回來（素材清單不在復原範圍內，要再匯入一次）。
    /// 移除後選取下一個素材（沒有下一個就選上一個）。
    /// </summary>
    private void RemoveSelectedMedia()
    {
        if (MediaList.SelectedItem is not MediaItem item) return;
        var clips = _timeline.AllClips.Where(c => c.Media == item.Info).ToList();
        if (clips.Count > 0)
        {
            var answer = MessageBox.Show(this,
                $"時間軸上有 {clips.Count} 個片段使用 {item.Info.FileName}。\n\n移除素材會一併移除這些片段，確定要移除嗎？",
                "移除素材", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) return;
            _engine?.Pause();
            var before = _history.Capture();
            foreach (var clip in clips) _timeline.Remove(clip);
            _history.Commit(before, $"移除素材 {item.Info.FileName} 的片段");
            if (TimelineView.SelectedClip is { } selected && clips.Contains(selected)) TimelineView.Select(null);
            OnClipsChanged();
        }

        // 先選好下一個再移除，選取只換一次，不會中途變成「沒有選取」而切到剪輯預覽。
        var index = _media.IndexOf(item);
        MediaList.SelectedItem = index + 1 < _media.Count ? _media[index + 1] : index > 0 ? _media[index - 1] : null;
        _media.RemoveAt(index);
        // 鍵盤焦點留在清單上，可以連按 Delete 移除下一個。
        if (MediaList.SelectedItem != null
            && MediaList.ItemContainerGenerator.ContainerFromItem(MediaList.SelectedItem) is ListBoxItem container)
            container.Focus();
        ShowStatus(clips.Count > 0
            ? $"已從素材清單移除 {item.Info.FileName}，並移除時間軸上的 {clips.Count} 個片段（Ctrl+Z 可以把片段和素材都找回來）"
            : $"已從素材清單移除 {item.Info.FileName}", StatusKind.Info);
    }

    private void OnMediaSelected(object sender, SelectionChangedEventArgs e)
    {
        _selectedMedia = MediaList.SelectedItem as MediaItem;
        InvalidateFps();
        MediaDetails.Text = _selectedMedia == null ? "" : DescribeMedia(_selectedMedia.Info);
        _points.Clear();
        if (_selectedMedia == null)
        {
            // 選取的素材被移除且清單已空：匯入影片預覽沒有東西可看，時間軸上還有片段就改看剪輯預覽。
            if (!IsTimelinePreview)
            {
                if (_timeline.AllClips.Any()) SwitchToTimelinePreview();
                else LoadPreviewSource();
            }
            if (_media.Count == 0 && !_timeline.AllClips.Any()) EmptyHint.Visibility = Visibility.Visible;
            UpdateOutputInfo();
            UpdateMarks();
            return;
        }
        EmptyHint.Visibility = Visibility.Collapsed;
        UpdateOutputInfo();
        if (SourceMode.IsChecked == true) LoadPreviewSource();
        else SourceMode.IsChecked = true;
        UpdateMarks();
    }

    private static string DescribeMedia(MediaInfo m)
    {
        if (m.IsStill)
            return string.Join('\n', m.FileName, $"圖片：{m.DisplayWidth}×{m.DisplayHeight}（{m.AspectLabel}）",
                $"格式：{m.VideoCodec}", $"加入時間軸為 {Timeline.StillClipDuration:0} 秒，之後可拖曳兩端調整長度");
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
