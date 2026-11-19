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
    /// <summary>S：在播放頭分割選取的片段。</summary>
    SplitClip,
    /// <summary>Page Up：跳到上一個剪輯點。</summary>
    PreviousEditPoint,
    /// <summary>Page Down：跳到下一個剪輯點。</summary>
    NextEditPoint,
}

/// <summary>按下快捷鍵時同時按著的修飾鍵。核心不依賴 WPF，由 App 端把 <c>ModifierKeys</c> 逐一對應過來。</summary>
[Flags]
public enum KeyModifiers
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4,
    Windows = 8,
}

public static class PreviewKeys
{
    /// <summary>
    /// 預覽快捷鍵只在沒有按任何修飾鍵時處理。
    /// Ctrl+S（使用者習慣的存檔）等組合鍵若被當成 S（分割片段），會改動時間軸，而本程式沒有復原。
    /// </summary>
    public static bool ShouldHandle(KeyModifiers modifiers) => modifiers == KeyModifiers.None;

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
        "S" => PreviewCommand.SplitClip,
        // Key.PageUp 與 Key.Prior、Key.PageDown 與 Key.Next 是同一個值，轉成文字可能是其中任一個。
        "PageUp" or "Prior" => PreviewCommand.PreviousEditPoint,
        "PageDown" or "Next" => PreviewCommand.NextEditPoint,
        _ => null,
    };
}
