using System.Globalization;
using System.Text;
using System.Text.Json;
using VideoEditor.Core.Models;
using TimelineModel = VideoEditor.Core.Timeline.Timeline;

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
        // 靜態圖片（ffprobe 讀成約 0.04 秒的影像）、極短或讀不到長度的檔案無法放上時間軸，匯入時就擋下。
        if (duration < TimelineModel.MinClipDuration)
            throw new InvalidDataException(duration <= 0
                ? "無法讀取檔案長度，可能是靜態圖片或檔案已損毀。"
                : string.Create(CultureInfo.InvariantCulture,
                    $"檔案太短（{duration:0.###} 秒），可能是靜態圖片；片段至少要 {TimelineModel.MinClipDuration} 秒。"));

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

    /// <summary>
    /// 由像素格式名稱判斷每個色版的位元數。只看名稱裡表示位元數的位置，
    /// 不能看到數字就算：nv12、yuv410p 的數字是排列方式與色度取樣，兩者都是 8-bit。
    /// </summary>
    public static int BitDepthOf(string pixelFormat)
    {
        // yuv420p10le、gbrp12be、gray16le、yuva444p10le：結尾是「p 或 gray + 位元數 + le／be」。
        var planar = System.Text.RegularExpressions.Regex.Match(pixelFormat, @"(?:p|gray|ya)(9|10|12|14|16)(?:le|be)$");
        if (planar.Success) return int.Parse(planar.Groups[1].Value, CultureInfo.InvariantCulture);
        // p010le、p212be、p416le：硬體解碼常見的半平面格式，後兩位是位元數。
        var semiPlanar = System.Text.RegularExpressions.Regex.Match(pixelFormat, @"^p[024](10|12|16)(?:le|be)?$");
        if (semiPlanar.Success) return int.Parse(semiPlanar.Groups[1].Value, CultureInfo.InvariantCulture);
        // y210le、y212le（封裝 4:2:2）與 nv20le（10-bit 版的 nv16）。
        var packed = System.Text.RegularExpressions.Regex.Match(pixelFormat, @"^y2(10|12)(?:le|be)?$");
        if (packed.Success) return int.Parse(packed.Groups[1].Value, CultureInfo.InvariantCulture);
        if (pixelFormat.StartsWith("nv20", StringComparison.Ordinal)) return 10;
        if (pixelFormat.StartsWith("x2rgb10", StringComparison.Ordinal) || pixelFormat.StartsWith("x2bgr10", StringComparison.Ordinal)) return 10;
        // rgb48、bgr48、rgba64、bgra64：每個色版 16-bit。
        if (System.Text.RegularExpressions.Regex.IsMatch(pixelFormat, @"^(?:rgb48|bgr48|rgba64|bgra64)")) return 16;
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
