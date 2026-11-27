namespace VideoEditor.Core.Models;

/// <summary>狀態列訊息的種類，決定圖示、顏色與留多久。</summary>
public enum StatusKind
{
    Info,
    Success,
    Warning,
    Error,
}

/// <summary>狀態列的訊息要留多久（UIUX-PLAN-2 決定 9）。</summary>
public static class StatusPolicy
{
    public static readonly TimeSpan ShortLived = TimeSpan.FromSeconds(8);

    /// <summary>
    /// 一般與完成訊息 8 秒後消失；警告與錯誤要讓人看到，留到下一則訊息；
    /// 附帶按鈕（例如「開啟資料夾」）或標明要留著（例如還在進行中的工作）的也留著。回傳 null 表示不自動消失。
    /// </summary>
    public static TimeSpan? Lifetime(StatusKind kind, bool hasAction = false, bool keep = false) =>
        keep || hasAction || kind is StatusKind.Warning or StatusKind.Error ? null : ShortLived;
}
