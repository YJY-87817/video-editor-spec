using VideoEditor.Core.Playback;
using VideoEditor.Core.Editing;

namespace VideoEditor.App;

/// <summary>片段清單裡的一列；<see cref="Label"/> 是顯示的文字，依序串起編號、檔名、長度與各項設定。</summary>
public sealed record ClipRow(TimelineClip Clip, int Number)
{
    public string Label => string.Concat(
        $"{Number}. {(Clip.Media.IsStill ? "圖片 " : "")}{Clip.Media.FileName}  {PlaybackText.Duration(Clip.Duration)}",
        Clip.Reversed ? "  ⟲ 倒放" : "",
        Clip.Transform.IsIdentity ? "" : "  " + Clip.Transform.Label,
        Clip.Framing.IsDefault ? "" : "  " + Clip.Framing.ShortLabel);
}
