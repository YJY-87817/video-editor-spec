using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Microsoft.Win32;
using VideoEditor.App.Preview;
using VideoEditor.Core.Export;
using VideoEditor.Core.Ffmpeg;
using VideoEditor.Core.Models;
using VideoEditor.Core.Playback;

namespace VideoEditor.App;

public partial class MainWindow
{
    // ---- 播放控制 ----

    private void OnEnginePosition()
    {
        var position = _engine!.Position;
        using (_sliderGuard.Enter())
        {
            ScrubSlider.Maximum = Math.Max(0.001, _engine.Duration);
            ScrubSlider.Value = position;
        }
        TimeText.Text = PlaybackText.Position(position, _engine.Duration);
        if (IsTimelinePreview) TimelineView.Playhead = position;
        UpdateMarkRange();
        UpdateFramingForPosition();
    }

    private void OnScrubChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_sliderGuard.Active) _engine?.Seek(e.NewValue);
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

    /// <summary>逐一對應，不靠兩個列舉的數值恰好相同。</summary>
    private static KeyModifiers CurrentModifiers()
    {
        var keys = Keyboard.Modifiers;
        var result = KeyModifiers.None;
        if (keys.HasFlag(ModifierKeys.Alt)) result |= KeyModifiers.Alt;
        if (keys.HasFlag(ModifierKeys.Control)) result |= KeyModifiers.Control;
        if (keys.HasFlag(ModifierKeys.Shift)) result |= KeyModifiers.Shift;
        if (keys.HasFlag(ModifierKeys.Windows)) result |= KeyModifiers.Windows;
        return result;
    }

    /// <summary>WPF 的按鍵對應到預覽快捷鍵；不是快捷鍵的按鍵回傳 null。</summary>
    private static PreviewKey? ToPreviewKey(Key key) => key switch
    {
        Key.Space => PreviewKey.Space,
        Key.J => PreviewKey.J,
        Key.K => PreviewKey.K,
        Key.L => PreviewKey.L,
        Key.Left => PreviewKey.Left,
        Key.Right => PreviewKey.Right,
        Key.I => PreviewKey.I,
        Key.O => PreviewKey.O,
        Key.Delete => PreviewKey.Delete,
        Key.S => PreviewKey.S,
        Key.PageUp => PreviewKey.PageUp,
        Key.PageDown => PreviewKey.PageDown,
        _ => null,
    };

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Ctrl／Alt／Shift／Win 組合鍵（例如 Ctrl+S）不是預覽快捷鍵。
        if (!PreviewKeys.ShouldHandle(CurrentModifiers())) return;
        // 正在輸入文字時，按鍵歸文字框。
        if (e.OriginalSource is System.Windows.Controls.Primitives.TextBoxBase or System.Windows.Controls.PasswordBox) return;
        if (ToPreviewKey(e.Key) is not { } key) return;
        var command = PreviewKeys.FromKey(key);
        // 清單與捲軸自己用換頁鍵捲動，不搶。
        if (command is PreviewCommand.PreviousEditPoint or PreviewCommand.NextEditPoint
            && e.OriginalSource is System.Windows.Controls.Primitives.Selector or System.Windows.Controls.ListBoxItem
                or System.Windows.Controls.Primitives.ScrollBar) return;
        // 焦點在素材清單時，Delete 移除選取的素材，而不是時間軸上的片段。
        if (command == PreviewCommand.DeleteClip && MediaList.IsKeyboardFocusWithin) RemoveSelectedMedia();
        else Execute(command);
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
            case PreviewCommand.SplitClip: SplitSelectedClip(); break;
            case PreviewCommand.PreviousEditPoint: JumpToEditPoint(forward: false); break;
            case PreviewCommand.NextEditPoint: JumpToEditPoint(forward: true); break;
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
