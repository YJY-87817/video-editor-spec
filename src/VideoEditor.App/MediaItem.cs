using System.ComponentModel;
using System.Windows.Media.Imaging;
using VideoEditor.Core.Models;
using VideoEditor.Core.Playback;

namespace VideoEditor.App;

/// <summary>素材清單裡的一個項目。時間軸上用了幾段會變，所以通知畫面（健檢報告 W-7）；其餘欄位建立後不變。</summary>
public sealed class MediaItem(MediaInfo info) : INotifyPropertyChanged
{
    private int _usageCount;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>時間軸上有幾段片段使用這個素材（UIUX-PLAN-2 #11）。</summary>
    public int UsageCount
    {
        get => _usageCount;
        set
        {
            if (_usageCount == value) return;
            _usageCount = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UsageCount)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsUsed)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UsageText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UsageToolTip)));
        }
    }

    public bool IsUsed => _usageCount > 0;
    public string UsageText => $"{_usageCount} 段";
    public string UsageToolTip => $"時間軸上有 {_usageCount} 段使用這個素材";

    public MediaInfo Info { get; } = info;
    public BitmapSource? Thumbnail { get; set; }
    public string Summary =>
        Info.IsStill ? $"圖片  {Info.DisplayWidth}×{Info.DisplayHeight}"
        : Info.HasVideo ? $"{Info.DisplayWidth}×{Info.DisplayHeight}  {PlaybackText.Duration(Info.Duration)}"
        : $"音訊  {PlaybackText.Duration(Info.Duration)}";
}
