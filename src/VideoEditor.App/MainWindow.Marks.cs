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
    // ---- 入出點 ----

    private void OnMarkIn(object sender, RoutedEventArgs e) => MarkIn();
    private void OnMarkOut(object sender, RoutedEventArgs e) => MarkOut();

    /// <summary>對齊到畫面上顯示的那一格，匯出的切點才會與看到的相同。</summary>
    private double SnappedSourcePosition(MediaInfo media)
    {
        var position = _engine!.Position;
        return media.FrameRate.Value > 0
            ? Math.Min(media.Duration, FrameExtractor.FrameIndex(position, media.FrameRate.Value) / media.FrameRate.Value)
            : position;
    }

    private void MarkIn()
    {
        if (_engine == null || _selectedMedia == null || IsTimelinePreview) return;
        _markIn = SnappedSourcePosition(_selectedMedia.Info);
        if (_markOut <= _markIn) _markOut = null;
        UpdateMarks();
    }

    private void MarkOut()
    {
        if (_engine == null || _selectedMedia == null || IsTimelinePreview) return;
        _markOut = SnappedSourcePosition(_selectedMedia.Info);
        if (_markIn >= _markOut) _markIn = null;
        UpdateMarks();
    }

    private void UpdateMarks()
    {
        MarkText.Text = _selectedMedia == null || IsTimelinePreview
            ? ""
            : $"I {FormatDuration(_markIn ?? 0)}  O {FormatDuration(_markOut ?? _selectedMedia.Info.Duration)}";
        UpdateMarkRange();
    }

    private void UpdateMarkRange()
    {
        if (_selectedMedia == null || IsTimelinePreview || _selectedMedia.Info.Duration <= 0)
        {
            MarkRange.Visibility = Visibility.Collapsed;
            return;
        }
        var duration = _selectedMedia.Info.Duration;
        var inPoint = _markIn ?? 0;
        var outPoint = _markOut ?? duration;
        MarkRange.Visibility = Visibility.Visible;
        Canvas.SetLeft(MarkRange, inPoint / duration * ScrubSlider.ActualWidth);
        MarkRange.Width = Math.Max(1, (outPoint - inPoint) / duration * ScrubSlider.ActualWidth);
    }

    private void OnAddToTimeline(object sender, RoutedEventArgs e)
    {
        if (_selectedMedia == null) return;
        var media = _selectedMedia.Info;
        try
        {
            var clip = _timeline.Append(media, _markIn ?? 0, _markOut ?? media.Duration);
            Timeline.Select(clip);
        }
        catch (ArgumentException error)
        {
            StatusText.Text = error.Message;
            return;
        }
        _markIn = null;
        _markOut = null;
        UpdateMarks();
        OnClipsChanged();
    }
}
