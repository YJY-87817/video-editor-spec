using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text;
using VideoEditor.Core.Ffmpeg;
using VideoEditor.Core.Logging;

namespace VideoEditor.Core.Export;

/// <summary>
/// 把匯出的濾鏡圖從命令列搬到檔案。濾鏡圖約佔每個片段命令列長度的四分之三，片段多時整條命令列會超過
/// Windows 的長度上限；改由檔案讀取後，上限從約 80 個片段提高到約 300 個（其餘是每個片段的輸入參數）。
/// 讀檔的選項因 FFmpeg 版本而異：7.0 起是 <c>-/filter_complex 檔案</c>，舊版是 <c>-filter_complex_script 檔案</c>
/// （新版對舊選項的支援沒有保證），所以不比對版本號，直接試跑一個極小的濾鏡圖看哪個語法能用。
/// </summary>
public static class FilterGraphFile
{
    public const string NewOption = "-/filter_complex";
    public const string LegacyOption = "-filter_complex_script";

    private const string FilterOption = "-filter_complex";
    private const string ProbeGraph = "nullsrc=s=16x16:d=0.1[v]";

    // 同一份 FFmpeg 只探測一次。
    private static readonly ConcurrentDictionary<string, Task<string>> Probed = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>命令列的估計長度（每個參數加引號與空白約 3 個字元）。</summary>
    public static int CommandLength(IEnumerable<string> arguments) => arguments.Sum(argument => argument.Length + 3);

    /// <summary>命令列超過 threshold 字元，而且有可以搬出去的濾鏡圖時，才值得改用檔案。</summary>
    public static bool ShouldUseFile(IReadOnlyList<string> arguments, int threshold) =>
        arguments.Contains(FilterOption) && CommandLength(arguments) > threshold;

    /// <summary>
    /// 這份 FFmpeg 讀濾鏡圖檔案要用的選項：優先用新語法，不支援就用舊語法。
    /// 探測本身出錯（例如 FFmpeg 無法啟動）時回傳舊語法，之後真正的匯出會顯示實際的錯誤。
    /// </summary>
    public static Task<string> OptionAsync(FfmpegTools tools) => Probed.GetOrAdd(tools.Ffmpeg, _ => ProbeAsync(tools));

    private static async Task<string> ProbeAsync(FfmpegTools tools)
    {
        var file = Path.Combine(Path.GetTempPath(), $"VideoEditor-probe-{Guid.NewGuid():N}.txt");
        try
        {
            await File.WriteAllTextAsync(file, ProbeGraph, new UTF8Encoding(false)).ConfigureAwait(false);
            await ProcessRunner.RunForBytesAsync(tools.Ffmpeg,
                ["-hide_banner", "-v", "error", NewOption, file, "-map", "[v]", "-f", "null", "-"],
                timeout: FfmpegTimeouts.Default.EncoderList, logFailure: false).ConfigureAwait(false);
            Log.Info($"濾鏡圖檔案：使用 {NewOption}");
            return NewOption;
        }
        catch (Exception e) when (e is FfmpegException or Win32Exception or IOException or UnauthorizedAccessException)
        {
            Log.Info($"濾鏡圖檔案：{NewOption} 不能用，改用 {LegacyOption}");
            return LegacyOption;
        }
        finally
        {
            try { File.Delete(file); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// 把「-filter_complex 濾鏡圖」換成「option 檔案路徑」，回傳新的參數與要寫進檔案的濾鏡圖。
    /// 沒有 -filter_complex 或它後面沒有值時丟出 <see cref="InvalidOperationException"/>。
    /// </summary>
    public static (IReadOnlyList<string> Arguments, string Graph) Move(IReadOnlyList<string> arguments, string option, string path)
    {
        var index = arguments.ToList().IndexOf(FilterOption);
        if (index < 0 || index + 1 >= arguments.Count) throw new InvalidOperationException("參數裡沒有濾鏡圖。");
        var result = arguments.ToList();
        result[index] = option;
        result[index + 1] = path;
        return (result, arguments[index + 1]);
    }
}
