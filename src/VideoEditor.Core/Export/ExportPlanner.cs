using System.Globalization;
using System.Text;
using VideoEditor.Core.Editing;
using VideoEditor.Core.Models;

namespace VideoEditor.Core.Export;

/// <param name="Arguments">FFmpeg 參數，<b>不含</b>輸出路徑（輸出路徑在 <paramref name="OutputPath"/>，匯出時會換成暫存檔）。</param>
/// <param name="OutputPath">使用者選的最終輸出路徑。</param>
public sealed record ExportPlan(
    IReadOnlyList<string> Arguments,
    string OutputPath,
    double Duration,
    Size2 Size,
    string VideoCodec,
    string PixelFormat,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<ReverseJob> ReverseJobs)
{
    /// <summary>全部工作量（秒）：倒放片段的準備加上最後的編碼，用來換算整體進度。</summary>
    public double TotalWork => ReverseJobs.Sum(job => job.Duration) + Duration;
}

/// <summary>匯出的基準：解析度、影格率、編碼格式與色彩都照它。Size 是基準片段轉完方向後的尺寸（輸出比例套用前）。</summary>
public sealed record ExportReference(MediaInfo Media, Size2 Size);

/// <summary>
/// 由時間軸與輸出設定產生 FFmpeg 參數。不執行任何程序，方便測試。
/// 有倒放片段時，要先執行 <see cref="ExportPlan.ReverseJobs"/> 產生中間檔，Arguments 才讀得到（見 <see cref="ReverseGraph"/>）。
/// </summary>
public static class ExportPlanner
{
    private const int AudioSampleRate = 48000;
    private const long MinAudioBitRate = 192_000;
    private const long MaxAudioBitRate = 512_000;
    private const int MaxCommandLength = 30000;

    /// <summary>時間軸全部是圖片時，輸出解析度的較大邊上限（4K）。手機照片常有 4000～8000 像素，照原尺寸輸出很多播放器播不動。</summary>
    public const int MaxStillOutputLongSide = 3840;

    /// <summary>
    /// 時間軸全部是圖片時的基準：30 fps、8-bit、BT.709（與一般 SDR 影片相同）。
    /// 圖片沒有影格率、位元深度與色彩資訊可以沿用。
    /// </summary>
    public static readonly MediaInfo StillsOnlyReference = new()
    {
        Path = "", HasVideo = true, FrameRate = new Rational(30, 1), PixelFormat = "yuv420p", BitDepth = 8,
        ColorPrimaries = "bt709", ColorTransfer = "bt709", ColorSpace = "bt709", ColorRange = "tv",
    };

    /// <summary>
    /// 規格：以時間軸第一個影片片段為基準；圖片沒有影格率與色彩可以沿用，所以取第一個<b>不是圖片</b>的影片片段。
    /// 全部是圖片時用 <see cref="StillsOnlyReference"/>，尺寸取第一張圖（較大邊不超過 <see cref="MaxStillOutputLongSide"/>）。
    /// 影片軌沒有片段時為 null。
    /// </summary>
    public static ExportReference? Reference(Timeline timeline)
    {
        var clips = timeline.VideoClips;
        if (clips.Count == 0) return null;
        // 第一個片段旋轉 90° 時，「原尺寸」輸出也跟著轉成直式。
        if (clips.FirstOrDefault(c => !c.Media.IsStill) is { } video) return new ExportReference(video.Media, video.DisplaySize);
        return new ExportReference(StillsOnlyReference, LimitLongSide(clips[0].DisplaySize, MaxStillOutputLongSide));
    }

    private static Size2 LimitLongSide(Size2 size, int maxLongSide)
    {
        var scale = (double)maxLongSide / Math.Max(size.Width, size.Height);
        return scale >= 1 ? size : new Size2((int)Math.Round(size.Width * scale), (int)Math.Round(size.Height * scale));
    }

    /// <param name="reverseBufferBytes">倒放時每一小段的記憶體上限，測試時可調小以驗證分段。</param>
    public static ExportPlan Build(Timeline timeline, OutputSettings settings, string outputPath,
        long reverseBufferBytes = ReverseGraph.DefaultBufferBytes)
    {
        var clips = timeline.VideoClips;
        var (reference, referenceSize) = Reference(timeline) ?? throw new InvalidOperationException("影片軌沒有片段，無法匯出。");
        var size = OutputSize.Compute(referenceSize.Width, referenceSize.Height, settings.Ratio);
        var duration = timeline.Duration;
        var (codec, pixelFormat) = ChooseVideoFormat(reference);
        var hasAudio = timeline.AllClips.Any(c => c.Media.HasAudio);

        // 全部來自同一支素材，或全為相同的固定影格率時，不插入 fps 濾鏡，保留原始時間戳記。
        // 圖片不算：它本來就以基準影格率產生（見下面迴圈裡的圖片分支）。
        var videos = clips.Where(c => !c.Media.IsStill).ToList();
        var keepTimestamps = videos.All(c => c.Media == reference)
            || videos.All(c => SameRate(c.Media.FrameRate, reference.FrameRate) && !c.Media.IsVariableFrameRate);
        var fpsFilter = keepTimestamps ? "" : $"fps={reference.FrameRate},";
        var outputFormat = OutputFormatFilter(reference, pixelFormat);
        var stillFormat = StillFormatFilter(reference, pixelFormat);

        var args = new List<string> { "-hide_banner", "-nostats", "-progress", "pipe:1", "-y" };
        var graph = new List<string>();
        var reverseJobs = new List<ReverseJob>();
        var concatInputs = new StringBuilder();
        var segments = 0;
        var input = 0;

        // 空白以輸出的影格為單位。黑畫面只能是整數格，空白長度若照時間軸的秒數給，
        // 每段都會被進位成整數格，後面的片段一段比一段晚，配樂（照絕對時間放）也跟著與畫面錯開。
        // 所以記下已經輸出了幾格，每段空白補到「下一個片段的位置最接近的那一格」，誤差不累積，最多半格。
        var gridRate = reference.EffectiveFrameRate;
        long GridFrame(double seconds) => (long)Math.Round(seconds * gridRate);
        long framesDone = 0;

        void AddGap(long frames)
        {
            var label = segments++;
            var length = frames / gridRate;
            // d 多給半格、再以 trim 取準確的格數：只靠 d 的話，秒數的捨入可能讓它多出或少掉一格。
            graph.Add($"color=c=black:s={size.Width}x{size.Height}:r={GridRateText(reference)}:d={Num(length + 0.5 / gridRate)},trim=end_frame={frames},{outputFormat},setsar=1[v{label}]");
            concatInputs.Append($"[v{label}]");
            if (!hasAudio) return;
            graph.Add($"anullsrc=r={AudioSampleRate}:cl=stereo:d={Num(length)}[a{label}]");
            concatInputs.Append($"[a{label}]");
        }

        foreach (var clip in clips)
        {
            var gap = GridFrame(clip.Start) - framesDone;
            if (gap >= 1)
            {
                AddGap(gap);
                framesDone += gap;
            }

            var label = segments++;
            var withAudio = hasAudio && clip.Media.HasAudio;
            string videoSource, audioSource;
            var clipFps = fpsFilter;
            var clipFormat = outputFormat;
            var clipTail = "";
            if (clip.Media.IsStill)
            {
                // 圖片：只讀一次、鋪黑底、縮放裁切、轉色彩都只做一次，最後才用 loop 重複成準確的格數。
                // 不用 -loop 1 讀入：那樣每一格都重新解碼與縮放，2400 萬像素的照片 5 秒要 30 幾秒（實測），先處理再重複不到 2 秒。
                // -framerate 讓這一格的時間基準就是輸出影格率，setpts=N 之後每格剛好一格的時間，接在其他片段後面不會錯開。
                // 不加 -ss：JPEG 加上 -ss 會解不出畫面（見 FrameExtractor.SeekTo）。
                var frames = Math.Max(1, GridFrame(clip.Duration));
                args.AddRange(["-framerate", GridRateText(reference), ..Ffmpeg.FfmpegInput.LocalFilesOnly, "-i", clip.Media.Path]);
                videoSource = $"[{input}:v]{StillFlatten}";
                audioSource = "";
                clipFps = "";
                clipFormat = stillFormat;
                clipTail = $",loop=loop={frames - 1}:size=1:start=0,setpts=N";
                input++;
            }
            else if (clip.Reversed)
            {
                var frameBytes = ReverseGraph.FrameBytes(clip.Media, clip.Media.DisplayWidth, clip.Media.DisplayHeight);
                var job = ReverseGraph.Plan($"r{label}", clip.Media, clip.SourceIn, clip.SourceOut, withAudio, "", frameBytes, reverseBufferBytes);
                reverseJobs.Add(job);
                // 中間檔已經倒放好，這裡只是照順序讀進來。
                args.AddRange(["-f", "concat", ..Ffmpeg.FfmpegInput.LocalFilesOnly, "-i", job.ListPath]);
                videoSource = $"[{input}:v]setpts=PTS-STARTPTS,{ReverseGraph.InputFilter(clip.Media)}";
                audioSource = $"[{input}:a]asetpts=PTS-STARTPTS,";
                input++;
            }
            else
            {
                // -ss/-t 放在 -i 之前：快速定位，重新編碼時切點仍然準確。
                args.AddRange(["-ss", Num(clip.SourceIn), "-t", Num(clip.Duration), ..Ffmpeg.FfmpegInput.LocalFilesOnly, "-i", clip.Media.Path]);
                videoSource = $"[{input}:v]setpts=PTS-STARTPTS,";
                audioSource = $"[{input}:a]asetpts=PTS-STARTPTS,";
                input++;
            }

            graph.Add($"{videoSource}{clipFps}{FitFilter(clip.Media, size, settings.Mode, clip.Transform, clip.Framing)},setsar=1,{clipFormat}{clipTail}[v{label}]");
            concatInputs.Append($"[v{label}]");
            if (hasAudio)
            {
                graph.Add(withAudio
                    ? $"{audioSource}{AudioFormat},volume={Num(clip.Volume)},apad,atrim=0:{Num(clip.Duration)}[a{label}]"
                    : $"anullsrc=r={AudioSampleRate}:cl=stereo:d={Num(clip.Duration)}[a{label}]");
                concatInputs.Append($"[a{label}]");
            }

            framesDone += Math.Max(1, GridFrame(clip.Duration));
        }

        // 配樂比影片長時，以黑畫面補到時間軸結束。
        if (GridFrame(duration) - framesDone >= 1) AddGap(GridFrame(duration) - framesDone);

        var hasMusic = timeline.MusicClips.Count > 0;
        var concatAudio = hasMusic ? "acat" : "aout";
        graph.Add(hasAudio
            ? $"{concatInputs}concat=n={segments}:v=1:a=1[vout][{concatAudio}]"
            : $"{concatInputs}concat=n={segments}:v=1:a=0[vout]");

        if (hasMusic)
        {
            var mix = new StringBuilder("[acat]");
            var index = 0;
            foreach (var clip in timeline.MusicClips)
            {
                args.AddRange(["-ss", Num(clip.SourceIn), "-t", Num(clip.Duration), ..Ffmpeg.FfmpegInput.LocalFilesOnly, "-i", clip.Media.Path]);
                var delay = (long)Math.Round(clip.Start * 1000);
                graph.Add($"[{input}:a]asetpts=PTS-STARTPTS,{AudioFormat},volume={Num(clip.Volume)},adelay={delay}:all=1[m{index}]");
                mix.Append($"[m{index}]");
                input++;
                index++;
            }
            // normalize=0：不自動壓低音量，預覽與匯出才會一致。
            graph.Add($"{mix}amix=inputs={index + 1}:duration=longest:normalize=0[aout]");
        }

        args.AddRange(["-filter_complex", string.Join(';', graph), "-map", "[vout]"]);
        if (hasAudio) args.AddRange(["-map", "[aout]"]);
        args.AddRange(["-c:v", codec, "-preset", "medium", "-crf", settings.Crf.ToString(CultureInfo.InvariantCulture), "-pix_fmt", pixelFormat]);
        if (codec == "libx265") args.AddRange(["-tag:v", "hvc1", "-x265-params", "log-level=error"]);
        args.AddRange(["-fps_mode", "passthrough"]);
        if (hasAudio)
            args.AddRange(["-c:a", "aac", "-b:a", AudioBitRate(timeline).ToString(CultureInfo.InvariantCulture), "-ar", AudioSampleRate.ToString(CultureInfo.InvariantCulture)]);
        args.AddRange(["-t", Num(duration), "-movflags", "+faststart"]);

        // 濾鏡圖太長時 ExportService 會把它放進檔案（見 FilterGraphFile），所以這裡只計算濾鏡圖以外的參數：
        // 每個片段的 -ss -t -i 路徑 才是命令列真正的上限。
        var graphLength = args[args.IndexOf("-filter_complex") + 1].Length + 3;
        if (FilterGraphFile.CommandLength(args) - graphLength + outputPath.Length + 3 > MaxCommandLength)
            throw new InvalidOperationException("片段數量過多，超過 Windows 命令列長度上限。");
        return new ExportPlan(args, outputPath, duration, size, codec, pixelFormat, CollectWarnings(timeline, reference, referenceSize, size, settings.Mode), reverseJobs);
    }

    /// <summary>決策：8-bit 用 H.264；10-bit 以上或 HDR 用 H.265，並維持來源色彩格式。</summary>
    public static (string Codec, string PixelFormat) ChooseVideoFormat(MediaInfo source)
    {
        var chroma = source.PixelFormat.Contains("444") ? "444" : source.PixelFormat.Contains("422") ? "422" : "420";
        if (source.BitDepth > 8 || source.IsHdr)
            return ("libx265", $"yuv{chroma}p{(source.BitDepth >= 12 ? 12 : 10)}le");
        return ("libx264", $"yuv{chroma}p");
    }

    /// <summary>規格：不低於來源；AAC 實務上限 512k，來源更高（如 PCM）時取上限。</summary>
    public static long AudioBitRate(Timeline timeline)
    {
        var highest = timeline.AllClips.Where(c => c.Media.HasAudio).Select(c => c.Media.AudioBitRate).DefaultIfEmpty(0).Max();
        return Math.Clamp(highest, MinAudioBitRate, MaxAudioBitRate);
    }

    /// <summary>
    /// 像素比修正 → 旋轉與鏡像 →（填滿裁切時）裁出畫面位置 → 縮放至輸出尺寸。像素比要在旋轉前修正，因為它是對原始方向定義的。
    /// 畫面位置以轉完的方向記錄，所以裁切放在旋轉之後；裁出的範圍已是輸出比例，之後的縮放與裁切只修正一像素內的誤差。
    /// </summary>
    public static string FitFilter(MediaInfo media, Size2 size, FitMode mode, FrameTransform transform = default, ClipFraming? framing = null)
    {
        var prefix = (media.SampleAspect.Num == media.SampleAspect.Den ? "" : $"scale=iw*{media.SampleAspect}:ih,setsar=1,")
            + transform.Filter();
        var source = transform.Apply(new Size2(media.DisplayWidth, media.DisplayHeight));
        return prefix + (mode == FitMode.Fill
            ? (framing?.CropFilter(source, size) ?? "") + $"scale={size.Width}:{size.Height}:force_original_aspect_ratio=increase:flags=lanczos,crop={size.Width}:{size.Height}"
            : $"scale={size.Width}:{size.Height}:force_original_aspect_ratio=decrease:flags=lanczos,pad={size.Width}:{size.Height}:(ow-iw)/2:(oh-ih)/2:black");
    }

    /// <summary>
    /// 統一像素格式並標記色彩資訊。新版 FFmpeg 的編碼器色彩標記取自影格，
    /// 所以要用 setparams 寫到影格上；它只改標記，不轉換像素值。
    /// 因此色彩範圍要先用 scale 明確轉成輸出的範圍，像素值才會與標記一致：
    /// 只寫 format=yuv420p 時，全範圍（yuvj420p）來源會被轉成 limited，再被標回 full，白色變灰、黑色變淺。
    /// 輸入範圍取自每一格的標記，所以 limited 的片段接在 full 的輸出裡也會正確轉換。
    /// </summary>
    private static string OutputFormatFilter(MediaInfo source, string pixelFormat)
    {
        var tags = new List<string>();
        if (source.ColorPrimaries is { } primaries) tags.Add($"color_primaries={primaries}");
        if (source.ColorTransfer is { } transfer) tags.Add($"color_trc={transfer}");
        if (source.ColorSpace is { } space) tags.Add($"colorspace={space}");
        var rangeConversion = "";
        if (source.ColorRange is "pc" or "tv")
        {
            rangeConversion = $"scale=out_range={source.ColorRange},";
            tags.Add($"range={source.ColorRange}");
        }
        var format = $"{rangeConversion}format={pixelFormat}";
        return tags.Count == 0 ? format : $"{format},setparams={string.Join(':', tags)}";
    }

    /// <summary>
    /// 圖片的透明部分鋪黑（見 <see cref="Ffmpeg.FfmpegInput.StillFlatten"/>，預覽與縮圖用同一個）。
    /// 之後的縮放、裁切、加黑邊都在 RGB 上做，最後由 <see cref="StillFormatFilter"/> 轉成 YUV。
    /// </summary>
    private const string StillFlatten = Ffmpeg.FfmpegInput.StillFlatten + ",";

    /// <summary>
    /// 圖片（RGB）轉成輸出的 YUV：明確指定與基準相同的轉換矩陣與範圍。不指定時 FFmpeg 用 BT.601，
    /// 和 BT.709 的影片放在一起顏色會偏。之後接 <see cref="OutputFormatFilter"/> 標記色彩。
    /// </summary>
    private static string StillFormatFilter(MediaInfo reference, string pixelFormat)
    {
        var matrix = reference.ColorSpace switch
        {
            "smpte170m" or "bt470bg" => "bt601",
            "bt2020nc" or "bt2020c" => "bt2020",
            "smpte240m" => "smpte240m",
            "fcc" => "fcc",
            _ => "bt709",
        };
        var range = reference.ColorRange == "pc" ? "pc" : "tv";
        return $"scale=out_color_matrix={matrix}:out_range={range},format={pixelFormat},{OutputFormatFilter(reference, pixelFormat)}";
    }

    private static List<string> CollectWarnings(Timeline timeline, MediaInfo reference, Size2 referenceSize, Size2 outputSize, FitMode mode)
    {
        var warnings = new List<string>();
        // 畫面位置放大時，等於把來源的一小塊放大成整個畫面，畫質一定會下降；加黑邊不使用畫面位置。
        var zoomed = timeline.VideoClips.Select((clip, index) => (clip.Framing, Number: index + 1))
            .Where(c => mode == FitMode.Fill && c.Framing.IsZoomed).ToList();
        if (zoomed.Count > 0)
            warnings.Add($"{string.Join("、", zoomed.Select(c => $"第 {c.Number} 段放大 {c.Framing.EffectiveZoom * 100:0}%"))}（畫面位置），"
                + "匯出時會放大來源畫面，畫質會比原始畫面差。");
        // 解析度不足的圖片：匯出時會放大（填滿裁切看較短的那一邊，加黑邊看較長的那一邊）。
        var blurry = timeline.VideoClips.Select((clip, index) => (clip, Number: index + 1))
            .Where(c => c.clip.Media.IsStill && Upscales(c.clip.DisplaySize, outputSize, mode)).ToList();
        if (blurry.Count > 0)
            warnings.Add($"{string.Join("、", blurry.Select(c => $"第 {c.Number} 段圖片（{c.clip.DisplaySize.Width}×{c.clip.DisplaySize.Height}）"))}"
                + $"比輸出的 {outputSize.Width}×{outputSize.Height} 小，匯出時會放大，畫面會比較模糊。");
        if (reference.IsHdr && timeline.VideoClips.Any(c => c.Media.IsStill))
            warnings.Add("圖片會以 SDR 放進 HDR 影片，不做色調對應，圖片的顏色可能偏淡。");

        // 以下比較影片片段之間的差異；圖片沒有影格率、位元深度與色彩資訊，不算在內。
        var media = timeline.VideoClips.Select(c => c.Media).Where(m => !m.IsStill).Distinct().ToList();
        if (media.Any(m => !SameRate(m.FrameRate, reference.FrameRate)))
            warnings.Add($"片段影格率不一致，將統一為第一個片段的 {reference.FrameRate.Value:0.###} fps。");
        if (media.Any(m => m.IsVariableFrameRate) && media.Count > 1)
            warnings.Add("含可變影格率素材，與其他片段接合時將轉為固定影格率。");
        if (timeline.VideoClips.Any(c => !c.Media.IsStill && c.DisplaySize != referenceSize))
            warnings.Add("片段解析度不一致，輸出解析度以第一個片段為準。");
        if (media.Any(m => m.IsHdr != reference.IsHdr || m.BitDepth != reference.BitDepth))
            warnings.Add("片段的色彩格式不一致（SDR/HDR 或位元深度），會統一為第一個片段的格式，不做色調對應，部分片段顏色可能不正確。");
        if (timeline.AllClips.Any(c => c.Media.AudioChannels > 2))
            warnings.Add("多聲道音訊會混音為立體聲。");
        return warnings;
    }

    /// <summary>輸出影格率的文字（與 gridRate 一致）：讀不到影格率的基準素材用 <see cref="MediaInfo.DefaultFrameRate"/>。</summary>
    private static string GridRateText(MediaInfo reference) =>
        reference.FrameRate.Value > 0 ? reference.FrameRate.ToString() : Num(MediaInfo.DefaultFrameRate);

    private static bool Upscales(Size2 source, Size2 output, FitMode mode)
    {
        var widthScale = (double)output.Width / source.Width;
        var heightScale = (double)output.Height / source.Height;
        return (mode == FitMode.Fill ? Math.Max(widthScale, heightScale) : Math.Min(widthScale, heightScale)) > 1 + 1e-9;
    }

    private static bool SameRate(Rational a, Rational b) => Math.Abs(a.Value - b.Value) < 0.001;
    private static string AudioFormat => $"aresample={AudioSampleRate},aformat=sample_fmts=fltp:channel_layouts=stereo";
    private static string Num(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
}
