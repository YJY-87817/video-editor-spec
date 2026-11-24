using System.Windows.Media.Imaging;
using VideoEditor.Core.Models;
using VideoEditor.Core.Playback;

namespace VideoEditor.App;

/// <summary>素材清單裡的一個項目。</summary>
public sealed class MediaItem(MediaInfo info)
{
    public MediaInfo Info { get; } = info;
    public BitmapSource? Thumbnail { get; set; }
    public string Summary =>
        Info.IsStill ? $"圖片  {Info.DisplayWidth}×{Info.DisplayHeight}"
        : Info.HasVideo ? $"{Info.DisplayWidth}×{Info.DisplayHeight}  {PlaybackText.Duration(Info.Duration)}"
        : $"音訊  {PlaybackText.Duration(Info.Duration)}";
}
