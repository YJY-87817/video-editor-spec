using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Microsoft.Win32;
using VideoEditor.App.Preview;
using VideoEditor.Core.Export;
using VideoEditor.Core.Ffmpeg;
using VideoEditor.Core.Models;
using VideoEditor.Core.Editing;

namespace VideoEditor.App;

public partial class MainWindow
{
    // ---- 時間軸與片段 ----

    private void OnClipsChanged()
    {
        RefreshClipList();
        // 片段的編號（第幾段）可能變了。
        UpdateClipPanel(TimelineView.SelectedClip);
        TimelineView.InvalidateVisual();
        UpdateTimelineScrollBar();
        UpdateOutputInfo();
        UpdatePreviewLayout();
        // 先在背景產生倒放預覽檔，按播放時多半已經好了；修剪後舊範圍的預覽檔也在這裡停掉。
        _engine?.SyncReverse(_timeline);
        if (IsTimelinePreview) _engine?.Refresh();
    }

    private void RefreshClipList()
    {
        var rows = _timeline.VideoClips.Select((clip, index) => new ClipRow(clip, index + 1)).ToList();
        using (_clipListGuard.Enter())
        {
            ClipList.ItemsSource = rows;
            ClipList.SelectedItem = rows.FirstOrDefault(r => r.Clip == TimelineView.SelectedClip);
        }
    }

    private void OnClipListSelected(object sender, SelectionChangedEventArgs e)
    {
        if (!_clipListGuard.Active && ClipList.SelectedItem is ClipRow row) TimelineView.Select(row.Clip);
    }

    private void OnTimelineSelectionChanged(TimelineClip? clip)
    {
        UpdateClipPanel(clip);
        SplitButton.IsEnabled = DeleteButton.IsEnabled = clip != null;
        using (_volumeGuard.Enter())
        {
            VolumeSlider.IsEnabled = clip is { Media.HasAudio: true };
            VolumeSlider.Value = (clip?.Volume ?? 1) * 100;
            VolumeText.Text = clip == null ? "—" : clip.Media.HasAudio ? $"{VolumeSlider.Value:0}%" : "無聲音";
        }

        using (_clipListGuard.Enter())
            ClipList.SelectedItem = (ClipList.ItemsSource as IEnumerable<ClipRow>)?.FirstOrDefault(r => r.Clip == clip);

        using (_reverseGuard.Enter())
        {
            // 圖片片段不能倒放（Timeline.SetReversed 會拒絕）。
            ReverseCheck.IsEnabled = clip is { Track: TrackKind.Video, Media.IsStill: false };
            ReverseCheck.IsChecked = clip?.Reversed == true;
        }
        UpdateTransformControls(clip);
        UpdateFramingControls(clip);
    }

    /// <summary>
    /// 右側「選取的片段」：沒有選取時只顯示提示；配樂片段只有音量；影片片段再加上旋轉鏡像、畫面位置與倒放。
    /// </summary>
    private void UpdateClipPanel(TimelineClip? clip)
    {
        ClipHint.Visibility = clip == null ? Visibility.Visible : Visibility.Collapsed;
        ClipPropertiesPanel.Visibility = clip == null ? Visibility.Collapsed : Visibility.Visible;
        if (clip == null) return;
        VideoPropertiesPanel.Visibility = clip.Track == TrackKind.Video ? Visibility.Visible : Visibility.Collapsed;
        ClipPropertiesTitle.Text = clip.Track == TrackKind.Video
            ? $"第 {_timeline.VideoClips.TakeWhile(c => c != clip).Count() + 1} 段：{clip.Media.FileName}"
            : $"配樂：{clip.Media.FileName}";
        ClipPropertiesTitle.ToolTip = clip.Media.Path;
    }

    // ---- 旋轉與鏡像 ----

    private void UpdateTransformControls(TimelineClip? clip)
    {
        TransformPanel.IsEnabled = clip is { Track: TrackKind.Video };
        TransformText.Text = clip is { Track: TrackKind.Video } ? clip.Transform.Label : "—";
    }

    private void OnRotateLeft(object sender, RoutedEventArgs e) => ChangeTransform("左轉 90°", t => t.Rotate(-90));
    private void OnRotateRight(object sender, RoutedEventArgs e) => ChangeTransform("右轉 90°", t => t.Rotate(90));
    private void OnFlipHorizontal(object sender, RoutedEventArgs e) => ChangeTransform("水平鏡像", t => t.ToggleHorizontal());
    private void OnFlipVertical(object sender, RoutedEventArgs e) => ChangeTransform("垂直鏡像", t => t.ToggleVertical());
    private void OnResetTransform(object sender, RoutedEventArgs e) => ChangeTransform("重設旋轉與鏡像", _ => FrameTransform.Identity);

    private void ChangeTransform(string label, Func<FrameTransform, FrameTransform> change)
    {
        if (TimelineView.SelectedClip is not { Track: TrackKind.Video } clip) return;
        var before = _history.Capture();
        var followUp = _editor.ChangeTransform(clip, change, IsTimelinePreview, TimelineView.Playhead);
        _history.Commit(before, label);
        UpdateTransformControls(clip);
        // 方向改變時畫面位置會回到預設。
        UpdateFramingControls(clip);
        OnClipsChanged();
        // 看得到效果：切到時間軸預覽，並把播放頭移進這個片段。
        switch (followUp)
        {
            case PreviewFollowUp.ShowClipInTimeline:
                TimelineView.Playhead = clip.Start;
                SwitchToTimelinePreview();
                break;
            case PreviewFollowUp.SeekToClip:
                _engine?.Seek(clip.Start);
                break;
        }
    }

    private void OnReverseChanged(object sender, RoutedEventArgs e)
    {
        if (_reverseGuard.Active || TimelineView.SelectedClip is not { Track: TrackKind.Video } clip) return;
        _engine?.Pause();
        var before = _history.Capture();
        var reversed = ReverseCheck.IsChecked == true;
        _timeline.SetReversed(clip, reversed);
        _history.Commit(before, reversed ? "倒放" : "取消倒放");
        OnClipsChanged();
    }

    private void OnVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_volumeGuard.Active || TimelineView.SelectedClip is not { } clip) return;
        var before = _history.Capture();
        clip.Volume = TimelineEditor.VolumeFromPercent(e.NewValue);
        // 拖曳滑桿時每一點移動都會進來，同一個片段連續調整併成一步。
        _history.Commit(before, "調整音量", $"volume:{clip.Id}");
        VolumeText.Text = $"{e.NewValue:0}%";
        TimelineView.InvalidateVisual();
    }

    /// <summary>和前一段（-1）或後一段（1）交換順序。右鍵選單使用（UIUX-PLAN 階段 5）。</summary>
    private void SwapSelected(int direction)
    {
        if (TimelineView.SelectedClip is not { } clip) return;
        var before = _history.Capture();
        if (!_timeline.Swap(clip, direction)) return;
        _history.Commit(before, "調整順序");
        OnClipsChanged();
    }

    private void OnDeleteClip(object sender, RoutedEventArgs e) => DeleteSelectedClip();

    private void DeleteSelectedClip()
    {
        if (TimelineView.SelectedClip is not { } clip) return;
        var before = _history.Capture();
        _timeline.Remove(clip);
        _history.Commit(before, "刪除片段");
        TimelineView.Select(null);
        OnClipsChanged();
    }

    private void OnZoomIn(object sender, RoutedEventArgs e) => TimelineView.SetZoom(TimelineView.PixelsPerSecond * ZoomStep);
    private void OnZoomOut(object sender, RoutedEventArgs e) => TimelineView.SetZoom(TimelineView.PixelsPerSecond / ZoomStep);
    private void OnZoomFit(object sender, RoutedEventArgs e) => TimelineView.ZoomToFit();

    private void OnSilenceSnapClick(object sender, RoutedEventArgs e) => TimelineView.SnapToSilence = SilenceSnapCheck.IsChecked == true;

    private void OnClipSnapClick(object sender, RoutedEventArgs e) => TimelineView.SnapToClips = ClipSnapCheck.IsChecked == true;

    // ---- 分割與跳到剪輯點 ----

    private void OnSplitClick(object sender, RoutedEventArgs e) => SplitSelectedClip();

    /// <summary>在播放頭分割選取的片段，成功後選取右段。不能分割時在狀態列說明原因。</summary>
    private void SplitSelectedClip()
    {
        var before = _history.Capture();
        var result = _editor.Split(TimelineView.SelectedClip, TimelineView.Playhead, IsTimelinePreview);
        _history.Commit(before, "分割片段");
        if (result.Clip == null)
        {
            ShowStatus(result.Error ?? "", StatusKind.Warning);
            return;
        }
        _engine?.Pause();
        TimelineView.Select(result.Clip);
        OnClipsChanged();
    }

    /// <summary>
    /// Page Up／Page Down：時間軸預覽時跳到影片軌片段的邊緣（含時間軸開頭）；
    /// 素材模式時在素材開頭、入點、出點、結尾之間跳。已經是第一個或最後一個時不動。
    /// </summary>
    private void JumpToEditPoint(bool forward)
    {
        if (_engine == null) return;
        IReadOnlyList<double> points;
        double position;
        if (IsTimelinePreview)
        {
            points = EditPoints.Timeline(_timeline);
            position = TimelineView.Playhead;
        }
        else
        {
            if (_selectedMedia == null) return;
            points = EditPoints.Source(_selectedMedia.Info, _points);
            position = _engine.Position;
        }
        if ((forward ? EditPoints.Next(points, position) : EditPoints.Previous(points, position)) is not { } target) return;
        if (IsTimelinePreview) TimelineView.Playhead = target;
        _engine.Seek(target);
    }

    private void OnTimelineScroll(object sender, ScrollEventArgs e) => TimelineView.SetScrollOffset(e.NewValue);

    private void UpdateTimelineScrollBar()
    {
        TimelineScroll.Minimum = 0;
        TimelineScroll.Maximum = Math.Max(0, TimelineView.ContentWidth - TimelineView.ViewportWidth);
        TimelineScroll.ViewportSize = TimelineView.ViewportWidth;
        TimelineScroll.SmallChange = 40;
        TimelineScroll.LargeChange = TimelineView.ViewportWidth * 0.8;
        TimelineScroll.Value = TimelineView.ScrollOffset;
    }
}
