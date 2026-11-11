using System.Text;

namespace VideoEditor.Core.Ffmpeg;

/// <summary>
/// 這份 FFmpeg 內含哪些編碼器。FFmpeg 的組建各不相同，缺少 libx264／libx265 的並不少見；
/// 不先檢查的話要到匯出途中才失敗，使用者看到的是 FFmpeg 的原始錯誤訊息。
/// </summary>
public sealed class FfmpegEncoders(FfmpegTools tools)
{
    private Task<IReadOnlySet<string>>? _available;

    /// <summary>查詢一次後記住結果；查詢失敗時下次重查。</summary>
    public Task<IReadOnlySet<string>> GetAsync()
    {
        if (_available is { IsFaulted: false, IsCanceled: false }) return _available;
        return _available = QueryAsync();
    }

    private async Task<IReadOnlySet<string>> QueryAsync() =>
        Parse(Encoding.UTF8.GetString(await ProcessRunner.RunForBytesAsync(tools.Ffmpeg, ["-hide_banner", "-encoders"])));

    /// <summary>解析 <c>ffmpeg -encoders</c> 的輸出：分隔線之後每行是「旗標 名稱 說明」。</summary>
    public static IReadOnlySet<string> Parse(string output)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var listing = false;
        foreach (var line in output.Split('\n'))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (!listing)
            {
                listing = parts.Length == 1 && parts[0].All(c => c == '-');
                continue;
            }
            if (parts.Length >= 2) names.Add(parts[1]);
        }
        return names;
    }

    /// <summary>一組 FFmpeg 指令用到的編碼器（-c:v 與 -c:a 之後的名稱）。</summary>
    public static IReadOnlyList<string> Required(IEnumerable<IReadOnlyList<string>> commands) =>
        commands.SelectMany(args => args.Zip(args.Skip(1)).Where(pair => pair.First is "-c:v" or "-c:a").Select(pair => pair.Second))
            .Distinct().ToList();

    /// <summary>缺少必要的編碼器時回傳給使用者看的說明；都有時回傳 null。</summary>
    public static string? MissingMessage(IEnumerable<string> required, IReadOnlySet<string> available)
    {
        var missing = required.Where(name => !available.Contains(name)).ToList();
        return missing.Count == 0
            ? null
            : $"這份 FFmpeg 缺少匯出需要的編碼器：{string.Join("、", missing)}。\n\n"
                + "請換成含 libx264 與 libx265 的 FFmpeg（一般稱為 full 或 GPL 版），放到程式目錄的 tools\\ffmpeg，或加入 PATH。";
    }
}
