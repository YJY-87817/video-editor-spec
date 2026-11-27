using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using VideoEditor.Core.Editing;
using VideoEditor.Core.Logging;
using VideoEditor.Core.Models;

namespace VideoEditor.App;

public partial class MainWindow
{
    // ---- 素材清單 → 時間軸：拖曳、雙擊（移除素材在 MainWindow.Import.cs） ----

    private Point _mediaDragStart;
    private MediaItem? _mediaDragItem;

    private void OnMediaListMouseDown(object sender, MouseButtonEventArgs e)
    {
        _mediaDragStart = e.GetPosition(MediaList);
        _mediaDragItem = e.OriginalSource is DependencyObject source
            && ItemsControl.ContainerFromElement(MediaList, source) is ListBoxItem { DataContext: MediaItem item } ? item : null;
    }

    /// <summary>按住素材移動超過系統的拖曳距離才開始拖曳，單純點選不受影響。</summary>
    private void OnMediaListMouseMove(object sender, MouseEventArgs e)
    {
        if (_mediaDragItem is not { } item || e.LeftButton != MouseButtonState.Pressed) return;
        var offset = e.GetPosition(MediaList) - _mediaDragStart;
        if (Math.Abs(offset.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(offset.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _mediaDragItem = null;
        DragDrop.DoDragDrop(MediaList, new DataObject(typeof(MediaItem), item), DragDropEffects.Copy);
    }

    /// <summary>時間軸問：拖曳中的是不是素材，加入後多長（圖片 5 秒；選取中的素材套用已標的入出點）。</summary>
    private (MediaInfo Media, double Duration)? DraggedMedia(IDataObject data)
    {
        if (data.GetData(typeof(MediaItem)) is not MediaItem item) return null;
        var info = item.Info;
        var duration = info.IsStill ? Timeline.StillClipDuration
            : item == _selectedMedia ? _points.OutPoint(info) - _points.InPoint
            : info.Duration;
        return (info, duration);
    }

    private void OnMediaDropped(MediaInfo media, double time)
    {
        var item = _media.FirstOrDefault(m => m.Info == media);
        // 入出點只屬於選取中（正在預覽）的素材；拖別的素材時整段加入。
        var points = item != null && item == _selectedMedia ? _points : new InOutPoints();
        _engine?.Pause();
        var before = _history.Capture();
        var result = _editor.AddAt(media, points, time);
        if (result.Clip == null)
        {
            ShowStatus(result.Error ?? "", StatusKind.Warning);
            return;
        }
        _history.Commit(before, "加入片段");
        TimelineView.Select(result.Clip);
        UpdateMarks();
        OnClipsChanged();
        ShowInsertResult(1, result.Shift, result.ShiftedCount);
    }

    /// <summary>從檔案總管拖到時間軸：先匯入（已匯入的不重複），再依拖進來的順序接著放；影片與聲音各自接在自己的軌道上。整批算一步復原。</summary>
    private async void OnFilesDroppedOnTimeline(string[] files, double time)
    {
        if (_import == null) return;
        try
        {
            await _import.ImportAsync(files);
        }
        catch (Exception e)
        {
            Log.Error("匯入時發生預料之外的錯誤", e);
            ShowStatus($"匯入時發生錯誤：{e.Message}", StatusKind.Error);
            return;
        }

        var items = files.Select(FindMedia).OfType<MediaItem>().Distinct().ToList();
        if (items.Count == 0) return;
        _engine?.Pause();
        var before = _history.Capture();
        var next = new Dictionary<TrackKind, double>();
        TimelineClip? last = null;
        var added = 0;
        double shift = 0;
        var shifted = 0;
        foreach (var item in items)
        {
            var track = Timeline.DefaultTrack(item.Info);
            var result = _editor.AddAt(item.Info, new InOutPoints(), next.GetValueOrDefault(track, time));
            if (result.Clip == null)
            {
                ShowStatus($"{item.Info.FileName}：{result.Error}", StatusKind.Warning);
                continue;
            }
            next[track] = result.Clip.End;
            last = result.Clip;
            added++;
            shift += result.Shift;
            shifted = Math.Max(shifted, result.ShiftedCount);
        }
        if (last == null) return;
        _history.Commit(before, added == 1 ? "加入片段" : $"加入 {added} 個片段");
        TimelineView.Select(last);
        OnClipsChanged();
        ShowInsertResult(added, shift, shifted);
    }

    private MediaItem? FindMedia(string path)
    {
        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
        return _media.FirstOrDefault(m => string.Equals(m.Info.Path, full, StringComparison.OrdinalIgnoreCase)
            || string.Equals(m.Info.Path, path, StringComparison.OrdinalIgnoreCase));
    }

    private void ShowInsertResult(int added, double shift, int shiftedCount)
    {
        var what = added == 1 ? "已加入片段" : $"已加入 {added} 個片段";
        if (shiftedCount == 0) ShowStatus(what, StatusKind.Success);
        else ShowStatus($"{what}，後面 {shiftedCount} 段往後移 {shift:0.##} 秒（Ctrl+Z 可復原）", StatusKind.Success);
    }

    /// <summary>雙擊素材：和按「加入時間軸」一樣，接在最後面。</summary>
    private void OnMediaDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (!IsOnListItem(MediaList, e)) return;
        OnAddToTimeline(sender, e);
        e.Handled = true;
    }

    // ---- 時間軸右鍵選單 ----

    /// <summary>依右鍵選取的片段決定選單項目：有片段時是片段的操作，空白處是時間軸的顯示與吸附設定。</summary>
    private void OnTimelineContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var clip = TimelineView.SelectedClip;
        var video = clip is { Track: TrackKind.Video };
        foreach (var item in new Control[] { MenuSplit, MenuDelete, MenuRippleDelete, MenuClipSeparator1, MenuSwapPrevious, MenuSwapNext, MenuClipSeparator2, MenuVolumeReset })
            item.Visibility = clip != null ? Visibility.Visible : Visibility.Collapsed;
        foreach (var item in new Control[] { MenuReverse, MenuRotateLeft, MenuRotateRight, MenuFlipHorizontal, MenuFlipVertical, MenuResetFraming, MenuClipSeparator3 })
            item.Visibility = video ? Visibility.Visible : Visibility.Collapsed;
        foreach (var item in new Control[] { MenuZoomFit, MenuSnapSilence, MenuSnapClips })
            item.Visibility = clip == null ? Visibility.Visible : Visibility.Collapsed;

        if (clip != null)
        {
            var list = clip.Track == TrackKind.Video ? _timeline.VideoClips : _timeline.MusicClips;
            var index = list.ToList().IndexOf(clip);
            MenuSwapPrevious.IsEnabled = index > 0;
            MenuSwapNext.IsEnabled = index >= 0 && index < list.Count - 1;
            MenuVolumeReset.IsEnabled = clip.Media.HasAudio && Math.Abs(clip.Volume - 1) > 1e-9;
            MenuReverse.IsEnabled = ReverseCheck.IsEnabled;
            MenuReverse.IsChecked = clip.Reversed;
            MenuResetFraming.IsEnabled = SelectedMode == FitMode.Fill && !clip.Framing.IsDefault;
        }
        MenuSnapSilence.IsChecked = SilenceSnapCheck.IsChecked == true;
        MenuSnapClips.IsChecked = ClipSnapCheck.IsChecked == true;
    }

    private void OnMenuSwapPrevious(object sender, RoutedEventArgs e) => SwapSelected(-1);
    private void OnMenuSwapNext(object sender, RoutedEventArgs e) => SwapSelected(1);

    /// <summary>經由右側的勾選框切換，和在右側勾選走同一條路（含復原紀錄）。</summary>
    private void OnMenuReverse(object sender, RoutedEventArgs e) => ReverseCheck.IsChecked = MenuReverse.IsChecked;

    private void OnMenuVolumeReset(object sender, RoutedEventArgs e) => VolumeSlider.Value = 100;

    private void OnMenuResetFraming(object sender, RoutedEventArgs e)
    {
        if (TimelineView.SelectedClip is { Track: TrackKind.Video } clip) ResetFraming(clip);
    }

    private void OnMenuSnapSilence(object sender, RoutedEventArgs e)
    {
        SilenceSnapCheck.IsChecked = MenuSnapSilence.IsChecked;
        TimelineView.SnapToSilence = MenuSnapSilence.IsChecked;
    }

    private void OnMenuSnapClips(object sender, RoutedEventArgs e)
    {
        ClipSnapCheck.IsChecked = MenuSnapClips.IsChecked;
        TimelineView.SnapToClips = MenuSnapClips.IsChecked;
    }
}
