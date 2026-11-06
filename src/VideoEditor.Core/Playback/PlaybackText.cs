namespace VideoEditor.Core.Playback;

/// <summary>播放控制列上的文字。</summary>
public static class PlaybackText
{
    public static string Play(bool playing, bool rewinding) => playing || rewinding ? "⏸ 暫停" : "▶ 播放";

    public static string Rewind(bool rewinding, int speed) => rewinding ? $"◀◀ {speed}×" : "◀◀ 倒帶";

    public static string FastForward(bool fastForwarding, int rate) => fastForwarding ? $"▶▶ {rate}×" : "▶▶ 快轉";

    /// <summary>時、分、秒與百分之一秒，例如 00:01:05.50。負值視為 0。</summary>
    public static string Duration(double seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return $"{(int)span.TotalHours:00}:{span.Minutes:00}:{span.Seconds:00}.{span.Milliseconds / 10:00}";
    }

    public static string Position(double position, double duration) => $"{Duration(position)} / {Duration(duration)}";
}
