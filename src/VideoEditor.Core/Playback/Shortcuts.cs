namespace VideoEditor.Core.Playback;

/// <summary>快捷鍵一覽（F1）的一列。</summary>
public sealed record Shortcut(string Group, string Keys, string Description);

/// <summary>
/// 快捷鍵一覽的內容（UIUX-PLAN #15）。和 <see cref="PreviewKeys"/> 放在一起，改快捷鍵時一起改；
/// 測試會檢查每個 <see cref="PreviewKey"/> 都列在這裡。
/// </summary>
public static class Shortcuts
{
    public static readonly IReadOnlyList<Shortcut> All =
    [
        new("播放", "空白鍵", "播放／暫停"),
        new("播放", "J", "倒帶（往回播放）；倒帶中再按加速為 2×、4×、8×"),
        new("播放", "K", "暫停"),
        new("播放", "L", "播放；播放中再按快轉為 2×、4×、8×"),
        new("播放", "← / →", "前一格／後一格"),
        new("播放", "Page Up / Page Down", "跳到上一個／下一個剪輯點"),
        new("播放", "Home / End", "跳到開頭／結尾"),
        new("播放", "Ctrl + G", "跳到指定時間（也可以點目前時間的文字）"),
        new("標記", "I", "把目前這一格設為入點（匯入影片預覽）"),
        new("標記", "O", "把目前這一格設為出點（匯入影片預覽）"),
        new("標記", "E", "把預覽中的素材（入點到出點）加到時間軸最後面"),
        new("標記", "Shift + E", "把預覽中的素材插入到播放頭（在片段前半插在前面，後半插在後面）"),
        new("編輯", "S", "在時間軸播放頭分割選取的片段（剪輯預覽）"),
        new("編輯", "Delete", "刪除選取的片段；焦點在素材清單時是從清單移除素材"),
        new("編輯", "Shift + Delete", "刪除並補齊：刪除片段，同一軌後面的片段往前移"),
        new("編輯", "Alt + ← / →", "選取前一段／後一段片段"),
        new("編輯", "Ctrl + Z", "復原"),
        new("編輯", "Ctrl + Y／Ctrl + Shift + Z", "重做"),
        new("時間軸", "Ctrl + 滾輪、+ / -", "縮放時間軸"),
        new("時間軸", "滾輪", "左右捲動時間軸"),
        new("時間軸", "按住 Alt 拖曳", "暫時不吸附"),
        new("其他", "F1", "顯示這個一覽"),
    ];

    /// <summary>每個預覽快捷鍵在一覽裡的寫法（測試用，確保沒有漏列）。</summary>
    public static string KeysText(PreviewKey key) => key switch
    {
        PreviewKey.Space => "空白鍵",
        PreviewKey.Left or PreviewKey.Right => "← / →",
        PreviewKey.PageUp or PreviewKey.PageDown => "Page Up / Page Down",
        PreviewKey.Home or PreviewKey.End => "Home / End",
        _ => key.ToString(),
    };
}
