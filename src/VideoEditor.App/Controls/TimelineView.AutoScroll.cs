using System.Windows;
using System.Windows.Threading;
using VideoEditor.Core.Editing;

namespace VideoEditor.App.Controls;

/// <summary>
/// 拖曳時游標靠近左右邊緣就自動捲動（UIUX-PLAN-2 #3）：拖曳片段、修剪、拖曳播放頭與拖入素材都適用。
/// 越靠近邊緣越快，最快每秒捲一個畫面寬。捲動後用最後的游標位置重新套用拖曳，片段才會跟著走。
/// </summary>
public sealed partial class TimelineView
{
    private const double AutoScrollEdge = 40;
    private static readonly TimeSpan AutoScrollInterval = TimeSpan.FromMilliseconds(30);

    private DispatcherTimer? _autoScroll;
    private double _autoScrollSpeed;
    private Point _autoScrollPoint;
    private bool _autoScrollDropping;

    private void UpdateAutoScroll(Point p, bool dropping = false)
    {
        _autoScrollPoint = p;
        _autoScrollDropping = dropping;
        _autoScrollSpeed = TimelineDrag.AutoScrollSpeed(p.X, HeaderWidth, ActualWidth, AutoScrollEdge, ViewportWidth);
        if (_autoScrollSpeed == 0)
        {
            StopAutoScroll();
            return;
        }
        if (_autoScroll != null) return;
        _autoScroll = new DispatcherTimer(DispatcherPriority.Input) { Interval = AutoScrollInterval };
        _autoScroll.Tick += OnAutoScrollTick;
        _autoScroll.Start();
    }

    private void OnAutoScrollTick(object? sender, EventArgs e)
    {
        var before = _scrollOffset;
        SetScrollOffset(_scrollOffset + _autoScrollSpeed * AutoScrollInterval.TotalSeconds);
        // 已經捲到頭（或時間軸還不夠長）就不用重套拖曳。
        if (Math.Abs(_scrollOffset - before) < 0.01) return;
        // 拖入素材時，下一次 DragOver 會依新的捲動位置重畫預定位置；拖曳片段時要自己重套。
        if (!_autoScrollDropping && _drag != DragKind.None) ApplyDrag(_autoScrollPoint);
        InvalidateVisual();
    }

    private void StopAutoScroll()
    {
        if (_autoScroll == null) return;
        _autoScroll.Stop();
        _autoScroll.Tick -= OnAutoScrollTick;
        _autoScroll = null;
        _autoScrollSpeed = 0;
    }
}
