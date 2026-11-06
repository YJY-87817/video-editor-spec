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
    // ---- 時間軸與片段 ----

    private void OnClipsChanged()
    {
        RefreshClipList();
        Timeline.InvalidateVisual();
        UpdateTimelineScrollBar();
        UpdateOutputInfo();
        UpdatePreviewLayout();
        if (IsTimelinePreview) _engine?.Refresh();
    }

    private void RefreshClipList()
    {
        _updatingClipList = true;
        var rows = _timeline.VideoClips.Select((clip, index) => new ClipRow(clip, index + 1)).ToList();
        ClipList.ItemsSource = rows;
        ClipList.SelectedItem = rows.FirstOrDefault(r => r.Clip == Timeline.SelectedClip);
        _updatingClipList = false;
    }

    private void OnClipListSelected(object sender, SelectionChangedEventArgs e)
    {
        if (!_updatingClipList && ClipList.SelectedItem is ClipRow row) Timeline.Select(row.Clip);
    }

    private void OnTimelineSelectionChanged(TimelineClip? clip)
    {
        _updatingVolume = true;
        VolumeSlider.IsEnabled = clip is { Media.HasAudio: true };
        VolumeSlider.Value = (clip?.Volume ?? 1) * 100;
        VolumeText.Text = clip == null ? "—" : $"{VolumeSlider.Value:0}%";
        _updatingVolume = false;

        _updatingClipList = true;
        ClipList.SelectedItem = (ClipList.ItemsSource as IEnumerable<ClipRow>)?.FirstOrDefault(r => r.Clip == clip);
        _updatingClipList = false;

        _updatingReverse = true;
        ReverseCheck.IsEnabled = clip is { Track: TrackKind.Video };
        ReverseCheck.IsChecked = clip?.Reversed == true;
        _updatingReverse = false;
        UpdateTransformControls(clip);
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
        if (Timeline.SelectedClip is not { Track: TrackKind.Video } clip) return;
        var followUp = _editor.ChangeTransform(clip, change, IsTimelinePreview, Timeline.Playhead);
        UpdateTransformControls(clip);
        OnClipsChanged();
        // 看得到效果：切到時間軸預覽，並把播放頭移進這個片段。
        switch (followUp)
        {
            case PreviewFollowUp.ShowClipInTimeline:
                Timeline.Playhead = clip.Start;
                SwitchToTimelinePreview();
                break;
            case PreviewFollowUp.SeekToClip:
                _engine?.Seek(clip.Start);
                break;
        }
    }

    private void OnReverseChanged(object sender, RoutedEventArgs e)
    {
        if (_updatingReverse || Timeline.SelectedClip is not { Track: TrackKind.Video } clip) return;
        _engine?.Pause();
        _timeline.SetReversed(clip, ReverseCheck.IsChecked == true);
        // 先在背景產生倒放預覽檔，按播放時多半已經好了。
        _engine?.PrepareReverse(clip);
        OnClipsChanged();
    }

    private void OnVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingVolume || Timeline.SelectedClip is not { } clip) return;
        clip.Volume = TimelineEditor.VolumeFromPercent(e.NewValue);
        VolumeText.Text = $"{e.NewValue:0}%";
        Timeline.InvalidateVisual();
    }

    private void OnMoveUp(object sender, RoutedEventArgs e) => SwapSelected(-1);
    private void OnMoveDown(object sender, RoutedEventArgs e) => SwapSelected(1);

    private void SwapSelected(int direction)
    {
        if (Timeline.SelectedClip is { } clip && _timeline.Swap(clip, direction)) OnClipsChanged();
    }

    private void OnDeleteClip(object sender, RoutedEventArgs e) => DeleteSelectedClip();

    private void DeleteSelectedClip()
    {
        if (Timeline.SelectedClip is not { } clip) return;
        _timeline.Remove(clip);
        Timeline.Select(null);
        OnClipsChanged();
    }

    private void OnZoomIn(object sender, RoutedEventArgs e) => Timeline.SetZoom(Timeline.PixelsPerSecond * ZoomStep);
    private void OnZoomOut(object sender, RoutedEventArgs e) => Timeline.SetZoom(Timeline.PixelsPerSecond / ZoomStep);
    private void OnZoomFit(object sender, RoutedEventArgs e) => Timeline.ZoomToFit();

    private void OnTimelineScroll(object sender, ScrollEventArgs e) => Timeline.SetScrollOffset(e.NewValue);

    private void UpdateTimelineScrollBar()
    {
        TimelineScroll.Minimum = 0;
        TimelineScroll.Maximum = Math.Max(0, Timeline.ContentWidth - Timeline.ViewportWidth);
        TimelineScroll.ViewportSize = Timeline.ViewportWidth;
        TimelineScroll.SmallChange = 40;
        TimelineScroll.LargeChange = Timeline.ViewportWidth * 0.8;
        TimelineScroll.Value = Timeline.ScrollOffset;
    }
}
