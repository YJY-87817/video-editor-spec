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
    // ---- 入出點 ----

    private void OnMarkIn(object sender, RoutedEventArgs e) => MarkIn();
    private void OnMarkOut(object sender, RoutedEventArgs e) => MarkOut();

    private void MarkIn()
    {
        if (_engine == null || _selectedMedia == null || IsTimelinePreview) return;
        _points.SetIn(InOutPoints.Snap(_engine.Position, _selectedMedia.Info));
        UpdateMarks();
    }

    private void MarkOut()
    {
        if (_engine == null || _selectedMedia == null || IsTimelinePreview) return;
        _points.SetOut(InOutPoints.Snap(_engine.Position, _selectedMedia.Info));
        UpdateMarks();
    }

    private void UpdateMarks()
    {
        MarkText.Text = _selectedMedia == null || IsTimelinePreview
            ? ""
            : $"I {PlaybackText.Duration(_points.InPoint)}  O {PlaybackText.Duration(_points.OutPoint(_selectedMedia.Info))}";
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
        var inPoint = _points.InPoint;
        var outPoint = _points.OutPoint(_selectedMedia.Info);
        MarkRange.Visibility = Visibility.Visible;
        Canvas.SetLeft(MarkRange, inPoint / duration * ScrubSlider.ActualWidth);
        MarkRange.Width = Math.Max(1, (outPoint - inPoint) / duration * ScrubSlider.ActualWidth);
    }

    private void OnAddToTimeline(object sender, RoutedEventArgs e)
    {
        if (_selectedMedia == null) return;
        var result = _editor.Add(_selectedMedia.Info, _points);
        if (result.Clip == null)
        {
            StatusText.Text = result.Error ?? "";
            return;
        }
        Timeline.Select(result.Clip);
        UpdateMarks();
        OnClipsChanged();
    }
}
