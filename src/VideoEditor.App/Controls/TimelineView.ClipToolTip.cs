using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using VideoEditor.Core.Editing;
using VideoEditor.Core.Models;

namespace VideoEditor.App.Controls;

/// <summary>
/// 滑鼠停在片段上時的提示（檔名、位置、長度、來源範圍、改過的設定；UIUX-PLAN-2 #7）。
/// <para>
/// 不用元件的 <c>ToolTip</c> 屬性：WPF 只在滑鼠「進入」元件時看有沒有提示，整條時間軸是同一個元件，
/// 滑鼠已經在上面時才換提示內容，提示永遠不會出現。所以這裡自己計時、自己開關。
/// </para>
/// </summary>
public sealed partial class TimelineView
{
    private readonly ToolTip _clipToolTip = new() { Placement = PlacementMode.Mouse };
    private DispatcherTimer? _clipToolTipTimer;
    private TimelineClip? _clipToolTipClip;

    /// <summary>滑鼠移動時呼叫：換到別的片段就關掉、停一下再開；同一段只更新文字。</summary>
    private void UpdateClipToolTip(TimelineClip? clip)
    {
        if (clip == _clipToolTipClip)
        {
            if (_clipToolTip.IsOpen) RefreshClipToolTip();
            return;
        }
        HideClipToolTip();
        if (clip == null) return;
        _clipToolTipClip = clip;
        _clipToolTipTimer ??= CreateClipToolTipTimer();
        _clipToolTipTimer.Start();
    }

    private DispatcherTimer CreateClipToolTipTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(SystemParameters.MouseHoverTime.TotalMilliseconds) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (_clipToolTipClip == null || _drag != DragKind.None || !IsMouseOver) return;
            _clipToolTip.PlacementTarget = this;
            RefreshClipToolTip();
            if (_clipToolTipClip != null) _clipToolTip.IsOpen = true;
        };
        return timer;
    }

    /// <summary>重算文字；片段已經不在時間軸上（被刪掉、復原）就關掉。片段剛被修改過也是最新的。</summary>
    private void RefreshClipToolTip()
    {
        if (_clipToolTipClip is not { } clip) return;
        if (!_timeline.AllClips.Contains(clip))
        {
            HideClipToolTip();
            return;
        }
        var text = ClipDescription.Describe(clip, RulerFps, (seconds, fps) => VideoEditor.Core.Playback.Timecode.Display(seconds, fps, RulerDisplay));
        // 文字沒變就不換，提示才不會閃。
        if (_clipToolTip.Content as string != text) _clipToolTip.Content = text;
    }

    /// <summary>離開時間軸、按下滑鼠（拖曳時提示會擋住畫面）時關掉。</summary>
    private void HideClipToolTip()
    {
        _clipToolTipTimer?.Stop();
        _clipToolTipClip = null;
        _clipToolTip.IsOpen = false;
    }
}
