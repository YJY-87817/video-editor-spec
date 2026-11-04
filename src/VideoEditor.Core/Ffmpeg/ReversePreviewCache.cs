using System.Globalization;
using VideoEditor.Core.Export;
using VideoEditor.Core.Models;

namespace VideoEditor.Core.Ffmpeg;

/// <summary>
/// 倒放片段的預覽檔。LibVLC 不能倒著播放，所以先用 FFmpeg 產生一個已倒放、縮小的暫存檔，
/// 播放時改播這個檔案。檔案依來源與入出點快取，關閉時刪除。
/// </summary>
public sealed class ReversePreviewCache(FfmpegTools tools, string? directory = null) : IDisposable
{
    private readonly string _directory = directory ?? Path.Combine(Path.GetTempPath(), "VideoEditor", "reverse-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<(string Path, long In, long Out, int Width, int Height), Task<string>> _files = [];
    // 一次只產生一個，避免同時跑多個 FFmpeg 拖慢播放。
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _disposed = new();

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
        var args = new List<string> { "-hide_banner", "-nostats", "-v", "error", "-y" };
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
            await ProcessRunner.RunForBytesAsync(tools.Ffmpeg, BuildArguments(media, sourceIn, sourceOut, size, output), token);
            return output;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static long Ticks(double seconds) => (long)Math.Round(seconds * 1_000_000);

    public void Dispose()
    {
        _disposed.Cancel();
        try { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
