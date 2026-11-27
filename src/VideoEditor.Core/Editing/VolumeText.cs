using System.Globalization;
using System.Text;

namespace VideoEditor.Core.Editing;

/// <summary>音量的百分比與分貝文字，以及輸入框的解析（UIUX-PLAN #13）。</summary>
public static class VolumeText
{
    public const double MaxPercent = 200;

    /// <summary>百分比換成分貝：100% 是 0 dB，200% 約 +6 dB，0% 是靜音（沒有分貝值）。</summary>
    public static double? Decibels(double percent) => percent <= 0 ? null : 20 * Math.Log10(percent / 100);

    /// <summary>輸入框旁邊的說明：「靜音」「原音量」「+3.5 dB」「-6.0 dB」。</summary>
    public static string Describe(double percent)
    {
        if (Decibels(Math.Round(percent)) is not { } db) return "靜音";
        if (Math.Abs(db) < 0.05) return "原音量";
        return (db > 0 ? "+" : "") + db.ToString("0.0", CultureInfo.InvariantCulture) + " dB";
    }

    /// <summary>
    /// 解析輸入的百分比：<c>150</c>、<c>150%</c>、<c>72.5</c>（四捨五入成整數），全形數字也可以。
    /// 不是數字或超出 0～200 時回傳 null。
    /// </summary>
    public static double? ParsePercent(string? text)
    {
        var input = (text ?? "").Normalize(NormalizationForm.FormKC).Trim().TrimEnd('%').Trim();
        if (!double.TryParse(input, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)) return null;
        value = Math.Round(value, MidpointRounding.AwayFromZero);
        return value is >= 0 and <= MaxPercent ? value : null;
    }
}
