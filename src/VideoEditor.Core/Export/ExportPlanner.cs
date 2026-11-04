using System.Globalization;
using System.Text;
using VideoEditor.Core.Models;
using TimelineModel = VideoEditor.Core.Timeline.Timeline;

namespace VideoEditor.Core.Export;

public sealed record ExportPlan(
    IReadOnlyList<string> Arguments,
    double Duration,
    Size2 Size,
    string VideoCodec,
    string PixelFormat,
    IReadOnlyList<string> Warnings);

/// <summary>由時間軸與輸出設定產生 FFmpeg 參數。不執行任何程序，方便測試。</summary>
public static class ExportPlanner
{
    private const int AudioSampleRate = 48000;
    private const long MinAudioBitRate = 192_000;
    private const long MaxAudioBitRate = 512_000;
    private const int MaxCommandLength = 30000;

    /// <param name="reverseBufferBytes">倒放時每一小段的記憶體上限，測試時可調小以驗證分段。</param>
    public static ExportPlan Build(TimelineModel timeline, OutputSettings settings, string outputPath,
        long reverseBufferBytes = ReverseGraph.DefaultBufferBytes)
    {
        var clips = timeline.VideoClips;
        if (clips.Count == 0) throw new InvalidOperationException("影片軌沒有片段，無法匯出。");

        // 決策：以時間軸上第一個片段作為解析度、影格率與色彩的基準。
        var reference = clips[0].Media;
        var size = OutputSize.Compute(reference.DisplayWidth, reference.DisplayHeight, settings.Ratio);
        var duration = timeline.Duration;
        var (codec, pixelFormat) = ChooseVideoFormat(reference);
        var hasAudio = timeline.AllClips.Any(c => c.Media.HasAudio);

        // 全部來自同一支素材，或全為相同的固定影格率時，不插入 fps 濾鏡，保留原始時間戳記。
        var keepTimestamps = clips.All(c => c.Media == reference)
            || clips.All(c => SameRate(c.Media.FrameRate, reference.FrameRate) && !c.Media.IsVariableFrameRate);
        var fpsFilter = keepTimestamps ? "" : $"fps={reference.FrameRate},";
        var outputFormat = OutputFormatFilter(reference, pixelFormat);

        var args = new List<string> { "-hide_banner", "-nostats", "-progress", "pipe:1", "-y" };
        var graph = new List<string>();
        var concatInputs = new StringBuilder();
        var segments = 0;
        var input = 0;

        void AddGap(double length)
        {
            var label = segments++;
            graph.Add($"color=c=black:s={size.Width}x{size.Height}:r={reference.FrameRate}:d={Num(length)},{outputFormat},setsar=1[v{label}]");
            concatInputs.Append($"[v{label}]");
            if (!hasAudio) return;
            graph.Add($"anullsrc=r={AudioSampleRate}:cl=stereo:d={Num(length)}[a{label}]");
            concatInputs.Append($"[a{label}]");
        }

        var cursor = 0.0;
        foreach (var clip in clips)
        {
            if (clip.Start - cursor > 0.001) AddGap(clip.Start - cursor);

            var label = segments++;
            var withAudio = hasAudio && clip.Media.HasAudio;
            string videoSource, audioSource;
            if (clip.Reversed)
            {
                var frameBytes = ReverseGraph.FrameBytes(clip.Media, clip.Media.DisplayWidth, clip.Media.DisplayHeight);
                ReverseGraph.Add(args, graph, ref input, $"r{label}", clip.Media, clip.SourceIn, clip.SourceOut, withAudio, "", frameBytes, reverseBufferBytes);
                videoSource = $"[r{label}v]";
                audioSource = $"[r{label}a]";
            }
            else
            {
                // -ss/-t 放在 -i 之前：快速定位，重新編碼時切點仍然準確。
                args.AddRange(["-ss", Num(clip.SourceIn), "-t", Num(clip.Duration), "-i", clip.Media.Path]);
                videoSource = $"[{input}:v]setpts=PTS-STARTPTS,";
                audioSource = $"[{input}:a]asetpts=PTS-STARTPTS,";
                input++;
            }

            graph.Add($"{videoSource}{fpsFilter}{FitFilter(clip.Media, size, settings.Mode)},setsar=1,{outputFormat}[v{label}]");
            concatInputs.Append($"[v{label}]");
            if (hasAudio)
            {
                graph.Add(withAudio
                    ? $"{audioSource}{AudioFormat},volume={Num(clip.Volume)},apad,atrim=0:{Num(clip.Duration)}[a{label}]"
                    : $"anullsrc=r={AudioSampleRate}:cl=stereo:d={Num(clip.Duration)}[a{label}]");
                concatInputs.Append($"[a{label}]");
            }

            cursor = clip.End;
        }

        // 配樂比影片長時，以黑畫面補到時間軸結束。
        if (duration - cursor > 0.001) AddGap(duration - cursor);

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
                args.AddRange(["-ss", Num(clip.SourceIn), "-t", Num(clip.Duration), "-i", clip.Media.Path]);
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
        args.AddRange(["-t", Num(duration), "-movflags", "+faststart", outputPath]);

        if (args.Sum(a => a.Length + 3) > MaxCommandLength)
            throw new InvalidOperationException(timeline.VideoClips.Any(c => c.Reversed)
                ? "片段數量過多（倒放片段會分成多段處理），超過 Windows 命令列長度上限。請縮短倒放片段。"
                : "片段數量過多，超過 Windows 命令列長度上限。");
        return new ExportPlan(args, duration, size, codec, pixelFormat, CollectWarnings(timeline, reference));
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
    public static long AudioBitRate(TimelineModel timeline)
    {
        var highest = timeline.AllClips.Where(c => c.Media.HasAudio).Select(c => c.Media.AudioBitRate).DefaultIfEmpty(0).Max();
        return Math.Clamp(highest, MinAudioBitRate, MaxAudioBitRate);
    }

    public static string FitFilter(MediaInfo media, Size2 size, FitMode mode)
    {
        var prefix = media.SampleAspect.Num == media.SampleAspect.Den ? "" : $"scale=iw*{media.SampleAspect}:ih,setsar=1,";
        return prefix + (mode == FitMode.Fill
            ? $"scale={size.Width}:{size.Height}:force_original_aspect_ratio=increase:flags=lanczos,crop={size.Width}:{size.Height}"
            : $"scale={size.Width}:{size.Height}:force_original_aspect_ratio=decrease:flags=lanczos,pad={size.Width}:{size.Height}:(ow-iw)/2:(oh-ih)/2:black");
    }

    /// <summary>
    /// 統一像素格式並標記色彩資訊。新版 FFmpeg 的編碼器色彩標記取自影格，
    /// 所以要用 setparams 寫到影格上；它只改標記，不轉換像素值。
    /// </summary>
    private static string OutputFormatFilter(MediaInfo source, string pixelFormat)
    {
        var tags = new List<string>();
        if (source.ColorPrimaries is { } primaries) tags.Add($"color_primaries={primaries}");
        if (source.ColorTransfer is { } transfer) tags.Add($"color_trc={transfer}");
        if (source.ColorSpace is { } space) tags.Add($"colorspace={space}");
        if (source.ColorRange is { } range) tags.Add($"range={range}");
        return tags.Count == 0 ? $"format={pixelFormat}" : $"format={pixelFormat},setparams={string.Join(':', tags)}";
    }

    private static List<string> CollectWarnings(TimelineModel timeline, MediaInfo reference)
    {
        var warnings = new List<string>();
        var media = timeline.VideoClips.Select(c => c.Media).Distinct().ToList();
        if (media.Any(m => !SameRate(m.FrameRate, reference.FrameRate)))
            warnings.Add($"片段影格率不一致，將統一為第一個片段的 {reference.FrameRate.Value:0.###} fps。");
        if (media.Any(m => m.IsVariableFrameRate) && media.Count > 1)
            warnings.Add("含可變影格率素材，與其他片段接合時將轉為固定影格率。");
        if (media.Any(m => m.DisplayWidth != reference.DisplayWidth || m.DisplayHeight != reference.DisplayHeight))
            warnings.Add("片段解析度不一致，輸出解析度以第一個片段為準。");
        if (media.Any(m => m.IsHdr != reference.IsHdr || m.BitDepth != reference.BitDepth))
            warnings.Add("片段的色彩格式不一致（SDR/HDR 或位元深度），會統一為第一個片段的格式，不做色調對應，部分片段顏色可能不正確。");
        if (timeline.AllClips.Any(c => c.Media.AudioChannels > 2))
            warnings.Add("多聲道音訊會混音為立體聲。");
        return warnings;
    }

    private static bool SameRate(Rational a, Rational b) => Math.Abs(a.Value - b.Value) < 0.001;
    private static string AudioFormat => $"aresample={AudioSampleRate},aformat=sample_fmts=fltp:channel_layouts=stereo";
    private static string Num(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
}
