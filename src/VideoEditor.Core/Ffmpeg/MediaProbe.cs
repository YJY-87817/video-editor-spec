using System.Globalization;
using System.Text;
using System.Text.Json;
using VideoEditor.Core.Models;

namespace VideoEditor.Core.Ffmpeg;

public sealed class MediaProbe(FfmpegTools tools)
{
    public async Task<MediaInfo> ProbeAsync(string path, CancellationToken token = default)
    {
        string[] args = ["-v", "error", "-print_format", "json", "-show_format", "-show_streams", path];
        var json = await ProcessRunner.RunForBytesAsync(tools.Ffprobe, args, token);
        return Parse(Encoding.UTF8.GetString(json), path);
    }

    public static MediaInfo Parse(string json, string path)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var format = root.TryGetProperty("format", out var f) ? f : default;
        var streams = root.TryGetProperty("streams", out var s) ? s.EnumerateArray().ToList() : [];

        // 內嵌封面會被標示為影像串流，不算影片。
        var video = streams.FirstOrDefault(x => Str(x, "codec_type") == "video" && !IsAttachedPicture(x));
        var audio = streams.FirstOrDefault(x => Str(x, "codec_type") == "audio");
        var hasVideo = video.ValueKind == JsonValueKind.Object;
        var hasAudio = audio.ValueKind == JsonValueKind.Object;
        if (!hasVideo && !hasAudio) throw new InvalidDataException("檔案中沒有可用的影像或音訊。");

        var duration = Dbl(format, "duration");
        if (duration <= 0 && hasVideo) duration = Dbl(video, "duration");
        if (duration <= 0 && hasAudio) duration = Dbl(audio, "duration");

        var info = new MediaInfo
        {
            Path = path,
            Duration = duration,
            BitRate = Lng(format, "bit_rate"),
            HasAudio = hasAudio,
            AudioCodec = hasAudio ? Str(audio, "codec_name") ?? "" : "",
            AudioBitRate = hasAudio ? Lng(audio, "bit_rate") : 0,
            AudioSampleRate = hasAudio ? (int)Lng(audio, "sample_rate") : 0,
            AudioChannels = hasAudio ? (int)Lng(audio, "channels") : 0,
        };
        if (!hasVideo) return info;

        var rotation = ReadRotation(video);
        var sar = Rational.Parse(Str(video, "sample_aspect_ratio")?.Replace(':', '/'));
        if (sar.Num <= 0 || sar.Den <= 0) sar = new Rational(1, 1);
        var width = (int)Math.Round(Lng(video, "width") * sar.Value);
        var height = (int)Lng(video, "height");
        if (Math.Abs(rotation) % 180 == 90) (width, height) = (height, width);

        var realRate = Rational.Parse(Str(video, "r_frame_rate"));
        var averageRate = Rational.Parse(Str(video, "avg_frame_rate"));
        // r_frame_rate 與平均影格率差距超過 1% 視為可變影格率。
        var isVariable = realRate.Value > 0 && averageRate.Value > 0
            && Math.Abs(realRate.Value - averageRate.Value) / realRate.Value > 0.01;
        var pixelFormat = Str(video, "pix_fmt") ?? "";

        return info with
        {
            HasVideo = true,
            DisplayWidth = width,
            DisplayHeight = height,
            Rotation = rotation,
            FrameRate = isVariable || realRate.Value <= 0 ? averageRate : realRate,
            IsVariableFrameRate = isVariable,
            VideoCodec = Str(video, "codec_name") ?? "",
            PixelFormat = pixelFormat,
            BitDepth = BitDepthOf(pixelFormat),
            ColorPrimaries = Known(Str(video, "color_primaries")),
            ColorTransfer = Known(Str(video, "color_transfer")),
            ColorSpace = Known(Str(video, "color_space")),
            ColorRange = Known(Str(video, "color_range")),
            SampleAspect = sar,
        };
    }

    public static int BitDepthOf(string pixelFormat)
    {
        foreach (var depth in new[] { 16, 14, 12, 10, 9 })
            if (pixelFormat.Contains(depth.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)) return depth;
        return 8;
    }

    private static int ReadRotation(JsonElement video)
    {
        if (video.TryGetProperty("side_data_list", out var sideData))
            foreach (var item in sideData.EnumerateArray())
                if (item.TryGetProperty("rotation", out var value) && value.TryGetDouble(out var degrees))
                    return (int)Math.Round(degrees);

        if (video.TryGetProperty("tags", out var tags)
            && int.TryParse(Str(tags, "rotate"), CultureInfo.InvariantCulture, out var rotate))
            return rotate;
        return 0;
    }

    private static bool IsAttachedPicture(JsonElement stream) =>
        stream.TryGetProperty("disposition", out var d) && d.TryGetProperty("attached_pic", out var a)
        && a.ValueKind == JsonValueKind.Number && a.GetInt32() == 1;

    private static string? Known(string? value) => value is null or "unknown" or "reserved" ? null : value;

    private static string? Str(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value)) return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
    }

    private static double Dbl(JsonElement element, string name) =>
        double.TryParse(Str(element, name), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;

    private static long Lng(JsonElement element, string name) =>
        long.TryParse(Str(element, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
}
