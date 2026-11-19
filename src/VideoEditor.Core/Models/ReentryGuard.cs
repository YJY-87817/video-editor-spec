namespace VideoEditor.Core.Models;

/// <summary>
/// 程式自己改控制項的值時，擋掉它觸發的事件處理器（避免 值改變 → 事件 → 又改值 的循環）。
/// 用 <c>using</c> 包住修改的程式碼：不論中間有沒有丟例外，離開區塊時都會解除，不會讓事件處理器永遠失效（W-6）。
/// <code>
/// using (_sliderGuard.Enter()) { Slider.Value = x; }
/// if (_sliderGuard.Active) return; // 事件處理器開頭
/// </code>
/// 只在單一執行緒（UI 執行緒）使用；巢狀進入時，內層離開就解除（與原本的旗標行為相同）。
/// </summary>
public sealed class ReentryGuard
{
    /// <summary>目前是否在程式自己修改控制項的區塊內。</summary>
    public bool Active { get; private set; }

    public Scope Enter()
    {
        Active = true;
        return new Scope(this);
    }

    public readonly struct Scope(ReentryGuard guard) : IDisposable
    {
        public void Dispose() => guard.Active = false;
    }
}
