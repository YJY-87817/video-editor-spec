using System.Globalization;
using VideoEditor.Core.Models;

namespace VideoEditor.Core.Ffmpeg;

public sealed record RawFrame(byte[] Bgra, int Width, int Height);

public sealed class FrameExtractor(FfmpegTools tools)
{
    /// <summary>解出指定時間的影格（BGRA）；超出影片結尾時回傳 null。</summary>
    public async Task<RawFrame?> ExtractAsync(string path, double time, Size2 size, CancellationToken token = default)
    {
        string[] args =
        [
            "-v", "error", "-ss", Seconds(Math.Max(0, time)), "-i", path,
            "-frames:v", "1", "-an", "-vf", $"scale={size.Width}:{size.Height},setsar=1",
            "-f", "rawvideo", "-pix_fmt", "bgra", "pipe:1",
        ];
        var bytes = await ProcessRunner.RunForBytesAsync(tools.Ffmpeg, args, token);
        return bytes.Length == size.Width * size.Height * 4 ? new RawFrame(bytes, size.Width, size.Height) : null;
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
            "-v", "error", "-ss", Seconds(FrameSeekTime(firstFrame, fps)), "-i", path,
            "-an", "-vf", $"select=not(mod(n\\,{step})),scale={size.Width}:{size.Height},setsar=1",
            "-fps_mode", "passthrough", "-frames:v", count.ToString(CultureInfo.InvariantCulture),
            "-f", "rawvideo", "-pix_fmt", "bgra", "pipe:1",
        ];
        var bytes = await ProcessRunner.RunForBytesAsync(tools.Ffmpeg, args, token);
        var frameSize = size.Width * size.Height * 4;
        var frames = new List<RawFrame>();
        for (var offset = 0; offset + frameSize <= bytes.Length; offset += frameSize)
            frames.Add(new RawFrame(bytes.AsSpan(offset, frameSize).ToArray(), size.Width, size.Height));
        return frames;
    }

    /// <summary>解出 JPEG 縮圖。</summary>
    public Task<byte[]> ThumbnailAsync(string path, double time, int width, CancellationToken token = default)
    {
        string[] args =
        [
            "-v", "error", "-ss", Seconds(time), "-i", path, "-frames:v", "1", "-an",
            "-vf", $"scale={width}:-2", "-c:v", "mjpeg", "-q:v", "5", "-f", "image2pipe", "pipe:1",
        ];
        return ProcessRunner.RunForBytesAsync(tools.Ffmpeg, args, token);
    }

    /// <summary>預覽解析度：長邊不超過 maxLongSide，取偶數。</summary>
    public static Size2 PreviewSize(int width, int height, int maxLongSide)
    {
        var scale = Math.Min(1.0, (double)maxLongSide / Math.Max(width, height));
        return new Size2(Even(width * scale), Even(height * scale));
    }

    /// <summary>第 n 格的定位時間。略早於該格時間戳，避免捨入後落到下一格。</summary>
    public static double FrameSeekTime(long frame, double fps) => Math.Max(0, (frame - 0.25) / fps);

    /// <summary>時間所在的影格編號。</summary>
    public static long FrameIndex(double time, double fps) => (long)Math.Floor(time * fps + 0.5);

    private static int Even(double value) => Math.Max(2, (int)Math.Round(value / 2) * 2);
    private static string Seconds(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
}
