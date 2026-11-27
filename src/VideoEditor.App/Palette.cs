using System.Windows.Media;

namespace VideoEditor.App;

/// <summary>
/// XAML 與程式碼共用的顏色，只在這裡寫一次（W-3）。XAML 以 <c>{x:Static local:Palette.Accent}</c> 取用。
/// 只放跨檔案重複出現的顏色；只有單一控制項用到的（例如時間軸的片段底色）留在該控制項裡。
/// </summary>
public static class Palette
{
    /// <summary>強調色（黃）：選取、入出點標記、畫面位置框。</summary>
    public static readonly Brush Accent = Frozen(0xFF, 0xD2, 0x4A);

    /// <summary>來源預覽的藍：預覽外框與進度面板。</summary>
    public static readonly Brush SourceBlue = Frozen(0x4A, 0x90, 0xD9);

    /// <summary>次要文字（說明、狀態列的一般訊息）。</summary>
    public static readonly Brush TextSecondary = Frozen(0xB0, 0xB0, 0xB0);

    /// <summary>較不重要的說明文字。在 #1E1E1E 背景上對比約 6:1，12 號字也達到 WCAG AA。</summary>
    public static readonly Brush TextDim = Frozen(0x9A, 0x9A, 0x9A);

    /// <summary>狀態列：完成。</summary>
    public static readonly Brush StatusSuccess = Frozen(0x7F, 0xE0, 0x9A);

    /// <summary>狀態列：警告（做不到、被擋下的操作）。</summary>
    public static readonly Brush StatusWarning = Frozen(0xF0, 0xC0, 0x5A);

    /// <summary>狀態列：錯誤。</summary>
    public static readonly Brush StatusError = Frozen(0xFF, 0x7A, 0x7A);

    public static Brush Frozen(byte r, byte g, byte b) => Frozen(0xFF, r, g, b);

    public static Brush Frozen(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }
}
