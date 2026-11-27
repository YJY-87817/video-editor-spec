using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace VideoEditor.Core.Playback;

/// <summary>時間的顯示方式（設定檔記住使用者的選擇）。</summary>
public enum TimeDisplay
{
    /// <summary>時:分:秒:格，例如 00:01:05:12。</summary>
    Timecode,
    /// <summary>時:分:秒.百分秒，例如 00:01:05.40。</summary>
    Decimal,
}

/// <summary>解析使用者輸入的時間：成功時 <see cref="Seconds"/> 有值，失敗時 <see cref="Error"/> 是給人看的說明。</summary>
public sealed record TimeInput(double? Seconds, string? Error);

/// <summary>
/// 時間碼的顯示與輸入（UIUX-PLAN #14、#22）。格是「這一秒裡的第幾格」，時分秒照實際時間走，
/// 所以 29.97 fps 時每秒是 0～29 格，不會和實際時間越差越多（不做 drop-frame）。
/// 沒有影格率（圖片）時以 30 fps 計。
/// </summary>
public static partial class Timecode
{
    public const double FallbackFps = 30;
    private const double Epsilon = 1e-6;

    public static double EffectiveFps(double fps) => fps > 0 && double.IsFinite(fps) ? fps : FallbackFps;

    /// <summary>一秒最多幾格（29.97 → 30），格數要小於它。</summary>
    public static int FramesPerSecond(double fps) => (int)Math.Ceiling(EffectiveFps(fps) - Epsilon);

    /// <summary>時:分:秒:格。負值視為 0。</summary>
    public static string Format(double seconds, double fps)
    {
        fps = EffectiveFps(fps);
        seconds = Math.Max(0, seconds);
        var whole = Math.Floor(seconds + Epsilon);
        var frame = (int)Math.Floor((seconds - whole) * fps + Epsilon);
        frame = Math.Clamp(frame, 0, FramesPerSecond(fps) - 1);
        var total = (long)whole;
        return $"{total / 3600:00}:{total / 60 % 60:00}:{total % 60:00}:{frame:00}";
    }

    /// <summary>
    /// 對齊到最近的一格（和 <c>InOutPoints.Snap</c> 相同，四捨五入）。輸入的時間碼在非整數影格率（29.97）時
    /// 會落在兩格之間，往下取會跳到前一格，顯示成和輸入不同的時間碼。
    /// </summary>
    public static double AlignToFrame(double seconds, double fps)
    {
        fps = EffectiveFps(fps);
        return Math.Floor(seconds * fps + 0.5) / fps;
    }

    /// <summary>依設定的顯示方式格式化。</summary>
    public static string Display(double seconds, double fps, TimeDisplay mode) =>
        mode == TimeDisplay.Timecode ? Format(seconds, fps) : PlaybackText.Duration(seconds);

    /// <summary>
    /// 解析輸入的時間。接受（全形的數字與符號也可以）：
    /// <list type="bullet">
    /// <item>秒：<c>65</c>、<c>65.5</c></item>
    /// <item>分:秒、時:分:秒：<c>1:05</c>、<c>1:05.5</c>、<c>1:02:03</c></item>
    /// <item>時:分:秒:格：<c>00:01:05:12</c></item>
    /// <item>格數：<c>120f</c></item>
    /// <item>相對於目前位置：前面加 <c>+</c> 或 <c>-</c>，例如 <c>+10</c>、<c>-2.5</c>、<c>+12f</c>、<c>-1:00</c></item>
    /// </list>
    /// 回傳的秒數不限制範圍（可能小於 0 或超過結尾），由呼叫端決定怎麼處理。
    /// </summary>
    public static TimeInput Parse(string? text, double fps, double current)
    {
        fps = EffectiveFps(fps);
        var input = (text ?? "").Normalize(NormalizationForm.FormKC).Trim().Replace(" ", "");
        // 全形的「：」「．」經 NFKC 會變成半形；中文輸入法常打出的頓號、句號也當成分隔。
        input = input.Replace('。', '.').Replace('、', ':');
        if (input.Length == 0) return Fail("請輸入時間。");

        var sign = 0;
        if (input[0] is '+' or '-')
        {
            sign = input[0] == '+' ? 1 : -1;
            input = input[1..];
            if (input.Length == 0) return Fail("「+」「-」後面要接時間。");
        }

        var parsed = FramesPattern().Match(input) is { Success: true } frames
            ? new TimeInput(long.Parse(frames.Groups[1].Value, CultureInfo.InvariantCulture) / fps, null)
            : ParseClock(input, fps);
        if (parsed.Seconds is not { } value) return parsed;
        return new TimeInput(sign == 0 ? value : current + sign * value, null);
    }

    public const string Help = "看不懂這個時間。可以輸入 65、1:05、1:05.5、00:01:05:12、120f，或用 +10、-2.5、+12f 從目前位置前後移動。";

    private static TimeInput Fail(string message) => new(null, message);

    /// <summary>秒、分:秒、時:分:秒、時:分:秒:格。</summary>
    private static TimeInput ParseClock(string input, double fps)
    {
        var parts = input.Split(':');
        if (parts.Length > 4 || parts.Any(p => p.Length == 0)) return Fail(Help);

        var frames = 0;
        if (parts.Length == 4)
        {
            if (!int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out frames)) return Fail(Help);
            if (frames >= FramesPerSecond(fps))
                return Fail($"格數要小於 {FramesPerSecond(fps)}（每秒 {fps.ToString("0.###", CultureInfo.InvariantCulture)} 格）。");
            parts = parts[..3];
        }

        // 只有最後一段（秒）可以有小數；有格數時秒不能有小數。
        var last = parts[^1];
        if (!double.TryParse(last, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var seconds)) return Fail(Help);
        if (input.Split(':').Length == 4 && last.Contains('.')) return Fail(Help);
        // 前面有更大的單位時，秒、分要小於 60（1:75 多半是打錯）。
        if (parts.Length >= 2 && seconds >= 60) return Fail("秒數要小於 60。");

        var total = seconds;
        var scale = 60.0;
        for (var i = parts.Length - 2; i >= 0; i--, scale *= 60)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out var unit)) return Fail(Help);
            if (parts.Length == 3 && i == 1 && unit >= 60) return Fail("分鐘要小於 60。");
            total += unit * scale;
        }
        return new TimeInput(total + frames / fps, null);
    }

    [GeneratedRegex(@"^(\d+)[fF]$")]
    private static partial Regex FramesPattern();
}
