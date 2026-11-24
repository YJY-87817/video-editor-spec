using System.Globalization;
using System.Text;
using System.Text.Json;
using VideoEditor.Core.Editing;
using VideoEditor.Core.Models;

namespace VideoEditor.Core.Ffmpeg;

public sealed class MediaProbe(FfmpegTools tools, FfmpegTimeouts? timeouts = null)
{
    // 檔案自己宣稱的數值沒有上限，損毀或惡意的檔案可能寫出極端值，讓時間軸、倒放分段、預覽緩衝配置出天文數字。
    // 上限遠高於實際素材（16K 影像、1000 fps 高速攝影、48 小時錄影），只擋明顯不合理的值。
    public const int MaxDimension = 16384;
    public const double MaxFrameRate = 1000;
    public static readonly TimeSpan MaxDuration = TimeSpan.FromHours(48);

    /// <summary>
    /// 視為靜態圖片的影像編碼；容器還要是單張圖片（見 <see cref="IsStillImage"/>）。
    /// 動態 GIF（容器 gif）、APNG（容器 apng）不在其中，照影片處理。
    /// </summary>
    private static readonly HashSet<string> StillCodecs = new(StringComparer.Ordinal) { "mjpeg", "png", "bmp", "webp", "tiff" };

    public async Task<MediaInfo> ProbeAsync(string path, CancellationToken token = default)
    {
        string[] args = ["-v", "error", "-print_format", "json", "-show_format", "-show_streams", ..FfmpegInput.LocalFilesOnly, "-i", path];
        var json = Encoding.UTF8.GetString(await ProcessRunner.RunForBytesAsync(tools.Ffprobe, args, token, (timeouts ?? FfmpegTimeouts.Default).Probe));
        if (!IsStillImage(json)) return Parse(json, path);
        // 動態 WebP 在 ffprobe 看來和單張 WebP 一樣（webp_pipe），只能看檔頭分辨。照影片處理（長度讀不到，會被擋下）。
        if (IsAnimatedWebp(ReadHeader(path, 21))) return Parse(json, path, allowStill: false);

        // 圖片的 EXIF 方向（手機直拍照片）不在串流資訊裡，只出現在解出的影格上（FFmpeg 解碼時會自動轉正），
        // 所以圖片要再讀一次第一格的資訊。影片不需要，不多花這一次。
        string[] withFrame = [.. args[..^2], "-show_frames", "-read_intervals", "%+#1", .. args[^2..]];
        json = Encoding.UTF8.GetString(await ProcessRunner.RunForBytesAsync(tools.Ffprobe, withFrame, token, (timeouts ?? FfmpegTimeouts.Default).Probe));
        return Parse(json, path);
    }

    /// <summary>ffprobe 的結果是否為單張靜態圖片：容器是 image2 或 *_pipe（如 png_pipe），影像編碼是圖片格式。</summary>
    public static bool IsStillImage(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var format = root.TryGetProperty("format", out var f) ? f : default;
        var streams = root.TryGetProperty("streams", out var s) && s.ValueKind == JsonValueKind.Array ? s.EnumerateArray().ToList() : [];
        var video = streams.FirstOrDefault(x => Str(x, "codec_type") == "video" && !IsAttachedPicture(x));
        return IsStillImage(format, video);
    }

    private static bool IsStillImage(JsonElement format, JsonElement video)
    {
        if (video.ValueKind != JsonValueKind.Object) return false;
        var container = Str(format, "format_name") ?? "";
        return (container == "image2" || container.EndsWith("_pipe", StringComparison.Ordinal))
            && StillCodecs.Contains(Str(video, "codec_name") ?? "");
    }

    /// <summary>
    /// WebP 檔頭是否標示為動畫：RIFF....WEBP 之後是延伸格式 VP8X，旗標（第 20 個位元組）的 0x02 是動畫。
    /// 不是 WebP 或檔頭太短時為 false。
    /// </summary>
    public static bool IsAnimatedWebp(ReadOnlySpan<byte> header) =>
        header.Length >= 21
        && header[..4].SequenceEqual("RIFF"u8) && header[8..12].SequenceEqual("WEBP"u8) && header[12..16].SequenceEqual("VP8X"u8)
        && (header[20] & 0x02) != 0;

    /// <summary>讀檔案開頭的 count 個位元組；讀不到（檔案被移走、沒有權限）時回傳空的，交給 ffprobe 的結果判斷。</summary>
    private static byte[] ReadHeader(string path, int count)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var buffer = new byte[count];
            return buffer[..stream.ReadAtLeast(buffer, count, throwOnEndOfStream: false)];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return [];
        }
    }

    public static MediaInfo Parse(string json, string path) => Parse(json, path, allowStill: true);

    /// <param name="allowStill">false 時即使看起來是單張圖片也照影片處理（動態 WebP）。</param>
    private static MediaInfo Parse(string json, string path, bool allowStill)
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

        var isStill = allowStill && IsStillImage(format, video);
        var duration = isStill ? Timeline.MaxStillDuration : Dbl(format, "duration");
        if (duration <= 0 && hasVideo) duration = Dbl(video, "duration");
        if (duration <= 0 && hasAudio) duration = Dbl(audio, "duration");
        // 極短或讀不到長度的影片、音訊無法放上時間軸，匯入時就擋下。靜態圖片的長度在上面已經換成 MaxStillDuration，不會走到這裡；
        // 但 ffprobe 認不出是圖片的單格影像（例如沒有容器名稱）仍會在這裡因為太短被擋下。
        if (duration < Timeline.MinClipDuration)
            throw new InvalidDataException(duration <= 0
                ? "無法讀取檔案長度，可能是靜態圖片或檔案已損毀。"
                : string.Create(CultureInfo.InvariantCulture,
                    $"檔案太短（{duration:0.###} 秒），可能是靜態圖片；片段至少要 {Timeline.MinClipDuration} 秒。"));

        if (duration > MaxDuration.TotalSeconds)
            throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture,
                $"檔案長度（{duration / 3600:0.#} 小時）超過上限 {MaxDuration.TotalHours:0} 小時，檔案可能已損毀。"));

        var info = new MediaInfo
        {
            Path = path,
            Duration = duration,
            BitRate = Lng(format, "bit_rate"),
            HasAudio = hasAudio,
            AudioCodec = hasAudio ? Str(audio, "codec_name") ?? "" : "",
            AudioBitRate = hasAudio ? Lng(audio, "bit_rate") : 0,
            AudioSampleRate = hasAudio ? Int(audio, "sample_rate") : 0,
            AudioChannels = hasAudio ? Int(audio, "channels") : 0,
        };
        if (!hasVideo) return info;

        // 圖片的旋轉（EXIF 方向）在第一格的資訊裡（見 ProbeAsync）；影片在串流資訊裡。
        var rotation = isStill ? ReadFrameRotation(root) : ReadRotation(video);
        var sar = Rational.Parse(Str(video, "sample_aspect_ratio")?.Replace(':', '/'));
        if (sar.Num <= 0 || sar.Den <= 0) sar = new Rational(1, 1);
        // 先用 double 檢查再轉成 int：超出範圍的值轉型時會繞回小數字或負數，繞過上限。
        var rawWidth = Lng(video, "width") * sar.Value;
        double rawHeight = Lng(video, "height");
        if (Math.Abs(rotation) % 180 == 90) (rawWidth, rawHeight) = (rawHeight, rawWidth);
        if (double.IsNaN(rawWidth) || rawWidth < 0 || rawHeight < 0)
            throw new InvalidDataException("影像尺寸不正確，檔案可能已損毀。");
        if (rawWidth > MaxDimension || rawHeight > MaxDimension)
            throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture,
                $"影像尺寸（{rawWidth:0}×{rawHeight:0}）超過上限 {MaxDimension}×{MaxDimension}，檔案可能已損毀。"));
        var width = (int)Math.Round(rawWidth);
        var height = (int)rawHeight;

        // 圖片的 r_frame_rate 是 ffprobe 的預設值 25/1，不是真的影格率，不採用。
        var realRate = isStill ? new Rational(0, 1) : Rational.Parse(Str(video, "r_frame_rate"));
        var averageRate = isStill ? new Rational(0, 1) : Rational.Parse(Str(video, "avg_frame_rate"));
        if (Math.Max(realRate.Value, averageRate.Value) > MaxFrameRate)
            throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture,
                $"影格率（{Math.Max(realRate.Value, averageRate.Value):0.#} fps）超過上限 {MaxFrameRate:0} fps，檔案可能已損毀。"));
        // r_frame_rate 與平均影格率差距超過 1% 視為可變影格率。
        var isVariable = realRate.Value > 0 && averageRate.Value > 0
            && Math.Abs(realRate.Value - averageRate.Value) / realRate.Value > 0.01;
        var pixelFormat = Str(video, "pix_fmt") ?? "";

        return info with
        {
            HasVideo = true,
            IsStill = isStill,
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

    /// <summary>第一個影像影格上的旋轉角度（ffprobe -show_frames）；沒有影格資訊或沒有旋轉時為 0。</summary>
    private static int ReadFrameRotation(JsonElement root)
    {
        if (!root.TryGetProperty("frames", out var frames) || frames.ValueKind != JsonValueKind.Array) return 0;
        foreach (var frame in frames.EnumerateArray())
            if (Str(frame, "media_type") == "video") return ReadRotation(frame);
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

    /// <summary>整數欄位，限制在 int 範圍內（不會繞回）。</summary>
    private static int Int(JsonElement element, string name) => (int)Math.Clamp(Lng(element, name), 0, int.MaxValue);

    private static long Lng(JsonElement element, string name) =>
        long.TryParse(Str(element, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
}
