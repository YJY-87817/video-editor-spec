namespace VideoEditor.Core.Playback;

/// <summary>預覽區可以下的指令。按鈕與快捷鍵共用同一組。</summary>
public enum PreviewCommand
{
    TogglePlay,
    Pause,
    Rewind,
    FastForward,
    /// <summary>L：停止時播放，播放中再按加速。</summary>
    PlayOrFaster,
    StepBack,
    StepForward,
    MarkIn,
    MarkOut,
    DeleteClip,
}

public static class PreviewKeys
{
    /// <summary>
    /// 快捷鍵對應的指令。keyName 是 WPF <c>Key</c> 列舉的名稱（<c>e.Key.ToString()</c>），
    /// 這樣核心不必依賴 WPF。沒有對應的按鍵回傳 null。
    /// </summary>
    public static PreviewCommand? FromKeyName(string keyName) => keyName switch
    {
        "Space" => PreviewCommand.TogglePlay,
        "J" => PreviewCommand.Rewind,
        "K" => PreviewCommand.Pause,
        "L" => PreviewCommand.PlayOrFaster,
        "Left" => PreviewCommand.StepBack,
        "Right" => PreviewCommand.StepForward,
        "I" => PreviewCommand.MarkIn,
        "O" => PreviewCommand.MarkOut,
        "Delete" => PreviewCommand.DeleteClip,
        _ => null,
    };
}
