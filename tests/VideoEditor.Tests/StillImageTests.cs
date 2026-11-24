using VideoEditor.Core.Editing;
using VideoEditor.Core.Ffmpeg;
using VideoEditor.Core.Models;

namespace VideoEditor.Tests;

/// <summary>由 ffprobe 的 JSON 判斷靜態圖片（docs/IMAGE-CLIP-PLAN.md 階段 2）。</summary>
public class StillImageParseTests
{
    /// <summary>ffprobe 讀單張圖片的結果（FFmpeg 6.1.1 實測）：長度 0.04 秒或讀不到，影格率是預設的 25/1。</summary>
    private static string Still(string container, string codec, string pixelFormat = "rgb24", string duration = "N/A",
        int width = 640, int height = 480, string frames = "") => $$"""
        {"streams":[{"codec_type":"video","codec_name":"{{codec}}","pix_fmt":"{{pixelFormat}}","width":{{width}},"height":{{height}},
                     "r_frame_rate":"25/1","avg_frame_rate":"25/1","duration":"{{duration}}"}],
         "format":{"format_name":"{{container}}","duration":"{{duration}}"}{{frames}}}
        """;

    private static string FrameWithRotation(int rotation) => $$"""
        ,"frames":[{"media_type":"video","width":640,"height":480,
                    "side_data_list":[{"side_data_type":"3x3 displaymatrix","rotation":{{rotation}}}]}]
        """;

    [Theory]
    [InlineData("image2", "mjpeg", "yuvj444p", "0.040000")] // .jpg
    [InlineData("jpeg_pipe", "mjpeg", "yuvj420p", "N/A")]
    [InlineData("png_pipe", "png", "rgb24", "N/A")]
    [InlineData("png_pipe", "png", "rgba", "N/A")]
    [InlineData("bmp_pipe", "bmp", "bgr24", "N/A")]
    [InlineData("webp_pipe", "webp", "yuv420p", "N/A")]
    [InlineData("tiff_pipe", "tiff", "rgb24", "N/A")]
    public void SingleImagesAreStills(string container, string codec, string pixelFormat, string duration)
    {
        var json = Still(container, codec, pixelFormat, duration);
        Assert.True(MediaProbe.IsStillImage(json));

        var info = MediaProbe.Parse(json, "photo");

        Assert.True(info.IsStill);
        Assert.True(info.HasVideo);
        Assert.False(info.HasAudio);
        Assert.Equal(Timeline.MaxStillDuration, info.Duration);
        Assert.Equal(0, info.FrameRate.Value); // 25/1 是 ffprobe 的預設值，不是真的影格率
        Assert.False(info.IsVariableFrameRate);
        Assert.Equal((640, 480), (info.DisplayWidth, info.DisplayHeight));
    }

    [Theory]
    [InlineData(0, 640, 480)]
    [InlineData(-90, 480, 640)] // EXIF 方向 5、6
    [InlineData(90, 480, 640)] // EXIF 方向 7、8
    [InlineData(-180, 640, 480)] // EXIF 方向 2、3
    public void ExifRotationFromTheFirstFrameSetsTheDisplaySize(int rotation, int width, int height)
    {
        var info = MediaProbe.Parse(Still("image2", "mjpeg", "yuvj420p", "0.04", frames: FrameWithRotation(rotation)), "photo.jpg");

        Assert.Equal(rotation, info.Rotation);
        Assert.Equal((width, height), (info.DisplayWidth, info.DisplayHeight));
    }

    [Fact]
    public void StillWithoutFrameInformationIsUpright()
    {
        var info = MediaProbe.Parse(Still("image2", "mjpeg", "yuvj420p", "0.04"), "photo.jpg");
        Assert.Equal(0, info.Rotation);
        Assert.Equal((640, 480), (info.DisplayWidth, info.DisplayHeight));
    }

    [Theory]
    [InlineData("gif", "gif")] // 動態 GIF
    [InlineData("apng", "apng")] // 動態 PNG
    [InlineData("mov,mp4,m4a,3gp,3g2,mj2", "png")] // PNG 編碼的影片
    [InlineData("mov,mp4,m4a,3gp,3g2,mj2", "mjpeg")] // Motion JPEG 影片
    [InlineData("avi", "mjpeg")]
    [InlineData("image2", "h264")]
    public void AnimationsAndVideosAreNotStills(string container, string codec)
    {
        var json = Still(container, codec, duration: "2.0");
        Assert.False(MediaProbe.IsStillImage(json));
        var info = MediaProbe.Parse(json, "clip");
        Assert.False(info.IsStill);
        Assert.Equal(2.0, info.Duration);
        Assert.Equal(25, info.FrameRate.Value);
    }

    [Fact]
    public void VideoStreamRotationIsStillReadFromTheStream()
    {
        // 影片的旋轉照舊從串流資訊讀，影格資訊即使存在也不採用。
        var json = """
            {"streams":[{"codec_type":"video","codec_name":"h264","width":1920,"height":1080,"r_frame_rate":"30/1","avg_frame_rate":"30/1",
                         "side_data_list":[{"rotation":90}]}],
             "format":{"format_name":"mov,mp4,m4a,3gp,3g2,mj2","duration":"2.0"},
             "frames":[{"media_type":"video","side_data_list":[{"rotation":180}]}]}
            """;
        var info = MediaProbe.Parse(json, "x.mp4");
        Assert.False(info.IsStill);
        Assert.Equal(90, info.Rotation);
        Assert.Equal((1080, 1920), (info.DisplayWidth, info.DisplayHeight));
    }

    [Fact]
    public void OversizedStillIsStillRejected() =>
        Assert.Contains("超過上限", Assert.Throws<InvalidDataException>(
            () => MediaProbe.Parse(Still("png_pipe", "png", width: 20000, height: 100), "huge.png")).Message);

    [Theory]
    [InlineData(0x02, true)] // 動畫旗標
    [InlineData(0x12, true)] // 動畫 + 透明
    [InlineData(0x10, false)] // 只有透明
    [InlineData(0x00, false)]
    public void AnimatedWebpIsRecognisedFromTheHeader(byte flags, bool animated)
    {
        byte[] header = [.. "RIFF"u8, 0, 0, 0, 0, .. "WEBPVP8X"u8, 10, 0, 0, 0, flags];
        Assert.Equal(animated, MediaProbe.IsAnimatedWebp(header));
    }

    [Fact]
    public void SimpleWebpAndOtherFilesAreNotAnimated()
    {
        Assert.False(MediaProbe.IsAnimatedWebp([.. "RIFF"u8, 0, 0, 0, 0, .. "WEBPVP8 "u8, 10, 0, 0, 0, 0x02])); // 簡單格式沒有旗標
        Assert.False(MediaProbe.IsAnimatedWebp([.. "RIFF"u8, 0, 0, 0, 0, .. "WAVEfmt "u8, 0, 0, 0, 0, 0x02]));
        Assert.False(MediaProbe.IsAnimatedWebp([.. "RIFF"u8, 0, 0, 0]));
        Assert.False(MediaProbe.IsAnimatedWebp([]));
    }

    [Fact]
    public void AudioOnlyFileIsNotAStill() =>
        Assert.False(MediaProbe.IsStillImage("""
            {"streams":[{"codec_type":"audio","codec_name":"aac"}],"format":{"format_name":"mov,mp4,m4a,3gp,3g2,mj2","duration":"3"}}
            """));
}

/// <summary>以實際的 ffprobe 讀圖片檔。</summary>
[Collection("media")]
[Trait("Category", "Integration")]
public class StillImageProbeTests(TestMedia media)
{
    [Theory]
    [InlineData("still.jpg")]
    [InlineData("still.png")]
    [InlineData("still.bmp")]
    [InlineData("still.webp")]
    [InlineData("still.tif")]
    public async Task CommonImageFormatsAreStills(string name)
    {
        var path = await MakeImage(name, "testsrc2=s=320x240");

        var info = await media.Probe(path);

        Assert.True(info.IsStill);
        Assert.Equal((320, 240), (info.DisplayWidth, info.DisplayHeight));
        Assert.Equal(Timeline.MaxStillDuration, info.Duration);
        Assert.False(info.HasAudio);
    }

    [Fact]
    public async Task TransparentPngIsAStill()
    {
        var path = await MakeImage("alpha.png", "testsrc2=s=64x48,format=rgba");
        Assert.True((await media.Probe(path)).IsStill);
    }

    [Fact]
    public async Task TransparentPartsOfThePreviewFrameAreBlack()
    {
        // 左半完全透明（底下藏著紅色），右半不透明的紅色：預覽要和匯出一樣，透明處是黑色。
        var path = await MakeImage("hidden-red.png", HalfTransparentRed);

        var frame = await new FrameExtractor(media.Tools).ExtractAsync(path, 0, new Size2(64, 48), still: true);

        Assert.NotNull(frame);
        Assert.Equal<byte>([0, 0, 0, 255], Pixel(frame.Bgra, 64, 8, 24));    // BGRA：黑、不透明
        var red = Pixel(frame.Bgra, 64, 56, 24); // BGRA：紅、不透明（預乘後 255 可能捨入成 254）
        Assert.True(red is [0, 0, >= 250, 255], $"不透明處應為紅色，實際 {string.Join(',', red)}");
    }

    [Fact]
    public async Task TransparentPartsOfTheThumbnailAreBlack()
    {
        var path = await MakeImage("hidden-red-thumb.png", HalfTransparentRed);

        var jpeg = await new FrameExtractor(media.Tools).ThumbnailAsync(path, 0, 64, still: true);

        var thumb = media.Output("hidden-red-thumb.jpg");
        await File.WriteAllBytesAsync(thumb, jpeg);
        var rgb = await ProcessRunner.RunForBytesAsync(media.Tools.Ffmpeg,
            ["-v", "error", "-i", thumb, "-f", "rawvideo", "-pix_fmt", "rgb24", "pipe:1"]);
        var left = rgb.AsSpan((24 * 64 + 8) * 3, 3).ToArray();
        var right = rgb.AsSpan((24 * 64 + 56) * 3, 3).ToArray();
        Assert.True(left.All(v => v < 16), $"透明處應為黑色，實際 {string.Join(',', left)}");
        Assert.True(right[0] > 230 && right[1] < 30 && right[2] < 30, $"不透明處應為紅色，實際 {string.Join(',', right)}");
    }

    private const string HalfTransparentRed = "color=c=red:s=64x48,format=rgba,geq=r=255:g=0:b=0:a='if(lt(X\\,32)\\,0\\,255)'";

    private static byte[] Pixel(byte[] bgra, int width, int x, int y) => bgra.AsSpan((y * width + x) * 4, 4).ToArray();

    [Theory]
    [InlineData(1, 320, 240)]
    [InlineData(2, 320, 240)]
    [InlineData(3, 320, 240)]
    [InlineData(4, 320, 240)]
    [InlineData(5, 240, 320)]
    [InlineData(6, 240, 320)]
    [InlineData(7, 240, 320)]
    [InlineData(8, 240, 320)]
    public async Task ExifOrientationMatchesWhatFfmpegDecodes(int orientation, int width, int height)
    {
        var path = media.Output($"exif{orientation}.jpg");
        await File.WriteAllBytesAsync(path, WithExifOrientation(await File.ReadAllBytesAsync(await MakeImage($"plain{orientation}.jpg", "testsrc2=s=320x240")), orientation));

        var info = await media.Probe(path);

        Assert.True(info.IsStill);
        Assert.Equal((width, height), (info.DisplayWidth, info.DisplayHeight));
        // FFmpeg 解碼時會依 EXIF 轉正：解出的畫面尺寸要和記錄的顯示尺寸相同，比例與畫面位置才算得對。
        var frame = await new FrameExtractor(media.Tools).ExtractAsync(path, 0, new Size2(width, height), still: true);
        Assert.NotNull(frame);
        // 不縮放時解出的畫面就是轉正後的尺寸（像素數 = 寬 × 高，gray 每像素 1 位元組）。
        var native = await ProcessRunner.RunForBytesAsync(media.Tools.Ffmpeg,
            ["-v", "error", "-i", path, "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "gray", "pipe:1"]);
        Assert.Equal(width * height, native.Length);
        var decoded = await ProcessRunner.RunForBytesAsync(media.Tools.Ffprobe,
            ["-v", "error", "-select_streams", "v", "-show_entries", "stream=width,height", "-of", "csv=p=0", path]);
        Assert.Equal("320,240", System.Text.Encoding.UTF8.GetString(decoded).Trim()); // 檔案本身沒有轉
    }

    [Fact]
    public async Task AnimatedGifIsNotAStill()
    {
        var path = media.Output("animated.gif");
        await ProcessRunner.RunForBytesAsync(media.Tools.Ffmpeg,
            ["-hide_banner", "-y", "-v", "error", "-f", "lavfi", "-i", "testsrc2=s=64x48:r=10:d=1", path]);

        var info = await media.Probe(path);

        Assert.False(info.IsStill);
        Assert.True(info.Duration > 0.5);
    }

    [Fact]
    public async Task AnimatedWebpIsNotAStill()
    {
        // 動態 WebP 在 ffprobe 看來和單張一樣；把單張 WebP 的檔頭改成延伸格式並設動畫旗標，模擬動態 WebP 的檔頭。
        var still = await File.ReadAllBytesAsync(await MakeImage("anim-src.webp", "testsrc2=s=64x48"));
        var path = media.Output("animated.webp");
        await File.WriteAllBytesAsync(path, [.. still[..12], .. "VP8X"u8, 10, 0, 0, 0, 0x02, .. still[21..]]);

        // 改過的檔頭 ffprobe 可能讀不了（丟出錯誤）或讀成沒有長度（被擋下）；不論哪一種，都不能變成圖片。
        var error = await Record.ExceptionAsync(async () => Assert.False((await media.Probe(path)).IsStill));
        Assert.True(error is null or FfmpegException or InvalidDataException, error?.ToString());
    }

    [Fact]
    public async Task VideosAreNotStills()
    {
        Assert.False((await media.Probe(media.Landscape)).IsStill);
        Assert.False((await media.Probe(media.Music)).IsStill);
    }

    private async Task<string> MakeImage(string name, string source)
    {
        var path = media.Output(name);
        await ProcessRunner.RunForBytesAsync(media.Tools.Ffmpeg,
            ["-hide_banner", "-y", "-v", "error", "-f", "lavfi", "-i", source, "-frames:v", "1", path]);
        return path;
    }

    /// <summary>在 JPEG 開頭（SOI 之後）插入只含方向欄位的 EXIF（APP1）區段。</summary>
    private static byte[] WithExifOrientation(byte[] jpeg, int orientation)
    {
        byte[] tiff =
        [
            (byte)'M', (byte)'M', 0, 42, 0, 0, 0, 8, // 大端序、IFD 從第 8 個位元組開始
            0, 1, // 1 個欄位
            0x01, 0x12, 0, 3, 0, 0, 0, 1, 0, (byte)orientation, 0, 0, // Orientation，SHORT × 1
            0, 0, 0, 0, // 沒有下一個 IFD
        ];
        byte[] header = [(byte)'E', (byte)'x', (byte)'i', (byte)'f', 0, 0];
        var length = 2 + header.Length + tiff.Length;
        byte[] segment = [0xFF, 0xE1, (byte)(length >> 8), (byte)length, .. header, .. tiff];
        return [.. jpeg[..2], .. segment, .. jpeg[2..]];
    }
}
