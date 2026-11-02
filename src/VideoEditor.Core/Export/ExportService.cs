using System.Globalization;
using VideoEditor.Core.Ffmpeg;
using TimelineModel = VideoEditor.Core.Timeline.Timeline;

namespace VideoEditor.Core.Export;

public sealed class ExportService(FfmpegTools tools)
{
    /// <summary>估算 CRF 輸出大小的安全係數；實際大小取決於畫面內容。</summary>
    private const double SizeSafetyFactor = 1.5;

    /// <summary>匯出前檢查：路徑可寫入、不覆蓋來源、磁碟空間足夠。回傳錯誤訊息，通過時為 null。</summary>
    public static string? Preflight(TimelineModel timeline, string outputPath)
    {
        var fullPath = Path.GetFullPath(outputPath);
        if (timeline.AllClips.Any(c => string.Equals(Path.GetFullPath(c.Media.Path), fullPath, StringComparison.OrdinalIgnoreCase)))
            return "輸出檔不能與來源素材相同。";

        var directory = Path.GetDirectoryName(fullPath)!;
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, $".write-test-{Guid.NewGuid():N}");
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"無法寫入輸出位置：{e.Message}";
        }

        var estimate = EstimateBytes(timeline);
        var free = new DriveInfo(Path.GetPathRoot(fullPath)!).AvailableFreeSpace;
        return free < estimate
            ? $"磁碟空間可能不足：預估需要 {estimate / 1_048_576} MB，剩餘 {free / 1_048_576} MB。"
            : null;
    }

    public static long EstimateBytes(TimelineModel timeline) =>
        (long)(timeline.AllClips.Sum(c => c.Media.BitRate / 8.0 * c.Duration) * SizeSafetyFactor);

    /// <summary>先寫入暫存檔，成功才改名；取消或失敗時清掉暫存檔，不留下殘缺影片。</summary>
    public async Task ExportAsync(ExportPlan plan, string outputPath, IProgress<double>? progress, CancellationToken token = default)
    {
        var temporary = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outputPath))!,
            $"{Path.GetFileNameWithoutExtension(outputPath)}.part{Path.GetExtension(outputPath)}");
        var args = plan.Arguments.Take(plan.Arguments.Count - 1).Append(temporary);

        try
        {
            await ProcessRunner.RunWithLinesAsync(tools.Ffmpeg, args, line =>
            {
                if (ParseProgress(line, plan.Duration) is { } value) progress?.Report(value);
            }, token);
            File.Move(temporary, outputPath, overwrite: true);
            progress?.Report(1);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public static double? ParseProgress(string line, double duration)
    {
        const string key = "out_time_us=";
        if (!line.StartsWith(key, StringComparison.Ordinal) || duration <= 0) return null;
        if (!long.TryParse(line.AsSpan(key.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var us)) return null;
        return Math.Clamp(us / 1_000_000.0 / duration, 0, 1);
    }
}
