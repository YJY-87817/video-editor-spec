namespace VideoEditor.Core.Editing;

/// <summary>時間軸片段的滑鼠提示（UIUX-PLAN-2 #7）。</summary>
public static class ClipDescription
{
    /// <summary>
    /// 幾行文字：檔名、在時間軸上的位置與長度、來源的入點與出點（圖片沒有），
    /// 再列出改過的設定（音量、倒放、旋轉鏡像、畫面位置）。
    /// 時間由 <paramref name="formatTime"/>（秒數, 影格率）格式化（呼叫端依顯示方式傳入，Editing 不依賴 Playback）；
    /// 時間軸的時間用 <paramref name="timelineFps"/>（輸出基準），來源的時間用素材自己的影格率。
    /// </summary>
    public static string Describe(TimelineClip clip, double timelineFps, Func<double, double, string> formatTime)
    {
        string Timeline(double seconds) => formatTime(seconds, timelineFps);
        string Source(double seconds) => formatTime(seconds, clip.Media.FrameRate.Value);

        var kind = clip.Media.IsStill ? "（圖片）" : clip.Track == TrackKind.Music ? "（配樂）" : "";
        var lines = new List<string>
        {
            clip.Media.FileName + kind,
            $"時間軸：{Timeline(clip.Start)} – {Timeline(clip.End)}",
            $"長度：{Timeline(clip.Duration)}",
        };
        if (!clip.Media.IsStill) lines.Add($"來源：{Source(clip.SourceIn)} – {Source(clip.SourceOut)}");
        if (clip.Media.HasAudio && Math.Abs(clip.Volume - 1) > 1e-9)
            lines.Add($"音量：{clip.Volume * 100:0}%（{VolumeText.Describe(clip.Volume * 100)}）");
        if (clip.Reversed) lines.Add("倒放");
        if (!clip.Transform.IsIdentity) lines.Add($"旋轉鏡像：{clip.Transform.Label}");
        if (!clip.Framing.IsDefault) lines.Add($"畫面位置：{clip.Framing.Label}");
        return string.Join('\n', lines);
    }
}
