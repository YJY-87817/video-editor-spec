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
        using (_volumeGuard.Enter())
        {
            VolumeSlider.IsEnabled = clip is { Media.HasAudio: true };
            VolumeSlider.Value = (clip?.Volume ?? 1) * 100;
            VolumeText.Text = clip == null ? "—" : $"{VolumeSlider.Value:0}%";
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

    // ---- 旋轉與鏡像 ----

    private void UpdateTransformControls(TimelineClip? clip)
    {
        TransformPanel.IsEnabled = clip is { Track: TrackKind.Video };
        TransformText.Text = clip is { Track: TrackKind.Video } ? clip.Transform.Label : "—";
    }

    private void OnRotateLeft(object sender, RoutedEventArgs e) => ChangeTransform(t => t.Rotate(-90));
    private void OnRotateRight(object sender, RoutedEventArgs e) => ChangeTransform(t => t.Rotate(90));
    private void OnFlipHorizontal(object sender, RoutedEventArgs e) => ChangeTransform(t => t.ToggleHorizontal());
    private void OnFlipVertical(object sender, RoutedEventArgs e) => ChangeTransform(t => t.ToggleVertical());
    private void OnResetTransform(object sender, RoutedEventArgs e) => ChangeTransform(_ => FrameTransform.Identity);

    private void ChangeTransform(Func<FrameTransform, FrameTransform> change)
    {
        if (TimelineView.SelectedClip is not { Track: TrackKind.Video } clip) return;
        var followUp = _editor.ChangeTransform(clip, change, IsTimelinePreview, TimelineView.Playhead);
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
        _timeline.SetReversed(clip, ReverseCheck.IsChecked == true);
        OnClipsChanged();
    }

    private void OnVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_volumeGuard.Active || TimelineView.SelectedClip is not { } clip) return;
        clip.Volume = TimelineEditor.VolumeFromPercent(e.NewValue);
        VolumeText.Text = $"{e.NewValue:0}%";
        TimelineView.InvalidateVisual();
    }

    private void OnMoveUp(object sender, RoutedEventArgs e) => SwapSelected(-1);
    private void OnMoveDown(object sender, RoutedEventArgs e) => SwapSelected(1);

    private void SwapSelected(int direction)
    {
        if (TimelineView.SelectedClip is { } clip && _timeline.Swap(clip, direction)) OnClipsChanged();
    }

    private void OnDeleteClip(object sender, RoutedEventArgs e) => DeleteSelectedClip();

    private void DeleteSelectedClip()
    {
        if (TimelineView.SelectedClip is not { } clip) return;
        _timeline.Remove(clip);
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
        var result = _editor.Split(TimelineView.SelectedClip, TimelineView.Playhead, IsTimelinePreview);
        if (result.Clip == null)
        {
            StatusText.Text = result.Error ?? "";
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
