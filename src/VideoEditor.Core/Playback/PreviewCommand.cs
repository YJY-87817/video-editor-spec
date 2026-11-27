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

/// <summary>
/// 預覽快捷鍵用到的按鍵。App 端把 WPF 的 <c>Key</c> 以 switch 對應過來（拼錯會編譯失敗），核心不必依賴 WPF，
/// 按鍵也不必每次轉成字串。WPF 的 <c>Key.Prior</c>／<c>Key.Next</c> 與 <c>Key.PageUp</c>／<c>Key.PageDown</c> 是同一個值。
/// </summary>
public enum PreviewKey
{
    Space, J, K, L, Left, Right, I, O, Delete, S, PageUp, PageDown,
}

public static class PreviewKeys
{
    /// <summary>
    /// 預覽快捷鍵只在沒有按任何修飾鍵時處理。
    /// Ctrl+S（使用者習慣的存檔）等組合鍵若被當成 S（分割片段），會在沒有預期時改動時間軸。
    /// 組合鍵另外處理（Ctrl+Z 復原、Ctrl+G 跳到指定時間）。
    /// </summary>
    public static bool ShouldHandle(KeyModifiers modifiers) => modifiers == KeyModifiers.None;

    /// <summary>快捷鍵對應的指令。每個 <see cref="PreviewKey"/> 都有對應的指令。</summary>
    public static PreviewCommand FromKey(PreviewKey key) => key switch
    {
        PreviewKey.Space => PreviewCommand.TogglePlay,
        PreviewKey.J => PreviewCommand.Rewind,
        PreviewKey.K => PreviewCommand.Pause,
        PreviewKey.L => PreviewCommand.PlayOrFaster,
        PreviewKey.Left => PreviewCommand.StepBack,
        PreviewKey.Right => PreviewCommand.StepForward,
        PreviewKey.I => PreviewCommand.MarkIn,
        PreviewKey.O => PreviewCommand.MarkOut,
        PreviewKey.Delete => PreviewCommand.DeleteClip,
        PreviewKey.S => PreviewCommand.SplitClip,
        PreviewKey.PageUp => PreviewCommand.PreviousEditPoint,
        PreviewKey.PageDown => PreviewCommand.NextEditPoint,
        _ => throw new ArgumentOutOfRangeException(nameof(key), key, "沒有對應的預覽指令"),
    };
}
