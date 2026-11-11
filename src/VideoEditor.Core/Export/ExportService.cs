using System.Globalization;
using VideoEditor.Core.Ffmpeg;
using TimelineModel = VideoEditor.Core.Timeline.Timeline;

namespace VideoEditor.Core.Export;

public sealed class ExportService(FfmpegTools tools)
{
    /// <summary>估算 CRF 輸出大小的安全係數；實際大小取決於畫面內容。</summary>
    private const double SizeSafetyFactor = 1.5;

    /// <summary>
    /// 重新編碼後每個像素大約用掉的位元數（CRF 18、一般實拍畫面的粗估）。
    /// H.265 同畫質較省，但這裡用它的都是 10-bit 以上或 HDR 的來源，抓得保守一些。
    /// </summary>
    private const double H264BitsPerPixel = 0.2;
    private const double H265BitsPerPixel = 0.15;
    private const double DefaultFrameRate = 30;

    /// <summary>匯出前檢查：路徑可寫入、不覆蓋來源、磁碟空間足夠。回傳錯誤訊息，通過時為 null。</summary>
    /// <param name="settings">輸出設定，用來依輸出解析度估算檔案大小；沒有給時只依來源位元率估算。</param>
    public static string? Preflight(TimelineModel timeline, string outputPath, Models.OutputSettings? settings = null)
    {
        var fullPath = Path.GetFullPath(outputPath);
        // 素材在匯入後被移動、刪除，或放在已拔掉的磁碟上：先說清楚，不要等 FFmpeg 失敗才丟出它的原始錯誤。
        var missing = timeline.AllClips.Select(c => c.Media.Path).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(path => !File.Exists(path)).ToList();
        if (missing.Count > 0)
            return $"找不到素材檔案：\n{string.Join('\n', missing.Select(path => "　" + path))}\n\n請確認檔案還在原位置（外接磁碟是否已連接），或把它從時間軸移除後重新匯入。";

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

        var estimate = EstimateBytes(timeline, settings);
        // 網路路徑（\\伺服器\共用資料夾）等查不到剩餘空間的位置就略過這項檢查；能否寫入上面已經確認過。
        return FreeSpace(fullPath) is { } free && free < estimate
            ? $"磁碟空間可能不足：粗估需要 {estimate / 1_048_576} MB（實際大小依畫面內容而定），剩餘 {free / 1_048_576} MB。"
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

    /// <summary>
    /// 輸出檔的預估大小，加上倒放片段匯出期間暫存的中間檔（與輸出檔放在同一個磁碟）。
    /// 輸出檔取兩種估法中較大的：來源位元率 × 安全係數，以及依輸出解析度與影格率算出的重新編碼大小。
    /// 只看來源位元率會低估：來源是高壓縮的 HEVC，或填滿裁切把畫面放大時，CRF 18 重新編碼可能大上好幾倍。
    /// </summary>
    public static long EstimateBytes(TimelineModel timeline, Models.OutputSettings? settings = null)
    {
        var fromSource = timeline.AllClips.Sum(c => c.Media.BitRate / 8.0 * c.Duration) * SizeSafetyFactor;
        var intermediate = timeline.VideoClips.Where(c => c.Reversed).Sum(c => ReverseGraph.IntermediateBytes(c.Media, c.SourceIn, c.SourceOut));
        return (long)Math.Max(fromSource, settings == null ? 0 : EncodedBytes(timeline, settings)) + intermediate;
    }

    /// <summary>依輸出解析度、影格率與編碼格式估算重新編碼後的大小（含聲音）。基準與 ExportPlanner 相同：第一個影片片段。</summary>
    private static double EncodedBytes(TimelineModel timeline, Models.OutputSettings settings)
    {
        if (timeline.VideoClips.Count == 0) return 0;
        var first = timeline.VideoClips[0];
        var size = Models.OutputSize.Compute(first.DisplaySize.Width, first.DisplaySize.Height, settings.Ratio);
        var fps = first.Media.FrameRate.Value > 0 ? first.Media.FrameRate.Value : DefaultFrameRate;
        var bitsPerPixel = ExportPlanner.ChooseVideoFormat(first.Media).Codec == "libx265" ? H265BitsPerPixel : H264BitsPerPixel;
        var video = (double)size.Width * size.Height * fps * bitsPerPixel / 8;
        var audio = timeline.AllClips.Any(c => c.Media.HasAudio) ? ExportPlanner.AudioBitRate(timeline) / 8.0 : 0;
        return (video + audio) * timeline.Duration;
    }

    /// <summary>
    /// 匯出時先寫入的暫存檔，與輸出檔放在同一個資料夾（改名才不必跨磁碟複製）。
    /// 檔名含隨機碼，一定不會與現有檔案同名：固定檔名可能剛好是使用者自己的檔案、甚至是時間軸上的素材，
    /// 會被 FFmpeg 覆蓋，結束時又被刪掉。
    /// </summary>
    public static string TemporaryPath(string outputPath)
    {
        var fullPath = Path.GetFullPath(outputPath);
        var directory = Path.GetDirectoryName(fullPath)!;
        while (true)
        {
            var candidate = Path.Combine(directory,
                $"{Path.GetFileNameWithoutExtension(fullPath)}.{Guid.NewGuid().ToString("N")[..8]}.part{Path.GetExtension(fullPath)}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    /// <summary>倒放中間檔的暫存資料夾，放在暫存檔旁邊，名稱取自暫存檔所以同樣不會與現有檔案同名。</summary>
    public static string WorkDirectory(string temporaryPath) => temporaryPath + ".reverse";

    /// <summary>
    /// 有倒放片段時先逐段產生中間檔（放在輸出檔旁的暫存資料夾，結束時一定刪除），再編碼成輸出檔。
    /// 先寫入暫存檔，成功才改名；取消或 FFmpeg 失敗時清掉暫存檔，不留下殘缺影片。
    /// 已經做好、只是改名失敗（例如匯出途中用播放器打開了舊的輸出檔）時保留暫存檔，丟出 <see cref="ExportRenameException"/>。
    /// </summary>
    public async Task ExportAsync(ExportPlan plan, string outputPath, IProgress<double>? progress, CancellationToken token = default)
    {
        var temporary = TemporaryPath(outputPath);
        var work = WorkDirectory(temporary);
        var args = ReverseGraph.Resolve(plan.Arguments.Take(plan.Arguments.Count - 1), work).Append(temporary);
        var finished = false;
        // 進度依處理的秒數計：先是各個倒放片段，最後才是編碼。
        var total = plan.TotalWork;
        var prepared = 0.0;

        try
        {
            if (plan.ReverseJobs.Count > 0) Directory.CreateDirectory(work);
            foreach (var job in plan.ReverseJobs)
            {
                var before = prepared;
                await ReverseGraph.RunAsync(tools, job, work, seconds => progress?.Report(Math.Clamp((before + seconds) / total, 0, 1)), token);
                prepared = before + job.Duration;
            }
            await ProcessRunner.RunWithLinesAsync(tools.Ffmpeg, args, line =>
            {
                if (ParseProgress(line, plan.Duration) is { } value) progress?.Report((prepared + value * plan.Duration) / total);
            }, token);
            finished = true;
            try
            {
                File.Move(temporary, outputPath, overwrite: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                throw new ExportRenameException(temporary, outputPath, e);
            }
            progress?.Report(1);
        }
        finally
        {
            if (!finished) DeleteQuietly(temporary);
            if (plan.ReverseJobs.Count > 0) TemporaryFiles.DeleteDirectory(work);
        }
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        // 刪不掉（例如防毒軟體還在掃描）時不要蓋掉原本的錯誤；暫存檔名明顯，使用者可以自己刪。
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    public static double? ParseProgress(string line, double duration)
    {
        const string key = "out_time_us=";
        if (!line.StartsWith(key, StringComparison.Ordinal) || duration <= 0) return null;
        if (!long.TryParse(line.AsSpan(key.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var us)) return null;
        return Math.Clamp(us / 1_000_000.0 / duration, 0, 1);
    }
}

/// <summary>影片已經做好，但無法改成輸出檔名。做好的影片保留在 <see cref="TemporaryPath"/>。</summary>
public sealed class ExportRenameException(string temporaryPath, string outputPath, Exception inner)
    : IOException($"影片已做好，但無法存成「{Path.GetFileName(outputPath)}」（可能正被播放器等程式開著）：{inner.Message}\n\n"
        + $"做好的影片保留在：\n{temporaryPath}\n\n請關閉開著它的程式後，自行把它改名，或重新匯出。", inner)
{
    public string TemporaryPath { get; } = temporaryPath;
}
