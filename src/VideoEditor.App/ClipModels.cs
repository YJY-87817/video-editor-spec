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

public sealed class MediaItem(MediaInfo info)
{
    public MediaInfo Info { get; } = info;
    public BitmapSource? Thumbnail { get; set; }
    public string Summary => Info.HasVideo
        ? $"{Info.DisplayWidth}×{Info.DisplayHeight}  {PlaybackText.Duration(Info.Duration)}"
        : $"音訊  {PlaybackText.Duration(Info.Duration)}";
}

public sealed record ClipRow(TimelineClip Clip, int Number)
{
    public string Label => $"{Number}. {Clip.Media.FileName}  {PlaybackText.Duration(Clip.Duration)}{(Clip.Reversed ? "  ⟲ 倒放" : "")}{(Clip.Transform.IsIdentity ? "" : "  " + Clip.Transform.Label)}";
}