using System.Globalization;
using VideoEditor.Core.Models;

namespace VideoEditor.Core.Export;

/// <summary>倒放的一個步驟：一次 FFmpeg 執行，把一小段倒放後寫成中間檔。Duration 是這一小段的長度（秒），用來換算進度。</summary>
public sealed record ReverseStep(IReadOnlyList<string> Arguments, double Duration);

/// <summary>
/// 一個倒放片段的全部工作：依序執行 Steps，再把 ListContent 寫到 ListPath，
/// 之後的 FFmpeg 指令以 <c>-f concat -i ListPath</c> 讀到的就是整段倒放好的影像與聲音。
/// 路徑裡的 <see cref="ReverseGraph.WorkToken"/> 在執行時換成實際的暫存資料夾。
/// </summary>
public sealed record ReverseJob(string ListPath, string ListContent, IReadOnlyList<ReverseStep> Steps, double Duration);

/// <summary>
/// 倒放片段的處理方式。
/// reverse / areverse 濾鏡要把整段影格留在記憶體裡才能倒著輸出，長片段會吃光記憶體，
/// 所以把片段切成小段，每一小段各跑一次 FFmpeg、倒放後寫成無損的中間檔，最後由後往前接起來。
/// 不能把所有小段放進同一個 FFmpeg 指令：各個輸入會同時解碼、各自的 reverse 同時緩衝，等於整段都在記憶體裡。
/// </summary>
public static class ReverseGraph
{
    /// <summary>每一小段解碼後的影格最多占用的記憶體。</summary>
    public const long DefaultBufferBytes = 512L * 1024 * 1024;
    private const double DefaultFrameRate = 30;

    /// <summary>暫存資料夾在參數裡的代號，執行時由 <see cref="Resolve"/> 換成實際路徑。</summary>
    public const string WorkToken = "{work}";
    private const double IntermediateCompression = 0.6;

    /// <summary>
    /// 一小段。Start／Length 是影像的定位範圍（切點在兩格之間）；
    /// AudioStart／AudioLength 是這幾格實際顯示的時間範圍，聲音取這一段。
    /// </summary>
    public sealed record Chunk(double Start, double Length, double AudioStart, double AudioLength);

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
            chunks.Add(new Chunk(Boundary(start), Boundary(stop) - Boundary(start), start / fps, (stop - start) / fps));
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
    /// 規劃一個倒放片段。中間檔是 FFV1 影像加 PCM 聲音（都不損畫質與音質），放在 NUT 容器裡以保留來源的時間基準。
    /// videoFilter 會套在每一小段倒放之前（例如預覽時先縮小，可減少記憶體與中間檔大小）。
    /// </summary>
    public static ReverseJob Plan(string name, MediaInfo media, double sourceIn, double sourceOut, bool withAudio,
        string videoFilter, long frameBytes, long bufferBytes = DefaultBufferBytes)
    {
        var fps = media.FrameRate.Value > 0 ? media.FrameRate.Value : DefaultFrameRate;
        var chunks = Split(sourceIn, sourceOut, fps, frameBytes, bufferBytes);
        var prefilter = videoFilter.Length == 0 ? "" : videoFilter + ",";
        // FFV1 沒有全範圍專用的 yuvj 格式；直接交給編碼器會被轉成 limited range。
        // 明確指定輸入輸出都是全範圍，只換格式名稱、不動像素值。
        var keepRange = media.PixelFormat.StartsWith("yuvj", StringComparison.Ordinal)
            ? $",scale=in_range=pc:out_range=pc,format=yuv{media.PixelFormat[4..]}"
            : "";

        var steps = new List<ReverseStep>();
        var list = new System.Text.StringBuilder("ffconcat version 1.0\n");
        long Microseconds(double seconds) => (long)Math.Round(seconds * 1_000_000);
        var elapsed = 0.0;

        // 由後往前：最後一小段倒放後最先播出。
        for (var i = chunks.Count - 1; i >= 0; i--)
        {
            var chunk = chunks[i];
            var file = $"{name}-{i:D5}.nut";
            var args = new List<string> { "-hide_banner", "-nostats", "-v", "error", "-progress", "pipe:1", "-y" };
            args.AddRange(["-ss", Num(chunk.Start), "-t", Num(chunk.Length), "-i", media.Path]);
            var graph = $"[0:v]setpts=PTS-STARTPTS,{prefilter}reverse{keepRange}[v]";
            if (withAudio)
            {
                // 聲音取這幾格實際顯示的時間範圍；影像的定位點提前半格只是為了不漏掉第一格。
                args.AddRange(["-ss", Num(chunk.AudioStart), "-t", Num(chunk.AudioLength), "-vn", "-i", media.Path]);
                // 先補齊到這一小段的長度再倒放，聲音才不會與畫面錯開。
                graph += $";[1:a]asetpts=PTS-STARTPTS,apad,atrim=0:{Num(chunk.AudioLength)},areverse[a]";
            }
            args.AddRange(["-filter_complex", graph, "-map", "[v]"]);
            if (withAudio) args.AddRange(["-map", "[a]", "-c:a", "pcm_f32le"]);
            // -g 1：每格都是關鍵影格。passthrough：保留來源的時間戳記，不補格、不丟格。
            args.AddRange(["-c:v", "ffv1", "-level", "3", "-g", "1", "-fps_mode", "passthrough", "-f", "nut", $"{WorkToken}/{file}"]);
            steps.Add(new ReverseStep(args, chunk.AudioLength));

            // 每個檔案的長度寫成「累計時間」相減，捨入誤差才不會隨小段數量累積。
            var next = elapsed + chunk.AudioLength;
            list.Append($"file {file}\nduration {Num((Microseconds(next) - Microseconds(elapsed)) / 1_000_000.0)}\n");
            elapsed = next;
        }

        return new ReverseJob($"{WorkToken}/{name}.ffconcat", list.ToString(), steps, elapsed);
    }

    /// <summary>
    /// 接在倒放中間檔輸入之後的濾鏡：中間檔不記錄色彩範圍，這裡把來源的範圍標回影格上，
    /// 後面轉換色彩範圍時才知道輸入是全範圍還是 limited。只改標記，不動像素值。
    /// </summary>
    public static string InputFilter(MediaInfo media) =>
        media.ColorRange is "pc" or "tv" ? $"setparams=range={media.ColorRange}," : "";

    /// <summary>中間檔大約占用的磁碟空間（位元組）。FFV1 的壓縮率依畫面內容而定，這裡抓解碼後大小的六成。</summary>
    public static long IntermediateBytes(MediaInfo media, double sourceIn, double sourceOut)
    {
        var fps = media.FrameRate.Value > 0 ? media.FrameRate.Value : DefaultFrameRate;
        var frames = Math.Max(1, FirstFrameAtOrAfter(sourceOut, fps) - FirstFrameAtOrAfter(sourceIn, fps));
        var video = frames * FrameBytes(media, media.DisplayWidth, media.DisplayHeight) * IntermediateCompression;
        var audio = media.HasAudio ? (sourceOut - sourceIn) * Math.Max(1, media.AudioSampleRate) * Math.Max(1, media.AudioChannels) * 4 : 0;
        return (long)(video + audio);
    }

    /// <summary>把參數裡的 <see cref="WorkToken"/> 換成實際的暫存資料夾。</summary>
    public static IEnumerable<string> Resolve(IEnumerable<string> args, string workDirectory) =>
        args.Select(arg => ResolvePath(arg, workDirectory));

    public static string ResolvePath(string path, string workDirectory) =>
        path.StartsWith(WorkToken + "/", StringComparison.Ordinal) ? Path.Combine(workDirectory, path[(WorkToken.Length + 1)..]) : path;

    /// <summary>
    /// 在 workDirectory 裡執行一個倒放工作：逐一產生每小段的中間檔，再寫出接合清單。
    /// progress 回報這個工作已完成的秒數（0 到 job.Duration）。資料夾由呼叫端建立與刪除。
    /// </summary>
    public static async Task RunAsync(Ffmpeg.FfmpegTools tools, ReverseJob job, string workDirectory, Action<double>? progress, CancellationToken token)
    {
        var done = 0.0;
        foreach (var step in job.Steps)
        {
            var before = done;
            await Ffmpeg.ProcessRunner.RunWithLinesAsync(tools.Ffmpeg, Resolve(step.Arguments, workDirectory), line =>
            {
                if (ExportService.ParseProgress(line, step.Duration) is { } value) progress?.Invoke(before + value * step.Duration);
            }, token);
            done = before + step.Duration;
            progress?.Invoke(done);
        }
        await File.WriteAllTextAsync(ResolvePath(job.ListPath, workDirectory), job.ListContent, token);
    }

    private static string Num(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
}
