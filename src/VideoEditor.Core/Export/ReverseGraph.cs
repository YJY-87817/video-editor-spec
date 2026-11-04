using System.Globalization;
using VideoEditor.Core.Models;

namespace VideoEditor.Core.Export;

/// <summary>
/// 產生倒放用的 FFmpeg 輸入與濾鏡。
/// reverse / areverse 濾鏡要把整段影格留在記憶體裡才能倒著輸出，長片段會吃光記憶體，
/// 所以把片段切成小段：每段各自倒放，再由後往前接起來。
/// </summary>
public static class ReverseGraph
{
    /// <summary>每一小段解碼後的影格最多占用的記憶體。</summary>
    public const long DefaultBufferBytes = 512L * 1024 * 1024;
    private const double DefaultFrameRate = 30;

    public sealed record Chunk(double Start, double Length);

    /// <summary>
    /// 把 [sourceIn, sourceOut) 切成小段，依來源順序回傳。
    /// 以影格為單位切：包含時間戳在 [sourceIn, sourceOut) 內的每一格，所有切點都放在兩格之間，
    /// 捨入誤差才不會讓影格重複、遺漏，或多出出點之後的那一格。
    /// </summary>
    public static IReadOnlyList<Chunk> Split(double sourceIn, double sourceOut, double fps, long frameBytes, long bufferBytes = DefaultBufferBytes)
    {
        if (fps <= 0) fps = DefaultFrameRate;
        var framesPerChunk = Math.Max(1, bufferBytes / Math.Max(1, frameBytes));
        var first = FirstFrameAtOrAfter(sourceIn, fps);
        var end = Math.Max(first + 1, FirstFrameAtOrAfter(sourceOut, fps));
        double Boundary(long frame) => Math.Max(0, (frame - 0.5) / fps);

        var chunks = new List<Chunk>();
        for (var start = first; start < end; start += framesPerChunk)
        {
            var stop = Math.Min(end, start + framesPerChunk);
            chunks.Add(new Chunk(Boundary(start), Boundary(stop) - Boundary(start)));
        }
        return chunks;
    }

    /// <summary>時間戳不早於 time 的第一格。</summary>
    public static long FirstFrameAtOrAfter(double time, double fps) => (long)Math.Ceiling(time * fps - 1e-6);

    /// <summary>解碼後一格的大小（位元組），依來源的色度取樣與位元深度估算。</summary>
    public static long FrameBytes(MediaInfo media, int width, int height)
    {
        var pixelFormat = media.PixelFormat;
        var samplesPerPixel = pixelFormat.Contains("444") ? 3.0 : pixelFormat.Contains("422") ? 2.0 : 1.5;
        var bytesPerSample = media.BitDepth > 8 ? 2 : 1;
        return (long)(Math.Max(1, width) * (long)Math.Max(1, height) * samplesPerPixel * bytesPerSample);
    }

    /// <summary>
    /// 加入倒放片段的輸入與濾鏡，輸出標籤為 [{label}v]，withAudio 時另有 [{label}a]（未轉格式、未調音量）。
    /// videoFilter 會套在每一小段倒放之前（例如預覽時先縮小，可減少記憶體用量）。
    /// </summary>
    public static void Add(List<string> args, List<string> graph, ref int input, string label, MediaInfo media,
        double sourceIn, double sourceOut, bool withAudio, string videoFilter, long frameBytes, long bufferBytes = DefaultBufferBytes)
    {
        var chunks = Split(sourceIn, sourceOut, media.FrameRate.Value, frameBytes, bufferBytes);
        var concat = new System.Text.StringBuilder();
        var prefilter = videoFilter.Length == 0 ? "" : videoFilter + ",";

        // 由後往前：最後一小段倒放後最先播出。
        for (var i = chunks.Count - 1; i >= 0; i--)
        {
            var chunk = chunks[i];
            args.AddRange(["-ss", Num(chunk.Start), "-t", Num(chunk.Length), "-i", media.Path]);
            var part = $"{label}c{i}";
            graph.Add($"[{input}:v]setpts=PTS-STARTPTS,{prefilter}reverse[{part}v]");
            concat.Append($"[{part}v]");
            if (withAudio)
            {
                // 先補齊到這一小段的長度再倒放，聲音才不會與畫面錯開。
                graph.Add($"[{input}:a]asetpts=PTS-STARTPTS,apad,atrim=0:{Num(chunk.Length)},areverse[{part}a]");
                concat.Append($"[{part}a]");
            }
            input++;
        }

        graph.Add(withAudio
            ? $"{concat}concat=n={chunks.Count}:v=1:a=1[{label}v][{label}a]"
            : $"{concat}concat=n={chunks.Count}:v=1:a=0[{label}v]");
    }

    private static string Num(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
}
