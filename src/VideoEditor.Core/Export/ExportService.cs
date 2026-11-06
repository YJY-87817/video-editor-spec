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

        // 輸出檔已存在時先確認能覆蓋：否則要到匯出跑完、改名時才失敗，做好的影片也會被丟掉。
        if (File.Exists(fullPath) && CannotOverwrite(fullPath) is { } reason) return reason;

        var estimate = EstimateBytes(timeline);
        // 網路路徑（\\伺服器\共用資料夾）等查不到剩餘空間的位置就略過這項檢查；能否寫入上面已經確認過。
        return FreeSpace(fullPath) is { } free && free < estimate
            ? $"磁碟空間可能不足：預估需要 {estimate / 1_048_576} MB，剩餘 {free / 1_048_576} MB。"
            : null;
    }

    /// <summary>輸出位置所在磁碟的剩餘空間；查不到（例如 UNC 網路路徑）時回傳 null。</summary>
    public static long? FreeSpace(string fullPath)
    {
        try
        {
            return new DriveInfo(Path.GetPathRoot(fullPath)!).AvailableFreeSpace;
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>已存在的輸出檔無法覆蓋時回傳原因：被其他程式開著，或是唯讀、沒有權限。</summary>
    private static string? CannotOverwrite(string path)
    {
        try
        {
            using var _ = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return "輸出檔是唯讀的，或沒有權限覆蓋。請換一個檔名或位置。";
        }
        catch (IOException)
        {
            return "輸出檔正被其他程式使用（例如播放器），請先關閉它，或換一個檔名。";
        }
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
