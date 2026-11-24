using System.Globalization;
using VideoEditor.Core.Models;

namespace VideoEditor.Core.Ffmpeg;

public sealed record RawFrame(byte[] Bgra, int Width, int Height);

public sealed class FrameExtractor(FfmpegTools tools, FfmpegTimeouts? timeouts = null)
{
    private FfmpegTimeouts Limits => timeouts ?? FfmpegTimeouts.Default;

    /// <summary>解出指定時間的影格（BGRA）；超出影片結尾時回傳 null。</summary>
    /// <param name="still">靜態圖片：不管 time，直接解出那一張（見 <see cref="SeekTo"/>）。</param>
    public async Task<RawFrame?> ExtractAsync(string path, double time, Size2 size, CancellationToken token = default, bool still = false)
    {
        string[] args =
        [
            "-v", "error", ..SeekTo(time, still), ..FfmpegInput.LocalFilesOnly, "-i", path,
            "-frames:v", "1", "-an", "-vf", $"scale={size.Width}:{size.Height},setsar=1",
            "-f", "rawvideo", "-pix_fmt", "bgra", "pipe:1",
        ];
        var chunks = await ProcessRunner.RunForChunksAsync(tools.Ffmpeg, args, FrameBytes(size), 1, token, Limits.Frame);
        return chunks.Count == 1 ? new RawFrame(chunks[0], size.Width, size.Height) : null;
    }

    /// <summary>
    /// 一次解出多格：從第 firstFrame 格起，每 step 格取一格，最多 count 格（BGRA）。
    /// 倒帶播放時用，比逐格各跑一次 FFmpeg 快得多。回傳的第 i 格是第 firstFrame + i × step 格。
    /// </summary>
    public async Task<IReadOnlyList<RawFrame>> ExtractRangeAsync(string path, long firstFrame, int count, int step, double fps, Size2 size,
        CancellationToken token = default)
    {
        step = Math.Max(1, step);
        string[] args =
        [
            "-v", "error", "-ss", Seconds(FrameMath.FrameSeekTime(firstFrame, fps)), ..FfmpegInput.LocalFilesOnly, "-i", path,
            "-an", "-vf", $"select=not(mod(n\\,{step})),scale={size.Width}:{size.Height},setsar=1",
            "-fps_mode", "passthrough", "-frames:v", count.ToString(CultureInfo.InvariantCulture),
            "-f", "rawvideo", "-pix_fmt", "bgra", "pipe:1",
        ];
        var chunks = await ProcessRunner.RunForChunksAsync(tools.Ffmpeg, args, FrameBytes(size), count, token, Limits.FrameRange);
        return chunks.Select(bytes => new RawFrame(bytes, size.Width, size.Height)).ToList();
    }

    /// <summary>解出 JPEG 縮圖。</summary>
    /// <param name="still">靜態圖片：不管 time，直接解出那一張（見 <see cref="SeekTo"/>）。</param>
    public Task<byte[]> ThumbnailAsync(string path, double time, int width, CancellationToken token = default, bool still = false)
    {
        string[] args =
        [
            "-v", "error", ..SeekTo(time, still), ..FfmpegInput.LocalFilesOnly, "-i", path, "-frames:v", "1", "-an",
            "-vf", $"scale={width}:-2", "-c:v", "mjpeg", "-q:v", "5", "-f", "image2pipe", "pipe:1",
        ];
        return ProcessRunner.RunForBytesAsync(tools.Ffmpeg, args, token, Limits.Thumbnail);
    }

    /// <summary>
    /// 輸入前的定位參數。圖片不定位：FFmpeg 6.1 對 JPEG（image2）加上 -ss 0 會解不出任何畫面。
    /// 影片照舊加 -ss，即使是 0：來源的時間戳不一定從 0 開始，不加的話可能解出 0 秒以前的畫面。
    /// </summary>
    private static string[] SeekTo(double time, bool still) => still ? [] : ["-ss", Seconds(Math.Max(0, time))];

    /// <summary>預覽解析度：長邊不超過 maxLongSide，取偶數。</summary>
    public static Size2 PreviewSize(int width, int height, int maxLongSide)
    {
        var scale = Math.Min(1.0, (double)maxLongSide / Math.Max(width, height));
        return new Size2(Even(width * scale), Even(height * scale));
    }

    /// <summary>一格 BGRA 影格的位元組數。</summary>
    public static int FrameBytes(Size2 size) => size.Width * size.Height * 4;

    private static int Even(double value) => Math.Max(2, (int)Math.Round(value / 2) * 2);
    private static string Seconds(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
}
