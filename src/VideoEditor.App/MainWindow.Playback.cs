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
using VideoEditor.Core.Playback;
using VideoEditor.Core.Timeline;
using TimelineModel = VideoEditor.Core.Timeline.Timeline;

namespace VideoEditor.App;

public partial class MainWindow
{
    // ---- 播放控制 ----

    private void OnEnginePosition()
    {
        var position = _engine!.Position;
        _updatingSlider = true;
        ScrubSlider.Maximum = Math.Max(0.001, _engine.Duration);
        ScrubSlider.Value = position;
        _updatingSlider = false;
        TimeText.Text = PlaybackText.Position(position, _engine.Duration);
        if (IsTimelinePreview) Timeline.Playhead = position;
        UpdateMarkRange();
    }

    private void OnScrubChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_updatingSlider) _engine?.Seek(e.NewValue);
    }

    private void OnTimelinePlayheadDragged(double time)
    {
        SwitchToTimelinePreview();
        _engine?.Seek(time);
    }

    private void OnPlayClick(object sender, RoutedEventArgs e) => Execute(PreviewCommand.TogglePlay);
    private void OnRewindClick(object sender, RoutedEventArgs e) => Execute(PreviewCommand.Rewind);
    private void OnFastForwardClick(object sender, RoutedEventArgs e) => Execute(PreviewCommand.FastForward);
    private void OnStepBack(object sender, RoutedEventArgs e) => Execute(PreviewCommand.StepBack);
    private void OnStepForward(object sender, RoutedEventArgs e) => Execute(PreviewCommand.StepForward);

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (PreviewKeys.FromKeyName(e.Key.ToString()) is not { } command) return;
        Execute(command);
        e.Handled = true;
    }

    /// <summary>按鈕與快捷鍵共用的指令入口。</summary>
    private void Execute(PreviewCommand command)
    {
        switch (command)
        {
            case PreviewCommand.TogglePlay: _engine?.Toggle(); break;
            case PreviewCommand.Pause: _engine?.Pause(); break;
            case PreviewCommand.Rewind: _engine?.Rewind(); break;
            case PreviewCommand.FastForward: _engine?.FastForward(); break;
            case PreviewCommand.PlayOrFaster: _engine?.PlayOrFaster(); break;
            case PreviewCommand.StepBack: _engine?.StepFrame(-1); break;
            case PreviewCommand.StepForward: _engine?.StepFrame(1); break;
            case PreviewCommand.MarkIn: MarkIn(); break;
            case PreviewCommand.MarkOut: MarkOut(); break;
            case PreviewCommand.DeleteClip: DeleteSelectedClip(); break;
        }
    }

    private void UpdatePlaybackButtons()
    {
        var engine = _engine!;
        PlayButton.Content = PlaybackText.Play(engine.IsPlaying, engine.IsRewinding);
        RewindButton.Content = PlaybackText.Rewind(engine.IsRewinding, engine.RewindSpeed);
        FastForwardButton.Content = PlaybackText.FastForward(engine.IsFastForwarding, engine.PlaybackRate);
    }
}
