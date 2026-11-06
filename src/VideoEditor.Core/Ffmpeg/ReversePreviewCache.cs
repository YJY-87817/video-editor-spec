using System.Globalization;
using VideoEditor.Core.Export;
using VideoEditor.Core.Models;

namespace VideoEditor.Core.Ffmpeg;

/// <summary>
/// 倒放片段的預覽檔。LibVLC 不能倒著播放，所以先用 FFmpeg 產生一個已倒放、縮小的暫存檔，
/// 播放時改播這個檔案。檔案依來源與入出點快取，關閉時刪除。
/// </summary>
/// <param name="progress">產生中回報 0–1；產生結束（成功或失敗）時回報 null。</param>
public sealed class ReversePreviewCache(FfmpegTools tools, string? directory = null, IProgress<double?>? progress = null) : IDisposable
{
    private readonly string _directory = directory ?? Path.Combine(Path.GetTempPath(), "VideoEditor", "reverse-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<(string Path, long In, long Out, int Width, int Height), Task<string>> _files = [];
    // 一次只產生一個，避免同時跑多個 FFmpeg 拖慢播放。
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _disposed = new();
    // 關閉時 FFmpeg 剛被停止，可能還佔用預覽檔一下子；刪不掉就稍等再試。
    private const int DeleteAttempts = 20;
    private const int DeleteRetryDelayMs = 100;

    /// <summary>取得（必要時開始產生）預覽檔。檔案從片段第一格（來源的 sourceOut 前一格）開始，長度等於片段。</summary>
    public Task<string> GetAsync(MediaInfo media, double sourceIn, double sourceOut, Size2 size)
    {
        var key = (media.Path, Ticks(sourceIn), Ticks(sourceOut), size.Width, size.Height);
        if (_files.TryGetValue(key, out var existing) && !existing.IsFaulted && !existing.IsCanceled) return existing;
        var task = RenderAsync(media, sourceIn, sourceOut, size, _disposed.Token);
        _files[key] = task;
        return task;
    }

    public static IReadOnlyList<string> BuildArguments(MediaInfo media, double sourceIn, double sourceOut, Size2 size, string output)
    {
        var args = new List<string> { "-hide_banner", "-nostats", "-v", "error", "-progress", "pipe:1", "-y" };
        var graph = new List<string>();
        var input = 0;
        var frameBytes = ReverseGraph.FrameBytes(media, size.Width, size.Height);
        // 先縮小再倒放，記憶體用量依預覽尺寸計算。
        ReverseGraph.Add(args, graph, ref input, "r", media, sourceIn, sourceOut, media.HasAudio,
            $"scale={size.Width}:{size.Height},setsar=1", frameBytes);
        args.AddRange(["-filter_complex", string.Join(';', graph), "-map", "[rv]"]);
        if (media.HasAudio) args.AddRange(["-map", "[ra]", "-c:a", "aac", "-b:a", "160k"]);
        // 短 GOP：播放時常從片段中間開始，定位才快。
        args.AddRange(["-c:v", "libx264", "-preset", "ultrafast", "-crf", "23", "-g", "10", "-pix_fmt", "yuv420p",
            "-t", (sourceOut - sourceIn).ToString("0.######", CultureInfo.InvariantCulture), output]);
        return args;
    }

    private async Task<string> RenderAsync(MediaInfo media, double sourceIn, double sourceOut, Size2 size, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            Directory.CreateDirectory(_directory);
            var output = Path.Combine(_directory, $"{Guid.NewGuid():N}.mp4");
            var duration = sourceOut - sourceIn;
            progress?.Report(0);
            await ProcessRunner.RunWithLinesAsync(tools.Ffmpeg, BuildArguments(media, sourceIn, sourceOut, size, output), line =>
            {
                if (ExportService.ParseProgress(line, duration) is { } value) progress?.Report(value);
            }, token);
            return output;
        }
        finally
        {
            progress?.Report(null);
            _gate.Release();
        }
    }

    private static long Ticks(double seconds) => (long)Math.Round(seconds * 1_000_000);

    /// <summary>停止產生中的預覽檔並刪除暫存資料夾。檔案仍被佔用時會重試，最多約 2 秒。</summary>
    public void Dispose()
    {
        _disposed.Cancel(); // 會立刻終止產生中的 FFmpeg
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // 仍刪不掉就放棄：只是暫存目錄裡留下一些檔案，不影響程式關閉。
                if (attempt >= DeleteAttempts) return;
                Thread.Sleep(DeleteRetryDelayMs);
            }
        }
    }
}
